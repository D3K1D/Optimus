using System.Globalization;
using System.Text.Json.Nodes;
using OptimizationTool.Transport.Logging;

namespace OptimizationTool.Engine.Agent;

// M9 §6.8 broad check-up (contracts/m9-ai-powers.md): the research run widened. After the per-finding labels phase of a
// research run (and between scans at most once a week while the host runs), up to five isolated calls, one per §6.10
// inventory area (hardware, drivers, windows, programs, background), through the same IWebResearch seam with the fixed
// prompt checkup-v2 and that area's names, publishers and versions only. Same fence, cancel hooks, observed-sources rule and URL hygiene as the labels phase. Kept
// results become AI cards (origin "ai", no action, web-tainted) on the current report through Followups.
//
// State lives in one settings row (key research.checkup): the last outcome and provider, lastAt, the per-area cache
// (hash of the entries sent + when) and the run in flight. One check-up at a time.
public sealed partial class AgentService
{
    public const string CheckupKey = "research.checkup";
    public static readonly string[] CheckupStatuses = ["off", "idle", "running", "done", "partial", "failed", "unavailable"];
    /// <summary>How often the host looks whether the weekly check-up is due (§6.8: "the host checks hourly").</summary>
    public static readonly TimeSpan CheckupGateInterval = TimeSpan.FromHours(1);

    private DateTimeOffset _nextCheckupGate = DateTimeOffset.MinValue;

    private sealed record PlannedCheckup(string RunId, string ReportId, string Kind, string? Model, string Account, long Revision,
        List<(string Area, List<CheckupEntry> Entries, string Hash)> Areas, CancellationTokenSource Cts);

    // ---------------------------------------------------------------- state

    private static JsonObject CheckupState(Db db)
    {
        var s = Settings.Get(db, CheckupKey) as JsonObject ?? [];
        s["status"] ??= "idle";
        s["areas"] ??= new JsonObject();
        if (!s.ContainsKey("lastAt")) s["lastAt"] = null;
        if (!s.ContainsKey("by")) s["by"] = null;
        if (!s.ContainsKey("run")) s["run"] = null;
        return s;
    }

    private static JsonObject? RunningCheckup(JsonObject state) => state["run"] is JsonObject run && run.Text("status") == "running" ? run : null;

    /// <summary>research.get .checkup = {status, lastAt, cards} (§6.8 read side).</summary>
    private JsonObject CheckupView(Db db, string reportId)
    {
        var state = CheckupState(db);
        var kind = SelectedKind(db);
        string status;
        if (!ReadPermissions(db).Web || kind is null) status = "off";
        else if (RunningCheckup(state) is not null) status = "running";
        else if (WebFor(db, kind, ResearchModel(kind)) == "no") status = "unavailable";
        else status = state["by"] is JsonObject by && by.Text("kind") == kind && by["model"]?.GetValue<string>() == ResearchModel(kind) ? state.Text("status") : "idle";
        var missed = status == "partial" ? state["missed"]?.DeepClone() as JsonArray ?? [] : [];
        return new JsonObject { ["status"] = status, ["lastAt"] = state["lastAt"]?.DeepClone(), ["cards"] = Followups.CheckupCardIds(db, reportId), ["missed"] = missed };
    }

    private static void EmitCheckupState(Db db, JsonObject run) =>
        db.AppendEvent("user", "research.state", new JsonObject
        {
            ["reportId"] = run.Text("reportId"), ["runId"] = run.Text("runId"), ["status"] = run.Text("status"),
            ["done"] = run["done"]?.DeepClone() ?? 0, ["total"] = run["total"]?.DeepClone() ?? 0, ["trial"] = false, ["phase"] = "checkup",
        });

    /// <summary>Marks the running check-up cancelled (of one provider kind or report, or any). True when one was. In the caller's transaction.</summary>
    private bool CancelCheckup(Db db, string? kind, string? reportId = null)
    {
        var state = CheckupState(db);
        if (RunningCheckup(state) is not { } run) return false;
        if (kind is not null && run.Text("kind") != kind) return false;
        if (reportId is not null && run.Text("reportId") != reportId) return false;
        run["status"] = "cancelled";
        run["finishedAt"] = Time.Format(Now());
        state["status"] = "idle";
        Settings.Set(db, CheckupKey, state);
        EmitCheckupState(db, run);
        CancellationTokenSource? cts;
        lock (_gate) _researchRuns.Remove(run.Text("runId"), out cts);
        if (cts is not null) _ = Task.Run(() => { try { cts.Cancel(); } catch (ObjectDisposedException) { } }); // off the store lock
        return true;
    }

    /// <summary>On startup: a check-up the previous host left running is cancelled, never resumed (areas already checked stay cached).</summary>
    private static void RecoverCheckup(Db db)
    {
        var state = CheckupState(db);
        if (RunningCheckup(state) is not { } run) return;
        run["status"] = "cancelled";
        state["status"] = "idle";
        Settings.Set(db, CheckupKey, state);
    }

    // ---------------------------------------------------------------- starting

    /// <summary>The weekly tick: starts the check-up when allowed.</summary>
    private void StartCheckup(string reportId)
    {
        try
        {
            PlannedCheckup? plan = null;
            _store.Write(db => { plan = BeginCheckup(db, reportId); });
            if (plan is not null) LaunchCheckup(plan);
        }
        catch (Exception ex) when (ex is RpcError or ObjectDisposedException or InvalidOperationException) { CheckupLog($"not started report={reportId} {ex.GetType().Name}", ErrorSites.CheckupNotStarted, LogLevel.Warn); }
    }

    /// <summary>
    /// The labels phase's handoff (m9 §6.8): the check-up run is created in the caller's transaction (the one that ends
    /// or skips the labels phase), so no cancel can fall between them. A refusal to start is logged, never thrown into it.
    /// </summary>
    private PlannedCheckup? HandOffCheckup(Db db, string reportId)
    {
        try { return BeginCheckup(db, reportId); }
        catch (Exception ex) when (ex is RpcError or InvalidOperationException) { CheckupLog($"not started report={reportId} {ex.GetType().Name}", ErrorSites.CheckupNotStarted, LogLevel.Warn); return null; }
    }

    private void LaunchCheckup(PlannedCheckup plan) => Track(Task.Run(() => RunCheckupAsync(plan)));

    /// <summary>
    /// Between scans (§6.8): at most once every 7 days while the host runs, checked hourly from the provider sweep with
    /// the injected clock. Nothing happens with web off, without a provider that can search, or without a report.
    /// </summary>
    private void CheckupTick()
    {
        var now = Now();
        if (_nextCheckupGate == DateTimeOffset.MinValue) _nextCheckupGate = now + CheckupGateInterval; // first look: an hour after start
        if (now < _nextCheckupGate) return;
        _nextCheckupGate = now + CheckupGateInterval;
        string? reportId = null;
        _store.Read(db =>
        {
            var state = CheckupState(db);
            if (RunningCheckup(state) is not null) return 0;
            if (state["lastAt"]?.GetValue<string>() is { } last && now - ParseTime(last) < CheckupPolicy.Week) return 0;
            reportId = db.Scalar("SELECT report_id FROM reports ORDER BY rowid DESC LIMIT 1") as string;
            return 0;
        });
        if (reportId is not null) StartCheckup(reportId);
    }

    /// <summary>
    /// Creates the run (in the caller's transaction) and returns what to launch, or null when nothing runs: web off, no
    /// provider or web "no", not the current report, the labels phase still active, a check-up already running, or a PC
    /// summary with nothing in it. Areas unchanged and checked within 7 days are skipped; all skipped → done at once.
    /// </summary>
    private PlannedCheckup? BeginCheckup(Db db, string reportId)
    {
        var perms = ReadPermissions(db);
        if (!perms.Web || SelectedKind(db) is not { } kind) { CheckupLog($"not started report={reportId} reason={(perms.Web ? "no_provider" : "web_off")}"); return null; }
        if (db.Scalar("SELECT report_id FROM reports ORDER BY rowid DESC LIMIT 1") as string != reportId) { CheckupLog($"not started report={reportId} reason=report_not_current"); return null; }
        if (LatestRun(db, reportId) is { } labels && ActiveResearch.Contains(labels.Status)) { CheckupLog($"not started report={reportId} reason=labels_active"); return null; } // the labels go first
        var state = CheckupState(db);
        if (RunningCheckup(state) is not null) { CheckupLog($"not started report={reportId} reason=already_running"); return null; }
        var model = ResearchModel(kind);
        if (WebFor(db, kind, model) == "no") { CheckupLog($"not started report={reportId} reason={WebNoReason(kind)} kind={kind}", ErrorSites.CheckupNotStarted, LogLevel.Info); return null; }

        var summary = CheckupPolicy.Summary(ReportService.Load(db, reportId), LatestInventory(db), LocalIdentities());
        if (summary.Empty) { CheckupLog($"not started report={reportId} reason=empty_summary"); return null; }
        var now = Now();
        var cache = state["areas"] as JsonObject ?? [];
        var areas = new List<(string, List<CheckupEntry>, string)>();
        foreach (var area in CheckupPolicy.Areas)
        {
            var entries = summary.Entries[area];
            if (entries.Count == 0) continue;
            var hash = CheckupPolicy.AreaHash(entries);
            if (cache[area] is JsonObject hit && hit.Text("hash") == hash && hit["at"]?.GetValue<string>() is { } at && now - ParseTime(at) < CheckupPolicy.Week) continue;
            areas.Add((area, entries, hash));
        }

        var runId = "research-" + Ids.RandomHex(8);
        var run = new JsonObject
        {
            ["runId"] = runId, ["reportId"] = reportId, ["status"] = "running", ["done"] = 0, ["total"] = areas.Count,
            ["kind"] = kind, ["account"] = AccountOf(kind), ["model"] = model, ["revision"] = perms.Revision,
            ["usage"] = EmptyUsage(), ["dropped"] = 0, ["kept"] = 0, ["startedAt"] = Time.Format(now), ["finishedAt"] = null,
            ["areas"] = Json.Array(areas.Select(a => a.Item1).ToList()), ["checked"] = new JsonArray(),
            ["unsent"] = new JsonObject(summary.Unsent.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value))),
        };
        state["run"] = run;
        Settings.Set(db, CheckupKey, state);
        EmitCheckupState(db, run);
        CheckupLog($"{runId}: begin report={reportId} kind={kind} model={model} areas={areas.Count} unsent={summary.Unsent.Values.Sum()}{(areas.Count == 0 ? " reason=all_cached" : "")}");
        if (areas.Count == 0)
        {
            EndCheckup(db, runId, "done"); // all cached: costs nothing
            return null;
        }
        var cts = new CancellationTokenSource();
        lock (_gate) _researchRuns[runId] = cts;
        return new PlannedCheckup(runId, reportId, kind, model, AccountOf(kind), perms.Revision, areas, cts);
    }

    /// <summary>The latest finished onboarding inventory's items (subject → value), as get_pc_specs reads them.</summary>
    private static Dictionary<string, JsonObject> LatestInventory(Db db)
    {
        var map = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var inventoryId = db.Scalar("SELECT inventory_id FROM inventories WHERE status IN ('complete','partial') ORDER BY started_at DESC, rowid DESC LIMIT 1") as string;
        if (inventoryId is null) return map;
        foreach (var (subject, body) in db.Query("SELECT subject, body FROM inventory_items WHERE inventory_id = $i ORDER BY ord", r => (r.GetString(0), r.GetString(1)), ("$i", inventoryId)))
        {
            try { if (Json.Parse(body)?["value"] is JsonObject v) map.TryAdd(subject, v); }
            catch (System.Text.Json.JsonException) { }
        }
        return map;
    }

    // ---------------------------------------------------------------- the run

    private async Task RunCheckupAsync(PlannedCheckup plan)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(plan.Cts.Token);
        budget.CancelAfter(_options.CheckupBudget);
        var token = budget.Token;
        int failed = 0, reached = 0, calls = 0;
        try
        {
            if (!_store.Write(db => CheckupFence(db, plan))) return;
            var researcher = Researcher(plan.Kind);
            if (researcher is null) { CheckupLog($"{plan.RunId}: unavailable reason=no_research_seam kind={plan.Kind}", ErrorSites.CheckupNoSeam, LogLevel.Warn); FinishCheckup(plan, "unavailable"); return; }
            ProviderCredential? credential = null;
            if (InAppKinds.Contains(plan.Kind))
            {
                credential = await CredentialAsync(plan.Kind, token).ConfigureAwait(false);
                if (credential is null) { CheckupLog($"{plan.RunId}: failed reason=not_signed_in kind={plan.Kind}", ErrorSites.CheckupNotSignedIn, LogLevel.Warn); FinishCheckup(plan, "failed"); return; }
            }
            var kept = 0;
            foreach (var (area, entries, hash) in plan.Areas)
            {
                if (calls >= CheckupPolicy.MaxCalls || token.IsCancellationRequested)
                {
                    CheckupLog($"{plan.RunId}: stopped before {area} reason={(calls >= CheckupPolicy.MaxCalls ? "max_calls" : "run_budget")} calls={calls}", ErrorSites.CheckupBudget, LogLevel.Info);
                    break;
                }
                calls++;
                var (outcome, count) = await CheckupCallAsync(plan, researcher, credential, area, entries, hash, kept, token).ConfigureAwait(false);
                if (outcome == CallOutcome.Fenced) return;
                if (outcome == CallOutcome.Unsupported) return;
                reached++;
                kept += count;
                if (outcome == CallOutcome.Failed) failed++;
            }
            var status = reached < plan.Areas.Count ? "partial" : failed == 0 ? "done" : failed == plan.Areas.Count ? "failed" : "partial";
            CheckupLog($"{plan.RunId}: end status={status} reached={reached} failed={failed} areas={plan.Areas.Count} calls={calls}");
            FinishCheckup(plan, status);
        }
        catch (OperationCanceledException)
        {
            if (!plan.Cts.IsCancellationRequested) CheckupLog($"{plan.RunId}: stopped reason=run_budget reached={reached}/{plan.Areas.Count}", ErrorSites.CheckupBudget, LogLevel.Warn);
            else CheckupLog($"{plan.RunId}: stopped reason=cancelled");
            FinishCheckup(plan, plan.Cts.IsCancellationRequested ? "cancelled" : "partial");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Current.Error($"checkup: {plan.RunId}: run failed {ex.GetType().Name}", ErrorSites.CheckupRunFailed, ex);
            FinishCheckup(plan, "failed");
        }
        finally
        {
            lock (_gate) _researchRuns.Remove(plan.RunId);
            plan.Cts.Dispose();
        }
    }

    /// <summary>One isolated call for one area, then its results through the fence. Returns the outcome and the cards stored.</summary>
    private async Task<(CallOutcome, int)> CheckupCallAsync(PlannedCheckup plan, IWebResearch researcher, ProviderCredential? credential, string area,
        List<CheckupEntry> entries, string hash, int keptSoFar, CancellationToken token)
    {
        ResearchAnswer answer;
        using (var call = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            call.CancelAfter(CheckupPolicy.CallTimeout);
            try
            {
                answer = await researcher.ResearchAsync(credential ?? new ProviderCredential(plan.Kind, ""), CheckupPolicy.Call(plan.Model ?? "", area, entries), call.Token)
                    .WaitAsync(call.Token).ConfigureAwait(false);
            }
            catch (WebResearchUnsupportedException ex)
            {
                CheckupLog($"{plan.RunId} {area}: unavailable reason=unsupported_{ex.Reason} kind={plan.Kind} model={plan.Model}", ErrorSites.CheckupUnsupported, LogLevel.Warn);
                var stored = _store.Write(db =>
                {
                    if (!CheckupFence(db, plan)) return false;
                    if (ex.Reason != "not_available_yet") SetWeb(db, plan.Kind, plan.Model, "no");
                    EndCheckup(db, plan.RunId, "unavailable");
                    return true;
                });
                return (stored ? CallOutcome.Unsupported : CallOutcome.Fenced, 0);
            }
            catch (OperationCanceledException) when (plan.Cts.IsCancellationRequested) { return (CallOutcome.Fenced, 0); }
            catch (Exception ex) when (ex is ProviderException or OperationCanceledException or TimeoutException or System.Net.Http.HttpRequestException)
            {
                // Sign-in, network, rate-limit, timeout and the search-count kill change nothing about availability; the area stays unchecked.
                CheckupLog($"{plan.RunId} {area}: call failed reason={(ex is ProviderException pe ? "provider_error code=" + pe.Code : ex is OperationCanceledException or TimeoutException ? "timeout" : "network")}", ErrorSites.CheckupCallFailed, LogLevel.Warn);
                if (token.IsCancellationRequested) { CheckupLog($"{plan.RunId} {area}: run budget ran out", ErrorSites.CheckupBudget, LogLevel.Warn); throw new OperationCanceledException(token); } // the run budget ran out → partial
                return (_store.Write(db => CountCheckupCall(db, plan, failedCall: true)) ? CallOutcome.Failed : CallOutcome.Fenced, 0);
            }
        }
        return _store.Write(db => StoreCheckupAnswer(db, plan, area, entries, hash, answer, keptSoFar));
    }

    /// <summary>The fence of §6.3, for the check-up: this run is the running one, permissions unchanged with web on, same provider, account and model, same current report.</summary>
    private bool CheckupFence(Db db, PlannedCheckup plan)
    {
        var state = CheckupState(db);
        if (RunningCheckup(state) is not { } run || run.Text("runId") != plan.RunId)
        {
            CheckupLog($"{plan.RunId}: fenced reason=not_running", ErrorSites.CheckupFenced, LogLevel.Info);
            return false;
        }
        var perms = ReadPermissions(db);
        var reason = !perms.Web ? "web_off"
            : perms.Revision != plan.Revision ? "permissions_changed"
            : SelectedKind(db) != plan.Kind ? "provider_changed"
            : ResearchModel(plan.Kind) != plan.Model ? "model_changed"
            : AccountOf(plan.Kind) != plan.Account ? "account_changed"
            : db.Scalar("SELECT report_id FROM reports ORDER BY rowid DESC LIMIT 1") as string != plan.ReportId ? "report_not_current"
            : null;
        if (reason is null) return true;
        CheckupLog($"{plan.RunId}: fenced reason={reason}; check-up cancelled", ErrorSites.CheckupFenced, LogLevel.Info);
        CancelCheckup(db, null);
        return false;
    }

    /// <summary>A check-up guard event with its reason code (counts, areas and ids only; never a name or version).</summary>
    private static void CheckupLog(string message, string? site = null, LogLevel level = LogLevel.Debug,
        [System.Runtime.CompilerServices.CallerFilePath] string file = "", [System.Runtime.CompilerServices.CallerLineNumber] int line = 0,
        [System.Runtime.CompilerServices.CallerMemberName] string member = "") =>
        AppLog.Current.Write(level, "checkup: " + message, site, null, file, line, member);

    private bool CountCheckupCall(Db db, PlannedCheckup plan, bool failedCall, ResearchAnswer? answer = null, int dropped = 0, int kept = 0)
    {
        if (!CheckupFence(db, plan)) return false;
        var state = CheckupState(db);
        var run = (JsonObject)state["run"]!;
        run["done"] = (run["done"]?.GetValue<long>() ?? 0) + 1;
        if (answer is not null)
        {
            var usage = run["usage"] as JsonObject ?? EmptyUsage();
            usage["searches"] = (usage["searches"]?.GetValue<long>() ?? 0) + Math.Max(0, answer.Searches);
            usage["inputTokens"] = (usage["inputTokens"]?.GetValue<long>() ?? 0) + Math.Max(0, answer.Usage.InputTokens);
            usage["outputTokens"] = (usage["outputTokens"]?.GetValue<long>() ?? 0) + Math.Max(0, answer.Usage.OutputTokens);
            run["usage"] = usage;
        }
        run["dropped"] = (run["dropped"]?.GetValue<long>() ?? 0) + dropped;
        run["kept"] = (run["kept"]?.GetValue<long>() ?? 0) + kept;
        if (failedCall) run["failed"] = (run["failed"]?.GetValue<long>() ?? 0) + 1;
        Settings.Set(db, CheckupKey, state);
        EmitCheckupState(db, run);
        return true;
    }

    /// <summary>
    /// A fresh answer, in one fenced transaction: usage, availability, the host rules (§6.8), the kept results as cards
    /// (≤ 3 per area, ≤ 8 per run), the sources by ID and the area cache. An answer over the search kill or unusable
    /// counts as a failed call (the area is not cached).
    /// </summary>
    private (CallOutcome, int) StoreCheckupAnswer(Db db, PlannedCheckup plan, string area, List<CheckupEntry> entries, string hash, ResearchAnswer answer, int keptSoFar)
    {
        if (!CheckupFence(db, plan)) return (CallOutcome.Fenced, 0);
        if (answer.Observed.Count > 0) SetWeb(db, plan.Kind, plan.Model, "yes"); // observed sources → the provider can search (§6.4)
        var max = Math.Max(0, Math.Min(CheckupPolicy.MaxPerArea, CheckupPolicy.MaxKept - keptSoFar));
        string? unusable = null;
        var ruled = answer.Searches > CheckupPolicy.KillAboveSearches ? null : CheckupPolicy.Apply(answer.Text, entries, answer.Observed, max, out unusable);
        if (ruled is not { } r)
        {
            if (answer.Searches > CheckupPolicy.KillAboveSearches)
                CheckupLog($"{plan.RunId} {area}: answer dropped reason=kill_above_searches searches={answer.Searches}", ErrorSites.CheckupKillAboveSearches, LogLevel.Warn);
            else
                CheckupLog($"{plan.RunId} {area}: answer dropped reason={unusable} chars={answer.Text?.Length ?? 0}", ErrorSites.CheckupAnswerUnusable, LogLevel.Warn);
            CountCheckupCall(db, plan, failedCall: true, answer);
            return (CallOutcome.Failed, 0);
        }
        var dr = r.Dropped;
        if (dr.NotSent + dr.NoSource + dr.BadShape + dr.OverCap > 0)
            CheckupLog($"{plan.RunId} {area}: results kept={r.Kept.Count} dropped not_sent={dr.NotSent} url_not_observed={dr.NoSource} bad_shape={dr.BadShape} over_cap={dr.OverCap}",
                ErrorSites.CheckupResultsDropped, LogLevel.Info);
        else
            CheckupLog($"{plan.RunId} {area}: results kept={r.Kept.Count} observed={answer.Observed.Count} searches={answer.Searches}");
        var by = ProviderLabel(plan.Kind);
        var at = db.Now();
        var stored = 0;
        foreach (var result in r.Kept)
        {
            var key = area + "|" + result.Entry.Name.ToLowerInvariant() + "|" + result.Kind;
            var sourceKey = "ck-" + Ids.Hash16(key);
            var sources = new JsonArray();
            foreach (var s in result.Sources)
            {
                var id = "src-" + Ids.RandomHex(8);
                var title = ResearchPolicy.SourceTitle(s);
                var domain = UrlHygiene.Domain(s.Url);
                db.Exec("INSERT INTO research_sources (source_id, url, title, domain, run_id, product_key, created_at) VALUES ($i, $u, $t, $d, $r, $k, $c)",
                    ("$i", id), ("$u", s.Url), ("$t", title), ("$d", domain), ("$r", plan.RunId), ("$k", sourceKey), ("$c", at));
                sources.Add(new JsonObject { ["sourceId"] = id, ["title"] = title, ["domain"] = domain });
            }
            if (Followups.UpsertCheckupCard(db, plan.ReportId, key, result.Kind, result.Title, result.What, CheckupPolicy.Category(result.Kind), sources, by,
                CheckupPolicy.MaxCardsPerReport, at) is not null) stored++;
        }
        var d = r.Dropped;
        CountCheckupCall(db, plan, failedCall: false, answer, d.NotSent + d.NoSource + d.BadShape + d.OverCap + (r.Kept.Count - stored), stored);
        var state = CheckupState(db);
        ((JsonObject)state["areas"]!)[area] = new JsonObject { ["hash"] = hash, ["at"] = Time.Format(Now()) };
        ((JsonArray)(((JsonObject)state["run"]!)["checked"] ??= new JsonArray())).Add(area);
        Settings.Set(db, CheckupKey, state);
        return (CallOutcome.Labelled, r.Kept.Count);
    }

    private void FinishCheckup(PlannedCheckup plan, string status)
    {
        try { _store.Write(db => EndCheckup(db, plan.RunId, status)); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Ends the running check-up with a status; done and partial record lastAt. Nothing when it is no longer running.</summary>
    private void EndCheckup(Db db, string runId, string status)
    {
        var state = CheckupState(db);
        if (RunningCheckup(state) is not { } run || run.Text("runId") != runId) return;
        run["status"] = status;
        run["finishedAt"] = Time.Format(Now());
        state["status"] = status == "cancelled" ? "idle" : status;
        // §6.8 end states: the planned areas not checked (failed, not reached) when the check-up ends partial.
        var checkedAreas = Json.Strings(run["checked"]);
        state["missed"] = status == "partial" ? Json.Array(Json.Strings(run["areas"]).Where(a => !checkedAreas.Contains(a)).ToList()) : new JsonArray();
        state["by"] = new JsonObject { ["kind"] = run.Text("kind"), ["model"] = run["model"]?.DeepClone() };
        if (status is "done" or "partial") state["lastAt"] = Time.Format(Now());
        Settings.Set(db, CheckupKey, state);
        EmitCheckupState(db, run);
    }

    /// <summary>The last check-up's usage and counts (tests and logs; not part of research.get).</summary>
    internal JsonObject? LastCheckupRun() => _store.Read(db => CheckupState(db)["run"]?.DeepClone() as JsonObject);
}

using System.Text.Json.Nodes;
using OptimizationTool.Transport.Logging;

namespace OptimizationTool.Engine.Agent;

// M9 §6 researched reports (contracts/m9-ai-powers.md §6.1–§6.5): the research service. One run per report at a time; it
// starts by itself when a scan finishes with do.web on and the selected provider can search (web "yes" or "unknown").
// Each subject is one isolated call (IWebResearch: the fixed prompt research-v1 + one subject, no PC tools, no chat
// history). Results are stored only through Fence: in one transaction that finds the run active and not cancelled, the
// permissions revision unchanged with web on, the provider, account and model unchanged and the report still current.
// The host rules of §6.2 (ResearchPolicy.Apply) run on every result, cached or fresh; the finding itself never changes.
public sealed partial class AgentService
{
    public const string ResearchWebKey = "research.web";
    public static readonly string[] ResearchStatuses = ["off", "queued", "running", "done", "partial", "cancelled", "failed", "unavailable"];
    private static readonly string[] ActiveResearch = ["queued", "running"];

    /// <summary>runId → the cancel switch of a run in flight in this process. Guarded by _gate.</summary>
    private readonly Dictionary<string, CancellationTokenSource> _researchRuns = [];
    /// <summary>kind → a counter bumped whenever its account or key changes (the fence's "account unchanged"). Guarded by _gate.</summary>
    private readonly Dictionary<string, long> _accountGeneration = [];

    /// <summary>
    /// The research seam per kind. In-app kinds: their adapter. The headless kind (Claude Code, m9 §6.3): the
    /// configured seam, else the built-in <see cref="HeadlessResearcher"/> (needs the CLI runner and the data folder).
    /// </summary>
    private IWebResearch? Researcher(string kind) =>
        InAppKinds.Contains(kind) ? Adapter(kind) as IWebResearch : McpKinds.Contains(kind) ? _options.HeadlessResearch ?? BuiltInHeadlessResearch() : null;

    private HeadlessResearcher? _headlessResearch;

    private HeadlessResearcher? BuiltInHeadlessResearch()
    {
        if (_options.CliRunner is not { } runner || DataDir is not { } dataDir) return null;
        lock (_gate)
            return _headlessResearch ??= new HeadlessResearcher(runner, dataDir, ResearchClientOf, Environment.GetEnvironmentVariable, _log);
    }

    /// <summary>
    /// m9 §6.3 capability for the built-in headless research: the CLI is installed and usable, a profile covers its
    /// version, and the probe found what research needs (--safe-mode and --setting-sources).
    /// Otherwise research is unavailable for that CLI (web "no"); chat is unaffected.
    /// </summary>
    private bool HeadlessResearchReady(string kind) =>
        _options.HeadlessResearch is not null || HeadlessResearcher.Unavailable(kind, ResearchClientOf(kind)) is null;

    private string AccountOf(string kind)
    {
        lock (_gate) return _accountGeneration.GetValueOrDefault(kind).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A key, sign-in or check changed this kind's account: its fence no longer holds and any run on it is cancelled.</summary>
    private void AccountChanged(string kind)
    {
        lock (_gate) _accountGeneration[kind] = _accountGeneration.GetValueOrDefault(kind) + 1;
        try { _store.Write(db => CancelActiveResearch(db, kind)); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// The provider research and the check-up run on: the chosen kind (<see cref="ChosenKinds(Db)"/>); an older "api"
    /// profile that leaves it open uses the first API kind with a key. Null: none chosen, or an API kind without its key.
    /// </summary>
    internal string? SelectedKind(Db db) => ChosenKinds(db) switch
    {
        [var one] => ApiKeyKinds.Contains(one) && !HasCredential(one) ? null : one,
        var open => open.FirstOrDefault(HasCredential),
    };

    private string? ResearchModel(string kind) =>
        InAppKinds.Contains(kind) ? Adapter(kind) is { } a ? ModelFor(kind, a) : null : McpKinds.Contains(kind) ? "cli" : null;

    private string ProviderLabel(string kind) => kind switch { "chatgpt_plan" => "ChatGPT", _ => Adapter(kind)?.DisplayName ?? RecipientLabel(kind) };

    /// <summary>§6.4: "yes" | "no" | "unknown" for a provider kind and model, as research calls found it.</summary>
    internal string WebFor(Db db, string kind, string? model)
    {
        if (kind == "local" || Researcher(kind) is null) return "no";
        if (McpKinds.Contains(kind) && !HeadlessResearchReady(kind)) return "no";
        var stored = Settings.Get(db, ResearchWebKey)?[WebKey(kind, model)] is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null;
        return stored is "yes" or "no" ? stored : "unknown";
    }

    /// <summary>Why <see cref="WebFor"/> says "no", as a reason code for the developer log.</summary>
    private string WebNoReason(string kind) =>
        kind == "local" ? "local_model"
        : Researcher(kind) is null ? "provider_unsupported"
        : McpKinds.Contains(kind) && !HeadlessResearchReady(kind) ? "cli_unavailable_" + (HeadlessResearcher.Unavailable(kind, ResearchClientOf(kind)) ?? "unknown")
        : "web_off_for_model";

    /// <summary>A research-run guard event with its reason code (counts and ids only).</summary>
    private static void ResearchLog(string message, string? site = null, LogLevel level = LogLevel.Debug,
        [System.Runtime.CompilerServices.CallerFilePath] string file = "", [System.Runtime.CompilerServices.CallerLineNumber] int line = 0,
        [System.Runtime.CompilerServices.CallerMemberName] string member = "") =>
        AppLog.Current.Write(level, "research: " + message, site, null, file, line, member);

    /// <summary>§6.4: kept per provider kind and model, and for a CLI kind per CLI version too (a new version is tried again).</summary>
    private string WebKey(string kind, string? model)
    {
        var key = kind + "|" + (model ?? "");
        if (!McpKinds.Contains(kind)) return key;
        lock (_gate) return key + "|" + (_clients.TryGetValue(kind, out var c) ? c.Version ?? "" : "");
    }

    private void SetWeb(Db db, string kind, string? model, string value)
    {
        var map = Settings.Get(db, ResearchWebKey) as JsonObject ?? [];
        if (map[WebKey(kind, model)]?.GetValue<string>() == value) return;
        map[WebKey(kind, model)] = value;
        Settings.Set(db, ResearchWebKey, map);
    }

    /// <summary>available.web for ai.getPermissions: the selected provider's research availability.</summary>
    private string AvailableWeb(Db db) => SelectedKind(db) is { } kind ? WebFor(db, kind, ResearchModel(kind)) : "unknown";

    // ---------------------------------------------------------------- read side

    private sealed record RunRow(string RunId, string ReportId, string Status, long Done, long Total, bool Trial, string Kind, string Account, string? Model,
        long Revision, JsonObject Usage);

    private static RunRow? LatestRun(Db db, string reportId) =>
        db.Query("SELECT run_id, report_id, status, done, total, trial, provider_kind, account, model, permissions_revision, usage FROM research_runs WHERE report_id = $r ORDER BY rowid DESC LIMIT 1",
            ReadRun, ("$r", reportId)).FirstOrDefault();

    private static RunRow? RunById(Db db, string runId) =>
        db.Query("SELECT run_id, report_id, status, done, total, trial, provider_kind, account, model, permissions_revision, usage FROM research_runs WHERE run_id = $r",
            ReadRun, ("$r", runId)).FirstOrDefault();

    private static RunRow ReadRun(Microsoft.Data.Sqlite.SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4),
        r.GetInt64(5) != 0, r.GetString(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.GetInt64(9),
        Json.Parse(r.GetString(10)) as JsonObject ?? EmptyUsage());

    private static JsonObject EmptyUsage() => new() { ["searches"] = 0, ["inputTokens"] = 0, ["outputTokens"] = 0 };

    /// <summary>
    /// A label applies only when it was made under the current research policy: its product key (which hashes the policy
    /// version) has a product row of this version. A label from an older policy is neither shown nor reused; the next run
    /// or lookup re-evaluates it. Expects the label table aliased <c>l</c> and the version bound to <c>$p</c>.
    /// </summary>
    private const string CurrentPolicyLabel =
        "EXISTS (SELECT 1 FROM research_products p WHERE p.product_key = l.product_key AND p.policy_version = $p)";

    /// <summary>The labelled items of a report, in report order (§6.5).</summary>
    private static JsonArray Items(Db db, JsonObject report)
    {
        var labels = db.Query("SELECT finding_id, item FROM research_labels l WHERE report_id = $r AND " + CurrentPolicyLabel, r => (Id: r.GetString(0), Item: r.GetString(1)),
            ("$r", report.Text("reportId")), ("$p", ResearchPolicy.PolicyVersion)).ToDictionary(x => x.Id, x => x.Item, StringComparer.Ordinal);
        var items = new JsonArray();
        foreach (var f in report["findings"] as JsonArray ?? [])
            if (labels.TryGetValue(f.Text("id"), out var item) && Json.Parse(item) is JsonObject o) items.Add(o);
        return items;
    }

    private JsonObject ResearchView(Db db, string reportId)
    {
        var report = ReportService.Load(db, reportId);
        var web = ReadPermissions(db).Web;
        var run = LatestRun(db, reportId);
        var active = run is not null && ActiveResearch.Contains(run.Status);
        var status = !web ? "off" : run is not null ? run.Status : AvailableWeb(db) == "no" ? "unavailable" : "off";
        return new JsonObject
        {
            ["runId"] = run?.RunId, ["status"] = status, ["done"] = run?.Done ?? 0, ["total"] = run?.Total ?? 0,
            ["trial"] = active && run!.Trial, ["items"] = Items(db, report), ["usage"] = run?.Usage.DeepClone() ?? EmptyUsage(),
            ["checkup"] = CheckupView(db, reportId),
        };
    }

    private static void EmitState(Db db, string runId)
    {
        if (RunById(db, runId) is not { } run) return;
        db.AppendEvent("user", "research.state", new JsonObject
        {
            ["reportId"] = run.ReportId, ["runId"] = run.RunId, ["status"] = run.Status, ["done"] = run.Done, ["total"] = run.Total,
            ["trial"] = ActiveResearch.Contains(run.Status) && run.Trial, ["phase"] = "labels",
        });
    }

    /// <summary>report.get / finding.get overlay (§6.2 read side): finding.research = {verdict, researchedAt}. The stored report is never rewritten.</summary>
    internal static JsonObject WithResearch(Db db, JsonObject report)
    {
        var labels = ResearchLabels(db, report);
        if (labels.Count == 0) return report;
        foreach (var f in report["findings"] as JsonArray ?? [])
            if (f is JsonObject finding && labels.TryGetValue(finding.Text("id"), out var item))
                finding["research"] = new JsonObject { ["verdict"] = item["verdict"]?.DeepClone(), ["researchedAt"] = item["researchedAt"]?.DeepClone() };
        return report;
    }

    /// <summary>
    /// The research items that apply to a report's findings now (finding ID → item). Rule 6: a label stops applying when
    /// the finding's severity rose above the one it was applied to.
    /// </summary>
    internal static Dictionary<string, JsonObject> ResearchLabels(Db db, JsonObject report)
    {
        var labels = db.Query("SELECT finding_id, item, severity FROM research_labels l WHERE report_id = $r AND " + CurrentPolicyLabel,
            r => (Id: r.GetString(0), Item: r.GetString(1), Severity: r.GetString(2)), ("$r", report.Text("reportId")), ("$p", ResearchPolicy.PolicyVersion))
            .ToDictionary(x => x.Id, StringComparer.Ordinal);
        var applied = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (labels.Count == 0) return applied;
        foreach (var f in report["findings"] as JsonArray ?? [])
        {
            if (f is not JsonObject finding || !labels.TryGetValue(finding.Text("id"), out var label)) continue;
            if (ReportBuilder.SeverityRank(finding.Text("severity")) > ReportBuilder.SeverityRank(label.Severity)) continue;
            if (Json.Parse(label.Item) is JsonObject item) applied[finding.Text("id")] = item;
        }
        return applied;
    }

    // ---------------------------------------------------------------- methods

    /// <summary>research.get {reportId} → {runId, status, done, total, trial, items, usage}.</summary>
    public JsonNode ResearchGet(JsonObject p)
    {
        var reportId = P.Req(p, "reportId", 64);
        return _store.Read(db => (JsonNode)ResearchView(db, reportId));
    }

    /// <summary>research.start {idempotencyKey, reportId}: web off → -32013 web_off; no AI → -32013 no_provider; an active run → its view.</summary>
    public JsonNode ResearchStart(JsonObject p)
    {
        var key = P.IdempotencyKey(p);
        var reportId = P.Req(p, "reportId", 64);
        PlannedRun? launch = null;
        PlannedCheckup? checkup = null;
        var (result, _) = _store.Write(db => Idempotency.Run(db, "research.start", key, p, () =>
        {
            launch = BeginRun(db, reportId, auto: false);
            // m9 §6.8: no labels to run → the check-up follows at once (when allowed), created in this same transaction.
            if (launch is null) checkup = HandOffCheckup(db, reportId);
            return ResearchView(db, reportId);
        }));
        if (launch is not null) Launch(launch);
        else if (checkup is not null) LaunchCheckup(checkup);
        return result;
    }

    /// <summary>research.cancel {reportId}: the active run is marked cancelled first, so a call that cannot be stopped publishes nothing.</summary>
    public JsonNode ResearchCancel(JsonObject p)
    {
        var reportId = P.Req(p, "reportId", 64);
        return _store.Write(db =>
        {
            ReportService.Load(db, reportId);
            if (LatestRun(db, reportId) is { } run && ActiveResearch.Contains(run.Status)) CancelRun(db, run.RunId);
            CancelCheckup(db, null, reportId); // m9 §6.8: both phases
            return (JsonNode)ResearchView(db, reportId);
        });
    }

    /// <summary>research.source {sourceId} → {url} (shell-only: the shell re-validates and opens it). A URL failing hygiene → -32013 blocked.</summary>
    public JsonNode ResearchSource(JsonObject p)
    {
        var sourceId = P.Req(p, "sourceId", 64);
        var url = _store.Read(db => db.Scalar("SELECT url FROM research_sources WHERE source_id = $s", ("$s", sourceId)) as string)
            ?? throw RpcError.NotFound("source", sourceId);
        return UrlHygiene.Clean(url) is { } clean ? new JsonObject { ["url"] = clean }
            : throw RpcError.State("blocked", "That source cannot be opened.", ErrorSites.SourceOpenBlocked);
    }

    // ---------------------------------------------------------------- lifecycle hooks

    /// <summary>scan.finished with a report: research starts by itself when do.web is on and the provider can search.</summary>
    private void OnStoreEvent(EventRecord e)
    {
        if (e.Type != "scan.finished" || e.Payload["reportId"]?.GetValue<string>() is not { } reportId) return;
        if (e.Payload["status"]?.GetValue<string>() is not ("complete" or "complete_with_gaps")) return;
        Track(Task.Run(() => AutoStart(reportId)));
    }

    private void AutoStart(string reportId)
    {
        try
        {
            PlannedRun? launch = null;
            PlannedCheckup? checkup = null;
            _store.Write(db =>
            {
                launch = BeginRun(db, reportId, auto: true);
                if (launch is null) checkup = HandOffCheckup(db, reportId); // m9 §6.8: nothing to label → the check-up follows at once
            });
            if (launch is not null) Launch(launch);
            else if (checkup is not null) LaunchCheckup(checkup);
        }
        catch (Exception ex) when (ex is RpcError or ObjectDisposedException or InvalidOperationException)
        {
            ResearchLog($"auto start failed {ex.GetType().Name}{(ex is RpcError r ? " code=" + r.Code : "")}", ErrorSites.ResearchNotStarted, LogLevel.Warn);
        }
    }

    /// <summary>A new scan cancels any running research (its report is about to stop being current).</summary>
    internal void ResearchOnScanStarted()
    {
        try { _store.Write(db => CancelActiveResearch(db, null)); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Inside the ai.setPermissions transaction: any save cancels a running run (the revision changed). Returns true when
    /// the run should start again for the current report (web stays on and a run was cancelled, or web was just turned on).
    /// </summary>
    private bool ResearchOnPermissionsSaved(Db db, Permissions before, Permissions after)
    {
        var cancelled = CancelActiveResearch(db, null);
        return after.Web && (cancelled || !before.Web);
    }

    private void RestartResearchForCurrentReport()
    {
        var reportId = _store.Read(db => db.Scalar("SELECT report_id FROM reports ORDER BY rowid DESC LIMIT 1") as string);
        if (reportId is not null) Track(Task.Run(() => AutoStart(reportId)));
    }

    /// <summary>Marks every active run (of one provider kind, or all) cancelled. True when one was.</summary>
    private bool CancelActiveResearch(Db db, string? kind)
    {
        var ids = db.Query("SELECT run_id FROM research_runs WHERE status IN ('queued','running') AND ($k IS NULL OR provider_kind = $k)", r => r.GetString(0), ("$k", kind));
        foreach (var id in ids) CancelRun(db, id);
        var checkup = CancelCheckup(db, kind); // m9 §6.8: the check-up phase is cancelled by the same hooks
        return ids.Count > 0 || checkup;
    }

    private void CancelRun(Db db, string runId)
    {
        db.Exec("UPDATE research_runs SET status = 'cancelled', trial = 0, finished_at = $t WHERE run_id = $r AND status IN ('queued','running')",
            ("$t", db.Now()), ("$r", runId));
        EmitState(db, runId);
        CancellationTokenSource? cts;
        lock (_gate) _researchRuns.Remove(runId, out cts);
        if (cts is not null) _ = Task.Run(() => { try { cts.Cancel(); } catch (ObjectDisposedException) { } }); // off the store lock
    }

    /// <summary>On startup: a run the previous host left active is cancelled, never resumed.</summary>
    private void RecoverResearch() => _store.Write(db =>
    {
        db.Exec("UPDATE research_runs SET status = 'cancelled', trial = 0, finished_at = $t WHERE status IN ('queued','running')", ("$t", db.Now()));
        RecoverCheckup(db);
    });

    /// <summary>Host shutdown: active runs are marked cancelled before the store closes.</summary>
    private void StopResearch()
    {
        try { _store.Write(db => CancelActiveResearch(db, null)); }
        catch (ObjectDisposedException) { }
    }

    // ---------------------------------------------------------------- starting a run

    private sealed record PlannedRun(string RunId, string ReportId, string Kind, string? Model, string Account, long Revision, List<ResearchSubject> Subjects,
        Dictionary<string, string> Severities, HashSet<string> ThreatFiles, bool Trial, CancellationTokenSource Cts);

    /// <summary>
    /// Creates the run row (in the caller's transaction) and returns what to launch, or null when nothing runs (an active
    /// run already exists, nothing to research, or web is "no"). Auto starts return null instead of refusing.
    /// </summary>
    private PlannedRun? BeginRun(Db db, string reportId, bool auto)
    {
        var report = ReportService.Load(db, reportId);
        var perms = ReadPermissions(db);
        if (!perms.Web)
        {
            if (auto) { ResearchLog($"not started report={reportId} reason=web_off auto=true", ErrorSites.ResearchNotStarted, LogLevel.Info); return null; }
            throw RpcError.State("web_off", "Online lookups are off.");
        }
        var kind = SelectedKind(db);
        if (kind is null)
        {
            if (auto) { ResearchLog($"not started report={reportId} reason=no_provider auto=true", ErrorSites.ResearchNotStarted, LogLevel.Info); return null; }
            throw RpcError.State("no_provider", "No AI is selected.");
        }
        if (LatestRun(db, reportId) is { } current && ActiveResearch.Contains(current.Status)) { ResearchLog($"not started report={reportId} reason=run_active run={current.RunId}"); return null; }
        if (auto && db.Scalar("SELECT report_id FROM reports ORDER BY rowid DESC LIMIT 1") as string != reportId) { ResearchLog($"not started report={reportId} reason=report_not_current"); return null; }

        // One run at a time: a run for another report ends.
        foreach (var other in db.Query("SELECT run_id FROM research_runs WHERE status IN ('queued','running')", r => r.GetString(0))) CancelRun(db, other);

        var model = ResearchModel(kind);
        var web = WebFor(db, kind, model);
        if (auto && web == "no") { ResearchLog($"not started report={reportId} reason={WebNoReason(kind)} kind={kind}", ErrorSites.ResearchNotStarted, LogLevel.Info); return null; }
        var subjects = ResearchPolicy.Subjects(report, ResearchKindOf, _options.FileIdentity, LocalIdentities(), out var skipped);
        var runId = "research-" + Ids.RandomHex(8);
        if (skipped > 0)
        {
            _log($"research {runId}: {skipped} finding(s) skipped (no name that may leave this PC)"); // host.log (tests read it)
            ResearchLog($"{runId}: subjects skipped={skipped} reason=no_name_may_leave (identity filter or never-store)", ErrorSites.ResearchSubjectsSkipped);
        }
        var status = web == "no" ? "unavailable" : subjects.Count == 0 ? "done" : "queued";
        var trial = status == "queued" && web == "unknown";
        db.Exec("""
            INSERT INTO research_runs (run_id, report_id, status, done, total, trial, provider_kind, account, model, permissions_revision, usage, started_at, finished_at)
            VALUES ($id, $r, $s, 0, $n, $tr, $k, $a, $m, $rev, $u, $t, $f)
            """,
            ("$id", runId), ("$r", reportId), ("$s", status), ("$n", subjects.Count), ("$tr", trial ? 1 : 0), ("$k", kind), ("$a", AccountOf(kind)),
            ("$m", model), ("$rev", perms.Revision), ("$u", Json.Write(EmptyUsage())), ("$t", db.Now()), ("$f", status == "queued" ? null : db.Now()));
        EmitState(db, runId);
        ResearchLog($"{runId}: begin report={reportId} kind={kind} model={model} web={web} subjects={subjects.Count} status={status}{(web == "no" ? " reason=" + WebNoReason(kind) : "")}",
            web == "no" ? ErrorSites.ResearchNotStarted : null, web == "no" ? LogLevel.Info : LogLevel.Debug);
        if (status != "queued") return null;

        var severities = (report["findings"] as JsonArray ?? []).ToDictionary(f => f.Text("id"), f => f.Text("severity"), StringComparer.Ordinal);
        var cts = new CancellationTokenSource();
        lock (_gate) _researchRuns[runId] = cts;
        return new PlannedRun(runId, reportId, kind, model, AccountOf(kind), perms.Revision, subjects, severities, ResearchPolicy.ThreatFiles(report), trial, cts);
    }

    /// <summary>This PC's user, PC and domain names (the options seam, else Windows), for the identity filter.</summary>
    private IReadOnlyList<LocalIdentity> LocalIdentities() => _options.LocalIdentities?.Invoke() ?? IdentityFilter.Local();

    /// <summary>The rule's Research kind (m9 §6.1), or null when the rule does not declare one.</summary>
    private string? ResearchKindOf(string ruleId) => _catalog.Rules.FirstOrDefault(r => r.Id == ruleId)?.Research;

    private void Launch(PlannedRun run) => Track(Task.Run(() => RunResearchAsync(run)));

    // ---------------------------------------------------------------- the run

    private enum CallOutcome { Labelled, NoLabel, Failed, Unsupported, Fenced }

    private async Task RunResearchAsync(PlannedRun run)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(run.Cts.Token);
        budget.CancelAfter(ResearchPolicy.RunBudget);
        var token = budget.Token;
        int failed = 0, reached = 0;
        var unsupported = false;
        try
        {
            if (!_store.Write(db => Fence(db, run, markRunning: true))) return;

            ProviderCredential? credential = null;
            var researcher = Researcher(run.Kind);
            if (InAppKinds.Contains(run.Kind) && researcher is not null)
            {
                credential = await CredentialAsync(run.Kind, token).ConfigureAwait(false);
                if (credential is null) { ResearchLog($"{run.RunId}: failed reason=not_signed_in kind={run.Kind}", ErrorSites.ResearchNotSignedIn, LogLevel.Warn); Finish(run, "failed"); return; }
            }

            // Cached products are not researched again; the host rules are re-applied to this report every time.
            var pending = new List<ResearchSubject>();
            foreach (var subject in run.Subjects)
            {
                var cached = _store.Read(db => CachedProduct(db, subject.ProductKey));
                if (cached is null) { pending.Add(subject); continue; }
                if (_store.Write(db => StoreCached(db, run, subject, cached)) == CallOutcome.Fenced) return;
                reached++;
            }
            if (pending.Count == 0) { ResearchLog($"{run.RunId}: end status=done reason=all_cached"); Finish(run, "done"); return; }
            if (researcher is null)
            {
                // No research seam for this kind (no CLI runner or data folder in this build): no labels, nothing stored.
                ResearchLog($"{run.RunId}: unavailable reason=no_research_seam kind={run.Kind}", ErrorSites.ResearchNoSeam, LogLevel.Warn);
                Finish(run, "unavailable");
                return;
            }

            // The trial (web "unknown"): the first subject runs alone; its outcome decides availability.
            var start = 0;
            if (run.Trial)
            {
                var outcome = await CallOneAsync(run, researcher, credential, pending[0], token).ConfigureAwait(false);
                start = 1;
                reached++;
                if (outcome == CallOutcome.Fenced) return;
                if (outcome == CallOutcome.Unsupported) { unsupported = true; return; }
                if (outcome == CallOutcome.Failed) failed++;
            }

            using var slots = new SemaphoreSlim(InAppKinds.Contains(run.Kind) ? 3 : 1);
            var stop = false;
            var tasks = pending.Skip(start).Select(async subject =>
            {
                try { await slots.WaitAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                try
                {
                    if (Volatile.Read(ref stop) || token.IsCancellationRequested) return;
                    var outcome = await CallOneAsync(run, researcher, credential, subject, token).ConfigureAwait(false);
                    Interlocked.Increment(ref reached);
                    switch (outcome)
                    {
                        case CallOutcome.Failed: Interlocked.Increment(ref failed); break;
                        case CallOutcome.Unsupported: unsupported = true; Volatile.Write(ref stop, true); break;
                        case CallOutcome.Fenced: Volatile.Write(ref stop, true); break;
                    }
                }
                finally { slots.Release(); }
            }).ToList();
            await Task.WhenAll(tasks).ConfigureAwait(false);
            if (unsupported) return;
            var status = reached < run.Subjects.Count ? "partial" : failed == 0 ? "done" : failed == run.Subjects.Count ? "failed" : "partial";
            ResearchLog($"{run.RunId}: end status={status} reached={reached} failed={failed} subjects={run.Subjects.Count}");
            Finish(run, status);
        }
        catch (OperationCanceledException)
        {
            ResearchLog($"{run.RunId}: stopped reason={(run.Cts.IsCancellationRequested ? "cancelled" : "run_budget")}", run.Cts.IsCancellationRequested ? null : ErrorSites.ResearchCallFailed, run.Cts.IsCancellationRequested ? LogLevel.Debug : LogLevel.Warn);
            Finish(run, run.Cts.IsCancellationRequested ? "cancelled" : "partial");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Current.Error($"research: {run.RunId}: run failed {ex.GetType().Name}", ErrorSites.ResearchRunFailed, ex);
            Finish(run, "failed");
        }
        finally
        {
            lock (_gate) _researchRuns.Remove(run.RunId);
            run.Cts.Dispose();
        }
    }

    /// <summary>One isolated call for one subject, then its result through the fence.</summary>
    private async Task<CallOutcome> CallOneAsync(PlannedRun run, IWebResearch researcher, ProviderCredential? credential, ResearchSubject subject, CancellationToken token)
    {
        ResearchAnswer answer;
        using (var call = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            call.CancelAfter(ResearchPolicy.CallTimeout);
            try
            {
                answer = await researcher.ResearchAsync(credential ?? new ProviderCredential(run.Kind, ""), ResearchPolicy.Call(run.Model ?? "", subject), call.Token)
                    .WaitAsync(call.Token).ConfigureAwait(false);
            }
            catch (WebResearchUnsupportedException ex)
            {
                // Refused as unsupported: web "no" for this provider and model; the run ends unavailable. Nothing else is stored.
                ResearchLog($"{run.RunId}: unavailable reason=unsupported_{ex.Reason} kind={run.Kind} model={run.Model}", ErrorSites.ResearchUnsupported, LogLevel.Warn);
                var fenced = _store.Write(db =>
                {
                    if (!Fence(db, run)) return true;
                    if (ex.Reason != "not_available_yet") SetWeb(db, run.Kind, run.Model, "no");
                    db.Exec("UPDATE research_runs SET status = 'unavailable', trial = 0, finished_at = $t WHERE run_id = $r", ("$t", db.Now()), ("$r", run.RunId));
                    EmitState(db, run.RunId);
                    return false;
                });
                if (!fenced) CancelLocal(run);
                return fenced ? CallOutcome.Fenced : CallOutcome.Unsupported;
            }
            catch (OperationCanceledException) when (run.Cts.IsCancellationRequested) { return CallOutcome.Fenced; }
            catch (Exception ex) when (ex is ProviderException or OperationCanceledException or TimeoutException or System.Net.Http.HttpRequestException)
            {
                // Sign-in, network, rate-limit and timeout failures change nothing about availability; the subject stays unlabelled.
                ResearchLog($"{run.RunId}: call failed reason={(ex is ProviderException pe ? "provider_error code=" + pe.Code : ex is OperationCanceledException or TimeoutException ? "timeout" : "network")}", ErrorSites.ResearchCallFailed, LogLevel.Warn);
                if (token.IsCancellationRequested && !run.Cts.IsCancellationRequested) { ResearchLog($"{run.RunId}: run budget ran out", ErrorSites.ResearchCallFailed, LogLevel.Warn); return CallOutcome.Failed; } // the run budget ran out
                return _store.Write(db => CountFailure(db, run)) ? CallOutcome.Failed : CallOutcome.Fenced;
            }
        }
        return _store.Write(db => StoreAnswer(db, run, subject, answer));
    }

    private void CancelLocal(PlannedRun run)
    {
        lock (_gate) _researchRuns.Remove(run.RunId);
        try { run.Cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// The fence (§6.3): all of these hold, or nothing is stored and the run is cancelled: this run is the report's active
    /// run, the permissions revision is unchanged with web on, the provider kind, account and model are unchanged, and the
    /// report is still the current one.
    /// </summary>
    private bool Fence(Db db, PlannedRun run, bool markRunning = false)
    {
        var row = RunById(db, run.RunId);
        if (row is null || !ActiveResearch.Contains(row.Status))
        {
            ResearchLog($"{run.RunId}: fenced reason=run_not_active status={row?.Status ?? "missing"}", ErrorSites.ResearchFenced, LogLevel.Info);
            return false;
        }
        var perms = ReadPermissions(db);
        var reason = LatestRun(db, run.ReportId)?.RunId != run.RunId ? "not_latest_run"
            : !perms.Web ? "web_off"
            : perms.Revision != run.Revision ? "permissions_changed"
            : SelectedKind(db) != run.Kind ? "provider_changed"
            : ResearchModel(run.Kind) != run.Model ? "model_changed"
            : AccountOf(run.Kind) != run.Account ? "account_changed"
            : db.Scalar("SELECT report_id FROM reports ORDER BY rowid DESC LIMIT 1") as string != run.ReportId ? "report_not_current"
            : null;
        if (reason is not null)
        {
            ResearchLog($"{run.RunId}: fenced reason={reason}; run cancelled", ErrorSites.ResearchFenced, LogLevel.Info);
            CancelRun(db, run.RunId);
            return false;
        }
        if (markRunning && row.Status == "queued")
        {
            db.Exec("UPDATE research_runs SET status = 'running' WHERE run_id = $r", ("$r", run.RunId));
            EmitState(db, run.RunId);
        }
        return true;
    }

    private bool CountFailure(Db db, PlannedRun run)
    {
        if (!Fence(db, run)) return false;
        db.Exec("UPDATE research_runs SET done = done + 1, trial = 0 WHERE run_id = $r", ("$r", run.RunId));
        EmitState(db, run.RunId);
        return true;
    }

    private void Finish(PlannedRun run, string status)
    {
        try
        {
            // m9 §6.8: the check-up follows the labels phase (not after a cancelled run, nor when the provider refused search).
            // It is created in the transaction that ends the labels phase, so a cancel (or a permissions save, a provider
            // change, a new scan) either finds the labels still active or the check-up already running: never neither.
            var next = _store.Write(db =>
            {
                if (RunById(db, run.RunId) is not { } row || !ActiveResearch.Contains(row.Status)) return null;
                db.Exec("UPDATE research_runs SET status = $s, trial = 0, finished_at = $t WHERE run_id = $r", ("$s", status), ("$t", db.Now()), ("$r", run.RunId));
                EmitState(db, run.RunId);
                return status is not ("cancelled" or "unavailable") ? HandOffCheckup(db, run.ReportId) : null;
            });
            if (next is not null) LaunchCheckup(next);
        }
        catch (ObjectDisposedException) { }
    }

    // ---------------------------------------------------------------- storing

    /// <summary>A fresh general result for a product (30 days, checked on read), or null.</summary>
    private static JsonObject? CachedProduct(Db db, string productKey) =>
        db.Query("SELECT body FROM research_products WHERE product_key = $k AND policy_version = $p AND expires_at > $n",
            r => Json.Parse(r.GetString(0)) as JsonObject, ("$k", productKey), ("$p", ResearchPolicy.PolicyVersion), ("$n", db.Now())).FirstOrDefault();

    /// <summary>A fresh answer: parse, keep only observed sources, cache the general result, apply the host rules per finding.</summary>
    private CallOutcome StoreAnswer(Db db, PlannedRun run, ResearchSubject subject, ResearchAnswer answer)
    {
        if (!Fence(db, run)) return CallOutcome.Fenced;
        var usage = RunById(db, run.RunId)!.Usage;
        usage["searches"] = (usage["searches"]?.GetValue<long>() ?? 0) + Math.Max(0, answer.Searches);
        usage["inputTokens"] = (usage["inputTokens"]?.GetValue<long>() ?? 0) + Math.Max(0, answer.Usage.InputTokens);
        usage["outputTokens"] = (usage["outputTokens"]?.GetValue<long>() ?? 0) + Math.Max(0, answer.Usage.OutputTokens);
        db.Exec("UPDATE research_runs SET done = done + 1, trial = 0, usage = $u WHERE run_id = $r", ("$u", Json.Write(usage)), ("$r", run.RunId));
        // Observed sources → the provider can search (§6.4). A call without any changes nothing.
        if (answer.Observed.Count > 0) SetWeb(db, run.Kind, run.Model, "yes");

        var body = StoreProduct(db, run.RunId, subject, answer, By(run.Kind, run.Model));
        var outcome = body is not null && ApplyLabels(db, run.ReportId, run.RunId, run.Severities, run.ThreatFiles, subject, body) ? CallOutcome.Labelled : CallOutcome.NoLabel;
        EmitState(db, run.RunId);
        ResearchLog($"{run.RunId}: call {(outcome == CallOutcome.Labelled ? "labelled" : "no_label")} observed={answer.Observed.Count} searches={answer.Searches}");
        return outcome;
    }

    /// <summary>
    /// Parses a fresh answer, keeps only observed sources (rule 1: none → null, nothing stored), stores them by ID and
    /// caches the general result for its product. Shared by research runs and chat lookups (web_lookup, m9 §7).
    /// </summary>
    private static JsonObject? StoreProduct(Db db, string runId, ResearchSubject subject, ResearchAnswer answer, JsonObject by)
    {
        var verdict = ResearchPolicy.ParseAnswer(answer.Text, out var dropReason);
        if (verdict is null)
        {
            ResearchLog($"{runId}: answer dropped reason={dropReason} chars={answer.Text?.Length ?? 0}", ErrorSites.ResearchAnswerDropped, LogLevel.Info);
            return null;
        }
        var picked = ResearchPolicy.PickSources(verdict, answer.Observed);
        if (picked.Count == 0)
        {
            ResearchLog($"{runId}: answer dropped reason=no_observed_source listed={verdict.Urls.Count} observed={answer.Observed.Count}", ErrorSites.ResearchNoObservedSource, LogLevel.Info);
            return null;
        }
        var at = db.Now();
        var sources = new JsonArray();
        foreach (var s in picked)
        {
            var id = "src-" + Ids.RandomHex(8);
            var title = ResearchPolicy.SourceTitle(s);
            var domain = UrlHygiene.Domain(s.Url);
            db.Exec("INSERT INTO research_sources (source_id, url, title, domain, run_id, product_key, created_at) VALUES ($i, $u, $t, $d, $r, $k, $c)",
                ("$i", id), ("$u", s.Url), ("$t", title), ("$d", domain), ("$r", runId), ("$k", subject.ProductKey), ("$c", at));
            sources.Add(new JsonObject { ["sourceId"] = id, ["title"] = title, ["domain"] = domain });
        }
        var body = new JsonObject
        {
            ["verdict"] = verdict.Verdict, ["summary"] = verdict.Summary, ["sources"] = sources, ["by"] = by, ["researchedAt"] = at,
        };
        var expires = Time.Format(DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture) + ResearchPolicy.CacheFor);
        db.Exec("""
            INSERT INTO research_products (product_key, policy_version, body, researched_at, expires_at) VALUES ($k, $p, $b, $t, $e)
            ON CONFLICT (product_key, policy_version) DO UPDATE SET body = $b, researched_at = $t, expires_at = $e
            """, ("$k", subject.ProductKey), ("$p", ResearchPolicy.PolicyVersion), ("$b", Json.Write(body)), ("$t", at), ("$e", expires));
        return body;
    }

    private CallOutcome StoreCached(Db db, PlannedRun run, ResearchSubject subject, JsonObject body)
    {
        if (!Fence(db, run)) return CallOutcome.Fenced;
        db.Exec("UPDATE research_runs SET done = done + 1, trial = 0 WHERE run_id = $r", ("$r", run.RunId));
        var labelled = ApplyLabels(db, run.ReportId, run.RunId, run.Severities, run.ThreatFiles, subject, body);
        EmitState(db, run.RunId);
        return labelled ? CallOutcome.Labelled : CallOutcome.NoLabel;
    }

    /// <summary>Applies the general result to each finding of the subject in this report, after the host rules ran on it.</summary>
    private static bool ApplyLabels(Db db, string reportId, string runId, IReadOnlyDictionary<string, string> severities, ISet<string> threatFiles,
        ResearchSubject subject, JsonObject body)
    {
        var sources = body["sources"] as JsonArray ?? [];
        var any = false;
        foreach (var findingId in subject.FindingIds)
        {
            var severity = severities.GetValueOrDefault(findingId) ?? "info";
            var applied = ResearchPolicy.Apply(severity, subject, body.Text("verdict"), sources.Count, threatFiles);
            if (applied is null) continue;
            var item = new JsonObject
            {
                ["findingId"] = findingId, ["verdict"] = applied.Value.Verdict, ["summary"] = body.Text("summary"),
                ["sources"] = sources.DeepClone(), ["basis"] = applied.Value.Basis, ["by"] = body["by"]?.DeepClone(),
                ["researchedAt"] = body.Text("researchedAt"),
            };
            db.Exec("""
                INSERT INTO research_labels (report_id, finding_id, run_id, product_key, severity, item, researched_at) VALUES ($r, $f, $run, $k, $s, $i, $t)
                ON CONFLICT (report_id, finding_id) DO UPDATE SET run_id = $run, product_key = $k, severity = $s, item = $i, researched_at = $t
                """, ("$r", reportId), ("$f", findingId), ("$run", runId), ("$k", subject.ProductKey), ("$s", severity), ("$i", Json.Write(item)),
                ("$t", body.Text("researchedAt")));
            any = true;
        }
        return any;
    }

    private JsonObject By(string kind, string? model) => new() { ["kind"] = kind, ["label"] = ProviderLabel(kind), ["model"] = model };
}

using System.Globalization;

using System.Text.Json.Nodes;

namespace OptimizationTool.Engine.Agent;

/// <summary>Who is calling: the pipe connection and its client kind (desktop | test | agent).</summary>
/// <summary>ProcessId is the client process verified by the host (GetNamedPipeClientProcessId), not a claim.</summary>
public sealed record CallContext(string ConnectionId, string ClientKind, int ProcessId = 0, string? UserSid = null, int Session = 0);

/// <summary>M3 wiring the host fills in (contracts/m3-agent.md). Defaults are safe: no providers, memory-only keys, deny-all projection.</summary>
public sealed class AgentOptions
{
    /// <summary>In-app model adapters by <see cref="IProviderAdapter.Kind"/>.</summary>
    public IReadOnlyList<IProviderAdapter> Providers { get; init; } = [];

    /// <summary>Key storage. Null uses a memory-only store for this process.</summary>
    public ICredentialStore? Credentials { get; init; }

    /// <summary>
    /// The projection entry point (§3.5), given a stored evidence body. Null uses the projection owner's
    /// <c>EvidenceProjection.ProjectStored</c>. Tests inject fakes.
    /// </summary>
    public Func<JsonObject, IAliasMap, ShareOptions, string?, ProjectionStats, ProjectedEvidence?>? Project { get; init; }

    /// <summary>Wait before the single retry of a rate-limited or overloaded turn (the seam carries no retry-after).</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Full path of OptimizationTool.Mcp.exe, for the registration commands provider.connect returns.</summary>
    public string? McpExecutablePath { get; init; }

    /// <summary>Browser sign-ins for account-based kinds (chatgpt_plan). Implemented in src/Providers.</summary>
    public IReadOnlyList<IProviderSignIn> SignIns { get; init; } = [];

    /// <summary>Opens a URL in the system browser (tests replace it). Only ever called with a validated issuer URL.</summary>
    public Func<Uri, bool>? OpenBrowser { get; init; }

    /// <summary>
    /// How long a browser sign-in may take before it is abandoned (callback_timeout). The sign-in's own wait for the
    /// browser is 3 minutes; this adds room for the token exchange after it.
    /// </summary>
    public TimeSpan SignInTimeout { get; init; } = TimeSpan.FromMinutes(3.5);

    /// <summary>M5: starts headless Claude Code / Codex runs (src/Host CliRunner). Null: MCP kinds cannot be asked in-app.</summary>
    public ICliRunner? CliRunner { get; init; }
}

/// <summary>Task limits (§2). Defaults are the contract's example values.</summary>
public sealed record Limits(int DiagnosticCalls = 10, int ResultBytes = 32768, int TotalBytes = 131072, int Turns = 8, int TurnSeconds = 90, int MaxSampleSeconds = 60)
{
    public JsonObject ToJson() => new()
    {
        ["diagnosticCalls"] = DiagnosticCalls, ["resultBytes"] = ResultBytes, ["totalBytes"] = TotalBytes,
        ["turns"] = Turns, ["turnSeconds"] = TurnSeconds, ["maxSampleSeconds"] = MaxSampleSeconds,
    };
}

/// <summary>What a task may do and share (§2 grant). CanRequestChanges (M4, default off): the task may ask for catalog changes the user approves.</summary>
public sealed record Grant(IReadOnlyList<string> Diagnostics, bool ShareNames, Limits Limits, int IdleExpirySeconds, bool CanRequestChanges = false)
{
    public JsonObject ToJson() => new()
    {
        ["diagnostics"] = Json.Array(Diagnostics), ["share"] = new JsonObject { ["names"] = ShareNames },
        ["limits"] = Limits.ToJson(), ["idleExpirySeconds"] = IdleExpirySeconds, ["canRequestChanges"] = CanRequestChanges,
    };

    public static Grant FromJson(JsonNode node) => new(
        Json.Strings(node["diagnostics"]), node["share"]?["names"]?.GetValue<bool>() ?? false,
        new Limits(I(node, "diagnosticCalls"), I(node, "resultBytes"), I(node, "totalBytes"), I(node, "turns"), I(node, "turnSeconds"), I(node, "maxSampleSeconds")),
        node["idleExpirySeconds"]!.GetValue<int>(), node["canRequestChanges"]?.GetValue<bool>() ?? false);

    private static int I(JsonNode node, string key) => node["limits"]![key]!.GetValue<int>();

    /// <summary>Validates a client-supplied grant; missing limits take defaults; out-of-range values are refused.</summary>
    public static Grant Parse(JsonNode? node)
    {
        if (node is not JsonObject g) throw RpcError.Params("grant", "grant must be an object.");
        var diagnostics = g["diagnostics"] is JsonArray a ? a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : "").ToList() : null;
        if (diagnostics is null || diagnostics.Any(d => Agent.Diagnostics.Find(d) is null))
            throw RpcError.Params("grant.diagnostics", "grant.diagnostics must list known diagnostics.");
        var names = g["share"]?["names"] is JsonValue nv && nv.TryGetValue<bool>(out var n) && n;
        var d = new Limits();
        var l = g["limits"] as JsonObject ?? [];
        int Lim(string key, int fallback, int min, int max)
        {
            if (l[key] is null) return fallback;
            if (l[key] is JsonValue v && v.TryGetValue<int>(out var x) && x >= min && x <= max) return x;
            throw RpcError.Params("grant.limits." + key, $"{key} must be {min}-{max}.");
        }
        var limits = new Limits(
            Lim("diagnosticCalls", d.DiagnosticCalls, 0, 50), Lim("resultBytes", d.ResultBytes, 1024, 262144),
            Lim("totalBytes", d.TotalBytes, 1024, 1 << 20), Lim("turns", d.Turns, 1, 20),
            Lim("turnSeconds", d.TurnSeconds, 5, 300), Lim("maxSampleSeconds", d.MaxSampleSeconds, Agent.Diagnostics.MinSampleSeconds, 120));
        if (limits.TotalBytes < limits.ResultBytes) throw RpcError.Params("grant.limits.totalBytes", "totalBytes must be at least resultBytes.");
        var idle = 900;
        if (g["idleExpirySeconds"] is { } iv)
            idle = iv is JsonValue v && v.TryGetValue<int>(out var x) && x is >= 60 and <= 86400 ? x : throw RpcError.Params("grant.idleExpirySeconds", "idleExpirySeconds must be 60-86400.");
        var changes = g["canRequestChanges"] is JsonValue cv && cv.TryGetValue<bool>(out var c) && c;
        return new Grant(diagnostics.Distinct().ToList(), names, limits, idle, changes);
    }
}

/// <summary>
/// The agent gateway (contracts/m3-agent.md): tasks and grants, the model-facing tools, the in-app loop, providers,
/// pairing with native clients and report reviews. Split across AgentService.*.cs by concern.
/// </summary>
public sealed partial class AgentService
{
    public const string TaskStreamPrefix = "task:";
    public static readonly string[] RecipientKinds = ["anthropic_api", "openai_api", "chatgpt_plan", "claude_code_mcp", "codex_mcp"];
    public static readonly string[] InAppKinds = ["anthropic_api", "openai_api", "chatgpt_plan"];

    /// <summary>In-app kinds configured with an API key (provider.setKey). chatgpt_plan signs in through the browser instead.</summary>
    public static readonly string[] ApiKeyKinds = ["anthropic_api", "openai_api"];

    private readonly Store _store;
    private readonly AuditCatalog _catalog;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly AgentOptions _options;
    private readonly ICredentialStore _credentials;
    private readonly Func<JsonObject, IAliasMap, ShareOptions, string?, ProjectionStats, ProjectedEvidence?> _project;
    private readonly object _gate = new();
    private readonly Dictionary<string, CancellationTokenSource> _taskTokens = [];
    private readonly Dictionary<string, AskRun> _asking = [];

    /// <summary>
    /// The ask running on a task (one at a time). agent.cancel marks it <see cref="Cancelled"/>; before publishing its
    /// answer the run claims it (<see cref="Answering"/>), after which agent.cancel reports cancelled=false. Both flags
    /// change only under <c>_gate</c>, so a cancel that returns true always means no agent.message for that run.
    /// </summary>
    private sealed class AskRun
    {
        public bool Cancelled;
        public bool Answering;
        public bool Revoked; // the task ended (stop, expiry, recipient change): no answer, whatever the run says
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>How long agent.cancel waits for the cancelled run to end, so an ask right after it is not refused as busy.</summary>
    internal static readonly TimeSpan CancelWait = TimeSpan.FromSeconds(5);

    /// <summary>The run is over: the task takes asks again, and an agent.cancel waiting on it returns.</summary>
    private void EndRun(string taskId)
    {
        AskRun? run;
        lock (_gate) _asking.Remove(taskId, out run);
        run?.Done.TrySetResult();
    }

    /// <summary>
    /// The run's last word, in one store transaction: agent.turn finished (when <paramref name="usage"/> is given) and
    /// agent.message, claiming the answer so a later agent.cancel reports cancelled=false. Refused, with agent.turn
    /// failed instead, when agent.cancel or the task's end (stop, expiry, a recipient change) got there first or the
    /// task is no longer active. Because the stop also commits through the store, the message can never follow the
    /// task's end. True when published.
    /// </summary>
    private bool Publish(string taskId, string turnId, int n, string text, JsonObject? usage = null) => _store.Write(db =>
    {
        var task = LoadTask(db, taskId);
        if (Refusal(task) is { } refused)
        {
            db.AppendEvent(Stream(taskId), "agent.turn", new JsonObject { ["turnId"] = turnId, ["n"] = n, ["status"] = "failed", ["code"] = refused });
            return false;
        }
        if (usage is not null)
            db.AppendEvent(Stream(taskId), "agent.turn", new JsonObject { ["turnId"] = turnId, ["n"] = n, ["status"] = "finished", ["usage"] = usage });
        AppendMessage(db, taskId, turnId, text);
        return true;
    });

    /// <summary>
    /// Why the running ask may not publish (cancelled, or the task's inactive code), or null: then the answer is
    /// claimed (a later agent.cancel reports cancelled=false). Called inside a store transaction.
    /// </summary>
    private string? Refusal(JsonObject task)
    {
        lock (_gate)
        {
            _asking.TryGetValue(task.Text("taskId"), out var run);
            var refused = run is { Cancelled: true } ? "cancelled"
                : run is { Revoked: true } || task.Text("status") != "active" ? InactiveCode(task) ?? "revoked"
                : null;
            if (refused is null && run is not null) run.Answering = true;
            return refused;
        }
    }

    /// <summary>
    /// The run found its turn budget used up: in one transaction, an active task becomes exhausted and the run says
    /// why. A task that already ended (stop, expiry) or a cancelled run keeps its status and gets agent.turn failed.
    /// </summary>
    internal void Exhaust(string taskId, string turnId, int n) => _store.Write(db =>
    {
        var task = LoadTask(db, taskId);
        if (Refusal(task) is { } refused)
        {
            db.AppendEvent(Stream(taskId), "agent.turn", new JsonObject { ["turnId"] = turnId, ["n"] = n, ["status"] = "failed", ["code"] = refused });
            return;
        }
        SetStatus(db, task, "exhausted");
        AppendMessage(db, taskId, turnId, "I stopped because this task has used all of its turns.");
    });

    /// <summary>
    /// A turn of this task that started but never ended (the host stopped mid-ask) and has no live run: it gets
    /// agent.turn failed {code:"interrupted"}, so the UI stops waiting. True when one was closed. In a store transaction.
    /// </summary>
    private bool InterruptUnfinished(Db db, string taskId)
    {
        lock (_gate) if (_asking.ContainsKey(taskId)) return false; // a live run ends its own turn
        var events = db.Query("SELECT type, payload FROM events WHERE stream = $s AND type IN ('agent.turn','agent.message') ORDER BY seq DESC LIMIT 200",
            r => (Type: r.GetString(0), Payload: Json.Parse(r.GetString(1)) as JsonObject), ("$s", Stream(taskId)));
        var last = events.FirstOrDefault(e => e.Type == "agent.turn" && e.Payload?["status"]?.GetValue<string>() == "started");
        if (last.Payload?["turnId"]?.GetValue<string>() is not { } turnId) return false;
        var ended = events.Any(e => e.Payload?["turnId"]?.GetValue<string>() == turnId
            && (e.Type == "agent.message" || e.Payload?["status"]?.GetValue<string>() == "failed"));
        if (ended) return false;
        var n = last.Payload["n"] is JsonValue nv && nv.TryGetValue<int>(out var x) ? x : 0;
        db.AppendEvent(Stream(taskId), "agent.turn", new JsonObject { ["turnId"] = turnId, ["n"] = n, ["status"] = "failed", ["code"] = "interrupted" });
        return true;
    }

    /// <summary>agent.cancel or the task's end reached the running ask (its token is cancelled moments later, off the caller's thread).</summary>
    private bool CancelRequested(string taskId)
    {
        lock (_gate) return _asking.TryGetValue(taskId, out var run) && (run.Cancelled || run.Revoked);
    }

    /// <summary>The task ended (SetStatus): its running ask may no longer publish an answer. Called under the store lock.</summary>
    private void RevokeRun(string taskId)
    {
        lock (_gate) if (_asking.TryGetValue(taskId, out var run)) run.Revoked = true;
    }
    private readonly List<Task> _background = [];

    /// <summary>M4: the action pipeline (request_action, get_operation by planId, cancelling a task's pending plan).</summary>
    internal Actions.ActionService? Actions { get; init; }

    internal AgentService(Store store, AuditCatalog catalog, TimeProvider time, AgentOptions options, Action<string> log)
    {
        _store = store;
        _catalog = catalog;
        _time = time;
        _options = options;
        _log = log;
        _credentials = options.Credentials ?? new MemoryCredentialStore();
        _project = options.Project ?? ((body, aliases, share, declared, stats) => EvidenceProjection.ProjectStored(body, aliases, share, declaredSensitivity: declared, stats));
    }


    internal bool HasActiveWork
    {
        get { lock (_gate) return _asking.Count > 0 || _background.Any(t => !t.IsCompleted); }
    }

    public static string Stream(string taskId) => TaskStreamPrefix + taskId;

    private DateTimeOffset Now() => _time.GetUtcNow();
    private static DateTimeOffset ParseTime(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private CancellationToken TaskToken(string taskId)
    {
        lock (_gate)
        {
            if (!_taskTokens.TryGetValue(taskId, out var cts)) _taskTokens[taskId] = cts = new CancellationTokenSource();
            return cts.Token;
        }
    }

    private void CancelTask(string taskId)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            _taskTokens.Remove(taskId, out cts);
        }
        // Off this thread: callers may hold the store lock, and cancellation can run continuations inline.
        if (cts is not null) _ = Task.Run(cts.Cancel);
    }

    private void Track(Task task)
    {
        lock (_gate)
        {
            _background.RemoveAll(t => t.IsCompleted);
            _background.Add(task);
        }
    }

    internal async Task StopAllAsync()
    {
        string[] ids;
        Task[] running;
        lock (_gate)
        {
            ids = [.. _taskTokens.Keys];
            running = [.. _background];
        }
        foreach (var id in ids) CancelTask(id);
        await _sweep.CancelAsync().ConfigureAwait(false);
        try { await _sweepTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        try { await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (Exception ex) { _log($"agent shutdown: {ex.GetType().Name}"); }
    }

    // ---------------------------------------------------------------- tasks

    internal static JsonObject LoadTask(Db db, string taskId)
    {
        var body = db.Scalar("SELECT body FROM agent_tasks WHERE task_id = $t", ("$t", taskId)) as string ?? throw RpcError.NotFound("task", taskId);
        try { return Json.Parse(body) as JsonObject ?? throw RpcError.Corrupt("task", taskId); }
        catch (System.Text.Json.JsonException) { throw RpcError.Corrupt("task", taskId); }
    }

    private static void SaveTask(Db db, JsonObject task) =>
        db.Exec("UPDATE agent_tasks SET body = $b, status = $s, last_activity_at = $a WHERE task_id = $t",
            ("$b", Json.Write(task)), ("$s", task.Text("status")), ("$a", task.Text("lastActivityAt")), ("$t", task.Text("taskId")));

    private static Grant GrantOf(JsonObject task) => Grant.FromJson(task["grant"]!);
    private static JsonObject Used(JsonObject task) => task["used"]!.AsObject();
    private static long UsedValue(JsonObject task, string key) => Used(task)[key]!.GetValue<long>();
    private static void AddUsed(JsonObject task, string key, long by) => Used(task)[key] = UsedValue(task, key) + by;

    /// <summary>Moves an active task to a final status and announces it on its stream.</summary>
    private void SetStatus(Db db, JsonObject task, string status)
    {
        if (task.Text("status") == status) return;
        task["status"] = status;
        SaveTask(db, task);
        db.AppendEvent(Stream(task.Text("taskId")), "agent.task", new JsonObject { ["status"] = status, ["used"] = Used(task).DeepClone() });
        if (status == "active") return;
        RevokeRun(task.Text("taskId")); // synchronously: an answer finishing right now cannot be published after this
        CancelTask(task.Text("taskId"));
        db.Exec("DELETE FROM agent_transcripts WHERE task_id = $t", ("$t", task.Text("taskId"))); // a finished task's history goes with it
        Actions?.CancelTaskPlans(db, task.Text("taskId")); // a plan still waiting for the user dies with its task
        // A native client's pairing ends with its task: the bridge pairs again for the next task.
        if (task["pairing"]?["pairingId"]?.GetValue<string>() is { } pairingId && PairingRow(db, pairingId) is { Status: "approved" } pairing)
            SetPairing(db, pairing, "expired");
        RefreshProviders(db, task["recipient"].Text("kind")); // an MCP kind goes back to waiting/idle
    }

    /// <summary>Loads a task and applies lazy idle expiry.</summary>
    private JsonObject LoadLive(Db db, string taskId)
    {
        var task = LoadTask(db, taskId);
        if (task.Text("status") == "active")
        {
            var idle = TimeSpan.FromSeconds(GrantOf(task).IdleExpirySeconds);
            if (Now() - ParseTime(task.Text("lastActivityAt")) > idle) SetStatus(db, task, "expired");
        }
        return task;
    }

    private void Touch(JsonObject task) => task["lastActivityAt"] = Time.Format(Now());

    /// <summary>The tool error code for a task that is not active, or null.</summary>
    private static string? InactiveCode(JsonObject task) => task.Text("status") switch
    {
        "active" => null, "expired" => "expired", "exhausted" => "exhausted", _ => "revoked",
    };

    public JsonNode Start(JsonObject p)
    {
        var key = P.IdempotencyKey(p);
        var goal = P.Req(p, "goal", 500).Trim();
        if (goal.Length == 0) throw RpcError.Params("goal", "goal must not be empty.");
        var kind = P.OneOf(p, "recipientKind", RecipientKinds);
        var model = P.Str(p, "model", required: false, maxLength: 100);
        var pairingId = P.Str(p, "pairingId", required: false, maxLength: 64);
        if (p["scope"] is not JsonObject scope) throw RpcError.Params("scope", "scope must be an object.");
        var reportId = P.Req(scope, "reportId", 64);
        List<string>? findingIds = null;
        if (scope["findingIds"] is JsonArray fa)
            findingIds = fa.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw RpcError.Params("scope.findingIds", "findingIds must be strings.")).Distinct().ToList();
        else if (scope["findingIds"] is not null) throw RpcError.Params("scope.findingIds", "findingIds must be an array or null.");
        var grant = Grant.Parse(p["grant"]);

        var inApp = InAppKinds.Contains(kind);
        IProviderAdapter? adapter = null;
        if (inApp)
        {
            adapter = Adapter(kind) ?? throw RpcError.State("provider_unavailable", "That provider is not available in this build.");
            if (!HasCredential(kind)) throw RpcError.State("provider_not_configured", "Connect that provider first.");
            if (ToolsDisabled(kind) && (grant.Diagnostics.Count > 0 || grant.CanRequestChanges))
                throw RpcError.State("tools_unavailable", "Report review available: this account cannot use tools, so start without diagnostics.");
        }
        // M5 §3: an MCP kind without a pairing is an in-app (headless) task, allowed while its CLI is connected.
        var headless = !inApp && pairingId is null;
        if (headless)
        {
            if (_options.CliRunner is null || _options.McpExecutablePath is null) throw RpcError.State("provider_unavailable", "That provider is not available in this build.");
            if (!ClientConnected(kind)) throw RpcError.State("provider_not_connected", "Install and sign in to that client first.");
        }

        var (result, _) = _store.Write(db => Idempotency.Run(db, "agent.start", key, p, () =>
        {
            var report = ReportService.Load(db, reportId);
            if (findingIds is not null)
            {
                var known = report["findings"]!.AsArray().Select(f => f.Text("id")).ToHashSet();
                var unknown = findingIds.FirstOrDefault(id => !known.Contains(id));
                if (unknown is not null) throw RpcError.NotFound("finding", unknown);
            }

            JsonObject? pairing = null;
            string? connectionId = null;
            if (!inApp && !headless)
            {
                var row = PairingRow(db, pairingId!) ?? throw RpcError.NotFound("pairing", pairingId!);
                if (row.Status != "approved") throw RpcError.State("pairing_not_approved", "Approve the pairing first.");
                if (!PairingMatches(row.Client, kind)) throw RpcError.State("pairing_mismatch", "That pairing is for another client.");
                if (db.Scalar("SELECT task_id FROM agent_tasks WHERE status = 'active' AND connection_id = $c AND recipient_kind <> $k LIMIT 1",
                        ("$c", Binding(row.CompanionPid)), ("$k", kind)) is string) throw RpcError.State("pairing_in_use", "That pairing serves another task.");
                connectionId = Binding(row.CompanionPid);
                pairing = new JsonObject { ["pairingId"] = pairingId, ["client"] = row.Client, ["companionPid"] = row.CompanionPid };
            }

            // One active task per recipient kind: starting another stops the previous one.
            foreach (var previous in db.Query("SELECT task_id FROM agent_tasks WHERE status = 'active' AND recipient_kind = $k", r => r.GetString(0), ("$k", kind)))
                SetStatus(db, LoadTask(db, previous), "stopped");

            var taskId = "task-" + Ids.RandomHex();
            var now = Time.Format(Now());
            var task = new JsonObject
            {
                ["taskId"] = taskId, ["goal"] = goal,
                ["recipient"] = new JsonObject
                {
                    ["kind"] = kind, ["label"] = RecipientLabel(kind), ["model"] = inApp ? model ?? ModelFor(kind, adapter!) : null,
                },
                ["clientSession"] = null, // headless: the client's session (Claude Code) or thread (Codex) ID, for resuming
                ["scope"] = new JsonObject { ["reportId"] = reportId, ["findingIds"] = findingIds is null ? null : Json.Array(findingIds) },
                ["grant"] = grant.ToJson(),
                ["status"] = "active",
                ["used"] = new JsonObject { ["diagnosticCalls"] = 0, ["bytesShared"] = 0, ["turns"] = 0, ["inputTokens"] = 0, ["outputTokens"] = 0 },
                ["createdAt"] = now, ["lastActivityAt"] = now, ["pairing"] = pairing,
            };
            if (headless) task["recipient"]!["mode"] = "headless";
            else task.Remove("clientSession");
            db.Exec("INSERT INTO agent_tasks (task_id, recipient_kind, body, status, connection_id, created_at, last_activity_at) VALUES ($t, $k, $b, 'active', $c, $n, $n)",
                ("$t", taskId), ("$k", kind), ("$b", Json.Write(task)), ("$c", connectionId), ("$n", now));
            db.AppendEvent(Stream(taskId), "agent.task", new JsonObject { ["status"] = "active", ["used"] = task["used"]!.DeepClone() });
            lock (_gate) _mcpOutcome.Remove(kind);
            RefreshProviders(db, kind);
            return new JsonObject { ["taskId"] = taskId };
        }));
        return Get(new JsonObject { ["taskId"] = result.Text("taskId") });
    }

    public JsonNode Get(JsonObject p)
    {
        var taskId = P.Req(p, "taskId", 64);
        return _store.Write(db =>
        {
            var task = LoadLive(db, taskId);
            var activity = db.Query("SELECT seq, type, at, payload FROM events WHERE stream = $s ORDER BY seq DESC LIMIT 100",
                r => (JsonNode?)new JsonObject
                {
                    ["seq"] = r.GetInt64(0), ["type"] = r.GetString(1), ["at"] = r.GetString(2), ["payload"] = Json.Parse(r.GetString(3)),
                }, ("$s", Stream(taskId)));
            activity.Reverse();
            var view = (JsonObject)task.DeepClone();
            view["activity"] = new JsonArray(activity.ToArray());
            // This task's own diagnostic evidence, projected exactly as the model received it (aliases are task-stable).
            var released = db.Query("""
                SELECT e.body FROM agent_evidence e JOIN agent_released r ON r.task_id = e.task_id AND r.evidence_id = e.evidence_id
                WHERE e.task_id = $t ORDER BY e.rowid
                """, r => (JsonObject)Json.Parse(r.GetString(0))!, ("$t", taskId));
            // m6: benchmark evidence released to this task (get_benchmarks, or an ask's benchmark context) resolves too,
            // so the UI's citation chips find it.
            released.AddRange(db.Query("""
                SELECT b.body FROM bench_evidence b JOIN agent_released r ON r.evidence_id = b.evidence_id
                WHERE r.task_id = $t ORDER BY b.rowid
                """, r => (JsonObject)Json.Parse(r.GetString(0))!, ("$t", taskId)));
            view["evidence"] = Project(db, taskId, released, new ShareOptions(GrantOf(task).ShareNames), new ProjectionStats()).Items;
            return (JsonNode)view;
        });
    }

    /// <summary>agent.active: active tasks, newest first, so the UI can re-attach after a restart.</summary>
    public JsonNode Active() => _store.Write(db =>
    {
        var ids = db.Query("SELECT task_id FROM agent_tasks WHERE status = 'active' ORDER BY created_at DESC, rowid DESC", r => r.GetString(0));
        var tasks = ids.Select(id => LoadLive(db, id)).Where(t => t.Text("status") == "active").Select(t => (JsonNode?)t.DeepClone()).ToArray();
        return (JsonNode)new JsonObject { ["tasks"] = new JsonArray(tasks) };
    });

    public JsonNode Stop(JsonObject p)
    {
        var taskId = P.Req(p, "taskId", 64);
        _store.Write(db =>
        {
            var task = LoadLive(db, taskId);
            if (task.Text("status") == "active") SetStatus(db, task, "stopped");
        });
        CancelTask(taskId);
        return Get(p);
    }

    /// <summary>
    /// agent.cancel (m5-chat §8): cancels only the running in-app turn or headless CLI run (its process tree is killed);
    /// the run ends with agent.turn failed {code:"cancelled"} and no agent.message, and the task stays active.
    /// cancelled=false when the answer was already being published, or nothing ran and no turn is left open; a turn left
    /// open by a host restart is closed as interrupted (cancelled=true). The call waits up to
    /// <see cref="CancelWait"/> for the run to end, so an agent.ask right after it is not refused as busy.
    /// </summary>
    public async Task<JsonNode> CancelAsync(JsonObject p)
    {
        var taskId = P.Req(p, "taskId", 64);
        _store.Read(db => LoadTask(db, taskId)); // not_found for an unknown task
        AskRun? run;
        bool answering;
        lock (_gate)
        {
            answering = _asking.TryGetValue(taskId, out run) && run.Answering;
            if (run is not null && !answering) run.Cancelled = true;
        }
        if (answering) return new JsonObject { ["taskId"] = taskId, ["cancelled"] = false }; // the answer is being published
        if (run is null)
        {
            // No live run: a turn left unfinished (the host stopped mid-ask) is closed as interrupted, so the UI stops waiting.
            var closed = _store.Write(db => InterruptUnfinished(db, taskId));
            return new JsonObject { ["taskId"] = taskId, ["cancelled"] = closed };
        }
        // The run's token is cancelled and dropped; the next ask gets a fresh one.
        CancelTask(taskId);
        try { await run.Done.Task.WaitAsync(CancelWait).ConfigureAwait(false); }
        catch (TimeoutException) { _log($"task {taskId} cancel: the run is still ending"); }
        return new JsonObject { ["taskId"] = taskId, ["cancelled"] = true };
    }

    private static string RecipientLabel(string kind) => kind switch
    {
        "anthropic_api" => "Claude (API)", "openai_api" => "OpenAI (API)", "claude_code_mcp" => "Claude Code", "codex_mcp" => "Codex", _ => kind,
    };

    // ---------------------------------------------------------------- pairing (§6)

    public const int PairingMinutes = 5;
    public static readonly string[] PairClients = ["claude_code", "codex", "unknown"];

    private sealed record Pairing(string Id, string Client, string Status, string ConnectionId, long CompanionPid, string Code, string ExpiresAt);

    /// <summary>A native client's task is bound to its bridge process (verified PID), so it survives a pipe reconnect.</summary>
    internal static string Binding(long pid) => "pid:" + pid.ToString(CultureInfo.InvariantCulture);

    private static Pairing? PairingRow(Db db, string pairingId) =>
        db.Query("SELECT pairing_id, client, status, connection_id, companion_pid, code, expires_at FROM agent_pairings WHERE pairing_id = $p",
            r => new Pairing(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetString(5), r.GetString(6)),
            ("$p", pairingId)).FirstOrDefault();

    private static bool PairingMatches(string client, string kind) =>
        client == "unknown" || (client == "claude_code" && kind == "claude_code_mcp") || (client == "codex" && kind == "codex_mcp");

    private void SetPairing(Db db, Pairing pairing, string status)
    {
        db.Exec("UPDATE agent_pairings SET status = $s WHERE pairing_id = $p", ("$s", status), ("$p", pairing.Id));
        db.AppendEvent("user", "agent.pairing", new JsonObject
        {
            ["pairingId"] = pairing.Id, ["client"] = pairing.Client, ["code"] = pairing.Code, ["status"] = status,
        });
        RefreshProviders(db, KindsOf(pairing.Client));
    }

    private void SetOutcome(string client, string? outcome)
    {
        lock (_gate)
            foreach (var kind in KindsOf(client))
                if (outcome is null) _mcpOutcome.Remove(kind); else _mcpOutcome[kind] = outcome;
    }

    /// <summary>Lazy expiry of a pending pairing (5 minutes).</summary>
    private Pairing? LivePairing(Db db, string pairingId)
    {
        var row = PairingRow(db, pairingId);
        if (row is { Status: "pending" } && Now() >= ParseTime(row.ExpiresAt))
        {
            SetOutcome(row.Client, "pairing_expired");
            SetPairing(db, row, "expired");
            row = row with { Status = "expired" };
        }
        return row;
    }

    /// <summary>agent.pair (agent connections): a 4-digit code the user compares in the app. Bound to the verified client PID.</summary>
    public JsonNode Pair(JsonObject p, CallContext ctx)
    {
        var client = P.OneOf(p, "client", PairClients);
        P.Str(p, "clientVersion", required: false, maxLength: 64);
        if (P.Int(p, "companionPid", required: false) is { } claimed && claimed != ctx.ProcessId)
            throw RpcError.Params("companionPid", "companionPid must be the calling process.");
        // M5 §3: a bridge started by a headless run presents the run's token instead of asking the user.
        if (P.Str(p, "runToken", required: false, maxLength: 64) is { } runToken) return PairRun(runToken, ctx);
        return _store.Write(db =>
        {
            // One pending pairing per bridge process; a new request replaces it.
            foreach (var old in db.Query("SELECT pairing_id FROM agent_pairings WHERE companion_pid = $pid AND status = 'pending'", r => r.GetString(0), ("$pid", ctx.ProcessId)))
                SetPairing(db, PairingRow(db, old)!, "expired");
            var pairingId = "pair-" + Ids.RandomHex();
            var code = System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 10000).ToString("D4", CultureInfo.InvariantCulture);
            var expires = Time.Format(Now().AddMinutes(PairingMinutes));
            db.Exec("""
                INSERT INTO agent_pairings (pairing_id, client, client_version, companion_pid, code, status, connection_id, created_at, expires_at)
                VALUES ($p, $c, $v, $pid, $code, 'pending', $conn, $t, $e)
                """, ("$p", pairingId), ("$c", client), ("$v", p["clientVersion"]?.GetValue<string>()), ("$pid", ctx.ProcessId), ("$code", code),
                ("$conn", ctx.ConnectionId), ("$t", Time.Format(Now())), ("$e", expires));
            db.AppendEvent("user", "agent.pairing", new JsonObject { ["pairingId"] = pairingId, ["client"] = client, ["code"] = code, ["status"] = "pending" });
            SetOutcome(client, null);
            RefreshProviders(db, KindsOf(client));
            return (JsonNode)new JsonObject { ["pairingId"] = pairingId, ["code"] = code, ["expiresAt"] = expires };
        });
    }

    /// <summary>agent.pairStatus: only pairings of the calling bridge process (any of its connections).</summary>
    public JsonNode PairStatus(JsonObject p, CallContext ctx)
    {
        var pairingId = P.Req(p, "pairingId", 64);
        if (RunPairStatus(pairingId, ctx) is { } run) return run;
        return _store.Write(db =>
        {
            var row = LivePairing(db, pairingId);
            if (row is null || row.CompanionPid != ctx.ProcessId) throw RpcError.NotFound("pairing", pairingId);
            db.Exec("UPDATE agent_pairings SET connection_id = $c WHERE pairing_id = $p", ("$c", ctx.ConnectionId), ("$p", pairingId));
            var taskId = db.Scalar("SELECT task_id FROM agent_tasks WHERE connection_id = $b AND status = 'active' LIMIT 1", ("$b", Binding(row.CompanionPid))) as string;
            return (JsonNode)new JsonObject { ["pairingId"] = pairingId, ["status"] = row.Status, ["taskId"] = row.Status == "approved" ? taskId : null };
        });
    }

    /// <summary>agent.approvePairing (desktop UI only).</summary>
    public JsonNode ApprovePairing(JsonObject p)
    {
        var pairingId = P.Req(p, "pairingId", 64);
        if (p["approve"] is not JsonValue av || !av.TryGetValue<bool>(out var approve)) throw RpcError.Params("approve", "approve must be true or false.");
        _store.Write(db => { LivePairing(db, pairingId); }); // lazy expiry commits even though the approval below then fails
        return _store.Write(db =>
        {
            var row = LivePairing(db, pairingId) ?? throw RpcError.NotFound("pairing", pairingId);
            if (row.Status != "pending") throw RpcError.State(row.Status == "expired" ? "expired" : "not_pending", $"The pairing is {row.Status}.");
            var status = approve ? "approved" : "denied";
            SetOutcome(row.Client, approve ? null : "denied");
            // A task whose bridge exited (client_closed) continues on the newly approved bridge; its history stays.
            if (approve) RebindClosedTask(db, row);
            SetPairing(db, row, status);
            return (JsonNode)new JsonObject { ["pairingId"] = pairingId, ["status"] = status };
        });
    }

    private static void RebindClosedTask(Db db, Pairing row)
    {
        foreach (var kind in KindsOf(row.Client))
        {
            // Only user-window tasks (bound to a bridge); in-app headless tasks have no binding and are never taken over.
            var bound = db.Query("SELECT task_id, connection_id FROM agent_tasks WHERE status = 'active' AND recipient_kind = $k AND connection_id IS NOT NULL ORDER BY rowid DESC LIMIT 1",
                r => (Id: r.GetString(0), Binding: r.IsDBNull(1) ? "" : r.GetString(1)), ("$k", kind)).FirstOrDefault();
            if (bound.Id is null) continue;
            if (long.TryParse(bound.Binding.AsSpan(Math.Min(4, bound.Binding.Length)), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && ProcessAlive(pid)) continue;
            var task = LoadTask(db, bound.Id);
            task["pairing"] = new JsonObject { ["pairingId"] = row.Id, ["client"] = row.Client, ["companionPid"] = row.CompanionPid };
            SaveTask(db, task);
            db.Exec("UPDATE agent_tasks SET connection_id = $b WHERE task_id = $t", ("$b", Binding(row.CompanionPid)), ("$t", bound.Id));
            return;
        }
    }

    private static bool ProcessAlive(long pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
    }

    /// <summary>
    /// A closed agent connection ends its pairings only when the bridge process is gone; a live bridge may
    /// reconnect and continue (contracts/m3-agent.md §6). The task stays active (status client_closed, §9) until
    /// the user stops it, it idles out, or a newly approved bridge takes it over. If the process is still exiting
    /// when the pipe closes, the status sweep notices it moments later.
    /// </summary>
    public void ConnectionClosed(CallContext ctx)
    {
        if (ctx.ClientKind != "agent") return;
        if (ProcessAlive(ctx.ProcessId)) { RefreshProviders(McpKinds); return; }
        EndBridge(ctx.ProcessId);
    }

    private void EndBridge(long pid) => _store.Write(db =>
    {
        foreach (var id in db.Query("SELECT pairing_id FROM agent_pairings WHERE companion_pid = $p AND status IN ('pending','approved')", r => r.GetString(0), ("$p", pid)))
            SetPairing(db, PairingRow(db, id)!, "expired");
        RefreshProviders(db, McpKinds);
    });

    /// <summary>On startup: pairings and native-client tasks whose bridge process has exited end.</summary>
    internal void RecoverAfterRestart()
    {
        var pids = _store.Read(db => db.Query("SELECT DISTINCT companion_pid FROM agent_pairings WHERE status IN ('pending','approved')", r => r.GetInt64(0)));
        foreach (var pid in pids.Where(pid => !ProcessAlive(pid))) EndBridge(pid);
        var bound = _store.Read(db => db.Query("SELECT DISTINCT connection_id FROM agent_tasks WHERE status = 'active' AND connection_id LIKE 'pid:%'", r => r.GetString(0)));
        foreach (var b in bound)
            if (long.TryParse(b.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && !ProcessAlive(pid)) EndBridge(pid);
        // An ask cut off by the host stopping never ended its turn: close it as interrupted (m5-chat §8).
        foreach (var id in _store.Read(db => db.Query("SELECT task_id FROM agent_tasks WHERE status = 'active'", r => r.GetString(0))))
            _store.Write(db => InterruptUnfinished(db, id));
        StartStatusSweep();
    }

    // ---------------------------------------------------------------- aliases (§3.5)

    /// <summary>Task- (or review-) stable aliases persisted per scope; names are stored only as hashes.</summary>
    private sealed class DbAliasMap(Db db, string scopeId) : IAliasMap
    {
        public string Alias(string kind, string realName)
        {
            var hash = Ids.Hash16(kind, realName.Trim().ToLowerInvariant());
            if (db.Scalar("SELECT alias FROM agent_aliases WHERE scope_id = $s AND kind = $k AND name_hash = $h", ("$s", scopeId), ("$k", kind), ("$h", hash)) is string existing)
                return existing;
            var n = (long)db.Scalar("SELECT COUNT(*) FROM agent_aliases WHERE scope_id = $s AND kind = $k", ("$s", scopeId), ("$k", kind))!;
            var alias = $"{AliasMap.Noun(kind)} {Letters(n)}"; // the projection owner's canonical noun, so stored rows match what is shown
            db.Exec("INSERT INTO agent_aliases (scope_id, kind, name_hash, alias) VALUES ($s, $k, $h, $a)", ("$s", scopeId), ("$k", kind), ("$h", hash), ("$a", alias));
            return alias;
        }

        /// <summary>0 → A, 25 → Z, 26 → AA.</summary>
        private static string Letters(long n)
        {
            var s = "";
            do { s = (char)('A' + n % 26) + s; n = n / 26 - 1; } while (n >= 0);
            return s;
        }
    }
}

/// <summary>Memory-only key store (no Remember, or no DPAPI store in this build). Keys never leave this object.</summary>
public sealed class MemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _keys = [];
    public void Save(string kind, string apiKey, bool remember) { lock (_keys) _keys[kind] = apiKey; }
    public ProviderCredential? Load(string kind) { lock (_keys) return _keys.TryGetValue(kind, out var k) ? new ProviderCredential(kind, k) : null; }
    public void Delete(string kind) { lock (_keys) _keys.Remove(kind); }
    public bool Has(string kind) { lock (_keys) return _keys.ContainsKey(kind); }
}

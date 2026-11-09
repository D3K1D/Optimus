using System.Text.Json.Nodes;
using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Engine.Actions;

/// <summary>Host-provided action wiring (contracts/m4-actions.md). Handlers come from WindowsModules.Actions.</summary>
public sealed class ActionOptions
{
    public IReadOnlyList<IActionHandler> Handlers { get; init; } = [];

    /// <summary>firewall.enable_public stays disabled unless the host runs with --enable-firewall-handler.</summary>
    public bool EnableFirewallHandler { get; init; }

    public TimeSpan PlanLifetime { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan NonceLifetime { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan ResolveTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan SettingTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan ProviderTimeout { get; init; } = TimeSpan.FromHours(4);
}

/// <summary>Who asked for a plan: the user, or an agent task (in_app | claude_code | codex) and its tool call.</summary>
public sealed record RequestedBy(string Kind, string? TaskId = null, string? InvocationId = null)
{
    public static readonly RequestedBy User = new("user");
    public JsonObject ToJson() => new() { ["kind"] = Kind, ["taskId"] = TaskId, ["invocationId"] = InvocationId };
}

/// <summary>
/// The action pipeline (contracts/m4-actions.md §3–§5): catalog, plans with a hash over their canonical fields,
/// shell-only challenge/approve bound to plan, hash, nonce, user SID and session, a per-target lock with a live
/// re-resolve inside it, the journal (prepared → attempted → verified | failed | indeterminate) persisted with its
/// events, provider operations in the background with progress, restart reconcile, undo with a drift check, and
/// manual tasks re-checked by re-running the finding's rule. Only compiled handlers write; this class never does.
/// </summary>
public sealed partial class ActionService
{
    public const string ManualOpen = "manual.open";
    public const string FirewallActionId = "firewall.enable_public";
    public static readonly string[] PlanStatuses =
        ["awaiting_user", "approved", "running", "verified", "failed", "indeterminate", "declined", "expired", "cancelled", "ineligible"];

    private readonly Store _store;
    private readonly AuditCatalog _catalog;
    private readonly TimeProvider _time;
    private readonly ActionOptions _options;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, SemaphoreSlim> _locks = [];
    /// <summary>planId → the approve call that holds its target lock (a duplicate approve of that plan waits on it, briefly).</summary>
    private readonly Dictionary<string, Task> _approving = [];
    private readonly List<Task> _background = [];
    private readonly CancellationTokenSource _stopping = new();

    internal ActionService(Store store, AuditCatalog catalog, TimeProvider time, ActionOptions options, Action<string> log)
    {
        _store = store;
        _catalog = catalog;
        _time = time;
        _options = options;
        _log = log;
    }

    /// <summary>m6 §2: the per-user lease shared with scans, benchmarks and sensor snapshots.</summary>
    internal WorkLease Lease { get; init; } = new();

    internal bool HasActiveWork
    {
        get { lock (_gate) return _background.Any(t => !t.IsCompleted); }
    }

    private string Now() => Time.Format(_time.GetUtcNow());
    private DateTimeOffset Parse(string s) =>
        DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);

    internal IActionHandler? Handler(string actionId) => _options.Handlers.FirstOrDefault(h => h.ActionId == actionId);

    /// <summary>Why a registered handler is unavailable in this host, or null.</summary>
    private string? Disabled(string actionId) =>
        actionId == FirewallActionId && !_options.EnableFirewallHandler ? "disabled" : null;

    /// <summary>Catalog action IDs an agent may name in request_action (enabled handlers; manual.open is UI-only).</summary>
    internal IReadOnlyList<string> RequestableIds => _options.Handlers.Where(h => Disabled(h.ActionId) is null).Select(h => h.ActionId).ToList();

    // ---------------------------------------------------------------- catalog

    public JsonNode Catalog()
    {
        var actions = new JsonArray();
        foreach (var h in _options.Handlers)
        {
            var reason = Disabled(h.ActionId);
            var row = new JsonObject
            {
                ["actionId"] = h.ActionId, ["label"] = h.Label, ["privilege"] = h.Privilege, ["undo"] = h.SupportsUndo,
                ["kind"] = h.Kind, ["enabled"] = reason is null,
            };
            if (reason is not null) row["reason"] = reason;
            actions.Add(row);
        }
        actions.Add(new JsonObject
        {
            ["actionId"] = ManualOpen, ["label"] = "Open settings", ["privilege"] = "none", ["undo"] = false, ["kind"] = "manual", ["enabled"] = true,
        });
        return new JsonObject { ["actions"] = actions };
    }

    // ---------------------------------------------------------------- plans

    private static string TargetKey(string actionId, JsonObject target) =>
        target["type"]?.GetValue<string>() is { } type && target["id"]?.ToString() is { } id ? $"{type}:{id}" : actionId + ":" + Json.Digest(target);

    /// <summary>sha256 over the canonical plan fields an approval binds (m4 §1.2).</summary>
    internal static string Hash(JsonObject plan) => Json.Digest(new JsonObject
    {
        ["planId"] = plan["planId"]?.DeepClone(), ["actionId"] = plan["actionId"]?.DeepClone(), ["handlerVersion"] = plan["handlerVersion"]?.DeepClone(),
        ["target"] = plan["target"]?.DeepClone(), ["before"] = plan["before"]?.DeepClone(), ["after"] = plan["after"]?.DeepClone(),
        ["parameters"] = plan["parameters"]?.DeepClone(), ["privilege"] = plan["privilege"]?.DeepClone(),
        ["findingId"] = plan["findingId"]?.DeepClone(), ["expiresAt"] = plan["expiresAt"]?.DeepClone(),
    });

    private static bool Same(JsonNode? a, JsonNode? b) => Json.Write(Json.Canonical(a)) == Json.Write(Json.Canonical(b));

    internal static JsonObject LoadPlan(Db db, string planId)
    {
        var body = db.Scalar("SELECT body FROM action_plans WHERE plan_id = $p", ("$p", planId)) as string ?? throw RpcError.NotFound("plan", planId);
        return Json.Parse(body) as JsonObject ?? throw RpcError.Corrupt("plan", planId);
    }

    private void SavePlan(Db db, JsonObject plan, string status, JsonObject? ineligible = null)
    {
        plan["status"] = status;
        if (ineligible is not null) plan["ineligible"] = ineligible;
        db.Exec("UPDATE action_plans SET body = $b, status = $s WHERE plan_id = $p", ("$b", Json.Write(plan)), ("$s", status), ("$p", plan.Text("planId")));
        PlanEvent(db, plan);
    }

    private static void PlanEvent(Db db, JsonObject plan) =>
        db.AppendEvent("user", "action.plan", new JsonObject
        {
            ["planId"] = plan.Text("planId"), ["status"] = plan.Text("status"), ["requestedBy"] = plan["requestedBy"]?.DeepClone(),
        });

    /// <summary>Loads a plan, expiring it lazily when its 5 minutes pass while it waits for the user.</summary>
    private JsonObject LivePlan(Db db, string planId)
    {
        var plan = LoadPlan(db, planId);
        if (plan.Text("status") == "awaiting_user" && _time.GetUtcNow() >= Parse(plan.Text("expiresAt"))) SavePlan(db, plan, "expired");
        return plan;
    }

    /// <summary>Finds a finding by ID in the stored reports (with its report and current disposition).</summary>
    private static (string ReportId, JsonObject Report, JsonObject Finding)? FindFinding(Db db, string findingId)
    {
        foreach (var reportId in db.Query("SELECT report_id FROM reports WHERE body LIKE $p ORDER BY rowid DESC", r => r.GetString(0), ("$p", $"%\"{findingId}\"%")))
        {
            JsonObject report;
            try { report = ReportService.Load(db, reportId); }
            catch (RpcError) { continue; }
            if (report["findings"]!.AsArray().FirstOrDefault(f => f.Text("id") == findingId) is JsonObject finding) return (reportId, report, finding);
        }
        return null;
    }

    public async Task<JsonNode> PreviewAsync(JsonObject p)
    {
        var key = P.IdempotencyKey(p);
        var actionId = P.Req(p, "actionId", 64);
        var target = p["target"] is null ? null : p["target"] as JsonObject ?? throw RpcError.Params("target", "target must be an object.");
        var parameters = p["parameters"] is null ? null : p["parameters"] as JsonObject ?? throw RpcError.Params("parameters", "parameters must be an object.");
        var findingId = P.Str(p, "findingId", required: false, maxLength: 64);
        return await IdempotentAsync("action.preview", key, p,
            () => CreatePlanAsync(actionId, target, parameters, findingId, RequestedBy.User, undoOf: null)).ConfigureAwait(false);
    }

    /// <summary>Async idempotency: a recorded result replays; otherwise compute, then record (a concurrent winner wins).</summary>
    private async Task<JsonNode> IdempotentAsync(string method, string key, JsonObject p, Func<Task<JsonObject>> create)
    {
        var digest = Idempotency.Digest(p);
        JsonNode? Replay() => _store.Read(db =>
        {
            var row = db.Query("SELECT digest, result FROM idempotency WHERE method = $m AND key = $k",
                r => (Digest: r.GetString(0), Result: r.GetString(1)), ("$m", method), ("$k", key)).FirstOrDefault();
            if (row.Digest is null) return null;
            if (row.Digest != digest)
                throw new RpcError(ErrorCodes.IdempotencyMismatch, "This idempotencyKey was used with different parameters.", new JsonObject { ["idempotencyKey"] = key });
            var recorded = Json.Parse(row.Result)!;
            return recorded["planId"] is { } planId ? Get(new JsonObject { ["planId"] = planId.GetValue<string>() }) : recorded;
        });
        if (Replay() is { } earlier) return earlier;
        var plan = await create().ConfigureAwait(false);
        _store.Write(db => db.Exec("INSERT OR IGNORE INTO idempotency (method, key, digest, result, created_at) VALUES ($m, $k, $d, $r, $t)",
            ("$m", method), ("$k", key), ("$d", digest), ("$r", Json.Write(new JsonObject { ["planId"] = plan.Text("planId") })), ("$t", db.Now())));
        return Replay() ?? plan;
    }

    /// <summary>The -32013 reason when the requesting agent task is no longer active at insert time.</summary>
    internal const string TaskInactive = "task_inactive";

    /// <summary>Builds and stores a plan (awaiting_user or ineligible). The handler resolves the target from live state.</summary>
    internal async Task<JsonObject> CreatePlanAsync(string actionId, JsonObject? target, JsonObject? parameters, string? findingId, RequestedBy by, string? undoOf,
        ActionRequest? explicitRequest = null)
    {
        (string ReportId, JsonObject Report, JsonObject Finding)? found = findingId is null ? null : _store.Read(db => FindFinding(db, findingId)) ?? throw RpcError.NotFound("finding", findingId);
        if (actionId == ManualOpen) return CreateManual(found, findingId, by);

        var handler = Handler(actionId) ?? throw RpcError.Params("actionId", $"Unknown action {actionId}.");
        ActionResolution resolution;
        if (Disabled(actionId) is { } reason)
            resolution = ActionResolution.Ineligible(reason, "This change is not available in this version.", found?.Finding["settingsUri"]?.GetValue<string>());
        else
        {
            // Previewing from a card: the rule's chosen action parameters, unless the caller gave its own.
            if (parameters is null && target is null && found is { } f)
            {
                var (cardAction, cardParameters) = _store.Read(db => Followups.CardAction(db, f.ReportId, f.Finding.Text("id")));
                if (cardAction == actionId) parameters = cardParameters;
            }
            var request = explicitRequest ?? new ActionRequest(actionId, target, parameters, found?.Finding["subject"]?.GetValue<string>());
            resolution = await ResolveAsync(handler, request).ConfigureAwait(false);
        }
        return _store.Write(db => InsertPlan(db, handler, resolution, parameters, found, by, undoOf));
    }

    private async Task<ActionResolution> ResolveAsync(IActionHandler handler, ActionRequest request)
    {
        try
        {
            using var cts = new CancellationTokenSource(_options.ResolveTimeout);
            return await handler.ResolveAsync(request, cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return ActionResolution.Ineligible("timeout", "Windows did not answer in time."); }
        catch (Exception ex)
        {
            _log($"action {handler.ActionId}: resolve failed ({ex.GetType().Name})");
            return ActionResolution.Ineligible("unavailable", "The current state could not be read.");
        }
    }

    private JsonObject InsertPlan(Db db, IActionHandler handler, ActionResolution r, JsonObject? parameters,
        (string ReportId, JsonObject Report, JsonObject Finding)? found, RequestedBy by, string? undoOf)
    {
        // An agent's task may have been stopped while the target resolved (outside the lock). Checked in the inserting
        // transaction, so a stopped task never leaves a plan the user could approve (its stop cancelled the others).
        if (by.TaskId is { } taskId && db.Scalar("SELECT status FROM agent_tasks WHERE task_id = $t", ("$t", taskId)) as string is var status && status != "active")
            throw RpcError.State(TaskInactive, $"The task is {status ?? "gone"}.");
        var planId = "plan-" + Ids.RandomHex();
        var now = _time.GetUtcNow();
        var plan = new JsonObject
        {
            ["planId"] = planId, ["actionId"] = handler.ActionId, ["handlerVersion"] = handler.Version, ["label"] = handler.Label,
            ["kind"] = handler.Kind,
            ["findingId"] = found?.Finding.Text("id"), ["reportId"] = found?.ReportId,
            ["target"] = r.Target.DeepClone(), ["before"] = r.Before.DeepClone(), ["after"] = r.After.DeepClone(),
            ["parameters"] = parameters?.DeepClone(),
            ["rows"] = new JsonArray(r.Rows.Select(row => (JsonNode?)new JsonObject { ["label"] = row.Label, ["before"] = row.Before, ["after"] = row.After }).ToArray()),
            ["context"] = new JsonArray(r.Context.Select(kv => (JsonNode?)new JsonArray(kv.Key, kv.Value)).ToArray()),
            ["tradeoffs"] = Json.Array(r.Tradeoffs),
            ["privilege"] = handler.Privilege,
            ["undo"] = new JsonObject { ["supported"] = handler.SupportsUndo, ["conditions"] = r.UndoConditions },
            ["requestedBy"] = by.ToJson(),
            ["undoOf"] = undoOf,
            ["createdAt"] = Time.Format(now), ["expiresAt"] = Time.Format(now + _options.PlanLifetime),
            ["status"] = r.Eligible ? "awaiting_user" : "ineligible",
            ["ineligible"] = r.Eligible ? null : new JsonObject { ["code"] = r.IneligibleCode, ["message"] = r.IneligibleMessage, ["manualUri"] = r.ManualUri },
        };
        plan["planHash"] = Hash(plan);
        db.Exec("INSERT INTO action_plans (plan_id, body, status, task_id, target_key, created_at, expires_at) VALUES ($p, $b, $s, $t, $k, $c, $e)",
            ("$p", planId), ("$b", Json.Write(plan)), ("$s", plan.Text("status")), ("$t", by.TaskId), ("$k", TargetKey(handler.ActionId, r.Target)),
            ("$c", plan.Text("createdAt")), ("$e", plan.Text("expiresAt")));
        PlanEvent(db, plan);
        return plan;
    }

    public JsonNode Get(JsonObject p)
    {
        var planId = P.Req(p, "planId", 64);
        return _store.Write(db => (JsonNode)PlanView(db, planId));
    }

    /// <summary>A plan (lazily expired) with its operation, inside the caller's transaction.</summary>
    internal JsonObject PlanView(Db db, string planId)
    {
        var plan = (JsonObject)LivePlan(db, planId).DeepClone();
        plan["operation"] = OperationOfPlan(db, planId);
        return plan;
    }

    public JsonNode Decline(JsonObject p)
    {
        var planId = P.Req(p, "planId", 64);
        return _store.Write(db =>
        {
            var plan = LivePlan(db, planId);
            if (plan.Text("status") == "awaiting_user") SavePlan(db, plan, "declined", new JsonObject { ["code"] = "user_declined", ["message"] = "Declined." });
            return (JsonNode)plan;
        });
    }

    /// <summary>Agent task ended (stopped/expired/…): its plan still waiting for the user is cancelled. Running operations continue.</summary>
    internal void CancelTaskPlans(Db db, string taskId)
    {
        foreach (var planId in db.Query("SELECT plan_id FROM action_plans WHERE task_id = $t AND status = 'awaiting_user'", r => r.GetString(0), ("$t", taskId)))
            SavePlan(db, LoadPlan(db, planId), "cancelled", new JsonObject { ["code"] = "task_ended", ["message"] = "The task that asked for this change ended." });
    }

    /// <summary>The task's plan still waiting for the user, if any (at most one per task).</summary>
    internal string? PendingPlanOf(Db db, string taskId)
    {
        foreach (var planId in db.Query("SELECT plan_id FROM action_plans WHERE task_id = $t AND status = 'awaiting_user'", r => r.GetString(0), ("$t", taskId)))
            if (LivePlan(db, planId).Text("status") == "awaiting_user") return planId;
        return null;
    }

    // ---------------------------------------------------------------- challenge / approve (shell only)

    public JsonNode Challenge(JsonObject p, CallContext ctx)
    {
        var planId = P.Req(p, "planId", 64);
        return _store.Write(db =>
        {
            var plan = LivePlan(db, planId);
            if (plan.Text("status") != "awaiting_user") throw RpcError.State(plan.Text("status"), $"The plan is {plan.Text("status")}.");
            var nonce = Ids.RandomHex(16);
            var expires = Time.Format(_time.GetUtcNow() + _options.NonceLifetime);
            db.Exec("INSERT INTO action_nonces (nonce, plan_id, user_sid, session, expires_at) VALUES ($n, $p, $u, $s, $e)",
                ("$n", nonce), ("$p", planId), ("$u", ctx.UserSid ?? ""), ("$s", ctx.Session), ("$e", expires));
            return (JsonNode)new JsonObject { ["nonce"] = nonce, ["expiresAt"] = expires };
        });
    }

    private SemaphoreSlim Lock(string key)
    {
        lock (_gate)
        {
            if (!_locks.TryGetValue(key, out var sem)) _locks[key] = sem = new SemaphoreSlim(1, 1);
            return sem;
        }
    }

    /// <summary>
    /// action.approve: binds planId + planHash + single-use nonce + user SID + session. A plan runs at most once: a
    /// repeated approve returns the existing operation. Inside the target lock the handler re-resolves live state and
    /// the plan only runs if Before/After still match.
    /// </summary>
    public async Task<JsonNode> ApproveAsync(JsonObject p, CallContext ctx)
    {
        var planId = P.Req(p, "planId", 64);
        var planHash = P.Req(p, "planHash", 64);
        var nonce = P.Req(p, "nonce", 64);
        if (ctx.ClientKind is not ("desktop" or "test")) throw RpcError.State("not_allowed", "Only the app can approve a change.");

        var existing = _store.Read(db => OperationOfPlan(db, planId));
        if (existing is not null) return existing;
        var plan = _store.Read(db => LoadPlan(db, planId));
        if (plan.Text("actionId") == ManualOpen) throw RpcError.State("manual", "This is a manual step: open the settings, then check again.");
        var handler = Handler(plan.Text("actionId")) ?? throw RpcError.State("unavailable", "That action is not available in this build.");

        var sem = Lock(TargetKey(plan.Text("actionId"), plan["target"]!.AsObject()));
        // Never queue behind a change already running on this target (a Defender scan holds it for hours, and the app's
        // connection answers one request at a time): refuse at once. A repeated approve of a plan that ran replays.
        if (!sem.Wait(0))
        {
            Task? first;
            lock (_gate) _approving.TryGetValue(planId, out first);
            if (first is not null)
            {
                // The same plan is being approved right now (a racing duplicate): its call returns within the resolve and
                // write timeouts, so wait for it, then replay its operation (or try once more if it was refused).
                await first.ConfigureAwait(false);
                return await ApproveAsync(p, ctx).ConfigureAwait(false);
            }
            existing = _store.Read(db => OperationOfPlan(db, planId));
            if (existing is not null) return existing;
            throw new RpcError(ErrorCodes.InvalidState, "A change is being applied.", new JsonObject { ["reason"] = "action_active" });
        }
        var approving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _approving[planId] = approving.Task;
        var release = true;
        // m6 §2: executing a performance-affecting action takes the shared lease (approving alone does not). It is
        // taken before the nonce is consumed, so a busy refusal leaves the plan waiting for another approve.
        var leaseId = WorkLease.AffectsPerformance(plan.Text("actionId")) ? planId : null;
        var leaseKept = false;
        try
        {
            // Inside the lock: replay first, then the approval binding.
            existing = _store.Read(db => OperationOfPlan(db, planId));
            if (existing is not null) return existing;
            if (leaseId is not null) Lease.Acquire(WorkLease.Action, leaseId);
            _store.Write(db => Bind(db, planId, planHash, nonce, ctx));

            var request = new ActionRequest(plan.Text("actionId"), plan["target"]!.DeepClone().AsObject(), plan["parameters"]?.DeepClone() as JsonObject, null);
            var live = await ResolveAsync(handler, request).ConfigureAwait(false);
            // Settings compare Before and After; provider operations compare Target and After only (Defender moves its
            // own Before values — signature version, last scan — and that must not make an approval stale).
            var provider = handler.Kind == "provider";
            var matches = provider ? Same(live.Target, plan["target"]) && Same(live.After, plan["after"]) : Same(live.Before, plan["before"]) && Same(live.After, plan["after"]);
            if (!live.Eligible || !matches)
            {
                var current = _store.Write(db =>
                {
                    var stale = LoadPlan(db, planId);
                    SavePlan(db, stale, "ineligible", new JsonObject
                    {
                        ["code"] = "stale_plan", ["message"] = "The setting changed since the preview. Preview again.", ["manualUri"] = live.ManualUri,
                    });
                    return stale;
                });
                throw new RpcError(ErrorCodes.StaleRevision, "The plan no longer matches the PC.", new JsonObject { ["reason"] = "stale_plan", ["current"] = current });
            }

            var execution = new ActionExecution(planId, plan.Text("planHash"), plan["target"]!.DeepClone().AsObject(), plan["before"]!.DeepClone().AsObject(),
                plan["after"]!.DeepClone().AsObject(), plan["parameters"]?.DeepClone() as JsonObject);
            var operationId = _store.Write(db => Prepare(db, planId, live.Before));
            if (leaseId is not null)
            {
                Lease.Rename(WorkLease.Action, leaseId, operationId);
                leaseId = operationId;
            }

            if (handler.Kind == "provider")
            {
                release = false; // the background run holds the target lock until the provider operation ends
                leaseKept = true; // and the shared lease
                var held = leaseId;
                Track(Task.Run(async () =>
                {
                    try { await RunAsync(handler, execution, operationId, provider: true).ConfigureAwait(false); }
                    finally
                    {
                        if (held is not null) Lease.Release(WorkLease.Action, held);
                        sem.Release();
                    }
                }));
                return _store.Read(db => OperationView(db, operationId));
            }
            await RunAsync(handler, execution, operationId, provider: false).ConfigureAwait(false);
            return _store.Read(db => OperationView(db, operationId));
        }
        finally
        {
            if (leaseId is not null && !leaseKept) Lease.Release(WorkLease.Action, leaseId);
            lock (_gate) _approving.Remove(planId);
            if (release) sem.Release();
            approving.TrySetResult(); // after the release, so a waiting duplicate can take the lock itself

        }
    }

    /// <summary>Checks and consumes the approval binding. Hash or plan mismatches and nonce misuse are refused.</summary>
    private void Bind(Db db, string planId, string planHash, string nonce, CallContext ctx)
    {
        var plan = LivePlan(db, planId);
        if (plan.Text("status") == "expired") throw RpcError.State("expired", "The plan expired. Preview again.");
        if (plan.Text("status") != "awaiting_user") throw RpcError.State(plan.Text("status"), $"The plan is {plan.Text("status")}.");
        var row = db.Query("SELECT plan_id, user_sid, session, expires_at, used_at FROM action_nonces WHERE nonce = $n",
            r => (Plan: r.GetString(0), Sid: r.GetString(1), Session: r.GetInt64(2), Expires: r.GetString(3), Used: r.IsDBNull(4) ? null : r.GetString(4)),
            ("$n", nonce)).FirstOrDefault();
        if (row.Plan is null || row.Plan != planId || row.Used is not null || _time.GetUtcNow() >= Parse(row.Expires)
            || row.Sid != (ctx.UserSid ?? "") || row.Session != ctx.Session)
            throw RpcError.State("invalid_nonce", "The approval is not valid. Approve again.");
        db.Exec("UPDATE action_nonces SET used_at = $t WHERE nonce = $n", ("$t", db.Now()), ("$n", nonce));
        if (planHash != plan.Text("planHash"))
            throw new RpcError(ErrorCodes.StaleRevision, "The plan changed.", new JsonObject { ["reason"] = "stale_plan", ["current"] = plan.DeepClone() });
        SavePlan(db, plan, "approved");
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
        await _stopping.CancelAsync().ConfigureAwait(false);
        Task[] running;
        lock (_gate) running = [.. _background];
        try { await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (Exception ex) { _log($"actions shutdown: {ex.GetType().Name}"); }
    }
}

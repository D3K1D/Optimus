using System.Text.Json.Nodes;
using OptimizationTool.Transport.Logging;
using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Engine.Actions;

/// <summary>Host-provided action wiring (contracts/m4-actions.md). Handlers come from WindowsModules.Actions.</summary>
public sealed class ActionOptions
{
    public IReadOnlyList<IActionHandler> Handlers { get; init; } = [];

    /// <summary>firewall.enable_public stays disabled unless the host runs with --enable-firewall-handler.</summary>
    public bool EnableFirewallHandler { get; init; }

    /// <summary>
    /// m9 §3.2 (D6): every administrator action added in M9 stays off unless the host runs with --enable-admin-actions.
    /// Off: those catalog entries are disabled, a single admin request and a fix with an admin step are admin_unavailable.
    /// </summary>
    public bool EnableAdminActions { get; init; }

    /// <summary>The one-shot elevated broker (m9 §3.5). Null: no administrator step can run in this host.</summary>
    public IElevation? Elevation { get; init; }

    /// <summary>The longest wait for one broker session (UAC, the broker's own window and the steps).</summary>
    public TimeSpan ElevationTimeout { get; init; } = TimeSpan.FromMinutes(20);

    public TimeSpan PlanLifetime { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan NonceLifetime { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan ResolveTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan SettingTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan ProviderTimeout { get; init; } = TimeSpan.FromHours(4);

    /// <summary>m9 §5: approved scripts (runner, parser, policy, restore point). Default: no script path in this host.</summary>
    public Scripts.ScriptOptions Scripts { get; init; } = new();
}

/// <summary>Who asked for a plan: the user, or an agent task (in_app | claude_code) and its tool call.</summary>
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
        ["awaiting_user", "approved", "running", "verified", "failed", "indeterminate", "partial", "declined", "expired", "cancelled", "ineligible",
         "completed", "stopped", "timed_out", "not_run"]; // the last four: a script plan follows its operation's end state (m9 §5.7)

    private readonly Store _store;
    private readonly AuditCatalog _catalog;
    private readonly TimeProvider _time;
    private readonly ActionOptions _options;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    /// <summary>Resource locks (m9 §3.1): a plan holds its resource set from approve until its write (or provider run) ends.</summary>
    private readonly ResourceLocks _resources = new();
    /// <summary>planId → the approve call that holds its resource locks (a duplicate approve of that plan waits on it, briefly).</summary>
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
        // Compiled metadata is checked once: a handler outside the closed flag or privilege sets is a build defect.
        foreach (var h in options.Handlers)
            if (ActionFlags.Union(h.Flags) is null || h.Privilege is not ("none" or "admin"))
                throw new InvalidOperationException($"Action handler {h.ActionId} declares an unknown flag or privilege.");
    }

    /// <summary>m6 §2: the per-user lease shared with scans, benchmarks and sensor snapshots.</summary>
    internal WorkLease Lease { get; init; } = new();

    /// <summary>Design v59: settingsPath beside every manualUri and the manual.open target (English Windows only).</summary>
    internal SettingsPaths Paths { get; init; } = SettingsPaths.None;

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
        actionId == FirewallActionId ? (_options.EnableFirewallHandler ? null : "disabled")
        : Handler(actionId) is { Privilege: "admin" } && !_options.EnableAdminActions ? AdminUnavailable : null;

    /// <summary>
    /// A single plan may run this administrator action here: the firewall keeps its own flag (m4 §2); every other
    /// administrator action needs --enable-admin-actions (m9 D6). Both need the broker.
    /// </summary>
    private bool SingleAdminAllowed(string actionId) =>
        _options.Elevation is not null && (actionId == FirewallActionId ? _options.EnableFirewallHandler : _options.EnableAdminActions);

    /// <summary>A fix may carry this administrator step: --enable-admin-actions, plus the firewall's own flag for the firewall.</summary>
    private bool FixAdminAllowed(string actionId) =>
        _options.Elevation is not null && _options.EnableAdminActions && (actionId != FirewallActionId || _options.EnableFirewallHandler);

    private static ActionResolution AdminUnavailableResolution(string? manualUri = null) =>
        ActionResolution.Ineligible(AdminUnavailable, "Administrator changes aren't available in this version.", manualUri);

    /// <summary>Catalog action IDs an agent may name in request_action (enabled handlers; manual.open is UI-only).</summary>
    internal IReadOnlyList<string> RequestableIds => _options.Handlers.Where(h => Disabled(h.ActionId) is null).Select(h => h.ActionId).ToList();

    /// <summary>
    /// Every registered handler's metadata, then manual.open (UI-only), for the app guide (contracts/m9-ai-powers.md §2).
    /// Enabled says whether this host runs it; Requestable whether an agent may name it in request_action.
    /// </summary>
    internal IReadOnlyList<Agent.GuideAction> GuideActions() => GuideActionsOf(_options.Handlers, id => Disabled(id) is null);

    /// <summary>The guide's view of a handler list (tests pass the compiled Windows handlers to pin the guide version).</summary>
    internal static IReadOnlyList<Agent.GuideAction> GuideActionsOf(IEnumerable<IActionHandler> handlers, Func<string, bool> enabled) =>
    [
        .. handlers.Select(h => new Agent.GuideAction(h.ActionId, h.Version, h.Label, h.What, h.Group, h.Privilege, h.SupportsUndo, h.Kind,
            ActionFlags.Union(h.Flags) ?? [], h.ParameterSchema, Enabled: enabled(h.ActionId), Requestable: true, TargetKind: h.TargetKind)),
        new Agent.GuideAction(ManualOpen, ManualHandler.HandlerVersion, "Open settings", ManualHandler.WhatText, ManualHandler.GroupName, "none", false, "manual", [], null,
            Enabled: true, Requestable: false),
    ];

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
                ["kind"] = h.Kind, ["group"] = h.Group, ["what"] = h.What, ["flags"] = Json.Array(h.Flags), ["enabled"] = reason is null,
                ["targetKind"] = h.TargetKind, // what a targetRef must name (e.g. "startup entry"); null when the action takes no target
            };
            if (reason is not null) row["reason"] = reason;
            actions.Add(row);
        }
        actions.Add(new JsonObject
        {
            ["actionId"] = ManualOpen, ["label"] = "Open settings", ["privilege"] = "none", ["undo"] = false, ["kind"] = "manual",
            ["group"] = ManualHandler.GroupName, ["what"] = ManualHandler.WhatText, ["flags"] = new JsonArray(), ["enabled"] = true,
            ["targetKind"] = null,
        });
        return new JsonObject { ["actions"] = actions };
    }

    // ---------------------------------------------------------------- plans

    private static string TargetKey(string actionId, JsonObject target) =>
        target["type"]?.GetValue<string>() is { } type && target["id"]?.ToString() is { } id ? $"{type}:{id}" : actionId + ":" + Json.Digest(target);

    /// <summary>sha256 over the canonical plan fields an approval binds (m4 §1.2; m9 §4 adds what, flags and resources).</summary>
    internal static string Hash(JsonObject plan) => Json.Digest(new JsonObject
    {
        ["planId"] = plan["planId"]?.DeepClone(), ["actionId"] = plan["actionId"]?.DeepClone(), ["handlerVersion"] = plan["handlerVersion"]?.DeepClone(),
        ["target"] = plan["target"]?.DeepClone(), ["before"] = plan["before"]?.DeepClone(), ["after"] = plan["after"]?.DeepClone(),
        ["parameters"] = plan["parameters"]?.DeepClone(), ["privilege"] = plan["privilege"]?.DeepClone(),
        ["findingId"] = plan["findingId"]?.DeepClone(), ["expiresAt"] = plan["expiresAt"]?.DeepClone(),
        ["what"] = plan["what"]?.DeepClone(), ["flags"] = plan["flags"]?.DeepClone(), ["resources"] = plan["resources"]?.DeepClone(),
        ["kind"] = plan["kind"]?.DeepClone(), ["why"] = plan["why"]?.DeepClone(),
    });

    /// <summary>The privilege a resolution runs with: its per-target override, else the handler default.</summary>
    private static string PrivilegeOf(IActionHandler h, ActionResolution r) => r.Privilege ?? h.Privilege;

    /// <summary>The handler's flags plus the resolution's (a resolution never removes one), canonical order.</summary>
    private static IReadOnlyList<string> FlagsOf(IActionHandler h, ActionResolution r) => ActionFlags.Union(h.Flags, r.Flags) ?? [];

    /// <summary>
    /// A plan's or step's undo (m4 §3; design v62): supported when the handler can undo and the resolution keeps it, the
    /// condition sentence, and <c>nothingToUndo: true</c> only for an eligible resolution of a <see cref="IActionHandler.NothingToUndo"/>
    /// handler that isn't undoable and isn't flagged irreversible or data_loss (omitted otherwise).
    /// </summary>
    internal static JsonObject UndoOf(IActionHandler h, ActionResolution r)
    {
        var supported = h.SupportsUndo && r.UndoSupported != false;
        var undo = new JsonObject { ["supported"] = supported, ["conditions"] = r.UndoConditions };
        if (r.Eligible && h.NothingToUndo && !supported && !FlagsOf(h, r).Any(f => f is ActionFlags.Irreversible or ActionFlags.DataLoss))
            undo["nothingToUndo"] = true;
        return undo;
    }

    /// <summary>The lock set: the resolution's canonical resources, or the legacy per-target key when it names none.</summary>
    private static IReadOnlyList<string> ResourcesOf(IActionHandler h, ActionResolution r) =>
        !r.Eligible ? [] : r.Resources is { Count: > 0 } keys ? ResourceKeys.Normalize(keys) : [TargetKey(h.ActionId, r.Target)];

    /// <summary>A stored plan's lock set (plans stored before m9 have none: their legacy target key).</summary>
    private static IReadOnlyList<string> PlanResources(JsonObject plan) =>
        IsFix(plan) ? FixResources(plan)
        : plan["resources"] is JsonArray { Count: > 0 } keys
            ? keys.Select(k => k!.GetValue<string>()).ToList()
            : [TargetKey(plan.Text("actionId"), plan["target"]!.AsObject())];

    private static bool Same(JsonNode? a, JsonNode? b) => Json.Write(Json.Canonical(a)) == Json.Write(Json.Canonical(b));

    internal static JsonObject LoadPlan(Db db, string planId)
    {
        var body = db.Scalar("SELECT body FROM action_plans WHERE plan_id = $p", ("$p", planId)) as string ?? throw RpcError.NotFound("plan", planId);
        return Json.Parse(body) as JsonObject ?? throw RpcError.Corrupt("plan", planId);
    }

    private void SavePlan(Db db, JsonObject plan, string status, JsonObject? ineligible = null)
    {
        plan["status"] = status;
        if (ineligible is not null) plan["ineligible"] = Paths.Add(ineligible, ineligible["manualUri"]?.GetValue<string>());
        db.Exec("UPDATE action_plans SET body = $b, status = $s WHERE plan_id = $p", ("$b", Json.Write(plan)), ("$s", status), ("$p", plan.Text("planId")));
        PlanEvent(db, plan);
    }

    private static void PlanEvent(Db db, JsonObject plan)
    {
        db.AppendEvent("user", "action.plan", new JsonObject
        {
            ["planId"] = plan.Text("planId"), ["status"] = plan.Text("status"), ["requestedBy"] = plan["requestedBy"]?.DeepClone(),
        });
        // Every plan status change (proposed = awaiting_user, approved, declined, expired, running, …): ids and handler only.
        if (AppLog.Current.DevEnabled)
        {
            var kind = plan["kind"]?.GetValue<string>();
            var area = kind == FixKind ? "fix" : kind == ScriptKind ? "script" : "action";
            var handler = plan["actionId"]?.GetValue<string>() is { } a ? " " + a : "";
            AppLog.Current.Debug($"{area}: plan {plan.Text("planId")}{handler} {plan.Text("status")}");
        }
        FixAttempts.PlanChanged(db, plan); // m9 §17.3: a card-bound plan moved: its card.fix is announced
    }

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
        var reportId = P.Str(p, "reportId", required: false, maxLength: 64);
        var evidenceId = TargetRefId(p["targetRef"], "targetRef");
        if (evidenceId is not null && target is not null) throw RpcError.Params("targetRef", "Send target or targetRef, not both.");
        return await IdempotentAsync("action.preview", key, p, () =>
        {
            // m9 §3.1: the desktop's targetRef must belong to the finding (cited by it) or the report it names.
            var targetRef = evidenceId is null ? null : new TargetRef(evidenceId, _store.Read(db => DesktopEvidence(db, evidenceId, findingId, reportId)));
            return CreatePlanAsync(actionId, target, parameters, findingId, RequestedBy.User, undoOf: null, targetRef: targetRef);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Async idempotency with the key reserved before any work (m9 §1.3): a recorded result replays; a concurrent
    /// duplicate waits for the first call and then replays its result (or, when that call failed and stored nothing,
    /// tries itself). The key's result is written in the same transaction as the plan (<see cref="InsertPlan"/>), so a
    /// failed call leaves neither a plan nor a key, and no duplicate can store a second plan.
    /// </summary>
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
        while (true)
        {
            if (Replay() is { } earlier) return earlier;
            var mine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? first;
            lock (_gate)
                if (!_reserved.TryGetValue((method, key), out first)) _reserved[(method, key)] = mine.Task;
            if (first is not null)
            {
                await first.ConfigureAwait(false);
                continue;
            }
            try
            {
                if (Replay() is { } raced) return raced;
                var record = new PendingKey(method, key, digest);
                PendingRecord.Value = record;
                JsonObject plan;
                try { plan = await create().ConfigureAwait(false); }
                finally { PendingRecord.Value = null; }
                if (!record.Written) _store.Write(db => WriteKey(db, record, plan.Text("planId")));
                return Replay() ?? plan;
            }
            finally
            {
                lock (_gate) _reserved.Remove((method, key));
                mine.TrySetResult();
            }
        }
    }

    /// <summary>An idempotency key reserved by <see cref="IdempotentAsync"/>, written with the first plan its call stores.</summary>
    private sealed class PendingKey(string method, string key, string digest)
    {
        public string Method { get; } = method;
        public string Key { get; } = key;
        public string Digest { get; } = digest;
        public bool Written { get; set; }
    }

    /// <summary>The reserved key of the call in progress on this logical flow (flows into its InsertPlan).</summary>
    private static readonly AsyncLocal<PendingKey?> PendingRecord = new();

    /// <summary>(method, key) → the call holding the reservation.</summary>
    private readonly Dictionary<(string Method, string Key), Task> _reserved = [];

    private static void WriteKey(Db db, PendingKey record, string planId)
    {
        db.Exec("INSERT INTO idempotency (method, key, digest, result, created_at) VALUES ($m, $k, $d, $r, $t)",
            ("$m", record.Method), ("$k", record.Key), ("$d", record.Digest), ("$r", Json.Write(new JsonObject { ["planId"] = planId })), ("$t", db.Now()));
        record.Written = true;
    }

    /// <summary>The -32013 reason when the requesting agent task is no longer active at insert time.</summary>
    internal const string TaskInactive = "task_inactive";

    /// <summary>The -32013 reason when the requesting task no longer holds changes, or already has a plan waiting (m9 §1.3).</summary>
    internal const string PlanRefused = "plan_refused";

    /// <summary>
    /// In the caller's transaction: the requesting task is active, its grant holds changes at <paramref name="level"/> or
    /// more, and the slot rule (m9 §17.3) lets a plan on <paramref name="cardId"/> wait, not counting <paramref name="otherThan"/>.
    /// Throws TaskInactive or PlanRefused. Returns the task's parsed grant (the administrator check, m9 §17.1, reads it in the
    /// same transaction).
    /// </summary>
    private Grant CheckRequester(Db db, string taskId, string? cardId, string? otherThan, string level = "ask")
    {
        var row = db.Query("SELECT status, body FROM agent_tasks WHERE task_id = $t", r => (Status: r.GetString(0), Body: r.GetString(1)), ("$t", taskId)).FirstOrDefault();
        if (row.Status != "active") throw RpcError.State(TaskInactive, $"The task is {row.Status ?? "gone"}.");
        var grant = Json.Parse(row.Body)?["grant"] is { } g ? Grant.FromJson(g) : null;
        if (grant is null || Permissions.Level(grant.Changes) < Permissions.Level(level))
            throw RpcError.State(PlanRefused, "This task may not ask for changes.");
        CheckSlots(db, taskId, cardId, otherThan);
        return grant;
    }

    // ---------------------------------------------------------------- administrator rights (m9 §17.1)

    /// <summary>The step or request code when the requesting task's grant has administrator rights switched off.</summary>
    public const string AdminNotAllowed = "admin_not_allowed";
    public const string AdminNotAllowedMessage = "Administrator rights are switched off under Permissions.";
    private const string AdminUnavailableMessage = "Administrator changes aren't available in this version.";

    /// <summary>What ai.getPermissions reports as available.admin.</summary>
    /// <remarks>
    /// m9 §17.1 review decision: an administrator change can be approved here when either flag enables one (--enable-admin-actions,
    /// or --enable-firewall-handler for the firewall), so the permission is never shown unavailable while such a plan is approvable.
    /// Administrator checks (§17.2) follow --enable-admin-actions only: <see cref="AdminChecksEnabled"/>.
    /// </remarks>
    internal bool AdminActionsEnabled => _options.EnableAdminActions || _options.EnableFirewallHandler;

    /// <summary>m9 §17.2: administrator checks follow --enable-admin-actions only (the firewall flag never enables them).</summary>
    internal bool AdminChecksEnabled => _options.EnableAdminActions;

    /// <summary>
    /// m9 §17.1, the one rule for every path: an AI-requested plan (or step) whose resolved privilege is administrator needs
    /// the host flag (<paramref name="flagOn"/>) and the requesting task's <see cref="Grant.Admin"/>. Null when allowed;
    /// otherwise the code and message, with the flag winning (admin_unavailable before admin_not_allowed).
    /// </summary>
    internal static (string Code, string Message)? AdminRefusal(Grant grant, bool flagOn) =>
        !flagOn ? (AdminUnavailable, AdminUnavailableMessage)
        : !grant.Admin ? (AdminNotAllowed, AdminNotAllowedMessage)
        : null;

    /// <summary>
    /// The approve recheck for an AI-requested plan (m9 §17.1): its stored privilege, never what the AI said, against the
    /// flag and the grant. Throws -32013 with the refusal's code as reason; the caller cancels the plan (not_allowed).
    /// </summary>
    private static void CheckAdmin(JsonObject plan, Grant grant, bool flagOn)
    {
        if (plan.Text("privilege") == "admin" && AdminRefusal(grant, flagOn) is { } refusal) throw RpcError.State(refusal.Code, refusal.Message);
    }

    /// <summary>A stored plan's flag check at approve: single (its action's flag), fix (every administrator step's), script (elevated scripts).</summary>
    private bool AdminFlagFor(JsonObject plan) =>
        IsScript(plan) ? ElevatedScriptsAvailable
        : IsFix(plan) ? StepsOf(plan).Where(s => s.Text("privilege") == "admin").All(s => FixAdminAllowed(s.Text("actionId")))
        : SingleAdminAllowed(plan.Text("actionId"));

    /// <summary>
    /// A plan that lost the approve recheck is cancelled (it never runs): a task that ended or narrowed → task_ended; the
    /// administrator rule (m9 §17.1, e.g. a plan stored before the upgrade) → not_allowed with the rule's message.
    /// </summary>
    private void CancelLost(string planId, RpcError e, string endedMessage) => _store.Write(db =>
    {
        var lost = LoadPlan(db, planId);
        if (lost.Text("status") != "awaiting_user") return;
        var reason = (e.ErrorData as JsonObject)?["reason"]?.GetValue<string>();
        SavePlan(db, lost, "cancelled", reason is AdminNotAllowed or AdminUnavailable
            ? new JsonObject { ["code"] = "not_allowed", ["message"] = e.Message }
            : new JsonObject { ["code"] = "task_ended", ["message"] = endedMessage });
    });

    /// <summary>The -32013 reasons that make an approve cancel the plan instead of failing it.</summary>
    private static bool LostApprove(RpcError e) =>
        (e.ErrorData as JsonObject)?["reason"]?.GetValue<string>() is TaskInactive or PlanRefused or AdminNotAllowed or AdminUnavailable;

    /// <summary>Builds and stores a plan (awaiting_user or ineligible). The handler resolves the target from live state.</summary>
    internal async Task<JsonObject> CreatePlanAsync(string actionId, JsonObject? target, JsonObject? parameters, string? findingId, RequestedBy by, string? undoOf,
        ActionRequest? explicitRequest = null, TargetRef? targetRef = null, JsonObject? why = null, CardBinding? card = null)
    {
        (string ReportId, JsonObject Report, JsonObject Finding)? found = findingId is null ? null : _store.Read(db => FindFinding(db, findingId)) ?? throw RpcError.NotFound("finding", findingId);
        if (actionId == ManualOpen) return CreateManual(found, findingId, by);

        var handler = Handler(actionId) ?? throw RpcError.Params("actionId", $"Unknown action {actionId}.");
        ActionResolution resolution;
        if (Disabled(actionId) is { } reason)
            resolution = ActionResolution.Ineligible(reason, "This change is not available in this version.", found?.Finding["settingsUri"]?.GetValue<string>());
        else if (targetRef is { Evidence: null })
            // m9 §3.1: unknown, unreleased, out-of-scope or ambiguous evidence. No handler sees the request.
            resolution = ActionResolution.Ineligible(UnknownTarget, "Optimus can't match that target to evidence from this report or task.");
        else
        {
            // Previewing from a card: the rule's chosen action parameters, unless the caller gave its own.
            if (parameters is null && target is null && targetRef is null && found is { } f)
            {
                var (cardAction, cardParameters) = _store.Read(db => Followups.CardAction(db, f.ReportId, f.Finding.Text("id")));
                if (cardAction == actionId) parameters = cardParameters;
            }
            var request = explicitRequest ?? new ActionRequest(actionId, targetRef is null ? target : null, parameters, found?.Finding["subject"]?.GetValue<string>())
            {
                Evidence = targetRef?.Evidence,
            };
            resolution = await ResolveAsync(handler, request).ConfigureAwait(false);
            // m9 D6: an administrator target (a machine setting, an all-users startup entry, …) needs the host flag.
            if (resolution.Eligible && PrivilegeOf(handler, resolution) == "admin" && !SingleAdminAllowed(actionId))
                resolution = AdminUnavailableResolution(resolution.ManualUri);
        }
        return _store.Write(db => InsertPlan(db, handler, resolution, parameters, found, by, undoOf, targetRef?.EvidenceId, why, card));
    }

    private async Task<ActionResolution> ResolveAsync(IActionHandler handler, ActionRequest request)
    {
        try
        {
            using var cts = new CancellationTokenSource(_options.ResolveTimeout);
            var r = await handler.ResolveAsync(request, cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
            // Metadata from a resolution is checked like the handler's: a closed flag set, a known privilege, canonical keys.
            if (ActionFlags.Union(r.Flags) is null || r.Privilege is not (null or "none" or "admin") || r.Resources?.Any(k => !ResourceKeys.Valid(k)) == true)
            {
                AppLog.Current.Warn($"action: {handler.ActionId}: resolution has invalid flags, privilege or resources", ErrorSites.ActionResolveFailed);
                return ActionResolution.Ineligible("unavailable", "This change is not available right now.");
            }
            return r;
        }
        catch (OperationCanceledException) { return ActionResolution.Ineligible("timeout", "Windows did not answer in time."); }
        catch (Exception ex)
        {
            AppLog.Current.Warn($"action: {handler.ActionId}: resolve failed ({ex.GetType().Name})", ErrorSites.ActionResolveFailed);
            return ActionResolution.Ineligible("unavailable", "The current state could not be read.");
        }
    }

    private JsonObject InsertPlan(Db db, IActionHandler handler, ActionResolution r, JsonObject? parameters,
        (string ReportId, JsonObject Report, JsonObject Finding)? found, RequestedBy by, string? undoOf, string? targetRefId = null, JsonObject? why = null, CardBinding? card = null)
    {
        // An agent's task may have been stopped, or the user's permissions narrowed, while the target resolved (outside
        // the lock). Checked in the inserting transaction (m9 §1.3): the task is still active, still holds changes, and has
        // no other plan waiting for the user, so a plan that loses any of these races is never stored, never approvable.
        if (by.TaskId is { } taskId)
        {
            var grant = CheckRequester(db, taskId, card?.CardId, otherThan: null);
            // m9 §17.1: an AI's administrator change needs administrator rights in its grant (the flag was checked when it
            // resolved: admin_unavailable wins). By the resolved privilege, never by what the AI said.
            if (r.Eligible && PrivilegeOf(handler, r) == "admin" && AdminRefusal(grant, flagOn: true) is { } refusal)
                r = ActionResolution.Ineligible(refusal.Code, refusal.Message, r.ManualUri);
        }
        var planId = "plan-" + Ids.RandomHex();
        var now = _time.GetUtcNow();
        var plan = new JsonObject
        {
            ["planId"] = planId, ["actionId"] = handler.ActionId, ["handlerVersion"] = handler.Version, ["label"] = handler.Label,
            ["kind"] = handler.Kind, ["group"] = handler.Group, ["what"] = handler.What,
            // m9 §4.1: single plans carry the fix fields too (title and steps null; why from request_action).
            ["title"] = null, ["why"] = why?.DeepClone(), ["steps"] = null,
            ["findingId"] = found?.Finding.Text("id"), ["reportId"] = found?.ReportId,
            ["target"] = r.Target.DeepClone(), ["before"] = r.Before.DeepClone(), ["after"] = r.After.DeepClone(),
            ["parameters"] = parameters?.DeepClone(),
            ["rows"] = new JsonArray(r.Rows.Select(row => (JsonNode?)new JsonObject { ["label"] = row.Label, ["before"] = row.Before, ["after"] = row.After }).ToArray()),
            ["context"] = new JsonArray(r.Context.Select(kv => (JsonNode?)new JsonArray(kv.Key, kv.Value)).ToArray()),
            ["tradeoffs"] = Json.Array(r.Tradeoffs),
            ["privilege"] = PrivilegeOf(handler, r),
            ["flags"] = Json.Array(FlagsOf(handler, r)),
            ["resources"] = Json.Array(ResourcesOf(handler, r)),
            ["undo"] = UndoOf(handler, r),
            ["requestedBy"] = by.ToJson(),
            // m9 §3.1: the evidence the caller named (the handler resolved Target from its local fields).
            ["targetRef"] = targetRefId is null ? null : new JsonObject { ["evidenceId"] = targetRefId },
            ["undoOf"] = undoOf,
            ["createdAt"] = Time.Format(now), ["expiresAt"] = Time.Format(now + _options.PlanLifetime),
            ["status"] = r.Eligible ? "awaiting_user" : "ineligible",
            ["ineligible"] = r.Eligible ? null : Paths.Add(new JsonObject { ["code"] = r.IneligibleCode, ["message"] = r.IneligibleMessage, ["manualUri"] = r.ManualUri }, r.ManualUri),
        };
        BindCard(db, plan, card, by); // m9 §17.3: plan.cardId, plan.attemptId (the attempt may start here)
        plan["planHash"] = Hash(plan);
        db.Exec("INSERT INTO action_plans (plan_id, body, status, task_id, target_key, created_at, expires_at, card_id) VALUES ($p, $b, $s, $t, $k, $c, $e, $card)",
            ("$p", planId), ("$b", Json.Write(plan)), ("$s", plan.Text("status")), ("$t", by.TaskId), ("$k", TargetKey(handler.ActionId, r.Target)),
            ("$c", plan.Text("createdAt")), ("$e", plan.Text("expiresAt")), ("$card", plan["cardId"]?.GetValue<string>()));
        PlanEvent(db, plan);
        // action.preview / action.undo: the reserved idempotency key commits with the plan (m9 §1.3).
        if (PendingRecord.Value is { Written: false } pending) WriteKey(db, pending, planId);
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

    // ---------------------------------------------------------------- challenge / approve (shell only)

    public JsonNode Challenge(JsonObject p, CallContext ctx)
    {
        var planId = P.Req(p, "planId", 64);
        if (ctx.ClientKind is not ("desktop" or "test")) throw RpcError.State("not_allowed", "Only the app can approve a change.");
        var options = ScriptOptionsParam(p); // m9 §5.2: a script's dialog choices are bound into the nonce
        return _store.Write(db =>
        {
            var plan = LivePlan(db, planId);
            if (plan.Text("status") != "awaiting_user") throw RpcError.State(plan.Text("status"), $"The plan is {plan.Text("status")}.");
            var script = IsScript(plan);
            if (script && options is null) throw RpcError.Params("options", "A script's approval carries options {restorePoint, shareOutput}.");
            if (!script && options is not null) throw RpcError.Params("options", "options applies to a script plan only.");
            var nonce = Ids.RandomHex(16);
            var expires = Time.Format(_time.GetUtcNow() + _options.NonceLifetime);
            db.Exec("INSERT INTO action_nonces (nonce, plan_id, user_sid, session, expires_at) VALUES ($n, $p, $u, $s, $e)",
                ("$n", nonce), ("$p", planId), ("$u", ctx.UserSid ?? ""), ("$s", ctx.Session), ("$e", expires));
            if (script)
                db.Exec("INSERT INTO action_nonce_bindings (nonce, plan_hash, options) VALUES ($n, $h, $o)",
                    ("$n", nonce), ("$h", plan.Text("planHash")), ("$o", Json.Write(NormalisedOptions(plan, options!))));
            return (JsonNode)new JsonObject { ["nonce"] = nonce, ["expiresAt"] = expires };
        });
    }

    /// <summary>
    /// action.approve: binds planId + planHash + single-use nonce + user SID + session. A plan runs at most once: a
    /// repeated approve returns the existing operation. Holding the plan's resource locks, the handler re-resolves live
    /// state and the plan only runs if every execution-relevant field still matches (m9 §4).
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
        // m9 §5: a script has its own approval (options bound in the nonce, one transaction, the exclusive lease, its own run).
        if (IsScript(plan)) return await ApproveScriptAsync(p, ctx, plan).ConfigureAwait(false);
        if (p["options"] is not null) throw RpcError.Params("options", "options applies to a script plan only.");
        if (plan.Text("actionId") == ManualOpen) throw RpcError.State("manual", "This is a manual step: open the settings, then check again.");
        var fix = IsFix(plan);
        var handler = fix ? null : Handler(plan.Text("actionId")) ?? throw RpcError.State("unavailable", "That action is not available in this build.");

        // Never queue behind a change already running on an overlapping resource (a Defender scan holds it for hours, and
        // the app's connection answers one request at a time): refuse at once. A repeated approve of a plan that ran replays.
        if (!_resources.TryAcquire(planId, PlanResources(plan)))
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
        var leaseId = (fix ? StepsOf(plan).Any(s => Handler(s.Text("actionId"))?.TakesLease == true) : handler!.TakesLease) ? planId : null;
        var leaseKept = false;
        string? pendingStop = null; // a pending-stop entry not yet owned by its run (m9 review S5)
        try
        {
            // Inside the lock: replay first, then the approval binding.
            existing = _store.Read(db => OperationOfPlan(db, planId));
            if (existing is not null) return existing;
            if (leaseId is not null) Lease.Acquire(WorkLease.Action, leaseId);
            try { _store.Write(db => Bind(db, planId, planHash, nonce, ctx)); }
            catch (RpcError e) when (LostApprove(e))
            {
                CancelLost(planId, e, "The task that asked for this change ended.");
                throw RpcError.State("cancelled", "The plan is cancelled.");
            }

            if (fix)
            {
                // m9 §4: every step re-resolves inside the plan's locks before anything runs (else stale_plan); the steps then
                // run in the background, so "Stop after this step" can reach the host while they run.
                await RecheckFixAsync(planId, plan).ConfigureAwait(false);
                var fixOperationId = _store.Write(db => PrepareFix(db, planId));
                if (leaseId is not null)
                {
                    Lease.Rename(WorkLease.Action, leaseId, fixOperationId);
                    leaseId = fixOperationId;
                }
                release = false;
                leaseKept = true;
                var heldLease = leaseId;
                Track(Task.Run(async () =>
                {
                    try { await RunFixAsync(fixOperationId, plan).ConfigureAwait(false); }
                    finally
                    {
                        if (heldLease is not null) Lease.Release(WorkLease.Action, heldLease);
                        _resources.Release(planId);
                    }
                }));
                return _store.Read(db => OperationView(db, fixOperationId));
            }

            var request = new ActionRequest(plan.Text("actionId"), plan["target"]!.DeepClone().AsObject(), plan["parameters"]?.DeepClone() as JsonObject, null)
            {
                Undo = plan["undoOf"] is JsonValue,
                Recheck = true, // the target is the one this handler resolved for the plan
            };
            var live = await ResolveAsync(handler!, request).ConfigureAwait(false);
            // Settings compare Before and After; provider operations compare Target and After only (Defender moves its
            // own Before values — signature version, last scan — and that must not make an approval stale). Both compare
            // the privilege, flags and resources the user approved (m9 §4).
            var provider = handler!.Kind == "provider";
            var matches = (provider ? Same(live.Target, plan["target"]) && Same(live.After, plan["after"]) : Same(live.Before, plan["before"]) && Same(live.After, plan["after"]))
                && PrivilegeOf(handler!, live) == plan.Text("privilege")
                && Same(Json.Array(FlagsOf(handler!, live)), plan["flags"] ?? new JsonArray())
                && Same(Json.Array(ResourcesOf(handler!, live)), Json.Array(PlanResources(plan)));
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
            // m9 §3.5: an administrator plan runs as a one-step broker session; an undo names the broker's record of the original.
            AdminDispatch? admin = null;
            if (plan.Text("privilege") == "admin")
            {
                var undoRecord = plan["undoOf"] is JsonValue u && u.TryGetValue<string>(out var originalOp) ? _store.Read(db => BrokerRecordOf(db, originalOp, null)) : null;
                admin = new AdminDispatch(ElevatedStepOf(1, handler!, execution.Target, execution.Before, execution.After, execution.Parameters, undoRecord));
            }
            // m9 review R5: a provider operation is pending-stop from the transaction that creates it, so a Stop that comes
            // before its run registers the stop signal is kept (OperationStop reads under the same store lock). Until the
            // run owns it, any failure (the commit itself included) removes it again (m9 review S5).
            var operationId = _store.Write(db =>
            {
                var id = Prepare(db, planId, live.Before);
                if (provider) lock (_gate) _pendingStops[pendingStop = id] = false;
                return id;
            });
            if (leaseId is not null)
            {
                Lease.Rename(WorkLease.Action, leaseId, operationId);
                leaseId = operationId;
            }

            if (handler!.Kind == "provider")
            {
                release = false; // the background run holds the resource locks until the provider operation ends
                leaseKept = true; // and the shared lease
                var held = leaseId;
                pendingStop = null; // the run removes it when it ends
                Track(Task.Run(async () =>
                {
                    Task? stillWriting = null;
                    try { await RunAsync(handler!, execution, operationId, provider: true, t => stillWriting = t, admin).ConfigureAwait(false); }
                    finally
                    {
                        // A handler still running after its deadline, or a broker process that hasn't exited, keeps the locks
                        // and the lease until it really ends (m9 §13 R2), even when recording the result failed.
                        if (stillWriting is { } writing) await HoldAsync(writing).ConfigureAwait(false);
                        if (held is not null) Lease.Release(WorkLease.Action, held);
                        _resources.Release(planId);
                    }
                }));
                return _store.Read(db => OperationView(db, operationId));
            }
            Task? pending = null;
            try { await RunAsync(handler!, execution, operationId, provider: false, t => pending = t, admin).ConfigureAwait(false); }
            finally
            {
                if (pending is { } stillWriting)
                {
                    // Recorded indeterminate (or not recorded at all: the finish failed), but the write may still land: the
                    // locks (and lease) stay held until it ends, so nothing else (another action, a script) starts on these
                    // resources meanwhile (action_active).
                    release = false;
                    leaseKept = true;
                    var heldLease = leaseId;
                    Track(Task.Run(async () =>
                    {
                        try { await HoldAsync(stillWriting).ConfigureAwait(false); }
                        finally
                        {
                            if (heldLease is not null) Lease.Release(WorkLease.Action, heldLease);
                            _resources.Release(planId);
                        }
                    }));
                }
            }
            return _store.Read(db => OperationView(db, operationId));
        }
        finally
        {
            if (leaseId is not null && !leaseKept) Lease.Release(WorkLease.Action, leaseId);
            lock (_gate)
            {
                _approving.Remove(planId);
                if (pendingStop is not null) _pendingStops.Remove(pendingStop);
            }
            if (release) _resources.Release(planId);
            approving.TrySetResult(); // after the release, so a waiting duplicate can take the lock itself

        }
    }

    /// <summary>Checks and consumes the approval binding. Hash or plan mismatches and nonce misuse are refused.</summary>
    private void Bind(Db db, string planId, string planHash, string nonce, CallContext ctx)
    {
        var plan = LivePlan(db, planId);
        if (plan.Text("status") == "expired") throw RpcError.State("expired", "The plan expired. Preview again.");
        if (plan.Text("status") != "awaiting_user") throw RpcError.State(plan.Text("status"), $"The plan is {plan.Text("status")}.");
        // m9 §1.3: an agent's plan is approvable only while its task is active, still holds changes and has no other
        // plan waiting. A loser is cancelled by the caller (this transaction rolls back) and never runs. m9 §17.1: an
        // administrator plan also needs the flag and the grant's administrator rights (by its stored privilege).
        if (plan["requestedBy"]?["taskId"]?.GetValue<string>() is { } requester)
            CheckAdmin(plan, CheckRequester(db, requester, plan["cardId"]?.GetValue<string>(), otherThan: planId, level: IsFix(plan) ? FixKind : "ask"), AdminFlagFor(plan));
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
        catch (Exception ex) { AppLog.Current.Warn($"action: shutdown failed: {ex.GetType().Name}", ErrorSites.ActionShutdown); }
    }
}

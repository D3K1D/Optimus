using System.Globalization;
using System.Text.Json.Nodes;
using OptimizationTool.Transport.Logging;
using OptimizationTool.Engine.Actions.Scripts;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Engine.Actions.Scripts.Guard;

namespace OptimizationTool.Engine.Actions;

/// <summary>One propose_script call as the AI made it (contracts/m9-ai-powers.md §5.2). Never trusted: every field is checked.</summary>
internal sealed record ScriptProposal(string? Title, string? Why, string? By, string? Script, JsonArray? Parts, bool Elevated, string? FindingId);

/// <summary>A stored script plan with the propose_script result's refusals and warnings (§5.2 / §5.7).</summary>
internal sealed record ScriptProposed(JsonObject Plan, IReadOnlyList<GuardFinding> Refused, IReadOnlyList<GuardFinding> Warnings);

// Approved scripts (contracts/m9-ai-powers.md §5, §5.7): a script is its own plan (kind script) with its exact bytes, the
// guard's warnings and the AI's explanations; it is approved alone with the dialog's options bound into the nonce, and runs
// once, under an exclusive lease, through the one runner seam, with its own lifecycle (never verified, never undone).
public sealed partial class ActionService
{
    public const string ScriptKind = "script";
    public const int ScriptTitle = 80, ScriptWhy = 400, ScriptParts = 12, ScriptExplains = 200;
    public static readonly string[] ScriptStates = ["prepared", "running", "completed", "failed", "stopped", "timed_out", "not_run", "indeterminate"];
    public static readonly string[] ScriptEnded = ["completed", "failed", "stopped", "timed_out", "not_run", "indeterminate"];
    public const string ScriptAdminUnavailable = "admin_unavailable";

    /// <summary>The data folder (run folders live under &lt;data&gt;\script-runs).</summary>
    internal string? DataDir { get; init; }

    private ScriptOptions ScriptsWiring => _options.Scripts;

    /// <summary>This host has a script path: a runner, a policy reader and PowerShell's parser (or the explicit first-layer-only option).</summary>
    internal bool ScriptsAvailable => ScriptsWiring.Configured && (ScriptsWiring.Parser is not null || ScriptsWiring.AllowGuardWithoutParser);

    /// <summary>Elevated scripts run here: --enable-admin-actions, --enable-elevated-scripts and the broker's runner (m9 §5.4).</summary>
    internal bool ElevatedScriptsAvailable => _options.EnableAdminActions && ScriptsWiring.EnableElevatedScripts && ScriptsWiring.ElevatedRunner is not null;

    internal string RunsRoot => Path.GetFullPath(ScriptsWiring.RunsRoot ?? Path.Combine(DataDir ?? Path.GetTempPath(), "script-runs"));

    private static bool IsScript(JsonObject plan) => plan["kind"] is JsonValue k && k.TryGetValue<string>(out var s) && s == ScriptKind;

    /// <summary>A running script: its transcript, its stop source and why it was stopped.</summary>
    private sealed class LiveScript
    {
        public readonly ScriptTranscript Transcript = new();
        public required CancellationTokenSource Stop { get; init; }
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile string? Reason;
        public string? Folder;
        public DateTimeOffset? StartedAt;
        /// <summary>What may still be running after the run returned (a broker process that has not exited); the lease and the
        /// exclusion are released only once it ended (m9 §13 R2). Touched only by the run itself, then by its release.</summary>
        public readonly List<Task> StillRunning = [];
    }

    private readonly Dictionary<string, LiveScript> _scripts = [];

    // ---------------------------------------------------------------- propose

    /// <summary>
    /// propose_script: serialises the text (§5.1 rules 3–4), checks parts, title and why, reads the policy, runs the guard
    /// (both layers), and stores the plan (awaiting_user, or ineligible with the first refusal). The proposal is charged to the
    /// task in the same transaction that stores the plan, after the task is re-checked (active, scripts granted, not paired,
    /// not web-tainted, within limits.scripts, no other pending plan).
    /// </summary>
    internal async Task<ScriptProposed> CreateScriptAsync(ScriptProposal p, RequestedBy by, CancellationToken ct = default, CardBinding? card = null)
    {
        (string ReportId, JsonObject Report, JsonObject Finding)? found = p.FindingId is null ? null
            : _store.Read(db => FindFinding(db, p.FindingId)) ?? throw RpcError.NotFound("finding", p.FindingId);
        var refused = new List<GuardFinding>();
        IReadOnlyList<GuardFinding> warnings = [];
        var ser = ScriptText.Serialise(p.Script);
        var cleanTitle = PlainText(p.Title);
        var cleanWhy = PlainText(p.Why);
        var policy = new ScriptPolicyState(false, false);
        if (!ser.Ok) refused.Add(new GuardFinding(ser.Rule!, ser.Line));
        else
        {
            if (CheckParts(p.Parts, ser.Lines.Count) is { } partsLine) refused.Add(new GuardFinding("parts", partsLine.Line));
            if (cleanTitle is null || cleanTitle.Length > ScriptTitle) refused.Add(new GuardFinding("title", null));
            if (cleanWhy is null || cleanWhy.Length > ScriptWhy) refused.Add(new GuardFinding("why", null));
            try { policy = ScriptsWiring.Policy?.Read() ?? new ScriptPolicyState(true, false); }
            catch (Exception ex) { AppLog.Current.Warn($"script: policy read failed ({ex.GetType().Name})", ErrorSites.ScriptPolicyRead); policy = new ScriptPolicyState(true, false); }
            if (policy.Managed) refused.Add(new GuardFinding("managed", null));

            ScriptParse? parse = null;
            if (ScriptsWiring.Parser is { } parser)
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
                    cts.CancelAfter(_options.ResolveTimeout);
                    parse = await parser.ParseAsync(ser.Bytes, cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) { AppLog.Current.Warn($"script: parser unavailable ({ex.GetType().Name})", ErrorSites.ScriptParser); parse = null; }
            var guard = ScriptGuard.Check(ser.Text, parse, requireParser: !ScriptsWiring.AllowGuardWithoutParser);
            refused.AddRange(guard.Refused);
            warnings = guard.Warnings;
            // m9 §5.4, D6: an elevated script needs both host flags and the broker's runner; otherwise admin_unavailable.
            if (p.Elevated && refused.Count == 0 && !ElevatedScriptsAvailable) refused.Add(new GuardFinding(ScriptAdminUnavailable, null));
        }

        var restorePoint = ScriptsWiring.RestorePoint?.Availability() ?? new RestorePointAvailability(false, "flag_off");
        var plan = _store.Write(db =>
        {
            if (by.TaskId is { } taskId)
            {
                var grant = CheckScriptRequester(db, taskId, card?.CardId, otherThan: null, approving: false);
                // m9 §17.1: an elevated script needs administrator rights in the grant (the flags' admin_unavailable wins).
                if (p.Elevated && refused.Count == 0 && AdminRefusal(grant, flagOn: true) is { } admin) refused.Add(new GuardFinding(admin.Code, null));
                Agent.AgentService.ChargeScriptProposal(db, taskId); // §5.1 rule 8: charged here, refused proposals included
            }
            return InsertScript(db, p, ser, cleanTitle, cleanWhy, refused, warnings, policy, restorePoint, found, by, card);
        });
        return new ScriptProposed(plan, refused, warnings);
    }

    /// <summary>parts: 1–12, whole lines in range, ascending, not overlapping, explains plain text of 1–200 characters. Null when fine.</summary>
    private static (int? Line, bool Bad)? CheckParts(JsonArray? parts, int lineCount)
    {
        if (parts is null || parts.Count is < 1 or > ScriptParts) return (null, true);
        var previous = 0;
        foreach (var node in parts)
        {
            if (node is not JsonObject q) return (null, true);
            int? from = IntOf(q["fromLine"]), to = IntOf(q["toLine"]);
            var explains = q["explains"] is JsonValue ev && ev.TryGetValue<string>(out var e) ? e : null;
            var bad = from is null || to is null || from < 1 || to < from || to > lineCount || from <= previous
                      || explains is null || PlainText(explains) is null || explains.Length > ScriptExplains;
            if (bad) return (from, true);
            previous = to!.Value;
        }
        return null;
    }

    private static int? IntOf(JsonNode? n) => n is JsonValue v && Json.Number(v) is double d && d == Math.Floor(d) && Math.Abs(d) < 1e6 ? (int)d : null;

    /// <summary>
    /// In the caller's transaction, the task may (still) have a script: active, scripts and multi-step fixes granted, an
    /// in-app or headless task (never a paired user window), never web-tainted (§5.5); when proposing, within
    /// limits.scripts and within the slot rule (m9 §17.3). Throws TaskInactive or PlanRefused. Returns the task's parsed grant.
    /// </summary>
    private Agent.Grant CheckScriptRequester(Db db, string taskId, string? cardId, string? otherThan, bool approving)
    {
        var row = db.Query("SELECT status, body, connection_id FROM agent_tasks WHERE task_id = $t",
            r => (Status: r.GetString(0), Body: r.GetString(1), Bound: !r.IsDBNull(2)), ("$t", taskId)).FirstOrDefault();
        if (row.Status != "active") throw RpcError.State(TaskInactive, $"The task is {row.Status ?? "gone"}.");
        var task = Json.Parse(row.Body) as JsonObject ?? throw RpcError.State(PlanRefused, "This task may not propose scripts.");
        var grant = task["grant"] is { } g ? Agent.Grant.FromJson(g) : null;
        if (grant is null || !grant.Scripts || grant.Changes != FixKind) throw RpcError.State(PlanRefused, "This task may not propose scripts.");
        if (row.Bound || task["pairing"] is JsonObject) throw RpcError.State(PlanRefused, "A paired window can't propose scripts.");
        if (Agent.AgentService.IsWebTainted(task)) throw RpcError.State(PlanRefused, "Start a new conversation to propose a script.");
        var used = Agent.AgentService.ScriptProposals(task);
        if (approving ? used > grant.Limits.Scripts : used >= grant.Limits.Scripts) throw RpcError.State(PlanRefused, "This task has used all of its script proposals.");
        CheckSlots(db, taskId, cardId, otherThan);
        return grant;
    }

    /// <summary>The §5.2 / §5.7 plan of kind script, with its hash, stored with its event.</summary>
    private JsonObject InsertScript(Db db, ScriptProposal p, ScriptSerialisation ser, string? title, string? why, IReadOnlyList<GuardFinding> refused,
        IReadOnlyList<GuardFinding> warnings, ScriptPolicyState policy, RestorePointAvailability restorePoint,
        (string ReportId, JsonObject Report, JsonObject Finding)? found, RequestedBy by, CardBinding? card = null)
    {
        var planId = "plan-" + Ids.RandomHex();
        var now = _time.GetUtcNow();
        var first = refused.FirstOrDefault();
        var eligible = first is null;
        var plan = new JsonObject
        {
            ["planId"] = planId, ["kind"] = ScriptKind, ["actionId"] = null, ["handlerVersion"] = null, ["label"] = null,
            ["findingId"] = found?.Finding.Text("id"), ["reportId"] = found?.ReportId,
            ["title"] = title is null ? null : title.Length > ScriptTitle ? title[..ScriptTitle] : title,
            ["why"] = WhyOf(why is null ? null : why.Length > ScriptWhy ? why[..ScriptWhy] : why, p.By),
            ["target"] = null, ["before"] = null, ["after"] = null, ["parameters"] = null,
            ["rows"] = new JsonArray(), ["context"] = new JsonArray(), ["tradeoffs"] = new JsonArray(),
            ["privilege"] = p.Elevated ? "admin" : "none",
            ["flags"] = Json.Array([ActionFlags.Irreversible, ActionFlags.RunsCode]),
            ["resources"] = new JsonArray(),
            ["undo"] = new JsonObject { ["supported"] = false, ["conditions"] = null },
            ["steps"] = null,
            ["requestedBy"] = by.ToJson(),
            ["targetRef"] = null, ["undoOf"] = null,
            ["createdAt"] = Time.Format(now), ["expiresAt"] = Time.Format(now + _options.PlanLifetime),
            ["status"] = eligible ? "awaiting_user" : "ineligible",
            ["ineligible"] = eligible ? null : new JsonObject
            {
                ["code"] = first!.Rule, ["manualUri"] = null,
                ["message"] = first.Rule == AdminNotAllowed ? AdminNotAllowedMessage : (first.Line is { } l ? $"Line {l}: " : "") + $"refused ({first.Rule})",
            },
            ["script"] = !eligible ? null : new JsonObject
            {
                ["language"] = "powershell", ["text"] = ser.Text, ["sha256"] = ser.Sha256, ["bytes"] = ser.Bytes.Length, ["lines"] = ser.Lines.Count,
                ["parts"] = new JsonArray([.. p.Parts!.OfType<JsonObject>().Select(q => (JsonNode?)new JsonObject
                {
                    ["fromLine"] = IntOf(q["fromLine"]), ["toLine"] = IntOf(q["toLine"]), ["explains"] = PlainText(q["explains"]!.GetValue<string>()),
                })]),
                ["elevated"] = p.Elevated,
                ["guard"] = new JsonObject
                {
                    ["version"] = ScriptGuard.Version,
                    ["warnings"] = new JsonArray([.. warnings.Select(w => (JsonNode?)new JsonObject { ["rule"] = w.Rule, ["line"] = w.Line, ["text"] = w.Text })]),
                },
                ["restorePoint"] = new JsonObject { ["available"] = restorePoint.Available, ["reason"] = restorePoint.Available ? null : restorePoint.Reason ?? "flag_off" },
                ["bypassesPolicy"] = policy.NeedsBypass,
            },
        };
        BindCard(db, plan, card, by); // m9 §17.3
        plan["planHash"] = HashScript(plan);
        db.Exec("INSERT INTO action_plans (plan_id, body, status, task_id, target_key, created_at, expires_at, card_id) VALUES ($p, $b, $s, $t, $k, $c, $e, $card)",
            ("$p", planId), ("$b", Json.Write(plan)), ("$s", plan.Text("status")), ("$t", by.TaskId), ("$k", "script:" + planId),
            ("$c", plan.Text("createdAt")), ("$e", plan.Text("expiresAt")), ("$card", plan["cardId"]?.GetValue<string>()));
        PlanEvent(db, plan);
        return plan;
    }

    /// <summary>
    /// A script plan's hash (§5.2): the sha256 and size of the exact bytes, elevated, the parts, the guard version and its
    /// warnings, title, why and expiresAt, plus the plan's identity, privilege, flags, the restore-point availability and the
    /// policy line (what the dialog shows).
    /// </summary>
    internal static string HashScript(JsonObject plan)
    {
        var s = plan["script"] as JsonObject;
        return Json.Digest(new JsonObject
        {
            ["planId"] = plan["planId"]?.DeepClone(), ["kind"] = plan["kind"]?.DeepClone(), ["findingId"] = plan["findingId"]?.DeepClone(),
            ["title"] = plan["title"]?.DeepClone(), ["why"] = plan["why"]?.DeepClone(), ["privilege"] = plan["privilege"]?.DeepClone(),
            ["flags"] = plan["flags"]?.DeepClone(), ["expiresAt"] = plan["expiresAt"]?.DeepClone(),
            ["sha256"] = s?["sha256"]?.DeepClone(), ["bytes"] = s?["bytes"]?.DeepClone(), ["elevated"] = s?["elevated"]?.DeepClone(),
            ["parts"] = s?["parts"]?.DeepClone(), ["guardVersion"] = s?["guard"]?["version"]?.DeepClone(), ["warnings"] = s?["guard"]?["warnings"]?.DeepClone(),
            ["restorePoint"] = s?["restorePoint"]?.DeepClone(), ["bypassesPolicy"] = s?["bypassesPolicy"]?.DeepClone(),
        });
    }

    // ---------------------------------------------------------------- approve

    /// <summary>options {restorePoint, shareOutput}: both booleans, nothing else (the dialog's choices). Null when absent.</summary>
    private static JsonObject? ScriptOptionsParam(JsonObject p)
    {
        if (p["options"] is null) return null;
        if (p["options"] is not JsonObject o || o.Count != 2
            || o["restorePoint"] is not JsonValue rv || !rv.TryGetValue<bool>(out var rp)
            || o["shareOutput"] is not JsonValue sv || !sv.TryGetValue<bool>(out var share))
            throw RpcError.Params("options", "options must be {restorePoint: boolean, shareOutput: boolean}.");
        return new JsonObject { ["restorePoint"] = rp, ["shareOutput"] = share };
    }

    /// <summary>A restore point can only be asked for when the plan says one is available.</summary>
    private static JsonObject NormalisedOptions(JsonObject plan, JsonObject options) => new()
    {
        ["restorePoint"] = options["restorePoint"]!.GetValue<bool>() && plan["script"]?["restorePoint"]?["available"]?.GetValue<bool>() == true,
        ["shareOutput"] = options["shareOutput"]!.GetValue<bool>(),
    };

    /// <summary>
    /// action.approve for a script (§5.2 "Binding", §5.7): the exclusive lease first (any action running, a scan or a
    /// benchmark → busy, the plan stays awaiting_user, never queued), then one transaction that re-checks the task, consumes
    /// the nonce (bound to plan, hash, options, user, session and expiry), moves the plan to approved and creates the one
    /// operation and its durable dispatch record. A retry or a duplicate returns that operation; nothing runs twice.
    /// </summary>
    private async Task<JsonNode> ApproveScriptAsync(JsonObject p, CallContext ctx, JsonObject plan)
    {
        var planId = plan.Text("planId");
        var planHash = P.Req(p, "planHash", 64);
        var nonce = P.Req(p, "nonce", 64);
        var options = ScriptOptionsParam(p) ?? throw RpcError.Params("options", "A script's approval carries options {restorePoint, shareOutput}.");
        if (_store.Read(db => OperationOfPlan(db, planId)) is { } replay) return replay;

        if (!_resources.TryAcquireExclusive(planId, out var holder))
        {
            Task? first;
            lock (_gate) _approving.TryGetValue(planId, out first);
            if (first is not null)
            {
                await first.ConfigureAwait(false);
                return await ApproveAsync(p, ctx).ConfigureAwait(false);
            }
            if (_store.Read(db => OperationOfPlan(db, planId)) is { } existing) return existing;
            var busy = holder is null ? null : _store.Read(db => db.Scalar("SELECT operation_id FROM operations WHERE plan_id = $p", ("$p", holder)) as string);
            throw new RpcError(ErrorCodes.InvalidState, "A change is being applied.", new JsonObject { ["reason"] = "action_active", ["operationId"] = busy });
        }
        var approving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _approving[planId] = approving.Task;
        var kept = false;
        var leased = false;
        try
        {
            if (_store.Read(db => OperationOfPlan(db, planId)) is { } raced) return raced;
            // m6 §2 lease: a scan → -32004 scan_active, a benchmark → bench_active, another action → action_active.
            Lease.Acquire(WorkLease.Action, planId);
            leased = true;
            string operationId;
            try { operationId = _store.Write(db => BindScript(db, planId, planHash, nonce, options, ctx)); }
            catch (RpcError e) when (LostApprove(e))
            {
                CancelLost(planId, e, "The task that asked for this script can't run it any more.");
                throw RpcError.State("cancelled", "The plan is cancelled.");
            }
            Lease.Rename(WorkLease.Action, planId, operationId);
            var live = new LiveScript { Stop = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token) };
            lock (_gate) _scripts[operationId] = live;
            kept = true;
            var stored = _store.Read(db => LoadPlan(db, planId));
            var bound = _store.Read(db => LoadOperation(db, operationId))["options"]!.AsObject();
            Track(Task.Run(async () =>
            {
                try { await RunScriptAsync(operationId, stored, bound, live).ConfigureAwait(false); }
                finally
                {
                    // m9 §13 R2: an elevated run whose pipe ended keeps the lease and the exclusion until its broker exits.
                    if (live.StillRunning.Count > 0) await HoldAsync(Task.WhenAll(live.StillRunning)).ConfigureAwait(false);
                    Lease.Release(WorkLease.Action, operationId);
                    _resources.Release(planId);
                    lock (_gate) _scripts.Remove(operationId);
                    live.Stop.Dispose();
                    live.Done.TrySetResult();
                }
            }));
            return _store.Read(db => OperationView(db, operationId));
        }
        finally
        {
            if (!kept)
            {
                if (leased) Lease.Release(WorkLease.Action, planId);
                _resources.Release(planId);
            }
            lock (_gate) _approving.Remove(planId);
            approving.TrySetResult();
        }
    }

    /// <summary>The script approval transaction (see <see cref="ApproveScriptAsync"/>). Returns the new operation's ID.</summary>
    private string BindScript(Db db, string planId, string planHash, string nonce, JsonObject options, CallContext ctx)
    {
        var plan = LivePlan(db, planId);
        if (plan.Text("status") == "expired") throw RpcError.State("expired", "The plan expired. Ask again.");
        if (plan.Text("status") != "awaiting_user") throw RpcError.State(plan.Text("status"), $"The plan is {plan.Text("status")}.");
        // m9 §17.1: an elevated script also needs the flags and the grant's administrator rights (by the stored privilege).
        if (plan["requestedBy"]?["taskId"]?.GetValue<string>() is { } requester)
            CheckAdmin(plan, CheckScriptRequester(db, requester, plan["cardId"]?.GetValue<string>(), otherThan: planId, approving: true), ElevatedScriptsAvailable);
        var row = db.Query("SELECT plan_id, user_sid, session, expires_at, used_at FROM action_nonces WHERE nonce = $n",
            r => (Plan: r.GetString(0), Sid: r.GetString(1), Session: r.GetInt64(2), Expires: r.GetString(3), Used: r.IsDBNull(4) ? null : r.GetString(4)),
            ("$n", nonce)).FirstOrDefault();
        var binding = db.Query("SELECT plan_hash, options FROM action_nonce_bindings WHERE nonce = $n",
            r => (Hash: r.GetString(0), Options: r.GetString(1)), ("$n", nonce)).FirstOrDefault();
        if (row.Plan is null || row.Plan != planId || row.Used is not null || _time.GetUtcNow() >= Parse(row.Expires)
            || row.Sid != (ctx.UserSid ?? "") || row.Session != ctx.Session || binding.Hash is null)
            throw RpcError.State("invalid_nonce", "The approval is not valid. Approve again.");
        db.Exec("UPDATE action_nonces SET used_at = $t WHERE nonce = $n", ("$t", db.Now()), ("$n", nonce));
        // The choices travel from the dialog to the challenge; an approve that names other choices than the challenged ones is refused.
        if (binding.Options != Json.Write(NormalisedOptions(plan, options)) || binding.Hash != plan.Text("planHash"))
            throw RpcError.State("invalid_nonce", "The approval does not match what was shown. Approve again.");
        if (planHash != plan.Text("planHash"))
            throw new RpcError(ErrorCodes.StaleRevision, "The plan changed.", new JsonObject { ["reason"] = "stale_plan", ["current"] = plan.DeepClone() });
        SavePlan(db, plan, "approved");

        var operationId = "op-" + Ids.RandomHex();
        var bound = Json.Parse(binding.Options)!.AsObject();
        var op = new JsonObject
        {
            ["operationId"] = operationId, ["planId"] = planId, ["kind"] = ScriptKind, ["actionId"] = null, ["steps"] = null, ["state"] = "prepared",
            ["options"] = bound, ["startedAt"] = null, ["endedAt"] = null, ["restorePoint"] = null, ["receipt"] = null, ["sharedOutput"] = null,
            ["undo"] = new JsonObject { ["available"] = false, ["reason"] = "not_supported" }, ["error"] = null, ["progress"] = null,
            ["findingId"] = plan["findingId"]?.DeepClone(), ["requestedBy"] = plan["requestedBy"]?.DeepClone(),
            ["createdAt"] = db.Now(), ["updatedAt"] = db.Now(),
        };
        db.Exec("INSERT INTO operations (operation_id, plan_id, body, state, created_at) VALUES ($o, $p, $b, 'prepared', $t)",
            ("$o", operationId), ("$p", planId), ("$b", Json.Write(op)), ("$t", db.Now()));
        var script = plan["script"]!.AsObject();
        // The durable dispatch record (§5.2): one row per approved script; dispatched_at is set, and committed, before launch.
        db.Exec("""
            INSERT INTO script_runs (operation_id, plan_id, task_id, sha256, text, options, approved_at, state, exit_code, guard_version, warnings,
                restore_point, transcript_path, dispatched_at, ended_at)
            VALUES ($o, $p, $t, $h, $x, $op, $a, 'prepared', NULL, $g, $w, NULL, NULL, NULL, NULL)
            """, ("$o", operationId), ("$p", planId), ("$t", plan["requestedBy"]?["taskId"]?.GetValue<string>()), ("$h", script.Text("sha256")),
            ("$x", script.Text("text")), ("$op", Json.Write(bound)), ("$a", db.Now()), ("$g", script["guard"]!.Text("version")),
            ("$w", Json.Write(script["guard"]!["warnings"])));
        Step(db, operationId, "prepared", null);
        StateEvent(db, op);
        return operationId;
    }

    // ---------------------------------------------------------------- run

    private sealed class Observer(ActionService owner, string operationId, LiveScript live) : IScriptRunObserver
    {
        public void Started(DateTimeOffset at, string runFolder)
        {
            live.StartedAt = at;
            live.Folder = runFolder;
            owner.Transition(operationId, op =>
            {
                op["state"] = "running";
                op["startedAt"] = Time.Format(at);
                op["progress"] = "Running · 0:00";
            }, planStatus: "running", runState: "running");
        }

        public void Line(bool stderr, string text) => live.Transcript.Add(stderr, text);
    }

    /// <summary>One committed change to a script operation with its operation.state event (and the plan's status, when given).</summary>
    private void Transition(string operationId, Action<JsonObject> change, string? planStatus = null, string? runState = null, string? step = null)
    {
        try
        {
            _store.Write(db =>
            {
                var op = LoadOperation(db, operationId);
                change(op);
                SaveOperation(db, op);
                if (step is not null) Step(db, operationId, step, null);
                if (planStatus is not null)
                {
                    var plan = LoadPlan(db, op.Text("planId"));
                    if (plan.Text("status") != planStatus) SavePlan(db, plan, planStatus);
                }
                if (runState is not null) db.Exec("UPDATE script_runs SET state = $s WHERE operation_id = $o", ("$s", runState), ("$o", operationId));
                StateEvent(db, op);
            });
        }
        catch (Exception ex) { AppLog.Current.Error($"script: {operationId}: transition failed ({ex.GetType().Name})", ErrorSites.ScriptRunFailed); }
    }

    private static string Clock(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    /// <summary>
    /// The background run of an approved script (§5.4): the restore point first when asked (failed or declined → not_run),
    /// the bytes checked against the approved hash, the dispatch record committed, then the one call into the runner seam,
    /// with "Running · m:ss" progress, the 10-minute limit and Stop. Exit 0 is completed, never verified.
    /// </summary>
    private async Task RunScriptAsync(string operationId, JsonObject plan, JsonObject options, LiveScript live)
    {
        string state = "failed";
        int? exitCode = null;
        JsonObject? error = null;
        try
        {
            if (options["restorePoint"]?.GetValue<bool>() == true)
            {
                Transition(operationId, op => op["progress"] = "Making a restore point");
                RestorePointOutcome outcome;
                try
                {
                    outcome = ScriptsWiring.RestorePoint is { } step
                        ? await step.CreateAsync(live.Stop.Token).ConfigureAwait(false)
                        : new RestorePointOutcome("failed", null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AppLog.Current.Warn($"script: {operationId}: restore point failed ({ex.GetType().Name})", ErrorSites.ScriptRestorePoint);
                    outcome = new RestorePointOutcome("failed", null);
                }
                catch (OperationCanceledException) { outcome = new RestorePointOutcome("failed", null); }
                if (outcome.Ended is { } rpEnded) live.StillRunning.Add(rpEnded); // its broker session may outlive its answer
                var result = outcome.Result is "created" or "reused" or "declined" ? outcome.Result : "failed";
                var rp = new JsonObject { ["result"] = result, ["at"] = outcome.At is { } at && result is "created" or "reused" ? Time.Format(at) : null };
                Transition(operationId, op => { op["restorePoint"] = rp.DeepClone(); op["progress"] = null; });
                _store.Write(db => db.Exec("UPDATE script_runs SET restore_point = $r WHERE operation_id = $o", ("$r", Json.Write(rp)), ("$o", operationId)));
                if (result is "failed" or "declined")
                {
                    state = "not_run";
                    error = new JsonObject { ["code"] = "no_restore_point", ["message"] = "No restore point was made, so the script did not run." };
                    return;
                }
            }
            if (live.Stop.IsCancellationRequested)
            {
                state = live.Reason == "stop" ? "stopped" : "not_run";
                error = state == "not_run" ? new JsonObject { ["code"] = "interrupted", ["message"] = "The app stopped before the script started." } : null;
                return;
            }

            // The exact approved bytes, rebuilt from the stored text and checked against the approved hash and size.
            var script = plan["script"]!.AsObject();
            var ser = ScriptText.Serialise(script.Text("text"));
            if (!ser.Ok || ser.Sha256 != script.Text("sha256") || ser.Bytes.Length != script["bytes"]!.GetValue<int>())
            {
                error = NotStarted("The approved script no longer matches its hash, so nothing ran.");
                return;
            }
            // The policy is read again: a Group Policy that appeared since, or a policy line the card did not show, stops it here.
            ScriptPolicyState policy;
            try { policy = ScriptsWiring.Policy?.Read() ?? new ScriptPolicyState(true, false); }
            catch (Exception) { policy = new ScriptPolicyState(true, false); }
            if (policy.Managed || (policy.NeedsBypass && script["bypassesPolicy"]?.GetValue<bool>() != true))
            {
                error = NotStarted("The PowerShell script policy on this PC changed since the approval, so nothing ran.");
                return;
            }
            // The one runner seam: the user-scope runner, or (an elevated plan, both flags) the broker's.
            var elevated = script["elevated"]?.GetValue<bool>() == true;
            var chosen = elevated ? (ElevatedScriptsAvailable ? ScriptsWiring.ElevatedRunner : null) : ScriptsWiring.Runner;
            if (chosen is not { } runner)
            {
                error = NotStarted(elevated ? "Administrator scripts are not available in this version." : "Scripts are not available in this version.");
                return;
            }
            if (elevated)
                // Two broker sessions when a restore point came first: the user sees a second Windows prompt.
                Transition(operationId, op => op["progress"] = options["restorePoint"]?.GetValue<bool>() == true
                    ? "Asking Windows for permission again, to run the script" : "Asking Windows for permission to run the script");

            _store.Write(db => db.Exec("UPDATE script_runs SET dispatched_at = $t WHERE operation_id = $o", ("$t", db.Now()), ("$o", operationId)));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(live.Stop.Token);
            deadline.CancelAfter(ScriptsWiring.Timeout);
            using var ticking = new CancellationTokenSource();
            var ticker = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        await Task.Delay(ScriptsWiring.ProgressInterval, ticking.Token).ConfigureAwait(false);
                        if (live.StartedAt is { } started)
                            Transition(operationId, op => op["progress"] = "Running · " + Clock(_time.GetUtcNow() - started));
                    }
                }
                catch (OperationCanceledException) { }
            }, CancellationToken.None);

            ScriptRunResult run;
            try
            {
                var request = new ScriptRunRequest(operationId, ser.Bytes, ser.Sha256, ScriptsWiring.Timeout, policy.NeedsBypass, RunsRoot) { PlanId = plan.Text("planId") };
                run = await runner.RunAsync(request, new Observer(this, operationId, live), deadline.Token).ConfigureAwait(false);
                if (run.Ended is { } runEnded) live.StillRunning.Add(runEnded); // before anything else can fail (m9 §13 R2)
            }
            catch (OperationCanceledException) { run = ScriptRunResult.KilledRun(live.Folder, TimeSpan.Zero); }
            catch (Exception ex)
            {
                AppLog.Current.Warn($"script: {operationId}: runner failed ({ex.GetType().Name})", ErrorSites.ScriptRunFailed);
                run = live.StartedAt is null ? ScriptRunResult.NotStarted("launch_failed") : ScriptRunResult.KilledRun(live.Folder, TimeSpan.Zero);
                if (live.StartedAt is not null) live.Reason ??= "error";
            }
            finally
            {
                await ticking.CancelAsync().ConfigureAwait(false);
                try { await ticker.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
            live.Folder ??= run.RunFolder;

            if (run.NotStartedCode is { } code)
            {
                error = NotStarted(code switch
                {
                    "hash_mismatch" => "The script file did not match the approved script, so it did not run.",
                    "uac_cancelled" or "broker_cancelled" => "You cancelled in Windows, so the script did not run.",
                    _ when code.StartsWith("guard_", StringComparison.Ordinal) => "The administrator helper's own check refused the script, so it did not run.",
                    _ => "The script could not be started.",
                });
                return;
            }
            var stopped = live.Reason == "stop";
            var timedOut = !stopped && deadline.IsCancellationRequested && !live.Stop.IsCancellationRequested;
            if (run.Exited && !stopped && !timedOut)
            {
                exitCode = run.ExitCode;
                state = run.ExitCode == 0 ? "completed" : "failed";
                error = state == "failed" ? new JsonObject { ["code"] = "exit_code", ["message"] = $"The script ended with exit code {run.ExitCode}." } : null;
            }
            else if (stopped) state = "stopped";
            else if (timedOut)
            {
                state = "timed_out";
                error = new JsonObject { ["code"] = "timeout", ["message"] = $"Stopped after {(int)ScriptsWiring.Timeout.TotalMinutes} minutes." };
            }
            else
            {
                state = "indeterminate"; // the host is shutting down (or the runner broke) mid-run
                error = Interrupted();
            }
        }
        catch (Exception ex)
        {
            AppLog.Current.Error($"script: {operationId}: run failed ({ex.GetType().Name})", ErrorSites.ScriptRunFailed);
            state = live.StartedAt is null ? "failed" : "indeterminate";
            error = live.StartedAt is null ? NotStarted("The script could not be started.") : Interrupted();
        }
        finally
        {
            try { _store.Write(db => FinishScript(db, operationId, state, exitCode, error, live)); }
            catch (Exception ex) { AppLog.Current.Error($"script: {operationId}: finish failed ({ex.GetType().Name})", ErrorSites.ScriptRunFailed); }
        }
    }

    private static JsonObject NotStarted(string message) => new() { ["code"] = "not_started", ["message"] = message };

    private static JsonObject Interrupted() => new() { ["code"] = "interrupted", ["message"] = "Interrupted. Optimus doesn't know how far it got." };

    /// <summary>
    /// Ends a script operation (§5.4, §5.7): its receipt, the shared excerpt (when the user let the AI see output), the
    /// transcript file in the run folder, the script_runs row, the plan's status (it follows the end state) and the final
    /// event. A script never becomes verified and never marks a finding or card done.
    /// </summary>
    private void FinishScript(Db db, string operationId, string state, int? exitCode, JsonObject? error, LiveScript? live)
    {
        var op = LoadOperation(db, operationId);
        if (ScriptEnded.Contains(op.Text("state"))) return;
        var plan = LoadPlan(db, op.Text("planId"));
        var now = _time.GetUtcNow();
        var started = op["startedAt"] is JsonValue sv ? Parse(sv.GetValue<string>()) : (DateTimeOffset?)null;
        var transcript = live?.Transcript;
        var share = op["options"]?["shareOutput"]?.GetValue<bool>() == true && state != "not_run";
        JsonObject? shared = null;
        if (share)
        {
            var scriptPath = live?.Folder is { } folder ? Path.Combine(folder, "script.ps1") : null;
            var (excerpt, omitted) = ScriptOutputScrub.Excerpt(transcript?.Tail ?? [], transcript?.Lines ?? 0, scriptPath,
                ScriptsWiring.Identity?.Invoke() ?? ScrubIdentity.Current());
            shared = new JsonObject { ["excerpt"] = excerpt, ["omittedLines"] = omitted };
        }
        string? transcriptPath = null;
        if (transcript is not null && live?.Folder is { } runFolder && UnderRunsRoot(runFolder))
            try
            {
                transcriptPath = Path.Combine(runFolder, "transcript.txt");
                File.WriteAllText(transcriptPath, transcript.Text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Current.Warn($"script: {operationId}: transcript not stored ({ex.GetType().Name})", ErrorSites.ScriptTranscript);
                transcriptPath = null;
            }

        op["state"] = state;
        op["endedAt"] = Time.Format(now);
        op["progress"] = null;
        op["error"] = error;
        op["sharedOutput"] = shared;
        op["receipt"] = new JsonObject
        {
            ["state"] = state,
            ["exitCode"] = state is "completed" or "failed" ? exitCode : null,
            ["durationMs"] = started is { } s ? (long)Math.Max(0, (now - s).TotalMilliseconds) : 0,
            ["outputLines"] = transcript?.Lines ?? 0,
            ["truncated"] = transcript?.Truncated ?? false,
            ["sha256"] = plan["script"]?["sha256"]?.DeepClone(),
            ["restorePoint"] = op["restorePoint"]?.DeepClone(),
            ["sharedWithAi"] = shared is not null,
        };
        Step(db, operationId, state, error?["code"]?.GetValue<string>());
        SaveOperation(db, op);
        db.Exec("UPDATE script_runs SET state = $s, exit_code = $x, transcript_path = $t, ended_at = $e WHERE operation_id = $o",
            ("$s", state), ("$x", op["receipt"]!["exitCode"]?.GetValue<int>()), ("$t", transcriptPath), ("$e", Time.Format(now)), ("$o", operationId));
        if (plan.Text("status") is "approved" or "running") SavePlan(db, plan, state);
        StateEvent(db, op);
    }

    private bool UnderRunsRoot(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return full.StartsWith(RunsRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    // ---------------------------------------------------------------- stop, transcript, restart, retention

    /// <summary>operation.stop on a running script: every process in its job ends (the runner seam is cancelled), then the Operation.</summary>
    internal async Task<JsonNode> StopScriptAsync(string operationId)
    {
        LiveScript? live;
        lock (_gate) _scripts.TryGetValue(operationId, out live);
        AppLog.Current.Debug($"script: stop requested op={operationId} live={live is not null}");
        if (live is not null && !live.Done.Task.IsCompleted)
        {
            live.Reason ??= "stop";
            try { await live.Stop.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) { }
            try { await live.Done.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch (TimeoutException) { AppLog.Current.Info($"script: {operationId}: still ending after stop"); }
        }
        return _store.Read(db => (JsonNode)OperationView(db, operationId));
    }

    /// <summary>operation.stop for any operation (desktop): a script stops; a fix stops after its step; a long provider operation is cancelled; anything else is a no-op.</summary>
    public async Task<JsonNode> OperationStopAsync(JsonObject p)
    {
        var operationId = P.Req(p, "operationId", 64);
        var op = _store.Read(db => LoadOperation(db, operationId));
        return op.Text("kind") == ScriptKind ? await StopScriptAsync(operationId).ConfigureAwait(false) : OperationStop(p);
    }

    /// <summary>
    /// operation.transcript {operationId} (desktop) → {text, truncated}: the tagged lines joined by "\n". A running script's
    /// live transcript; an ended one's from its run folder. No caller names a path. Not a script → -32013 not_script.
    /// </summary>
    public JsonNode OperationTranscript(JsonObject p)
    {
        var operationId = P.Req(p, "operationId", 64);
        var op = _store.Read(db => LoadOperation(db, operationId));
        if (op.Text("kind") != ScriptKind) throw RpcError.State("not_script", "That operation is not a script.");
        LiveScript? live;
        lock (_gate) _scripts.TryGetValue(operationId, out live);
        if (live is not null && !ScriptEnded.Contains(op.Text("state")))
            return Fit(live.Transcript.Text, live.Transcript.Truncated);
        var path = _store.Read(db => db.Scalar("SELECT transcript_path FROM script_runs WHERE operation_id = $o", ("$o", operationId)) as string);
        var text = "";
        if (path is not null && UnderRunsRoot(path))
            try { if (File.Exists(path)) text = File.ReadAllText(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Current.Warn($"script: {operationId}: transcript unreadable ({ex.GetType().Name})", ErrorSites.ScriptTranscript); }
        return Fit(text, op["receipt"]?["truncated"]?.GetValue<bool>() ?? false);
    }

    /// <summary>Most of one protocol frame (1 MiB, JSON-escaped) that a transcript answer may use.</summary>
    internal const int TranscriptAnswerBytes = 900 * 1024;

    /// <summary>
    /// The stored transcript keeps the first 1 MiB; one answer is one protocol frame (1 MiB including the JSON escaping), so
    /// a transcript that would not fit is cut at a line end and marked truncated.
    /// </summary>
    private static JsonObject Fit(string text, bool truncated)
    {
        if (Json.Write(JsonValue.Create(text)).Length <= TranscriptAnswerBytes) return new JsonObject { ["text"] = text, ["truncated"] = truncated };
        var sb = new System.Text.StringBuilder();
        var size = 2;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var cost = Json.Write(JsonValue.Create(line + "\n")).Length - 2;
            if (size + cost > TranscriptAnswerBytes) break;
            sb.Append(line).Append('\n');
            size += cost;
        }
        return new JsonObject { ["text"] = sb.ToString(), ["truncated"] = true };
    }

    /// <summary>
    /// Restart (§5.4): a script that was prepared or running when the host stopped is never replayed. One whose dispatch
    /// record shows it was launched is indeterminate ("Interrupted…"); one never launched is not_run.
    /// </summary>
    internal void ReconcileScripts()
    {
        var open = _store.Read(db => db.Query("""
            SELECT o.operation_id, s.dispatched_at FROM operations o LEFT JOIN script_runs s ON s.operation_id = o.operation_id
            WHERE o.state IN ('prepared','running')
            """, r => (Id: r.GetString(0), Dispatched: !r.IsDBNull(1))));
        foreach (var (id, dispatched) in open)
        {
            if (_store.Read(db => LoadOperation(db, id)).Text("kind") != ScriptKind) continue;
            _store.Write(db =>
            {
                if (dispatched) FinishScript(db, id, "indeterminate", null, Interrupted(), null);
                else FinishScript(db, id, "not_run", null, new JsonObject { ["code"] = "interrupted", ["message"] = "The app stopped before the script started." }, null);
                var reconciled = LoadOperation(db, id);
                reconciled["reconciled"] = true;
                SaveOperation(db, reconciled);
            });
            AppLog.Current.Info($"script: reconciled {id}: {(dispatched ? "indeterminate" : "not_run")}");
        }
    }

    /// <summary>Transcripts are kept 30 days (§5.4): older run folders under the runs root are deleted and their paths cleared.</summary>
    internal void SweepScriptRuns()
    {
        var cutoff = _time.GetUtcNow() - ScriptsWiring.TranscriptLifetime;
        var old = _store.Read(db => db.Query("SELECT operation_id, transcript_path FROM script_runs WHERE ended_at IS NOT NULL AND ended_at < $c AND transcript_path IS NOT NULL",
            r => (Id: r.GetString(0), Path: r.GetString(1)), ("$c", Time.Format(cutoff))));
        foreach (var (id, path) in old)
        {
            var folder = Path.GetDirectoryName(path);
            if (folder is not null && UnderRunsRoot(folder))
                try
                {
                    var info = new DirectoryInfo(folder);
                    if (info.Exists && !info.Attributes.HasFlag(FileAttributes.ReparsePoint)) info.Delete(recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Current.Debug($"script: {id}: old run folder not removed ({ex.GetType().Name})"); continue; }
            _store.Write(db => db.Exec("UPDATE script_runs SET transcript_path = NULL WHERE operation_id = $o", ("$o", id)));
        }
    }

    // ---------------------------------------------------------------- what the AI gets

    /// <summary>get_operation for a script (§5.7): state, exit code, duration and line counts; the excerpt only when the user shared output.</summary>
    internal static JsonObject ScriptAiView(JsonObject op)
    {
        var receipt = op["receipt"] as JsonObject;
        var view = new JsonObject
        {
            ["operationId"] = op.Text("operationId"), ["kind"] = ScriptKind, ["state"] = op.Text("state"),
            ["exitCode"] = receipt?["exitCode"]?.DeepClone(), ["durationMs"] = receipt?["durationMs"]?.DeepClone() ?? 0,
            ["outputLines"] = receipt?["outputLines"]?.DeepClone() ?? 0, ["truncated"] = receipt?["truncated"]?.DeepClone() ?? false,
        };
        if (op["sharedOutput"] is JsonObject shared)
        {
            view["excerpt"] = shared["excerpt"]?.DeepClone();
            view["omittedLines"] = shared["omittedLines"]?.DeepClone();
            view["untrusted"] = ScriptOutputScrub.Untrusted;
        }
        return view;
    }

    /// <summary>The script operation of a plan owned by <paramref name="taskId"/>, as the AI sees it, or null.</summary>
    internal JsonObject? ScriptOperationForTask(Db db, string taskId, string operationId)
    {
        var planId = db.Scalar("SELECT plan_id FROM operations WHERE operation_id = $o", ("$o", operationId)) as string;
        if (planId is null || db.Scalar("SELECT 1 FROM action_plans WHERE plan_id = $p AND task_id = $t", ("$p", planId), ("$t", taskId)) is null) return null;
        var op = LoadOperation(db, operationId);
        return op.Text("kind") == ScriptKind ? ScriptAiView(op) : null;
    }

    /// <summary>The script proposals stored for display counts (tests and the activity summary).</summary>
    internal static string ScriptSummary(JsonObject plan) =>
        plan.Text("status") == "awaiting_user"
            ? string.Create(CultureInfo.InvariantCulture, $"A {plan["script"]!["lines"]}-line script, waiting for the user to read and approve it in Optimus. Poll get_operation with this planId.")
            : $"Not proposed: {plan["ineligible"]?["message"]?.GetValue<string>() ?? "refused"}";
}

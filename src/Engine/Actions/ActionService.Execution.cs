using System.Text.Json.Nodes;
using OptimizationTool.Transport.Logging;

namespace OptimizationTool.Engine.Actions;

// Journal, execution, receipts, operations, undo, restart reconcile and manual tasks (contracts/m4-actions.md §3–§5).
public sealed partial class ActionService
{
    public static readonly string[] OperationStates = ["prepared", "attempted", "verified", "completed", "failed", "indeterminate", "partial", "rolled_back_failed"];

    // ---------------------------------------------------------------- journal

    /// <summary>Journals "prepared" with the live preimage (before the write) and moves the plan to running.</summary>
    private string Prepare(Db db, string planId, JsonObject preimage)
    {
        var plan = LoadPlan(db, planId);
        var operationId = "op-" + Ids.RandomHex();
        var op = new JsonObject
        {
            ["operationId"] = operationId, ["planId"] = planId, ["actionId"] = plan.Text("actionId"), ["state"] = "prepared",
            ["preimage"] = preimage.DeepClone(), ["receipt"] = null, ["undo"] = null, ["error"] = null, ["progress"] = null,
            ["findingId"] = plan["findingId"]?.DeepClone(), ["requestedBy"] = plan["requestedBy"]?.DeepClone(),
            ["createdAt"] = db.Now(), ["updatedAt"] = db.Now(),
        };
        db.Exec("INSERT INTO operations (operation_id, plan_id, body, state, created_at) VALUES ($o, $p, $b, 'prepared', $t)",
            ("$o", operationId), ("$p", planId), ("$b", Json.Write(op)), ("$t", db.Now()));
        Step(db, operationId, "prepared", null);
        SavePlan(db, plan, "running");
        StateEvent(db, op);
        return operationId;
    }

    private static void Step(Db db, string operationId, string state, string? detail)
    {
        var seq = (long)db.Scalar("SELECT COALESCE(MAX(seq), 0) + 1 FROM operation_steps WHERE operation_id = $o", ("$o", operationId))!;
        db.Exec("INSERT INTO operation_steps (operation_id, seq, state, at, detail) VALUES ($o, $s, $st, $t, $d)",
            ("$o", operationId), ("$s", seq), ("$st", state), ("$t", db.Now()), ("$d", detail));
        LogStep(db, operationId, state, detail);
    }

    /// <summary>Operation id → when its last journal step was written (the step's duration in the developer log).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> StepClock = new(StringComparer.Ordinal);

    /// <summary>
    /// One line per journal step: the operation, its kind, the state (verified, completed, failed, indeterminate…), the
    /// detail (an error code or "step n") and the time since its previous step. Failures are Warn with the kind's site.
    /// </summary>
    private static void LogStep(Db db, string operationId, string state, string? detail)
    {
        var failed = state is "failed" or "indeterminate" or "partial" or "rolled_back_failed";
        var dev = AppLog.Current.DevEnabled;
        if (!failed && !dev) return;
        var stepLevel = detail?.StartsWith("step ", StringComparison.Ordinal) == true;
        var ms = "";
        if (dev)
        {
            // The clock runs only while dev.log is on; an operation's final state removes its entry.
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (StepClock.TryGetValue(operationId, out var last))
                ms = $" +{(long)System.Diagnostics.Stopwatch.GetElapsedTime(last, now).TotalMilliseconds}ms";
            if (!stepLevel && state is "verified" or "completed" or "failed" or "indeterminate" or "partial" or "rolled_back_failed" or "cancelled")
                StepClock.TryRemove(operationId, out _);
            else StepClock[operationId] = now;
        }
        var kind = db.Scalar("SELECT body FROM operations WHERE operation_id = $o", ("$o", operationId)) is string body && Json.Parse(body) is JsonObject o ? o["kind"]?.GetValue<string>() : null;
        var (area, site) = kind switch
        {
            FixKind => ("fix", stepLevel ? ErrorSites.FixStepFailed : ErrorSites.FixOpFailed),
            ScriptKind => ("script", ErrorSites.ScriptOpFailed),
            _ => ("action", ErrorSites.ActionOpFailed),
        };
        var line = $"{area}: op {operationId} {state}{(string.IsNullOrEmpty(detail) ? "" : " " + detail)}{ms}";
        if (failed) AppLog.Current.Warn(line, site);
        else AppLog.Current.Debug(line);
    }

    private static JsonObject LoadOperation(Db db, string operationId)
    {
        var body = db.Scalar("SELECT body FROM operations WHERE operation_id = $o", ("$o", operationId)) as string ?? throw RpcError.NotFound("operation", operationId);
        return Json.Parse(body) as JsonObject ?? throw RpcError.Corrupt("operation", operationId);
    }

    private static void SaveOperation(Db db, JsonObject op)
    {
        op["updatedAt"] = db.Now();
        db.Exec("UPDATE operations SET body = $b, state = $s WHERE operation_id = $o", ("$b", Json.Write(op)), ("$s", op.Text("state")), ("$o", op.Text("operationId")));
    }

    /// <summary>operation.state; a fix step transition adds step {n, state} (m9 §4.1). The final event of a fix has no step.</summary>
    private static void StateEvent(Db db, JsonObject op, (int N, string State)? step = null)
    {
        var payload = new JsonObject
        {
            ["operationId"] = op.Text("operationId"), ["planId"] = op.Text("planId"), ["state"] = op.Text("state"),
        };
        if (step is { } s) payload["step"] = new JsonObject { ["n"] = s.N, ["state"] = s.State };
        if (op["receipt"] is JsonObject receipt) payload["receipt"] = receipt.DeepClone();
        if (op["progress"] is JsonValue progress) payload["progress"] = progress.DeepClone();
        if (op["manualUri"] is JsonValue manualUri) payload["manualUri"] = manualUri.DeepClone();
        if (op["settingsPath"] is JsonValue settingsPath) payload["settingsPath"] = settingsPath.DeepClone(); // design v59, beside manualUri
        if (op["result"] is JsonValue result) payload["result"] = result.DeepClone(); // a provider operation's own result name
        db.AppendEvent("user", "operation.state", payload);
    }

    /// <summary>
    /// Runs the handler once (no retry). Settings: prepared → write → attempted → verified | failed | indeterminate.
    /// Provider operations: attempted with progress lines, then the result. Each state commits with its event.
    /// m9 §13 R2: whatever may still be writing (the handler's own task when it had not finished at the deadline, or the
    /// broker session until its process exits) is handed to <paramref name="keep"/> BEFORE the result is recorded, so a
    /// failing finish transaction can't lose it: the caller keeps the plan's resource locks (and lease) until that task
    /// ends, so nothing else starts on those resources while a write may still be landing. Not called when nothing runs.
    /// </summary>
    private async Task RunAsync(IActionHandler handler, ActionExecution execution, string operationId, bool provider, Action<Task> keep, AdminDispatch? admin = null)
    {
        // m9 review R5: whatever happened, a provider operation's pending-stop entry goes with its run.
        try { await RunOnceAsync(handler, execution, operationId, provider, keep, admin).ConfigureAwait(false); }
        finally { if (provider) lock (_gate) _pendingStops.Remove(operationId); }
    }

    /// <summary>
    /// m9 review R5: a single provider operation's stop signal, registered once its run reaches the tool. A Stop asked
    /// before (while queued, or between attempted and here) was kept as a pending stop and is applied now: nothing is
    /// registered and the run ends stopped. True when stopped already.
    /// </summary>
    private bool RegisterStop(string operationId, CancellationTokenSource stop)
    {
        lock (_gate)
        {
            if (_pendingStops.Remove(operationId, out var asked) && asked) { stop.Cancel(); return true; }
            _stopSignals[operationId] = stop;
            return false;
        }
    }

    /// <summary>A provider run stopped before its tool started: ends as a normal stop (a tool report names its result "stopped").</summary>
    private static ActionOutcome StoppedBeforeStart(IActionHandler handler) =>
        Stopped(new ActionOutcome("failed", handler.ReportsOnly ? new JsonObject { ["result"] = "stopped" } : [], "Stopped before it started.", "cancelled", "Stopped."));

    private async Task RunOnceAsync(IActionHandler handler, ActionExecution execution, string operationId, bool provider, Action<Task> keep, AdminDispatch? admin)
    {
        if (provider && RunSeam is { } queued) await queued("queued", operationId).ConfigureAwait(false);
        if (provider)
            _store.Write(db =>
            {
                var op = LoadOperation(db, operationId);
                op["state"] = "attempted";
                Step(db, operationId, "attempted", null);
                SaveOperation(db, op);
                StateEvent(db, op);
            });

        var progress = new Progress(this, operationId);
        ActionOutcome outcome;
        string? brokerReceiptId = null;
        Task<ActionOutcome>? work = null;
        Task? pendingBroker = null;
        try
        {
            if (admin is { } elevated)
            {
                // m9 §3.5: every administrator write goes through one broker session (here: one step, n = 1). A long
                // provider tool (a repair) streams progress, and operation.stop becomes the broker's stop frame (it kills the tool).
                using var stop = new CancellationTokenSource();
                if (provider && RunSeam is { } registering) await registering("registering", operationId).ConfigureAwait(false);
                if (provider && RegisterStop(operationId, stop))
                {
                    outcome = StoppedBeforeStart(handler); // stopped before the broker was asked: no UAC prompt, no tool
                }
                else
                {
                    progress.Report("Waiting for Windows permission");
                    ElevatedResult result;
                    try
                    {
                        result = await ElevateAsync(execution, [(1, handler, elevated.Step)], stop.Token,
                            provider ? new StepProgress(progress) : null, t => pendingBroker = t).ConfigureAwait(false);
                    }
                    finally { if (provider) lock (_gate) _stopSignals.Remove(operationId); }
                    brokerReceiptId = result.ReceiptId;
                    outcome = result.Steps.FirstOrDefault(s => s.N == 1).Outcome
                        ?? new ActionOutcome(result.Dispatched ? "indeterminate" : "failed", [], "The result could not be confirmed.", "bad_response");
                    if (outcome.State == "skipped") outcome = outcome with { State = "failed" };
                }
            }
            else
            {
                // A long provider tool (a Defender scan or update) can be stopped by operation.stop: its run is cancelled (the
                // handler kills the tool's process tree) and the operation ends cancelled once the handler has returned.
                using var stop = new CancellationTokenSource();
                if (provider && RunSeam is { } registering) await registering("registering", operationId).ConfigureAwait(false);
                if (provider && RegisterStop(operationId, stop))
                {
                    outcome = StoppedBeforeStart(handler); // stopped before the tool started: it never runs
                }
                else try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, stop.Token);
                    deadline.CancelAfter(provider ? _options.ProviderTimeout : _options.SettingTimeout);
                    work = handler.ExecuteAsync(execution, provider ? progress : null, deadline.Token);
                    try { outcome = await work.WaitAsync(deadline.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested)
                    {
                        // Give the handler a moment to end its tool; if it does not, its task keeps the plan's locks (Straggler).
                        try { await work.WaitAsync(StopGrace).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
                        outcome = new ActionOutcome("indeterminate", [], "Stopped.", "cancelled", "Stopped.");
                    }
                }
                finally { if (provider) lock (_gate) _stopSignals.Remove(operationId); }
                if (stop.IsCancellationRequested) outcome = Stopped(outcome);
            }
            if (admin is not null && provider && outcome.Code == "stopped") outcome = Stopped(outcome);
        }
        catch (OperationCanceledException)
        {
            outcome = new ActionOutcome("indeterminate", [], "The result could not be confirmed in time.", "timeout", "No answer in time.");
        }
        catch (Exception ex)
        {
            AppLog.Current.Warn($"action: {handler.ActionId}: execute failed ({ex.GetType().Name})", ErrorSites.ActionExecuteFailed);
            outcome = new ActionOutcome("indeterminate", [], "The result could not be confirmed.", "error", ex.GetType().Name);
        }
        if (Both(pendingBroker, Straggler(work, handler.ActionId)) is { } stillWriting) keep(stillWriting);
        _store.Write(db => Finish(db, handler, operationId, outcome, provider, brokerReceiptId));
    }

    private static Task? Both(Task? a, Task? b) => a is null ? b : b is null ? a : Task.WhenAll(a, b);

    /// <summary>
    /// Waits for a write that may still be landing before its locks and lease are released (m9 §13 R2). Ends early only when
    /// the host stops: the in-memory locks go with the process, and restart reconcile classifies what was interrupted.
    /// </summary>
    private async Task HoldAsync(Task stillWriting)
    {
        try { await stillWriting.WaitAsync(_stopping.Token).ConfigureAwait(false); }
        catch (Exception) { /* it ended with a fault (already logged), or the host is stopping */ }
    }

    /// <summary>How long a stopped provider handler gets to end its tool before the operation is closed (its locks wait for it).</summary>
    internal static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(10);

    /// <summary>
    /// operation.stop on a single long provider operation: unless the tool had already finished, the operation ends failed
    /// with error code cancelled and the plan cancelled (the fixture's shape); what the tool already did stays.
    /// </summary>
    private static ActionOutcome Stopped(ActionOutcome outcome) => outcome.State is "verified" or "completed"
        ? outcome
        : new ActionOutcome("cancelled", outcome.Observed, "Stopped. What it already did stays.", "cancelled", "Stopped.");

    /// <summary>A handler task still running after its deadline (observed, so its eventual fault is only logged), or null.</summary>
    private Task? Straggler(Task? work, string actionId)
    {
        if (work is null || work.IsCompleted) return null;
        AppLog.Current.Warn($"action: {actionId}: still running after its deadline; its resources stay locked until it ends", ErrorSites.ActionStraggler);
        return work.ContinueWith(t => AppLog.Current.Info($"action: {actionId}: ended after its deadline{(t.IsFaulted ? $" ({t.Exception?.InnerException?.GetType().Name})" : "")}", ErrorSites.ActionStraggler),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>An administrator step as it goes to the broker: the approved fields and, for an undo, the original's record.</summary>
    internal sealed record AdminDispatch(ElevatedStep Step);

    /// <summary>The broker step for an approved single plan or fix step (undoOf names the broker's own record of the original).</summary>
    private static ElevatedStep ElevatedStepOf(int n, IActionHandler h, JsonObject target, JsonObject before, JsonObject after, JsonObject? parameters,
        ElevatedUndoOf? undoOf) =>
        new(n, h.ActionId, h.Version, (JsonObject)target.DeepClone(), (JsonObject)before.DeepClone(), (JsonObject)after.DeepClone(),
            parameters?.DeepClone() as JsonObject, undoOf, Long: h.Kind == "provider" || h.Flags.Contains(ActionFlags.Long), Deadline: h.LongDeadline);

    /// <summary>The broker's progress lines of the one step become the operation's progress.</summary>
    private sealed class StepProgress(IProgress<string> inner) : IProgress<(int N, string Text)>
    {
        public void Report((int N, string Text) value) => inner.Report(value.Text);
    }

    /// <summary>The longest wait for one broker session: the configured one, or more when a long tool's own deadline needs it.</summary>
    private TimeSpan ElevationWait(IEnumerable<ElevatedStep> steps)
    {
        var tools = steps.Aggregate(TimeSpan.Zero, (sum, s) => sum + (s.Deadline ?? TimeSpan.Zero));
        var needed = TimeSpan.FromMinutes(5) + tools;
        return needed > _options.ElevationTimeout ? needed : _options.ElevationTimeout;
    }

    /// <summary>The broker's record of an operation's step (single operations: n = 1), for the undo that names it.</summary>
    private static ElevatedUndoOf? BrokerRecordOf(Db db, string operationId, int? n)
    {
        if (db.Scalar("SELECT 1 FROM operations WHERE operation_id = $o", ("$o", operationId)) is null) return null;
        if (n is { } step)
        {
            var row = LoadStepRows(db, operationId).FirstOrDefault(r => r["n"]!.GetValue<int>() == step);
            return row?["receipt"]?["brokerReceiptId"]?.GetValue<string>() is { } rid ? new ElevatedUndoOf(rid, step) : null;
        }
        return LoadOperation(db, operationId)["receipt"]?["brokerReceiptId"]?.GetValue<string>() is { } id ? new ElevatedUndoOf(id, 1) : null;
    }

    /// <summary>
    /// One broker session for these administrator steps (m9 §3.5): one UAC prompt, one broker window, the plan's order.
    /// Without a broker nothing runs (failed, broker_missing). <paramref name="stop"/> becomes the broker's stop frame.
    /// <paramref name="straggler"/> receives the session's end whenever it is still pending (m9 §13 R2: on a clean answer
    /// until the broker process exits; on our own deadline until the call returns and then its process exits).
    /// </summary>
    private async Task<ElevatedResult> ElevateAsync(ActionExecution plan, IReadOnlyList<(int N, IActionHandler Handler, ElevatedStep Step)> steps, CancellationToken stop,
        IProgress<(int N, string Text)>? progress = null, Action<Task>? straggler = null)
    {
        if (_options.Elevation is not { } elevation)
            return new ElevatedResult([.. steps.Select(s => (s.N, new ActionOutcome("failed", new JsonObject(), "Nothing changed.", "broker_missing",
                "Administrator changes aren't available in this version.")))], null, false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        deadline.CancelAfter(ElevationWaitSeam ?? ElevationWait(steps.Select(s => s.Step)));
        Task<ElevatedResult>? run = null;
        try
        {
            run = elevation.RunAsync(new ElevatedRequest(plan.PlanId, plan.PlanHash, [.. steps.Select(s => s.Step)]) { Progress = progress }, stop, deadline.Token);
            var result = await run.WaitAsync(deadline.Token).ConfigureAwait(false);
            if (result.Ended is { IsCompleted: false } ended) straggler?.Invoke(ended);
            return result;
        }
        catch (OperationCanceledException)
        {
            // The broker session may still be writing: whoever holds the steps' locks keeps them until it ends, i.e. until
            // the call returns AND the process it launched has exited. The call may already have completed while the deadline
            // won (m9 review S1): its Ended still counts, so the session's end is taken whether or not the call completed.
            if (ElevationCancelledSeam is { } seam) await seam(plan.PlanId).ConfigureAwait(false);
            if (run is not null && SessionEndAsync(run) is { IsCompleted: false } end)
            {
                AppLog.Current.Warn("action: broker session still running after its deadline; its resources stay locked until it ends", ErrorSites.ActionStraggler);
                straggler?.Invoke(end);
            }
            return new ElevatedResult([.. steps.Select(s => (s.N, new ActionOutcome("indeterminate", new JsonObject(), "The result could not be confirmed in time.",
                "timeout", "No answer in time.")))], null, true);
        }
    }

    /// <summary>A broker call we stopped waiting for: it ends when the call returns and then the process it launched has exited.</summary>
    private static async Task SessionEndAsync(Task<ElevatedResult> run)
    {
        ElevatedResult result;
        try { result = await run.ConfigureAwait(false); }
        catch (Exception) { return; } // the call itself failed: nothing of it is still running
        if (result.Ended is { } ended) await ended.ConfigureAwait(false);
    }

    /// <summary>A handler's progress line becomes an operation.state event (state attempted).</summary>
    private sealed class Progress(ActionService owner, string operationId) : IProgress<string>
    {
        public void Report(string value)
        {
            try
            {
                owner._store.Write(db =>
                {
                    var op = LoadOperation(db, operationId);
                    op["progress"] = Agent.AgentService.Clean(value);
                    SaveOperation(db, op);
                    StateEvent(db, op);
                });
            }
            catch (Exception ex) { AppLog.Current.Debug($"action: operation {operationId} progress: {ex.GetType().Name}"); }
        }
    }

    private void Finish(Db db, IActionHandler handler, string operationId, ActionOutcome outcome, bool provider, string? brokerReceiptId = null)
    {
        var op = LoadOperation(db, operationId);
        var plan = LoadPlan(db, op.Text("planId"));
        if (!provider) Step(db, operationId, "attempted", null);

        // Provider operations end "completed" on success (Observed.result: updated | already_current | no_threats | threats_found | threats_unknown).
        // A handler that only relays a tool's report (repairs, DNS flush) ends "completed": never verified (m9 §3.8).
        var (state, planStatus) = outcome.State switch
        {
            "completed" when handler.ReportsOnly => ("completed", "completed"),
            "verified" or "completed" => ("verified", "verified"),
            "failed" => ("failed", "failed"),
            "declined" => ("failed", "declined"),
            "cancelled" => ("failed", "cancelled"),
            _ => ("indeterminate", "indeterminate"),
        };
        if (outcome.Observed["manualUri"]?.GetValue<string>() is { } manualUri) { op["manualUri"] = manualUri; Paths.Add(op, manualUri); } // a failure's manual route (and its path)
        if (outcome.Observed["result"]?.GetValue<string>() is { } providerResult) op["result"] = providerResult;
        else if (handler.ReportsOnly) op["result"] = "failed"; // a tool report (repairs, DNS flush) always names its result
        op["state"] = state;
        op["progress"] = null;
        if (state == "verified")
            op["receipt"] = new JsonObject
            {
                ["receiptId"] = "rcpt-" + Ids.RandomHex(), ["verifiedAt"] = db.Now(), ["observed"] = outcome.Observed.DeepClone(),
                ["text"] = outcome.ReceiptText, ["benefit"] = "unmeasured",
            };
        else if (state == "completed")
            op["receipt"] = new JsonObject // what the tool reported, when it finished; not a verified change
            {
                ["receiptId"] = "rcpt-" + Ids.RandomHex(), ["completedAt"] = db.Now(), ["observed"] = outcome.Observed.DeepClone(),
                ["text"] = outcome.ReceiptText, ["benefit"] = "unmeasured",
            };
        // m9 §3.5: the broker's protected record of an administrator write; its undo names it (undoOf {receiptId, n}).
        if (state == "verified" && brokerReceiptId is not null) op["receipt"]!["brokerReceiptId"] = brokerReceiptId;
        op["error"] = state is "verified" or "completed" ? null : new JsonObject
        {
            ["code"] = outcome.State == "declined" ? outcome.Code ?? "uac_cancelled" : outcome.Code ?? outcome.State,
            ["message"] = outcome.Message ?? outcome.ReceiptText,
        };
        if (state is not ("verified" or "completed")) op["observed"] = outcome.Observed.DeepClone();
        // A plan whose resolution withdrew undo (no execution fingerprint, m9 §3.2) is not undoable either.
        var undoable = state == "verified" && handler.SupportsUndo && !provider && plan["undo"]?["supported"]?.GetValue<bool>() != false;
        op["undo"] = undoable ? new JsonObject { ["available"] = true, ["reason"] = null }
            : new JsonObject { ["available"] = false, ["reason"] = state is "verified" or "completed" ? "not_supported" : "not_verified" };
        Step(db, operationId, state, op["error"]?["code"]?.GetValue<string>());
        SaveOperation(db, op);
        SavePlan(db, plan, planStatus, planStatus == "declined" ? new JsonObject { ["code"] = op["error"]!.Text("code"), ["message"] = "Approval was cancelled in Windows." } : null);
        StateEvent(db, op);

        // A verified change shows as a decision on its card (m9 §17.3: plan.cardId, else the finding's), with undo availability.
        if (state == "verified") CloseCard(db, plan, operationId, undoable);
    }

    // ---------------------------------------------------------------- operations

    internal JsonObject? OperationOfPlan(Db db, string planId) =>
        db.Scalar("SELECT operation_id FROM operations WHERE plan_id = $p", ("$p", planId)) is string id ? OperationView(db, id) : null;

    private static JsonObject OperationView(Db db, string operationId)
    {
        var op = (JsonObject)LoadOperation(db, operationId).DeepClone();
        op.Remove("preimage");
        if (op.Text("kind") == FixKind) return FixOperationView(db, op);
        if (op.Text("kind") == ScriptKind) return op; // m9 §5.7: steps null; its own lifecycle, receipt and options
        op["steps"] = new JsonArray(db.Query("SELECT state, at, detail FROM operation_steps WHERE operation_id = $o ORDER BY seq",
            r => (JsonNode?)new JsonObject { ["state"] = r.GetString(0), ["at"] = r.GetString(1), ["detail"] = r.IsDBNull(2) ? null : r.GetString(2) },
            ("$o", operationId)).ToArray());
        return op;
    }

    public JsonNode OperationGet(JsonObject p)
    {
        var operationId = P.Req(p, "operationId", 64);
        return _store.Read(db => (JsonNode)OperationView(db, operationId));
    }

    public JsonNode OperationList(JsonObject p)
    {
        var limit = P.Int(p, "limit", required: false) ?? 20;
        if (limit is < 1 or > 50) throw RpcError.Params("limit", "limit must be 1 to 50.");
        var before = P.Str(p, "before", required: false, maxLength: 64);
        return _store.Read(db =>
        {
            long? beforeRow = null;
            if (before is not null)
                beforeRow = db.Scalar("SELECT rowid FROM operations WHERE operation_id = $o", ("$o", before)) as long? ?? throw RpcError.NotFound("operation", before);
            var ids = db.Query("SELECT operation_id FROM operations WHERE ($b IS NULL OR rowid < $b) ORDER BY rowid DESC LIMIT $l",
                r => r.GetString(0), ("$b", beforeRow), ("$l", limit + 1));
            return (JsonNode)new JsonObject
            {
                ["operations"] = new JsonArray(ids.Take((int)limit).Select(id => (JsonNode?)OperationView(db, id)).ToArray()),
                ["next"] = ids.Count > limit ? ids[(int)limit - 1] : null,
            };
        });
    }

    // ---------------------------------------------------------------- undo

    /// <summary>
    /// action.undo: a new plan restoring the operation's preimage, with its own approval. Only for a verified,
    /// undoable operation whose target still holds exactly the value we observed after the write (else changed_since).
    /// </summary>
    public async Task<JsonNode> UndoAsync(JsonObject p)
    {
        var key = P.IdempotencyKey(p);
        var operationId = P.Req(p, "operationId", 64);
        var wanted = UndoSteps(p);
        return await IdempotentAsync("action.undo", key, p, async () =>
        {
            AppLog.Current.Debug($"action: undo requested op={operationId}{(wanted is null ? "" : " steps=" + wanted.Count)}");
            var (op, plan) = _store.Read(db =>
            {
                var o = LoadOperation(db, operationId);
                return (o, LoadPlan(db, o.Text("planId")));
            });
            // m9 §4: a fix is undone as a new fix plan of its verified, undoable steps (or the ones named in steps).
            if (IsScript(plan)) throw new RpcError(ErrorCodes.InvalidState, "A script can't be undone.", new JsonObject { ["reason"] = "not_undoable" });
            if (IsFix(plan)) return await UndoFixAsync(op, plan, wanted).ConfigureAwait(false);
            if (wanted is not null) throw RpcError.Params("steps", "steps applies to a multi-step fix only.");
            var handler = Handler(plan.Text("actionId")) ?? throw RpcError.State("unavailable", "That action is not available in this build.");
            var findingId = plan["findingId"]?.GetValue<string>();
            var manualUri = plan["ineligible"]?["manualUri"]?.GetValue<string>();

            Task<JsonObject> Ineligible(string code, string message)
            {
                AppLog.Current.Info($"action: undo of {operationId} ineligible reason={code}");
                return Task.FromResult(_store.Write(db => InsertPlan(db, handler,
                ActionResolution.Ineligible(code, message, manualUri), null, findingId is null ? null : FindFinding(db, findingId), RequestedBy.User, operationId)));
            }

            if (op.Text("state") != "verified") return await Ineligible("not_verified", "Only a confirmed change can be undone.").ConfigureAwait(false);
            if (!handler.SupportsUndo || plan.Text("kind") == "provider" || plan["undo"]?["supported"]?.GetValue<bool>() == false) return await Ineligible("not_supported", "This change cannot be undone.").ConfigureAwait(false);

            JsonObject current;
            try
            {
                using var cts = new CancellationTokenSource(_options.ResolveTimeout);
                current = await handler.ReadTargetAsync(plan["target"]!.DeepClone().AsObject(), cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not RpcError)
            {
                return await Ineligible("unavailable", "The current state could not be read.").ConfigureAwait(false);
            }
            if (!Same(current, op["receipt"]?["observed"]))
                return await Ineligible("changed_since", "The setting changed since this change was made.").ConfigureAwait(false);

            var request = handler.UndoRequest(plan["target"]!.DeepClone().AsObject(), plan["before"]!.DeepClone().AsObject());
            if (request is null) return await Ineligible("not_supported", "This change cannot be undone.").ConfigureAwait(false);
            request = request with { Undo = true }; // engine-set: the handler may restore the recorded preimage
            return await CreatePlanAsync(request.ActionId, request.Target, request.Parameters, findingId, RequestedBy.User, operationId, request).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- restart reconcile (§5)

    /// <summary>
    /// For each prepared/attempted operation: live == before → failed (no change observed); live == after → verified
    /// (authorship uncertain after interruption); otherwise indeterminate. Never repeats a write.
    /// </summary>
    internal async Task ReconcileAsync()
    {
        ReconcileScripts(); // m9 §5.4: never replayed; indeterminate or not_run, never through the setting journal below
        var pending = _store.Read(db => db.Query("SELECT operation_id FROM operations WHERE state IN ('prepared','attempted')", r => r.GetString(0)));
        foreach (var operationId in pending)
        {
            var kind = _store.Read(db => LoadOperation(db, operationId)).Text("kind");
            if (kind == ScriptKind) continue;
            if (kind == FixKind)
            {
                await ReconcileFixAsync(operationId).ConfigureAwait(false);
                continue;
            }
            var (op, plan) = _store.Read(db =>
            {
                var o = LoadOperation(db, operationId);
                return (o, LoadPlan(db, o.Text("planId")));
            });
            var handler = Handler(plan.Text("actionId"));
            JsonObject? live = null;
            if (handler is not null)
                try
                {
                    using var cts = new CancellationTokenSource(_options.ResolveTimeout);
                    live = await handler.ReadTargetAsync(plan["target"]!.DeepClone().AsObject(), cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) { AppLog.Current.Warn($"action: reconcile {operationId}: {ex.GetType().Name}", ErrorSites.ActionReconcile); }

            ActionOutcome outcome = live is null
                ? new ActionOutcome("indeterminate", [], "The result could not be confirmed after a restart.", "interrupted", "The app stopped during the change.")
                : Same(live, plan["after"]) ? new ActionOutcome("verified", live, "Confirmed after a restart. Authorship uncertain after interruption.")
                : Same(live, plan["before"]) ? new ActionOutcome("failed", live, "No change was observed after a restart.", "interrupted", "No change observed.")
                : new ActionOutcome("indeterminate", live, "The setting has another value after a restart.", "interrupted", "Unexpected value.");
            var provider = plan.Text("kind") == "provider";
            if (provider && live is null) outcome = outcome with { State = "indeterminate" };
            string? brokerReceiptId = null;
            if (plan.Text("privilege") == "admin")
            {
                // m9 §3.5: a step dispatched to the broker is classified from its protected journal and live state.
                var journal = _options.Elevation?.Journal(plan.Text("planId"));
                var record = journal?.Steps.FirstOrDefault(s => s.N == 1);
                brokerReceiptId = journal?.ReceiptId;
                if (journal is not null && record is not { Prepared: true })
                    outcome = new ActionOutcome("failed", live ?? [], "No change was made after a restart.", "interrupted", "The administrator helper never started the change.");
                else if (provider && record?.State is "completed" or "verified")
                    outcome = new ActionOutcome("completed", record.Observed?.DeepClone() as JsonObject ?? [], "Finished before the restart (from the administrator journal).");
                else if (provider && record?.State is "failed")
                    outcome = new ActionOutcome("failed", record.Observed?.DeepClone() as JsonObject ?? [], "It failed before the restart.", "interrupted", "Failed.");
            }
            _store.Write(db =>
            {
                Finish(db, handler ?? new MissingHandler(plan.Text("actionId")), operationId, outcome, provider: true, brokerReceiptId);
                var reconciled = LoadOperation(db, operationId);
                reconciled["reconciled"] = true;
                if (outcome.State == "verified") reconciled["note"] = "authorship uncertain after interruption";
                SaveOperation(db, reconciled);
            });
            AppLog.Current.Info($"action: reconciled {operationId}: {outcome.State}");
        }
        // Plans approved but never prepared (host stopped between approve and prepare) cannot have written anything.
        _store.Write(db =>
        {
            foreach (var planId in db.Query("SELECT plan_id FROM action_plans WHERE status = 'approved'", r => r.GetString(0)))
                if (db.Scalar("SELECT 1 FROM operations WHERE plan_id = $p", ("$p", planId)) is null)
                    SavePlan(db, LoadPlan(db, planId), "cancelled", new JsonObject { ["code"] = "interrupted", ["message"] = "The app stopped before the change." });
        });
    }

    private sealed class MissingHandler(string actionId) : IActionHandler
    {
        public string ActionId => actionId;
        public string Version => "missing";
        public string Label => actionId;
        public string Privilege => "none";
        public bool SupportsUndo => false;
        public string Kind => "setting";
        public string Group => "other";
        public string What => "Not available in this build.";
        public IReadOnlyList<string> Flags => [];
        public JsonObject? ParameterSchema => null;
        public bool TakesLease => false;
        public Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct) => Task.FromResult(ActionResolution.Ineligible("unavailable", "Unavailable."));
        public Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct) => Task.FromResult(new JsonObject());
        public Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct) => throw new InvalidOperationException();
        public ActionRequest? UndoRequest(JsonObject target, JsonObject before) => null;
    }

    // ---------------------------------------------------------------- manual tasks

    /// <summary>
    /// manual.open: a plan that only names the settings page; the task waits for a re-check (awaiting_rescan). Design v60:
    /// a finding with no Settings page whose card has guide lines (steps done elsewhere, e.g. the BIOS) gets a plan with no
    /// page: target {type: manual, id: the finding ID, label: its title}, no rows, lock key manual:&lt;findingId&gt;.
    /// Neither a page nor guide lines → no_settings.
    /// </summary>
    private JsonObject CreateManual((string ReportId, JsonObject Report, JsonObject Finding)? found, string? findingId, RequestedBy by)
    {
        if (found is not { } f) throw RpcError.Params("findingId", "manual.open needs a findingId.");
        var uri = f.Finding["settingsUri"]?.GetValue<string>();
        if (uri is null && !_store.Read(db => Followups.HasGuide(db, f.ReportId, f.Finding.Text("id"))))
            throw RpcError.State("no_settings", "That finding has no settings page.");
        return _store.Write(db =>
        {
            var handler = new ManualHandler(page: uri is not null);
            var resolution = uri is not null
                // Design v59: the page's English path in the target, on English Windows only.
                ? new ActionResolution(true, Paths.Add(new JsonObject { ["type"] = "settings", ["id"] = uri, ["label"] = f.Finding.Text("title") }, uri),
                    [], [], [new PlanRow("Opens", "", uri)], [], [], null)
                : new ActionResolution(true, new JsonObject { ["type"] = "manual", ["id"] = f.Finding.Text("id"), ["label"] = f.Finding.Text("title") },
                    [], [], [], [], [], null);
            var plan = InsertPlan(db, handler, resolution, null, found, by, null);
            db.Exec("INSERT INTO manual_tasks (finding_id, report_id, status, updated_at) VALUES ($f, $r, 'awaiting_rescan', $t) ON CONFLICT (finding_id) DO UPDATE SET status = 'awaiting_rescan', updated_at = $t",
                ("$f", findingId), ("$r", f.ReportId), ("$t", db.Now()));
            return plan;
        });
    }

    /// <summary>Design v63: its `what` (also in the native approval window) says whether a Windows page opens.</summary>
    private sealed class ManualHandler(bool page) : IActionHandler
    {
        public const string GroupName = "guided";
        public const string WhatText = "Opens the right page in Windows Settings, where you make the change yourself. Optimus checks it afterwards.";
        public const string NoPageWhatText = "You make this change yourself, outside Windows Settings. Optimus checks it afterwards.";
        public string ActionId => ManualOpen;
        public const string HandlerVersion = "1.1.0";
        public string Version => HandlerVersion;
        public string Label => "Open settings";
        public string Privilege => "none";
        public bool SupportsUndo => false;
        public string Kind => "manual";
        public string Group => GroupName;
        public string What => page ? WhatText : NoPageWhatText;
        public IReadOnlyList<string> Flags => [];
        public JsonObject? ParameterSchema => null;
        public bool TakesLease => false;
        public Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct) => throw new InvalidOperationException();
        public Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct) => Task.FromResult(new JsonObject());
        public Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct) => throw new InvalidOperationException();
        public ActionRequest? UndoRequest(JsonObject target, JsonObject before) => null;
    }

    /// <summary>
    /// manual.recheck: re-runs the collectors the finding's rule reads and re-evaluates that rule. The finding gone
    /// (rule clear, or no longer this fingerprint) → verified_by_rescan; still there or not evaluable → awaiting_rescan.
    /// </summary>
    public async Task<JsonNode> ManualRecheckAsync(JsonObject p)
    {
        var findingId = P.Req(p, "findingId", 64);
        var (reportId, report, finding) = _store.Read(db => FindFinding(db, findingId)) ?? throw RpcError.NotFound("finding", findingId);
        var ruleId = finding.Text("ruleId");
        var rule = _catalog.Rules.FirstOrDefault(r => r.Id == ruleId) ?? throw RpcError.State("unavailable", "That check is not available in this build.");

        var coverage = new Dictionary<string, string>();
        var items = new List<EvidenceItem>();
        var observedAt = Now();
        foreach (var collector in rule.Inputs.Select(id => _catalog.Find(id)).OfType<ICollector>())
        {
            var (facts, receipt) = await ScanCoordinator.CollectAsync(collector, _stopping.Token).ConfigureAwait(false);
            coverage[collector.Id] = receipt.Status;
            items.AddRange(facts.Select(f => new EvidenceItem("ev-" + Ids.Hash16("recheck", findingId, collector.Id, f.Subject), f.Subject, collector.Id,
                collector.Category, f.Value.DeepClone(), f.Display, observedAt)));
        }
        RuleResult result;
        try
        {
            var profile = _store.Read(db => OnboardingService.Read(db).Profile);
            result = rule.Evaluate(new RuleContext(items, coverage, profile));
        }
        catch (Exception ex)
        {
            AppLog.Current.Warn($"action: recheck {ruleId}: {ex.GetType().Name}", ErrorSites.ActionRecheck);
            result = RuleResult.Insufficient("Rule error.");
        }
        var stillThere = result.Status == "finding" && result.Findings.Any(d => Ids.Fingerprint(ruleId, d.Subject) == finding.Text("fingerprint"));
        var status = !stillThere && result.Status is "clear" or "finding" or "inapplicable" ? "verified_by_rescan" : "awaiting_rescan";
        _ = report;
        return _store.Write(db =>
        {
            db.Exec("INSERT INTO manual_tasks (finding_id, report_id, status, updated_at) VALUES ($f, $r, $s, $t) ON CONFLICT (finding_id) DO UPDATE SET status = $s, updated_at = $t",
                ("$f", findingId), ("$r", reportId), ("$s", status), ("$t", db.Now()));
            if (status == "verified_by_rescan") Followups.MarkVerified(db, reportId, findingId, null, false);
            return (JsonNode)new JsonObject
            {
                ["findingId"] = findingId, ["status"] = status, ["ruleStatus"] = result.Status, ["checkedAt"] = db.Now(),
                ["evidenceIds"] = Json.Array(items.Select(i => i.Id)),
            };
        });
    }
}

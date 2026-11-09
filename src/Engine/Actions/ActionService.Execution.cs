using System.Text.Json.Nodes;

namespace OptimizationTool.Engine.Actions;

// Journal, execution, receipts, operations, undo, restart reconcile and manual tasks (contracts/m4-actions.md §3–§5).
public sealed partial class ActionService
{
    public static readonly string[] OperationStates = ["prepared", "attempted", "verified", "failed", "indeterminate", "rolled_back_failed"];

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

    private static void StateEvent(Db db, JsonObject op)
    {
        var payload = new JsonObject
        {
            ["operationId"] = op.Text("operationId"), ["planId"] = op.Text("planId"), ["state"] = op.Text("state"),
        };
        if (op["receipt"] is JsonObject receipt) payload["receipt"] = receipt.DeepClone();
        if (op["progress"] is JsonValue progress) payload["progress"] = progress.DeepClone();
        if (op["manualUri"] is JsonValue manualUri) payload["manualUri"] = manualUri.DeepClone();
        db.AppendEvent("user", "operation.state", payload);
    }

    /// <summary>
    /// Runs the handler once (no retry). Settings: prepared → write → attempted → verified | failed | indeterminate.
    /// Provider operations: attempted with progress lines, then the result. Each state commits with its event.
    /// </summary>
    private async Task RunAsync(IActionHandler handler, ActionExecution execution, string operationId, bool provider)
    {
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
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            deadline.CancelAfter(provider ? _options.ProviderTimeout : _options.SettingTimeout);
            outcome = await handler.ExecuteAsync(execution, provider ? progress : null, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            outcome = new ActionOutcome("indeterminate", [], "The result could not be confirmed in time.", "timeout", "No answer in time.");
        }
        catch (Exception ex)
        {
            _log($"action {handler.ActionId}: execute failed ({ex.GetType().Name})");
            outcome = new ActionOutcome("indeterminate", [], "The result could not be confirmed.", "error", ex.GetType().Name);
        }
        _store.Write(db => Finish(db, handler, operationId, outcome, provider));
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
            catch (Exception ex) { owner._log($"operation {operationId} progress: {ex.GetType().Name}"); }
        }
    }

    private void Finish(Db db, IActionHandler handler, string operationId, ActionOutcome outcome, bool provider)
    {
        var op = LoadOperation(db, operationId);
        var plan = LoadPlan(db, op.Text("planId"));
        if (!provider) Step(db, operationId, "attempted", null);

        // Provider operations end "completed" on success (Observed.result: updated | already_current | no_threats | threats_found | threats_unknown).
        var (state, planStatus) = outcome.State switch
        {
            "verified" or "completed" => ("verified", "verified"),
            "failed" => ("failed", "failed"),
            "declined" => ("failed", "declined"),
            "cancelled" => ("failed", "cancelled"),
            _ => ("indeterminate", "indeterminate"),
        };
        if (outcome.Observed["manualUri"]?.GetValue<string>() is { } manualUri) op["manualUri"] = manualUri; // a failure's manual route
        if (outcome.Observed["result"]?.GetValue<string>() is { } providerResult) op["result"] = providerResult;
        op["state"] = state;
        op["progress"] = null;
        if (state == "verified")
            op["receipt"] = new JsonObject
            {
                ["receiptId"] = "rcpt-" + Ids.RandomHex(), ["verifiedAt"] = db.Now(), ["observed"] = outcome.Observed.DeepClone(),
                ["text"] = outcome.ReceiptText, ["benefit"] = "unmeasured",
            };
        op["error"] = state == "verified" ? null : new JsonObject
        {
            ["code"] = outcome.State == "declined" ? outcome.Code ?? "uac_cancelled" : outcome.Code ?? outcome.State,
            ["message"] = outcome.Message ?? outcome.ReceiptText,
        };
        if (state != "verified") op["observed"] = outcome.Observed.DeepClone();
        var undoable = state == "verified" && handler.SupportsUndo && !provider;
        op["undo"] = undoable ? new JsonObject { ["available"] = true, ["reason"] = null }
            : new JsonObject { ["available"] = false, ["reason"] = state == "verified" ? "not_supported" : "not_verified" };
        Step(db, operationId, state, op["error"]?["code"]?.GetValue<string>());
        SaveOperation(db, op);
        SavePlan(db, plan, planStatus, planStatus == "declined" ? new JsonObject { ["code"] = op["error"]!.Text("code"), ["message"] = "Approval was cancelled in Windows." } : null);
        StateEvent(db, op);

        // A verified change shows as a decision on the finding's card, with undo availability.
        if (state == "verified" && plan["reportId"]?.GetValue<string>() is { } reportId && plan["findingId"]?.GetValue<string>() is { } findingId)
            Followups.MarkVerified(db, reportId, findingId, operationId, undoable);
    }

    // ---------------------------------------------------------------- operations

    internal JsonObject? OperationOfPlan(Db db, string planId) =>
        db.Scalar("SELECT operation_id FROM operations WHERE plan_id = $p", ("$p", planId)) is string id ? OperationView(db, id) : null;

    private static JsonObject OperationView(Db db, string operationId)
    {
        var op = (JsonObject)LoadOperation(db, operationId).DeepClone();
        op.Remove("preimage");
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
        return await IdempotentAsync("action.undo", key, p, async () =>
        {
            var (op, plan) = _store.Read(db =>
            {
                var o = LoadOperation(db, operationId);
                return (o, LoadPlan(db, o.Text("planId")));
            });
            var handler = Handler(plan.Text("actionId")) ?? throw RpcError.State("unavailable", "That action is not available in this build.");
            var findingId = plan["findingId"]?.GetValue<string>();
            var manualUri = plan["ineligible"]?["manualUri"]?.GetValue<string>();

            Task<JsonObject> Ineligible(string code, string message) => Task.FromResult(_store.Write(db => InsertPlan(db, handler,
                ActionResolution.Ineligible(code, message, manualUri), null, findingId is null ? null : FindFinding(db, findingId), RequestedBy.User, operationId)));

            if (op.Text("state") != "verified") return await Ineligible("not_verified", "Only a confirmed change can be undone.").ConfigureAwait(false);
            if (!handler.SupportsUndo || plan.Text("kind") == "provider") return await Ineligible("not_supported", "This change cannot be undone.").ConfigureAwait(false);

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
        var pending = _store.Read(db => db.Query("SELECT operation_id FROM operations WHERE state IN ('prepared','attempted')", r => r.GetString(0)));
        foreach (var operationId in pending)
        {
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
                catch (Exception ex) { _log($"reconcile {operationId}: {ex.GetType().Name}"); }

            ActionOutcome outcome = live is null
                ? new ActionOutcome("indeterminate", [], "The result could not be confirmed after a restart.", "interrupted", "The app stopped during the change.")
                : Same(live, plan["after"]) ? new ActionOutcome("verified", live, "Confirmed after a restart. Authorship uncertain after interruption.")
                : Same(live, plan["before"]) ? new ActionOutcome("failed", live, "No change was observed after a restart.", "interrupted", "No change observed.")
                : new ActionOutcome("indeterminate", live, "The setting has another value after a restart.", "interrupted", "Unexpected value.");
            var provider = plan.Text("kind") == "provider";
            if (provider && live is null) outcome = outcome with { State = "indeterminate" };
            _store.Write(db =>
            {
                Finish(db, handler ?? new MissingHandler(plan.Text("actionId")), operationId, outcome, provider: true);
                var reconciled = LoadOperation(db, operationId);
                reconciled["reconciled"] = true;
                if (outcome.State == "verified") reconciled["note"] = "authorship uncertain after interruption";
                SaveOperation(db, reconciled);
            });
            _log($"reconciled {operationId}: {outcome.State}");
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
        public Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct) => Task.FromResult(ActionResolution.Ineligible("unavailable", "Unavailable."));
        public Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct) => Task.FromResult(new JsonObject());
        public Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct) => throw new InvalidOperationException();
        public ActionRequest? UndoRequest(JsonObject target, JsonObject before) => null;
    }

    // ---------------------------------------------------------------- manual tasks

    /// <summary>manual.open: a plan that only names the settings page; the task waits for a re-check (awaiting_rescan).</summary>
    private JsonObject CreateManual((string ReportId, JsonObject Report, JsonObject Finding)? found, string? findingId, RequestedBy by)
    {
        if (found is not { } f) throw RpcError.Params("findingId", "manual.open needs a findingId.");
        var uri = f.Finding["settingsUri"]?.GetValue<string>() ?? throw RpcError.State("no_settings", "That finding has no settings page.");
        return _store.Write(db =>
        {
            var handler = new ManualHandler();
            var resolution = new ActionResolution(true, new JsonObject { ["type"] = "settings", ["id"] = uri, ["label"] = f.Finding.Text("title") },
                [], [], [new PlanRow("Opens", "", uri)], [], [], null);
            var plan = InsertPlan(db, handler, resolution, null, found, by, null);
            db.Exec("INSERT INTO manual_tasks (finding_id, report_id, status, updated_at) VALUES ($f, $r, 'awaiting_rescan', $t) ON CONFLICT (finding_id) DO UPDATE SET status = 'awaiting_rescan', updated_at = $t",
                ("$f", findingId), ("$r", f.ReportId), ("$t", db.Now()));
            return plan;
        });
    }

    private sealed class ManualHandler : IActionHandler
    {
        public string ActionId => ManualOpen;
        public string Version => "1.0.0";
        public string Label => "Open settings";
        public string Privilege => "none";
        public bool SupportsUndo => false;
        public string Kind => "manual";
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
            _log($"recheck {ruleId}: {ex.GetType().Name}");
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

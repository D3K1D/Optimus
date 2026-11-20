using System.Text.Json.Nodes;

namespace OptimizationTool.Engine.Actions;

// action.narrow (contracts/m9-ai-powers.md §17.3): the user switched steps of a fix off before approving. The parent fix is
// replaced, in one transaction, by a plan of the kept steps exactly as they were (same order, every execution field
// unchanged), with its own hash and the parent's expiry; the UI then opens the native dialog for the new plan, so the
// approval path itself does not change. A narrowed plan is a reduced plan: its verified run does not close the card.
public sealed partial class ActionService
{
    /// <summary>
    /// action.narrow {idempotencyKey, planId, keep:[n]} (desktop only) → the new plan. keep: a non-empty, strictly ascending,
    /// proper subset of the parent's step numbers (-32602). The parent must be an awaiting fix plan that is not an undo plan
    /// (-32013 not_narrowable, or its status). A kept step that no longer resolves the same → -32002 stale_plan, and the parent
    /// ends ineligible (stale_plan) as an approval that finds drift does (card.fix: blocked, code stale_plan).
    /// </summary>
    public async Task<JsonNode> NarrowAsync(JsonObject p)
    {
        var key = P.IdempotencyKey(p);
        var planId = P.Req(p, "planId", 64);
        var keep = KeepParam(p);
        return await IdempotentAsync("action.narrow", key, p, async () =>
        {
            var parent = _store.Write(db => LivePlan(db, planId)); // lazy expiry commits on its own
            CheckNarrowable(parent, keep);
            var kept = StepsOf(parent).Where(s => keep.Contains(s["n"]!.GetValue<int>())).ToList();
            // Outside the transaction: every kept step re-resolves and must still match exactly (else stale_plan).
            foreach (var step in kept)
            {
                var h = Handler(step.Text("actionId"));
                var live = h is null || Disabled(step.Text("actionId")) is not (null or AdminUnavailable) || h.Kind != "setting"
                    ? null : await ResolveAsync(h, StepRequest(step)).ConfigureAwait(false);
                if (h is null || live is null || !StepMatches(h, live, step))
                {
                    // As an approval that finds drift: the parent ends ineligible (stale_plan), so the card shows blocked.
                    var current = _store.Write(db =>
                    {
                        var still = LivePlan(db, planId);
                        if (still.Text("status") == "awaiting_user")
                            SavePlan(db, still, "ineligible", new JsonObject
                            {
                                ["code"] = "stale_plan", ["message"] = "Something changed since the preview. Preview again.", ["manualUri"] = live?.ManualUri, ["steps"] = new JsonArray(),
                            });
                        return still;
                    });
                    throw new RpcError(ErrorCodes.StaleRevision, "A step no longer matches the PC.", new JsonObject { ["reason"] = "stale_plan", ["current"] = current.DeepClone() });
                }
            }
            return _store.Write(db => InsertNarrowed(db, planId, keep, kept));
        }).ConfigureAwait(false);
    }

    private static List<int> KeepParam(JsonObject p)
    {
        const string message = "keep must be step numbers in ascending order.";
        if (p["keep"] is not JsonArray a || a.Count is 0 or > FixCaps.Steps) throw RpcError.Params("keep", message);
        var list = new List<int>();
        foreach (var x in a)
        {
            if (x is not JsonValue v || Json.Number(v) is not double d || d != Math.Floor(d) || d is < 1 or > FixCaps.Steps) throw RpcError.Params("keep", message);
            if (list.Count > 0 && (int)d <= list[^1]) throw RpcError.Params("keep", message);
            list.Add((int)d);
        }
        return list;
    }

    /// <summary>The parent may be narrowed to <paramref name="keep"/>: an awaiting fix, not an undo plan, keep a proper subset of its steps.</summary>
    private static void CheckNarrowable(JsonObject parent, List<int> keep)
    {
        if (!IsFix(parent) || parent["undoOf"] is JsonValue)
            throw RpcError.State("not_narrowable", "Only a proposed fix can have steps switched off.");
        if (parent.Text("status") != "awaiting_user") throw RpcError.State(parent.Text("status"), $"The plan is {parent.Text("status")}.");
        var all = StepsOf(parent).Select(s => s["n"]!.GetValue<int>()).ToList();
        if (keep.Any(n => !all.Contains(n))) throw RpcError.Params("keep", "keep must name steps of this plan.");
        if (keep.Count == all.Count) throw RpcError.Params("keep", "keep must leave out at least one step.");
    }

    /// <summary>
    /// The narrowing transaction: the parent re-read live (awaiting, not expired); the requester task and its grant rechecked as
    /// for a new proposal (administrator rights included) and the slot rule not counting the parent; the parent cancelled
    /// (replaced, replacedBy); the new plan stored with the same requester, task, report, card and attempt, the kept steps in
    /// order, narrowedFrom, removed, the parent's expiresAt and its own hash, with the idempotency result.
    /// </summary>
    private JsonObject InsertNarrowed(Db db, string parentId, List<int> keep, List<JsonObject> kept)
    {
        var parent = LivePlan(db, parentId);
        CheckNarrowable(parent, keep);
        var cardId = parent["cardId"]?.GetValue<string>();
        var stepObjects = kept.Select((s, i) =>
        {
            var step = (JsonObject)s.DeepClone();
            step["n"] = i + 1; // the execution order of the new plan; contentHash covers the execution fields only
            return step;
        }).ToList();
        var privilege = stepObjects.Any(s => s.Text("privilege") == "admin") ? "admin" : "none";
        if (parent["requestedBy"]?["taskId"]?.GetValue<string>() is { } taskId)
        {
            var grant = CheckRequester(db, taskId, cardId, otherThan: parentId, level: FixKind);
            if (privilege == "admin")
            {
                var flag = stepObjects.Where(s => s.Text("privilege") == "admin").All(s => FixAdminAllowed(s.Text("actionId")));
                if (AdminRefusal(grant, flag) is { } refusal) throw RpcError.State(refusal.Code, refusal.Message);
            }
        }
        var newId = "plan-" + Ids.RandomHex();
        var flags = ActionFlags.Union([.. stepObjects.Select(s => (IReadOnlyList<string>)Json.Strings(s["flags"]).ToList())]) ?? [];
        var plan = new JsonObject
        {
            ["planId"] = newId, ["kind"] = FixKind, ["actionId"] = null, ["handlerVersion"] = null, ["label"] = null,
            ["findingId"] = parent["findingId"]?.DeepClone(), ["reportId"] = parent["reportId"]?.DeepClone(),
            ["title"] = parent["title"]?.DeepClone(), ["why"] = parent["why"]?.DeepClone(),
            ["target"] = null, ["before"] = null, ["after"] = null,
            ["rows"] = new JsonArray(), ["context"] = new JsonArray(), ["tradeoffs"] = new JsonArray(),
            ["privilege"] = privilege, ["flags"] = Json.Array(flags),
            ["resources"] = Json.Array(ResourceKeys.Normalize(stepObjects.SelectMany(s => Json.Strings(s["resources"])))),
            ["undo"] = new JsonObject { ["supported"] = stepObjects.Any(s => s["undo"]?["supported"]?.GetValue<bool>() == true), ["conditions"] = null },
            ["requestedBy"] = parent["requestedBy"]?.DeepClone(), ["undoOf"] = null,
            ["steps"] = new JsonArray([.. stepObjects]),
            ["createdAt"] = Now(), ["expiresAt"] = parent.Text("expiresAt"),
            ["status"] = "awaiting_user", ["ineligible"] = null,
            ["cardId"] = cardId, ["attemptId"] = parent["attemptId"]?.DeepClone(),
            ["narrowedFrom"] = parentId,
            // A narrowed parent's own removed steps carry forward first (switched off earlier), then this narrowing's, in step order.
            ["removed"] = new JsonArray([
                .. (parent["removed"] as JsonArray ?? []).OfType<JsonObject>().Select(r => (JsonNode?)new JsonObject { ["label"] = r.Text("label") }),
                .. StepsOf(parent).Where(s => !keep.Contains(s["n"]!.GetValue<int>()))
                    .Select(s => (JsonNode?)new JsonObject { ["label"] = s.Text("label") })]),
        };
        if (parent["leftOut"] is JsonArray leftOut) plan["leftOut"] = leftOut.DeepClone(); // still left out: still reduced
        plan["planHash"] = HashFix(plan);

        parent["replacedBy"] = newId;
        SavePlan(db, parent, "cancelled", new JsonObject { ["code"] = "replaced", ["message"] = "Replaced by the plan with the steps you kept." });
        db.Exec("INSERT INTO action_plans (plan_id, body, status, task_id, target_key, created_at, expires_at, card_id) VALUES ($p, $b, $s, $t, $k, $c, $e, $card)",
            ("$p", newId), ("$b", Json.Write(plan)), ("$s", "awaiting_user"), ("$t", parent["requestedBy"]?["taskId"]?.GetValue<string>()), ("$k", "fix:" + newId),
            ("$c", plan.Text("createdAt")), ("$e", plan.Text("expiresAt")), ("$card", cardId));
        PlanEvent(db, plan);
        if (PendingRecord.Value is { Written: false } pending) WriteKey(db, pending, newId);
        return plan;
    }
}

using System.Text.Json.Nodes;

namespace OptimizationTool.Engine.Actions;

/// <summary>
/// The card an AI plan belongs to (contracts/m9-ai-powers.md §17.3): the card, its report and the fix attempt. AttemptId null:
/// the inserting transaction starts the attempt (a cardRef outside a fix ask: a paired window's, or a chat turn's when
/// <see cref="TurnId"/> is set).
/// <see cref="ExpectedAttempt"/>: the card's current attempt when the binding was checked (a cardRef outside a fix ask), which
/// must still be current when the plan is inserted.
/// </summary>
internal sealed record CardBinding(string CardId, string ReportId, string? AttemptId, string By, string? TurnId = null, string? ExpectedAttempt = null);

// The one shared slot rule (§17.3, §17.6) for every path that creates, approves, narrows or revises an AI plan: expired
// plans are closed first; one pending plan per card across all tasks; at most three pending plans per task, of which at
// most one has no card; the plan being replaced or approved is not counted. The user's own previews take no slot.
public sealed partial class ActionService
{
    public const int PendingPerTask = 3;
    public const string CardSlotTaken = "A change is already waiting on this card.";
    public const string TaskSlotsFull = "Three changes are already waiting.";
    public const string NoCardSlotTaken = "A change is already waiting for the user's decision.";

    /// <summary>Every plan still awaiting the user whose time has passed is closed (expired), with its event.</summary>
    internal void ExpireDue(Db db)
    {
        foreach (var planId in db.Query("SELECT plan_id FROM action_plans WHERE status = 'awaiting_user'", r => r.GetString(0)))
            LivePlan(db, planId);
    }

    /// <summary>Why an AI plan of <paramref name="taskId"/> on <paramref name="cardId"/> (null: no card) may not wait now, or null.</summary>
    internal string? SlotRefusal(Db db, string taskId, string? cardId, string? otherThan)
    {
        ExpireDue(db);
        var pending = db.Query("SELECT plan_id, task_id, card_id FROM action_plans WHERE status = 'awaiting_user' AND task_id IS NOT NULL",
            r => (Plan: r.GetString(0), Task: r.GetString(1), Card: r.IsDBNull(2) ? null : r.GetString(2))).Where(p => p.Plan != otherThan).ToList();
        if (cardId is not null && pending.Any(p => p.Card == cardId)) return CardSlotTaken;
        var mine = pending.Where(p => p.Task == taskId).ToList();
        if (mine.Count >= PendingPerTask) return TaskSlotsFull;
        if (cardId is null && mine.Any(p => p.Card is null)) return NoCardSlotTaken;
        return null;
    }

    /// <summary>The slot rule in the caller's transaction: throws PlanRefused with the plain message.</summary>
    private void CheckSlots(Db db, string taskId, string? cardId, string? otherThan)
    {
        if (SlotRefusal(db, taskId, cardId, otherThan) is { } refused) throw RpcError.State(PlanRefused, refused);
    }

    /// <summary>
    /// propose_script (§17.6): the pending script plan of this task that holds the slot a new one would take (the same card,
    /// or the task's one no-card slot), or null.
    /// </summary>
    internal string? ScriptSlotHolder(Db db, string taskId, string? cardId)
    {
        ExpireDue(db);
        return db.Query("SELECT plan_id, body FROM action_plans WHERE status = 'awaiting_user' AND task_id = $t AND (card_id = $c OR ($c IS NULL AND card_id IS NULL)) ORDER BY rowid DESC",
                r => (Plan: r.GetString(0), Body: Json.Parse(r.GetString(1)) as JsonObject), ("$t", taskId), ("$c", cardId))
            .FirstOrDefault(p => p.Body is { } b && IsScript(b)).Plan;
    }

    /// <summary>
    /// m9 §17.3 review: whether a task's turn is still running (set by the agent service). Null: no turn is ever alive.
    /// </summary>
    internal Func<string, string, bool>? TurnAlive { get; set; }

    public const string StaleCard = "The card changed while the change was being prepared. Nothing was proposed.";

    /// <summary>
    /// Binds a new plan to its card and attempt in the inserting transaction (the attempt starts here for a cardRef outside
    /// a fix ask). Writes cardId and attemptId into the plan body. The binding was checked before the plan resolved, so it is
    /// checked again here: the card is still open and in the task's scope, the attempt expected is still the card's current
    /// one (a fix ask's attempt still working), and the originating turn still runs; otherwise PlanRefused (not_allowed).
    /// </summary>
    private void BindCard(Db db, JsonObject plan, CardBinding? card, RequestedBy by)
    {
        if (card is null || by.TaskId is not { } taskId)
        {
            plan["cardId"] = null;
            plan["attemptId"] = null;
            return;
        }
        var task = Agent.AgentService.LoadTask(db, taskId);
        var current = FixAttempts.Current(db, card.CardId);
        var stale = Agent.AgentService.ScopedCard(db, task, card.CardId)?.Text("status") != "open"
            || (card.AttemptId is { } expected
                ? current?.Id != expected || current.State != "working"
                : current?.Id != card.ExpectedAttempt || (FixAttempts.Derive(db, card.CardId)?["state"]?.GetValue<string>() is { } s && s != "plan_ready" && FixAttempts.ActiveStates.Contains(s)))
            || (card.TurnId is { } turn && TurnAlive?.Invoke(taskId, turn) != true);
        if (stale) throw RpcError.State(PlanRefused, StaleCard);
        var attemptId = card.AttemptId ?? FixAttempts.Start(db, taskId, card.ReportId, card.CardId, card.TurnId, card.By);
        plan["cardId"] = card.CardId;
        plan["attemptId"] = attemptId;
    }

    /// <summary>
    /// A plan's card is closed by a verified, not reduced run (§17.3, §17.6): by plan.cardId when set, otherwise by its finding
    /// as before. A reduced plan (leftOut or narrowedFrom) leaves the card open: the next scan decides.
    /// </summary>
    private static void CloseCard(Db db, JsonObject plan, string operationId, bool undoAvailable)
    {
        if (FixAttempts.Reduced(plan)) return;
        if (plan["cardId"]?.GetValue<string>() is { } cardId && plan["attemptId"]?.GetValue<string>() is { } attemptId
            && FixAttempts.Get(db, attemptId)?.ReportId is { } cardReport)
            Followups.MarkVerifiedCard(db, cardReport, cardId, operationId, undoAvailable);
        else if (plan["reportId"]?.GetValue<string>() is { } reportId && plan["findingId"]?.GetValue<string>() is { } findingId)
            Followups.MarkVerified(db, reportId, findingId, operationId, undoAvailable);
    }

    /// <summary>"Change something…" (§17.3): the awaiting plan the user asked to revise is cancelled (revised), with its event.</summary>
    internal void CancelRevised(Db db, string planId)
    {
        var plan = LivePlan(db, planId);
        if (plan.Text("status") != "awaiting_user") throw RpcError.Params("revise", "revise must name the plan waiting on this card.");
        SavePlan(db, plan, "cancelled", new JsonObject { ["code"] = "revised", ["message"] = "You asked for a different plan." });
    }

    /// <summary>The live plan (lazily expired), for the agent's checks in its own transaction.</summary>
    internal JsonObject LivePlanOf(Db db, string planId) => LivePlan(db, planId);
}

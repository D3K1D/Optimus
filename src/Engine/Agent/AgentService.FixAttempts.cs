using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;

namespace OptimizationTool.Engine.Agent;

// "Fix for me" (contracts/m9-ai-powers.md §17.3, §17.6): agent.ask {intent:"fix", cardId, text?, revise?} starts a fix
// attempt on an open card of the task's report and writes the turn itself; plans proposed in that turn (or with a cardRef)
// belong to the card; note_card may leave an attributed opinion; card.fix is derived (FixAttempts); fix.dismiss clears an
// attempt that has ended.
public sealed partial class AgentService
{
    public static readonly string[] AskIntents = ["fix"];

    /// <summary>m9 §17.6: the runaway bound on model round trips per task (the task ends exhausted).</summary>
    public const int TaskTurns = 64;

    /// <summary>A fix ask gets this many round trips more than a normal ask (8 + 4 = 12 with the default grant).</summary>
    public const int FixExtraTurns = 4;

    /// <summary>The round trips one ask may use (m9 §17.6): grant.limits.turns (8 by default), a fix ask 4 more (12).</summary>
    internal static int AskTurns(Grant grant, bool fix) => grant.Limits.Turns + (fix ? FixExtraTurns : 0);

    internal const string AskBudgetUsed = "I stopped here because this question used all of its steps. Ask again to go on.";

    /// <summary>One ask: the user's words (may be empty for a fix ask), the card they look at, and the fix request, if any.</summary>
    /// <summary><see cref="Continuation"/> (m9 §17.2): the host-written message after administrator checks, in place of the fix request.</summary>
    private sealed record AskTurn(string Text, string? CardId, FixAsk? Fix, string? Continuation = null);

    /// <summary>A fix ask: its card and attempt, and for "Change something…" the steps of the plan the user asked to revise.</summary>
    private sealed record FixAsk(string CardId, string AttemptId, string? Revised);

    /// <summary>
    /// The card a task may fix or bind a plan to: in the task's report, and (with scope.findingIds) its finding within them; a
    /// card without a finding only when the scope is the whole report. The get_cards view (status open | done | …), or null.
    /// </summary>
    internal static JsonObject? ScopedCard(Db db, JsonObject task, string cardId)
    {
        var reportId = task["scope"].Text("reportId");
        if (Followups.AgentCards(db, reportId, GrantOf(task).ShareNames, task.Text("taskId"))?.OfType<JsonObject>().FirstOrDefault(c => c.Text("cardId") == cardId) is not { } card)
            return null;
        if (task["scope"]?["findingIds"] is JsonArray scoped)
        {
            var finding = card["findingId"]?.GetValue<string>();
            if (finding is null || !Json.Strings(scoped).Contains(finding)) return null;
        }
        return card;
    }

    /// <summary>
    /// In agent.ask's transaction, after its admission: the card is in the task's report and scope (else -32602) and open
    /// (else -32013 card_not_open); revise names the awaiting plan of this card's current attempt in this task (else -32602),
    /// which is cancelled (revised); otherwise nothing may be in progress on the card (-32013 fix_active). Starts the attempt.
    /// </summary>
    private FixAsk StartFixAsk(Db db, JsonObject task, string cardId, string? revise, string turnId)
    {
        var taskId = task.Text("taskId");
        var card = ScopedCard(db, task, cardId) ?? throw RpcError.Params("cardId", "That card is not part of this task's report.");
        if (card.Text("status") != "open") throw RpcError.State("card_not_open", "That card is not open.");
        string? revised = null;
        if (revise is not null)
        {
            var current = FixAttempts.Current(db, cardId);
            JsonObject? plan = null;
            try { plan = Actions?.LivePlanOf(db, revise); }
            catch (RpcError) { plan = null; }
            if (current is null || plan is null || plan.Text("status") != "awaiting_user" || plan["cardId"]?.GetValue<string>() != cardId
                || plan["attemptId"]?.GetValue<string>() != current.Id || plan["requestedBy"]?["taskId"]?.GetValue<string>() != taskId)
                throw RpcError.Params("revise", "revise must name the plan waiting on this card in this task.");
            revised = PlanOutline(plan);
            Actions!.CancelRevised(db, revise);
        }
        else if (FixAttempts.Active(db, cardId)) throw new RpcError(ErrorCodes.InvalidState, "A fix is already in progress on this card.", new JsonObject { ["reason"] = "fix_active" });
        var attemptId = FixAttempts.Start(db, taskId, task["scope"].Text("reportId"), cardId, turnId, AiName(task["recipient"].Text("kind"), "in_app"));
        return new FixAsk(cardId, attemptId, revised);
    }

    /// <summary>What the revised plan was, for the AI: its title and its steps' handler labels (host text), in order.</summary>
    private static string PlanOutline(JsonObject plan)
    {
        var title = plan["title"]?.GetValue<string>() ?? plan["label"]?.GetValue<string>() ?? "a change";
        var steps = (plan["steps"] as JsonArray ?? []).OfType<JsonObject>().Select(s => $"{s["n"]}. {s.Text("label")}").ToList();
        return steps.Count == 0 ? $"\"{title}\"" : $"\"{title}\" with steps {string.Join("; ", steps)}";
    }

    /// <summary>The host-written fix request (m9 §17.3), added to the ask after the card line.</summary>
    private static IEnumerable<string> FixLines(FixAsk fix)
    {
        if (fix.Revised is { } revised)
            yield return $"The user asked to change your plan {revised}. It was withdrawn; propose a new plan that follows what the user says below.";
        yield return $"The user asks you to fix the issue on card {fix.CardId}. Investigate first with the checks and lookups your permissions allow. " +
            "Then end with exactly one of: (a) a plan: propose_fix; request_action for a long tool or a single change; propose_script only where scripts are allowed and nothing in the catalog fits; " +
            "(b) note_card on this card with an outcome: needs_user (the user has to do it) or nothing_to_fix, the note saying why. " +
            "Before proposing, say in a sentence or two what will change and why.";
    }

    /// <summary>m9 §17.3 review: the task's running ask is this turn and it was neither cancelled nor revoked.</summary>
    internal bool TurnAlive(string taskId, string turnId)
    {
        lock (_gate) return _asking.TryGetValue(taskId, out var run) && run.TurnId == turnId && !run.Cancelled && !run.Revoked;
    }

    /// <summary>The turn ended (any way): its fix attempt ends with what it left (a plan, an opinion, or no_fix).</summary>
    private void EndFixTurn(string taskId, string turnId)
    {
        try { _store.Write(db => FixAttempts.EndTurn(db, turnId)); }
        catch (Exception ex) { _log($"task {taskId} fix attempt end: {ex.GetType().Name}"); }
    }

    /// <summary>
    /// fix.dismiss {cardId} (desktop): the card's attempt has ended with nothing pending, running or waiting for checks → it is
    /// dismissed and the card is back to normal ({cardId, fix:null}); otherwise -32013 {reason:"fix_active"}. Nothing else
    /// changes (no status, receipt or undo). A card with no current attempt is already normal.
    /// </summary>
    public JsonNode FixDismiss(JsonObject p)
    {
        var cardId = P.Req(p, "cardId", 64);
        return _store.Write(db =>
        {
            Actions?.ExpireDue(db);
            if (FixAttempts.Current(db, cardId) is { } attempt)
            {
                if (FixAttempts.Active(db, cardId) || FixAttempts.CheckRequestOf(db, attempt.Id) is not null)
                    throw new RpcError(ErrorCodes.InvalidState, "A fix is still in progress on this card.", new JsonObject { ["reason"] = "fix_active" });
                FixAttempts.Dismiss(db, attempt);
            }
            return (JsonNode)new JsonObject { ["cardId"] = cardId, ["fix"] = null };
        });
    }

    /// <summary>
    /// A proposing tool's card (m9 §17.3), in phase 1: inside a fix ask's turn the plan belongs to its attempt (a cardRef naming
    /// another card, or a findingRef other than the card's finding → invalid_arguments); outside one, a cardRef names an open
    /// card of the task's report (else not_allowed) and the insert starts the attempt. Null binding: no card.
    /// </summary>
    private (CardBinding? Card, string? Code, string? Error) CardFor(Db db, JsonObject task, string? turnId, JsonObject args, string by)
    {
        var taskId = task.Text("taskId");
        var cardRef = args["cardRef"]?["cardId"]?.GetValue<string>();
        var findingRef = args["findingRef"]?.GetValue<string>();
        if (FixAttempts.OfTurn(db, turnId) is { } attempt && attempt.TaskId == taskId)
        {
            if (cardRef is not null && cardRef != attempt.CardId) return (null, "invalid_arguments", "cardRef must name the card you are fixing.");
            var finding = CardFinding(db, task, attempt.CardId);
            if (findingRef is not null && findingRef != finding) return (null, "invalid_arguments", "findingRef must be the finding of the card you are fixing, or left out.");
            return (new CardBinding(attempt.CardId, attempt.ReportId, attempt.Id, by, turnId), null, null);
        }
        if (cardRef is null) return (null, null, null);
        var card = ScopedCard(db, task, cardRef);
        if (card is null) return (null, "not_allowed", "That card is not part of this task.");
        if (card.Text("status") != "open") return (null, "not_allowed", "That card is not open.");
        if (findingRef is not null && findingRef != card["findingId"]?.GetValue<string>())
            return (null, "invalid_arguments", "findingRef must be that card's finding, or left out.");
        // A plan waiting on the card is the slot rule's to refuse (its own message); anything else in progress keeps its attempt.
        if (FixAttempts.Derive(db, cardRef)?["state"]?.GetValue<string>() is { } inProgress && inProgress != "plan_ready" && FixAttempts.ActiveStates.Contains(inProgress))
            return (null, "not_allowed", "A fix is already in progress on this card.");
        return (new CardBinding(cardRef, task["scope"].Text("reportId"), null, by, turnId, FixAttempts.Current(db, cardRef)?.Id), null, null);
    }

    private static string? CardFinding(Db db, JsonObject task, string cardId) =>
        Followups.AgentCards(db, task["scope"].Text("reportId"), true, task.Text("taskId"))?.OfType<JsonObject>()
            .FirstOrDefault(c => c.Text("cardId") == cardId)?["findingId"]?.GetValue<string>();

    /// <summary>
    /// note_card's outcome (m9 §17.3): an attributed opinion on the card's attempt (this turn's, else the card's current one when
    /// it is this task's, else a new ended attempt). Never the card's status. Null when stored; else the refusal message.
    /// </summary>
    private static string? NoteOutcome(Db db, JsonObject task, string? turnId, string cardId, string outcome, string by)
    {
        var taskId = task.Text("taskId");
        var attempt = FixAttempts.OfTurn(db, turnId) is { } mine && mine.TaskId == taskId && mine.CardId == cardId ? mine
            : FixAttempts.Current(db, cardId) is { } current && current.TaskId == taskId ? current : null;
        if (attempt is null)
        {
            if (FixAttempts.Active(db, cardId)) return "A fix is already in progress on this card.";
            var id = FixAttempts.Start(db, taskId, task["scope"].Text("reportId"), cardId, null, by);
            attempt = FixAttempts.Get(db, id)!;
        }
        FixAttempts.SetOpinion(db, attempt, outcome, by);
        return null;
    }
}

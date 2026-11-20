using System.Globalization;
using System.Text.Json.Nodes;

namespace OptimizationTool.Engine;

/// <summary>
/// "Fix for me" attempts (contracts/m9-ai-powers.md §17.3, §17.6). One current attempt per card (a new one replaces it);
/// its row state is working (the attempt's turn runs) | ended | replaced | dismissed. <c>card.fix</c> is derived on read
/// from the attempt, the plans bound to it and their operations, never stored; the AI's opinion (note_card outcome) is an
/// attributed record on the attempt, apart from the card's status, and below every host state. Each change is announced
/// on stream user as <c>card.fix {cardId, reportId, fix}</c>. All calls run inside the caller's store transaction.
/// </summary>
internal static class FixAttempts
{
    public const string EventType = "card.fix";

    /// <summary>card.fix.state in its order of precedence (§17.3).</summary>
    public static readonly string[] States =
    [
        "working", "waiting_checks", "checking", "plan_ready", "running", "fixed", "done_reduced", "partly_fixed", "finished", "failed", "stopped", "unclear", "blocked",
        "needs_you", "nothing_to_fix", "no_fix",
    ];

    /// <summary>States with something still happening: a new fix ask and fix.dismiss are refused (fix_active).</summary>
    public static readonly string[] ActiveStates = ["working", "waiting_checks", "checking", "plan_ready", "running"];

    /// <summary>note_card outcomes (the AI's opinion) and the card.fix state each shows as.</summary>
    public static readonly string[] Outcomes = ["needs_user", "nothing_to_fix"];

    internal sealed record Attempt(string Id, string TaskId, string ReportId, string CardId, string State, string? TurnId, string? Outcome,
        JsonObject? Opinion, string By, string CreatedAt, string UpdatedAt);

    private const string Columns = "attempt_id, task_id, report_id, card_id, state, turn_id, outcome, opinion, by_label, created_at, updated_at";

    private static Attempt Row(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : Json.Parse(r.GetString(7)) as JsonObject, r.GetString(8), r.GetString(9), r.GetString(10));

    /// <summary>The card's current attempt (working or ended), or null.</summary>
    public static Attempt? Current(Db db, string cardId) =>
        db.Query($"SELECT {Columns} FROM fix_attempts WHERE card_id = $c AND state IN ('working','ended') ORDER BY rowid DESC LIMIT 1", Row, ("$c", cardId)).FirstOrDefault();

    public static Attempt? Get(Db db, string attemptId) =>
        db.Query($"SELECT {Columns} FROM fix_attempts WHERE attempt_id = $a", Row, ("$a", attemptId)).FirstOrDefault();

    /// <summary>The attempt a running turn works for (its fix ask), or null.</summary>
    public static Attempt? OfTurn(Db db, string? turnId) => turnId is null ? null
        : db.Query($"SELECT {Columns} FROM fix_attempts WHERE turn_id = $t AND state = 'working' ORDER BY rowid DESC LIMIT 1", Row, ("$t", turnId)).FirstOrDefault();

    /// <summary>
    /// Starts a new attempt on a card, replacing its current one (whose opinion goes with it). <paramref name="turnId"/> set:
    /// the attempt works while that turn runs. Null (a paired window's cardRef, an opinion outside a fix turn): it starts ended.
    /// </summary>
    public static string Start(Db db, string taskId, string reportId, string cardId, string? turnId, string by)
    {
        var now = db.Now();
        db.Exec("UPDATE fix_attempts SET state = 'replaced', updated_at = $n WHERE card_id = $c AND state IN ('working','ended')", ("$n", now), ("$c", cardId));
        var attemptId = "fix-" + Ids.RandomHex();
        db.Exec($"INSERT INTO fix_attempts ({Columns}) VALUES ($a, $t, $r, $c, $s, $turn, NULL, NULL, $by, $n, $n)",
            ("$a", attemptId), ("$t", taskId), ("$r", reportId), ("$c", cardId), ("$s", turnId is null ? "ended" : "working"), ("$turn", turnId),
            ("$by", by), ("$n", now));
        Emit(db, cardId, reportId);
        return attemptId;
    }

    /// <summary>The turn ended (however it ended): its working attempt ends with what it left (a plan, an opinion, or no_fix).</summary>
    public static void EndTurn(Db db, string turnId)
    {
        foreach (var a in db.Query($"SELECT {Columns} FROM fix_attempts WHERE turn_id = $t AND state = 'working'", Row, ("$t", turnId)))
            End(db, a, Plans(db, a).Count > 0 ? "plan" : a.Opinion is not null ? "opinion" : "no_fix");
    }

    private static void End(Db db, Attempt a, string outcome)
    {
        db.Exec("UPDATE fix_attempts SET state = 'ended', outcome = $o, updated_at = $n WHERE attempt_id = $a", ("$o", outcome), ("$n", db.Now()), ("$a", a.Id));
        Emit(db, a.CardId, a.ReportId);
    }

    /// <summary>Startup (§17.6): an attempt found working (its turn died with the host) ends as no_fix. Returns how many.</summary>
    public static int RecoverWorking(Db db)
    {
        var working = db.Query($"SELECT {Columns} FROM fix_attempts WHERE state = 'working'", Row);
        foreach (var a in working) End(db, a, "no_fix");
        return working.Count;
    }

    /// <summary>The AI's opinion (note_card outcome): {outcome, by, taskId, at}. Never the card's status, never card.fix.state on its own.</summary>
    public static void SetOpinion(Db db, Attempt a, string outcome, string by)
    {
        var opinion = new JsonObject { ["outcome"] = outcome, ["by"] = by, ["taskId"] = a.TaskId, ["at"] = db.Now() };
        db.Exec("UPDATE fix_attempts SET opinion = $o, outcome = CASE WHEN state = 'ended' AND (outcome IS NULL OR outcome = 'no_fix') THEN 'opinion' ELSE outcome END, updated_at = $n WHERE attempt_id = $a",
            ("$o", Json.Write(opinion)), ("$n", db.Now()), ("$a", a.Id));
        Emit(db, a.CardId, a.ReportId);
    }

    /// <summary>fix.dismiss: the attempt ends and the card returns to normal.</summary>
    public static void Dismiss(Db db, Attempt a)
    {
        db.Exec("UPDATE fix_attempts SET state = 'dismissed', updated_at = $n WHERE attempt_id = $a", ("$n", db.Now()), ("$a", a.Id));
        Emit(db, a.CardId, a.ReportId);
    }

    /// <summary>
    /// The administrator checks (§17.2) of the attempt: its open request (the newest awaiting_user or running one; an awaiting one
    /// whose 10 minutes passed reads as ended, reading never writes) and whether it waits for the user (waiting_checks) or runs
    /// (checking). Null: none open.
    /// </summary>
    public static (string RequestId, bool Running)? CheckRequestOf(Db db, string attemptId)
    {
        var now = DateTimeOffset.Parse(db.Now(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        var open = db.Query("SELECT request_id, state, expires_at FROM check_requests WHERE attempt_id = $a AND state IN ('awaiting_user','running') ORDER BY rowid DESC",
                r => (Id: r.GetString(0), State: r.GetString(1), Expires: r.GetString(2)), ("$a", attemptId))
            .FirstOrDefault(q => q.State == "running"
                || DateTimeOffset.Parse(q.Expires, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal) > now);
        return open.Id is null ? null : (open.Id, open.State == "running");
    }

    /// <summary>The attempt's checks ended and its continuation waits to start (§17.2): the card shows working.</summary>
    public static bool ContinuationPending(Db db, string attemptId) =>
        db.Scalar("SELECT 1 FROM continuations WHERE attempt_id = $a AND state = 'pending' LIMIT 1", ("$a", attemptId)) is not null;

    /// <summary>A continuation after administrator checks (§17.2) works for the same attempt again, under its new turn.</summary>
    public static void Resume(Db db, string attemptId, string turnId)
    {
        db.Exec("UPDATE fix_attempts SET state = 'working', turn_id = $t, outcome = NULL, updated_at = $n WHERE attempt_id = $a AND state = 'ended'",
            ("$t", turnId), ("$n", db.Now()), ("$a", attemptId));
        if (Get(db, attemptId) is { } a) Emit(db, a.CardId, a.ReportId);
    }

    /// <summary>A plan bound to a card changed status (or was stored): its attempt moved, so the card's fix is announced.</summary>
    public static void PlanChanged(Db db, JsonObject plan)
    {
        if (plan["cardId"]?.GetValue<string>() is not { } cardId) return;
        if (plan["attemptId"]?.GetValue<string>() is { } attemptId)
            db.Exec("UPDATE fix_attempts SET updated_at = $n WHERE attempt_id = $a", ("$n", db.Now()), ("$a", attemptId));
        var reportId = plan["reportId"]?.GetValue<string>()
            ?? (plan["attemptId"]?.GetValue<string>() is { } a ? Get(db, a)?.ReportId : null) ?? "";
        Emit(db, cardId, reportId);
    }

    /// <summary>card.fix {cardId, reportId, fix} on stream user (fix null: the card is back to normal).</summary>
    public static void Emit(Db db, string cardId, string reportId) =>
        db.AppendEvent("user", EventType, new JsonObject { ["cardId"] = cardId, ["reportId"] = reportId, ["fix"] = Derive(db, cardId) });

    /// <summary>The plans bound to an attempt, oldest first (bodies).</summary>
    private static List<JsonObject> Plans(Db db, Attempt a) =>
        db.Query("SELECT body FROM action_plans WHERE card_id = $c ORDER BY rowid", r => Json.Parse(r.GetString(0)) as JsonObject, ("$c", a.CardId))
            .OfType<JsonObject>().Where(p => p["attemptId"]?.GetValue<string>() == a.Id).ToList();

    /// <summary>A plan with steps left out (§17.1) or switched off (action.narrow): its verified run does not close the card.</summary>
    public static bool Reduced(JsonObject plan) => plan["leftOut"] is JsonArray { Count: > 0 } || plan["narrowedFrom"] is JsonValue;

    /// <summary>
    /// card.fix for a card, or null (no current attempt, or the card is back to normal after a declined, expired or cancelled
    /// plan): {state, by, attemptId, taskId, planId, operationId, requestId, opinion, code, updatedAt}, in the §17.3 precedence. An
    /// awaiting plan whose time passed reads as expired here (reading never writes).
    /// </summary>
    public static JsonObject? Derive(Db db, string cardId)
    {
        if (Current(db, cardId) is not { } a) return null;
        var now = DateTimeOffset.Parse(db.Now(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        var plans = Plans(db, a);
        string Status(JsonObject p)
        {
            var s = p.Text("status");
            return s == "awaiting_user" && DateTimeOffset.Parse(p.Text("expiresAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal) <= now
                ? "expired" : s;
        }
        string? OperationOf(JsonObject p) => db.Scalar("SELECT operation_id FROM operations WHERE plan_id = $p", ("$p", p.Text("planId"))) as string;
        JsonObject Fix(string state, JsonObject? plan = null, string? requestId = null, string? code = null) => new()
        {
            ["state"] = state, ["by"] = a.By, ["attemptId"] = a.Id, ["taskId"] = a.TaskId, ["planId"] = plan?.Text("planId"), ["operationId"] = plan is null ? null : OperationOf(plan),
            ["requestId"] = requestId, ["opinion"] = a.Opinion?.DeepClone(), ["code"] = code, ["updatedAt"] = a.UpdatedAt,
        };

        if (a.State == "working") return Fix("working", plans.LastOrDefault());
        if (CheckRequestOf(db, a.Id) is { } request) return Fix(request.Running ? "checking" : "waiting_checks", requestId: request.RequestId);
        if (ContinuationPending(db, a.Id)) return Fix("working", plans.LastOrDefault()); // the checks ended; the AI goes on
        if (plans.LastOrDefault(p => Status(p) == "awaiting_user") is { } ready) return Fix("plan_ready", ready);
        if (plans.LastOrDefault(p => Status(p) is "approved" or "running") is { } running) return Fix("running", running);
        if (plans.LastOrDefault() is { } last)
            return Status(last) switch
            {
                "verified" => Fix(Reduced(last) ? "done_reduced" : "fixed", last),
                "partial" => Fix("partly_fixed", last),
                "completed" => Fix("finished", last),
                "failed" or "not_run" => Fix("failed", last),
                "stopped" or "timed_out" => Fix("stopped", last), // a long tool stopped or out of time: not a failure of the fix
                "indeterminate" => Fix("unclear", last),
                "ineligible" => Fix("blocked", last, code: last["ineligible"]?["code"]?.GetValue<string>()),
                _ => null, // declined, expired, cancelled: the card is back to normal
            };
        if (a.Opinion?["outcome"]?.GetValue<string>() is { } outcome) return Fix(outcome == "needs_user" ? "needs_you" : "nothing_to_fix");
        return Fix("no_fix");
    }

    /// <summary>The card has a fix in progress (working, checks, a plan waiting or running).</summary>
    public static bool Active(Db db, string cardId) => Derive(db, cardId)?["state"]?.GetValue<string>() is { } s && ActiveStates.Contains(s);
}

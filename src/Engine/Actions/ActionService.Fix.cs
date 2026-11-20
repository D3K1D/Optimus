using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using OptimizationTool.Transport.Logging;

namespace OptimizationTool.Engine.Actions;

/// <summary>One step of a proposed fix as the AI (or an undo) asked for it. Never trusted: the handler resolves it.</summary>
internal sealed record FixStepRequest(string ActionId, JsonObject? Parameters, string? Why, TargetRef? TargetRef);

/// <summary>Display caps of the native dialog (contracts/m9-ai-powers.md §4): content over a cap makes the plan too_large.</summary>
public static class FixCaps
{
    public const int Steps = 6, Rows = 4, Text = 400, Title = 80, StepWhy = 200;
}

// Fix plans (contracts/m9-ai-powers.md §4, §4.1): several setting-kind steps, one approval. The host resolves every step
// itself, fixes the order (user scope first, then administrator, each group in the AI's order), refuses ineligible or
// overlapping steps, journals each step in operation_step_states and stops at the first step that is not verified.
public sealed partial class ActionService
{
    public const string FixKind = "fix";
    public const string AdminUnavailable = "admin_unavailable";
    public static readonly string[] StepStates = ["pending", "prepared", "attempted", "verified", "failed", "indeterminate", "skipped"];

    /// <summary>operationIds whose "Stop after this step" was asked: no further step starts.</summary>
    private readonly HashSet<string> _stopRequests = [];

    /// <summary>operationId → the stop signal of its running broker session (m9 §3.5: the broker gets a stop frame).</summary>
    private readonly Dictionary<string, CancellationTokenSource> _stopSignals = [];

    /// <summary>
    /// m9 review R5: a single provider operation created but whose run has not registered its stop signal yet → whether Stop
    /// was asked meanwhile. Added with the operation (inside its prepared transaction), consumed when the run registers its
    /// signal (an asked Stop then starts it cancelled), removed when the run ends whatever happened.
    /// </summary>
    private readonly Dictionary<string, bool> _pendingStops = [];

    /// <summary>Single provider operations not yet registered (tests: nothing is kept after a run ends).</summary>
    internal int PendingStopCount { get { lock (_gate) return _pendingStops.Count; } }

    /// <summary>Test seam: awaited by a provider run at "queued" (before attempted) and "registering" (just before its stop signal registers).</summary>
    internal Func<string, string, Task>? RunSeam { get; set; }

    /// <summary>Test seam: the broker session's deadline instead of <see cref="ElevationWait"/>.</summary>
    internal TimeSpan? ElevationWaitSeam { get; set; }

    /// <summary>Test seam: awaited (with the plan id) once the broker session's deadline won, before its call is inspected.</summary>
    internal Func<string, Task>? ElevationCancelledSeam { get; set; }

    /// <summary>
    /// AI text for display only (title, why, step why): control and format characters (bidi, zero-width) removed,
    /// whitespace collapsed. Null when nothing is left. Never cut here: a caller compares the length with its cap.
    /// </summary>
    internal static string? PlainText(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var rune in s.EnumerateRunes())
        {
            var cat = Rune.GetUnicodeCategory(rune);
            if (Rune.IsWhiteSpace(rune) || cat is UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                space = sb.Length > 0;
                continue;
            }
            if (cat is UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned) continue;
            if (space) { sb.Append(' '); space = false; }
            sb.Append(rune.ToString());
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>The why object of a plan: {text, by}, or null.</summary>
    private static JsonObject? WhyOf(string? text, string? by) =>
        text is null ? null : new JsonObject { ["text"] = text, ["by"] = string.IsNullOrEmpty(by) ? "AI" : by };

    /// <summary>One resolved (or refused) step before ordering. At = the AI's position.</summary>
    private sealed record FixCandidate(int At, string ActionId, IActionHandler? Handler, ActionResolution Resolution, string Privilege,
        JsonObject? Parameters, string? Why, JsonObject? UndoOf, string? Label);

    private static ActionResolution StepIneligible(string code, string message) => ActionResolution.Ineligible(code, message);

    /// <summary>Resolves one step from live state; only enabled setting-kind handlers may be steps; administrator steps wait for the broker.</summary>
    private async Task<FixCandidate> ResolveStepAsync(int at, FixStepRequest req, string? findingSubject)
    {
        var handler = Handler(req.ActionId);
        ActionResolution r;
        if (handler is null || Disabled(req.ActionId) is not (null or AdminUnavailable) || handler.Kind != "setting")
            r = StepIneligible("not_supported", "This change can't be a step of a fix.");
        else if (req.TargetRef is { Evidence: null })
            r = StepIneligible(UnknownTarget, "Optimus can't match that target to evidence from this report or task.");
        else
            r = await ResolveAsync(handler, new ActionRequest(req.ActionId, null, req.Parameters?.DeepClone() as JsonObject, findingSubject)
            {
                Evidence = req.TargetRef?.Evidence,
            }).ConfigureAwait(false);
        return Checked(new FixCandidate(at, req.ActionId, handler, r, PrivilegeOrDefault(handler, r), req.Parameters, req.Why, null, null));
    }

    private static string PrivilegeOrDefault(IActionHandler? h, ActionResolution r) => r.Privilege ?? h?.Privilege ?? "none";

    /// <summary>The checks every eligible step gets after its handler resolved it: administrator steps and the row cap.</summary>
    private FixCandidate Checked(FixCandidate c)
    {
        if (!c.Resolution.Eligible) return c;
        // m9 §3.5, D6: administrator steps run through one broker session, only with --enable-admin-actions.
        if (c.Privilege == "admin" && !FixAdminAllowed(c.ActionId))
            return c with { Resolution = StepIneligible(AdminUnavailable, "Administrator steps aren't available in this version.") };
        if (c.Resolution.Rows.Count > FixCaps.Rows)
            return c with { Resolution = StepIneligible("too_large", "This step has more to show than the approval can.") };
        return c;
    }

    /// <summary>
    /// propose_fix: resolves every step and stores the fix plan (awaiting_user, or ineligible with per-step reasons). The
    /// AI's title and why are display text only; a text or step count over the dialog's caps makes the plan too_large.
    /// </summary>
    internal async Task<JsonObject> CreateFixAsync(string? title, string? why, string? by, IReadOnlyList<FixStepRequest> steps, string? findingId, RequestedBy requestedBy,
        CardBinding? card = null)
    {
        (string ReportId, JsonObject Report, JsonObject Finding)? found = findingId is null ? null
            : _store.Read(db => FindFinding(db, findingId)) ?? throw RpcError.NotFound("finding", findingId);
        var cleanTitle = PlainText(title);
        var cleanWhy = PlainText(why);
        string? tooLarge = steps.Count == 0 ? "A fix needs at least one step."
            : steps.Count > FixCaps.Steps ? $"More than {FixCaps.Steps} steps."
            : cleanTitle?.Length > FixCaps.Title ? "The title is too long."
            : cleanWhy?.Length > FixCaps.Text ? "The reason is too long."
            : steps.Any(s => PlainText(s.Why)?.Length > FixCaps.StepWhy) ? "A step's reason is too long."
            : null;
        var candidates = new List<FixCandidate>();
        if (tooLarge is null)
            for (var i = 0; i < steps.Count; i++)
            {
                var c = await ResolveStepAsync(i, steps[i], found?.Finding["subject"]?.GetValue<string>()).ConfigureAwait(false);
                candidates.Add(c with { Why = PlainText(steps[i].Why) });
            }
        return _store.Write(db => InsertFix(db, cleanTitle, WhyOf(cleanWhy, by), candidates, tooLarge, found, requestedBy, undoOf: null, card));
    }

    /// <summary>The step's execution-relevant fields (its contentHash covers exactly these).</summary>
    private static JsonObject ExecutionFields(JsonObject step) => new()
    {
        ["actionId"] = step["actionId"]?.DeepClone(), ["handlerVersion"] = step["handlerVersion"]?.DeepClone(), ["target"] = step["target"]?.DeepClone(),
        ["before"] = step["before"]?.DeepClone(), ["after"] = step["after"]?.DeepClone(), ["parameters"] = step["parameters"]?.DeepClone(),
        ["privilege"] = step["privilege"]?.DeepClone(), ["flags"] = step["flags"]?.DeepClone(), ["resources"] = step["resources"]?.DeepClone(),
    };

    /// <summary>A fix plan's hash: every step field (including its contentHash and why), title, why, expiresAt and the plan's identity.</summary>
    internal static string HashFix(JsonObject plan)
    {
        var fields = new JsonObject
        {
            ["planId"] = plan["planId"]?.DeepClone(), ["kind"] = plan["kind"]?.DeepClone(), ["findingId"] = plan["findingId"]?.DeepClone(),
            ["title"] = plan["title"]?.DeepClone(), ["why"] = plan["why"]?.DeepClone(), ["flags"] = plan["flags"]?.DeepClone(),
            ["privilege"] = plan["privilege"]?.DeepClone(), ["resources"] = plan["resources"]?.DeepClone(), ["undoOf"] = plan["undoOf"]?.DeepClone(),
            ["steps"] = plan["steps"]?.DeepClone(), ["expiresAt"] = plan["expiresAt"]?.DeepClone(),
        };
        // m9 §17.1: the left-out steps are shown, so they are bound too (only on plans that have them: older hashes stay).
        if (plan["leftOut"] is JsonArray leftOut) fields["leftOut"] = leftOut.DeepClone();
        // m9 §17.3: a narrowed plan binds its parent and the steps switched off (only on such plans: older hashes stay).
        if (plan["narrowedFrom"] is JsonValue narrowedFrom) fields["narrowedFrom"] = narrowedFrom.DeepClone();
        if (plan["removed"] is JsonArray removed) fields["removed"] = removed.DeepClone();
        return Json.Digest(fields);
    }

    private static JsonArray RowsJson(IReadOnlyList<PlanRow> rows) =>
        new(rows.Select(row => (JsonNode?)new JsonObject { ["label"] = row.Label, ["before"] = row.Before, ["after"] = row.After }).ToArray());

    /// <summary>The §4 manifest step of a candidate at execution position n.</summary>
    private static JsonObject ManifestStep(FixCandidate c, int n, ActionResolution r)
    {
        var h = c.Handler;
        var label = c.Label ?? h?.Label ?? c.ActionId;
        var step = r.Eligible
            ? new JsonObject
            {
                ["n"] = n, ["actionId"] = c.ActionId, ["handlerVersion"] = h!.Version, ["label"] = label, ["what"] = h.What,
                ["target"] = r.Target.DeepClone(), ["before"] = r.Before.DeepClone(), ["after"] = r.After.DeepClone(),
                ["parameters"] = c.Parameters?.DeepClone(), ["rows"] = RowsJson(r.Rows),
                ["context"] = new JsonArray(r.Context.Select(kv => (JsonNode?)new JsonArray(kv.Key, kv.Value)).ToArray()),
                ["tradeoffs"] = Json.Array(r.Tradeoffs), ["privilege"] = c.Privilege, ["flags"] = Json.Array(FlagsOf(h, r)),
                ["undo"] = UndoOf(h, r),
                ["resources"] = Json.Array(ResourcesOf(h, r)),
            }
            : new JsonObject
            {
                ["n"] = n, ["actionId"] = c.ActionId, ["handlerVersion"] = h?.Version, ["label"] = label, ["what"] = h?.What,
                ["target"] = null, ["before"] = null, ["after"] = null, ["parameters"] = c.Parameters?.DeepClone(),
                ["rows"] = new JsonArray(), ["context"] = new JsonArray(), ["tradeoffs"] = new JsonArray(), ["privilege"] = c.Privilege,
                ["flags"] = new JsonArray(), ["undo"] = new JsonObject { ["supported"] = false, ["conditions"] = null }, ["resources"] = new JsonArray(),
            };
        step["contentHash"] = Json.Digest(ExecutionFields(step));
        step["why"] = c.Why;
        if (c.UndoOf is not null) step["undoOf"] = c.UndoOf.DeepClone();
        return step;
    }

    /// <summary>
    /// Orders the candidates (user scope, then administrator; each group in the AI's order), marks a step whose resources
    /// overlap an earlier step's (conflict), builds the manifest and the §4.1 top level, and stores it with its event.
    /// An AI's fix (m9 §1.3, §17.1) first rechecks the requesting task in this transaction; an administrator step its grant
    /// refuses becomes admin_not_allowed (the flag's admin_unavailable wins), and steps blocked only by administrator rights
    /// are left out (<c>leftOut</c>) when every other step can be made; when all are blocked the plan is ineligible.
    /// </summary>
    private JsonObject InsertFix(Db db, string? title, JsonObject? why, IReadOnlyList<FixCandidate> candidates, string? tooLarge,
        (string ReportId, JsonObject Report, JsonObject Finding)? found, RequestedBy by, string? undoOf, CardBinding? card = null)
    {
        IReadOnlyList<FixCandidate> leftOut = [];
        if (by.TaskId is { } taskId)
        {
            var grant = CheckRequester(db, taskId, card?.CardId, otherThan: null, level: FixKind);
            candidates = [.. candidates.Select(c => c.Resolution.Eligible && c.Privilege == "admin" && AdminRefusal(grant, flagOn: true) is { } refusal
                ? c with { Resolution = StepIneligible(refusal.Code, refusal.Message) } : c)];
            var adminBlocked = candidates.Where(AdminBlocked).ToList();
            if (tooLarge is null && adminBlocked.Count > 0 && adminBlocked.Count < candidates.Count)
            {
                var rest = candidates.Where(c => !AdminBlocked(c)).ToList();
                if (Layout(rest).Blocked.Count == 0)
                {
                    candidates = rest;
                    leftOut = adminBlocked;
                }
            }
        }
        var (steps, blocked, firstManual) = Layout(candidates);

        JsonObject? ineligible = tooLarge is not null
            ? new JsonObject { ["code"] = "too_large", ["message"] = tooLarge, ["steps"] = new JsonArray(), ["manualUri"] = null }
            : blocked.Count > 0
                ? Paths.Add(new JsonObject
                {
                    ["code"] = blocked[0]!.Text("code"), ["message"] = $"Step {blocked[0]!["n"]}: {blocked[0]!.Text("message")}",
                    ["steps"] = blocked, ["manualUri"] = firstManual,
                }, firstManual)
                : null;
        var stepObjects = steps.OfType<JsonObject>().ToList();
        var flags = ActionFlags.Union([.. stepObjects.Select(s => (IReadOnlyList<string>)Json.Strings(s["flags"]).ToList())]) ?? [];
        var resources = ineligible is null ? ResourceKeys.Normalize(stepObjects.SelectMany(s => Json.Strings(s["resources"]))) : [];

        var planId = "plan-" + Ids.RandomHex();
        var now = _time.GetUtcNow();
        var plan = new JsonObject
        {
            ["planId"] = planId, ["kind"] = FixKind, ["actionId"] = null, ["handlerVersion"] = null, ["label"] = null,
            ["findingId"] = found?.Finding.Text("id"), ["reportId"] = found?.ReportId,
            ["title"] = title, ["why"] = why?.DeepClone(),
            ["target"] = null, ["before"] = null, ["after"] = null,
            ["rows"] = new JsonArray(), ["context"] = new JsonArray(), ["tradeoffs"] = new JsonArray(),
            ["privilege"] = stepObjects.Any(s => s.Text("privilege") == "admin") ? "admin" : "none",
            ["flags"] = Json.Array(flags),
            ["resources"] = Json.Array(resources),
            ["undo"] = new JsonObject { ["supported"] = stepObjects.Any(s => s["undo"]?["supported"]?.GetValue<bool>() == true), ["conditions"] = null },
            ["requestedBy"] = by.ToJson(),
            ["undoOf"] = undoOf,
            ["steps"] = tooLarge is null ? steps : new JsonArray(),
            ["createdAt"] = Time.Format(now), ["expiresAt"] = Time.Format(now + _options.PlanLifetime),
            ["status"] = ineligible is null ? "awaiting_user" : "ineligible",
            ["ineligible"] = ineligible,
        };
        // m9 §17.1: display only (handler text, never executed), in the AI's order; inside planHash.
        if (leftOut.Count > 0)
            plan["leftOut"] = new JsonArray([.. leftOut.OrderBy(c => c.At).Select(c => (JsonNode?)new JsonObject
            {
                ["actionId"] = c.ActionId, ["label"] = c.Label ?? c.Handler?.Label ?? c.ActionId, ["what"] = c.Handler?.What, ["code"] = c.Resolution.IneligibleCode,
            })]);
        BindCard(db, plan, card, by); // m9 §17.3
        plan["planHash"] = HashFix(plan);
        db.Exec("INSERT INTO action_plans (plan_id, body, status, task_id, target_key, created_at, expires_at, card_id) VALUES ($p, $b, $s, $t, $k, $c, $e, $card)",
            ("$p", planId), ("$b", Json.Write(plan)), ("$s", plan.Text("status")), ("$t", by.TaskId), ("$k", "fix:" + planId),
            ("$c", plan.Text("createdAt")), ("$e", plan.Text("expiresAt")), ("$card", plan["cardId"]?.GetValue<string>()));
        PlanEvent(db, plan);
        if (PendingRecord.Value is { Written: false } pending) WriteKey(db, pending, planId);
        return plan;
    }

    /// <summary>A step refused only for administrator rights: the flag (admin_unavailable) or the grant (admin_not_allowed).</summary>
    private static bool AdminBlocked(FixCandidate c) => !c.Resolution.Eligible && c.Resolution.IneligibleCode is AdminUnavailable or AdminNotAllowed;

    /// <summary>
    /// The manifest steps in execution order (user scope first, then administrator; each group in the AI's order), with a
    /// step whose resources overlap an earlier eligible step's marked conflict; the blocked rows; the first manual URI.
    /// </summary>
    private static (JsonArray Steps, JsonArray Blocked, string? FirstManual) Layout(IReadOnlyList<FixCandidate> candidates)
    {
        var ordered = candidates.OrderBy(c => c.Privilege == "admin" ? 1 : 0).ThenBy(c => c.At).ToList();
        var steps = new JsonArray();
        var blocked = new JsonArray();
        string? firstManual = null;
        for (var i = 0; i < ordered.Count; i++)
        {
            var c = ordered[i];
            var r = c.Resolution;
            if (r.Eligible)
                for (var j = 0; j < i; j++)
                {
                    var earlier = ordered[j];
                    if (!earlier.Resolution.Eligible) continue;
                    var mine = ResourcesOf(c.Handler!, r);
                    var theirs = ResourcesOf(earlier.Handler!, earlier.Resolution);
                    if (mine.Any(a => theirs.Any(b => ResourceKeys.Conflict(a, b))))
                    {
                        r = StepIneligible("conflict", $"Changes the same thing as step {j + 1}.");
                        break;
                    }
                }
            if (!r.Eligible)
            {
                blocked.Add(new JsonObject { ["n"] = i + 1, ["status"] = "ineligible", ["code"] = r.IneligibleCode, ["message"] = r.IneligibleMessage });
                firstManual ??= r.ManualUri;
            }
            steps.Add(ManifestStep(c, i + 1, r));
        }
        return (steps, blocked, firstManual);
    }

    private static bool IsFix(JsonObject plan) => plan["kind"] is JsonValue k && k.TryGetValue<string>(out var s) && s == FixKind;

    private static IReadOnlyList<JsonObject> StepsOf(JsonObject plan) => (plan["steps"] as JsonArray ?? []).OfType<JsonObject>().ToList();

    /// <summary>A fix plan's lock set: the union stored on the plan.</summary>
    private static IReadOnlyList<string> FixResources(JsonObject plan) => Json.Strings(plan["resources"]).ToList();

    /// <summary>The engine request that re-resolves a stored step (its own target; undo steps carry the engine's restore).</summary>
    private static ActionRequest StepRequest(JsonObject step) =>
        new(step.Text("actionId"), step["target"]?.DeepClone() as JsonObject, step["parameters"]?.DeepClone() as JsonObject, null)
        {
            Undo = step["undoOf"] is JsonObject,
            Recheck = true,
        };

    /// <summary>
    /// The live resolution equals the approved step in every execution-relevant field (m9 §4): handler version, target,
    /// before, after, privilege, flags and resources (parameters are the step's own, sent back to the handler).
    /// </summary>
    private static bool StepMatches(IActionHandler h, ActionResolution live, JsonObject step) =>
        live.Eligible
        && h.Version == step.Text("handlerVersion")
        && Same(live.Target, step["target"]) && Same(live.Before, step["before"]) && Same(live.After, step["after"])
        && PrivilegeOf(h, live) == step.Text("privilege")
        && Same(Json.Array(FlagsOf(h, live)), step["flags"] ?? new JsonArray())
        && Same(Json.Array(ResourcesOf(h, live)), step["resources"] ?? new JsonArray());

    /// <summary>
    /// Inside the plan's locks, before anything runs: every step re-resolves and must still match (else the whole plan is
    /// stale_plan, nothing written). Returns the live resolutions in step order.
    /// </summary>
    private async Task<List<ActionResolution>> RecheckFixAsync(string planId, JsonObject plan)
    {
        var live = new List<ActionResolution>();
        string? manualUri = null;
        var fresh = true;
        foreach (var step in StepsOf(plan))
        {
            var h = Handler(step.Text("actionId"));
            var r = h is null || Disabled(step.Text("actionId")) is not null || h.Kind != "setting"
                ? ActionResolution.Ineligible("unavailable", "Unavailable.")
                : await ResolveAsync(h, StepRequest(step)).ConfigureAwait(false);
            // An administrator step needs the flag and the broker (m9 D6); it is re-resolved by the broker itself as well.
            if (h is null || !StepMatches(h, r, step) || step.Text("privilege") == "admin" && !FixAdminAllowed(step.Text("actionId")))
            {
                fresh = false;
                manualUri ??= r.ManualUri;
                break;
            }
            live.Add(r);
        }
        if (fresh) return live;
        var current = _store.Write(db =>
        {
            var stale = LoadPlan(db, planId);
            SavePlan(db, stale, "ineligible", new JsonObject
            {
                ["code"] = "stale_plan", ["message"] = "Something changed since the preview. Preview again.", ["manualUri"] = manualUri, ["steps"] = new JsonArray(),
            });
            return stale;
        });
        throw new RpcError(ErrorCodes.StaleRevision, "The plan no longer matches the PC.", new JsonObject { ["reason"] = "stale_plan", ["current"] = current });
    }

    // ---------------------------------------------------------------- per-step journal (operation_step_states)

    private static JsonObject StepRow(string state, int n) => new()
    {
        ["n"] = n, ["state"] = state, ["preimage"] = null, ["dispatchedAt"] = null, ["observed"] = null, ["receipt"] = null,
        ["undo"] = new JsonObject { ["available"] = false, ["reason"] = "not_verified" }, ["error"] = null,
    };

    private static string? W(JsonNode? n) => n is null ? null : Json.Write(n);

    private static void SaveStepRow(Db db, string operationId, JsonObject row) =>
        db.Exec("""
            INSERT INTO operation_step_states (operation_id, n, state, preimage, dispatched_at, observed, receipt, undo, error, updated_at)
            VALUES ($o, $n, $s, $p, $d, $ob, $r, $u, $e, $t)
            ON CONFLICT (operation_id, n) DO UPDATE SET state = $s, preimage = $p, dispatched_at = $d, observed = $ob, receipt = $r, undo = $u, error = $e, updated_at = $t
            """,
            ("$o", operationId), ("$n", row["n"]!.GetValue<int>()), ("$s", row.Text("state")), ("$p", W(row["preimage"])), ("$d", row["dispatchedAt"]?.GetValue<string>()),
            ("$ob", W(row["observed"])), ("$r", W(row["receipt"])), ("$u", W(row["undo"])), ("$e", W(row["error"])), ("$t", db.Now()));

    internal static List<JsonObject> LoadStepRows(Db db, string operationId) =>
        db.Query("SELECT n, state, preimage, dispatched_at, observed, receipt, undo, error FROM operation_step_states WHERE operation_id = $o ORDER BY n",
            r => new JsonObject
            {
                ["n"] = r.GetInt32(0), ["state"] = r.GetString(1),
                ["preimage"] = r.IsDBNull(2) ? null : Json.Parse(r.GetString(2)), ["dispatchedAt"] = r.IsDBNull(3) ? null : r.GetString(3),
                ["observed"] = r.IsDBNull(4) ? null : Json.Parse(r.GetString(4)), ["receipt"] = r.IsDBNull(5) ? null : Json.Parse(r.GetString(5)),
                ["undo"] = r.IsDBNull(6) ? null : Json.Parse(r.GetString(6)), ["error"] = r.IsDBNull(7) ? null : Json.Parse(r.GetString(7)),
            }, ("$o", operationId));

    /// <summary>Creates the fix operation (state prepared, every step pending) and moves the plan to running, with its events.</summary>
    private string PrepareFix(Db db, string planId)
    {
        var plan = LoadPlan(db, planId);
        var operationId = "op-" + Ids.RandomHex();
        var op = new JsonObject
        {
            ["operationId"] = operationId, ["planId"] = planId, ["actionId"] = null, ["kind"] = FixKind, ["state"] = "prepared",
            ["receipt"] = null, ["undo"] = new JsonObject { ["available"] = false, ["reason"] = "not_verified" }, ["error"] = null, ["progress"] = null,
            ["findingId"] = plan["findingId"]?.DeepClone(), ["requestedBy"] = plan["requestedBy"]?.DeepClone(),
            ["createdAt"] = db.Now(), ["updatedAt"] = db.Now(),
        };
        // m9 §17.3: the receipt keeps what was left out (administrator rights) and what the user switched off (action.narrow).
        if (plan["leftOut"] is JsonArray leftOut) op["leftOut"] = leftOut.DeepClone();
        if (plan["removed"] is JsonArray removed) op["removed"] = removed.DeepClone();
        db.Exec("INSERT INTO operations (operation_id, plan_id, body, state, created_at) VALUES ($o, $p, $b, 'prepared', $t)",
            ("$o", operationId), ("$p", planId), ("$b", Json.Write(op)), ("$t", db.Now()));
        foreach (var step in StepsOf(plan)) SaveStepRow(db, operationId, StepRow("pending", step["n"]!.GetValue<int>()));
        Step(db, operationId, "prepared", null);
        SavePlan(db, plan, "running");
        StateEvent(db, op);
        return operationId;
    }

    /// <summary>One step transition, committed with its operation.state event (step {n, state}, progress "Step n of k").</summary>
    private void StepTransition(string operationId, int n, int k, Action<JsonObject, JsonObject> change, string? opState = null)
    {
        _store.Write(db =>
        {
            var op = LoadOperation(db, operationId);
            var row = LoadStepRows(db, operationId).First(r => r["n"]!.GetValue<int>() == n);
            change(row, op);
            SaveStepRow(db, operationId, row);
            if (opState is not null) op["state"] = opState;
            op["progress"] = $"Step {n} of {k}";
            Step(db, operationId, row.Text("state"), $"step {n}");
            SaveOperation(db, op);
            StateEvent(db, op, (n, row.Text("state")));
        });
    }

    /// <summary>The background run of an approved fix: each step in order, stopping at the first that is not verified.</summary>
    private async Task RunFixAsync(string operationId, JsonObject plan)
    {
        var steps = StepsOf(plan);
        var k = steps.Count;
        using var stop = new CancellationTokenSource();
        var pending = new List<Task>(); // handlers (or a broker session) still running after their deadline
        lock (_gate)
        {
            _stopSignals[operationId] = stop;
            if (_stopRequests.Contains(operationId)) stop.Cancel();
        }
        try
        {
            for (var i = 0; i < k; i++)
            {
                var step = steps[i];
                var n = step["n"]!.GetValue<int>();
                var h = Handler(step.Text("actionId"))!;
                if (i > 0)
                {
                    lock (_gate) if (_stopRequests.Contains(operationId)) break;
                    if (_stopping.IsCancellationRequested) break;
                }
                // m9 §4: user steps run first, one by one in the host; the administrator steps then go to the broker together.
                if (step.Text("privilege") == "admin")
                {
                    await RunAdminStepsAsync(operationId, plan, [.. steps.Skip(i)], k, stop.Token, pending.Add).ConfigureAwait(false);
                    break;
                }
                // Immediately before its write every step, the first included, must still match exactly (resolving the steps
                // after it at approval can itself have changed it); a mismatch fails this step (stale_plan), nothing is written
                // for it and the rest are skipped.
                var live = await ResolveAsync(h, StepRequest(step)).ConfigureAwait(false);
                if (!StepMatches(h, live, step))
                {
                    StaleStep(operationId, n, k);
                    break;
                }

                StepTransition(operationId, n, k, (row, _) =>
                {
                    row["state"] = "prepared";
                    row["preimage"] = live.Before.DeepClone();
                });
                StepTransition(operationId, n, k, (row, _) =>
                {
                    row["state"] = "attempted";
                    row["dispatchedAt"] = Now();
                }, opState: "attempted");

                var execution = new ActionExecution(plan.Text("planId"), plan.Text("planHash"), step["target"]!.DeepClone().AsObject(),
                    step["before"]!.DeepClone().AsObject(), step["after"]!.DeepClone().AsObject(), step["parameters"]?.DeepClone() as JsonObject);
                ActionOutcome outcome;
                Task<ActionOutcome>? work = null;
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                    deadline.CancelAfter(_options.SettingTimeout);
                    work = h.ExecuteAsync(execution, null, deadline.Token);
                    outcome = await work.WaitAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    outcome = new ActionOutcome("indeterminate", [], "The result could not be confirmed in time.", "timeout", "No answer in time.");
                }
                catch (Exception ex)
                {
                    AppLog.Current.Warn($"fix: step {h.ActionId}: execute failed ({ex.GetType().Name})", ErrorSites.FixStepExecuteFailed);
                    outcome = new ActionOutcome("indeterminate", [], "The result could not be confirmed.", "error", ex.GetType().Name);
                }
                if (Straggler(work, h.ActionId) is { } late) pending.Add(late);
                var verified = false;
                StepTransition(operationId, n, k, (row, _) => verified = ApplyOutcome(row, step, h, outcome, reconciled: false, Now()));
                if (verified && step["undoOf"] is JsonObject undoOf) _store.Write(db => MarkUndone(db, undoOf));
                if (!verified) break;
            }
        }
        catch (Exception ex) { AppLog.Current.Error($"fix: {operationId}: run failed ({ex.GetType().Name})", ErrorSites.FixRunFailed); }
        finally
        {
            lock (_gate)
            {
                _stopRequests.Remove(operationId);
                _stopSignals.Remove(operationId);
            }
            try { _store.Write(db => FinishFix(db, operationId)); }
            catch (Exception ex) { AppLog.Current.Error($"fix: {operationId}: finish failed ({ex.GetType().Name})", ErrorSites.FixRunFailed); }
            // The plan's locks (released by the caller when this returns) stay held while a late write may still land, or a
            // broker process (also after a clean answer) has not exited yet (m9 §13 R2).
            if (pending.Count > 0) await HoldAsync(Task.WhenAll(pending)).ConfigureAwait(false);
        }
    }

    /// <summary>A step that no longer matches its approved resolution: failed (stale_plan); the rest are skipped.</summary>
    private void StaleStep(string operationId, int n, int k) =>
        StepTransition(operationId, n, k, (row, _) =>
        {
            row["state"] = "failed";
            row["error"] = new JsonObject { ["code"] = "stale_plan", ["message"] = "This changed since the preview, so it was not made." };
            row["undo"] = new JsonObject { ["available"] = false, ["reason"] = "not_verified" };
        });

    /// <summary>
    /// The administrator steps of a fix (m9 §3.5, §4): each re-resolved in the host first (the steps up to the first that
    /// drifted are sent; that one fails stale_plan), all marked dispatched, then ONE broker session in the plan's order.
    /// The broker's protected journal is the first record of each step; the host's rows follow from its answer.
    /// </summary>
    private async Task RunAdminStepsAsync(string operationId, JsonObject plan, IReadOnlyList<JsonObject> steps, int k, CancellationToken stop, Action<Task> straggler)
    {
        var send = new List<(int N, IActionHandler Handler, ElevatedStep Step, JsonObject Manifest)>();
        int? stale = null;
        foreach (var step in steps)
        {
            var n = step["n"]!.GetValue<int>();
            var h = Handler(step.Text("actionId"));
            var live = h is null ? null : await ResolveAsync(h, StepRequest(step)).ConfigureAwait(false);
            if (h is null || live is null || !StepMatches(h, live, step) || !FixAdminAllowed(step.Text("actionId")))
            {
                stale = n;
                break;
            }
            var undoOf = step["undoOf"] is JsonObject u ? _store.Read(db => BrokerRecordOf(db, u.Text("operationId"), u["n"]?.GetValue<int>())) : null;
            send.Add((n, h, ElevatedStepOf(n, h, step["target"]!.AsObject(), step["before"]!.AsObject(), step["after"]!.AsObject(),
                step["parameters"] as JsonObject, undoOf), step));
        }
        if (send.Count == 0)
        {
            if (stale is { } s) StaleStep(operationId, s, k);
            return;
        }
        if (stop.IsCancellationRequested || _stopping.IsCancellationRequested) return; // the rest stay pending → skipped

        // Dispatched, not unstarted: a restart reads the broker's journal before it classifies these rows (m9 §3.5).
        var at = Now();
        foreach (var s in send)
            StepTransition(operationId, s.N, k, (row, _) =>
            {
                row["state"] = "attempted";
                row["dispatchedAt"] = at;
            }, opState: "attempted");

        var execution = new ActionExecution(plan.Text("planId"), plan.Text("planHash"), [], [], [], null);
        ElevatedResult result;
        try { result = await ElevateAsync(execution, [.. send.Select(s => (s.N, s.Handler, s.Step))], stop, straggler: straggler).ConfigureAwait(false); }
        catch (Exception ex)
        {
            AppLog.Current.Warn($"fix: {operationId}: broker failed ({ex.GetType().Name})", ErrorSites.FixBrokerFailed);
            result = new ElevatedResult([], null, true);
        }

        var allVerified = true;
        foreach (var s in send)
        {
            var outcome = result.Steps.FirstOrDefault(o => o.N == s.N).Outcome
                ?? new ActionOutcome(result.Dispatched ? "indeterminate" : "failed", [], "The result could not be confirmed.", result.Dispatched ? "bad_response" : "broker_not_started");
            var verified = false;
            StepTransition(operationId, s.N, k, (row, _) => verified = ApplyOutcome(row, s.Manifest, s.Handler, outcome, reconciled: false, Now(), result.ReceiptId));
            if (verified && s.Manifest["undoOf"] is JsonObject undoOf) _store.Write(db => MarkUndone(db, undoOf));
            allVerified &= verified;
        }
        if (allVerified && stale is { } drifted) StaleStep(operationId, drifted, k);
    }

    /// <summary>
    /// Applies a step outcome to its row: verified (receipt, undo) | failed | indeterminate | skipped (the broker never
    /// prepared it). Returns true when verified. A verified administrator step keeps the broker's record ID for its undo.
    /// </summary>
    private static bool ApplyOutcome(JsonObject row, JsonObject step, IActionHandler? h, ActionOutcome outcome, bool reconciled, string now,
        string? brokerReceiptId = null)
    {
        var state = outcome.State switch
        {
            "verified" or "completed" => "verified",
            "failed" or "declined" or "cancelled" => "failed",
            "skipped" => "skipped",
            _ => "indeterminate",
        };
        row["state"] = state;
        row["observed"] = outcome.Observed.DeepClone();
        if (state == "skipped")
        {
            row["undo"] = new JsonObject { ["available"] = false, ["reason"] = "not_verified" };
            row["error"] = null;
            return false;
        }
        if (state == "verified")
        {
            row["receipt"] = new JsonObject
            {
                ["receiptId"] = "rcpt-" + Ids.RandomHex(), ["verifiedAt"] = now, ["observed"] = outcome.Observed.DeepClone(),
                ["text"] = outcome.ReceiptText,
            };
            if (brokerReceiptId is not null) row["receipt"]!["brokerReceiptId"] = brokerReceiptId;
            var undoable = h is not null && h.SupportsUndo && step["undo"]?["supported"]?.GetValue<bool>() == true;
            row["undo"] = undoable ? new JsonObject { ["available"] = true, ["reason"] = null } : new JsonObject { ["available"] = false, ["reason"] = "not_supported" };
            row["error"] = null;
            if (reconciled) row["note"] = "authorship uncertain after interruption";
        }
        else
        {
            row["undo"] = new JsonObject { ["available"] = false, ["reason"] = "not_verified" };
            row["error"] = new JsonObject { ["code"] = outcome.Code ?? state, ["message"] = outcome.Message ?? outcome.ReceiptText };
        }
        return state == "verified";
    }

    /// <summary>
    /// Ends a fix operation from its steps (m9 §4): pending → skipped; all verified → verified; any indeterminate →
    /// indeterminate; none verified → failed; otherwise partial. The plan's status follows; the final event has no step.
    /// </summary>
    private void FinishFix(Db db, string operationId)
    {
        var op = LoadOperation(db, operationId);
        var plan = LoadPlan(db, op.Text("planId"));
        var rows = LoadStepRows(db, operationId);
        foreach (var row in rows.Where(r => r.Text("state") is "pending" or "prepared" or "attempted"))
        {
            // prepared/attempted rows here never reached the writer's answer (a crash is reconciled before this): skipped
            // only when nothing was dispatched; otherwise unclear.
            var dispatched = row.Text("state") != "pending";
            row["state"] = dispatched ? "indeterminate" : "skipped";
            row["undo"] = new JsonObject { ["available"] = false, ["reason"] = "not_verified" };
            if (dispatched) row["error"] = new JsonObject { ["code"] = "interrupted", ["message"] = "The result could not be confirmed." };
            SaveStepRow(db, operationId, row);
        }
        var states = rows.Select(r => r.Text("state")).ToList();
        var done = states.Count(s => s == "verified");
        var state = done == rows.Count ? "verified" : states.Contains("indeterminate") ? "indeterminate" : done == 0 ? "failed" : "partial";
        var bad = rows.FirstOrDefault(r => r.Text("state") is "failed" or "indeterminate");
        op["state"] = state;
        op["progress"] = null;
        op["receipt"] = null;
        op["error"] = bad is null ? null : new JsonObject
        {
            ["code"] = bad.Text("state") == "failed" ? "step_failed" : "step_indeterminate",
            ["message"] = $"Step {bad["n"]} {(bad.Text("state") == "failed" ? "failed" : "is unclear")}.",
            ["n"] = bad["n"]!.GetValue<int>(),
        };
        op["undo"] = OperationUndo(rows, done);
        Step(db, operationId, state, op["error"]?["code"]?.GetValue<string>());
        SaveOperation(db, op);
        // Nothing written because UAC or the broker's own window was cancelled: the plan is declined, as in m4 (m9 §4).
        var declined = done == 0 && bad?["error"]?["code"]?.GetValue<string>() is "uac_cancelled" or "broker_cancelled";
        if (plan.Text("status") is "running" or "approved") SavePlan(db, plan, declined ? "declined" : state,
            declined ? new JsonObject { ["code"] = bad!["error"]!.Text("code"), ["message"] = "Approval was cancelled in Windows." } : null);
        StateEvent(db, op);
        // m9 §17.3: a verified, not reduced fix closes its card (by plan.cardId, else by finding); a reduced one leaves it open.
        if (state == "verified") CloseCard(db, plan, operationId, op["undo"]!["available"]!.GetValue<bool>());
    }

    private static JsonObject OperationUndo(IReadOnlyList<JsonObject> rows, int verified) =>
        rows.Any(r => r["undo"]?["available"]?.GetValue<bool>() == true) ? new JsonObject { ["available"] = true, ["reason"] = null }
        : rows.Any(r => r["undo"]?["reason"]?.GetValue<string>() == "undone") ? new JsonObject { ["available"] = false, ["reason"] = "undone" }
        : new JsonObject { ["available"] = false, ["reason"] = verified > 0 ? "not_supported" : "not_verified" };

    /// <summary>An undo step verified: the original step's undo is used up (reason undone).</summary>
    private static void MarkUndone(Db db, JsonObject undoOf)
    {
        var operationId = undoOf.Text("operationId");
        var n = undoOf["n"]?.GetValue<int>() ?? 0;
        if (db.Scalar("SELECT 1 FROM operations WHERE operation_id = $o", ("$o", operationId)) is null) return;
        var rows = LoadStepRows(db, operationId);
        if (rows.FirstOrDefault(r => r["n"]!.GetValue<int>() == n) is not { } row) return;
        row["undo"] = new JsonObject { ["available"] = false, ["reason"] = "undone" };
        SaveStepRow(db, operationId, row);
        var op = LoadOperation(db, operationId);
        op["undo"] = OperationUndo(rows, rows.Count(r => r.Text("state") == "verified"));
        SaveOperation(db, op);
        StateEvent(db, op);
    }

    /// <summary>The fix operation as the API returns it: steps [{n, state, receipt, undo, error?}], the transition history in history.</summary>
    private static JsonObject FixOperationView(Db db, JsonObject op)
    {
        var operationId = op.Text("operationId");
        op["steps"] = new JsonArray(LoadStepRows(db, operationId).Select(r =>
        {
            var s = new JsonObject
            {
                ["n"] = r["n"]!.GetValue<int>(), ["state"] = r.Text("state"), ["receipt"] = r["receipt"]?.DeepClone(),
                ["undo"] = r["undo"]?.DeepClone() ?? new JsonObject { ["available"] = false, ["reason"] = "not_verified" },
            };
            if (r["error"] is JsonObject e) s["error"] = e.DeepClone();
            return (JsonNode?)s;
        }).ToArray());
        op["history"] = History(db, operationId);
        return op;
    }

    private static JsonArray History(Db db, string operationId) =>
        new(db.Query("SELECT state, at, detail FROM operation_steps WHERE operation_id = $o ORDER BY seq",
            r => (JsonNode?)new JsonObject { ["state"] = r.GetString(0), ["at"] = r.GetString(1), ["detail"] = r.IsDBNull(2) ? null : r.GetString(2) },
            ("$o", operationId)).ToArray());

    // ---------------------------------------------------------------- stop

    /// <summary>
    /// operation.stop: a fix's step in flight finishes and no further step starts; a running administrator tool (a repair)
    /// is ended by the broker (its stop frame); a running user-scope provider tool (a Defender scan or update) is cancelled.
    /// Either single operation then ends failed with error code cancelled and its plan cancelled. A Stop that comes before the
    /// single run registers its signal (queued, or just attempted) is kept and applied there: the tool never starts (m9
    /// review R5). A no-op on a finished operation or a setting change.
    /// </summary>
    public JsonNode OperationStop(JsonObject p)
    {
        var operationId = P.Req(p, "operationId", 64);
        AppLog.Current.Debug($"action: stop requested op={operationId}");
        return _store.Read(db =>
        {
            var op = LoadOperation(db, operationId);
            if (op.Text("state") is "prepared" or "attempted")
                lock (_gate)
                {
                    if (op.Text("kind") == FixKind) _stopRequests.Add(operationId);
                    if (_stopSignals.TryGetValue(operationId, out var signal)) signal.Cancel();
                    // m9 review R5: a single provider operation whose run has not registered its signal yet keeps the Stop.
                    else if (_pendingStops.ContainsKey(operationId)) _pendingStops[operationId] = true;
                }
            return (JsonNode)OperationView(db, operationId);
        });
    }

    // ---------------------------------------------------------------- reconcile

    /// <summary>
    /// Restart reconcile of a fix operation, per step (m9 §4): a prepared or attempted step is classified from live state
    /// (== before → failed; == after → verified, authorship uncertain; else indeterminate); a step never prepared is
    /// skipped. Nothing is resumed.
    /// </summary>
    private async Task ReconcileFixAsync(string operationId)
    {
        var (plan, rows) = _store.Read(db =>
        {
            var o = LoadOperation(db, operationId);
            return (LoadPlan(db, o.Text("planId")), LoadStepRows(db, operationId));
        });
        var steps = StepsOf(plan);
        var uncertain = false;
        // m9 §3.5: administrator steps the host had dispatched are classified from the broker's protected journal and live
        // state; a step the journal shows was never prepared is skipped. Read once, before any classification.
        ElevatedJournal? journal = null;
        var journalRead = false;
        foreach (var row in rows.Where(r => r.Text("state") is "prepared" or "attempted"))
        {
            var step = steps.FirstOrDefault(s => s["n"]!.GetValue<int>() == row["n"]!.GetValue<int>());
            var h = step is null ? null : Handler(step.Text("actionId"));
            if (step?.Text("privilege") == "admin")
            {
                if (!journalRead)
                {
                    journal = _options.Elevation?.Journal(plan.Text("planId"));
                    journalRead = true;
                }
                var record = journal?.Steps.FirstOrDefault(j => j.N == row["n"]!.GetValue<int>());
                if (journal is not null && record is not { Prepared: true })
                {
                    ApplyOutcome(row, step, h, new ActionOutcome("skipped", [], "Not started."), reconciled: true, Now());
                    _store.Write(db => SaveStepRow(db, operationId, row));
                    continue;
                }
            }
            JsonObject? live = null;
            if (h is not null && step!["target"] is JsonObject target)
                try
                {
                    using var cts = new CancellationTokenSource(_options.ResolveTimeout);
                    live = await h.ReadTargetAsync(target.DeepClone().AsObject(), cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) { AppLog.Current.Warn($"fix: reconcile {operationId} step {row["n"]}: {ex.GetType().Name}", ErrorSites.FixReconcile); }
            var outcome = live is null
                ? new ActionOutcome("indeterminate", [], "The result could not be confirmed after a restart.", "interrupted", "The app stopped during the change.")
                : Same(live, step!["after"]) ? new ActionOutcome("verified", live, "Confirmed after a restart. Authorship uncertain after interruption.")
                : Same(live, step!["before"]) ? new ActionOutcome("failed", live, "No change was observed after a restart.", "interrupted", "No change observed.")
                : new ActionOutcome("indeterminate", live, "The setting has another value after a restart.", "interrupted", "Unexpected value.");
            var brokerReceipt = step?.Text("privilege") == "admin" ? journal?.ReceiptId : null;
            uncertain |= ApplyOutcome(row, step ?? new JsonObject(), h, outcome, reconciled: true, Now(), brokerReceipt);
            if (outcome.State == "verified" && step?["undoOf"] is JsonObject undoOf) _store.Write(db => MarkUndone(db, undoOf));
            _store.Write(db => SaveStepRow(db, operationId, row));
        }
        _store.Write(db =>
        {
            // Steps after a step that did not verify never start (prepared/attempted were classified above).
            FinishFix(db, operationId);
            var reconciled = LoadOperation(db, operationId);
            reconciled["reconciled"] = true;
            if (uncertain) reconciled["note"] = "authorship uncertain after interruption";
            SaveOperation(db, reconciled);
        });
        AppLog.Current.Info($"fix: reconciled {operationId}");
    }

    // ---------------------------------------------------------------- undo

    /// <summary>action.undo's steps parameter: distinct step numbers 1–20, or null.</summary>
    private static List<int>? UndoSteps(JsonObject p)
    {
        if (p["steps"] is null) return null;
        if (p["steps"] is not JsonArray a || a.Count is 0 or > 20)
            throw RpcError.Params("steps", "steps must be a list of step numbers.");
        var list = new List<int>();
        foreach (var x in a)
        {
            if (x is not JsonValue v || Json.Number(v) is not double d || d != Math.Floor(d) || d is < 1 or > 20)
                throw RpcError.Params("steps", "steps must be a list of step numbers.");
            if (!list.Contains((int)d)) list.Add((int)d);
        }
        return list;
    }

    /// <summary>
    /// Undo of a fix (m9 §4): a new fix plan of the undo requests of the verified, undoable steps (all of them without
    /// <paramref name="wanted"/>), same host-fixed order, each with its own drift check. A named step that is not verified
    /// and undoable → -32013 not_undoable with its n.
    /// </summary>
    private async Task<JsonObject> UndoFixAsync(JsonObject op, JsonObject plan, List<int>? wanted)
    {
        var operationId = op.Text("operationId");
        var rows = _store.Read(db => LoadStepRows(db, operationId));
        var can = rows.Where(r => r.Text("state") == "verified" && r["undo"]?["available"]?.GetValue<bool>() == true)
            .Select(r => r["n"]!.GetValue<int>()).ToList();
        var want = wanted ?? can;
        if (want.Count == 0) throw new RpcError(ErrorCodes.InvalidState, "Nothing here can be undone.", new JsonObject { ["reason"] = "not_undoable" });
        foreach (var n in want)
            if (!can.Contains(n))
                throw new RpcError(ErrorCodes.InvalidState, $"Step {n} can't be undone.", new JsonObject { ["reason"] = "not_undoable", ["n"] = n });

        var findingId = plan["findingId"]?.GetValue<string>();
        var candidates = new List<FixCandidate>();
        foreach (var step in StepsOf(plan).Where(s => want.Contains(s["n"]!.GetValue<int>())))
        {
            var n = step["n"]!.GetValue<int>();
            var row = rows.First(r => r["n"]!.GetValue<int>() == n);
            var h = Handler(step.Text("actionId"));
            var undoOf = new JsonObject { ["operationId"] = operationId, ["receiptId"] = row["receipt"]?["receiptId"]?.DeepClone(), ["n"] = n };
            var label = "Undo: " + step.Text("label");
            var privilege = step.Text("privilege");
            ActionResolution r;
            JsonObject? parameters = null;
            if (h is null) r = StepIneligible("not_supported", "This change cannot be undone.");
            else
            {
                JsonObject? current = null;
                try
                {
                    using var cts = new CancellationTokenSource(_options.ResolveTimeout);
                    current = await h.ReadTargetAsync(step["target"]!.DeepClone().AsObject(), cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not RpcError) { AppLog.Current.Warn($"fix: undo {operationId} step {n}: {ex.GetType().Name}", ErrorSites.FixUndoStepFailed); }
                var request = current is null ? null : h.UndoRequest(step["target"]!.DeepClone().AsObject(), step["before"]!.DeepClone().AsObject());
                if (current is null) r = StepIneligible("unavailable", "The current state could not be read.");
                else if (!Same(current, row["receipt"]?["observed"])) r = StepIneligible("changed_since", "This changed since it was made.");
                else if (request is null) r = StepIneligible("not_supported", "This change cannot be undone.");
                else
                {
                    request = request with { Undo = true }; // engine-set: the handler may restore the recorded preimage
                    parameters = request.Parameters;
                    r = await ResolveAsync(h, request).ConfigureAwait(false);
                    if (r.Eligible) privilege = PrivilegeOf(h, r);
                }
            }
            candidates.Add(Checked(new FixCandidate(n, step.Text("actionId"), h, r, privilege, parameters, null, undoOf, label)));
        }
        return _store.Write(db => InsertFix(db, null, null, candidates, null, findingId is null ? null : FindFinding(db, findingId), RequestedBy.User, operationId));
    }
}

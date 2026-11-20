using System.Text.Json.Nodes;

namespace OptimizationTool.Engine.Actions;

// M4 seam between the engine's action pipeline (plans, approval, journal, reconcile, undo — src/Engine/Actions)
// and the compiled Windows handlers (src/Windows/Actions). See contracts/m4-actions.md.
// A handler only reads live state, builds the exact change, writes it, and reads it back. It never decides
// approval, never persists anything and never retries; the engine owns those.

/// <summary>A domain's compiled handlers. Registered in src/Windows/WindowsModules.cs (Actions list).</summary>
public interface IActionModule
{
    IEnumerable<IActionHandler> Handlers(ActionContext context);
}

/// <summary>
/// What handlers get from the host: elevation for machine-scope writes and feature switches. EnableAdminActions is the
/// host flag --enable-admin-actions (m9 §3.2, D6): every administrator action added in M9 is off without it.
/// </summary>
public sealed record ActionContext(IElevation Elevation, bool EnableFirewallHandler, bool EnableAdminActions = false);

public interface IActionHandler
{
    /// <summary>Catalog ID, e.g. "power.select_scheme".</summary>
    string ActionId { get; }
    string Version { get; }
    /// <summary>Short user-facing label, e.g. "Change power plan".</summary>
    string Label { get; }
    /// <summary>"none" (runs in the host as the user) or "admin" (runs through <see cref="IElevation"/>).</summary>
    string Privilege { get; }
    bool SupportsUndo { get; }
    /// <summary>"setting" (immediate write + read-back) or "provider" (a Windows-owned operation that runs for a while).</summary>
    string Kind { get; }
    /// <summary>Catalog group for the guide and the UI, e.g. "power", "security", "settings" (contracts/m9-ai-powers.md §3.1).</summary>
    string Group { get; }
    /// <summary>One plain sentence saying what the action does, for the user to learn.</summary>
    string What { get; }
    /// <summary>Consequence flags every plan of this handler carries (closed set: <see cref="ActionFlags"/>).</summary>
    IReadOnlyList<string> Flags { get; }
    /// <summary>JSON schema of the request parameters, or null when the action takes none.</summary>
    JsonObject? ParameterSchema { get; }
    /// <summary>Executing it takes the shared work lease (it loads the PC or changes what scans and benchmarks measure).</summary>
    bool TakesLease { get; }
    /// <summary>
    /// What a <c>targetRef {evidenceId}</c> must name for this action (e.g. "startup entry"), or null when the action
    /// takes no targetRef (contracts/m9-ai-powers.md §3.1). For the guide only; the handler checks the evidence itself.
    /// </summary>
    string? TargetKind => null;

    /// <summary>
    /// Some of this action's targets run elevated (m9 §3.5): the handler is registered in the broker too, under the same
    /// actionId and version. True for every admin-default handler; a user handler with machine-scope targets (setting.set
    /// machine rows, startup.disable all-users entries) says so here.
    /// </summary>
    bool CanElevate => Privilege == "admin";

    /// <summary>
    /// A long administrator tool's own deadline in the broker (repair.sfc 60 min, repair.dism_restorehealth 90 min), or null.
    /// The host's wait for the broker grows by it (m9 §3.5 "long tools … have their own deadline").
    /// </summary>
    TimeSpan? LongDeadline => null;

    /// <summary>
    /// True when this handler's "completed" is only a tool's own report (repair.sfc and repair.dism_restorehealth, whose
    /// result can be "could not repair"; network.flush_dns, which has nothing to read back), not a change Optimus read back
    /// independently: the operation and plan then end "completed", never "verified", the receipt says what the tool
    /// reported, and the finding stays open (m9 §3.8: repairs are never verified).
    /// </summary>
    bool ReportsOnly => false;

    /// <summary>
    /// True when the action changes nothing that would need undoing (design v62 "Nothing to undo": the Defender scans and
    /// definitions update, the two repairs, clearing the DNS cache, making a restore point). Its plans and steps carry
    /// <c>undo {supported: false, nothingToUndo: true}</c>: no undo line, no Undo afterwards, never part of Undo all. Never
    /// on a handler that can undo or whose flags hold irreversible or data_loss (those change something).
    /// </summary>
    bool NothingToUndo => false;

    /// <summary>
    /// Reads live state and builds the plan content, or an ineligible result. Called for preview and again inside
    /// the target lock right before execution; the engine refuses to run if Before/After differ from the approved plan.
    /// </summary>
    Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct);

    /// <summary>Reads the target's current value in the same shape as Before/After (for verify, reconcile and drift).</summary>
    Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct);

    /// <summary>
    /// Performs the write (or starts the provider operation and waits for it) and reads back. Must not retry and
    /// must return "indeterminate" rather than guess. Progress lines are short plain text ("Scanning · quick").
    /// </summary>
    Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct);

    /// <summary>The request that would restore <paramref name="before"/> on <paramref name="target"/>, or null if not undoable.</summary>
    ActionRequest? UndoRequest(JsonObject target, JsonObject before);
}

/// <summary>
/// Target/Parameters come from the catalog request (user or agent); handlers validate every field. <see cref="Undo"/>
/// is set only by the engine (an undo plan built from <see cref="IActionHandler.UndoRequest"/>, and its recheck), never
/// from a caller's arguments, so a handler may accept a restore of the recorded preimage only when it is true.
/// </summary>
public sealed record ActionRequest(string ActionId, JsonObject? Target, JsonObject? Parameters, string? FindingSubject)
{
    public bool Undo { get; init; }

    /// <summary>
    /// Engine-only: <see cref="Target"/> is the target this handler resolved for a stored plan (the recheck inside the
    /// lock at approval). A handler that resolves its target from evidence trusts a Target only when this or
    /// <see cref="Undo"/> is set; a caller's raw target is never one.
    /// </summary>
    public bool Recheck { get; init; }

    /// <summary>
    /// Engine-only: the local, unprojected evidence a <c>targetRef {evidenceId}</c> named, after the engine checked it was
    /// released to the task (or cited by its in-scope finding), or belongs to the report or finding the desktop named.
    /// Null when the request named no targetRef.
    /// </summary>
    public TargetEvidence? Evidence { get; init; }
}

/// <summary>
/// One evidence item as stored on this PC (never the projected copy the AI saw): its collector, subject and the full
/// local value, including fields the projection never releases (registry value names, file names, process start times).
/// </summary>
public sealed record TargetEvidence(string EvidenceId, string CollectorId, string Subject, JsonObject Value, string? ObservedAt);

/// <summary>One before → after row as the user sees it.</summary>
public sealed record PlanRow(string Label, string Before, string After);

public sealed record ActionResolution(
    bool Eligible,
    JsonObject Target, JsonObject Before, JsonObject After,
    IReadOnlyList<PlanRow> Rows,
    IReadOnlyList<KeyValuePair<string, string>> Context,
    IReadOnlyList<string> Tradeoffs,
    string? UndoConditions,
    string? IneligibleCode = null, string? IneligibleMessage = null, string? ManualUri = null,
    string? Privilege = null, IReadOnlyList<string>? Flags = null, IReadOnlyList<string>? Resources = null, bool? UndoSupported = null)
{
    // Privilege: per-target override of the handler default ("none" | "admin"); null = the handler's.
    // Flags: added to the handler's (never removes one). Resources: canonical keys of everything the step reads or
    // writes (contracts/m9-ai-powers.md §3.1, ResourceKeys); the plan's lock set.
    // UndoSupported: false withdraws undo for this target (e.g. no execution fingerprint could be recorded); null or
    // true keep the handler's SupportsUndo. A resolution never adds undo to a handler without it.

    public static ActionResolution Ineligible(string code, string message, string? manualUri = null) =>
        new(false, new JsonObject(), new JsonObject(), new JsonObject(), [], [], [], null, code, message, manualUri);
}

/// <summary>The approved plan as the handler executes it. PlanHash is passed through to elevation untouched.</summary>
public sealed record ActionExecution(string PlanId, string PlanHash, JsonObject Target, JsonObject Before, JsonObject After, JsonObject? Parameters);

/// <summary>
/// state: verified (target now equals After) | failed (still equals Before) | indeterminate (anything else or unknown)
/// | declined (e.g. UAC cancelled) | completed (provider operation finished; see Observed for its own result).
/// </summary>
public sealed record ActionOutcome(string State, JsonObject Observed, string ReceiptText, string? Code = null, string? Message = null);

/// <summary>An undo step names the broker's own protected record of the original step (m9 §3.5).</summary>
public sealed record ElevatedUndoOf(string ReceiptId, int N);

/// <summary>One administrator step of an approved plan, exactly as approved (the broker re-resolves it from live state).</summary>
public sealed record ElevatedStep(int N, string ActionId, string HandlerVersion, JsonObject Target, JsonObject Before, JsonObject After,
    JsonObject? Parameters, ElevatedUndoOf? UndoOf = null, bool Long = false, TimeSpan? Deadline = null);

/// <summary>
/// Every administrator step of one approved plan, in the plan's order: one UAC prompt, one broker window.
/// <see cref="Progress"/> receives the broker's progress lines of a long step (n, text); null ignores them.
/// </summary>
public sealed record ElevatedRequest(string PlanId, string PlanHash, IReadOnlyList<ElevatedStep> Steps)
{
    public IProgress<(int N, string Text)>? Progress { get; init; }
}

/// <summary>
/// The broker's answer, one outcome per requested step (state verified | completed | failed | indeterminate | skipped |
/// declined). <see cref="Dispatched"/>: the request reached a verified broker, so a step may have been written even when
/// the answer was lost. <see cref="ReceiptId"/>: the broker's protected record of this session (undo names it).
/// </summary>
public sealed record ElevatedResult(IReadOnlyList<(int N, ActionOutcome Outcome)> Steps, string? ReceiptId, bool Dispatched)
{
    /// <summary>
    /// m9 §13 R2: the result being available is not the session having ended. Completes only when the broker process has
    /// really exited (a clean answer: shortly after; a timeout, lost pipe or bad frame: whenever it exits; no process handle:
    /// never, so the steps' locks stay held until the host restarts). Null: nothing is running any more (nothing was
    /// launched, or a seam that runs nothing out of process). Whoever holds the steps' locks keeps them until it completes.
    /// </summary>
    public Task? Ended { get; init; }
}

/// <summary>One step as the broker's protected journal records it: prepared (preimage written before the write) and its outcome.</summary>
public sealed record JournalStep(int N, string ActionId, bool Prepared, string? State, JsonObject? Observed);

/// <summary>What the protected journal says about one plan (host-death recovery, m9 §3.5). Steps it never prepared are absent.</summary>
public sealed record ElevatedJournal(string? ReceiptId, IReadOnlyList<JournalStep> Steps);

/// <summary>Runs administrator steps in the one-shot elevated broker (src/Broker), which re-resolves and confirms them itself.</summary>
public interface IElevation
{
    /// <summary>
    /// One broker session for <paramref name="request"/>. Cancelling <paramref name="stop"/> sends the broker a stop frame
    /// (the step in flight finishes, no further step starts); <paramref name="ct"/> ends the wait.
    /// </summary>
    Task<ElevatedResult> RunAsync(ElevatedRequest request, CancellationToken stop, CancellationToken ct);

    /// <summary>The protected journal's records for <paramref name="planId"/>; null when the journal can't be read.</summary>
    ElevatedJournal? Journal(string planId);
}

/// <summary>One administrator check of a read request (m9 §17.2): catalog id and version (Agent.AdminChecks), window in days or null.</summary>
public sealed record ElevatedReadCheck(int N, string Id, string HandlerVersion, int? WindowDays);

/// <summary>One read request: 1–4 checks, <c>n</c> strictly ascending from 1. One UAC prompt and one helper window.</summary>
public sealed record ElevatedReadRequest(string RequestId, IReadOnlyList<ElevatedReadCheck> Checks)
{
    /// <summary>Called once the helper says it started reading (after its own window's Read); best effort.</summary>
    public Action? Started { get; init; }

    /// <summary>
    /// The engine's admission, called once right before the helper is launched, with the one-helper exclusion held (after
    /// any write, script or read that held it): false launches nothing (not_started/not_admitted). Must not block long.
    /// </summary>
    public Func<bool>? Admit { get; init; }
}

/// <summary>What a check's result covers: complete = everything that matched was returned; total = how many matched; returned = items sent.</summary>
public sealed record ElevatedReadCoverage(bool Complete, int Total, int Returned);

/// <summary>
/// One check's result. State: ok | failed | unsupported | skipped | cancelled (the helper's), with code (timeout,
/// access_denied, broker_cancelled, stopped, interrupted, malformed, …). Items are already checked against the check's
/// field list (AdminChecks.Conforms); they are PC data, untrusted, and not yet through the never-store/projection rules.
/// </summary>
public sealed record ElevatedCheckResult(int N, string State, string? Code, ElevatedReadCoverage Coverage, IReadOnlyList<JsonObject> Items);

/// <summary>
/// The whole read session. State: complete | declined | stopped | timeout | refused (the helper's read_end), or the
/// host's own: not_started (nothing was sent: flag off, helper missing, UAC refused to start, no connection, wrong
/// helper, different administrator), malformed (frames out of order, a bad item: every check failed/malformed, store
/// nothing), interrupted (the connection was lost before read_end: finished checks are kept, the rest failed/interrupted).
/// A UAC prompt the user cancelled is declined/uac_cancelled. <see cref="Dispatched"/>: the request reached a verified helper.
/// </summary>
public sealed record ElevatedReadResult(string State, string? Code, IReadOnlyList<ElevatedCheckResult> Checks, bool Dispatched)
{
    /// <summary>Completes when the helper process has exited (as <see cref="ElevatedResult.Ended"/>); null when nothing was launched.</summary>
    public Task? Ended { get; init; }
}

/// <summary>Administrator checks (m9 §17.2) through the one-shot elevated helper: read-only, behind --enable-admin-actions.</summary>
public interface IElevatedReads
{
    /// <summary>
    /// One helper session for <paramref name="request"/>. Cancelling <paramref name="stop"/> sends the helper a stop frame:
    /// the check in flight finishes, the rest are cancelled. Never throws for a helper problem; the result says what happened.
    /// </summary>
    Task<ElevatedReadResult> RunReadAsync(ElevatedReadRequest request, CancellationToken stop);
}

/// <summary>The closed set of consequence flags (contracts/m9-ai-powers.md §3.1), in display order.</summary>
public static class ActionFlags
{
    public const string Irreversible = "irreversible", DataLoss = "data_loss", RunsCode = "runs_code", Restart = "restart",
        SignOut = "sign_out", Network = "network", Long = "long";

    public static readonly IReadOnlyList<string> All = [Irreversible, DataLoss, RunsCode, Restart, SignOut, Network, Long];

    public static bool Valid(string flag) => All.Contains(flag);

    /// <summary>The union of flag lists in canonical order; null when any flag is outside the closed set.</summary>
    public static IReadOnlyList<string>? Union(params IReadOnlyList<string>?[] lists)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var list in lists)
            foreach (var f in list ?? [])
            {
                if (!Valid(f)) return null;
                set.Add(f);
            }
        return All.Where(set.Contains).ToList();
    }
}

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

/// <summary>What handlers get from the host: elevation for machine-scope writes and feature switches.</summary>
public sealed record ActionContext(IElevation Elevation, bool EnableFirewallHandler);

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

/// <summary>Target/Parameters come from the catalog request (user or agent); handlers validate every field.</summary>
public sealed record ActionRequest(string ActionId, JsonObject? Target, JsonObject? Parameters, string? FindingSubject);

/// <summary>One before → after row as the user sees it.</summary>
public sealed record PlanRow(string Label, string Before, string After);

public sealed record ActionResolution(
    bool Eligible,
    JsonObject Target, JsonObject Before, JsonObject After,
    IReadOnlyList<PlanRow> Rows,
    IReadOnlyList<KeyValuePair<string, string>> Context,
    IReadOnlyList<string> Tradeoffs,
    string? UndoConditions,
    string? IneligibleCode = null, string? IneligibleMessage = null, string? ManualUri = null)
{
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

/// <summary>Runs one machine-scope action in the one-shot elevated broker (src/Broker), which re-resolves it itself.</summary>
public interface IElevation
{
    Task<ActionOutcome> RunAsync(string actionId, ActionExecution execution, CancellationToken ct);
}

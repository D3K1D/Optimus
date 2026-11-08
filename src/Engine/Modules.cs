using System.Text.Json.Nodes;

namespace OptimizationTool.Engine;

// M2 seam between the engine and the read-only Windows audit (contracts/m2-collectors.md).
// A module bundles the collectors, rules and inventory steps for one domain. The engine plans collectors by
// category, commits their facts as evidence, then evaluates every rule against the scan's evidence.

/// <summary>One audit domain (security, system, reliability, …). Implemented in src/Windows.</summary>
public interface IAuditModule
{
    string Name { get; }
    IEnumerable<ICollector> Collectors(CollectorOptions options);
    IEnumerable<IRule> Rules();
    IEnumerable<IInventoryStep> InventorySteps(CollectorOptions options);
}

/// <summary>Budgets from docs/backend.md "Scan lifecycle". Tests shrink them.</summary>
public sealed record CollectorOptions
{
    public TimeSpan SampleWindow { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan EventLookback { get; init; } = TimeSpan.FromDays(7);
    public int EventRowCap { get; init; } = 2000;
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(20);
}

/// <summary>A deterministic, versioned interpretation of evidence. Rules never touch Windows directly.</summary>
public interface IRule
{
    /// <summary>Stable dotted ID, e.g. "fw.public_off". Changing meaning needs a new major version.</summary>
    string Id { get; }
    string Version { get; }
    /// <summary>Presentation category: security|performance|storage|privacy_telemetry|diagnostics|customization|misc.</summary>
    string Category { get; }
    /// <summary>Collector IDs whose coverage this rule depends on; drives "insufficient" vs "clear".</summary>
    IReadOnlyList<string> Inputs { get; }
    RuleResult Evaluate(RuleContext context);
}

/// <summary>One committed evidence item as rules see it.</summary>
public sealed record EvidenceItem(string Id, string Subject, string CollectorId, string Category, JsonNode Value, string Display, string ObservedAt);

public sealed class RuleContext(IReadOnlyList<EvidenceItem> evidence, IReadOnlyDictionary<string, string> coverage, Profile profile)
{
    public IReadOnlyList<EvidenceItem> Evidence { get; } = evidence;
    public Profile Profile { get; } = profile;

    public EvidenceItem? Get(string subject) => Evidence.FirstOrDefault(e => e.Subject == subject);
    public IEnumerable<EvidenceItem> WithPrefix(string prefix) => Evidence.Where(e => e.Subject.StartsWith(prefix, StringComparison.Ordinal));
    /// <summary>Coverage status of a collector in this scan, or null if it was not planned.</summary>
    public string? CoverageOf(string collectorId) => coverage.TryGetValue(collectorId, out var s) ? s : null;
}

/// <summary>status: finding | clear | insufficient | inapplicable. Only "finding" carries drafts.</summary>
public sealed record RuleResult(string Status, IReadOnlyList<FindingDraft> Findings, string? Reason = null)
{
    public static RuleResult Clear() => new("clear", []);
    public static RuleResult Insufficient(string reason) => new("insufficient", [], reason);
    public static RuleResult Inapplicable(string reason) => new("inapplicable", [], reason);
    public static RuleResult Found(params FindingDraft[] findings) => new("finding", findings);
}

/// <summary>
/// A finding before IDs are assigned. <see cref="Subject"/> is the normalized subject the fingerprint is built
/// from (stable across scans, so ignores carry over). Severity: high|medium|low|info. Confidence: high|medium|low.
/// </summary>
public sealed record FindingDraft(
    string Subject, string Title, string Severity, string Confidence,
    IReadOnlyList<string> EvidenceIds, CardDraft Card,
    IReadOnlyList<string>? Counterevidence = null, string ActionEligibility = "manual_review");

/// <summary>
/// Follow-up card copy. Keep it to one plain question; options from {"View steps","Ask about this","Ignore"}
/// in M2. <see cref="SettingsUri"/> is an ms-settings: or windowsdefender: destination from the reviewed catalog.
/// From M4 a card can offer "Preview change" for a catalog action: <see cref="ActionId"/> names it and
/// <see cref="ActionParameters"/> is the request the engine passes to that handler's ResolveAsync.
/// </summary>
public sealed record CardDraft(string Text, IReadOnlyList<string> Options, IReadOnlyList<string>? Guide = null, string? SettingsUri = null,
    string? ActionId = null, System.Text.Json.Nodes.JsonObject? ActionParameters = null);

/// <summary>A consented, local, no-AI inventory step (onboarding "Check this PC"). Group = UI row name.</summary>
public interface IInventoryStep
{
    string Group { get; }
    TimeSpan Timeout { get; }
    Task<InventoryStepResult> RunAsync(CancellationToken ct);
}

/// <summary>A display-safe wave label: classes and counts only — no names, IDs, SSIDs, addresses or paths.</summary>
public sealed record InventoryFact(string Kind, string Label);

/// <summary>Capabilities feed starter predicates, e.g. {"displayCount":2}.</summary>
public sealed record InventoryStepResult(IReadOnlyList<InventoryFact> WaveFacts, IReadOnlyList<Fact> Facts, CoverageReceipt Coverage, JsonObject Capabilities);

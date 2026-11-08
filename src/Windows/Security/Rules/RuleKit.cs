using OptimizationTool.Engine;

namespace OptimizationTool.Windows.Security.Rules;

/// <summary>Shared helpers for the security rules. Rules are pure: evidence in, result out.</summary>
internal static class RuleKit
{
    public static readonly IReadOnlyList<string> Options = ["View steps", "Ask about this", "Ignore"];

    public static bool Usable(RuleContext c, string collectorId) => c.CoverageOf(collectorId) is "complete" or "partial";

    public static RuleResult NotCovered(string collectorId) => RuleResult.Insufficient($"{collectorId} did not complete.");

    public static CardDraft Card(string text, string? settingsUri, params string[] guide) => new(text, Options, guide, settingsUri);

    public static DateTime? ObservedAt(EvidenceItem e) => Common.J.Time(System.Text.Json.Nodes.JsonValue.Create(e.ObservedAt));
}

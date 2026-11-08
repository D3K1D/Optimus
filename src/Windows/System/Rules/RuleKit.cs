using System.Text.Json.Nodes;
using OptimizationTool.Engine;

namespace OptimizationTool.Windows.SystemDomain.Rules;

internal static class RuleKit
{
    public static readonly IReadOnlyList<string> Options = ["View steps", "Ask about this", "Ignore"];

    /// <summary>Null when the collector produced usable coverage; otherwise the insufficient reason.</summary>
    public static string? Unusable(RuleContext ctx, string collectorId) => ctx.CoverageOf(collectorId) switch
    {
        "complete" or "partial" => null,
        null => $"{collectorId} not run",
        var s => $"{collectorId} {s}",
    };

    public static double? Num(this EvidenceItem e, string key) => Num(e.Value, key);

    public static double? Num(JsonNode? node, string key) => node is JsonObject o ? AsDouble(o[key]) : null;

    /// <summary>Any JSON number (whatever CLR type backs the node) as a double.</summary>
    public static double? AsDouble(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == global::System.Text.Json.JsonValueKind.Number
        && double.TryParse(v.ToJsonString(), global::System.Globalization.NumberStyles.Float, global::System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? d : null;

    public static bool? Bool(this EvidenceItem e, string key) =>
        e.Value is JsonObject o && o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    public static string? Str(this EvidenceItem e, string key) =>
        e.Value is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static List<double> Numbers(this EvidenceItem e, string key) =>
        e.Value is JsonObject o && o[key] is JsonArray a
            ? a.Select(AsDouble).Where(d => d is not null).Select(d => d!.Value).ToList()
            : [];

    public const double GiB = 1L << 30;

    public static string Gib(double bytes) => Math.Round(bytes / GiB, bytes >= 10 * GiB ? 0 : 1).ToString(global::System.Globalization.CultureInfo.InvariantCulture);
}

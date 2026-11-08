using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using OptimizationTool.Engine;

namespace OptimizationTool.Windows.Reliability;

/// <summary>
/// Base for the domain's collectors: the blocking read runs on a worker (the coordinator's timeout abandons it),
/// and unexpected errors become coverage, never exceptions with message text.
/// </summary>
internal abstract class ReliabilityCollector(CollectorOptions options) : ICollector
{
    protected CollectorOptions Options { get; } = options;

    public abstract string Id { get; }
    public string Version => "1.0.0";
    public abstract string Category { get; }
    public virtual TimeSpan Timeout => Options.DefaultTimeout;
    public abstract string GapTitle { get; }
    public abstract string GapSource { get; }
    public virtual string GapDisplay => $"{GapTitle} was unavailable.";

    public Task<CollectorResult> CollectAsync(CancellationToken ct) => Task.Run(() =>
    {
        try { return Collect(ct); }
        catch (OperationCanceledException) { throw; }
        catch (UnauthorizedAccessException) { return new CollectorResult([], new CoverageReceipt("denied", 0, "Access denied.")); }
        catch (System.Security.SecurityException) { return new CollectorResult([], new CoverageReceipt("denied", 0, "Access denied.")); }
        catch (Exception e) { return new CollectorResult([], new CoverageReceipt("failed", 0, e.GetType().Name)); }
    }, ct);

    protected abstract CollectorResult Collect(CancellationToken ct);
}

internal static class R
{
    public static string Hash8(string identity) => Ids.Hash16(identity)[..8];

    /// <summary>Lowercase dotted-key segment: letters/digits, other runs collapse to '-'.</summary>
    public static string Slug(string s)
    {
        var t = Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return t.Length == 0 ? "unknown" : t;
    }

    public static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
    public static string Duration(TimeSpan t) => XmlConvert.ToString(t);

    /// <summary>Short display-safe text: no control characters, bounded length.</summary>
    public static string Clean(string? s, int max = 80)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = Regex.Replace(s, @"[\p{C}]+", " ").Trim();
        return t.Length <= max ? t : t[..max].TrimEnd() + "…";
    }

    public static List<Fact> Sorted(IEnumerable<Fact> facts) => facts.OrderBy(f => f.Subject, StringComparer.Ordinal).ToList();

    public static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
}

/// <summary>Helpers shared by the domain's rules (pure: evidence in, result out).</summary>
internal static class RuleKit
{
    public static readonly IReadOnlyList<string> Options = ["View steps", "Ask about this", "Ignore"];

    /// <summary>Null when every input collector is complete/partial; else the reason for "insufficient".</summary>
    public static string? MissingInput(RuleContext ctx, IReadOnlyList<string> inputs)
    {
        foreach (var id in inputs)
        {
            var s = ctx.CoverageOf(id);
            if (s is not ("complete" or "partial")) return $"{id} is {s ?? "not planned"}.";
        }
        return null;
    }

    public static int Int(JsonNode? n, int fallback = 0) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i
        : n is JsonValue v2 && v2.TryGetValue<long>(out var l) ? (int)l : n is JsonValue v3 && v3.TryGetValue<double>(out var d) ? (int)d : fallback;
    public static long? Long(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var l) ? l
        : n is JsonValue v2 && v2.TryGetValue<int>(out var i) ? i : n is JsonValue v3 && v3.TryGetValue<double>(out var d) ? (long)d : null;
    public static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    public static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    public static DateTimeOffset? Time(JsonNode? n) =>
        Str(n) is { } s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    /// <summary>"in the last 7 days" from a lookback in seconds; falls back to "recently".</summary>
    public static string Window(JsonNode? lookbackSeconds)
    {
        var s = Long(lookbackSeconds);
        if (s is not > 0) return "recently";
        var days = (int)Math.Round(s.Value / 86400.0);
        return days <= 1 ? "in the last day" : $"in the last {days} days";
    }
}

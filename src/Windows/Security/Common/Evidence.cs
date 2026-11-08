using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using OptimizationTool.Engine;

namespace OptimizationTool.Windows.Security.Common;

/// <summary>Subject keys: lowercase dotted, with an 8-hex identity hash (contract §1.8).</summary>
internal static class Subjects
{
    /// <summary>First 8 hex of SHA-256 over the lower-cased identity.</summary>
    public static string Hash8(string identity) => Ids.Hash16(identity.ToLowerInvariant())[..8];
}

/// <summary>Scanned strings are hostile data: strip control and format characters and cap the length.</summary>
internal static class Text
{
    public static string Clean(string? value, int max = 80)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var sb = new StringBuilder(Math.Min(value.Length, max));
        foreach (var ch in value)
        {
            if (sb.Length >= max) break;
            var cat = char.GetUnicodeCategory(ch);
            if (cat is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse)
            {
                if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
                continue;
            }
            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    /// <summary>Clean, then mask SIDs and the given account name (task names often embed them).</summary>
    public static string Redact(string? value, string? userName, int max = 80)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(value ?? "", @"S-1-\d+(-\d+){1,14}", "<SID>",
            System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100));
        if (!string.IsNullOrEmpty(userName) && userName.Length >= 2)
            s = s.Replace(userName, "<user>", StringComparison.OrdinalIgnoreCase);
        return Clean(s, max);
    }

    public static string Iso(DateTime? utc) =>
        utc is { } t ? t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : "";

    public static JsonNode? IsoOrNull(DateTime? utc) => utc is null ? null : JsonValue.Create(Iso(utc));
}

/// <summary>Collects facts, keeps one per subject and emits them sorted (contract §1.8).</summary>
internal sealed class FactSet(string source)
{
    private readonly SortedDictionary<string, Fact> _facts = new(StringComparer.Ordinal);

    public int Count => _facts.Count;
    public bool Contains(string subject) => _facts.ContainsKey(subject);

    public void Add(string subject, JsonObject value, string display, string quality = "measured")
    {
        _facts.TryAdd(subject, new Fact(subject, source, quality, value, display, null));
    }

    public Fact? Get(string subject) => _facts.TryGetValue(subject, out var f) ? f : null;

    public CollectorResult Result(string status, string? reason) =>
        new(_facts.Values.ToList(), new CoverageReceipt(status, _facts.Count, reason));
}

/// <summary>Typed reads from evidence JSON (works for both in-memory and persisted values).</summary>
internal static class J
{
    public static bool? Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue(out bool b) ? b : null;
    public static long? Long(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out uint u)) return u;
        if (v.TryGetValue(out double d)) return (long)d;
        return null;
    }
    public static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s : null;
    public static DateTime? Time(JsonNode? n) =>
        Str(n) is { Length: > 0 } s && DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;
}

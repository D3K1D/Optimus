using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OptimizationTool.Engine.Agent;

/// <summary>Typed reads from evidence JSON. Anything of the wrong JSON type reads as null.</summary>
internal static class Read
{
    public static bool? Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue(out bool b) ? b : null;

    public static double? Num(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue(out double d)) return double.IsFinite(d) ? d : null;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out ulong u)) return u;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out uint ui)) return ui;
        if (v.TryGetValue(out float f)) return float.IsFinite(f) ? f : null;
        if (v.TryGetValue(out decimal m)) return (double)m;
        return null;
    }

    /// <summary>Whole numbers only (a fractional value is not an integer field).</summary>
    public static long? Long(JsonNode? n) =>
        Num(n) is { } d && Math.Abs(d) < 9e15 && Math.Floor(d) == d ? (long)d : null;

    public static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}

/// <summary>
/// Builds one projected value: every write is an allowlisted, typed field. A field that is present but fails its
/// type, range, pattern or sensitivity check is left out and counted in <see cref="ProjectionStats.FieldsDropped"/>.
/// </summary>
internal sealed class Out(IAliasMap aliases, ShareOptions share, ProjectionStats stats)
{
    private static readonly Regex VersionRx = new(@"^\d{1,9}(?:\.\d{1,9}){0,4}$", RegexOptions.CultureInvariant);
    private static readonly Regex HexRx = new(@"^0x[0-9a-f]{1,16}$", RegexOptions.CultureInvariant);
    private static readonly string[] TimeFormats = ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"];
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "M-d-yyyy", "MM-dd-yyyy", "M/d/yyyy", "MM/dd/yyyy", "yyyyMMdd"];

    private readonly List<string> _untrusted = [];

    public IAliasMap Aliases { get; } = aliases;
    public ShareOptions Share { get; } = share;
    public ProjectionStats Stats { get; } = stats;
    public JsonObject V { get; } = [];

    /// <summary>Counts a present-but-unshareable field.</summary>
    public void Dropped(JsonNode? n) { if (n is not null) Stats.DropField(); }

    public bool? Bool(string key, JsonNode? n)
    {
        var b = Read.Bool(n);
        if (b is null) Dropped(n); else V[key] = b;
        return b;
    }

    public long? Int(string key, JsonNode? n, long min = 0, long max = int.MaxValue)
    {
        var l = Read.Long(n);
        if (l is null || l < min || l > max) { Dropped(n); return null; }
        V[key] = l;
        return l;
    }

    /// <summary>Rounded number (default 0.1) within [min, max].</summary>
    public double? Num(string key, JsonNode? n, int digits = 1, double min = 0, double max = 1e12)
    {
        var d = Read.Num(n);
        if (d is null || d < min || d > max) { Dropped(n); return null; }
        var r = Math.Round(d.Value, digits, MidpointRounding.AwayFromZero);
        V[key] = r;
        return r;
    }

    public double? Pct(string key, JsonNode? n) => Num(key, n, 1, 0, 100);

    /// <summary>Bytes → GiB with one decimal.</summary>
    public double? Gib(string key, JsonNode? bytes) => Scaled(key, bytes, 1L << 30);

    /// <summary>Bytes → MiB with one decimal.</summary>
    public double? Mib(string key, JsonNode? bytes) => Scaled(key, bytes, 1L << 20);

    private double? Scaled(string key, JsonNode? bytes, long unit)
    {
        var d = Read.Num(bytes);
        if (d is null || d < 0 || d > 1e18) { Dropped(bytes); return null; }
        var r = Math.Round(d.Value / unit, 1, MidpointRounding.AwayFromZero);
        V[key] = r;
        return r;
    }

    /// <summary>A rounded array of percentages (cap 240 entries).</summary>
    public void PctArray(string key, JsonNode? n, int cap = 240)
    {
        if (n is not JsonArray a) { Dropped(n); return; }
        var arr = new JsonArray();
        foreach (var x in a.Take(cap))
        {
            if (Read.Num(x) is { } d && d is >= 0 and <= 100) arr.Add(Math.Round(d, 1, MidpointRounding.AwayFromZero));
            else { Dropped(x ?? JsonValue.Create(0)); }
        }
        V[key] = arr;
    }

    public void IntArray(string key, JsonNode? n, long min, long max, int cap = 64)
    {
        if (n is not JsonArray a) { Dropped(n); return; }
        var arr = new JsonArray();
        foreach (var x in a.Take(cap))
        {
            if (Read.Long(x) is { } l && l >= min && l <= max) arr.Add(l);
            else Dropped(x ?? JsonValue.Create(0));
        }
        V[key] = arr;
    }

    /// <summary>A value from a fixed list (ordinal). Anything else is dropped.</summary>
    public string? Enum(string key, JsonNode? n, params string[] allowed)
    {
        var s = Read.Str(n);
        if (s is null || Array.IndexOf(allowed, s) < 0) { Dropped(n); return null; }
        V[key] = s;
        return s;
    }

    /// <summary>A constant chosen by the projector (never from the evidence).</summary>
    public void Const(string key, string value) => V[key] = value;

    /// <summary>ISO-8601 UTC timestamp, re-formatted to seconds.</summary>
    public string? Time(string key, JsonNode? n)
    {
        var t = ParseTime(Read.Str(n));
        if (t is null) { Dropped(n); return null; }
        V[key] = t;
        return t;
    }

    public static string? ParseTime(string? s)
    {
        if (s is null || s.Length > 40) return null;
        return DateTimeOffset.TryParseExact(s, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : null;
    }

    public void TimeArray(string key, JsonNode? n, int cap = 20)
    {
        if (n is not JsonArray a) { Dropped(n); return; }
        var arr = new JsonArray();
        foreach (var x in a.Take(cap))
        {
            if (ParseTime(Read.Str(x)) is { } t) arr.Add(t);
            else Dropped(x ?? JsonValue.Create(0));
        }
        V[key] = arr;
    }

    /// <summary>A calendar date, re-formatted yyyy-MM-dd (or yyyy-MM when <paramref name="monthOnly"/>).</summary>
    public string? Date(string key, JsonNode? n, bool monthOnly = false)
    {
        var s = Read.Str(n);
        if (s is null || s.Length > 40) { Dropped(n); return null; }
        s = s.Trim();
        DateTime d;
        if (!DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
        {
            if (ParseTime(s) is { } t) d = DateTime.Parse(t, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
            else { Dropped(n); return null; }
        }
        if (d.Year is < 1980 or > 2200) { Dropped(n); return null; }
        var r = d.ToString(monthOnly ? "yyyy-MM" : "yyyy-MM-dd", CultureInfo.InvariantCulture);
        V[key] = r;
        return r;
    }

    /// <summary>Numeric dotted version (1–5 parts). Free-text versions are dropped.</summary>
    public string? Version(string key, JsonNode? n)
    {
        var s = Read.Str(n)?.Trim();
        if (s is null || s.Length > 48 || !VersionRx.IsMatch(s)) { Dropped(n); return null; }
        V[key] = s;
        return s;
    }

    /// <summary>A normalized hex code such as 0x9f.</summary>
    public string? Hex(string key, JsonNode? n)
    {
        var s = Read.Str(n);
        if (s is null || !HexRx.IsMatch(s)) { Dropped(n); return null; }
        V[key] = s;
        return s;
    }

    public static bool IsHex(string? s) => s is not null && HexRx.IsMatch(s);

    /// <summary>
    /// A name (app, process, device, publisher…): its task alias, or with share.names its sanitized text (marked
    /// untrusted). A name that looks sensitive falls back to the alias and counts as a dropped field.
    /// Returns the label used, or null when there is no name.
    /// </summary>
    public string? Name(string key, string kind, string? raw)
    {
        var (label, untrusted) = Resolve(kind, raw);
        if (label is null) return null;
        V[key] = label;
        if (untrusted) _untrusted.Add(key);
        return label;
    }

    /// <summary>The label for a name without writing a field (for map keys), and whether it is untrusted PC text.</summary>
    public (string? Label, bool Untrusted) Resolve(string kind, string? raw)
    {
        var folded = Sanitize.Fold(raw);
        if (folded.Length == 0) { if (raw is { Length: > 0 }) Stats.DropField(); return (null, false); }
        var alias = AliasMap.Canonical(kind, Aliases.Alias(kind, folded));
        if (!Share.Names) return (alias, false);
        var clean = Sanitize.Name(folded);
        if (clean is null) { Stats.DropField(); return (alias, false); }
        return (clean, true);
    }

    /// <summary>The alias for a name even when names are shared (subject keys always use aliases).</summary>
    public string? AliasOf(string kind, string? raw)
    {
        var folded = Sanitize.Fold(raw);
        return folded.Length == 0 ? null : AliasMap.Canonical(kind, Aliases.Alias(kind, folded));
    }

    /// <summary>Marks a field as carrying untrusted PC text (only names, when the grant shares them).</summary>
    public void MarkUntrusted(string key) => _untrusted.Add(key);

    public JsonObject Finish()
    {
        if (_untrusted.Count > 0)
            V["untrustedFields"] = new JsonArray(_untrusted.Distinct().Order(StringComparer.Ordinal).Select(k => (JsonNode?)JsonValue.Create(k)).ToArray());
        return V;
    }
}

/// <summary>Small text helpers for regenerated Display sentences.</summary>
internal static class Say
{
    public static string N(double d) => d.ToString("0.#", CultureInfo.InvariantCulture);
    public static string N(long l) => l.ToString(CultureInfo.InvariantCulture);
    public static string Times(long n) => n == 1 ? "once" : $"{N(n)} times";
    public static string Plural(long n, string one, string many) => n == 1 ? $"1 {one}" : $"{N(n)} {many}";
}

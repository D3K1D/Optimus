using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace OptimizationTool.Engine.Agent;

/// <summary>
/// Task-stable aliases (contracts/m3-agent.md §3.5): the first app seen in a task is "App A", the next "App B" …
/// "App Z", "App AA". Each kind has its own sequence. Names are compared after <see cref="Sanitize.Fold"/> and
/// case-insensitively. The map stores only a SHA-256 prefix of each name, never the name itself, so the persisted
/// form (<see cref="ToJson"/>) does not hold app or device names.
/// </summary>
public sealed class AliasMap : IAliasMap
{
    public const int FormatVersion = 1;

    /// <summary>Per-kind cap; past it every new name shares the overflow alias "App (other)".</summary>
    public const int MaxPerKind = 5000;

    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["app"] = "App", ["process"] = "Process", ["device"] = "Device", ["publisher"] = "Publisher",
        ["display"] = "Display", ["volume"] = "Volume", ["service"] = "Service", ["task"] = "Task",
        ["module"] = "Module", ["threat"] = "Threat", ["product"] = "Product", ["provider"] = "Provider", ["item"] = "Item",
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, List<string>> _order = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, int>> _index = new(StringComparer.Ordinal);

    /// <summary>The kinds this map labels; any other kind is treated as "item".</summary>
    public static IReadOnlyCollection<string> Kinds => (IReadOnlyCollection<string>)Labels.Keys;

    public string Alias(string kind, string realName)
    {
        var k = Kind(kind);
        var key = Key(realName);
        int n;
        lock (_gate)
        {
            if (!_index.TryGetValue(k, out var idx)) { _index[k] = idx = new(StringComparer.Ordinal); _order[k] = []; }
            if (!idx.TryGetValue(key, out n))
            {
                if (idx.Count >= MaxPerKind) return $"{Labels[k]} (other)";
                n = idx.Count;
                idx[key] = n;
                _order[k].Add(key);
            }
        }
        return $"{Labels[k]} {Letters(n)}";
    }

    /// <summary>How many distinct names of this kind have an alias.</summary>
    public int Count(string kind)
    {
        lock (_gate) return _index.TryGetValue(Kind(kind), out var idx) ? idx.Count : 0;
    }

    /// <summary>0 → A, 25 → Z, 26 → AA, 27 → AB … (bijective base 26).</summary>
    public static string Letters(int n)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(n);
        var sb = new StringBuilder();
        for (var i = n + 1; i > 0; i = (i - 1) / 26) sb.Insert(0, (char)('A' + (i - 1) % 26));
        return sb.ToString();
    }

    /// <summary>"App C" → "app-c": the alias as a subject-key segment.</summary>
    public static string Slug(string alias)
    {
        var sb = new StringBuilder(alias.Length);
        foreach (var ch in alias.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }

    private static string Kind(string? kind) => kind is not null && Labels.ContainsKey(kind) ? kind : "item";

    /// <summary>The label noun for a kind ("process" → "Process"); unknown kinds → "Item". Other IAliasMap implementations should use it.</summary>
    public static string Noun(string kind) => Labels[Kind(kind)];

    private static readonly System.Text.RegularExpressions.Regex Shape =
        new(@"^[A-Za-z]{1,16} ([A-Z]{1,6}|(other))$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Normalizes an alias returned by any <see cref="IAliasMap"/> to this kind's noun ("Item D" for a provider → "Provider D").
    /// Sequences are per kind, so only the noun changes. Anything not alias-shaped is never shared: it becomes "Noun (other)".
    /// </summary>
    public static string Canonical(string kind, string? returned)
    {
        var m = returned is null ? null : Shape.Match(returned);
        return $"{Noun(kind)} {(m is { Success: true } ? m.Groups[1].Value : "(other)")}";
    }

    private static string Key(string? realName)
    {
        var folded = Sanitize.Fold(realName).ToLowerInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(folded)))[..32];
    }

    // ------------------------------------------------------------------ persistence

    /// <summary>{"v":1,"kinds":{"app":["&lt;hash&gt;",…],…}} in first-use order. Holds no names.</summary>
    public JsonObject ToJson()
    {
        var kinds = new JsonObject();
        lock (_gate)
        {
            foreach (var k in _order.Keys.OrderBy(k => k, StringComparer.Ordinal))
                kinds[k] = new JsonArray(_order[k].Select(h => (JsonNode?)JsonValue.Create(h)).ToArray());
        }
        return new JsonObject { ["v"] = FormatVersion, ["kinds"] = kinds };
    }

    /// <summary>Restores a map written by <see cref="ToJson"/>. Throws <see cref="FormatException"/> on any other shape.</summary>
    public static AliasMap FromJson(JsonNode? json)
    {
        if (json is not JsonObject o || o["v"] is not JsonValue v || !v.TryGetValue(out int ver) || ver != FormatVersion
            || o["kinds"] is not JsonObject kinds)
            throw new FormatException("Unrecognized alias map.");
        var map = new AliasMap();
        foreach (var (k, list) in kinds)
        {
            if (!Labels.ContainsKey(k) || list is not JsonArray arr || arr.Count > MaxPerKind) throw new FormatException("Unrecognized alias map.");
            var idx = new Dictionary<string, int>(StringComparer.Ordinal);
            var order = new List<string>(arr.Count);
            foreach (var item in arr)
            {
                if (item is not JsonValue hv || !hv.TryGetValue(out string? h) || h is null || h.Length != 32
                    || !h.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f') || !idx.TryAdd(h, order.Count))
                    throw new FormatException("Unrecognized alias map.");
                order.Add(h);
            }
            map._index[k] = idx;
            map._order[k] = order;
        }
        return map;
    }

    public static AliasMap FromJson(string json)
    {
        try { return FromJson(JsonNode.Parse(json)); }
        catch (System.Text.Json.JsonException e) { throw new FormatException("Unrecognized alias map.", e); }
    }
}

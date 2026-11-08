using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OptimizationTool.Engine.Agent;

/// <summary>
/// fixture.* (synthetic M1 data, website demo): a generic allowlist. Booleans and numbers (rounded to 0.1) pass;
/// strings pass only when short, enum-like (≤ 3 words, letters/digits and . _ % + -) and not sensitive; name-like
/// keys become aliases; identifier/path/text keys are dropped; nesting is limited to two levels.
/// </summary>
internal sealed class FixtureProjector : Projector
{
    private static readonly Regex KeyRx = new(@"^[A-Za-z][A-Za-z0-9_]{0,39}$", RegexOptions.CultureInvariant);
    private static readonly Regex ValueRx = new(@"^[A-Za-z0-9][A-Za-z0-9 ._%+-]{0,31}$", RegexOptions.CultureInvariant);
    private static readonly Regex SegmentRx = new(@"^[a-z0-9][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant);

    private static readonly Regex WordRx = new(@"[A-Z]?[a-z0-9]+|[A-Z]+(?![a-z])", RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, string> NameWords = new(StringComparer.Ordinal)
    {
        ["publisher"] = "publisher", ["vendor"] = "publisher", ["process"] = "process", ["device"] = "device", ["product"] = "product",
        ["app"] = "app", ["apps"] = "app", ["name"] = "item", ["names"] = "item",
    };

    private static readonly HashSet<string> NeverWords = new(StringComparer.Ordinal)
    {
        "path", "paths", "image", "file", "dir", "folder", "command", "cmd", "args", "arguments", "message", "text", "title", "description",
        "user", "username", "account", "sid", "address", "ip", "mac", "ssid", "bssid", "serial", "guid", "uuid", "url", "uri", "email", "host",
        "hostname", "domain", "token", "secret", "password", "env", "registry", "pid", "computer", "machine", "network", "wifi",
    };

    public override IReadOnlyCollection<string> CollectorIds { get; } = [];
    public override string Source(string collectorId) => "Simulated data";

    public override Mapped? Map(string collectorId, string subject, JsonObject v, Out o)
    {
        var segments = subject.Split('.');
        if (segments.Length is 0 or > 8) return null;
        for (var i = 0; i < segments.Length; i++)
            if (!SegmentRx.IsMatch(segments[i])) segments[i] = Slug(o.AliasOf("item", segments[i]));
        var safeSubject = string.Join('.', segments);

        Copy(v, o.V, o, depth: 0, path: "");
        var parts = new List<string>();
        foreach (var (k, n) in o.V)
        {
            if (parts.Count >= 6) break;
            if (n is JsonValue jv) parts.Add($"{k} {(jv.TryGetValue(out string? s) ? s : jv.ToJsonString())}");
        }
        var label = safeSubject.Replace('.', ' ').Replace('_', ' ');
        return new(safeSubject, parts.Count == 0 ? $"Simulated {label}." : $"Simulated {label}: {string.Join(", ", parts)}.");
    }

    private static void Copy(JsonObject from, JsonObject to, Out o, int depth, string path)
    {
        foreach (var (key, node) in from.Take(32))
        {
            if (!KeyRx.IsMatch(key)) { o.Dropped(node ?? JsonValue.Create(0)); continue; }
            var words = WordRx.Matches(key).Select(m => m.Value.ToLowerInvariant()).ToList();
            var kinds = words.Select(w => NameWords.GetValueOrDefault(w)).Where(k => k is not null).ToList();
            var nameKind = kinds.FirstOrDefault(k => k != "item") ?? kinds.FirstOrDefault();
            if (words.Any(NeverWords.Contains)) { o.Dropped(node); continue; }
            switch (node)
            {
                case null:
                    to[key] = null;
                    break;
                case JsonObject obj when depth < 1:
                    var child = new JsonObject();
                    Copy(obj, child, o, depth + 1, path + key + ".");
                    to[key] = child;
                    break;
                case JsonArray arr when depth < 1:
                    var outArr = new JsonArray();
                    foreach (var x in arr.Take(32))
                    {
                        if (Scalar(x, nameKind, o, path + key) is { } s) outArr.Add(s);
                        else o.Dropped(x ?? JsonValue.Create(0));
                    }
                    to[key] = outArr;
                    break;
                case JsonValue:
                    if (Scalar(node, nameKind, o, path + key) is { } sv) to[key] = sv;
                    else o.Dropped(node);
                    break;
                default:
                    o.Dropped(node);
                    break;
            }
        }
    }

    private static JsonNode? Scalar(JsonNode? n, string? nameKind, Out o, string fieldPath)
    {
        if (Read.Bool(n) is { } b) return b;
        if (Read.Num(n) is { } d) return Math.Abs(d) < 1e15 ? Math.Round(d, 1, MidpointRounding.AwayFromZero) : null;
        if (Read.Str(n) is not { } s) return null;
        if (nameKind is not null)
        {
            var (label, untrusted) = o.Resolve(nameKind, s);
            if (label is not null && untrusted) o.MarkUntrusted(fieldPath);
            return label;
        }
        var f = Sanitize.Fold(s);
        if (f.Length == 0 || !ValueRx.IsMatch(f) || f.Count(c => c == ' ') > 2 || Sanitize.LooksSensitive(f)) return null;
        return f;
    }
}

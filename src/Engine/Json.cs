using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OptimizationTool.Engine;

internal static class Json
{
    public static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    public static string Write(JsonNode? node) => node?.ToJsonString(Compact) ?? "null";

    public static JsonNode? Parse(string text) => JsonNode.Parse(text);

    /// <summary>Copy with object keys sorted recursively; used for request digests.</summary>
    public static JsonNode? Canonical(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Canonical(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Canonical).ToArray()),
        null => null,
        _ => node.DeepClone(),
    };

    public static string Digest(JsonNode? node) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Write(Canonical(node)))));

    public static JsonArray Array(IEnumerable<string> items) => new(items.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());

    /// <summary>A JSON number as double, whether the node was parsed or built in-process (TryGetValue alone misses int-backed values).</summary>
    public static double? Number(JsonValue v) =>
        v.GetValueKind() == System.Text.Json.JsonValueKind.Number && double.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;

    public static List<string> Strings(JsonNode? node) =>
        node is JsonArray a ? a.Select(x => x?.GetValue<string>() ?? "").ToList() : [];
}

/// <summary>UTC ISO-8601 timestamps with millisecond precision and a Z suffix.</summary>
internal static class Time
{
    public static string Format(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

/// <summary>Reads typed request parameters, failing with -32602 and the offending field.</summary>
internal static class P
{
    public static JsonObject Obj(JsonNode? node) => node switch
    {
        null => [],
        JsonObject o => o,
        _ => throw RpcError.Params("params", "params must be an object."),
    };

    public static string? Str(JsonObject p, string field, bool required = true, int maxLength = 256)
    {
        var node = p[field];
        if (node is null)
            return required ? throw RpcError.Params(field, $"{field} is required.") : null;
        if (node is not JsonValue v || !v.TryGetValue<string>(out var s))
            throw RpcError.Params(field, $"{field} must be a string.");
        if (s.Length > maxLength) throw RpcError.Params(field, $"{field} is longer than {maxLength} characters.");
        return s;
    }

    public static string Req(JsonObject p, string field, int maxLength = 256) => Str(p, field, true, maxLength)!;

    public static long? Int(JsonObject p, string field, bool required = true)
    {
        var node = p[field];
        if (node is null)
            return required ? throw RpcError.Params(field, $"{field} is required.") : null;
        if (node is JsonValue v)
        {
            if (Json.Number(v) is double d && d == Math.Floor(d) && Math.Abs(d) < 1e15) return (long)d;
        }
        throw RpcError.Params(field, $"{field} must be an integer.");
    }

    public static string IdempotencyKey(JsonObject p)
    {
        var key = Req(p, "idempotencyKey", 64);
        if (key.Length == 0) throw RpcError.Params("idempotencyKey", "idempotencyKey must not be empty.");
        return key;
    }

    public static string OneOf(JsonObject p, string field, IReadOnlyCollection<string> allowed, bool required = true)
    {
        var s = Str(p, field, required);
        if (s is null) return "";
        return allowed.Contains(s) ? s : throw RpcError.Params(field, $"{field} must be one of: {string.Join(", ", allowed)}.");
    }
}

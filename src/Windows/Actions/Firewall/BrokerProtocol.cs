using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OptimizationTool.Windows.Actions.Firewall;

/// <summary>One request from the host to the elevated broker. Only these fields are read; everything is validated.</summary>
public sealed record BrokerRequest(
    string Nonce, string ActionId, string Mode, string PlanId, string PlanHash, string ContentHash,
    JsonObject Target, JsonObject Before, JsonObject After)
{
    public JsonObject ToJson() => new()
    {
        ["type"] = "request", ["nonce"] = Nonce, ["actionId"] = ActionId, ["mode"] = Mode, ["planId"] = PlanId,
        ["planHash"] = PlanHash, ["contentHash"] = ContentHash,
        ["target"] = Target.DeepClone(), ["before"] = Before.DeepClone(), ["after"] = After.DeepClone(),
    };

    /// <summary>Null when any field is missing, of the wrong type, or out of shape.</summary>
    public static BrokerRequest? Parse(JsonNode? node)
    {
        if (node is not JsonObject o || FirewallPlan.Str(o["type"]) != "request") return null;
        string? S(string k, int max) => FirewallPlan.Str(o[k]) is { } s && s.Length > 0 && s.Length <= max ? s : null;
        var nonce = S("nonce", 64); var action = S("actionId", 64); var mode = S("mode", 16);
        var planId = S("planId", 80); var planHash = S("planHash", 128); var content = S("contentHash", 64);
        if (nonce is null || action is null || mode is null || planId is null || planHash is null || content is null) return null;
        if (o["target"] is not JsonObject t || o["before"] is not JsonObject b || o["after"] is not JsonObject a) return null;
        return new BrokerRequest(nonce, action, mode, planId, planHash, content, (JsonObject)t.DeepClone(), (JsonObject)b.DeepClone(), (JsonObject)a.DeepClone());
    }
}

/// <summary>The broker's single answer. State: verified | failed | indeterminate | declined.</summary>
public sealed record BrokerResponse(string Nonce, string State, JsonObject Observed, string ReceiptText, string? Code, string? Message, string? ReceiptId)
{
    public JsonObject ToJson() => new()
    {
        ["type"] = "outcome", ["nonce"] = Nonce, ["state"] = State, ["observed"] = Observed.DeepClone(),
        ["receiptText"] = ReceiptText, ["code"] = Code, ["message"] = Message, ["receiptId"] = ReceiptId,
    };

    public static BrokerResponse? Parse(JsonNode? node)
    {
        if (node is not JsonObject o || FirewallPlan.Str(o["type"]) != "outcome") return null;
        var nonce = FirewallPlan.Str(o["nonce"]); var state = FirewallPlan.Str(o["state"]);
        if (nonce is null || state is not ("verified" or "failed" or "indeterminate" or "declined")) return null;
        var observed = o["observed"] as JsonObject ?? new JsonObject();
        return new BrokerResponse(nonce, state, (JsonObject)observed.DeepClone(), Clip(FirewallPlan.Str(o["receiptText"]), 300) ?? "",
            Clip(FirewallPlan.Str(o["code"]), 64), Clip(FirewallPlan.Str(o["message"]), 300), Clip(FirewallPlan.Str(o["receiptId"]), 80));
    }

    private static string? Clip(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}

/// <summary>Pipe name, nonce and framing shared by the host's BrokerElevation and the broker.</summary>
public static partial class BrokerProtocol
{
    public const string PipePrefix = "OptimizationTool.Broker.";
    /// <summary>Requests are tiny; anything larger is refused before allocating.</summary>
    public const int MaxFrameBytes = 64 * 1024;

    public static string NewPipeName() => PipePrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    public static string NewNonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public static bool IsValidPipeName(string? s) => s is not null && PipeRegex().IsMatch(s);
    public static bool IsValidNonce(string? s) => s is not null && NonceRegex().IsMatch(s);

    public static bool NonceEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    public static async Task WriteAsync(Stream stream, JsonObject message, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(message.ToJsonString());
        if (payload.Length > MaxFrameBytes) throw new InvalidDataException("Frame too large.");
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads one frame; throws <see cref="InvalidDataException"/> for oversize, truncated or non-JSON frames.</summary>
    public static async Task<JsonNode?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        if (await Fill(stream, header, ct).ConfigureAwait(false) < 4) throw new InvalidDataException("Truncated header.");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0 || length > MaxFrameBytes) throw new InvalidDataException("Frame too large.");
        var payload = new byte[length];
        if (await Fill(stream, payload, ct).ConfigureAwait(false) < length) throw new InvalidDataException("Truncated frame.");
        try { return JsonNode.Parse(payload, documentOptions: new System.Text.Json.JsonDocumentOptions { MaxDepth = 16 }); }
        catch (System.Text.Json.JsonException) { throw new InvalidDataException("Not JSON."); }
    }

    private static async Task<int> Fill(Stream s, byte[] buf, CancellationToken ct)
    {
        var total = 0;
        while (total < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    [GeneratedRegex("^OptimizationTool\\.Broker\\.[0-9a-f]{32}$")]
    private static partial Regex PipeRegex();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex NonceRegex();
}

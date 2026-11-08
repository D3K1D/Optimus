using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OptimizationTool.Desktop.Bridge;

/// <summary>What the shell does with one renderer message.</summary>
public abstract record BridgeDecision
{
    /// <summary>Drop silently (wrong sender). Nothing is posted back.</summary>
    public sealed record Ignore(string Reason) : BridgeDecision;

    /// <summary>Post this bridge message straight back (validation error).</summary>
    public sealed record Reply(string Json, string Reason) : BridgeDecision;

    /// <summary>Forward to the backend and relay the response under the renderer's id.</summary>
    public sealed record Forward(JsonNode Id, string Method, JsonObject? Params) : BridgeDecision;

    /// <summary>Handle inside the shell (never forwarded), e.g. shell.openSettings.</summary>
    public sealed record Shell(JsonNode Id, string Method, JsonObject? Params) : BridgeDecision;
}

/// <summary>
/// Validates renderer → shell messages (contracts/protocol-v1.md §7). Pure: no WebView2, no pipe.
/// </summary>
public sealed class BridgeRouter
{
    public const string AppHost = "app.optimizationtool.invalid";
    public const string AppOrigin = "https://app.optimizationtool.invalid/";
    public const string StartUrl = AppOrigin + "index.html";
    public const int MaxMessageBytes = 256 * 1024;
    public const int MaxMethodLength = 64;

    /// <summary>Desktop allowlist (§6, plus events.read from §5). session.hello is shell-only.</summary>
    public static readonly FrozenSet<string> DesktopMethods = new[]
    {
        "onboarding.get", "onboarding.save",
        "inventory.start", "inventory.get", "inventory.cancel",
        "starter.list",
        "scan.start", "scan.get", "scan.cancel",
        "report.get", "report.list", "evidence.get",
        "followup.get", "followup.respond",
        "events.read",
        // v1.1 (§9)
        "finding.get", "finding.setDisposition", "finding.clearDisposition",
        "scan.skipCollector", "report.compare",
        // v1.2 (m3-agent.md §4). provider.setKey and provider.setClient (m5) are shell-only (sent by ApiKeyRequest / ClientConnector); agent.pair, agent.pairStatus, tool.call are agent-only.
        "provider.list", "provider.connect", "provider.disconnect",
        "agent.start", "agent.get", "agent.active", "agent.ask", "agent.stop", "agent.cancel", "agent.approvePairing",
        "review.preview", "review.submit", "review.export", "review.import",
        // v1.3 (m4-actions.md §4). action.challenge and action.approve are shell-only (sent by PlanApproval).
        "action.catalog", "action.preview", "action.get", "action.decline", "action.undo",
        "operation.get", "operation.list", "manual.recheck",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Renderer-callable methods the shell handles itself (§9). Never forwarded to the backend.</summary>
    public static readonly FrozenSet<string> ShellMethods = new[] { SettingsLauncher.Method, SettingsLauncher.UrlMethod, ApiKeyRequest.Method, PlanApproval.Method,
        ClientConnector.StatusMethod, ClientConnector.RegisterMethod, ClientConnector.UnregisterMethod, ClientConnector.InstallMethod, ClientConnector.OpenMethod, ClientConnector.SignInMethod }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = 64 };

    /// <summary>True when <paramref name="uri"/> belongs to exactly https://app.optimizationtool.invalid (default port, no user info).</summary>
    public static bool IsAppOrigin(string? uri)
    {
        if (string.IsNullOrEmpty(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var u)) return false;
        return u.Scheme == Uri.UriSchemeHttps
            && string.Equals(u.IdnHost, AppHost, StringComparison.Ordinal)
            && u.IsDefaultPort
            && u.UserInfo.Length == 0;
    }

    public BridgeDecision Evaluate(string? json, string? source, bool isTopLevel)
    {
        if (!isTopLevel) return new BridgeDecision.Ignore("frame");
        if (!IsAppOrigin(source)) return new BridgeDecision.Ignore("origin");
        if (json is null) return Invalid(null, "json", "Empty message.");

        // chars >= bytes for UTF-16 → UTF-8 in the worst case direction we care about, so test cheaply first.
        if (json.Length > MaxMessageBytes || Encoding.UTF8.GetByteCount(json) > MaxMessageBytes)
            return Invalid(null, "size", "Message too large.");

        JsonNode? root;
        try { root = JsonNode.Parse(json, documentOptions: ParseOptions); }
        catch (JsonException) { return Invalid(null, "json", "Message is not valid JSON."); }

        if (root is not JsonObject msg) return Invalid(null, "json", "Message must be a JSON object.");

        JsonNode? id = null;
        if (msg["id"] is JsonValue idValue && idValue.GetValueKind() == JsonValueKind.Number)
            id = idValue.DeepClone();

        if (msg["type"] is not JsonValue typeValue || !typeValue.TryGetValue<string>(out var type) || type != "rpc")
            return Invalid(id, "type", "Unsupported message type.");
        if (id is null) return Invalid(null, "id", "Missing numeric id.");

        if (msg["method"] is not JsonValue methodValue || !methodValue.TryGetValue<string>(out var method)
            || method.Length == 0 || method.Length > MaxMethodLength)
            return Invalid(id, "method", "Missing method.");

        var isShell = ShellMethods.Contains(method);
        if (!isShell && !DesktopMethods.Contains(method))
            return new BridgeDecision.Reply(
                BridgeMessages.Error(id, -32012, "Method not allowed.",
                    new JsonObject { ["method"] = method, ["clientKind"] = "desktop" }),
                "method_not_allowed");

        JsonObject? parameters = null;
        if (msg.TryGetPropertyValue("params", out var p) && p is not null)
        {
            if (p is not JsonObject po) return Invalid(id, "params", "params must be an object.");
            parameters = (JsonObject)po.DeepClone();
        }

        return isShell
            ? new BridgeDecision.Shell(id, method, parameters)
            : new BridgeDecision.Forward(id, method, parameters);
    }

    private static BridgeDecision.Reply Invalid(JsonNode? id, string field, string message) =>
        new(BridgeMessages.Error(id, -32600, message, new JsonObject { ["field"] = field }), "invalid:" + field);
}

/// <summary>Builders for shell → renderer messages (§7).</summary>
public static class BridgeMessages
{
    public static string Result(JsonNode id, JsonNode? result) =>
        new JsonObject { ["type"] = "rpc.result", ["id"] = id.DeepClone(), ["result"] = result?.DeepClone() }.ToJsonString();

    public static string Error(JsonNode? id, int code, string message, JsonNode? data = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (data is not null) error["data"] = data.DeepClone();
        return new JsonObject { ["type"] = "rpc.error", ["id"] = id?.DeepClone(), ["error"] = error }.ToJsonString();
    }

    /// <summary>Maps a raw JSON-RPC response from the backend onto the renderer's id. Only code/message/data are relayed.</summary>
    public static string FromResponse(JsonNode id, JsonObject response)
    {
        if (response["error"] is JsonObject err)
        {
            var code = err["code"] is JsonValue c && c.TryGetValue<int>(out var ci) ? ci : -32603;
            var message = err["message"] is JsonValue m && m.TryGetValue<string>(out var ms) ? ms : "Backend error.";
            return Error(id, code, message, err["data"]);
        }
        return Result(id, response["result"]);
    }

    public static string Unavailable(JsonNode id) =>
        Error(id, -32000, "Backend unavailable.", new JsonObject { ["retryable"] = true });

    public static string Event(JsonNode? notifyParams) =>
        new JsonObject { ["type"] = "event", ["event"] = notifyParams?.DeepClone() }.ToJsonString();

    public static string Backend(string state, JsonNode? detail) =>
        new JsonObject { ["type"] = "backend", ["state"] = state, ["detail"] = detail?.DeepClone() }.ToJsonString();
}

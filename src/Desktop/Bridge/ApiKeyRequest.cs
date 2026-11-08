using System.Collections.Frozen;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OptimizationTool.Desktop.Bridge;

/// <summary>What the native key dialog returns. Hold it only for the duration of one setKey call.</summary>
public sealed class ApiKeyEntry(string apiKey, bool remember)
{
    public string ApiKey { get; private set; } = apiKey;
    public bool Remember { get; } = remember;

    /// <summary>Drops the reference (.NET strings cannot be zeroed; this keeps the key's lifetime short).</summary>
    public void Clear() => ApiKey = "";

    public override string ToString() => "ApiKeyEntry";
}

/// <summary>Asks the user for a key outside the renderer. Returns null on cancel.</summary>
public interface IApiKeyPrompt
{
    Task<ApiKeyEntry?> PromptAsync(string kind, CancellationToken ct);
}

/// <summary>
/// Shell-handled <c>shell.requestApiKey {kind}</c> (contracts/m3-agent.md §4, §7). Shows the native dialog, sends
/// <c>provider.setKey</c> to the host itself, and returns only <c>{status, error?}</c> to the renderer. Never forwarded.
/// The key never reaches the renderer, the log, an exception message, a command line or an environment block.
/// </summary>
public sealed partial class ApiKeyRequest
{
    public const string Method = "shell.requestApiKey";
    public const string SetKeyMethod = "provider.setKey";

    public static readonly FrozenSet<string> Kinds = new[] { "anthropic_api", "openai_api" }.ToFrozenSet(StringComparer.Ordinal);
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    private readonly IApiKeyPrompt _prompt;
    private readonly Func<long> _nowTicks;
    private readonly Lock _lock = new();
    private bool _open;
    private long _lastStartTicks = long.MinValue;

    public ApiKeyRequest(IApiKeyPrompt prompt, Func<long>? nowTicks = null)
    {
        _prompt = prompt;
        _nowTicks = nowTicks ?? Stopwatch.GetTimestamp;
    }

    public async Task<string> HandleAsync(JsonNode id, JsonObject? parameters, IBackend backend, DesktopLog log, CancellationToken ct)
    {
        if (parameters is null || parameters.Count != 1
            || parameters["kind"] is not JsonValue v || !v.TryGetValue<string>(out var kind) || !Kinds.Contains(kind))
        {
            log.Warn($"shell: refused {Method} (invalid params)");
            return BridgeMessages.Error(id, -32602, "Invalid params.", new JsonObject { ["field"] = "kind" });
        }

        lock (_lock)
        {
            if (_open)
                return BridgeMessages.Error(id, -32013, "A key dialog is already open.", new JsonObject { ["reason"] = "busy" });
            var now = _nowTicks();
            if (_lastStartTicks != long.MinValue && now - _lastStartTicks < MinInterval.TotalSeconds * Stopwatch.Frequency)
                return BridgeMessages.Error(id, -32013, "Too many requests.", new JsonObject { ["reason"] = "rate_limited" });
            _open = true;
            _lastStartTicks = now;
        }

        try
        {
            var status = await PromptAndSetAsync(kind, backend, log, ct);
            return BridgeMessages.Result(id, status);
        }
        finally
        {
            lock (_lock) _open = false;
        }
    }

    private async Task<JsonObject> PromptAndSetAsync(string kind, IBackend backend, DesktopLog log, CancellationToken ct)
    {
        ApiKeyEntry? entry;
        try
        {
            entry = await _prompt.PromptAsync(kind, ct);
        }
        catch (OperationCanceledException)
        {
            entry = null;
        }
        if (entry is null || entry.ApiKey.Length == 0)
        {
            log.Info($"shell: {Method} kind={kind} cancelled");
            return Status("cancelled");
        }

        var p = new JsonObject { ["kind"] = kind, ["apiKey"] = entry.ApiKey, ["remember"] = entry.Remember };
        JsonObject? response = null;
        JsonObject result;
        try
        {
            response = await backend.CallAsync(SetKeyMethod, p, ct);
            result = MapResponse(response);
        }
        catch (BackendUnavailableException)
        {
            result = Status("error", "backend_unavailable");
        }
        catch (OperationCanceledException)
        {
            result = Status("error", "cancelled");
        }
        catch (Exception ex)
        {
            // Type name only: never a message that could carry the request.
            log.Error($"shell: {SetKeyMethod} failed ({ex.GetType().Name})");
            result = Status("error", "failed");
        }
        finally
        {
            p.Remove("apiKey");
            entry.Clear();
        }

        log.Info($"shell: {SetKeyMethod} kind={kind} remember={entry.Remember} status={result["status"]!.GetValue<string>()}" +
                 (result["error"] is { } e ? $" error={e.GetValue<string>()}" : ""));
        return result;
    }

    /// <summary>Relays only a known status and a short code-shaped error. Free text from the host is never passed on.</summary>
    private static JsonObject MapResponse(JsonObject response)
    {
        if (response["error"] is JsonObject err)
        {
            var reason = err["data"]?["reason"] is JsonValue r && r.TryGetValue<string>(out var rs) && CodePattern().IsMatch(rs) ? rs : "backend_error";
            return Status("error", reason);
        }
        var status = response["result"]?["status"] is JsonValue s && s.TryGetValue<string>(out var ss) ? ss : null;
        if (status == "connected") return Status("connected");
        var code = response["result"]?["error"] is JsonValue c && c.TryGetValue<string>(out var cs) && CodePattern().IsMatch(cs)
            ? cs
            : response["result"]?["detail"] is JsonValue d && d.TryGetValue<string>(out var ds) && CodePattern().IsMatch(ds) ? ds : "check_failed";
        return Status("error", code);
    }

    private static JsonObject Status(string status, string? error = null)
    {
        var o = new JsonObject { ["status"] = status };
        if (error is not null) o["error"] = error;
        return o;
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex CodePattern();
}

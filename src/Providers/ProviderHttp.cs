using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Providers;

/// <summary>
/// Shared HTTP plumbing for the adapters: one request with a timeout, a fully read body, and the mapping of
/// transport and HTTP failures to <see cref="ProviderException"/> codes. Nothing here logs. Exception text is
/// built from fixed phrases plus a scrubbed, bounded provider message; never from headers or the key.
/// </summary>
internal static partial class ProviderHttp
{
    /// <summary>Key in <see cref="Exception.Data"/> holding the provider's retry-after hint in whole seconds (int).</summary>
    public const string RetryAfterSecondsKey = "retryAfterSeconds";

    public static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false,
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    public sealed record RawResponse(HttpStatusCode Status, string Body, HttpResponseHeaders Headers);

    /// <summary>
    /// Sends the request and reads the whole body. Caller cancellation propagates as <see cref="OperationCanceledException"/>;
    /// the adapter's own timeout becomes code "timeout"; transport failures become "network"; a body cut off mid-read "incomplete".
    /// </summary>
    public static async Task<RawResponse> SendAsync(HttpClient http, HttpRequestMessage request, TimeSpan timeout, string provider, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException("timeout", $"{provider} did not respond within {timeout.TotalSeconds:0} s.", retryable: false);
        }
        catch (HttpRequestException)
        {
            throw new ProviderException("network", $"Could not reach {provider}. Check the internet connection.", retryable: false);
        }

        using (response)
        {
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ProviderException("timeout", $"{provider} stopped responding mid-answer.", retryable: false);
            }
            catch (Exception e) when (e is HttpRequestException or IOException)
            {
                throw new ProviderException("incomplete", $"The answer from {provider} was cut off.", retryable: false);
            }
            return new RawResponse(response.StatusCode, body, response.Headers);
        }
    }

    /// <summary>Parses a success body as a JSON object; anything else is a truncated or partial answer.</summary>
    public static JsonObject ParseObject(string body, string provider)
    {
        try
        {
            if (JsonNode.Parse(body) is JsonObject obj) return obj;
        }
        catch (JsonException) { }
        throw new ProviderException("incomplete", $"The answer from {provider} was incomplete or not valid JSON.", retryable: false);
    }

    /// <summary>Maps a non-success HTTP response to a <see cref="ProviderException"/>.</summary>
    public static ProviderException MapError(RawResponse raw, string provider, string apiKey)
    {
        var (type, code, message) = ReadErrorBody(raw.Body);
        var detail = Scrub(message, apiKey);
        var status = (int)raw.Status;
        int? retryAfter = ReadRetryAfter(raw.Headers);

        bool quotaSignal = IsQuota(type) || IsQuota(code);
        ProviderException ex = status switch
        {
            401 or 403 => new("auth", $"{provider} rejected the API key ({status}).", retryable: false),
            402 => new("quota", $"{provider} reports a billing or quota problem.{Suffix(detail)}", retryable: false),
            429 when quotaSignal => new("quota", $"{provider} reports the account is out of credit or quota.{Suffix(detail)}", retryable: false),
            429 => new("rate_limited", $"{provider} rate limit reached.{RetrySuffix(retryAfter)}", retryable: true),
            408 => new("timeout", $"{provider} timed out handling the request.", retryable: false),
            500 or 502 or 503 or 504 or 529 => new("overloaded", $"{provider} is overloaded or unavailable ({status}).{RetrySuffix(retryAfter)}", retryable: true),
            >= 400 and < 500 => new("bad_request", $"{provider} rejected the request ({status}).{Suffix(detail)}", retryable: false),
            _ => new("overloaded", $"{provider} returned an unexpected status ({status}).", retryable: true),
        };
        if (retryAfter is int seconds) ex.Data[RetryAfterSecondsKey] = seconds;
        return ex;
    }

    static bool IsQuota(string? s) => s is not null &&
        (s.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase) || s.Contains("billing", StringComparison.OrdinalIgnoreCase));

    static string Suffix(string? detail) => string.IsNullOrEmpty(detail) ? "" : " " + detail;

    static string RetrySuffix(int? seconds) => seconds is int s ? $" Retry after {s} s." : "";

    static (string? Type, string? Code, string? Message) ReadErrorBody(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is JsonObject root && root["error"] is JsonObject err)
                return (Str(err["type"]), Str(err["code"]), Str(err["message"]));
        }
        catch (JsonException) { }
        return (null, null, null);
    }

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    /// <summary>retry-after-ms (OpenAI), then retry-after as seconds or an HTTP date. Rounded up to whole seconds.</summary>
    internal static int? ReadRetryAfter(HttpResponseHeaders headers)
    {
        if (headers.TryGetValues("retry-after-ms", out var ms) &&
            double.TryParse(ms.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var msValue) && msValue >= 0)
            return (int)Math.Ceiling(msValue / 1000.0);
        if (headers.RetryAfter is { } ra)
        {
            if (ra.Delta is TimeSpan d) return (int)Math.Ceiling(Math.Max(0, d.TotalSeconds));
            if (ra.Date is DateTimeOffset date) return (int)Math.Ceiling(Math.Max(0, (date - DateTimeOffset.UtcNow).TotalSeconds));
        }
        return null;
    }

    /// <summary>
    /// Makes a provider message safe to surface: removes the key itself, anything shaped like an API key (even masked,
    /// e.g. "sk-proj-****abcd"), control characters; bounds the length.
    /// </summary>
    internal static string? Scrub(string? message, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var s = message;
        if (!string.IsNullOrEmpty(apiKey)) s = s.Replace(apiKey, "[key]", StringComparison.Ordinal);
        s = KeyLike().Replace(s, "[key]");
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(char.IsControl(c) ? ' ' : c);
        s = sb.ToString().Trim();
        return s.Length > 300 ? s[..300] + "…" : s;
    }

    [GeneratedRegex(@"\b(sk|rk|pk)-[A-Za-z0-9_\-\*\.]{4,}", RegexOptions.CultureInvariant)]
    private static partial Regex KeyLike();

    /// <summary>Serialises a tool result's content as the string both providers expect.</summary>
    public static string ContentToString(JsonNode? content) => content switch
    {
        null => "null",
        JsonValue v when v.TryGetValue(out string? s) => s,
        _ => content.ToJsonString(),
    };

    public static StringContent JsonBody(JsonObject body) =>
        new(body.ToJsonString(), Encoding.UTF8, "application/json");
}

/// <summary>Checks whether a tool's JSON Schema can be sent with strict decoding without being rewritten.</summary>
internal static class StrictSchema
{
    /// <param name="allRequired">OpenAI strict mode also requires every property to be listed in "required".</param>
    public static bool IsCompatible(JsonObject schema, bool allRequired) => Check(schema, allRequired, depth: 0);

    static bool Check(JsonNode? node, bool allRequired, int depth)
    {
        if (depth > 32) return false;
        switch (node)
        {
            case JsonArray arr:
                return arr.All(n => Check(n, allRequired, depth + 1));
            case JsonObject obj:
                if (IsObjectType(obj))
                {
                    if (obj["additionalProperties"] is not JsonValue ap || !ap.TryGetValue(out bool apValue) || apValue) return false;
                    if (allRequired && obj["properties"] is JsonObject props)
                    {
                        var required = obj["required"] as JsonArray;
                        var names = required?.Select(r => r is JsonValue v && v.TryGetValue(out string? s) ? s : null).ToHashSet() ?? [];
                        if (props.Any(p => !names.Contains(p.Key))) return false;
                    }
                }
                foreach (var (key, child) in obj)
                {
                    if (key is "enum" or "const" or "default" or "examples" or "description" or "title") continue;
                    if (key == "properties" && child is JsonObject ps)
                    {
                        if (!ps.All(p => Check(p.Value, allRequired, depth + 1))) return false;
                    }
                    else if (!Check(child, allRequired, depth + 1)) return false;
                }
                return true;
            default:
                return true;
        }
    }

    static bool IsObjectType(JsonObject obj) => obj["type"] switch
    {
        JsonValue v when v.TryGetValue(out string? t) => t == "object",
        JsonArray a => a.Any(x => x is JsonValue v && v.TryGetValue(out string? t) && t == "object"),
        _ => obj.ContainsKey("properties"),
    };
}

internal static class JsonNodeExtensions
{
    /// <summary>The node's string value, or null when absent or not a string (never throws).</summary>
    public static string? AsString(this JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}

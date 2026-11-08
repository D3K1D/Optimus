using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Providers;

/// <summary>How function tools are offered on the ChatGPT plan route (docs/chatgpt-signin.md §4; feature-test per model).</summary>
public enum PlanToolDelivery
{
    /// <summary>One top-level {"type":"namespace"} tool grouping the function tools.</summary>
    Namespace,
    /// <summary>An {"type":"additional_tools","role":"developer"} item at the start of the input.</summary>
    AdditionalTools,
}

/// <summary>
/// "ChatGPT plan" route: the OpenAI Responses wire format with plan-mode rules — store:false, stream:true (the SSE
/// stream is fully drained into one complete turn), full input every turn, no previous_response_id, none of the
/// unsupported fields (no max_output_tokens: output and time budgets are enforced here), tools via a namespace or
/// additional_tools. Access tokens come from <see cref="ChatGptSignIn"/>; a 401 refreshes once and retries.
/// Extra error code: <c>not_eligible</c> (403: account, workspace, region or route not allowed).
/// </summary>
public sealed class ChatGptPlanAdapter : IProviderAdapter, IToolCapability
{
    public const string KindId = "chatgpt_plan";
    const string Provider = "ChatGPT";

    /// <summary>Fields the plan route rejects; never sent.</summary>
    public static readonly IReadOnlySet<string> BannedFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "background", "conversation", "max_output_tokens", "max_tool_calls", "metadata", "moderation", "multi_agent", "prompt",
        "prompt_cache_retention", "safety_identifier", "temperature", "top_logprobs", "top_p", "truncation", "user", "previous_response_id",
    };

    readonly HttpClient _http;
    readonly ChatGptSignIn? _signIn;
    readonly Uri _base;

    /// <param name="signIn">Token source. When null, the credential passed to each call is used as the bearer token.</param>
    public ChatGptPlanAdapter(HttpClient http, ChatGptSignIn? signIn, Uri? baseUri = null)
    {
        _http = http;
        _signIn = signIn;
        _base = baseUri ?? new Uri("https://api.openai.com/");
    }

    public string Kind => KindId;
    public string DisplayName => "ChatGPT";
    public string DefaultModel => "gpt-6.1-sol";

    public string ReasoningEffort { get; init; } = "medium";
    public PlanToolDelivery ToolDelivery { get; init; } = PlanToolDelivery.Namespace;
    public string ToolNamespace { get; init; } = "optimization_tool";
    public TimeSpan TurnTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan CheckTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Local output budget: rough characters per token used against MaxOutputTokens.</summary>
    public int CharsPerToken { get; init; } = 4;
    /// <summary>Hard cap on the raw SSE bytes read for one turn.</summary>
    public long MaxStreamBytes { get; init; } = 16 * 1024 * 1024;

    // ---- check --------------------------------------------------------------------------------------------

    public async Task<ProviderCheck> CheckAsync(ProviderCredential credential, CancellationToken ct)
    {
        try
        {
            var raw = await WithTokenAsync(credential, async token =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_base, "v1/models"));
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                return await ProviderHttp.SendAsync(_http, request, CheckTimeout, Provider, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            if (!raw.Status.IsSuccess()) throw MapError(raw.Status, raw.Body, raw.Headers);
            var root = ProviderHttp.ParseObject(raw.Body, Provider);
            List<string> models;
            if (root["models"] is JsonArray list)
                models = list.OfType<JsonObject>()
                    .Where(m => m["visibility"].AsString() is null or "list")
                    .Select(m => m["slug"].AsString() ?? m["id"].AsString())
                    .OfType<string>().ToList();
            else
                models = (root["data"] as JsonArray ?? []).Select(m => m?["id"].AsString()).OfType<string>().ToList();
            return new ProviderCheck(true, _signIn?.Account?.Email, models, null, Model: PickModel(models, DefaultModel));
        }
        catch (ProviderException e)
        {
            return new ProviderCheck(false, null, [], $"{e.Code}: {e.Message}", e.Code);
        }
        catch (InvalidOperationException)
        {
            return new ProviderCheck(false, null, [], "incomplete: The model list from ChatGPT had an unexpected shape.", "incomplete");
        }
    }

    /// <summary>The default when the plan lists it (or lists nothing); else the first model the plan lists (its own order).</summary>
    internal static string PickModel(IReadOnlyList<string> models, string fallback) =>
        models.Count == 0 || models.Contains(fallback) ? fallback : models[0];

    // ---- tool capability ----------------------------------------------------------------------------------

    static readonly ToolSpec PingTool = new("ping", "Connectivity check. Call it when asked to.", new System.Text.Json.Nodes.JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
        ["required"] = new JsonArray(),
        ["additionalProperties"] = false,
    });

    /// <summary>
    /// Feature test (uses a little plan usage): one tiny turn offering a "ping" tool in the configured delivery
    /// (namespace / additional_tools). True when the model calls it; false when it answers without a call or the route
    /// rejects the tool shape (bad_request). Auth, eligibility, network and rate errors propagate.
    /// </summary>
    public async Task<bool> SupportsToolsAsync(ProviderCredential credential, string model, CancellationToken ct)
    {
        var request = new ProviderRequest(model, "Follow the user's instruction exactly.",
            [new UserText("Call the ping tool once. Do not answer in text.")], [PingTool], 512);
        try
        {
            var turn = await RunTurnAsync(credential, request, ct).ConfigureAwait(false);
            return turn.StopReason == "tool_use" && turn.Turn.Calls.Any(c => c.Name == "ping");
        }
        catch (ProviderException e) when (e.Code == "bad_request")
        {
            return false;
        }
    }

    // ---- turn ---------------------------------------------------------------------------------------------

    public async Task<ProviderTurn> RunTurnAsync(ProviderCredential credential, ProviderRequest request, CancellationToken ct)
    {
        var body = BuildRequestBody(request);
        var json = body.ToJsonString();
        var outputCharBudget = (long)Math.Max(1, request.MaxOutputTokens) * CharsPerToken;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TurnTimeout);
        try
        {
            var response = await WithTokenAsync(credential, async token =>
            {
                var http = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, "v1/responses"))
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
                http.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                http.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
                try
                {
                    return await _http.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                }
                finally
                {
                    http.Dispose();
                }
            }, ct).ConfigureAwait(false);

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var errBody = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                    throw MapError(response.StatusCode, errBody, response.Headers);
                }
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                var turn = await DrainAsync(stream, outputCharBudget, timeout.Token).ConfigureAwait(false);
                return StripNamespace(turn);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException("timeout", $"{Provider} did not finish within {TurnTimeout.TotalSeconds:0} s.", retryable: false);
        }
        catch (HttpRequestException)
        {
            throw new ProviderException("network", $"Could not reach {Provider}. Check the internet connection.", retryable: false);
        }
        catch (IOException)
        {
            throw new ProviderException("incomplete", $"The answer from {Provider} was cut off.", retryable: false);
        }
    }

    /// <summary>Gets a token (from sign-in when present), sends, and on 401 refreshes once and resends.</summary>
    async Task<T> WithTokenAsync<T>(ProviderCredential credential, Func<string, Task<T>> send, CancellationToken ct)
    {
        var token = _signIn is null ? credential.ApiKey : (await _signIn.GetAccessTokenAsync(ct).ConfigureAwait(false)).ApiKey;
        var result = await send(token).ConfigureAwait(false);
        if (_signIn is null || Status(result) != HttpStatusCode.Unauthorized) return result;
        (result as IDisposable)?.Dispose();
        token = (await _signIn.RefreshAsync(ct).ConfigureAwait(false)).ApiKey;
        return await send(token).ConfigureAwait(false);
    }

    static HttpStatusCode? Status(object? r) => r switch
    {
        HttpResponseMessage m => m.StatusCode,
        ProviderHttp.RawResponse raw => raw.Status,
        _ => null,
    };

    // ---- request ------------------------------------------------------------------------------------------

    internal JsonObject BuildRequestBody(ProviderRequest request)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["store"] = false,
            ["stream"] = true,
        };
        if (!string.IsNullOrEmpty(request.System)) body["instructions"] = request.System;

        var input = OpenAiAdapter.BuildInput(request.Messages);
        if (request.Tools.Count > 0)
        {
            var functions = new JsonArray();
            foreach (var t in request.Tools)
            {
                functions.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = t.InputSchema.DeepClone(),
                    ["strict"] = StrictSchema.IsCompatible(t.InputSchema, allRequired: true),
                });
            }
            if (ToolDelivery == PlanToolDelivery.Namespace)
            {
                body["tools"] = new JsonArray(new JsonObject
                {
                    ["type"] = "namespace",
                    ["name"] = ToolNamespace,
                    ["description"] = "Read-only diagnostics from Optimization Tool on this PC.",
                    ["tools"] = functions,
                });
                // Rebuilt (non-replayed) calls need the namespace they were offered under.
                foreach (var item in input.OfType<JsonObject>())
                    if (item["type"].AsString() == "function_call" && !item.ContainsKey("namespace")) item["namespace"] = ToolNamespace;
            }
            else
            {
                // At the very start so the input stays an append-only prefix across turns.
                input.Insert(0, new JsonObject { ["type"] = "additional_tools", ["role"] = "developer", ["tools"] = functions });
            }
            body["tool_choice"] = "auto";
            body["parallel_tool_calls"] = true;
        }
        body["input"] = input;
        if (OpenAiAdapter.IsReasoningModel(request.Model))
        {
            body["include"] = new JsonArray("reasoning.encrypted_content");
            if (ReasoningEffort is not null) body["reasoning"] = new JsonObject { ["effort"] = ReasoningEffort };
        }
        foreach (var banned in BannedFields) body.Remove(banned);
        return body;
    }

    ProviderTurn StripNamespace(ProviderTurn turn)
    {
        var prefix = ToolNamespace + ".";
        if (!turn.Turn.Calls.Any(c => c.Name.StartsWith(prefix, StringComparison.Ordinal))) return turn;
        var calls = turn.Turn.Calls.Select(c => c.Name.StartsWith(prefix, StringComparison.Ordinal) ? c with { Name = c.Name[prefix.Length..] } : c).ToList();
        return turn with { Turn = turn.Turn with { Calls = calls } };
    }

    // ---- SSE ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads the whole event stream. Succeeds only on response.completed / response.incomplete (both carry the final
    /// response object, parsed by the shared Responses parser); response.failed and "error" events map to codes;
    /// a stream that ends without a terminal event is incomplete. If streamed text and argument deltas exceed the local
    /// output budget, reading stops and the turn ends as max_tokens with no tool calls.
    /// </summary>
    internal async Task<ProviderTurn> DrainAsync(Stream stream, long outputCharBudget, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        var doneItems = new SortedDictionary<int, JsonObject>();
        var text = new StringBuilder();
        long streamedChars = 0, bytes = 0;

        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is not null)
            {
                bytes += line.Length + 1;
                if (bytes > MaxStreamBytes)
                    throw new ProviderException("incomplete", $"The answer from {Provider} was too large.", retryable: false);
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    data.Append(line.AsSpan(5).TrimStart(' ')).Append('\n');
                    continue;
                }
                if (line.Length > 0) continue; // event:, id:, retry:, comments
            }
            if (data.Length > 0)
            {
                var payload = data.ToString().TrimEnd('\n');
                data.Clear();
                if (payload != "[DONE]")
                {
                    JsonObject? ev;
                    try { ev = JsonNode.Parse(payload) as JsonObject; }
                    catch (JsonException) { throw new ProviderException("incomplete", $"The answer from {Provider} was malformed.", retryable: false); }
                    if (ev is not null)
                    {
                        var result = Handle(ev, doneItems, text, ref streamedChars);
                        if (result is not null) return result;
                        if (streamedChars > outputCharBudget) return BudgetStop(doneItems, text, streamedChars);
                    }
                }
            }
            if (line is null)
                throw new ProviderException("incomplete", $"The answer from {Provider} ended early.", retryable: false);
        }
    }

    ProviderTurn? Handle(JsonObject ev, SortedDictionary<int, JsonObject> doneItems, StringBuilder text, ref long streamedChars)
    {
        switch (ev["type"].AsString())
        {
            case "response.output_text.delta":
            case "response.refusal.delta":
                if (ev["delta"].AsString() is { } d) { text.Append(d); streamedChars += d.Length; }
                return null;
            case "response.function_call_arguments.delta":
                if (ev["delta"].AsString() is { } a) streamedChars += a.Length;
                return null;
            case "response.output_item.done":
                if (ev["item"] is JsonObject item)
                    doneItems[ev["output_index"] is JsonValue iv && iv.TryGetValue(out int idx) ? idx : doneItems.Count] = (JsonObject)item.DeepClone();
                return null;
            case "response.completed":
            case "response.incomplete":
                if (ev["response"] is not JsonObject response)
                    throw new ProviderException("incomplete", $"The answer from {Provider} was incomplete.", retryable: false);
                response = (JsonObject)response.DeepClone();
                // Some streams send the final response without its output; use the items collected on the way.
                if (response["output"] is not JsonArray { Count: > 0 } && doneItems.Count > 0)
                    response["output"] = new JsonArray(doneItems.Values.Select(i => (JsonNode)i.DeepClone()).ToArray());
                response["status"] ??= ev["type"].AsString() == "response.completed" ? "completed" : "incomplete";
                try
                {
                    return OpenAiAdapter.ParseResponse(response);
                }
                catch (Exception e) when (e is InvalidOperationException or FormatException)
                {
                    throw new ProviderException("incomplete", $"The answer from {Provider} had an unexpected shape.", retryable: false);
                }
            case "response.failed":
                throw MapFailure(ev["response"]?["error"] as JsonObject);
            case "error":
                throw MapFailure(ev["error"] as JsonObject ?? ev);
            default:
                return null;
        }
    }

    ProviderTurn BudgetStop(SortedDictionary<int, JsonObject> doneItems, StringBuilder text, long streamedChars)
    {
        var kept = new JsonArray(doneItems.Values.Where(i => i["type"].AsString() != "function_call").Select(i => (JsonNode)i.DeepClone()).ToArray());
        var state = new JsonObject { ["kind"] = OpenAiAdapter.KindId, ["output"] = kept };
        var turn = new AssistantTurn(text.Length == 0 ? null : text.ToString(), [], state);
        return new ProviderTurn(turn, "max_tokens", new ProviderUsage(0, (int)Math.Min(int.MaxValue, streamedChars / Math.Max(1, CharsPerToken))));
    }

    // ---- errors -------------------------------------------------------------------------------------------

    internal static ProviderException MapError(HttpStatusCode status, string body, System.Net.Http.Headers.HttpResponseHeaders headers)
    {
        JsonObject? err = null;
        try { err = (JsonNode.Parse(body) as JsonObject)?["error"] as JsonObject; } catch (JsonException) { }
        var code = err?["code"].AsString();
        var retryAfter = ProviderHttp.ReadRetryAfter(headers);
        ProviderException ex = ((int)status, code) switch
        {
            (401, _) => new("auth", "ChatGPT sign-in is no longer accepted. Sign in again.", retryable: false),
            (403, _) => new("not_eligible", NotEligibleMessage(code), retryable: false),
            (429, "subscription_sharing_usage_limit_exceeded") => new("rate_limited", "ChatGPT plan usage limit reached. Check usage in ChatGPT settings." + RetrySuffix(retryAfter), retryable: true),
            (429, _) => new("rate_limited", "ChatGPT rate limit reached." + RetrySuffix(retryAfter), retryable: true),
            (400, "subscription_sharing_unsupported_capability") => new("bad_request", $"ChatGPT plan does not support part of this request ({SafeParam(err?["param"].AsString())}).", retryable: false),
            (>= 500, _) => new("overloaded", $"ChatGPT is unavailable ({(int)status}).{RetrySuffix(retryAfter)}", retryable: true),
            _ => new("bad_request", $"ChatGPT rejected the request ({(int)status}{(code is null ? "" : ", " + SafeParam(code))}).", retryable: false),
        };
        if (retryAfter is int s) ex.Data[ProviderHttp.RetryAfterSecondsKey] = s;
        return ex;
    }

    static ProviderException MapFailure(JsonObject? error)
    {
        var code = error?["code"].AsString();
        return code switch
        {
            "subscription_sharing_usage_limit_exceeded" => new("rate_limited", "ChatGPT plan usage limit reached. Check usage in ChatGPT settings.", retryable: true),
            "rate_limit_exceeded" => new("rate_limited", "ChatGPT rate limit reached.", retryable: true),
            "subscription_sharing_usage_unavailable" or "subscription_sharing_user_unavailable" or "server_error" =>
                new("overloaded", "ChatGPT plan usage is temporarily unavailable.", retryable: true),
            "subscription_sharing_user_not_eligible" or "subscription_sharing_route_not_supported" =>
                new("not_eligible", NotEligibleMessage(code), retryable: false),
            "subscription_sharing_invalid_user" => new("auth", "ChatGPT sign-in is no longer accepted. Sign in again.", retryable: false),
            _ => new("bad_request", $"ChatGPT could not answer ({SafeParam(code ?? "failed")}).", retryable: false),
        };
    }

    static string NotEligibleMessage(string? code) => code switch
    {
        "subscription_sharing_route_not_supported" => "ChatGPT plan usage is not available on this route.",
        _ => "This ChatGPT account, workspace or region can't use plan access in this app.",
    };

    static string RetrySuffix(int? s) => s is int v ? $" Retry after {v} s." : "";

    static string SafeParam(string? s) =>
        s is { Length: > 0 and <= 64 } && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '[' or ']') ? s : "unknown";
}

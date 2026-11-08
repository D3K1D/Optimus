using System.Text.Json;
using System.Text.Json.Nodes;
using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Providers;

/// <summary>
/// OpenAI Responses API (POST /v1/responses), raw HTTP, stateless (store:false), one non-streamed turn per call.
/// Every output item (reasoning with encrypted_content, messages, function_call) is kept verbatim in
/// <see cref="AssistantTurn.ProviderState"/> and replayed unchanged as input on the next turn.
/// </summary>
public sealed class OpenAiAdapter : IProviderAdapter
{
    public const string KindId = "openai_api";
    const string Provider = "OpenAI";

    readonly HttpClient _http;
    readonly Uri _base;

    public OpenAiAdapter(HttpClient http, Uri? baseUri = null)
    {
        _http = http;
        _base = baseUri ?? new Uri("https://api.openai.com/");
    }

    public string Kind => KindId;
    public string DisplayName => "OpenAI (API)";
    public string DefaultModel => "gpt-6.1-sol";

    /// <summary>reasoning.effort for reasoning models.</summary>
    public string ReasoningEffort { get; init; } = "medium";

    public TimeSpan TurnTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan CheckTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public async Task<ProviderCheck> CheckAsync(ProviderCredential credential, CancellationToken ct)
    {
        try
        {
            using var request = NewRequest(HttpMethod.Get, "v1/models", credential);
            var raw = await ProviderHttp.SendAsync(_http, request, CheckTimeout, Provider, ct).ConfigureAwait(false);
            if (!raw.Status.IsSuccess()) throw ProviderHttp.MapError(raw, Provider, credential.ApiKey);
            var root = ProviderHttp.ParseObject(raw.Body, Provider);
            var models = (root["data"] as JsonArray ?? [])
                .Select(m => m?["id"].AsString())
                .OfType<string>()
                .Where(IsChatModel)
                .Order(StringComparer.Ordinal)
                .ToList();
            return new ProviderCheck(true, null, models, null, Model: PickModel(models, DefaultModel));
        }
        catch (ProviderException e)
        {
            return new ProviderCheck(false, null, [], $"{e.Code}: {e.Message}", e.Code);
        }
        catch (InvalidOperationException)
        {
            return new ProviderCheck(false, null, [], "incomplete: The model list from OpenAI had an unexpected shape.", "incomplete");
        }
    }

    /// <summary>
    /// The model to use for an account that lists <paramref name="models"/>: the default when listed (or when nothing
    /// is listed), else the highest-named full-size gpt reasoning model, else any reasoning model, else the default.
    /// </summary>
    internal static string PickModel(IReadOnlyList<string> models, string fallback)
    {
        if (models.Count == 0 || models.Contains(fallback)) return fallback;
        static bool Small(string m) => m.Contains("-mini", StringComparison.Ordinal) || m.Contains("-nano", StringComparison.Ordinal);
        var reasoning = models.Where(IsReasoningModel).OrderByDescending(m => m, StringComparer.Ordinal).ToList();
        return reasoning.FirstOrDefault(m => m.StartsWith("gpt-", StringComparison.Ordinal) && !Small(m))
            ?? reasoning.FirstOrDefault()
            ?? fallback;
    }

    public async Task<ProviderTurn> RunTurnAsync(ProviderCredential credential, ProviderRequest request, CancellationToken ct)
    {
        var body = BuildRequestBody(request, ReasoningEffort);
        using var http = NewRequest(HttpMethod.Post, "v1/responses", credential);
        http.Content = ProviderHttp.JsonBody(body);

        var raw = await ProviderHttp.SendAsync(_http, http, TurnTimeout, Provider, ct).ConfigureAwait(false);
        if (!raw.Status.IsSuccess()) throw ProviderHttp.MapError(raw, Provider, credential.ApiKey);
        var root = ProviderHttp.ParseObject(raw.Body, Provider);
        try
        {
            return ParseResponse(root, credential.ApiKey);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            throw new ProviderException("incomplete", "The answer from OpenAI had an unexpected shape.", retryable: false);
        }
    }

    HttpRequestMessage NewRequest(HttpMethod method, string path, ProviderCredential credential)
    {
        var request = new HttpRequestMessage(method, new Uri(_base, path));
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + credential.ApiKey);
        return request;
    }

    // ---- request ------------------------------------------------------------------------------------------

    internal static JsonObject BuildRequestBody(ProviderRequest request, string? effort)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["store"] = false,
            ["max_output_tokens"] = request.MaxOutputTokens,
        };
        if (!string.IsNullOrEmpty(request.System)) body["instructions"] = request.System;
        body["input"] = BuildInput(request.Messages);
        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var t in request.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = t.InputSchema.DeepClone(),
                    // Strict only when the schema already meets the rules; rewriting it (nullable optionals) would
                    // change what the gateway's validator accepts.
                    ["strict"] = StrictSchema.IsCompatible(t.InputSchema, allRequired: true),
                });
            }
            body["tools"] = tools;
            body["tool_choice"] = "auto";
            body["parallel_tool_calls"] = true;
        }
        if (IsReasoningModel(request.Model))
        {
            // Stateless: reasoning items come back with encrypted_content for replay. "include" is the legacy
            // opt-in, still accepted, and needed by older reasoning models.
            body["include"] = new JsonArray("reasoning.encrypted_content");
            if (effort is not null) body["reasoning"] = new JsonObject { ["effort"] = effort };
        }
        return body;
    }

    internal static JsonArray BuildInput(IReadOnlyList<ProviderMessage> messages)
    {
        var input = new JsonArray();
        foreach (var message in messages)
        {
            switch (message)
            {
                case UserText u:
                    input.Add(new JsonObject
                    {
                        ["type"] = "message",
                        ["role"] = "user",
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = u.Text }),
                    });
                    break;
                case AssistantTurn a:
                    foreach (var item in AssistantItems(a)) input.Add(item);
                    break;
                case ToolResults tr:
                    foreach (var r in tr.Results)
                    {
                        input.Add(new JsonObject
                        {
                            ["type"] = "function_call_output",
                            ["call_id"] = r.CallId,
                            ["output"] = OutputString(r),
                        });
                    }
                    break;
                default:
                    throw new ArgumentException($"Unsupported message type {message.GetType().Name}.", nameof(messages));
            }
        }
        return input;
    }

    /// <summary>The Responses API has no is_error flag; a plain-string error is wrapped so the model can tell.</summary>
    static string OutputString(ToolResult r)
    {
        if (r.IsError && r.Content is JsonValue v && v.TryGetValue(out string? s))
            return new JsonObject { ["error"] = new JsonObject { ["message"] = s } }.ToJsonString();
        return ProviderHttp.ContentToString(r.Content);
    }

    static IEnumerable<JsonNode> AssistantItems(AssistantTurn turn)
    {
        if (turn.ProviderState is JsonObject state && state["kind"].AsString() == KindId && state["output"] is JsonArray output)
        {
            foreach (var item in output) if (item is not null) yield return item.DeepClone();
            yield break;
        }
        if (!string.IsNullOrEmpty(turn.Text))
        {
            yield return new JsonObject
            {
                ["type"] = "message",
                ["role"] = "assistant",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = turn.Text }),
            };
        }
        foreach (var call in turn.Calls)
        {
            yield return new JsonObject
            {
                ["type"] = "function_call",
                ["call_id"] = call.CallId,
                ["name"] = call.Name,
                ["arguments"] = call.Arguments.ToJsonString(),
            };
        }
    }

    // ---- response -----------------------------------------------------------------------------------------

    internal static ProviderTurn ParseResponse(JsonObject root, string apiKey = "")
    {
        var status = root["status"].AsString();
        if (status == "failed") throw FailedResponse(root, apiKey);
        if (status is not ("completed" or "incomplete") || root["output"] is not JsonArray output)
            throw new ProviderException("incomplete", "The answer from OpenAI was incomplete.", retryable: false);

        var completed = status == "completed";
        var texts = new List<string>();
        var calls = new List<ToolCall>();
        var kept = new JsonArray();
        var refused = false;

        foreach (var item in output)
        {
            if (item is not JsonObject obj) continue;
            switch (obj["type"].AsString())
            {
                case "message":
                    foreach (var part in obj["content"] as JsonArray ?? [])
                    {
                        switch (part?["type"].AsString())
                        {
                            case "output_text" when part["text"].AsString() is { Length: > 0 } t:
                                texts.Add(t);
                                break;
                            case "refusal":
                                refused = true;
                                if (part["refusal"].AsString() is { Length: > 0 } r) texts.Add(r);
                                break;
                        }
                    }
                    kept.Add(obj.DeepClone());
                    break;
                case "function_call":
                    // A call is only usable from a completed response; a dangling call would also make the next
                    // request fail (no matching function_call_output), so it is not replayed.
                    if (!completed) continue;
                    var callId = obj["call_id"].AsString();
                    var name = obj["name"].AsString();
                    if (string.IsNullOrEmpty(callId) || string.IsNullOrEmpty(name))
                        throw new ProviderException("incomplete", "OpenAI returned a tool call without a call_id or name.", retryable: false);
                    calls.Add(new ToolCall(callId, name, ParseArguments(obj["arguments"].AsString())));
                    kept.Add(obj.DeepClone());
                    break;
                default:
                    // reasoning and any other item types are replayed verbatim.
                    kept.Add(obj.DeepClone());
                    break;
            }
        }

        string stop;
        if (refused) stop = "refusal";
        else if (completed) stop = calls.Count > 0 ? "tool_use" : "end";
        else stop = root["incomplete_details"]?["reason"].AsString() switch
        {
            "max_output_tokens" => "max_tokens",
            "content_filter" => "refusal",
            _ => "other",
        };
        if (stop != "tool_use" && calls.Count > 0)
        {
            calls.Clear();
            for (var i = kept.Count - 1; i >= 0; i--)
                if (kept[i]?["type"].AsString() == "function_call") kept.RemoveAt(i);
        }

        var state = new JsonObject { ["kind"] = KindId, ["output"] = kept };
        if (root["model"] is JsonValue model) state["model"] = model.DeepClone();
        var turn = new AssistantTurn(texts.Count == 0 ? null : string.Join("\n\n", texts), calls, state);
        var usage = root["usage"] as JsonObject;
        return new ProviderTurn(turn, stop, new ProviderUsage(Int(usage?["input_tokens"]), Int(usage?["output_tokens"])));
    }

    /// <summary>Arguments arrive as a JSON string. Unparseable input is passed on as a marker object so the gateway's
    /// schema check rejects it with invalid_arguments and the model can retry, instead of failing the turn.</summary>
    internal static JsonObject ParseArguments(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        try
        {
            if (JsonNode.Parse(raw) is JsonObject obj) return obj;
        }
        catch (JsonException) { }
        return new JsonObject { ["__invalid_json"] = raw.Length > 2000 ? raw[..2000] : raw };
    }

    static ProviderException FailedResponse(JsonObject root, string apiKey)
    {
        var code = root["error"]?["code"].AsString();
        var detail = ProviderHttp.Scrub(root["error"]?["message"].AsString(), apiKey);
        return code switch
        {
            "rate_limit_exceeded" => new ProviderException("rate_limited", "OpenAI rate limit reached.", retryable: true),
            "server_error" or "vector_store_timeout" => new ProviderException("overloaded", "OpenAI failed to finish the answer.", retryable: true),
            "insufficient_quota" => new ProviderException("quota", "OpenAI reports the account is out of credit or quota.", retryable: false),
            _ => new ProviderException("bad_request", $"OpenAI could not answer ({code ?? "failed"}).{(detail is null ? "" : " " + detail)}", retryable: false),
        };
    }

    static int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue(out long l) ? (int)Math.Min(l, int.MaxValue) : 0;

    // ---- model capabilities -------------------------------------------------------------------------------

    /// <summary>Reasoning models take "reasoning" and return reasoning items: gpt-5+, o-series.</summary>
    internal static bool IsReasoningModel(string model) =>
        model.StartsWith("gpt-5", StringComparison.Ordinal) ||
        model.StartsWith("gpt-6", StringComparison.Ordinal) ||
        model.StartsWith("gpt-7", StringComparison.Ordinal) ||
        (model.Length > 1 && model[0] == 'o' && char.IsDigit(model[1]));

    static readonly string[] NonChat = ["audio", "realtime", "image", "transcribe", "tts", "embedding", "moderation", "search", "dall-e", "whisper", "instruct"];

    static bool IsChatModel(string id) =>
        (id.StartsWith("gpt-", StringComparison.Ordinal) || (id.Length > 1 && id[0] == 'o' && char.IsDigit(id[1]))) &&
        !NonChat.Any(n => id.Contains(n, StringComparison.Ordinal));
}

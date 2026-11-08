using System.Text.Json.Nodes;
using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Providers;

/// <summary>
/// Anthropic Messages API (POST /v1/messages), raw HTTP, one non-streamed turn per call.
/// The assistant's content blocks are kept verbatim in <see cref="AssistantTurn.ProviderState"/> and replayed unchanged,
/// which keeps the transcript append-only (thinking blocks are bound to the exact prior conversation).
/// </summary>
public sealed class AnthropicAdapter : IProviderAdapter
{
    public const string KindId = "anthropic_api";
    public const string ApiVersion = "2026-10-21";
    public const string FallbackBeta = "server-side-fallback-2026-07-01";
    const string Provider = "Anthropic";

    readonly HttpClient _http;
    readonly Uri _base;

    public AnthropicAdapter(HttpClient http, Uri? baseUri = null)
    {
        _http = http;
        _base = baseUri ?? new Uri("https://api.anthropic.com/");
    }

    public string Kind => KindId;
    public string DisplayName => "Claude (API)";
    public string DefaultModel => "claude-opus-5-5";

    /// <summary>output_config.effort for models that take it. Opus 5.5 defaults to medium; set explicitly.</summary>
    public string Effort { get; init; } = "medium";

    /// <summary>Opt in to server-side refusal fallbacks (fallbacks:"default") on models that support them.</summary>
    public bool UseRefusalFallbacks { get; init; } = true;

    public TimeSpan TurnTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan CheckTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public async Task<ProviderCheck> CheckAsync(ProviderCredential credential, CancellationToken ct)
    {
        try
        {
            using var request = NewRequest(HttpMethod.Get, "v1/models?limit=1000", credential);
            var raw = await ProviderHttp.SendAsync(_http, request, CheckTimeout, Provider, ct).ConfigureAwait(false);
            if (!raw.Status.IsSuccess()) throw ProviderHttp.MapError(raw, Provider, credential.ApiKey);
            var root = ProviderHttp.ParseObject(raw.Body, Provider);
            var models = (root["data"] as JsonArray ?? [])
                .Select(m => m?["id"].AsString())
                .OfType<string>()
                .ToList();
            return new ProviderCheck(true, null, models, null, Model: PickModel(models, DefaultModel));
        }
        catch (ProviderException e)
        {
            return new ProviderCheck(false, null, [], $"{e.Code}: {e.Message}", e.Code);
        }
        catch (InvalidOperationException)
        {
            return new ProviderCheck(false, null, [], "incomplete: The model list from Anthropic had an unexpected shape.", "incomplete");
        }
    }

    /// <summary>
    /// The model to use for an account that lists <paramref name="models"/> (newest first, as Anthropic lists them):
    /// the default when listed (or when nothing is listed), else the first Opus, else the first Sonnet, else the first
    /// other current Claude model, else the default.
    /// </summary>
    internal static string PickModel(IReadOnlyList<string> models, string fallback)
    {
        if (models.Count == 0 || models.Contains(fallback)) return fallback;
        static bool Current(string m) => m.StartsWith("claude-", StringComparison.Ordinal) &&
            !m.StartsWith("claude-2", StringComparison.Ordinal) && !m.StartsWith("claude-instant", StringComparison.Ordinal);
        return models.FirstOrDefault(m => m.StartsWith("claude-opus-", StringComparison.Ordinal))
            ?? models.FirstOrDefault(m => m.StartsWith("claude-sonnet-", StringComparison.Ordinal))
            ?? models.FirstOrDefault(Current)
            ?? fallback;
    }

    public async Task<ProviderTurn> RunTurnAsync(ProviderCredential credential, ProviderRequest request, CancellationToken ct)
    {
        var body = BuildRequestBody(request, UseRefusalFallbacks && SupportsFallbacks(request.Model), Effort);
        using var http = NewRequest(HttpMethod.Post, "v1/messages", credential);
        if (body.ContainsKey("fallbacks")) http.Headers.Add("anthropic-beta", FallbackBeta);
        http.Content = ProviderHttp.JsonBody(body);

        var raw = await ProviderHttp.SendAsync(_http, http, TurnTimeout, Provider, ct).ConfigureAwait(false);
        if (!raw.Status.IsSuccess()) throw ProviderHttp.MapError(raw, Provider, credential.ApiKey);
        var root = ProviderHttp.ParseObject(raw.Body, Provider);
        try
        {
            return ParseResponse(root);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            throw new ProviderException("incomplete", "The answer from Anthropic had an unexpected shape.", retryable: false);
        }
    }

    HttpRequestMessage NewRequest(HttpMethod method, string path, ProviderCredential credential)
    {
        var request = new HttpRequestMessage(method, new Uri(_base, path));
        request.Headers.TryAddWithoutValidation("x-api-key", credential.ApiKey);
        request.Headers.Add("anthropic-version", ApiVersion);
        return request;
    }

    // ---- request ------------------------------------------------------------------------------------------

    internal static JsonObject BuildRequestBody(ProviderRequest request, bool fallbacks, string? effort)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["max_tokens"] = request.MaxOutputTokens,
        };
        if (!string.IsNullOrEmpty(request.System)) body["system"] = request.System;
        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var t in request.Tools)
            {
                var tool = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["input_schema"] = t.InputSchema.DeepClone(),
                };
                if (StrictSchema.IsCompatible(t.InputSchema, allRequired: false)) tool["strict"] = true;
                tools.Add(tool);
            }
            body["tools"] = tools;
        }
        body["messages"] = BuildMessages(request.Messages);
        // Automatic prompt caching: tools -> system -> messages is a stable, append-only prefix across the loop.
        body["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
        if (effort is not null && SupportsEffort(request.Model)) body["output_config"] = new JsonObject { ["effort"] = effort };
        if (fallbacks) body["fallbacks"] = "default";
        return body;
    }

    internal static JsonArray BuildMessages(IReadOnlyList<ProviderMessage> messages)
    {
        var result = new JsonArray();
        foreach (var message in messages)
        {
            switch (message)
            {
                case UserText u:
                    AppendUser(result, [new JsonObject { ["type"] = "text", ["text"] = u.Text }], prepend: false);
                    break;
                case ToolResults tr:
                    var blocks = tr.Results.Select(r => (JsonNode)new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = r.CallId,
                        ["content"] = ProviderHttp.ContentToString(r.Content),
                        ["is_error"] = r.IsError,
                    }).ToList();
                    AppendUser(result, blocks, prepend: true);
                    break;
                case AssistantTurn a:
                    result.Add(new JsonObject { ["role"] = "assistant", ["content"] = AssistantContent(a) });
                    break;
                default:
                    throw new ArgumentException($"Unsupported message type {message.GetType().Name}.", nameof(messages));
            }
        }
        return result;
    }

    /// <summary>
    /// Adds blocks to a user message. Consecutive user entries merge into one message; tool_result blocks always
    /// go before any text in that message (the API requires them at the start of the following user turn).
    /// </summary>
    static void AppendUser(JsonArray result, List<JsonNode> blocks, bool prepend)
    {
        if (result.Count > 0 && result[^1] is JsonObject last && last["role"].AsString() == "user" && last["content"] is JsonArray content)
        {
            if (prepend)
            {
                var firstText = content.TakeWhile(b => b?["type"].AsString() == "tool_result").Count();
                for (var i = 0; i < blocks.Count; i++) content.Insert(firstText + i, blocks[i]);
            }
            else foreach (var b in blocks) content.Add(b);
            return;
        }
        var arr = new JsonArray();
        foreach (var b in blocks) arr.Add(b);
        result.Add(new JsonObject { ["role"] = "user", ["content"] = arr });
    }

    static JsonArray AssistantContent(AssistantTurn turn)
    {
        if (turn.ProviderState is JsonObject state && state["kind"].AsString() == KindId && state["content"] is JsonArray original)
            return (JsonArray)original.DeepClone();

        // No Anthropic state (e.g. a turn recorded by another adapter): rebuild from the neutral fields.
        var content = new JsonArray();
        if (!string.IsNullOrEmpty(turn.Text)) content.Add(new JsonObject { ["type"] = "text", ["text"] = turn.Text });
        foreach (var call in turn.Calls)
            content.Add(new JsonObject { ["type"] = "tool_use", ["id"] = call.CallId, ["name"] = call.Name, ["input"] = call.Arguments.DeepClone() });
        return content;
    }

    // ---- response -----------------------------------------------------------------------------------------

    internal static ProviderTurn ParseResponse(JsonObject root)
    {
        if (root["content"] is not JsonArray content || root["stop_reason"] is not JsonValue sr || !sr.TryGetValue(out string? stopRaw))
            throw new ProviderException("incomplete", "The answer from Anthropic was incomplete.", retryable: false);

        var stop = stopRaw switch
        {
            "end_turn" or "stop_sequence" => "end",
            "tool_use" => "tool_use",
            "max_tokens" => "max_tokens",
            "refusal" => "refusal",
            _ => "other",
        };

        var kept = SanitizeForReplay(content, keepToolUse: stop == "tool_use");
        var texts = new List<string>();
        var calls = new List<ToolCall>();
        foreach (var block in kept)
        {
            switch (block?["type"].AsString())
            {
                case "text":
                    if (block["text"].AsString() is { Length: > 0 } t) texts.Add(t);
                    break;
                case "tool_use":
                    var id = block["id"].AsString();
                    var name = block["name"].AsString();
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
                        throw new ProviderException("incomplete", "Anthropic returned a tool call without an ID or name.", retryable: false);
                    var args = block["input"] as JsonObject
                        ?? new JsonObject { ["__invalid_json"] = block["input"]?.ToJsonString() ?? "null" };
                    calls.Add(new ToolCall(id, name, (JsonObject)args.DeepClone()));
                    break;
            }
        }
        if (stop == "tool_use" && calls.Count == 0) stop = "other";

        var state = new JsonObject { ["kind"] = KindId, ["content"] = kept };
        if (root["model"] is JsonValue model) state["model"] = model.DeepClone();
        var turn = new AssistantTurn(texts.Count == 0 ? null : string.Join("\n\n", texts), calls, state);
        return new ProviderTurn(turn, stop, ReadUsage(root["usage"] as JsonObject));
    }

    /// <summary>
    /// Returns the blocks to replay next turn. After a mid-output server-side fallback, model-internal blocks
    /// (thinking, redacted_thinking, tool_use, unpaired server_tool_use) before the last fallback marker are dropped
    /// and the marker itself is omitted. tool_use blocks are dropped when the turn did not stop for tool use,
    /// since no tool_result will follow them.
    /// </summary>
    internal static JsonArray SanitizeForReplay(JsonArray content, bool keepToolUse)
    {
        var lastFallback = -1;
        for (var i = 0; i < content.Count; i++)
            if (content[i]?["type"].AsString() == "fallback") lastFallback = i;

        var pairedServerTools = content
            .Select(b => b?["tool_use_id"].AsString())
            .OfType<string>()
            .ToHashSet();

        var kept = new JsonArray();
        for (var i = 0; i < content.Count; i++)
        {
            var block = content[i];
            var type = block?["type"].AsString();
            if (block is null || type == "fallback") continue;
            if (i < lastFallback)
            {
                if (type is "thinking" or "redacted_thinking" or "tool_use") continue;
                if (type == "server_tool_use" && !pairedServerTools.Contains(block["id"].AsString() ?? "")) continue;
                if (type is not ("text" or "server_tool_use") && !(type ?? "").EndsWith("_tool_result", StringComparison.Ordinal)) continue;
            }
            if (type == "tool_use" && !keepToolUse) continue;
            kept.Add(block.DeepClone());
        }
        return kept;
    }

    static ProviderUsage ReadUsage(JsonObject? usage)
    {
        if (usage is null) return new ProviderUsage(0, 0);
        // With fallbacks, usage.iterations is per attempt and covers every attempt; top-level covers only the last.
        if (usage["iterations"] is JsonArray { Count: > 0 } iterations)
        {
            int inSum = 0, outSum = 0;
            foreach (var it in iterations.OfType<JsonObject>())
            {
                inSum += InputTokens(it);
                outSum += Int(it["output_tokens"]);
            }
            return new ProviderUsage(inSum, outSum);
        }
        return new ProviderUsage(InputTokens(usage), Int(usage["output_tokens"]));
    }

    static int InputTokens(JsonObject u) =>
        Int(u["input_tokens"]) + Int(u["cache_creation_input_tokens"]) + Int(u["cache_read_input_tokens"]);

    static int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue(out long l) ? (int)Math.Min(l, int.MaxValue) : 0;

    // ---- model capabilities -------------------------------------------------------------------------------

    /// <summary>output_config.effort: Opus 4.5+, Sonnet 4.6+, all 5.x families. Not Sonnet 4.5 / Haiku 4.5.</summary>
    internal static bool SupportsEffort(string model) =>
        !(model.StartsWith("claude-haiku-4", StringComparison.Ordinal) ||
          model.StartsWith("claude-sonnet-4-5", StringComparison.Ordinal) ||
          model.StartsWith("claude-sonnet-4-0", StringComparison.Ordinal) ||
          model.StartsWith("claude-opus-4-0", StringComparison.Ordinal) ||
          model.StartsWith("claude-opus-4-1", StringComparison.Ordinal) ||
          model.StartsWith("claude-3", StringComparison.Ordinal) ||
          model is "claude-sonnet-4" or "claude-opus-4");

    /// <summary>Server-side fallbacks:"default" on the Claude API: Opus 5.5, Opus 5, Fable 5.x, Sonnet 5.5. Never Haiku.</summary>
    internal static bool SupportsFallbacks(string model) =>
        model.StartsWith("claude-opus-5", StringComparison.Ordinal) ||
        model.StartsWith("claude-fable-5", StringComparison.Ordinal) ||
        model.StartsWith("claude-sonnet-5-5", StringComparison.Ordinal);
}

internal static class HttpStatusExtensions
{
    public static bool IsSuccess(this System.Net.HttpStatusCode status) => (int)status is >= 200 and < 300;
}

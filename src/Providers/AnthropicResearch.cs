using System.Text;
using System.Text.Json.Nodes;
using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Providers;

// m9 §6.3: one research call on the Anthropic Messages API. The request declares exactly one tool, the server web search
// tool with max_uses; no function tools, no fetch tool, no code execution. Pinned from the Claude API docs, "Web search
// tool" (platform.claude.com/docs/en/agents-and-tools/tool-use/web-search-tool, read 2026-11-10):
//   tool      {"type":"web_search_20250305","name":"web_search","max_uses":N}  (basic search: called directly, no dynamic
//             filtering. web_search_20260209 and later run search from code execution, which provisions a second tool
//             environment; research must declare search only, so the basic version is pinned.)
//   results   content block {"type":"web_search_tool_result","tool_use_id","content":[{"type":"web_search_result","url","title",
//             "encrypted_content","page_age"}]}; an error is {"content":{"type":"web_search_tool_result_error","error_code"}}
//   citations text blocks' "citations":[{"type":"web_search_result_location","url","title","encrypted_index","cited_text"}]
//   usage     usage.server_tool_use.web_search_requests
public sealed partial class AnthropicAdapter
{
    public const string WebSearchToolType = "web_search_20250305";
    public const string WebSearchToolName = "web_search";

    /// <summary>Thinking depth for research calls (models that take output_config.effort).</summary>
    public string ResearchEffort { get; init; } = "low";

    public async Task<ResearchAnswer> ResearchAsync(ProviderCredential credential, ResearchCall call, CancellationToken ct)
    {
        var body = BuildResearchBody(call, ResearchEffort);
        using var http = NewRequest(HttpMethod.Post, "v1/messages", credential);
        http.Content = ProviderHttp.JsonBody(body);
        ProviderHttp.RawResponse raw;
        raw = await ProviderHttp.SendAsync(_http, http, call.Timeout, Provider, ct, call.Model).ConfigureAwait(false);
        if (!raw.Status.IsSuccess())
        {
            var error = ProviderHttp.MapError(raw, Provider, credential.ApiKey);
            // The request shape is fixed; a 400 means the account or model refuses part of it (the web search tool).
            if (error.Code == "bad_request") throw new WebResearchUnsupportedException("tool_rejected");
            throw error;
        }
        var root = ProviderHttp.ParseObject(raw.Body, Provider);
        return ParseResearch(root);
    }

    /// <summary>The research request: the fixed system prompt, the one subject, and the web search tool only.</summary>
    internal static JsonObject BuildResearchBody(ResearchCall call, string? effort)
    {
        var body = new JsonObject
        {
            ["model"] = call.Model,
            ["max_tokens"] = call.MaxOutputTokens,
            ["system"] = call.System,
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = call.Subject }),
            }),
            ["tools"] = new JsonArray(new JsonObject
            {
                ["type"] = WebSearchToolType, ["name"] = WebSearchToolName, ["max_uses"] = call.MaxSearches,
            }),
        };
        if (effort is not null && SupportsEffort(call.Model)) body["output_config"] = new JsonObject { ["effort"] = effort };
        return body;
    }

    /// <summary>
    /// The answer text from text blocks only (joined as written: citations split one answer into several blocks), the
    /// sources the provider returned (web_search_result entries and web_search_result_location citations) and usage.
    /// Search-result content never becomes answer text.
    /// </summary>
    internal static ResearchAnswer ParseResearch(JsonObject root)
    {
        if (root["content"] is not JsonArray content)
            throw new ProviderException("incomplete", "The answer from Anthropic was incomplete.", retryable: false);
        var text = new StringBuilder();
        var observed = new List<ObservedSource>();
        var searchCalls = 0;
        foreach (var block in content.OfType<JsonObject>())
        {
            switch (block["type"].AsString())
            {
                case "text":
                    text.Append(block["text"].AsString());
                    foreach (var c in (block["citations"] as JsonArray ?? []).OfType<JsonObject>())
                        if (c["type"].AsString() == "web_search_result_location" && c["url"].AsString() is { } cu) observed.Add(new ObservedSource(cu, c["title"].AsString()));
                    break;
                case "server_tool_use":
                    if (block["name"].AsString() == WebSearchToolName) searchCalls++;
                    break;
                case "web_search_tool_result":
                    if (block["content"] is JsonArray results)
                        foreach (var r in results.OfType<JsonObject>())
                            if (r["type"].AsString() == "web_search_result" && r["url"].AsString() is { } u) observed.Add(new ObservedSource(u, r["title"].AsString()));
                    break;
            }
        }
        var usage = root["usage"] as JsonObject;
        var searches = usage?["server_tool_use"]?["web_search_requests"] is JsonValue sv && sv.TryGetValue(out long s) ? (int)Math.Min(s, int.MaxValue) : searchCalls;
        var refused = root["stop_reason"].AsString() == "refusal";
        return new ResearchAnswer(refused || text.Length == 0 ? null : text.ToString(), observed, searches, ReadUsage(usage));
    }
}

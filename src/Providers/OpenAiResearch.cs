using System.Text;
using System.Text.Json.Nodes;
using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Providers;

// m9 §6.3: one research call on the OpenAI Responses API (also used, through its own transport, by the ChatGPT plan route).
// The request declares exactly one tool, {"type":"web_search"}; no function tools. Pinned from the OpenAI docs
// (developers.openai.com/api/docs/guides/tools-web-search and the Responses "create" reference, read 2026-11-10):
//   tool      {"type":"web_search"} ("For new Responses API integrations, use {"type":"web_search"}"; web_search_preview is legacy)
//   include   ["web_search_call.action.sources"]: the complete list of URLs the search consulted
//   output    {"type":"web_search_call","id","status","action":{"type":"search","query"?,"queries"?,"sources"?:[{"type":"url","url"}]}}
//             or action {"type":"open_page","url"} / {"type":"find_in_page","url","pattern"}
//   citations message output_text "annotations":[{"type":"url_citation","start_index","end_index","url","title"}]
//   cap       max_tool_calls (Responses request field; caps built-in tool calls). The plan route rejects it, so there the
//             prompt's "at most 3 searches" is the only cap.
public sealed partial class OpenAiAdapter
{
    public const string WebSearchToolType = "web_search";
    public const string SourcesInclude = "web_search_call.action.sources";

    /// <summary>reasoning.effort for research calls on reasoning models.</summary>
    public string ResearchEffort { get; init; } = "low";

    public async Task<ResearchAnswer> ResearchAsync(ProviderCredential credential, ResearchCall call, CancellationToken ct)
    {
        var body = BuildResearchBody(call, ResearchEffort, plan: false);
        using var http = NewRequest(HttpMethod.Post, "v1/responses", credential);
        http.Content = ProviderHttp.JsonBody(body);
        var raw = await ProviderHttp.SendAsync(_http, http, call.Timeout, Provider, ct, call.Model).ConfigureAwait(false);
        if (!raw.Status.IsSuccess())
        {
            var error = ProviderHttp.MapError(raw, Provider, credential.ApiKey);
            if (error.Code == "bad_request") throw new WebResearchUnsupportedException("tool_rejected");
            throw error;
        }
        var root = ProviderHttp.ParseObject(raw.Body, Provider);
        if (root["status"].AsString() == "failed") throw FailedResponse(root, credential.ApiKey);
        return ParseResearch(root);
    }

    /// <summary>
    /// The research request: instructions (research-v1), the one subject, and the web_search tool only. The plan route
    /// streams, and takes neither max_output_tokens nor max_tool_calls.
    /// </summary>
    internal static JsonObject BuildResearchBody(ResearchCall call, string? effort, bool plan)
    {
        var body = new JsonObject
        {
            ["model"] = call.Model,
            ["store"] = false,
            ["instructions"] = call.System,
            ["input"] = new JsonArray(new JsonObject
            {
                ["type"] = "message", ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = call.Subject }),
            }),
            ["tools"] = new JsonArray(new JsonObject { ["type"] = WebSearchToolType }),
            ["tool_choice"] = "auto",
            ["include"] = new JsonArray(SourcesInclude),
        };
        if (plan) body["stream"] = true;
        else
        {
            body["max_output_tokens"] = call.MaxOutputTokens;
            body["max_tool_calls"] = call.MaxSearches;
        }
        if (effort is not null && IsReasoningModel(call.Model)) body["reasoning"] = new JsonObject { ["effort"] = effort };
        return body;
    }

    /// <summary>
    /// Answer text from output_text parts only; observed sources from web_search_call actions (search sources, opened pages)
    /// of completed web_search_call items only) and url_citation annotations. Searches that ran but exposed no result at all (no sources, no citations) mean the
    /// provider gives no verifiable sources: unsupported, never unverified labels.
    /// </summary>
    internal static ResearchAnswer ParseResearch(JsonObject root)
    {
        if (root["output"] is not JsonArray output)
            throw new ProviderException("incomplete", "The answer from OpenAI was incomplete.", retryable: false);
        var text = new StringBuilder();
        var observed = new List<ObservedSource>();
        int searches = 0, exposing = 0;
        var refused = false;
        foreach (var item in output.OfType<JsonObject>())
        {
            switch (item["type"].AsString())
            {
                case "web_search_call":
                    // Only a completed tool item establishes anything: a failed or unfinished search or page open is not a
                    // source the model observed, whatever its answer cites.
                    if (item["status"].AsString() != "completed") break;
                    var action = item["action"] as JsonObject;
                    var kind = action?["type"].AsString();
                    if (kind is null or "search") searches++;
                    if (action?["sources"] is JsonArray sources)
                    {
                        exposing++;
                        foreach (var s in sources.OfType<JsonObject>())
                            if (s["url"].AsString() is { } u) observed.Add(new ObservedSource(u, s["title"].AsString()));
                    }
                    if (kind is "open_page" or "find_in_page" && action?["url"].AsString() is { } page) { exposing++; observed.Add(new ObservedSource(page, null)); }
                    break;
                case "message":
                    foreach (var part in (item["content"] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        if (part["type"].AsString() == "refusal") refused = true;
                        if (part["type"].AsString() != "output_text") continue;
                        text.Append(part["text"].AsString());
                        foreach (var a in (part["annotations"] as JsonArray ?? []).OfType<JsonObject>())
                            if (a["type"].AsString() == "url_citation" && a["url"].AsString() is { } cu) { exposing++; observed.Add(new ObservedSource(cu, a["title"].AsString())); }
                    }
                    break;
            }
        }
        if (searches > 0 && exposing == 0) throw new WebResearchUnsupportedException("no_results_exposed");
        var usage = root["usage"] as JsonObject;
        return new ResearchAnswer(refused || text.Length == 0 ? null : text.ToString(), observed, searches,
            new ProviderUsage(Int(usage?["input_tokens"]), Int(usage?["output_tokens"])));
    }
}

using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Transport.Logging;

namespace OptimizationTool.Providers;

// m9 §6.3: one research call on the ChatGPT plan route: the same Responses shape as OpenAiAdapter's research request
// (web_search only), through this adapter's own transport and token handling (sign-in is not touched here). The plan
// route streams and rejects max_output_tokens and max_tool_calls; the output budget is enforced locally while draining.
public sealed partial class ChatGptPlanAdapter
{
    public string ResearchEffort { get; init; } = "low";

    public async Task<ResearchAnswer> ResearchAsync(ProviderCredential credential, ResearchCall call, CancellationToken ct)
    {
        var body = OpenAiAdapter.BuildResearchBody(call, ResearchEffort, plan: true);
        foreach (var banned in BannedFields) body.Remove(banned);
        var json = body.ToJsonString();
        var outputCharBudget = (long)Math.Max(1, call.MaxOutputTokens) * CharsPerToken;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(call.Timeout);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
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
                try { return await _http.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false); }
                finally { http.Dispose(); }
            }, ct).ConfigureAwait(false);

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var errBody = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                    var error = MapError(response.StatusCode, errBody, response.Headers);
                    LogStream(call.Model, started, $"research status={(int)response.StatusCode} code={error.Code}", ErrorSites.ProviderHttpStatus);
                    // 400 (e.g. subscription_sharing_unsupported_capability): the plan refuses the web search tool.
                    if (error.Code == "bad_request") throw new WebResearchUnsupportedException("tool_rejected");
                    throw error;
                }
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                JsonObject final;
                try { final = await DrainResponseAsync(stream, outputCharBudget, timeout.Token, call.KillAboveSearches).ConfigureAwait(false); }
                catch (ProviderException e)
                {
                    LogStream(call.Model, started, $"research status={(int)response.StatusCode} failed code={e.Code}", ErrorSites.ProviderStreamError);
                    throw;
                }
                LogStream(call.Model, started, $"research status={(int)response.StatusCode}");
                return OpenAiAdapter.ParseResearch(final);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LogStream(call.Model, started, "research failed reason=timeout", ErrorSites.ProviderTimeout);
            throw new ProviderException("timeout", $"{Provider} did not finish within {call.Timeout.TotalSeconds:0} s.", retryable: false);
        }
        catch (HttpRequestException)
        {
            LogStream(call.Model, started, "research failed reason=network", ErrorSites.ProviderNetwork);
            throw new ProviderException("network", $"Could not reach {Provider}. Check the internet connection.", retryable: false);
        }
        catch (IOException)
        {
            LogStream(call.Model, started, "research failed reason=incomplete", ErrorSites.ProviderIncomplete);
            throw new ProviderException("incomplete", $"The answer from {Provider} was cut off.", retryable: false);
        }
    }

    /// <summary>
    /// Reads the event stream to its terminal event and returns the final response object (its output filled from the
    /// items collected on the way when the terminal event omits them). response.failed and error events map to codes; a
    /// stream without a terminal event, or text past the local budget, is incomplete.
    /// </summary>
    internal async Task<JsonObject> DrainResponseAsync(Stream stream, long outputCharBudget, CancellationToken ct, int killAboveSearches = 0)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        var doneItems = new SortedDictionary<int, JsonObject>();
        long streamed = 0, bytes = 0;
        var searches = 0;
        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is not null)
            {
                bytes += line.Length + 1;
                if (bytes > MaxStreamBytes) throw new ProviderException("incomplete", $"The answer from {Provider} was too large.", retryable: false);
                if (line.StartsWith("data:", StringComparison.Ordinal)) { data.Append(line.AsSpan(5).TrimStart(' ')).Append('\n'); continue; }
                if (line.Length > 0) continue;
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
                    switch (ev?["type"].AsString())
                    {
                        case "response.output_text.delta":
                            streamed += ev["delta"].AsString()?.Length ?? 0;
                            if (streamed > outputCharBudget) throw new ProviderException("incomplete", $"The answer from {Provider} was too long.", retryable: false);
                            break;
                        case "response.output_item.done":
                            if (ev["item"] is JsonObject item)
                            {
                                // m9 §6.8: the plan route rejects max_tool_calls, so searches are counted here and the call
                                // is stopped above its limit (closing the stream ends the request).
                                if (item["type"].AsString() == "web_search_call" && killAboveSearches > 0 && ++searches > killAboveSearches)
                                    throw new ProviderException("search_limit", $"{Provider} searched more than allowed.", retryable: false);
                                doneItems[ev["output_index"] is JsonValue iv && iv.TryGetValue(out int idx) ? idx : doneItems.Count] = (JsonObject)item.DeepClone();
                            }
                            break;
                        case "response.completed":
                        case "response.incomplete":
                            if (ev["response"] is not JsonObject r) throw new ProviderException("incomplete", $"The answer from {Provider} was incomplete.", retryable: false);
                            var final = (JsonObject)r.DeepClone();
                            if (final["output"] is not JsonArray { Count: > 0 } && doneItems.Count > 0)
                                final["output"] = new JsonArray(doneItems.Values.Select(i => (JsonNode)i.DeepClone()).ToArray());
                            return final;
                        case "response.failed":
                            throw MapFailure(ev["response"]?["error"] as JsonObject);
                        case "error":
                            throw MapFailure(ev["error"] as JsonObject ?? ev);
                    }
                }
            }
            if (line is null) throw new ProviderException("incomplete", $"The answer from {Provider} ended early.", retryable: false);
        }
    }
}

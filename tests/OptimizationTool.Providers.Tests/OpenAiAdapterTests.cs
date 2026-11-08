using System.Net;
using System.Text.Json.Nodes;
using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Providers.Tests;

public sealed class OpenAiAdapterTests
{
    static readonly ProviderCredential Key = new("openai_api", TestKeys.OpenAi);

    static readonly ToolSpec RunDiagnostic = new("run_diagnostic", "Run one granted diagnostic.", (JsonObject)JsonNode.Parse("""
        {"type":"object","properties":{"diagnosticId":{"type":"string","enum":["power.inventory","process.sample"]},"window":{"type":["integer","null"]}},
         "required":["diagnosticId","window"],"additionalProperties":false}
        """)!);

    static readonly ToolSpec GetFindings = new("get_findings", "Read findings.", (JsonObject)JsonNode.Parse("""
        {"type":"object","properties":{"category":{"type":"string"}},"additionalProperties":false}
        """)!);

    static (OpenAiAdapter Adapter, FakeHandler Handler) Create()
    {
        var handler = new FakeHandler();
        return (new OpenAiAdapter(new HttpClient(handler)), handler);
    }

    static ProviderRequest Request(IReadOnlyList<ProviderMessage> messages, string model = "gpt-6.1-sol") =>
        new(model, "You are a read-only PC diagnostics assistant.", messages, [RunDiagnostic, GetFindings], 4096);

    const string Turn1 = """
        {"id":"resp_1","object":"response","status":"completed","model":"gpt-6.1-sol","output":[
          {"type":"reasoning","id":"rs_1","summary":[],"encrypted_content":"ENC-REASONING-1"},
          {"type":"function_call","id":"fc_1","call_id":"call_A","name":"run_diagnostic","arguments":"{\"diagnosticId\":\"power.inventory\",\"window\":null}","status":"completed"},
          {"type":"function_call","id":"fc_2","call_id":"call_B","name":"run_diagnostic","arguments":"{\"diagnosticId\":\"process.sample\",\"window\":15}","status":"completed"}],
         "usage":{"input_tokens":120,"output_tokens":60,"total_tokens":180}}
        """;

    const string Turn2 = """
        {"id":"resp_2","object":"response","status":"completed","model":"gpt-6.1-sol","output":[
          {"type":"reasoning","id":"rs_2","summary":[],"encrypted_content":"ENC-REASONING-2"},
          {"type":"message","id":"msg_2","role":"assistant","status":"completed","phase":"final_answer","content":[{"type":"output_text","text":"App A uses 31% CPU [ev-1].","annotations":[]}]}],
         "usage":{"input_tokens":300,"output_tokens":30}}
        """;

    const string Turn3 = """
        {"id":"resp_3","status":"completed","output":[
          {"type":"message","id":"msg_3","role":"assistant","content":[{"type":"output_text","text":"You're welcome."}]}],
         "usage":{"input_tokens":400,"output_tokens":4}}
        """;

    [Fact]
    public async Task Three_turn_tool_conversation_round_trips_call_ids_and_reasoning()
    {
        var (adapter, handler) = Create();
        handler.Respond(HttpStatusCode.OK, Turn1).Respond(HttpStatusCode.OK, Turn2).Respond(HttpStatusCode.OK, Turn3);

        var transcript = new List<ProviderMessage> { new UserText("Why is my PC loud?") };
        var t1 = await adapter.RunTurnAsync(Key, Request(transcript), CancellationToken.None);
        Assert.Equal("tool_use", t1.StopReason);
        Assert.Null(t1.Turn.Text);
        Assert.Equal(["call_A", "call_B"], t1.Turn.Calls.Select(c => c.CallId));
        Assert.Equal(15, t1.Turn.Calls[1].Arguments["window"]!.GetValue<int>());
        Assert.Equal(new ProviderUsage(120, 60), t1.Usage);

        transcript.Add(t1.Turn);
        transcript.Add(new ToolResults([
            new ToolResult("call_A", JsonNode.Parse("""{"evidence":[{"id":"ev-1"}]}""")!, false),
            new ToolResult("call_B", JsonValue.Create("denied by grant"), true),
        ]));
        var t2 = await adapter.RunTurnAsync(Key, Request(transcript), CancellationToken.None);
        Assert.Equal("end", t2.StopReason);
        Assert.Equal("App A uses 31% CPU [ev-1].", t2.Turn.Text);

        transcript.Add(t2.Turn);
        transcript.Add(new UserText("Thanks"));
        await adapter.RunTurnAsync(Key, Request(transcript), CancellationToken.None);

        // ---- request 1
        var r1 = handler.Requests[0];
        Assert.Equal("https://api.openai.com/v1/responses", r1.Uri.ToString());
        Assert.Equal("Bearer " + TestKeys.OpenAi, r1.Headers["authorization"]);
        var b1 = r1.Json;
        Assert.False(b1["store"]!.GetValue<bool>());
        Assert.Equal("gpt-6.1-sol", b1["model"]!.GetValue<string>());
        Assert.Equal(4096, b1["max_output_tokens"]!.GetValue<int>());
        Assert.Equal("You are a read-only PC diagnostics assistant.", b1["instructions"]!.GetValue<string>());
        Assert.Equal("medium", b1["reasoning"]!["effort"]!.GetValue<string>());
        Assert.Equal("reasoning.encrypted_content", b1["include"]![0]!.GetValue<string>());
        var tools = b1["tools"]!.AsArray();
        Assert.Equal("function", tools[0]!["type"]!.GetValue<string>());
        Assert.True(tools[0]!["strict"]!.GetValue<bool>());     // all properties required + additionalProperties:false
        Assert.False(tools[1]!["strict"]!.GetValue<bool>());    // optional property: sent non-strict, schema unchanged
        Assert.True(JsonNode.DeepEquals(GetFindings.InputSchema, tools[1]!["parameters"]));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            [{"type":"message","role":"user","content":[{"type":"input_text","text":"Why is my PC loud?"}]}]
            """), b1["input"]));

        // ---- request 2: reasoning + function_call items verbatim, then outputs keyed by call_id
        var in2 = handler.Requests[1].Json["input"]!.AsArray();
        Assert.Equal(6, in2.Count);
        var out1 = JsonNode.Parse(Turn1)!["output"]!.AsArray();
        for (var i = 0; i < 3; i++) Assert.True(JsonNode.DeepEquals(out1[i], in2[i + 1]));
        Assert.Equal("ENC-REASONING-1", in2[1]!["encrypted_content"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            {"type":"function_call_output","call_id":"call_A","output":"{\"evidence\":[{\"id\":\"ev-1\"}]}"}
            """), in2[4]));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            {"type":"function_call_output","call_id":"call_B","output":"{\"error\":{\"message\":\"denied by grant\"}}"}
            """), in2[5]));

        // ---- request 3: append-only, second turn's reasoning and message (with phase) replayed
        var in3 = handler.Requests[2].Json["input"]!.AsArray();
        Assert.Equal(9, in3.Count);
        for (var i = 0; i < in2.Count; i++) Assert.True(JsonNode.DeepEquals(in2[i], in3[i]));
        var out2 = JsonNode.Parse(Turn2)!["output"]!.AsArray();
        Assert.True(JsonNode.DeepEquals(out2[0], in3[6]));
        Assert.True(JsonNode.DeepEquals(out2[1], in3[7]));
        Assert.Equal("Thanks", in3[8]!["content"]![0]!["text"]!.GetValue<string>());

        Assert.All(handler.Requests, r => Assert.DoesNotContain(TestKeys.OpenAi, r.Body!));
    }

    [Fact]
    public void Incomplete_response_drops_partial_calls_and_maps_max_tokens()
    {
        var root = (JsonObject)JsonNode.Parse("""
            {"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"output":[
              {"type":"reasoning","id":"rs_1","summary":[],"encrypted_content":"E"},
              {"type":"function_call","call_id":"call_X","name":"get_findings","arguments":"{\"categ"}],
             "usage":{"input_tokens":5,"output_tokens":4096}}
            """)!;
        var turn = OpenAiAdapter.ParseResponse(root);
        Assert.Equal("max_tokens", turn.StopReason);
        Assert.Empty(turn.Turn.Calls);
        var kept = turn.Turn.ProviderState!["output"]!.AsArray();
        Assert.Single(kept);
        Assert.Equal("reasoning", kept[0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Refusal_and_content_filter_map_to_refusal()
    {
        var refusal = OpenAiAdapter.ParseResponse((JsonObject)JsonNode.Parse("""
            {"status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"refusal","refusal":"I can't help with that."}]}]}
            """)!);
        Assert.Equal("refusal", refusal.StopReason);
        Assert.Equal("I can't help with that.", refusal.Turn.Text);

        var filtered = OpenAiAdapter.ParseResponse((JsonObject)JsonNode.Parse("""
            {"status":"incomplete","incomplete_details":{"reason":"content_filter"},"output":[]}
            """)!);
        Assert.Equal("refusal", filtered.StopReason);
    }

    [Fact]
    public void Invalid_argument_json_becomes_a_marker_for_the_gateway_validator()
    {
        var turn = OpenAiAdapter.ParseResponse((JsonObject)JsonNode.Parse("""
            {"status":"completed","output":[{"type":"function_call","call_id":"call_Z","name":"get_findings","arguments":"{not json"}]}
            """)!);
        Assert.Equal("tool_use", turn.StopReason);
        Assert.Equal("{not json", turn.Turn.Calls[0].Arguments["__invalid_json"]!.GetValue<string>());
    }

    [Fact]
    public void Non_reasoning_models_get_no_reasoning_fields()
    {
        var body = OpenAiAdapter.BuildRequestBody(Request([new UserText("x")], "gpt-4.1"), "medium");
        Assert.False(body.ContainsKey("reasoning"));
        Assert.False(body.ContainsKey("include"));
        Assert.True(OpenAiAdapter.IsReasoningModel("o4-mini"));
    }

    [Fact]
    public void Turn_from_another_provider_is_rebuilt_as_neutral_items()
    {
        var input = OpenAiAdapter.BuildInput([
            new UserText("hi"),
            new AssistantTurn("Checking.", [new ToolCall("toolu_1", "get_findings", new JsonObject { ["category"] = "power" })],
                new JsonObject { ["kind"] = "anthropic_api", ["content"] = new JsonArray() }),
        ]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            [{"type":"message","role":"user","content":[{"type":"input_text","text":"hi"}]},
             {"type":"message","role":"assistant","content":[{"type":"output_text","text":"Checking."}]},
             {"type":"function_call","call_id":"toolu_1","name":"get_findings","arguments":"{\"category\":\"power\"}"}]
            """), input));
    }

    [Theory]
    [InlineData(401, "invalid_api_key", "auth", false)]
    [InlineData(403, "unsupported_country_region_territory", "auth", false)]
    [InlineData(429, "insufficient_quota", "quota", false)]
    [InlineData(429, "rate_limit_exceeded", "rate_limited", true)]
    [InlineData(400, "invalid_value", "bad_request", false)]
    [InlineData(404, "model_not_found", "bad_request", false)]
    [InlineData(500, "server_error", "overloaded", true)]
    [InlineData(503, "server_error", "overloaded", true)]
    public async Task Http_errors_map_to_codes_without_leaking_the_key(int status, string code, string expected, bool retryable)
    {
        var (adapter, handler) = Create();
        // OpenAI really does echo a masked key on 401; the full key too, for good measure.
        var body = $$$"""{"error":{"message":"Incorrect API key provided: sk-proj-****cdef. Also {{{TestKeys.OpenAi}}}","type":"invalid_request_error","code":"{{{code}}}","param":null}}""";
        handler.Respond((HttpStatusCode)status, body, r => r.Headers.TryAddWithoutValidation("retry-after-ms", "1500"));

        var ex = await Assert.ThrowsAsync<ProviderException>(() => adapter.RunTurnAsync(Key, Request([new UserText("x")]), CancellationToken.None));
        Assert.Equal(expected, ex.Code);
        Assert.Equal(retryable, ex.Retryable);
        Assert.DoesNotContain(TestKeys.OpenAi, ex.ToString());
        Assert.DoesNotContain("sk-proj", ex.ToString());
        if (expected == "rate_limited") Assert.Equal(2, ProviderRegistry.RetryAfterSeconds(ex));
    }

    [Fact]
    public async Task Failed_status_in_a_200_maps_by_error_code()
    {
        var (adapter, handler) = Create();
        handler.Respond(HttpStatusCode.OK, """{"status":"failed","error":{"code":"server_error","message":"boom"},"output":[]}""");
        var ex = await Assert.ThrowsAsync<ProviderException>(() => adapter.RunTurnAsync(Key, Request([new UserText("x")]), CancellationToken.None));
        Assert.Equal("overloaded", ex.Code);
    }

    [Theory]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":[{"type":"output_te""")]
    [InlineData("""{"status":"in_progress","output":[]}""")]
    [InlineData("""{"status":"completed"}""")]
    [InlineData("""{"status":"completed","output":[{"type":"function_call","name":"get_findings","arguments":"{}"}]}""")]
    public async Task Truncated_or_malformed_bodies_are_incomplete(string body)
    {
        var (adapter, handler) = Create();
        handler.Respond(HttpStatusCode.OK, body);
        var ex = await Assert.ThrowsAsync<ProviderException>(() => adapter.RunTurnAsync(Key, Request([new UserText("x")]), CancellationToken.None));
        Assert.Equal("incomplete", ex.Code);
    }

    [Fact]
    public async Task Check_lists_chat_models_and_reports_bad_keys()
    {
        var (adapter, handler) = Create();
        handler.Respond(HttpStatusCode.OK, """{"object":"list","data":[{"id":"gpt-6.1-sol"},{"id":"text-embedding-3-large"},{"id":"gpt-6-astra"},{"id":"gpt-realtime"},{"id":"o4-mini"}]}""");
        handler.Respond(HttpStatusCode.Unauthorized, """{"error":{"message":"Incorrect API key provided: sk-proj-****cdef.","code":"invalid_api_key"}}""");

        var ok = await adapter.CheckAsync(Key, CancellationToken.None);
        Assert.True(ok.Ok);
        Assert.Equal(["gpt-6-astra", "gpt-6.1-sol", "o4-mini"], ok.Models);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("https://api.openai.com/v1/models", handler.Requests[0].Uri.ToString());

        var bad = await adapter.CheckAsync(Key, CancellationToken.None);
        Assert.False(bad.Ok);
        Assert.StartsWith("auth:", bad.Error);
        Assert.DoesNotContain("sk-proj", bad.Error);
    }

    [LiveFact("OT_LIVE_OPENAI_KEY")]
    [Trait("Category", "Live")]
    public async Task Live_check_and_tool_turn()
    {
        var key = new ProviderCredential("openai_api", Environment.GetEnvironmentVariable("OT_LIVE_OPENAI_KEY")!);
        using var registry = ProviderRegistry.CreateDefault();
        var adapter = registry.Get("openai_api");
        var check = await adapter.CheckAsync(key, CancellationToken.None);
        Assert.True(check.Ok, check.Error);

        var transcript = new List<ProviderMessage> { new UserText("Call get_findings once, then summarise the result in one sentence.") };
        var req = new ProviderRequest(adapter.DefaultModel, "You are a test assistant.", transcript, [GetFindings], 2048);
        var t1 = await adapter.RunTurnAsync(key, req, CancellationToken.None);
        Assert.Equal("tool_use", t1.StopReason);
        transcript.Add(t1.Turn);
        transcript.Add(new ToolResults(t1.Turn.Calls.Select(c => new ToolResult(c.CallId, JsonNode.Parse("""{"findings":[{"id":"fnd-1","title":"High CPU"}]}""")!, false)).ToList()));
        var t2 = await adapter.RunTurnAsync(key, req with { Messages = transcript }, CancellationToken.None);
        Assert.Equal("end", t2.StopReason);
        Assert.False(string.IsNullOrWhiteSpace(t2.Turn.Text));
    }
}

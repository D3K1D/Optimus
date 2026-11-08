using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Providers.Tests;

public sealed class AnthropicAdapterTests
{
    static readonly ProviderCredential Key = new("anthropic_api", TestKeys.Anthropic);

    static readonly ToolSpec RunDiagnostic = new("run_diagnostic", "Run one granted diagnostic.", (JsonObject)JsonNode.Parse("""
        {"type":"object","properties":{"diagnosticId":{"type":"string","enum":["power.inventory","process.sample"]},"window":{"type":"integer"}},
         "required":["diagnosticId"],"additionalProperties":false}
        """)!);

    static readonly ToolSpec GetFindings = new("get_findings", "Read findings.", (JsonObject)JsonNode.Parse("""
        {"type":"object","properties":{"category":{"type":"string"}}}
        """)!);

    static (AnthropicAdapter Adapter, FakeHandler Handler) Create()
    {
        var handler = new FakeHandler();
        return (new AnthropicAdapter(new HttpClient(handler)), handler);
    }

    static ProviderRequest Request(IReadOnlyList<ProviderMessage> messages, string model = "claude-opus-5-5") =>
        new(model, "You are a read-only PC diagnostics assistant.", messages, [RunDiagnostic, GetFindings], 4096);

    const string Turn1 = """
        {"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5",
         "content":[
           {"type":"thinking","thinking":"","signature":"sig-AAA"},
           {"type":"text","text":"Let me measure."},
           {"type":"tool_use","id":"toolu_01","name":"run_diagnostic","input":{"diagnosticId":"power.inventory"}},
           {"type":"tool_use","id":"toolu_02","name":"run_diagnostic","input":{"diagnosticId":"process.sample","window":15}}],
         "stop_reason":"tool_use","stop_details":null,
         "usage":{"input_tokens":100,"cache_creation_input_tokens":20,"cache_read_input_tokens":5,"output_tokens":50}}
        """;

    const string Turn2 = """
        {"id":"msg_2","type":"message","role":"assistant","model":"claude-opus-5-5",
         "content":[{"type":"thinking","thinking":"","signature":"sig-BBB"},{"type":"text","text":"App A uses 31% CPU [ev-1]."}],
         "stop_reason":"end_turn","usage":{"input_tokens":300,"output_tokens":40}}
        """;

    const string Turn3 = """
        {"id":"msg_3","type":"message","role":"assistant","model":"claude-opus-5-5",
         "content":[{"type":"text","text":"You're welcome."}],
         "stop_reason":"end_turn","usage":{"input_tokens":400,"output_tokens":5}}
        """;

    [Fact]
    public async Task Three_turn_tool_conversation_has_exact_request_shapes()
    {
        var (adapter, handler) = Create();
        handler.Respond(HttpStatusCode.OK, Turn1).Respond(HttpStatusCode.OK, Turn2).Respond(HttpStatusCode.OK, Turn3);

        var transcript = new List<ProviderMessage> { new UserText("Why is my PC loud?") };
        var t1 = await adapter.RunTurnAsync(Key, Request(transcript), CancellationToken.None);

        Assert.Equal("tool_use", t1.StopReason);
        Assert.Equal("Let me measure.", t1.Turn.Text);
        Assert.Equal(["toolu_01", "toolu_02"], t1.Turn.Calls.Select(c => c.CallId));
        Assert.Equal("process.sample", t1.Turn.Calls[1].Arguments["diagnosticId"]!.GetValue<string>());
        Assert.Equal(new ProviderUsage(125, 50), t1.Usage);

        transcript.Add(t1.Turn);
        transcript.Add(new ToolResults([
            new ToolResult("toolu_01", JsonNode.Parse("""{"evidence":[{"id":"ev-1"}]}""")!, false),
            new ToolResult("toolu_02", JsonNode.Parse("""{"error":{"code":"not_allowed","message":"no"}}""")!, true),
        ]));
        var t2 = await adapter.RunTurnAsync(Key, Request(transcript), CancellationToken.None);
        Assert.Equal("end", t2.StopReason);
        Assert.Empty(t2.Turn.Calls);

        transcript.Add(t2.Turn);
        transcript.Add(new UserText("Thanks"));
        var t3 = await adapter.RunTurnAsync(Key, Request(transcript), CancellationToken.None);
        Assert.Equal("You're welcome.", t3.Turn.Text);

        // ---- request 1
        var r1 = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, r1.Method);
        Assert.Equal("https://api.anthropic.com/v1/messages", r1.Uri.ToString());
        Assert.Equal(TestKeys.Anthropic, r1.Headers["x-api-key"]);
        Assert.Equal("2026-10-21", r1.Headers["anthropic-version"]);
        Assert.Equal("server-side-fallback-2026-07-01", r1.Headers["anthropic-beta"]);
        var b1 = r1.Json;
        Assert.Equal("claude-opus-5-5", b1["model"]!.GetValue<string>());
        Assert.Equal(4096, b1["max_tokens"]!.GetValue<int>());
        Assert.Equal("You are a read-only PC diagnostics assistant.", b1["system"]!.GetValue<string>());
        Assert.Equal("default", b1["fallbacks"]!.GetValue<string>());
        Assert.Equal("medium", b1["output_config"]!["effort"]!.GetValue<string>());
        Assert.Equal("ephemeral", b1["cache_control"]!["type"]!.GetValue<string>());
        Assert.False(b1.ContainsKey("temperature"));
        Assert.False(b1.ContainsKey("thinking"));
        Assert.False(b1.ContainsKey("tool_choice"));
        var tools = b1["tools"]!.AsArray();
        Assert.Equal("run_diagnostic", tools[0]!["name"]!.GetValue<string>());
        Assert.True(tools[0]!["strict"]!.GetValue<bool>());               // additionalProperties:false
        Assert.False(tools[1]!.AsObject().ContainsKey("strict"));          // open schema: not strict
        Assert.True(JsonNode.DeepEquals(RunDiagnostic.InputSchema, tools[0]!["input_schema"]));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            [{"role":"user","content":[{"type":"text","text":"Why is my PC loud?"}]}]
            """), b1["messages"]));

        // ---- request 2: assistant blocks replayed verbatim, tool_result blocks open the next user message
        var m2 = handler.Requests[1].Json["messages"]!.AsArray();
        Assert.Equal(3, m2.Count);
        var turn1Content = JsonNode.Parse(Turn1)!["content"];
        Assert.True(JsonNode.DeepEquals(turn1Content, m2[1]!["content"]));
        Assert.Equal("assistant", m2[1]!["role"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            {"role":"user","content":[
              {"type":"tool_result","tool_use_id":"toolu_01","content":"{\"evidence\":[{\"id\":\"ev-1\"}]}","is_error":false},
              {"type":"tool_result","tool_use_id":"toolu_02","content":"{\"error\":{\"code\":\"not_allowed\",\"message\":\"no\"}}","is_error":true}]}
            """), m2[2]));

        // ---- request 3: append-only; earlier messages byte-identical to request 2
        var m3 = handler.Requests[2].Json["messages"]!.AsArray();
        Assert.Equal(5, m3.Count);
        for (var i = 0; i < 3; i++) Assert.True(JsonNode.DeepEquals(m2[i], m3[i]));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Turn2)!["content"], m3[3]!["content"]));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"role":"user","content":[{"type":"text","text":"Thanks"}]}"""), m3[4]));

        // The key appears only in the auth header, never in a body.
        Assert.All(handler.Requests, r => Assert.DoesNotContain(TestKeys.Anthropic, r.Body!));
    }

    [Fact]
    public void Tool_results_followed_by_text_share_one_user_message_with_results_first()
    {
        var messages = AnthropicAdapter.BuildMessages([
            new UserText("hi"),
            new AssistantTurn(null, [new ToolCall("toolu_9", "get_findings", [])], null),
            new ToolResults([new ToolResult("toolu_9", JsonValue.Create("plain text"), false)]),
            new UserText("and also"),
        ]);
        Assert.Equal(3, messages.Count);
        var content = messages[2]!["content"]!.AsArray();
        Assert.Equal("tool_result", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("plain text", content[0]!["content"]!.GetValue<string>());
        Assert.Equal("text", content[1]!["type"]!.GetValue<string>());
        // Rebuilt assistant turn when no provider state exists.
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""[{"type":"tool_use","id":"toolu_9","name":"get_findings","input":{}}]"""), messages[1]!["content"]));
    }

    [Theory]
    [InlineData("end_turn", "end")]
    [InlineData("stop_sequence", "end")]
    [InlineData("max_tokens", "max_tokens")]
    [InlineData("refusal", "refusal")]
    [InlineData("pause_turn", "other")]
    [InlineData("model_context_window_exceeded", "other")]
    public void Stop_reasons_map(string wire, string expected)
    {
        var root = (JsonObject)JsonNode.Parse($$$"""
            {"content":[{"type":"text","text":"x"},{"type":"tool_use","id":"toolu_1","name":"get_findings","input":{}}],
             "stop_reason":"{{{wire}}}","usage":{"input_tokens":1,"output_tokens":1}}
            """)!;
        var turn = AnthropicAdapter.ParseResponse(root);
        Assert.Equal(expected, turn.StopReason);
        // A turn that did not stop for tool use exposes no calls and replays no dangling tool_use block.
        Assert.Empty(turn.Turn.Calls);
        Assert.DoesNotContain(turn.Turn.ProviderState!["content"]!.AsArray(), b => b!["type"]!.GetValue<string>() == "tool_use");
    }

    [Fact]
    public void Mid_output_fallback_drops_declined_internal_blocks()
    {
        var root = (JsonObject)JsonNode.Parse("""
            {"model":"claude-opus-5","content":[
               {"type":"thinking","thinking":"","signature":"s1"},
               {"type":"text","text":"Partial "},
               {"type":"tool_use","id":"toolu_old","name":"get_findings","input":{}},
               {"type":"fallback","from":{"model":"claude-opus-5-5"},"to":{"model":"claude-opus-5"}},
               {"type":"thinking","thinking":"","signature":"s2"},
               {"type":"tool_use","id":"toolu_new","name":"get_findings","input":{}}],
             "stop_reason":"tool_use",
             "usage":{"input_tokens":10,"output_tokens":3,"iterations":[
               {"type":"message","input_tokens":10,"output_tokens":2},{"type":"fallback_message","input_tokens":12,"output_tokens":3}]}}
            """)!;
        var turn = AnthropicAdapter.ParseResponse(root);
        Assert.Equal(["toolu_new"], turn.Turn.Calls.Select(c => c.CallId));
        var types = turn.Turn.ProviderState!["content"]!.AsArray().Select(b => b!["type"]!.GetValue<string>());
        Assert.Equal(["text", "thinking", "tool_use"], types);
        Assert.Equal(new ProviderUsage(22, 5), turn.Usage);
    }

    [Fact]
    public void Older_models_get_no_effort_and_no_fallbacks()
    {
        var body = AnthropicAdapter.BuildRequestBody(Request([new UserText("x")], "claude-haiku-4-5"), fallbacks: AnthropicAdapter.SupportsFallbacks("claude-haiku-4-5"), "medium");
        Assert.False(body.ContainsKey("output_config"));
        Assert.False(body.ContainsKey("fallbacks"));
        Assert.False(AnthropicAdapter.SupportsFallbacks("claude-haiku-5-5"));
        Assert.True(AnthropicAdapter.SupportsFallbacks("claude-sonnet-5-5"));
    }

    [Theory]
    [InlineData(401, "authentication_error", "auth", false)]
    [InlineData(403, "permission_error", "auth", false)]
    [InlineData(402, "billing_error", "quota", false)]
    [InlineData(400, "invalid_request_error", "bad_request", false)]
    [InlineData(404, "not_found_error", "bad_request", false)]
    [InlineData(413, "request_too_large", "bad_request", false)]
    [InlineData(429, "rate_limit_error", "rate_limited", true)]
    [InlineData(500, "api_error", "overloaded", true)]
    [InlineData(503, "api_error", "overloaded", true)]
    [InlineData(529, "overloaded_error", "overloaded", true)]
    [InlineData(408, "timeout_error", "timeout", false)]
    public async Task Http_errors_map_to_codes_without_leaking_the_key(int status, string type, string code, bool retryable)
    {
        var (adapter, handler) = Create();
        // Hostile body echoing the key, a masked key and request headers.
        var body = $$$"""{"type":"error","error":{"type":"{{{type}}}","message":"bad key {{{TestKeys.Anthropic}}} sk-ant-****cdef x-api-key: {{{TestKeys.Anthropic}}}"}}""";
        handler.Respond((HttpStatusCode)status, body, r => r.Headers.TryAddWithoutValidation("retry-after", "7"));

        var ex = await Assert.ThrowsAsync<ProviderException>(() => adapter.RunTurnAsync(Key, Request([new UserText("x")]), CancellationToken.None));
        Assert.Equal(code, ex.Code);
        Assert.Equal(retryable, ex.Retryable);
        Assert.DoesNotContain(TestKeys.Anthropic, ex.Message);
        Assert.DoesNotContain("sk-ant", ex.ToString());
        if (code == "rate_limited")
        {
            Assert.Contains("Retry after 7 s", ex.Message);
            Assert.Equal(7, ProviderRegistry.RetryAfterSeconds(ex));
        }
    }

    [Theory]
    [InlineData("""{"id":"msg_1","content":[{"type":"text","text":"Hel""")]
    [InlineData("<html>gateway</html>")]
    [InlineData("""{"id":"msg_1","content":[]}""")]
    [InlineData("""{"content":[17],"stop_reason":"end_turn"}""")]
    public async Task Truncated_or_malformed_bodies_are_incomplete(string body)
    {
        var (adapter, handler) = Create();
        handler.Respond(HttpStatusCode.OK, body);
        var ex = await Assert.ThrowsAsync<ProviderException>(() => adapter.RunTurnAsync(Key, Request([new UserText("x")]), CancellationToken.None));
        Assert.Equal("incomplete", ex.Code);
    }

    [Fact]
    public async Task Connection_dropped_mid_body_is_incomplete()
    {
        var (adapter, handler) = Create();
        handler.Respond((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BrokenStream(Encoding.UTF8.GetBytes("""{"content":[{"type":"te""")))
        }));
        var ex = await Assert.ThrowsAsync<ProviderException>(() => adapter.RunTurnAsync(Key, Request([new UserText("x")]), CancellationToken.None));
        Assert.Equal("incomplete", ex.Code);
    }

    [Fact]
    public async Task Network_failure_and_timeout_and_cancellation()
    {
        var (adapter, handler) = Create();
        handler.Respond((_, _) => throw new HttpRequestException("No such host is known. (api.anthropic.com:443)"));
        var net = await Assert.ThrowsAsync<ProviderException>(() => adapter.RunTurnAsync(Key, Request([new UserText("x")]), CancellationToken.None));
        Assert.Equal("network", net.Code);

        var slowHandler = new FakeHandler();
        slowHandler.Respond(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; });
        var slow = new AnthropicAdapter(new HttpClient(slowHandler)) { TurnTimeout = TimeSpan.FromMilliseconds(100) };
        var timeout = await Assert.ThrowsAsync<ProviderException>(() => slow.RunTurnAsync(Key, Request([new UserText("x")]), CancellationToken.None));
        Assert.Equal("timeout", timeout.Code);

        var cancelHandler = new FakeHandler();
        cancelHandler.Respond(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; });
        var cancelled = new AnthropicAdapter(new HttpClient(cancelHandler));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.RunTurnAsync(Key, Request([new UserText("x")]), cts.Token));
    }

    [Fact]
    public async Task Check_lists_models_with_a_get_and_reports_bad_keys()
    {
        var (adapter, handler) = Create();
        handler.Respond(HttpStatusCode.OK, """{"data":[{"id":"claude-opus-5-5","type":"model"},{"id":"claude-haiku-5-5","type":"model"}],"has_more":false}""");
        handler.Respond(HttpStatusCode.Unauthorized, $$$"""{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key {{{TestKeys.Anthropic}}}"}}""");

        var ok = await adapter.CheckAsync(Key, CancellationToken.None);
        Assert.True(ok.Ok);
        Assert.Equal(["claude-opus-5-5", "claude-haiku-5-5"], ok.Models);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.StartsWith("https://api.anthropic.com/v1/models", handler.Requests[0].Uri.ToString());
        Assert.Equal(TestKeys.Anthropic, handler.Requests[0].Headers["x-api-key"]);
        Assert.Null(handler.Requests[0].Body);

        var bad = await adapter.CheckAsync(Key, CancellationToken.None);
        Assert.False(bad.Ok);
        Assert.StartsWith("auth:", bad.Error);
        Assert.Equal("auth", bad.Code);
        Assert.Equal("claude-opus-5-5", ok.Model);
        Assert.DoesNotContain(TestKeys.Anthropic, bad.Error);
        Assert.DoesNotContain(TestKeys.Anthropic, Key.ToString());
    }

    [LiveFact("OT_LIVE_ANTHROPIC_KEY")]
    [Trait("Category", "Live")]
    public async Task Live_check_and_tool_turn()
    {
        var key = new ProviderCredential("anthropic_api", Environment.GetEnvironmentVariable("OT_LIVE_ANTHROPIC_KEY")!);
        using var registry = ProviderRegistry.CreateDefault();
        var adapter = registry.Get("anthropic_api");
        var check = await adapter.CheckAsync(key, CancellationToken.None);
        Assert.True(check.Ok, check.Error);

        var transcript = new List<ProviderMessage> { new UserText("Call get_findings once with no arguments, then summarise the result in one sentence.") };
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

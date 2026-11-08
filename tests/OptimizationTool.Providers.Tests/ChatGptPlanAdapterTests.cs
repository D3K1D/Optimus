using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Providers.Tests;

public sealed class ChatGptPlanAdapterTests
{
    static readonly ProviderCredential Token = new("chatgpt_plan", "ACCESS-TOKEN-do-not-leak");

    static readonly ToolSpec GetFindings = new("get_findings", "Read findings.", (JsonObject)JsonNode.Parse("""
        {"type":"object","properties":{"category":{"type":"string"}},"required":["category"],"additionalProperties":false}
        """)!);

    static ProviderRequest Request(IReadOnlyList<ProviderMessage> messages, int maxTokens = 4096) =>
        new("gpt-6.1-sol", "You are a read-only PC diagnostics assistant.", messages, [GetFindings], maxTokens);

    static string Sse(params object[] events) =>
        string.Concat(events.Select(e => $"event: x\ndata: {(e is string s ? s : ((JsonNode)e).ToJsonString())}\n\n"));

    static HttpResponseMessage Stream(string sse) =>
        new(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };

    const string ToolCallStream = """
        event: response.created
        data: {"type":"response.created","response":{"id":"resp_1","status":"in_progress","output":[]}}

        data: {"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","id":"rs_1","summary":[],"encrypted_content":"ENC-1"}}

        data: {"type":"response.function_call_arguments.delta","output_index":1,"delta":"{\"category\":"}

        data: {"type":"response.function_call_arguments.delta","output_index":1,"delta":"\"power\"}"}

        data: {"type":"response.output_item.done","output_index":1,"item":{"type":"function_call","id":"fc_1","call_id":"call_A","namespace":"optimization_tool","name":"get_findings","arguments":"{\"category\":\"power\"}"}}

        data: {"type":"response.output_item.done","output_index":2,"item":{"type":"function_call","id":"fc_2","call_id":"call_B","namespace":"optimization_tool","name":"get_findings","arguments":"{\"category\":\"storage\"}"}}

        : keep-alive comment

        event: response.completed
        data: {"type":"response.completed","response":{"id":"resp_1","status":"completed","model":"gpt-6.1-sol","output":[],"usage":{"input_tokens":50,"output_tokens":20}}}

        """;

    [Fact]
    public async Task Stream_is_drained_into_one_tool_turn_and_request_follows_plan_rules()
    {
        var handler = new FakeHandler();
        handler.Respond((_, _) => Task.FromResult(Stream(ToolCallStream)));
        handler.Respond((_, _) => Task.FromResult(Stream(Sse(
            JsonNode.Parse("""{"type":"response.output_text.delta","delta":"Done [ev-1]."}""")!,
            JsonNode.Parse("""{"type":"response.completed","response":{"status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Done [ev-1]."}]}],"usage":{"input_tokens":90,"output_tokens":5}}}""")!))));
        var adapter = new ChatGptPlanAdapter(new HttpClient(handler), signIn: null);

        var transcript = new List<ProviderMessage> { new UserText("Why is my PC loud?") };
        var t1 = await adapter.RunTurnAsync(Token, Request(transcript), CancellationToken.None);
        Assert.Equal("tool_use", t1.StopReason);
        Assert.Equal(["call_A", "call_B"], t1.Turn.Calls.Select(c => c.CallId));
        Assert.Equal("get_findings", t1.Turn.Calls[0].Name);
        Assert.Equal("storage", t1.Turn.Calls[1].Arguments["category"]!.GetValue<string>());
        Assert.Equal(new ProviderUsage(50, 20), t1.Usage);

        transcript.Add(t1.Turn);
        transcript.Add(new ToolResults([new ToolResult("call_A", JsonValue.Create("ok"), false), new ToolResult("call_B", JsonValue.Create("ok"), false)]));
        var t2 = await adapter.RunTurnAsync(Token, Request(transcript), CancellationToken.None);
        Assert.Equal("end", t2.StopReason);
        Assert.Equal("Done [ev-1].", t2.Turn.Text);

        var r1 = handler.Requests[0];
        Assert.Equal("https://api.openai.com/v1/responses", r1.Uri.ToString());
        Assert.Equal("Bearer ACCESS-TOKEN-do-not-leak", r1.Headers["authorization"]);
        Assert.Equal("text/event-stream", r1.Headers["accept"]);
        var b1 = r1.Json;
        Assert.True(b1["stream"]!.GetValue<bool>());
        Assert.False(b1["store"]!.GetValue<bool>());
        Assert.Equal("You are a read-only PC diagnostics assistant.", b1["instructions"]!.GetValue<string>());
        foreach (var banned in ChatGptPlanAdapter.BannedFields) Assert.False(b1.ContainsKey(banned), banned);
        var ns = b1["tools"]![0]!;
        Assert.Equal("namespace", ns["type"]!.GetValue<string>());
        Assert.Equal("optimization_tool", ns["name"]!.GetValue<string>());
        Assert.Equal("get_findings", ns["tools"]![0]!["name"]!.GetValue<string>());
        Assert.True(ns["tools"]![0]!["strict"]!.GetValue<bool>());
        Assert.DoesNotContain(b1["input"]!.AsArray(), i => i!["role"]?.GetValue<string>() == "system");

        // Turn 2 replays the full history: user, reasoning, both calls (with namespace), both outputs.
        var in2 = handler.Requests[1].Json["input"]!.AsArray();
        Assert.Equal(["message", "reasoning", "function_call", "function_call", "function_call_output", "function_call_output"],
            in2.Select(i => i!["type"]!.GetValue<string>()));
        Assert.Equal("ENC-1", in2[1]!["encrypted_content"]!.GetValue<string>());
        Assert.Equal("optimization_tool", in2[2]!["namespace"]!.GetValue<string>());
        Assert.Equal("call_B", in2[5]!["call_id"]!.GetValue<string>());
        Assert.All(handler.Requests, r => Assert.DoesNotContain("ACCESS-TOKEN", r.Body!));
    }

    [Fact]
    public void Additional_tools_delivery_puts_one_developer_item_first_and_rebuilt_calls_get_the_namespace()
    {
        var additional = new ChatGptPlanAdapter(new HttpClient(new FakeHandler()), null) { ToolDelivery = PlanToolDelivery.AdditionalTools };
        var body = additional.BuildRequestBody(Request([new UserText("hi")]));
        Assert.False(body.ContainsKey("tools"));
        var first = body["input"]![0]!;
        Assert.Equal("additional_tools", first["type"]!.GetValue<string>());
        Assert.Equal("developer", first["role"]!.GetValue<string>());
        Assert.Equal("get_findings", first["tools"]![0]!["name"]!.GetValue<string>());

        var ns = new ChatGptPlanAdapter(new HttpClient(new FakeHandler()), null);
        var rebuilt = ns.BuildRequestBody(Request([
            new UserText("hi"),
            new AssistantTurn(null, [new ToolCall("toolu_1", "get_findings", [])], null),
            new ToolResults([new ToolResult("toolu_1", JsonValue.Create("x"), false)]),
        ]));
        Assert.Equal("optimization_tool", rebuilt["input"]![1]!["namespace"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""data: {"type":"response.output_text.delta","delta":"Hel"}""" + "\n\n")]
    [InlineData("""data: {"type":"response.output_text.delta","delta":"Hel""")]
    [InlineData("")]
    public async Task Stream_without_a_terminal_event_is_incomplete(string sse)
    {
        var handler = new FakeHandler();
        handler.Respond((_, _) => Task.FromResult(Stream(sse)));
        var adapter = new ChatGptPlanAdapter(new HttpClient(handler), null);
        var ex = await Assert.ThrowsAsync<ProviderException>(() => adapter.RunTurnAsync(Token, Request([new UserText("x")]), CancellationToken.None));
        Assert.Equal("incomplete", ex.Code);
    }

    [Theory]
    [InlineData("subscription_sharing_usage_limit_exceeded", "rate_limited")]
    [InlineData("subscription_sharing_usage_unavailable", "overloaded")]
    [InlineData("subscription_sharing_user_not_eligible", "not_eligible")]
    [InlineData("subscription_sharing_invalid_user", "auth")]
    public async Task Failed_event_mid_stream_maps_by_code(string code, string expected)
    {
        var handler = new FakeHandler();
        handler.Respond((_, _) => Task.FromResult(Stream(Sse(
            JsonNode.Parse("""{"type":"response.output_text.delta","delta":"Partial"}""")!,
            JsonNode.Parse("""{"type":"response.failed","response":{"status":"failed","error":{"code":"CODE","message":"x"}}}""".Replace("CODE", code))!))));
        var adapter = new ChatGptPlanAdapter(new HttpClient(handler), null);
        var ex = await Assert.ThrowsAsync<ProviderException>(() => adapter.RunTurnAsync(Token, Request([new UserText("x")]), CancellationToken.None));
        Assert.Equal(expected, ex.Code);
    }

    [Theory]
    [InlineData(401, "subscription_sharing_invalid_user", "auth", false)]
    [InlineData(403, "subscription_sharing_user_not_eligible", "not_eligible", false)]
    [InlineData(403, "subscription_sharing_route_not_supported", "not_eligible", false)]
    [InlineData(429, "subscription_sharing_usage_limit_exceeded", "rate_limited", true)]
    [InlineData(429, "rate_limit_exceeded", "rate_limited", true)]
    [InlineData(400, "subscription_sharing_unsupported_capability", "bad_request", false)]
    [InlineData(503, "subscription_sharing_usage_unavailable", "overloaded", true)]
    public async Task Http_errors_map_to_codes(int status, string code, string expected, bool retryable)
    {
        var handler = new FakeHandler();
        handler.Respond((HttpStatusCode)status, $$$"""{"error":{"code":"{{{code}}}","message":"token ACCESS-TOKEN-do-not-leak","param":"tools[0]"}}""",
            r => r.Headers.TryAddWithoutValidation("retry-after", "30"));
        var adapter = new ChatGptPlanAdapter(new HttpClient(handler), null);
        var ex = await Assert.ThrowsAsync<ProviderException>(() => adapter.RunTurnAsync(Token, Request([new UserText("x")]), CancellationToken.None));
        Assert.Equal(expected, ex.Code);
        Assert.Equal(retryable, ex.Retryable);
        Assert.DoesNotContain("ACCESS-TOKEN", ex.ToString());
        if (expected == "rate_limited") Assert.Equal(30, ProviderRegistry.RetryAfterSeconds(ex));
    }

    [Fact]
    public async Task Local_output_budget_stops_the_stream_as_max_tokens_without_calls()
    {
        var deltas = Enumerable.Range(0, 200).Select(_ => (object)JsonNode.Parse("""{"type":"response.output_text.delta","delta":"0123456789"}""")!);
        var sse = Sse([JsonNode.Parse("""{"type":"response.output_item.done","output_index":0,"item":{"type":"function_call","call_id":"c","name":"get_findings","arguments":"{}"}}""")!, .. deltas])
                  + "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}\n\n";
        var handler = new FakeHandler();
        handler.Respond((_, _) => Task.FromResult(Stream(sse)));
        var adapter = new ChatGptPlanAdapter(new HttpClient(handler), null);
        var turn = await adapter.RunTurnAsync(Token, Request([new UserText("x")], maxTokens: 100), CancellationToken.None);
        Assert.Equal("max_tokens", turn.StopReason);
        Assert.Empty(turn.Turn.Calls);
        Assert.True(turn.Turn.Text!.Length is > 400 and < 500);
        Assert.Empty(turn.Turn.ProviderState!["output"]!.AsArray());
    }

    [Fact]
    public async Task Unauthorized_refreshes_once_through_sign_in_and_retries()
    {
        using var f = new FakeIssuer();
        Assert.True((await f.SignIn.SignInAsync(f.Approve(), CancellationToken.None)).Ok);
        var api = new FakeHandler();
        api.Respond(HttpStatusCode.Unauthorized, """{"error":{"code":"invalid_token"}}""");
        api.Respond((_, _) => Task.FromResult(Stream(Sse(
            JsonNode.Parse("""{"type":"response.completed","response":{"status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"hi"}]}]}}""")!))));
        var adapter = new ChatGptPlanAdapter(new HttpClient(api), f.SignIn);

        var turn = await adapter.RunTurnAsync(new ProviderCredential("chatgpt_plan", "ignored"), Request([new UserText("x")]), CancellationToken.None);
        Assert.Equal("hi", turn.Turn.Text);
        Assert.Equal("Bearer ACCESS-1", api.Requests[0].Headers["authorization"]);
        Assert.Equal("Bearer ACCESS-2", api.Requests[1].Headers["authorization"]);
        Assert.Equal(1, f.RefreshCount);
    }

    [Fact]
    public async Task Check_lists_visible_models_and_reports_not_eligible()
    {
        var handler = new FakeHandler();
        handler.Respond(HttpStatusCode.OK, """{"models":[{"slug":"gpt-6.1-sol","display_name":"GPT-6.1 Sol","visibility":"list"},{"slug":"internal-x","visibility":"hidden"}]}""");
        handler.Respond(HttpStatusCode.Forbidden, """{"error":{"code":"subscription_sharing_user_not_eligible"}}""");
        var adapter = new ChatGptPlanAdapter(new HttpClient(handler), null);

        var ok = await adapter.CheckAsync(Token, CancellationToken.None);
        Assert.True(ok.Ok);
        Assert.Equal(["gpt-6.1-sol"], ok.Models);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("https://api.openai.com/v1/models", handler.Requests[0].Uri.ToString());

        var bad = await adapter.CheckAsync(Token, CancellationToken.None);
        Assert.False(bad.Ok);
        Assert.StartsWith("not_eligible:", bad.Error);
        Assert.Equal("not_eligible", bad.Code);
        Assert.Equal("gpt-6.1-sol", ok.Model);
    }

    [Fact]
    public async Task Tool_capability_test_reports_true_on_a_call_and_false_on_text()
    {
        var handler = new FakeHandler();
        handler.Respond((_, _) => Task.FromResult(Stream(Sse(JsonNode.Parse(
            """{"type":"response.completed","response":{"status":"completed","output":[{"type":"function_call","call_id":"c1","namespace":"optimization_tool","name":"ping","arguments":"{}"}]}}""")!))));
        handler.Respond((_, _) => Task.FromResult(Stream(Sse(JsonNode.Parse(
            """{"type":"response.completed","response":{"status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"pong"}]}]}}""")!))));
        handler.Respond(HttpStatusCode.BadRequest, """{"error":{"code":"subscription_sharing_unsupported_capability","param":"tools"}}""");
        var adapter = new ChatGptPlanAdapter(new HttpClient(handler), null);
        Assert.True(await adapter.SupportsToolsAsync(Token, adapter.DefaultModel, CancellationToken.None));
        Assert.False(await adapter.SupportsToolsAsync(Token, adapter.DefaultModel, CancellationToken.None));
        Assert.False(await adapter.SupportsToolsAsync(Token, adapter.DefaultModel, CancellationToken.None));
    }

    [Fact]
    public void Registry_includes_the_plan_adapter()
    {
        using var registry = ProviderRegistry.CreateDefault();
        Assert.Equal(["anthropic_api", "openai_api", "chatgpt_plan"], registry.Kinds);
        Assert.IsAssignableFrom<IToolCapability>(registry.Get("chatgpt_plan"));
    }
}

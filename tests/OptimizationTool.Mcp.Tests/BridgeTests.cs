using System.Text.Json;
using System.Text.Json.Nodes;
using static OptimizationTool.Mcp.Tests.McpHarness;

namespace OptimizationTool.Mcp.Tests;

public sealed class BridgeTests
{
    private static async Task<(StubAgentHost Host, McpHarness Mcp)> PairedAsync()
    {
        var host = new StubAgentHost();
        var mcp = await McpHarness.StartAsync(host.PipeName);
        await mcp.CallAsync("list_capabilities"); // pairs
        host.PairStatus = "approved";
        return (host, mcp);
    }

    [Fact]
    public async Task ToolsList_HasToolsWithSchemasAndAnnotations()
    {
        await using var host = new StubAgentHost();
        await using var mcp = await McpHarness.StartAsync(host.PipeName);

        Assert.Equal("optimization-tool", mcp.Client.ServerInfo!.Name);
        var instructions = mcp.Client.ServerInstructions;
        Assert.NotNull(instructions);
        Assert.Contains("read-only", instructions);
        Assert.Contains("untrusted", instructions);
        Assert.Contains("[ev-", instructions);
        Assert.Contains("approval in the app", instructions);
        Assert.Contains("get_operation with the planId", instructions);
        Assert.Contains("Never say a change happened unless get_operation returns a verified receipt", instructions);

        var tools = await mcp.Client.ListToolsAsync();
        Assert.Equal(["add_card", "get_benchmarks", "get_cards", "get_fan_guidance", "get_findings", "get_operation", "get_pc_specs", "get_report_history", "list_capabilities", "note_card", "request_action", "run_diagnostic"], tools.Select(t => t.Name).Order().ToArray());
        foreach (var tool in tools)
        {
            var annotations = tool.ProtocolTool.Annotations!;
            Assert.Equal(tool.Name is not ("request_action" or "note_card" or "add_card"), annotations.ReadOnlyHint);
            Assert.False(annotations.OpenWorldHint);
            Assert.False(annotations.DestructiveHint);
            Assert.False(annotations.IdempotentHint);
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            var schema = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!;
            Assert.Equal("object", schema["type"]!.GetValue<string>());
            Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        }

        var run = JsonNode.Parse(tools.Single(t => t.Name == "run_diagnostic").ProtocolTool.InputSchema.GetRawText())!;
        Assert.Equal(10, run["properties"]!["diagnosticId"]!["enum"]!.AsArray().Count);
        Assert.Contains("sensors.snapshot", run["properties"]!["diagnosticId"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("diagnosticId", run["required"]![0]!.GetValue<string>());
        Assert.Equal(["system", "application", "update", "defender", "exposed", "all"], run["properties"]!["target"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        var findings = JsonNode.Parse(tools.Single(t => t.Name == "get_findings").ProtocolTool.InputSchema.GetRawText())!;
        Assert.Equal(["category", "severity", "findingIds", "cursor"], findings["properties"]!.AsObject().Select(p => p.Key).ToArray());

        var op = JsonNode.Parse(tools.Single(t => t.Name == "get_operation").ProtocolTool.InputSchema.GetRawText())!;
        Assert.Null(op["required"]); // operationId or planId
        Assert.Equal("^op-[A-Za-z0-9-]{1,80}$", op["properties"]!["operationId"]!["pattern"]!.GetValue<string>());
        Assert.Equal("^plan-[A-Za-z0-9-]{1,80}$", op["properties"]!["planId"]!["pattern"]!.GetValue<string>());

        var request = tools.Single(t => t.Name == "request_action");
        Assert.Contains("approve", request.Description);
        Assert.Contains("planId", request.Description);
        var ra = JsonNode.Parse(request.ProtocolTool.InputSchema.GetRawText())!;
        Assert.Equal(["actionId", "targetRef", "parameters", "findingRef"], ra["properties"]!.AsObject().Select(p => p.Key).ToArray());
        Assert.Equal(["power.select_scheme", "defender.update_signatures", "defender.quick_scan", "defender.full_scan", "firewall.enable_public"],
            ra["properties"]!["actionId"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.Equal("object", ra["properties"]!["targetRef"]!["type"]!.GetValue<string>());
        Assert.Equal("object", ra["properties"]!["parameters"]!["type"]!.GetValue<string>());
        Assert.Equal("^fnd-[A-Za-z0-9-]{1,80}$", ra["properties"]!["findingRef"]!["pattern"]!.GetValue<string>());
        Assert.Equal(["actionId"], ra["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());

        Assert.Equal(0, host.Connections); // listing tools never touches the host
    }

    [Fact]
    public async Task BenchmarkTools_HaveSchemas()
    {
        await using var host = new StubAgentHost();
        await using var mcp = await McpHarness.StartAsync(host.PipeName);
        var tools = await mcp.Client.ListToolsAsync();

        var bench = tools.Single(t => t.Name == "get_benchmarks");
        Assert.True(bench.ProtocolTool.Annotations!.ReadOnlyHint);
        var b = JsonNode.Parse(bench.ProtocolTool.InputSchema.GetRawText())!;
        Assert.Equal(["runId", "limit"], b["properties"]!.AsObject().Select(p => p.Key).ToArray());
        Assert.Null(b["required"]); // runId or limit, both optional
        Assert.Equal("string", b["properties"]!["runId"]!["type"]!.GetValue<string>());
        Assert.Equal(1, b["properties"]!["limit"]!["minimum"]!.GetValue<int>());
        Assert.Equal(5, b["properties"]!["limit"]!["maximum"]!.GetValue<int>());

        var fan = tools.Single(t => t.Name == "get_fan_guidance");
        Assert.True(fan.ProtocolTool.Annotations!.ReadOnlyHint);
        var f = JsonNode.Parse(fan.ProtocolTool.InputSchema.GetRawText())!;
        Assert.Equal(["cpu", "gpu", "ram", "disk"], f["properties"]!["part"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.Equal(["part"], f["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task BenchmarkTools_ForwardedAsToolCall()
    {
        var (host, mcp) = await PairedAsync();
        await using var _ = host;
        await using var __ = mcp;
        host.ToolHandler = (name, args) => StubAgentHost.Result(new JsonObject { ["untrusted"] = "x", ["tool"] = name });

        var r = await mcp.CallAsync("get_benchmarks", new { limit = 2 });
        Assert.False(r.IsError);
        var call = host.Last("tool.call")!;
        Assert.Equal("get_benchmarks", call["name"]!.GetValue<string>());
        Assert.Equal(2, call["arguments"]!["limit"]!.GetValue<int>());

        r = await mcp.CallAsync("get_fan_guidance", new { part = "gpu" });
        Assert.False(r.IsError);
        call = host.Last("tool.call")!;
        Assert.Equal("get_fan_guidance", call["name"]!.GetValue<string>());
        Assert.Equal("gpu", call["arguments"]!["part"]!.GetValue<string>());
    }

    [Fact]
    public async Task RequestAction_ForwardedAndPlanPolledByPlanId()
    {
        var (host, mcp) = await PairedAsync();
        await using var _ = host;
        await using var __ = mcp;
        host.ToolHandler = (name, args) => name switch
        {
            "request_action" => StubAgentHost.Result(new JsonObject { ["untrusted"] = "x", ["planId"] = "plan-7", ["status"] = "awaiting_user", ["summary"] = "Switch power plan" }),
            "get_operation" => StubAgentHost.Result(new JsonObject { ["untrusted"] = "x", ["planId"] = args!["planId"]!.DeepClone(), ["status"] = "awaiting_user" }),
            _ => StubAgentHost.Error(-32601, "nope"),
        };

        var r = await mcp.CallAsync("request_action", new
        {
            actionId = "power.select_scheme",
            targetRef = new { schemeAlias = "Scheme B" },
            parameters = new { },
            findingRef = "fnd-abc",
        });

        Assert.False(r.IsError);
        var call = host.Last("tool.call")!;
        Assert.Equal("request_action", call["name"]!.GetValue<string>());
        Assert.Equal("power.select_scheme", call["arguments"]!["actionId"]!.GetValue<string>());
        Assert.Equal("Scheme B", call["arguments"]!["targetRef"]!["schemeAlias"]!.GetValue<string>());
        Assert.Equal("fnd-abc", call["arguments"]!["findingRef"]!.GetValue<string>());
        Assert.Equal("plan-7", Json(r)["planId"]!.GetValue<string>());
        Assert.Equal("awaiting_user", Json(r)["status"]!.GetValue<string>());
        Assert.Equal(1, host.Count("tool.call")); // the bridge never polls or approves a plan itself

        r = await mcp.CallAsync("get_operation", new { planId = "plan-7" });
        Assert.False(r.IsError);
        Assert.Equal("plan-7", host.Last("tool.call")!["arguments"]!["planId"]!.GetValue<string>());
        Assert.Equal("awaiting_user", Json(r)["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task FirstCall_HelloesAsAgentAndReturnsPairingCode()
    {
        await using var host = new StubAgentHost();
        await using var mcp = await McpHarness.StartAsync(host.PipeName, "claude-code", "2.1.310");

        var r = await mcp.CallAsync("list_capabilities");

        Assert.False(r.IsError);
        Assert.Equal("Waiting for approval in Optimus (code 4821). Ask the user to approve it in the app, then call this tool again.", Text(r));
        var hello = host.Last("session.hello")!;
        Assert.Equal("agent", hello["client"]!["kind"]!.GetValue<string>());
        Assert.Equal(1, hello["protocol"]!["major"]!.GetValue<int>());
        Assert.Equal(2, hello["protocol"]!["minor"]!.GetValue<int>());
        var pair = host.Last("agent.pair")!;
        Assert.Equal("claude_code", pair["client"]!.GetValue<string>());
        Assert.Equal("2.1.310", pair["clientVersion"]!.GetValue<string>());
        Assert.Equal(Environment.ProcessId, pair["companionPid"]!.GetValue<int>());
        Assert.Equal(0, host.Count("tool.call"));

        // Still pending: same code, no second pairing, nothing forwarded.
        var again = await mcp.CallAsync("get_findings");
        Assert.Contains("(code 4821)", Text(again));
        Assert.Equal(1, host.Count("agent.pair"));
        Assert.Equal(0, host.Count("tool.call"));
    }

    [Theory]
    [InlineData("codex-mcp-client", "codex")]
    [InlineData("Codex", "codex")]
    [InlineData("claude-code", "claude_code")]
    [InlineData("some-editor", "unknown")]
    [InlineData(null, "unknown")]
    public async Task Pairing_ReportsClientKind(string? clientName, string expected)
    {
        Assert.Equal(expected, ClientIdentity.From(clientName, "1").Kind);
        if (clientName is null) return;
        await using var host = new StubAgentHost();
        await using var mcp = await McpHarness.StartAsync(host.PipeName, clientName, "9.9");
        await mcp.CallAsync("list_capabilities");
        Assert.Equal(expected, host.Last("agent.pair")!["client"]!.GetValue<string>());
    }

    [Fact]
    public async Task AfterApproval_ForwardsToolCallAndReturnsHostJson()
    {
        var (host, mcp) = await PairedAsync();
        await using var _ = host;
        await using var __ = mcp;

        var r = await mcp.CallAsync("run_diagnostic", new { diagnosticId = "process.sample", window = 15 });

        Assert.False(r.IsError);
        var call = host.Last("tool.call")!;
        Assert.Equal("run_diagnostic", call["name"]!.GetValue<string>());
        Assert.Equal("process.sample", call["arguments"]!["diagnosticId"]!.GetValue<string>());
        Assert.Equal(15, call["arguments"]!["window"]!.GetValue<int>());
        var json = Json(r);
        Assert.Equal("run_diagnostic", json["tool"]!.GetValue<string>());
        Assert.StartsWith("Strings in this result", json["untrusted"]!.GetValue<string>());
        Assert.NotNull(r.StructuredContent);
        Assert.Equal("run_diagnostic", r.StructuredContent!.Value.GetProperty("tool").GetString());
        Assert.Equal(1, host.Count("agent.pair"));
    }

    [Fact]
    public async Task HostErrors_MapToToolErrors()
    {
        var (host, mcp) = await PairedAsync();
        await using var _ = host;
        await using var __ = mcp;

        // JSON-RPC error from the host → §3 error shape with the host's code and message.
        host.ToolHandler = (_, _) => StubAgentHost.Error(-32602, "window must be 5-60", new JsonObject { ["field"] = "window" });
        var r = await mcp.CallAsync("run_diagnostic", new { diagnosticId = "process.sample", window = 2 });
        Assert.True(r.IsError);
        var e = Json(r)["error"]!;
        Assert.Equal("invalid_arguments", e["code"]!.GetValue<string>());
        Assert.Equal("window must be 5-60", e["message"]!.GetValue<string>());
        Assert.Equal(-32602, e["hostCode"]!.GetValue<int>());

        // A named code in data wins.
        host.ToolHandler = (_, _) => StubAgentHost.Error(-32013, "Diagnostic not granted", new JsonObject { ["code"] = "not_allowed" });
        r = await mcp.CallAsync("run_diagnostic", new { diagnosticId = "startup.list" });
        Assert.True(r.IsError);
        Assert.Equal("not_allowed", Json(r)["error"]!["code"]!.GetValue<string>());

        // §3 error result → passed through verbatim as a tool error.
        var exhausted = new JsonObject { ["untrusted"] = "x", ["error"] = new JsonObject { ["code"] = "exhausted", ["message"] = "Diagnostic budget used up." } };
        host.ToolHandler = (_, _) => StubAgentHost.Result(exhausted.DeepClone());
        r = await mcp.CallAsync("run_diagnostic", new { diagnosticId = "startup.list" });
        Assert.True(r.IsError);
        Assert.True(JsonNode.DeepEquals(exhausted, Json(r)));
        Assert.Equal(1, host.Count("agent.pair")); // budget errors keep the pairing
    }

    [Fact]
    public async Task RevokedOrDenied_NextCallRequestsNewCode()
    {
        var (host, mcp) = await PairedAsync();
        await using var _ = host;
        await using var __ = mcp;

        host.ToolHandler = (_, _) => StubAgentHost.Result(new JsonObject { ["error"] = new JsonObject { ["code"] = "revoked", ["message"] = "The user stopped this task." } });
        var r = await mcp.CallAsync("get_findings");
        Assert.True(r.IsError);
        Assert.Equal("revoked", Json(r)["error"]!["code"]!.GetValue<string>());
        Assert.Equal(1, host.Count("agent.pair")); // no pairing card popped up unasked

        host.PairStatus = "denied";
        r = await mcp.CallAsync("get_findings");
        Assert.Contains("(code 4822)", Text(r));
        Assert.Equal(2, host.Count("agent.pair"));

        r = await mcp.CallAsync("get_findings");
        Assert.True(r.IsError);
        Assert.Equal("not_allowed", Json(r)["error"]!["code"]!.GetValue<string>());
        Assert.Contains("denied", Json(r)["error"]!["message"]!.GetValue<string>());

        host.PairStatus = "pending";
        r = await mcp.CallAsync("get_findings");
        Assert.Contains("(code 4823)", Text(r));
    }

    [Fact]
    public async Task ExpiredPairing_RepairsWithNewCode()
    {
        await using var host = new StubAgentHost();
        await using var mcp = await McpHarness.StartAsync(host.PipeName);
        await mcp.CallAsync("list_capabilities");
        host.PairStatus = "expired";
        var r = await mcp.CallAsync("list_capabilities");
        Assert.False(r.IsError);
        Assert.Contains("(code 4822)", Text(r));
    }

    [Fact]
    public async Task HostAbsent_SaysToOpenTheApp()
    {
        await using var mcp = await McpHarness.StartAsync("OptimizationTool.test.absent." + Guid.NewGuid().ToString("N"));
        var r = await mcp.CallAsync("list_capabilities");
        Assert.True(r.IsError);
        Assert.Equal("unavailable", Json(r)["error"]!["code"]!.GetValue<string>());
        Assert.Contains("Open Optimus to connect", Json(r)["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task PipeDrop_ReconnectsAndKeepsPairingTheHostStillHas()
    {
        var (host, mcp) = await PairedAsync();
        await using var _ = host;
        await using var __ = mcp;
        Assert.False((await mcp.CallAsync("list_capabilities")).IsError);

        host.Drop();
        await Task.Delay(300);
        var r = await mcp.CallAsync("list_capabilities");

        Assert.False(r.IsError);
        Assert.Equal("list_capabilities", Json(r)["tool"]!.GetValue<string>());
        Assert.Equal(2, host.Hellos);
        Assert.Equal(2, host.Count("agent.pairStatus")); // re-checked after reconnect
        Assert.Equal(1, host.Count("agent.pair"));
    }

    [Fact]
    public async Task PipeDrop_HostForgotPairing_RepairsWithNewCode()
    {
        var (host, mcp) = await PairedAsync();
        await using var _ = host;
        await using var __ = mcp;
        Assert.False((await mcp.CallAsync("list_capabilities")).IsError);

        host.ForgetPairingsOnDrop = true;
        host.Drop();
        await Task.Delay(300);
        var r = await mcp.CallAsync("list_capabilities");

        Assert.False(r.IsError);
        Assert.Contains("(code 4822)", Text(r));
        Assert.Equal(2, host.Count("agent.pair"));
        Assert.Equal(1, host.Count("tool.call"));
    }

    [Fact]
    public async Task HostSaysNotPaired_RepairsImmediately()
    {
        var (host, mcp) = await PairedAsync();
        await using var _ = host;
        await using var __ = mcp;
        host.ToolHandler = (_, _) => StubAgentHost.Error(-32013, "invalid_state", new JsonObject { ["reason"] = "not_paired" });

        var r = await mcp.CallAsync("get_findings");
        Assert.False(r.IsError);
        Assert.Contains("(code 4822)", Text(r));
    }

    [Fact]
    public async Task LongDiagnostic_IsPolledThroughGetOperation()
    {
        var (host, mcp) = await PairedAsync();
        await using var _ = host;
        await using var __ = mcp;
        var polls = 0;
        host.ToolHandler = (name, args) => name switch
        {
            "run_diagnostic" => StubAgentHost.Result(new JsonObject { ["operationId"] = "op-1", ["status"] = "running", ["etaSeconds"] = 0.1 }),
            "get_operation" when ++polls < 3 => StubAgentHost.Result(new JsonObject { ["operationId"] = "op-1", ["status"] = "running", ["etaSeconds"] = 0.1 }),
            "get_operation" => StubAgentHost.Result(new JsonObject
            {
                ["operationId"] = args!["operationId"]!.DeepClone(), ["status"] = "complete",
                ["result"] = new JsonObject { ["diagnosticId"] = "process.sample", ["status"] = "complete", ["evidence"] = new JsonArray() },
            }),
            _ => StubAgentHost.Error(-32601, "nope"),
        };

        var r = await mcp.CallAsync("run_diagnostic", new { diagnosticId = "process.sample", window = 30 });

        Assert.False(r.IsError);
        Assert.Equal("complete", Json(r)["status"]!.GetValue<string>());
        Assert.Equal("process.sample", Json(r)["result"]!["diagnosticId"]!.GetValue<string>());
        Assert.Equal(4, host.Count("tool.call")); // run + 3 polls
    }

    [Fact]
    public async Task LongDiagnostic_PastPollBudget_ReturnsOperationId()
    {
        await using var host = new StubAgentHost();
        await using var mcp = await McpHarness.StartAsync(host.PipeName, configure: o => o with { PollBudget = TimeSpan.FromMilliseconds(300) });
        await mcp.CallAsync("list_capabilities");
        host.PairStatus = "approved";
        host.ToolHandler = (_, _) => StubAgentHost.Result(new JsonObject { ["operationId"] = "op-9", ["status"] = "running", ["etaSeconds"] = 0.1 });

        var r = await mcp.CallAsync("run_diagnostic", new { diagnosticId = "process.sample", window = 60 });

        Assert.False(r.IsError);
        Assert.Equal("op-9", Json(r)["operationId"]!.GetValue<string>());
        Assert.Equal("running", Json(r)["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task OversizeArguments_RejectedWithoutForwarding()
    {
        var (host, mcp) = await PairedAsync();
        await using var _ = host;
        await using var __ = mcp;

        var r = await mcp.CallAsync("get_findings", new { cursor = new string('x', 70 * 1024) });

        Assert.True(r.IsError);
        Assert.Equal("too_large", Json(r)["error"]!["code"]!.GetValue<string>());
        Assert.Equal(0, host.Count("tool.call"));
    }

    [Fact]
    public async Task CallTimeout_ReturnsTimeoutError()
    {
        await using var host = new StubAgentHost();
        await using var mcp = await McpHarness.StartAsync(host.PipeName, configure: o => o with { CallTimeout = TimeSpan.FromMilliseconds(300) });
        await mcp.CallAsync("list_capabilities");
        host.PairStatus = "approved";
        host.ToolHandler = (_, _) => { Thread.Sleep(1500); return StubAgentHost.Result(new JsonObject()); };

        var r = await mcp.CallAsync("list_capabilities");

        Assert.True(r.IsError);
        Assert.Equal("timeout", Json(r)["error"]!["code"]!.GetValue<string>());
    }
}

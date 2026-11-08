using System.Text.Json.Nodes;
using static OptimizationTool.Mcp.Tests.McpHarness;

namespace OptimizationTool.Mcp.Tests;

/// <summary>m5-chat §3: a bridge started by the app's own headless run presents OT_RUN_TOKEN instead of a pairing code.</summary>
public sealed class RunTokenTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task RunToken_IsSentInPair_ApprovedAtOnce_AndTheCallIsForwarded()
    {
        await using var host = new StubAgentHost { RunToken = Token };
        await using var mcp = await McpHarness.StartAsync(host.PipeName, configure: o => o with { RunToken = Token });

        var r = await mcp.CallAsync("get_cards");
        Assert.NotEqual(true, r.IsError);
        Assert.Equal("get_cards", Json(r)["tool"]!.GetValue<string>()); // forwarded on the first call: no waiting message
        Assert.Equal(Token, host.Last("agent.pair")!["runToken"]!.GetValue<string>());
        Assert.Equal(1, host.Count("tool.call"));

        await mcp.CallAsync("get_pc_specs");
        Assert.Equal(1, host.Count("agent.pair")); // stays paired for the run
        Assert.Equal(2, host.Count("tool.call"));
    }

    [Fact]
    public async Task UnknownRunToken_IsNotAllowed_AndNothingIsForwarded()
    {
        await using var host = new StubAgentHost { RunToken = "ffffffffffffffffffffffffffffffff" };
        await using var mcp = await McpHarness.StartAsync(host.PipeName, configure: o => o with { RunToken = Token });

        var r = await mcp.CallAsync("get_cards");
        Assert.True(r.IsError);
        Assert.Equal("not_allowed", Json(r)["error"]!["code"]!.GetValue<string>());
        Assert.Equal(0, host.Count("tool.call"));
    }

    [Fact]
    public async Task WithoutRunToken_PairingIsUnchanged()
    {
        await using var host = new StubAgentHost();
        await using var mcp = await McpHarness.StartAsync(host.PipeName, configure: o => o with { RunToken = null });
        var r = await mcp.CallAsync("get_cards");
        Assert.Contains("Waiting for approval", Text(r));
        Assert.Null(host.Last("agent.pair")!["runToken"]);
    }

    [Theory]
    [InlineData(Token, true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", false)]
    [InlineData("0123", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void RunToken_FromEnvironment_Is32LowercaseHex(string? value, bool accepted) =>
        Assert.Equal(accepted ? value : null, BridgeOptions.ReadRunToken(value));
}

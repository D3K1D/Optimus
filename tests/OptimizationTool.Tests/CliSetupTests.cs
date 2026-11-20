using System.Text.Json.Nodes;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Host;
using OptimizationTool.Transport;

namespace OptimizationTool.Tests;

/// <summary>Cross-PC CLI setup on the host side: outdated / blocked / auth reports, npm shims in the runner.</summary>
public sealed class CliSetupTests : IDisposable
{
    private readonly string _bin = Path.Combine(Path.GetTempPath(), "ot-tests", "setup-" + Guid.NewGuid().ToString("N"));
    private readonly string _claude;

    public CliSetupTests()
    {
        Directory.CreateDirectory(_bin);
        _claude = Path.Combine(_bin, "claude.exe");
        File.WriteAllText(_claude, "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_bin, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static AgentOptions Options(FakeCli cli)
    {
        var o = AgentSetup.Options();
        return new AgentOptions
        {
            Providers = o.Providers, Credentials = o.Credentials, Project = o.Project, RetryDelay = o.RetryDelay,
            McpExecutablePath = o.McpExecutablePath, CliRunner = cli,
        };
    }

    private static async Task<string> State(Client c, string kind)
    {
        var entry = (await c.Call("provider.list"))["providers"]!.AsArray().Single(p => p.Str("kind") == kind)!;
        return entry.Str("status") + (entry["detail"] is null ? "" : "/" + entry.Str("detail"));
    }

    // ---------- 3, 7, 10. outdated / blocked / auth reports ----------

    [Fact]
    public async Task OutdatedOrBlockedClient_IsUnavailable_AndCannotStartAHeadlessTask()
    {
        await using var host = TestHost.StartAgent(Options(new FakeCli()));
        var c = await host.ConnectAsync(minor: 4);
        var reportId = await AgentSetup.Report(c);
        var start = new JsonObject
        {
            ["idempotencyKey"] = Guid.NewGuid().ToString(), ["goal"] = "Chat", ["recipientKind"] = "claude_code_mcp",
            ["scope"] = new JsonObject { ["reportId"] = reportId, ["findingIds"] = null }, ["grant"] = AgentSetup.Grant(),
        };

        await c.Call("provider.setClient", new JsonObject { ["kind"] = "claude_code_mcp", ["path"] = _claude, ["signedIn"] = true, ["compatible"] = false });
        Assert.Equal("error/unavailable", await State(c, "claude_code_mcp"));
        Assert.Equal("provider_not_connected", (await c.Fails("agent.start", start)).ErrorData.Str("reason"));

        await c.Call("provider.setClient", new JsonObject { ["kind"] = "claude_code_mcp", ["path"] = null, ["signedIn"] = null, ["blocked"] = true });
        Assert.Equal("error/unavailable", await State(c, "claude_code_mcp"));

        await c.Call("provider.setClient", new JsonObject { ["kind"] = "claude_code_mcp", ["path"] = _claude, ["signedIn"] = true, ["auth"] = "api_key" });
        Assert.Equal("connected", await State(c, "claude_code_mcp"));
        SchemaTests.AssertValid("provider-list", await c.Call("provider.list"));
    }

    [Theory]
    [InlineData("""{"kind":"claude_code_mcp","path":null,"signedIn":null,"auth":"cheap"}""")]
    [InlineData("""{"kind":"claude_code_mcp","path":null,"signedIn":null,"compatible":"no"}""")]
    [InlineData("""{"kind":"claude_code_mcp","path":null,"signedIn":null,"blocked":1}""")]
    public async Task SetClient_RefusesBadNewFields(string json)
    {
        await using var host = TestHost.StartAgent(Options(new FakeCli()));
        var c = await host.ConnectAsync(minor: 4);
        Assert.Equal(-32602, (await c.Fails("provider.setClient", (JsonObject)JsonNode.Parse(json)!)).Code);
    }

    [Fact]
    public async Task SetClient_BlockedWithAPath_IsRefused()
    {
        await using var host = TestHost.StartAgent(Options(new FakeCli()));
        var c = await host.ConnectAsync(minor: 4);
        Assert.Equal(-32602, (await c.Fails("provider.setClient", new JsonObject { ["kind"] = "claude_code_mcp", ["path"] = _claude, ["signedIn"] = true, ["blocked"] = true })).Code);
    }

    // ---------- 2. the runner starts a resolved npm shim directly ----------

    [Fact]
    public void Runner_StartsAResolvedShimWithAnArgumentList()
    {
        var npm = Path.Combine(_bin, "A&B!%^", "npm");
        var exe = Path.Combine(npm, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "");
        File.WriteAllText(Path.Combine(npm, "node_modules", "@anthropic-ai", "claude-code", "package.json"), """{"bin":{"claude":"bin/claude.exe"}}""");
        File.WriteAllText(Path.Combine(npm, "claude.cmd"), "@echo off");

        var run = Path.Combine(_bin, "A&B!%^", "run");
        var psi = new CliRunner(@"C:\Windows", () => null).StartInfo(new CliLaunch(Path.Combine(npm, "claude.cmd"), ["--mcp-config", Path.Combine(run, "mcp.json")], "", _bin))!;
        Assert.Equal(exe, psi.FileName);
        Assert.Equal(["--mcp-config", Path.Combine(run, "mcp.json")], psi.ArgumentList);
        Assert.Equal("", psi.Arguments);

        // Without the package the same shim cannot cross cmd.exe: not started, with a reason.
        File.Delete(Path.Combine(npm, "node_modules", "@anthropic-ai", "claude-code", "package.json"));
        Assert.Null(new CliRunner(@"C:\Windows", () => null).StartInfo(new CliLaunch(Path.Combine(npm, "claude.cmd"), ["--version"], "", _bin)));
    }

    [Fact]
    public async Task Runner_ReportsWhyALaunchFailed()
    {
        var exit = await new CliRunner().RunAsync(new CliLaunch(Path.Combine(_bin, "missing.exe"), [], "", _bin), _ => { }, default);
        Assert.False(exit.Started);
        Assert.Equal("launch_failed", exit.LaunchError);
        var refused = await new CliRunner().RunAsync(new CliLaunch(Path.Combine(_bin, "x.cmd"), ["a\"b"], "", _bin), _ => { }, default);
        Assert.Equal("unsupported_path", refused.LaunchError);
    }
}

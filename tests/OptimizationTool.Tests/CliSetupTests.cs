using System.Text.Json.Nodes;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Host;
using OptimizationTool.Transport;

namespace OptimizationTool.Tests;

/// <summary>Cross-PC CLI setup on the host side: Codex credential store pass-through, outdated / blocked / auth reports, npm shims in the runner.</summary>
public sealed class CliSetupTests : IDisposable
{
    private readonly string _bin = Path.Combine(Path.GetTempPath(), "ot-tests", "setup-" + Guid.NewGuid().ToString("N"));
    private readonly string _claude, _codex;

    public CliSetupTests()
    {
        Directory.CreateDirectory(_bin);
        _claude = Path.Combine(_bin, "claude.exe");
        _codex = Path.Combine(_bin, "codex.exe");
        File.WriteAllText(_claude, "");
        File.WriteAllText(_codex, "");
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

    // ---------- 6. cli_auth_credentials_store ----------

    [Theory]
    [InlineData("cli_auth_credentials_store = \"keyring\"\n", "keyring")]
    [InlineData("model = \"gpt-5\"\r\ncli_auth_credentials_store='auto'   # use the OS store\r\n", "auto")]
    [InlineData("cli_auth_credentials_store = \"file\"", "file")]
    [InlineData("cli_auth_credentials_store = \"ephemeral\"", "ephemeral")]
    [InlineData("[profiles.work]\ncli_auth_credentials_store = \"keyring\"\n", null)]  // not top level
    [InlineData("cli_auth_credentials_store = \"keyring' -c evil='1\"", null)]
    [InlineData("cli_auth_credentials_store = \"KEYRING\"", null)]
    [InlineData("cli_auth_credentials_store = keyring", null)]
    [InlineData("# cli_auth_credentials_store = \"keyring\"", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void CodexConfig_ReadsOnlyTheValidatedTopLevelKey(string? toml, string? expected) =>
        Assert.Equal(expected, CodexConfig.CredentialsStore(toml));

    [Fact]
    public void CodexConfig_PathPrefersCodexHome()
    {
        Assert.Equal(@"D:\codex\config.toml", CodexConfig.ConfigPath(@"D:\codex", @"C:\Users\u"));
        Assert.Equal(@"C:\Users\u\.codex\config.toml", CodexConfig.ConfigPath(null, @"C:\Users\u"));
        Assert.Equal(@"C:\Users\u\.codex\config.toml", CodexConfig.ConfigPath("relative", @"C:\Users\u"));
        Assert.Null(CodexConfig.ConfigPath(null, null));
    }

    [Fact]
    public void CodexArgv_PassesTheCredentialStore_AndRefusesAnUnknownOne()
    {
        var args = HeadlessCommand.Codex(@"C:\run", @"C:\b.exe", new string('a', 32), null, HeadlessCommand.CodexLockdownFeatures, "keyring")!;
        Assert.Equal(["-c", "cli_auth_credentials_store='keyring'", "-"], args.TakeLast(3));
        Assert.Null(HeadlessCommand.Codex(@"C:\run", @"C:\b.exe", new string('a', 32), null, HeadlessCommand.CodexLockdownFeatures, "evil"));
        Assert.DoesNotContain(HeadlessCommand.Codex(@"C:\run", @"C:\b.exe", new string('a', 32), null, HeadlessCommand.CodexLockdownFeatures)!, a => a.Contains("cli_auth", StringComparison.Ordinal));
        var resume = HeadlessCommand.Codex(@"C:\run", @"C:\b.exe", new string('a', 32), "01a1acef-e7fe-7660-8a5e-dbabb0764106", HeadlessCommand.CodexLockdownFeatures, "auto")!;
        Assert.Equal(["-c", "cli_auth_credentials_store='auto'", "-"], resume.TakeLast(3));
    }

    [Fact]
    public async Task CodexRun_CarriesTheUsersCredentialStore()
    {
        var cli = new FakeCli();
        await using var host = TestHost.StartAgent(Options(cli));
        host.Server.Engine.Agent.CodexCredentialsStore = () => "keyring";
        var c = await host.ConnectAsync(minor: 4);
        var reportId = await AgentSetup.Report(c);
        await c.Call("provider.setClient", new JsonObject { ["kind"] = "codex_mcp", ["path"] = _codex, ["signedIn"] = true, ["codexFeatures"] = new JsonArray("shell_tool") });
        var taskId = (await AgentSetup.StartTask(c, reportId, "codex_mcp")).Str("taskId");
        await AgentSetup.Ask(c, taskId, "Hi?");
        await AgentSetup.TaskEvent(c, taskId, "agent.turn", p => p.Str("status") is "finished" or "failed");
        var launch = Assert.Single(cli.Launches);
        Assert.Equal(["-c", "cli_auth_credentials_store='keyring'", "-"], launch.Arguments.TakeLast(3));
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
        Assert.Equal(-32602, (await c.Fails("provider.setClient", new JsonObject { ["kind"] = "codex_mcp", ["path"] = _codex, ["signedIn"] = true, ["blocked"] = true })).Code);
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

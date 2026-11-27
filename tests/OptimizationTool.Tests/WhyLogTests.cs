using System.Text.Json.Nodes;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Transport.Logging;

namespace OptimizationTool.Tests;

/// <summary>
/// The "why" lines of headless research and headless chat (docs/error-logging.md, M9): each failure logs a Warn with its
/// site, and no message carries a path, a user or PC name, an email or stderr text, even when those values reach the code.
/// </summary>
[Collection(AppLogCurrentCollection.Name)]
public sealed class WhyLogTests : IDisposable
{
    private const string FakeUser = "zqxuser";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ot-tests", "whylog-" + FakeUser + "-" + Guid.NewGuid().ToString("N"));
    private readonly StringWriter _echo = new();

    public WhyLogTests()
    {
        Directory.CreateDirectory(_dir);
        // A scrubber that knows no names: what the lines say is what the code wrote, so a leak shows.
        AppLog.Open("host", null, devLog: false, _echo, makeCurrent: true, scrubber: LogScrubber.None);
    }

    public void Dispose()
    {
        AppLog.Open("none", null, devLog: false, makeCurrent: true);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Log()
    {
        lock (_echo) return _echo.ToString();
    }

    /// <summary>Nothing that identifies the PC or its user, and no stderr text, is in the log.</summary>
    private void AssertClean()
    {
        var log = Log();
        foreach (var bad in new[] { _dir, Path.GetTempPath().TrimEnd('\\'), FakeUser, "example.com", Environment.UserName, Environment.MachineName, "secret stderr" })
            Assert.DoesNotContain(bad, log, StringComparison.OrdinalIgnoreCase);
    }

    private ResearchClient Client(string? version = "2.1.318", bool? signedIn = true) =>
        new(Path.Combine(_dir, "claude.cmd"), version, signedIn, ["--safe-mode", "--setting-sources"]);

    private HeadlessResearcher Researcher(FakeCli cli, ResearchClient? client) => new(cli, _dir, _ => client, _ => null, _ => { });

    private static ResearchCall Call() => CheckupPolicy.Call("cli", "programs", [new CheckupEntry("i1", "programs", "Litware VPN", "3.2")]);

    private static readonly ProviderCredential Claude = new("claude_code_mcp", "");

    private const string Init = """{"type":"system","subtype":"init","session_id":"s","tools":["WebSearch"],"mcp_servers":[]}""";

    /// <summary>The line of <paramref name="site"/>: one Warn, with its site and the given text.</summary>
    private void AssertLine(string site, string text)
    {
        var line = Assert.Single(Log().Split('\n'), l => l.Contains("[" + site + "]", StringComparison.Ordinal) && l.Contains(text, StringComparison.Ordinal));
        Assert.Contains(" WARN ", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Research_logs_why_it_could_not_run()
    {
        var cli = new FakeCli();
        await Assert.ThrowsAsync<WebResearchUnsupportedException>(() => Researcher(cli, null).ResearchAsync(Claude, Call(), default));
        AssertLine(ErrorSites.ResearchCliUnavailable, "research: headless unavailable reason=no_client kind=claude_code_mcp");
        await Assert.ThrowsAsync<WebResearchUnsupportedException>(() => Researcher(cli, Client(version: "0.0.1")).ResearchAsync(Claude, Call(), default));
        AssertLine(ErrorSites.ResearchCliUnavailable, "reason=no_profile");
        await Assert.ThrowsAsync<WebResearchUnsupportedException>(() =>
            Researcher(cli, Client() with { ClaudeFlags = ["--safe-mode"] }).ResearchAsync(Claude, Call(), default));
        AssertLine(ErrorSites.ResearchCliUnavailable, "reason=no_capability");

        var e = await Assert.ThrowsAsync<ProviderException>(() => Researcher(cli, Client(signedIn: false)).ResearchAsync(Claude, Call(), default));
        Assert.Equal("auth", e.Code);
        AssertLine(ErrorSites.ResearchCliNotSignedIn, "research: headless not signed in kind=claude_code_mcp");
        Assert.Empty(cli.Launches);
        AssertClean();
    }

    [Fact]
    public async Task Research_logs_why_a_call_failed_without_paths_names_or_stderr()
    {
        var cli = new FakeCli();
        var researcher = Researcher(cli, Client());
        async Task<string> Fails()
        {
            var ex = await Assert.ThrowsAsync<ProviderException>(() => researcher.ResearchAsync(Claude, Call(), default));
            return ex.Code;
        }

        // search_limit: the eleventh search kills the call.
        cli.Script = (_, line, ct) =>
        {
            line(Init);
            for (var i = 1; i <= 30; i++)
            {
                ct.ThrowIfCancellationRequested();
                line($$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_{{{i}}}","name":"WebSearch","input":{"query":"q{{{i}}}"}}]}}""");
            }
            return Task.FromResult(new CliExit(0, ""));
        };
        Assert.Equal("search_limit", await Fails());
        AssertLine(ErrorSites.ResearchCliSearchLimit, "research: headless call stopped reason=search_limit searches=11 limit=10");

        // tool_blocked: a tool outside the profile.
        cli.Script = (_, line, ct) =>
        {
            line(Init);
            line("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_1","name":"Bash","input":{"command":"dir C:\\Users\\zqxuser"}}]}}""");
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new CliExit(0, ""));
        };
        Assert.Equal("tool_blocked", await Fails());
        AssertLine(ErrorSites.ResearchCliToolBlocked, "research: headless call stopped reason=tool_blocked what=tool_use Bash");

        // Not started: a launch error that is not one of the fixed codes (here a path) logs as "other".
        cli.Script = (_, _, _) => Task.FromResult(new CliExit(0, "", Started: false, LaunchError: Path.Combine(_dir, "claude.cmd")));
        Assert.Equal("unavailable", await Fails());
        AssertLine(ErrorSites.ResearchCliNotStarted, "research: headless CLI did not start reason=other");
        cli.Script = (_, _, _) => Task.FromResult(new CliExit(0, "", Started: false, LaunchError: "access_denied"));
        Assert.Equal("unavailable", await Fails());
        AssertLine(ErrorSites.ResearchCliNotStarted, "reason=access_denied");

        // No answer: the exit code and the stderr length only, never its text (a path, a user name, an email).
        var stderr = $"secret stderr {Path.Combine(_dir, "x.log")} {FakeUser}@example.com";
        cli.Script = (_, line, _) => { line(Init); return Task.FromResult(new CliExit(3, stderr)); };
        Assert.Equal("client_error", await Fails());
        AssertLine(ErrorSites.ResearchCliNoAnswer, $"research: headless no answer exit=3 stderr={stderr.Length}ch auth_like=no");

        // A sign-in-looking failure.
        cli.Script = (_, line, _) => { line(Init); return Task.FromResult(new CliExit(1, "secret stderr: Not logged in")); };
        Assert.Equal("auth", await Fails());
        AssertLine(ErrorSites.ResearchCliNotSignedIn, "research: headless no answer exit=1 stderr=28ch auth_like=yes");

        Assert.Empty(Directory.GetDirectories(Path.Combine(_dir, "research-runs"))); // cleanup worked: no cleanup line
        Assert.DoesNotContain("[" + ErrorSites.ResearchCliCleanup + "]", Log(), StringComparison.Ordinal);
        AssertClean();
    }

    [Fact]
    public void The_scrubber_still_hides_a_path_or_name_if_one_is_ever_written()
    {
        var echo = new StringWriter();
        var profile = @"C:\Users\" + FakeUser;
        var log = AppLog.Open("host", null, devLog: false, echo, makeCurrent: false, scrubber: new LogScrubber(profile, FakeUser, "ZQXPC", null));
        log.Warn($"research: headless run folder ACL failed at {profile}\\AppData\\x by {FakeUser} on ZQXPC", ErrorSites.ResearchCliFolderAcl);
        var text = echo.ToString();
        Assert.Contains(@"%USERPROFILE%\AppData\x by <user> on <pc>", text, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeUser, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[" + ErrorSites.ResearchCliFolderAcl + "]", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "launch_failed")]
    [InlineData("policy", "policy")]
    [InlineData(@"C:\Users\zqxuser\claude.cmd", "other")]
    public void LaunchCode_is_a_fixed_vocabulary(string? code, string logged) => Assert.Equal(logged, HeadlessResearcher.LaunchCode(code));

    [Theory]
    [InlineData("tool_use server other", "tool_use server other")]
    [InlineData(@"tool_use C:\Users\zqxuser", "other")]
    [InlineData("a@b.example.com", "other")]
    [InlineData(null, "other")]
    public void LogToken_keeps_only_plain_tokens(string? s, string logged) => Assert.Equal(logged, HeadlessResearcher.LogToken(s));

    // ---------------------------------------------------------------- headless chat

    private static AgentOptions Options(FakeCli cli)
    {
        var o = AgentSetup.Options();
        return new AgentOptions
        {
            Providers = o.Providers, Credentials = o.Credentials, Project = o.Project, RetryDelay = o.RetryDelay,
            McpExecutablePath = o.McpExecutablePath, CliRunner = cli,
        };
    }

    [Fact]
    public async Task Chat_logs_a_blocked_tool_and_a_run_that_did_not_start()
    {
        var claude = Path.Combine(_dir, "claude.exe");
        File.WriteAllText(claude, "");
        var cli = new FakeCli();
        await using var host = TestHost.StartAgent(Options(cli));
        var c = await host.ConnectAsync(minor: 4);
        var reportId = await AgentSetup.Report(c);
        await c.Call("provider.setClient", new JsonObject { ["kind"] = "claude_code_mcp", ["path"] = claude, ["signedIn"] = true });
        var taskId = (await AgentSetup.StartTask(c, reportId, "claude_code_mcp")).Str("taskId");

        cli.Script = async (_, emit, ct) =>
        {
            emit("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"mcp__other__x","input":{}}]}}""");
            await Task.Delay(Timeout.Infinite, ct);
            return new CliExit(0, "");
        };
        await AgentSetup.Ask(c, taskId, "Search the web");
        var turn = await AgentSetup.TaskEvent(c, taskId, "agent.turn", t => t.Str("status") is "finished" or "failed");
        Assert.Equal("tool_blocked", turn["payload"].Str("code"));
        AssertLine(ErrorSites.ChatToolBlocked, $"chat: task {taskId} headless run stopped reason=tool_blocked what=tool_use server other");
        for (var i = 0; i < 500 && host.Server.Engine.Agent.HasActiveWork; i++) await Task.Delay(10);

        cli.Script = (_, _, _) => Task.FromResult(new CliExit(0, "", Started: false, LaunchError: claude));
        await AgentSetup.Ask(c, taskId, "Again");
        await AgentSetup.TaskEvent(c, taskId, "agent.turn", t => t.Str("status") == "failed" && t.Str("code") == "unavailable");
        AssertLine(ErrorSites.ChatNotStarted, $"chat: task {taskId} headless run did not start reason=other");
        AssertClean();
    }
}

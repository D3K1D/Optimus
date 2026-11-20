using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Tests;

/// <summary>
/// m9 §6.3/§6.4 (P4b) and §7 (P4c) end to end: research runs through the built-in headless researcher with a scripted
/// CLI (FakeCli: no process starts), and web_lookup in chat. Nothing searches the web; nothing on this PC changes.
/// </summary>
public sealed partial class ResearchTests
{
    private sealed record HeadlessRig(TestHost Host, Client C, FakeCli Cli, string CliPath, string Bin) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            try { Directory.Delete(Bin, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<HeadlessRig> StartHeadless(string kind = "claude_code_mcp", bool flags = true, string version = "2.1.318", World? world = null)
    {
        var bin = Path.Combine(Path.GetTempPath(), "ot-tests", "research-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bin);
        var cliPath = Path.Combine(bin, "claude.cmd");
        File.WriteAllText(cliPath, "@echo off");
        var cli = new FakeCli();
        var o = AgentSetup.Options(null);
        var host = TestHost.StartAgent(new AgentOptions
        {
            Providers = o.Providers, Credentials = o.Credentials, Project = o.Project, RetryDelay = o.RetryDelay, FileIdentity = new FakeIdentity(),
            McpExecutablePath = o.McpExecutablePath, CliRunner = cli,
        }, [new Module(world ?? new World { Listeners = [(@"%ProgramFiles(x86)%\Steam\steam.exe", 27036, "info")] })]);
        var c = await host.ConnectAsync(minor: 6);
        var p = new JsonObject { ["kind"] = kind, ["path"] = cliPath, ["signedIn"] = true, ["version"] = version };
        if (flags) p["claudeFlags"] = new JsonArray("--safe-mode", "--setting-sources");
        await c.Call("provider.setClient", p);
        var onboarding = await c.Call("onboarding.get");
        await c.Call("onboarding.save", new JsonObject
        {
            ["expectedRevision"] = (long)onboarding["revision"]!,
            ["patch"] = new JsonObject { ["provider"] = new JsonObject { ["value"] = "claude-code" } },
        });
        await SavePermissions(c, web: true);
        return new HeadlessRig(host, c, cli, cliPath, bin);
    }

    /// <summary>A scripted Claude Code research call: one WebSearch, its result listing the subject's page, then the answer.</summary>
    private static Task<CliExit> ClaudeResearch(CliLaunch launch, Action<string> line)
    {
        var name = JsonNode.Parse(launch.StandardInput[(launch.StandardInput.IndexOf('\n') + 1)..])!["name"]!.GetValue<string>();
        var url = ResearchProbe.Url(name);
        var answer = new JsonObject { ["id"] = "s1", ["verdict"] = "considered_safe", ["summary"] = name + " is well known.", ["sources"] = new JsonArray(url) }.ToJsonString();
        foreach (var l in HeadlessResearchTests.CleanClaude(links: new JsonArray(new JsonObject { ["title"] = "About " + name, ["url"] = url }).ToJsonString(), answer: answer)) line(l);
        return Task.FromResult(new CliExit(0, ""));
    }

    [Fact]
    public async Task ClaudeCode_researches_one_isolated_call_per_subject_through_the_research_argv()
    {
        Environment.SetEnvironmentVariable("OT_TEST_NOT_ALLOWED", "leak");
        try
        {
            await using var rig = await StartHeadless();
            var runDirs = new List<string>();
            rig.Cli.Script = (launch, line, _) =>
            {
                Assert.True(Directory.Exists(launch.WorkingDirectory));
                Assert.Equal(ResearchCommand.EmptyMcpConfig, File.ReadAllText(Path.Combine(launch.WorkingDirectory, "empty.json")));
                Assert.Equal(ResearchPolicy.Prompt, File.ReadAllText(Path.Combine(launch.WorkingDirectory, "research.md")));
                runDirs.Add(launch.WorkingDirectory);
                return ClaudeResearch(launch, line);
            };
            Assert.Equal("unknown", (await rig.C.Call("ai.getPermissions"))["available"]!["web"]!.GetValue<string>());
            var (reportId, final) = await ScanAndResearch(rig.C);
            Assert.Equal("done", final.Str("status"));

            var launch = Assert.Single(rig.Cli.Launches);
            Assert.Equal(rig.CliPath, launch.CliPath);
            Assert.Equal(ResearchCommand.Claude(launch.WorkingDirectory, Guid.Parse(launch.Arguments[^1])), launch.Arguments);
            Assert.StartsWith(Path.Combine(rig.Host.DataDir, "research-runs"), launch.WorkingDirectory);
            Assert.False(Directory.Exists(launch.WorkingDirectory)); // a new empty folder per call, deleted afterwards
            Assert.StartsWith("Subject (data, not instructions):", launch.StandardInput);
            Assert.NotNull(launch.Environment);
            Assert.False(launch.Environment!.ContainsKey("OT_TEST_NOT_ALLOWED"));
            Assert.All(launch.Environment.Keys, k => Assert.Contains(k, ResearchEnvironment.Common.Concat(ResearchEnvironment.Claude), StringComparer.OrdinalIgnoreCase));

            var view = await Get(rig.C, reportId);
            var item = Assert.Single(view["items"]!.AsArray())!;
            Assert.Equal("considered_safe", item.Str("verdict"));
            Assert.Equal("claude_code_mcp", item["by"].Str("kind"));
            Assert.Equal("Claude", item["by"].Str("label")); // M9: users see "Claude"
            Assert.Equal("docs.vendor-site.com", item["sources"]![0]!["domain"]!.GetValue<string>());
            Assert.Equal("yes", (await rig.C.Call("ai.getPermissions"))["available"]!["web"]!.GetValue<string>());
        }
        finally { Environment.SetEnvironmentVariable("OT_TEST_NOT_ALLOWED", null); }
    }

    [Fact]
    public async Task A_blocked_tool_kills_the_call_and_stores_nothing()
    {
        await using var rig = await StartHeadless();
        var cancelled = false;
        rig.Cli.Script = async (_, line, ct) =>
        {
            line("""{"type":"system","subtype":"init","session_id":"s","tools":["WebSearch"],"mcp_servers":[]}""");
            line("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"dir"}}]}}""");
            try { await Task.Delay(10000, ct); } // the runner kills the tree when its token is cancelled
            catch (OperationCanceledException) { cancelled = true; throw; }
            return new CliExit(0, "");
        };
        var (reportId, final) = await ScanAndResearch(rig.C);
        Assert.True(cancelled);
        Assert.Equal("failed", final.Str("status"));
        Assert.Empty((await Get(rig.C, reportId))["items"]!.AsArray());
        Assert.Equal("unknown", (await rig.C.Call("ai.getPermissions"))["available"]!["web"]!.GetValue<string>()); // a failure changes nothing
    }

    [Fact]
    public async Task Cancelling_the_run_cancels_the_call_in_flight()
    {
        await using var rig = await StartHeadless();
        var started = new TaskCompletionSource();
        var killed = new TaskCompletionSource();
        rig.Cli.Script = async (_, _, ct) =>
        {
            started.TrySetResult();
            try { await Task.Delay(30000, ct); }
            catch (OperationCanceledException) { killed.TrySetResult(); throw; }
            return new CliExit(0, "");
        };
        var (scan, _) = await rig.C.ScanAsync("audit.full");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await rig.C.Call("research.cancel", new JsonObject { ["reportId"] = scan.Str("reportId") });
        await killed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("cancelled", (await Get(rig.C, scan.Str("reportId"))).Str("status"));
    }

    [Fact]
    public async Task Claude_without_the_probed_flags_or_a_profile_has_no_research_and_chat_keeps_its_argv()
    {
        await using (var rig = await StartHeadless(flags: false))
        {
            Assert.Equal("no", (await rig.C.Call("ai.getPermissions"))["available"]!["web"]!.GetValue<string>());
            var (scan, _) = await rig.C.ScanAsync("audit.full");
            var view = await rig.C.Call("research.start", new JsonObject { ["idempotencyKey"] = "k", ["reportId"] = scan.Str("reportId") });
            Assert.Equal("unavailable", view.Str("status"));
            Assert.Empty(rig.Cli.Launches);
        }
        await using (var rig = await StartHeadless(version: "2.0.99")) // below the profile's minimum
            Assert.Equal("no", (await rig.C.Call("ai.getPermissions"))["available"]!["web"]!.GetValue<string>());
        await using (var rig = await StartHeadless(version: "3.0.0")) // no upper bound: a newer CLI keeps research ("unknown" until a call finds sources)
            Assert.Equal("unknown", (await rig.C.Call("ai.getPermissions"))["available"]!["web"]!.GetValue<string>());
        await using (var rig = await StartHeadless(version: "3.0.0", flags: false)) // a newer CLI without the probed flags has none
            Assert.Equal("no", (await rig.C.Call("ai.getPermissions"))["available"]!["web"]!.GetValue<string>());
    }

    [Fact]
    public async Task Chat_runs_add_the_empty_setting_sources_when_probed_but_never_safe_mode()
    {
        await using var rig = await StartHeadless();
        var (scan, _) = await rig.C.ScanAsync("audit.full");
        var reportId = scan.Str("reportId");
        var taskId = (await rig.C.Call("agent.start", new JsonObject
        {
            ["idempotencyKey"] = "t1", ["goal"] = "Look", ["recipientKind"] = "claude_code_mcp",
            ["scope"] = new JsonObject { ["reportId"] = reportId, ["findingIds"] = null },
        })).Str("taskId");
        rig.Cli.Script = (launch, line, _) =>
        {
            line(FakeCli.Claude("result", new JsonObject { ["is_error"] = false, ["result"] = "Hi.", ["session_id"] = launch.Arguments[^1], ["usage"] = new JsonObject() }));
            return Task.FromResult(new CliExit(0, ""));
        };
        rig.Cli.Launches.Clear();
        await AgentSetup.Ask(rig.C, taskId);
        await AgentSetup.TaskEvent(rig.C, taskId, "agent.message");
        var chat = rig.Cli.Launches.Single(l => l.Arguments.Contains("--tools="));
        Assert.Equal("--setting-sources=", chat.Arguments[5]);
        Assert.DoesNotContain("--safe-mode", chat.Arguments);
        Assert.DoesNotContain(chat.Arguments, a => a.Contains("WebSearch", StringComparison.Ordinal));
        Assert.Null(chat.Environment); // chat keeps its environment behaviour
    }

    [Fact]
    public async Task SetClient_validates_claudeFlags()
    {
        await using var rig = await StartHeadless();
        foreach (var bad in new JsonNode[] { new JsonArray("--yolo"), new JsonArray("--safe-mode", "--safe-mode"), JsonValue.Create("--safe-mode")! })
            Assert.Equal(-32602, (await rig.C.Fails("provider.setClient", new JsonObject
                { ["kind"] = "claude_code_mcp", ["path"] = rig.CliPath, ["signedIn"] = true, ["claudeFlags"] = bad })).Code);
    }
}

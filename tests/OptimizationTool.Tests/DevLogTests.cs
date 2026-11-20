using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Transport.Logging;

namespace OptimizationTool.Tests;

/// <summary>
/// AppLog.Current is process-wide: tests that make a temp log current run in this collection, one at a time and never
/// beside another collection.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AppLogCurrentCollection
{
    public const string Name = "AppLog.Current";
}

/// <summary>
/// Developer-log instrumentation (docs/error-logging.md §1): RpcError records where it was raised and its site, the host
/// logs a sited error at Warn with its location, web_lookup logs why it refused or failed, and parameters and chat text
/// are never written.
/// </summary>
[Collection(AppLogCurrentCollection.Name)]
public sealed class DevLogTests
{
    private static int Line([CallerLineNumber] int line = 0) => line;

    /// <summary>A temp dev log made current for the test; Dispose restores the no-op log and deletes the folder.</summary>
    private sealed class CurrentLog : IDisposable
    {
        public string Dir { get; } = Directory.CreateTempSubdirectory("otdevlog-").FullName;
        public AppLog Log { get; }

        public CurrentLog() => Log = AppLog.Open("host", Dir, devLog: true, makeCurrent: true, scrubber: LogScrubber.None);

        /// <summary>Everything written to every log file in the folder.</summary>
        public string Text() => string.Concat(Directory.GetFiles(Path.Combine(Dir, "logs")).Select(ReadShared));

        public string Dev() => ReadShared(AppLog.DevPathFor(Dir));

        private static string ReadShared(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }

        public void Dispose()
        {
            AppLog.Open("none", null, devLog: false, makeCurrent: true); // back to the no-op log
            try { Directory.Delete(Dir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // ---- RpcError ----

    [Fact]
    public void RpcError_helpers_record_their_caller_and_site()
    {
        var line = Line() + 1;
        var state = RpcError.State("blocked", "No.", ErrorSites.SourceOpenBlocked);
        Assert.Equal(line, state.RaisedLine);
        Assert.EndsWith("DevLogTests.cs", state.RaisedFile, StringComparison.Ordinal);
        Assert.DoesNotContain("RpcError.cs", state.RaisedAt, StringComparison.Ordinal);
        Assert.Equal(ErrorSites.SourceOpenBlocked, state.Site);

        line = Line() + 1;
        var paramsError = RpcError.Params("field", "Bad.");
        Assert.Equal(line, paramsError.RaisedLine);
        Assert.Null(paramsError.Site);
        Assert.Equal(line + 3, RpcError.NotFound("report", "r-1").RaisedLine);
        Assert.Equal(line + 4, RpcError.Corrupt("report", "r-1").RaisedLine);

        line = Line() + 1;
        var direct = new RpcError(ErrorCodes.InvalidState, "x") { Site = ErrorSites.HostRpcInternal };
        Assert.Equal(line, direct.RaisedLine);
        Assert.Equal(ErrorSites.HostRpcInternal, direct.Site);
        // The JSON-RPC error data is unchanged: the site and location are never sent to the client.
        Assert.Equal("""{"reason":"blocked"}""", state.ErrorData!.ToJsonString());
    }

    [Fact]
    public async Task An_rpc_failing_with_a_sited_error_writes_a_warn_line_with_the_site_and_location()
    {
        await using var rig = await ResearchTests.Start();
        var (reportId, _) = await ResearchTests.ScanAndResearch(rig.C);
        var sourceId = (await ResearchTests.Get(rig.C, reportId))["items"]![0]!["sources"]![0]!["sourceId"]!.GetValue<string>();
        rig.Host.Server.Engine.Store.Write(db => db.Exec("UPDATE research_sources SET url = 'http://plain.example.com/' WHERE source_id = $s", ("$s", sourceId)));

        using var log = new CurrentLog();
        var error = await rig.C.Fails("research.source", new JsonObject { ["sourceId"] = sourceId });
        Assert.Equal(ErrorCodes.InvalidState, error.Code);

        var normal = File.ReadAllText(AppLog.PathFor(log.Dir, "host"));
        var warn = Assert.Single(normal.Split('\n'), l => l.Contains("rpc: research.source err -32013", StringComparison.Ordinal));
        Assert.Contains(" WARN ", warn, StringComparison.Ordinal);
        Assert.Contains("[" + ErrorSites.SourceOpenBlocked + "]", warn, StringComparison.Ordinal);
        Assert.Contains("(raised @src/Engine/Agent/AgentService.Research.cs:", warn, StringComparison.Ordinal);
        Assert.Contains("@src/Host/HostServer.cs:", warn, StringComparison.Ordinal);
        Assert.Contains("research: url refused reason=not_https", log.Dev(), StringComparison.Ordinal);
    }

    // ---- web_lookup reasons ----

    [Fact]
    public async Task Every_web_lookup_refusal_and_failure_logs_its_reason_code()
    {
        await using var rig = await ResearchTests.Start(world: new ResearchTests.World { ThreatFiles = ["Other.exe"] });
        // The labels phase stores nothing (no observed source), so lookups make fresh calls.
        rig.Probe.Answer = (_, _, _) => Task.FromResult(new ResearchAnswer("""{"verdict":"considered_safe","summary":"x","sources":[]}""", [], 0, new ProviderUsage(1, 1)));
        var (reportId, _) = await ResearchTests.ScanAndResearch(rig.C);
        var findings = await ResearchTests.FindingsByImage(rig.C, reportId);
        var steam = findings["steam.exe"].Str("id");
        var remote = findings["RemoteHelper.exe"].Str("id");
        var report = await rig.C.Call("report.get", new JsonObject { ["reportId"] = reportId });
        var threat = report["findings"]!.AsArray().Single(x => x!["ruleId"]!.GetValue<string>() == "test.threat")!["id"]!.GetValue<string>();

        using var log = new CurrentLog();
        var task = (await AgentSetup.StartTask(rig.C, reportId, grant: ResearchTests.WebGrant(lookups: 10))).Str("taskId");

        // Not researchable.
        Assert.Equal("not_allowed", (await ResearchTests.Lookup(rig.Host, task, threat))["error"].Str("code"));

        // The provider fails, times out, then refuses web search; after that web is "no" for this model.
        rig.Probe.Answer = (_, _, _) => throw new ProviderException("rate_limited", "Slow down.", true);
        Assert.Equal("unavailable", (await ResearchTests.Lookup(rig.Host, task, steam))["error"].Str("code"));
        rig.Probe.Answer = (_, _, _) => throw new TimeoutException();
        Assert.Equal("timeout", (await ResearchTests.Lookup(rig.Host, task, steam))["error"].Str("code"));
        rig.Probe.Answer = (_, _, _) => throw new WebResearchUnsupportedException("tool_rejected");
        Assert.Equal("unavailable", (await ResearchTests.Lookup(rig.Host, task, steam)).Str("status"));
        Assert.Equal("unavailable", (await ResearchTests.Lookup(rig.Host, task, remote)).Str("status"));

        // Out of scope (a new task, scoped to one finding; starting it ends the first).
        var scoped = (await rig.C.Call("agent.start", new JsonObject
        {
            ["idempotencyKey"] = "s", ["goal"] = "x", ["recipientKind"] = "anthropic_api", ["grant"] = ResearchTests.WebGrant(),
            ["scope"] = new JsonObject { ["reportId"] = reportId, ["findingIds"] = new JsonArray(remote) },
        })).Str("taskId");
        Assert.Equal("not_allowed", (await ResearchTests.Lookup(rig.Host, scoped, steam))["error"].Str("code"));

        // The lookup limit.
        var one = (await AgentSetup.StartTask(rig.C, reportId, grant: ResearchTests.WebGrant(lookups: 1))).Str("taskId");
        await ResearchTests.Lookup(rig.Host, one, steam);
        Assert.Equal("not_allowed", (await ResearchTests.Lookup(rig.Host, one, steam))["error"].Str("code"));

        var dev = log.Dev();
        foreach (var expected in new[]
        {
            $"web_lookup task={task} refused reason=rule_not_researchable [{ErrorSites.LookupRefused}]",
            $"web_lookup task={scoped} refused reason=out_of_scope [{ErrorSites.LookupRefused}]",
            $"web_lookup task={task} none reason=provider_error code=rate_limited",
            $"web_lookup task={task} none reason=timeout",
            $"web_lookup task={task} unavailable reason=unsupported_tool_rejected",
            $"web_lookup task={task} unavailable reason=web_off_for_model",
            $"web_lookup task={one} refused reason=limit",
        })
            Assert.Contains(expected, dev, StringComparison.Ordinal);
        // Failures reach the normal file too (Warn), with the site and the code location.
        var normal = File.ReadAllText(AppLog.PathFor(log.Dir, "host"));
        Assert.Contains($"reason=provider_error code=rate_limited kind=anthropic_api", normal, StringComparison.Ordinal);
        Assert.Contains($"[{ErrorSites.LookupProviderError}] @src/Engine/Agent/AgentService.WebLookup.cs:", normal, StringComparison.Ordinal);
        Assert.Contains($"[{ErrorSites.LookupWebOff}]", normal, StringComparison.Ordinal);
    }

    // ---- never logged ----

    [Fact]
    public async Task Parameters_and_chat_text_never_reach_the_logs()
    {
        const string secretParam = "secret-PARAM-123";
        const string chatText = "secret-CHAT-456 why is my PC loud";
        await using var rig = await ResearchTests.Start();
        var (reportId, _) = await ResearchTests.ScanAndResearch(rig.C);
        rig.Probe.Turn = _ => FakeProvider.Say("A model answer secret-ANSWER-789.");

        using var log = new CurrentLog();
        Assert.Equal(-32001, (await rig.C.Fails("research.get", new JsonObject { ["reportId"] = secretParam })).Code);
        Assert.Equal(-32001, (await rig.C.Fails("report.get", new JsonObject { ["reportId"] = secretParam })).Code);
        await rig.C.Fails("no.such." + secretParam, []);
        var task = (await AgentSetup.StartTask(rig.C, reportId, goal: chatText)).Str("taskId");
        await rig.C.Call("agent.get", new JsonObject { ["taskId"] = task });
        await AgentSetup.Ask(rig.C, task, chatText);
        await AgentSetup.TaskEvent(rig.C, task, "agent.message");

        var all = log.Text();
        Assert.Contains("rpc: research.get err -32001", all, StringComparison.Ordinal);
        Assert.Contains("rpc: <unknown> err -32601", all, StringComparison.Ordinal);
        Assert.Contains("rpc: agent.start ok", all, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-PARAM", all, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-CHAT", all, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-ANSWER", all, StringComparison.Ordinal);
    }
}

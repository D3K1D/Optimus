using System.Text;
using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Tests;

/// <summary>network.listeners target exposed/all, and usage events that move while a task runs.</summary>
public class ListenersAndUsageTests
{
    /// <summary>300 synthetic endpoints, mostly loopback or system, plus a Public firewall profile that is active and off.</summary>
    private sealed class ListenerWorld : IAuditModule
    {
        public string Name => "listeners";
        public IEnumerable<IRule> Rules() => [];
        public IEnumerable<IInventoryStep> InventorySteps(CollectorOptions options) => [];

        private static Fact Row(string protocol, int port, string scope, bool system, string? exe) =>
            new($"net.listener.{protocol}.{port}.{Ids.Hash16(exe ?? "system", scope)[..8]}", "Windows network tables", "measured", new JsonObject
            {
                ["protocol"] = protocol, ["family"] = "ipv4", ["port"] = port, ["scope"] = scope, ["pid"] = 1000 + port % 50,
                ["image"] = exe is null ? null : @"%ProgramFiles%\Synthetic\" + exe, ["imageKnown"] = exe is not null, ["isSystem"] = system, ["bindCount"] = 1,
            }, $"{exe ?? "Windows (System)"} listens on {protocol} port {port}.", null);

        public IEnumerable<ICollector> Collectors(CollectorOptions options) =>
        [
            new FakeCollector("win.security.listeners", "security", _ =>
            {
                var facts = new List<Fact>();
                facts.AddRange(Enumerable.Range(0, 200).Select(i => Row("tcp", 40000 + i, "loopback", false, "loop.exe")));
                facts.AddRange(Enumerable.Range(0, 60).Select(i => Row("udp", 50000 + i, "wildcard", true, null)));
                facts.Add(Row("udp", 1900, "wildcard", true, null));
                facts.Add(Row("tcp", 445, "wildcard", true, null));
                facts.Add(Row("tcp", 135, "wildcard", true, null));
                facts.AddRange(Enumerable.Range(0, 26).Select(i => Row("tcp", 27015 + i, "wildcard", false, "games.exe")));
                facts.Add(Row("tcp", 3389, "wildcard", false, "remote.exe"));
                facts.AddRange(Enumerable.Range(0, 10).Select(i => Row("tcp", 8000 + i, "specific", false, "web.exe")));
                facts.Add(new Fact("net.listeners.summary", "Windows network tables", "measured", new JsonObject { ["tcpCount"] = 239, ["udpCount"] = 61 }, "Summary.", null));
                return Task.FromResult(new CollectorResult(facts, new CoverageReceipt("complete", facts.Count, null)));
            }),
            new FakeCollector("win.security.firewall", "security", _ => Task.FromResult(FakeCollector.One("firewall.profile.public",
                new JsonObject { ["profile"] = "Public", ["enabled"] = false, ["active"] = true }, "Public profile is off."))),
            new FakeCollector("win.system.power", "performance", _ => Task.FromResult(FakeCollector.One("power.active_scheme",
                new JsonObject { ["onBattery"] = false }, "Balanced."))),
        ];
    }

    private static async Task<(TestHost Host, Client C, string TaskId)> Start(string[] diagnostics)
    {
        var options = AgentSetup.Options(null, new FakeProvider("anthropic_api"));
        var real = new AgentOptions { Providers = options.Providers, Credentials = options.Credentials }; // the real projection
        var host = TestHost.StartAgent(real, [new ListenerWorld()]);
        var c = await host.ConnectAsync();
        var reportId = await AgentSetup.Report(c);
        await AgentSetup.SetKey(c);
        var taskId = (await AgentSetup.StartTask(c, reportId, grant: AgentSetup.Grant(diagnostics))).Str("taskId");
        return (host, c, taskId);
    }

    [Fact]
    public async Task ExposedListeners_AreSummarized_RiskyFirst()
    {
        var (host, c, taskId) = await Start(["network.listeners"]);
        await using var _ = host;
        var agent = host.Server.Engine.Agent;

        var result = await agent.CallToolAsync(taskId, "run_diagnostic", new JsonObject { ["diagnosticId"] = "network.listeners" }, "in_app", null, default);
        Assert.Null(result["error"]);
        Assert.Null(result["truncated"]);
        Assert.True(Encoding.UTF8.GetByteCount(result.ToJsonString()) < 6000, result.ToJsonString());
        var evidence = result["evidence"]!.AsArray();

        var summary = evidence[0]!["value"]!;
        Assert.Equal("net.listeners.exposed.summary", evidence[0].Str("subject"));
        Assert.Equal(300, (int)summary["totalListeners"]!);
        Assert.Equal(239, (int)summary["tcp"]!);
        Assert.Equal(61, (int)summary["udp"]!);
        Assert.Equal(200, (int)summary["loopbackOnly"]!);
        Assert.Equal(100, (int)summary["reachableFromOtherDevices"]!);
        Assert.Equal(4, (int)summary["exposedItems"]!);
        Assert.Equal(4, (int)summary["exposedWithRiskyPorts"]!); // games.exe 27015-27040 includes 27017 (database)
        Assert.True((bool)summary["publicProfileActive"]!);
        Assert.False((bool)summary["publicFirewallOn"]!);

        var items = evidence.Skip(1).ToList();
        Assert.Equal(4, items.Count);
        // Risky first (exposure table: 445 file sharing, 3389 remote access, 8000 dev server), then by lowest port.
        Assert.StartsWith("net.exposed.tcp.wildcard.", items[0].Str("subject"));
        Assert.Equal([135, 445], items[0]!["value"]!["ports"]!.AsArray().Select(p => (int)(long)p!));   // Windows (System); UDP 1900 is low concern
        Assert.Contains(3389, items[1]!["value"]!["ports"]!.AsArray().Select(p => (int)(long)p!));
        Assert.StartsWith("net.exposed.tcp.specific.", items[2].Str("subject"));                  // web.exe 8000-8009
        Assert.Equal(26, items[3]!["value"]!["ports"]!.AsArray().Count);                         // games.exe, under the 64-port cap
        Assert.All(items, i => Assert.NotEmpty(i!["value"]!["exposure"]!.AsArray()));
        Assert.DoesNotContain(".exe", result.ToJsonString());                                     // the projector aliased every app
        Assert.DoesNotContain("Synthetic", result.ToJsonString());
        Assert.DoesNotContain("1900", result.ToJsonString());
        Assert.DoesNotContain("50001", result.ToJsonString());                                    // system UDP on ordinary ports is not exposed

        // The derived items are task evidence citing their source rows.
        var stored = host.Server.Engine.Store.Read(db => (string)db.Scalar("SELECT body FROM agent_evidence WHERE evidence_id = $e", ("$e", evidence[2].Str("id")))!);
        var sources = JsonNode.Parse(stored)!["sourceEvidenceIds"]!.AsArray();
        Assert.Single(sources);

        // target all: every row, truncated to fit.
        var all = await agent.CallToolAsync(taskId, "run_diagnostic", new JsonObject { ["diagnosticId"] = "network.listeners", ["target"] = "all" }, "in_app", null, default);
        Assert.True((bool)all["truncated"]!);
        Assert.Equal(301, (int)all["total"]!);
        Assert.Equal("invalid_arguments", (await agent.CallToolAsync(taskId, "run_diagnostic",
            new JsonObject { ["diagnosticId"] = "network.listeners", ["target"] = "system" }, "in_app", null, default))["error"].Str("code"));

        var caps = await agent.CallToolAsync(taskId, "list_capabilities", null, "in_app", null, default);
        var schema = caps["diagnostics"]!.AsArray().Single(d => d.Str("id") == "network.listeners")!["arguments"]!;
        Assert.Equal(["exposed", "all"], schema["properties"]!["target"]!["enum"]!.AsArray().Select(x => (string)x!));
        foreach (var r in new[] { result, all, caps }) SchemaTests.AssertValid("tool-result", r);
    }

    [Fact]
    public async Task McpSession_ReportsUsageAfterEachCall()
    {
        var (host, c, _) = await Start(["power.inventory"]);
        await using var __ = host;
        var engine = host.Server.Engine;
        var ctx = new CallContext("c-usage", "agent", Environment.ProcessId);
        var pair = await engine.InvokeAsync("agent.pair", new JsonObject { ["client"] = "claude_code" }, ctx);
        await c.Call("agent.approvePairing", new JsonObject { ["pairingId"] = pair.Str("pairingId"), ["approve"] = true });
        var reportId = (await c.Call("report.list"))["reports"]![0].Str("reportId");
        var taskId = (await AgentSetup.StartTask(c, reportId, "claude_code_mcp", AgentSetup.Grant(["power.inventory"]), pair.Str("pairingId"))).Str("taskId");

        await engine.InvokeAsync("tool.call", new JsonObject { ["name"] = "run_diagnostic", ["arguments"] = new JsonObject { ["diagnosticId"] = "power.inventory" } }, ctx);
        await engine.InvokeAsync("tool.call", new JsonObject { ["name"] = "get_findings", ["arguments"] = new JsonObject() }, ctx);

        List<JsonObject> Usage() => c.Events.Where(e => e.Str("stream") == "task:" + taskId && e.Str("type") == "agent.task" && e["payload"].Str("status") == "active")
            .OrderBy(e => (long)e["seq"]!).Select(e => e["payload"]!["used"]!.AsObject()).ToList();
        for (var i = 0; i < 200 && Usage().Count < 3; i++) await Task.Delay(10);
        var usage = Usage();
        Assert.Equal(3, usage.Count); // start, after call 1, after call 2: one per call
        Assert.Equal([0, 1, 1], usage.Select(u => (int)u["diagnosticCalls"]!));
        var bytes = usage.Select(u => (int)u["bytesShared"]!).ToList();
        Assert.True(bytes[0] == 0 && bytes[1] > 0 && bytes[2] > bytes[1], string.Join(",", bytes));

        var tools = c.Events.Where(e => e.Str("stream") == "task:" + taskId && e.Str("type") == "agent.tool").Select(e => e["payload"]!).ToList();
        Assert.Equal(["running", "complete", "running", "complete"], tools.Select(t => t.Str("status")));
        Assert.All(tools, t => Assert.Equal("claude_code", t.Str("origin")));
        foreach (var e in c.Events) SchemaTests.AssertValid("event", e);
    }
}

using System.Text;
using System.Text.Json.Nodes;
using OptimizationTool.Engine;

namespace OptimizationTool.Tests;

/// <summary>m3-agent §3: results larger than resultBytes are truncated (summary first), never errored.</summary>
public class TruncationTests
{
    /// <summary>A listeners-like collector: 500 endpoint rows (loopback, system and public) plus a summary row.</summary>
    private sealed class ManyListeners : IAuditModule
    {
        public string Name => "listeners";
        public IEnumerable<IRule> Rules() => [];
        public IEnumerable<IInventoryStep> InventorySteps(CollectorOptions options) => [];
        public IEnumerable<ICollector> Collectors(CollectorOptions options) =>
        [
            new FakeCollector("win.security.listeners", "security", _ =>
            {
                var facts = Enumerable.Range(0, 500).Select(i => new Fact($"net.listener.tcp.{1000 + i}.{i:x8}", "Listeners", "measured", new JsonObject
                {
                    ["protocol"] = "tcp", ["port"] = 1000 + i, ["scope"] = i % 3 == 0 ? "loopback" : "wildcard", ["isSystem"] = i % 5 == 0, ["bindCount"] = 1,
                    ["padding"] = 123456789.25, ["more"] = 987654321.5,
                }, $"Row {i}.", null)).ToList();
                // The summary is emitted last, like the real collector: the gateway must still keep it.
                facts.Add(new Fact("net.listeners.summary", "Listeners", "measured", new JsonObject { ["tcpCount"] = 500, ["udpCount"] = 0 }, "500 TCP listeners.", null));
                return Task.FromResult(new CollectorResult(facts, new CoverageReceipt("complete", facts.Count, null)));
            }),
        ];
    }

    [Fact]
    public async Task FiveHundredRows_AreTruncatedToResultBytes_KeepingTheSummary()
    {
        await using var host = TestHost.StartAgent(AgentSetup.Options(null, new FakeProvider("anthropic_api")), [new ManyListeners()]);
        var c = await host.ConnectAsync();
        var reportId = await AgentSetup.Report(c);
        await AgentSetup.SetKey(c);
        var taskId = (await AgentSetup.StartTask(c, reportId, grant: AgentSetup.Grant(["network.listeners"]))).Str("taskId");

        var result = await host.Server.Engine.Agent.CallToolAsync(taskId, "run_diagnostic", new JsonObject { ["diagnosticId"] = "network.listeners", ["target"] = "all" }, "in_app", null, default);
        Assert.Null(result["error"]);
        var bytes = Encoding.UTF8.GetByteCount(result.ToJsonString());
        Assert.True(bytes <= 32768, $"{bytes} bytes");
        Assert.True((bool)result["truncated"]!);
        var evidence = result["evidence"]!.AsArray();
        Assert.Equal(501, (int)result["total"]!);
        Assert.Equal(evidence.Count, (int)result["returned"]!);
        Assert.InRange(evidence.Count, 2, 500);
        Assert.Equal("net.listeners.summary", RawSubject(host, evidence[0].Str("id")));

        // Non-loopback, non-system rows come before loopback/system ones.
        var rows = evidence.Skip(1).Select(e => (JsonObject)e!["value"]!).ToList();
        var lastPreferred = rows.FindLastIndex(v => !Demoted(v));
        var firstDemoted = rows.FindIndex(Demoted);
        Assert.True(firstDemoted == -1 || firstDemoted > lastPreferred, "a loopback/system row came before a preferred one");

        var task = await c.Call("agent.get", new JsonObject { ["taskId"] = taskId });
        Assert.Equal(bytes, (int)task["used"]!["bytesShared"]!); // what was actually sent
    }

    private static bool Demoted(JsonObject v) => v.Str("scope") == "loopback" || (v["isSystem"]?.GetValue<bool>() ?? false);

    private static string RawSubject(TestHost host, string evidenceId) =>
        host.Server.Engine.Store.Read(db => (string)db.Scalar("SELECT subject FROM agent_evidence WHERE evidence_id = $e", ("$e", evidenceId))!);
}

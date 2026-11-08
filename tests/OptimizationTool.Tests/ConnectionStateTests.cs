using System.Diagnostics;
using System.Text.Json.Nodes;
using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Tests;

/// <summary>m3-agent §9: every connection state change emits exactly one provider.status, with the right payload.</summary>
public class ConnectionStateTests
{
    private static List<string> Transitions(Client c, string kind) =>
        c.Events.Where(e => e.Str("type") == "provider.status" && e["payload"].Str("kind") == kind)
            .OrderBy(e => (long)e["seq"]!)
            .Select(e => e["payload"].Str("status") + (e["payload"]!["detail"] is null ? "" : "/" + e["payload"].Str("detail")))
            .ToList();

    private static async Task Expect(Client c, string kind, params string[] expected)
    {
        for (var i = 0; i < 300 && Transitions(c, kind).Count < expected.Length; i++) await Task.Delay(10);
        await Task.Delay(50); // nothing extra may follow
        Assert.Equal(expected, Transitions(c, kind));
    }

    private static async Task<JsonObject> Entry(Client c, string kind) =>
        (await c.Call("provider.list"))["providers"]!.AsArray().Single(p => p.Str("kind") == kind)!.AsObject();

    /// <summary>A real process that stays alive until killed: stands in for a bridge PID.</summary>
    private static Process Bridge() => Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 120 127.0.0.1")
    {
        CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true,
    })!;

    [Fact]
    public async Task ApiKind_KeyChecksTurnsAndDisconnect()
    {
        var provider = new FakeProvider("anthropic_api",
            (_, _) => throw new ProviderException("auth", "401", retryable: false),
            (_, _) => throw new ProviderException("network", "unreachable", retryable: false),
            FakeProvider.Step(FakeProvider.Say("Fine.")));
        var store = new MemoryCredentialStore();
        await using var host = TestHost.StartAgent(AgentSetup.Options(store, provider));
        var c = await host.ConnectAsync(minor: 2);
        var reportId = await AgentSetup.Report(c);

        var first = await Entry(c, "anthropic_api");
        Assert.Equal("not_configured", first.Str("status"));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T", first.Str("since"));
        Assert.Equal("local", (await Entry(c, "local")).Str("status"));

        await AgentSetup.SetKey(c);
        await Expect(c, "anthropic_api", "checking", "connected");

        var taskId = (await AgentSetup.StartTask(c, reportId)).Str("taskId");
        await AgentSetup.Ask(c, taskId);
        await AgentSetup.TaskEvent(c, taskId, "agent.turn", p => p.Str("status") == "failed");
        await Expect(c, "anthropic_api", "checking", "connected", "error/auth");
        Assert.True(store.Has("anthropic_api")); // the key is kept until replaced
        Assert.Equal("auth", (await Entry(c, "anthropic_api")).Str("detail"));

        // Retry = provider.connect re-runs the check.
        await c.Call("provider.connect", new JsonObject { ["kind"] = "anthropic_api" });
        await Expect(c, "anthropic_api", "checking", "connected", "error/auth", "checking", "connected");

        await Idle(host);
        await AgentSetup.Ask(c, taskId, "again");
        await c.WaitForEvent(e => e.Str("stream") == "task:" + taskId && e.Str("type") == "agent.turn" && e["payload"].Str("code") == "network");
        await Idle(host);
        await AgentSetup.Ask(c, taskId, "once more");
        await AgentSetup.TaskEvent(c, taskId, "agent.message");
        await Expect(c, "anthropic_api", "checking", "connected", "error/auth", "checking", "connected", "error/network", "connected");

        await c.Call("provider.disconnect", new JsonObject { ["kind"] = "anthropic_api" });
        await Expect(c, "anthropic_api", "checking", "connected", "error/auth", "checking", "connected", "error/network", "connected", "not_configured");
        foreach (var e in c.Events) SchemaTests.AssertValid("event", e);
        SchemaTests.AssertValid("provider-list", await c.Call("provider.list"));
    }

    private static async Task Idle(TestHost host)
    {
        for (var i = 0; i < 300 && host.Server.Engine.Agent.HasActiveWork; i++) await Task.Delay(10);
    }

    [Fact]
    public async Task McpKind_PairingTaskAndBridgeLifecycle()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        await using var host = TestHost.StartAgent(AgentSetup.Options(null), time: time);
        var c = await host.ConnectAsync(minor: 2);
        var reportId = await AgentSetup.Report(c);
        var engine = host.Server.Engine;
        const string kind = "claude_code_mcp";

        var initial = await Entry(c, kind);
        Assert.Equal("waiting", initial.Str("status"));
        Assert.Equal("never_seen", initial.Str("detail"));
        Assert.Null(initial["pairingId"]);

        using var bridge1 = Bridge();
        var ctx1 = new CallContext("c-1", "agent", bridge1.Id);
        async Task<JsonObject> Pair(CallContext ctx) => (JsonObject)(await engine.InvokeAsync("agent.pair", new JsonObject { ["client"] = "claude_code" }, ctx))!;
        Task Approve(JsonObject pair, bool yes = true) => c.Call("agent.approvePairing", new JsonObject { ["pairingId"] = pair.Str("pairingId"), ["approve"] = yes });

        var lapsed = await Pair(ctx1);
        await Expect(c, kind, "pending");
        var pendingEvent = c.Events.Last(e => e.Str("type") == "provider.status");
        Assert.Equal(lapsed.Str("code"), pendingEvent["payload"].Str("code"));
        // The pending entry names its pairing, so the app can approve it after a reload.
        Assert.Equal(lapsed.Str("pairingId"), (await Entry(c, kind)).Str("pairingId"));
        time.Advance(TimeSpan.FromMinutes(6));
        await c.Call("provider.list");
        await Expect(c, kind, "pending", "error/pairing_expired");

        var denied = await Pair(ctx1);
        await Approve(denied, yes: false);
        await Expect(c, kind, "pending", "error/pairing_expired", "pending", "error/denied");

        var pair = await Pair(ctx1);
        await Approve(pair);
        // Paired: the entry carries the approved pairing, for agent.start from any Start.
        var pairedEntry = await Entry(c, kind);
        Assert.Equal(("paired", pair.Str("pairingId")), (pairedEntry.Str("status"), pairedEntry.Str("pairingId")));
        SchemaTests.AssertValid("provider-list", await c.Call("provider.list"));
        var task = await AgentSetup.StartTask(c, reportId, kind, pairingId: pair.Str("pairingId"));
        await Expect(c, kind, "pending", "error/pairing_expired", "pending", "error/denied", "pending", "paired", "active");

        // Stopping the task returns the kind to waiting/idle.
        await c.Call("agent.stop", new JsonObject { ["taskId"] = task.Str("taskId") });
        await Expect(c, kind, "pending", "error/pairing_expired", "pending", "error/denied", "pending", "paired", "active", "waiting/idle");

        // The bridge process exits while its task is active → client_closed; a new bridge pairs → pending; approve → active again.
        var pair2 = await Pair(ctx1);
        await Approve(pair2);
        var task2 = await AgentSetup.StartTask(c, reportId, kind, pairingId: pair2.Str("pairingId"));
        bridge1.Kill();
        await bridge1.WaitForExitAsync();
        engine.Agent.ConnectionClosed(ctx1);
        string[] head = ["pending", "error/pairing_expired", "pending", "error/denied", "pending", "paired", "active", "waiting/idle", "pending", "paired", "active", "client_closed"];
        await Expect(c, kind, head);
        Assert.Equal("active", (await c.Call("agent.get", new JsonObject { ["taskId"] = task2.Str("taskId") })).Str("status"));

        using var bridge2 = Bridge();
        var ctx2 = new CallContext("c-2", "agent", bridge2.Id);
        var pair3 = await Pair(ctx2);
        await Expect(c, kind, [.. head, "pending"]);
        await Approve(pair3);
        await Expect(c, kind, [.. head, "pending", "active"]);
        var tool = await engine.InvokeAsync("tool.call", new JsonObject { ["name"] = "list_capabilities" }, ctx2);
        Assert.NotNull(tool!["diagnostics"]); // the same task continues on the new bridge
        bridge2.Kill();

        foreach (var e in c.Events) SchemaTests.AssertValid("event", e);
    }

    [Fact]
    public async Task RejectedBridgeImage_ShowsSetupIssue()
    {
        await using var host = TestHost.StartAgent(AgentSetup.Options(null), devAllowAnyAgent: false);
        var desktop = await host.ConnectAsync(minor: 2);
        await desktop.Call("provider.list");
        var bridge = await host.ConnectAsync(hello: false);
        Assert.Equal("untrusted_agent", (await bridge.Fails("session.hello", Rpc.HelloParams(kind: "agent", minor: 2))).ErrorData.Str("reason"));
        await Expect(desktop, "claude_code_mcp", "error/bridge_rejected");
        await Expect(desktop, "codex_mcp", "error/bridge_rejected");
        Assert.Empty(bridge.Events);
        Assert.Equal("bridge_rejected", (await Entry(desktop, "codex_mcp")).Str("detail"));
    }
}

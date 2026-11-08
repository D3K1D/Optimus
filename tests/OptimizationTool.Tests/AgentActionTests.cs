using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Tests;

/// <summary>M4 agent tools: request_action (never approves) and get_operation by planId, with fake handlers only.</summary>
public class AgentActionTests
{
    private static async Task<(TestHost Host, FakePc Pc, Client C, string ReportId)> Start()
    {
        var pc = new FakePc();
        var host = TestHost.StartActions([new FakeActionModule(new FakeSchemeHandler(pc), new FakeScanHandler(pc))],
            FakeAudit.Modules(new FakeWorld(), new CollectorOptions(), sampler: false), agent: AgentSetup.Options(null, new FakeProvider("anthropic_api")));
        var c = await host.ConnectAsync(minor: 3);
        var reportId = await AgentSetup.Report(c);
        await AgentSetup.SetKey(c);
        return (host, pc, c, reportId);
    }

    private static JsonObject Request(string actionId, string? scheme = null)
    {
        var a = new JsonObject { ["actionId"] = actionId };
        if (scheme is not null) a["parameters"] = new JsonObject { ["scheme"] = scheme };
        return a;
    }

    private static JsonObject ChangesGrant(bool allowed)
    {
        var g = AgentSetup.Grant([]);
        g["canRequestChanges"] = allowed;
        return g;
    }

    [Fact]
    public async Task RequestAction_NeedsTheGrant_AllowsOnePendingPlan_AndNeverApproves()
    {
        var (host, pc, c, reportId) = await Start();
        await using var _ = host;
        var agent = host.Server.Engine.Agent;

        var plain = (await AgentSetup.StartTask(c, reportId, grant: ChangesGrant(false))).Str("taskId");
        Assert.Equal("not_allowed", (await agent.CallToolAsync(plain, "request_action", Request("power.select_scheme", "balanced"), "in_app", null, default))["error"].Str("code"));
        Assert.DoesNotContain(Diagnostics.Tools(Grant.FromJson(ChangesGrant(false).Also(g => g["limits"] = new Limits().ToJson()).Also(g => g["idleExpirySeconds"] = 900))), t => t.Name == "request_action");

        var taskId = (await AgentSetup.StartTask(c, reportId, grant: ChangesGrant(true))).Str("taskId");
        var task = await c.Call("agent.get", new JsonObject { ["taskId"] = taskId });
        Assert.True((bool)task["grant"]!["canRequestChanges"]!);

        var requested = await agent.CallToolAsync(taskId, "request_action", Request("power.select_scheme", "balanced"), "in_app", null, default);
        Assert.Equal("awaiting_user", requested.Str("status"));
        Assert.Equal("Change power plan", requested["summary"].Str("action"));
        Assert.DoesNotContain("balanced", requested.ToJsonString()); // row values stay in the app's approval dialog
        var planId = requested.Str("planId");
        var plan = await c.Call("action.get", new JsonObject { ["planId"] = planId });
        Assert.Equal("in_app", plan["requestedBy"].Str("kind"));
        Assert.Equal(taskId, plan["requestedBy"].Str("taskId"));

        Assert.Equal("not_allowed", (await agent.CallToolAsync(taskId, "request_action", Request("defender.quick_scan"), "in_app", null, default))["error"].Str("code"));
        Assert.Equal("invalid_arguments", (await agent.CallToolAsync(taskId, "get_operation",
            new JsonObject { ["planId"] = planId, ["operationId"] = "op-1" }, "in_app", null, default))["error"].Str("code"));
        Assert.Equal("invalid_arguments", (await agent.CallToolAsync(taskId, "get_operation", new JsonObject(), "in_app", null, default))["error"].Str("code"));
        Assert.Equal("awaiting_user", (await agent.CallToolAsync(taskId, "get_operation", new JsonObject { ["planId"] = planId }, "in_app", null, default)).Str("status"));
        Assert.Equal(0, pc.Writes); // requesting never approves

        // Stopping the task cancels its plan that is still waiting for the user.
        await c.Call("agent.stop", new JsonObject { ["taskId"] = taskId });
        Assert.Equal("cancelled", (await c.Call("action.get", new JsonObject { ["planId"] = planId })).Str("status"));
        Assert.Contains(c.Events, e => e.Str("type") == "action.plan" && e["payload"].Str("planId") == planId && e["payload"].Str("status") == "cancelled");
        Assert.Contains(c.Events, e => e.Str("stream") == "task:" + taskId && e.Str("type") == "agent.tool" && e["payload"].Str("tool") == "request_action");
        Assert.Equal(0, pc.Writes);
    }

    /// <summary>A handler whose target resolution waits for the test (the window in which the user stops the task).</summary>
    private sealed class GatedHandler(IActionHandler inner) : IActionHandler
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ActionId => inner.ActionId;
        public string Version => inner.Version;
        public string Label => inner.Label;
        public string Privilege => inner.Privilege;
        public bool SupportsUndo => inner.SupportsUndo;
        public string Kind => inner.Kind;
        public async Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            return await inner.ResolveAsync(request, ct).ConfigureAwait(false);
        }
        public Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct) => inner.ReadTargetAsync(target, ct);
        public Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct) => inner.ExecuteAsync(execution, progress, ct);
        public ActionRequest? UndoRequest(JsonObject target, JsonObject before) => inner.UndoRequest(target, before);
    }

    [Fact]
    public async Task StoppingATask_WhileItsRequestResolves_LeavesNoApprovablePlan()
    {
        var pc = new FakePc();
        var gated = new GatedHandler(new FakeSchemeHandler(pc));
        await using var host = TestHost.StartActions([new FakeActionModule(gated, new FakeScanHandler(pc))],
            FakeAudit.Modules(new FakeWorld(), new CollectorOptions(), sampler: false), agent: AgentSetup.Options(null, new FakeProvider("anthropic_api")));
        var c = await host.ConnectAsync(minor: 3);
        var reportId = await AgentSetup.Report(c);
        await AgentSetup.SetKey(c);
        var taskId = (await AgentSetup.StartTask(c, reportId, grant: ChangesGrant(true))).Str("taskId");

        var call = host.Server.Engine.Agent.CallToolAsync(taskId, "request_action", Request("power.select_scheme", "balanced"), "in_app", null, default);
        await gated.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await c.Call("agent.stop", new JsonObject { ["taskId"] = taskId });
        gated.Release.SetResult();
        var result = await call.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("revoked", result["error"].Str("code"));
        var plans = host.Server.Engine.Store.Read(db => db.Query("SELECT status FROM action_plans WHERE task_id = $t", r => r.GetString(0), ("$t", taskId)));
        Assert.Empty(plans);
        Assert.DoesNotContain(c.Events, e => e.Str("type") == "action.plan" && e["payload"].Str("status") == "awaiting_user");
    }

    [Fact]
    public async Task StoppingATask_WhileItsChangeRuns_LetsTheOperationFinish_AndKeepsTheReceiptLocal()
    {
        var (host, pc, c, reportId) = await Start();
        await using var _ = host;
        var engine = host.Server.Engine;
        var ctx = new CallContext("c-bridge", "agent", Environment.ProcessId);
        var pair = await engine.InvokeAsync("agent.pair", new JsonObject { ["client"] = "claude_code" }, ctx);
        await c.Call("agent.approvePairing", new JsonObject { ["pairingId"] = pair.Str("pairingId"), ["approve"] = true });
        var taskId = (await AgentSetup.StartTask(c, reportId, "claude_code_mcp", ChangesGrant(true), pair.Str("pairingId"))).Str("taskId");

        var requested = await engine.InvokeAsync("tool.call", new JsonObject { ["name"] = "request_action", ["arguments"] = Request("defender.quick_scan") }, ctx);
        var plan = await c.Call("action.get", new JsonObject { ["planId"] = requested.Str("planId") });
        Assert.Equal("claude_code", plan["requestedBy"].Str("kind"));
        var op = await Act.Approve(c, plan); // only the app approves
        await c.WaitForEvent(e => e.Str("type") == "operation.state" && e["payload"].Str("progress") == "Scanning · quick");

        await c.Call("agent.stop", new JsonObject { ["taskId"] = taskId });
        pc.ProviderGate.SetResult();
        await c.WaitForEvent(e => e.Str("type") == "operation.state" && e["payload"].Str("state") == "verified");
        var receipt = await c.Call("operation.get", new JsonObject { ["operationId"] = op.Str("operationId") });
        Assert.Equal("verified", receipt.Str("state"));
        Assert.NotNull(receipt["receipt"]);
        var after = (JsonObject)(await engine.InvokeAsync("tool.call", new JsonObject { ["name"] = "get_operation", ["arguments"] = new JsonObject { ["planId"] = plan.Str("planId") } }, ctx))!;
        Assert.Equal("revoked", after["error"].Str("code")); // the stopped task sees nothing more
        Assert.Equal(1, pc.ProviderRuns);
    }

    [Fact]
    public async Task GetOperation_ByPlanId_ReportsTheReceipt()
    {
        var (host, pc, c, reportId) = await Start();
        await using var _ = host;
        var agent = host.Server.Engine.Agent;
        var taskId = (await AgentSetup.StartTask(c, reportId, grant: ChangesGrant(true))).Str("taskId");
        var requested = await agent.CallToolAsync(taskId, "request_action", Request("power.select_scheme", "balanced"), "in_app", null, default);
        await Act.Approve(c, await c.Call("action.get", new JsonObject { ["planId"] = requested.Str("planId") }));
        var status = await agent.CallToolAsync(taskId, "get_operation", new JsonObject { ["planId"] = requested.Str("planId") }, "in_app", null, default);
        Assert.Equal("verified", status.Str("status"));
        Assert.Equal("verified", status["operation"].Str("state"));
        Assert.NotNull(status["operation"]!["receipt"]);
        SchemaTests.AssertValid("tool-result", status);
        SchemaTests.AssertValid("tool-result", requested);
        Assert.Equal(1, pc.Writes);
    }
}

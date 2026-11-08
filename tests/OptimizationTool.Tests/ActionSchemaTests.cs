using System.Text.Json.Nodes;
using OptimizationTool.Engine;

namespace OptimizationTool.Tests;

/// <summary>Live M4 host output validated against action-plan, operation and event schemas.</summary>
public class ActionSchemaTests
{
    [Fact]
    public async Task ActionOutput_MatchesSchemas()
    {
        var pc = new FakePc();
        var uac = new CancelledUac();
        await using var host = TestHost.StartActions([new FakeActionModule(new FakeSchemeHandler(pc), new FakeScanHandler(pc), new FakeFirewallHandler(uac))],
            elevation: uac, enableFirewall: false);
        var c = await host.ConnectAsync(minor: 3);

        var plan = await Act.Preview(c);
        SchemaTests.AssertValid("action-plan", plan);
        SchemaTests.AssertValid("action-plan", await Act.Preview(c, "firewall.enable_public", scheme: null)); // ineligible (disabled)
        var op = await Act.Approve(c, plan);
        SchemaTests.AssertValid("operation", op);
        SchemaTests.AssertValid("action-plan", await c.Call("action.get", new JsonObject { ["planId"] = plan.Str("planId") }));
        SchemaTests.AssertValid("action-plan", await c.Call("action.undo", new JsonObject { ["idempotencyKey"] = "u", ["operationId"] = op.Str("operationId") }));

        var scan = await Act.Approve(c, await Act.Preview(c, "defender.quick_scan", scheme: null));
        SchemaTests.AssertValid("operation", scan);
        await c.WaitForEvent(e => e.Str("type") == "operation.state" && e["payload"].Str("progress") is { Length: > 0 });
        pc.ProviderGate.SetResult();
        await c.WaitForEvent(e => e.Str("type") == "operation.state" && e["payload"].Str("operationId") == scan.Str("operationId") && e["payload"].Str("state") == "verified");

        foreach (var o in (await c.Call("operation.list"))["operations"]!.AsArray()) SchemaTests.AssertValid("operation", o);
        foreach (var e in c.Events) SchemaTests.AssertValid("event", e);
        Assert.Contains(c.Events, e => e.Str("type") == "action.plan");
        Assert.Contains(c.Events, e => e.Str("type") == "operation.state");
    }
}

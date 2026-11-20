using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Transport;

namespace OptimizationTool.Tests;

/// <summary>followup.front (design v55, "Show" on the new check-up cards chip): named cards move to the front of the undecided queue, in order.</summary>
public sealed class FollowupFrontTests
{
    [Fact]
    public async Task Front_moves_the_named_cards_first_in_order_and_ignores_unknown_and_decided_ones()
    {
        await using var host = TestHost.StartAgent(AgentSetup.Options(null, new FakeProvider("anthropic_api")));
        var c = await host.ConnectAsync(minor: 5);
        var reportId = await AgentSetup.Report(c);
        var view = await c.Call("followup.get", new JsonObject { ["reportId"] = reportId });
        var decided = view["card"].Str("id");
        view = await c.Call("followup.respond", Rpc.Respond(reportId, (long)view["revision"]!, "card", decided, option: "ignore"));
        var firstPending = view["card"].Str("id");
        string a1 = "", a2 = "";
        host.Server.Engine.Store.Write(db =>
        {
            a1 = Followups.AddAiCard(db, reportId, "task-0123456789abcdef", "First new", "Because.", "diagnostics", [], [], "Claude")!;
            a2 = Followups.AddAiCard(db, reportId, "task-0123456789abcdef", "Second new", "Because.", "diagnostics", [], [], "Claude")!;
        });
        var before = await c.Call("followup.get", new JsonObject { ["reportId"] = reportId });

        var moved = await c.Call("followup.front", new JsonObject { ["reportId"] = reportId, ["cardIds"] = new JsonArray(a2, decided, "card-0000000000000000", a1, a2) });
        SchemaTests.AssertValid("followup", moved);
        Assert.Equal([a2, a1, firstPending], new[] { moved["card"].Str("id") }.Concat(moved["upcoming"]!.AsArray().Select(x => x.Str("id"))));
        Assert.Equal(before["pendingCount"]!.GetValue<long>(), moved["pendingCount"]!.GetValue<long>());
        Assert.Equal(before["decisions"]!.ToJsonString(), moved["decisions"]!.ToJsonString()); // the decided card stays decided
        Assert.Equal((long)before["revision"]! + 1, (long)moved["revision"]!);
        Assert.Equal(moved.ToJsonString(), (await c.Call("followup.get", new JsonObject { ["reportId"] = reportId })).ToJsonString()); // persisted

        // Nothing to move (already in front, or nothing known): the same view, revision unchanged.
        var same = await c.Call("followup.front", new JsonObject { ["reportId"] = reportId, ["cardIds"] = new JsonArray(a2, a1) });
        Assert.Equal(moved.ToJsonString(), same.ToJsonString());

        foreach (var bad in new JsonNode?[] { null, new JsonArray(), new JsonArray(Enumerable.Range(0, 9).Select(i => (JsonNode?)$"card-{i}").ToArray()), new JsonArray(1) })
        {
            var p = new JsonObject { ["reportId"] = reportId };
            if (bad is not null) p["cardIds"] = bad;
            Assert.Equal(ErrorCodes.InvalidParams, (await c.Fails("followup.front", p)).Code);
        }
        Assert.Equal(ErrorCodes.NotFound, (await c.Fails("followup.front", new JsonObject { ["reportId"] = "rpt-missing", ["cardIds"] = new JsonArray(a1) })).Code);
        Assert.Contains("followup.front", AuditEngine.DesktopMethods);
    }
}

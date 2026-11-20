using System.Text.Json.Nodes;
using OptimizationTool.Engine;

namespace OptimizationTool.Tests;

/// <summary>The renderer's card views (followup.get): stored internal fields, the m9 §5.5 taint marks included, never leave the host.</summary>
public sealed class FollowupViewTests
{
    [Fact]
    public async Task CardViews_CarryNoTaintMarks_OrOtherInternalFields()
    {
        await using var host = TestHost.StartAgent(AgentSetup.Options(null, new FakeProvider("anthropic_api")));
        var c = await host.ConnectAsync(minor: 5);
        var reportId = await AgentSetup.Report(c);
        var first = (await c.Call("followup.get", new JsonObject { ["reportId"] = reportId }))["card"].Str("id");
        host.Server.Engine.Store.Write(db =>
        {
            Assert.True(Followups.NoteCard(db, reportId, first, "A web-tainted note.", "task-0123456789abcdef", "Claude", writerTainted: true));
            var aiCard = Followups.AddAiCard(db, reportId, "task-0123456789abcdef", "Check this", "Because.", "performance", [], [], "Claude", writerTainted: true)!;
            // Bring the AI card into view (it is appended at the end of the queue).
            var state = (JsonObject)Engine.Json.Parse((string)db.Scalar("SELECT state FROM followups WHERE report_id = $r", ("$r", reportId))!)!;
            var queue = state["queue"]!.AsArray().Select(n => n!.GetValue<string>()).Where(id => id != aiCard && id != first).ToList();
            state["queue"] = new JsonArray([.. new[] { first, aiCard }.Concat(queue).Select(id => (JsonNode?)id)]);
            db.Exec("UPDATE followups SET state = $s WHERE report_id = $r", ("$s", Engine.Json.Write(state)), ("$r", reportId));
        });

        var view = await c.Call("followup.get", new JsonObject { ["reportId"] = reportId });
        SchemaTests.AssertValid("followup", view);
        var cards = new[] { view["card"]! }.Concat(view["upcoming"]!.AsArray().Select(n => n!)).Select(n => n.AsObject()).ToList();
        Assert.Contains(cards, x => x.Str("note") == "A web-tainted note.");
        Assert.Contains(cards, x => x.Str("origin") == "ai");
        foreach (var card in cards)
            foreach (var field in Followups.Internal)
                Assert.False(card.ContainsKey(field), $"{field} reached the renderer");
        Assert.DoesNotContain("Tainted", view.ToJsonString(), StringComparison.Ordinal);
        // What the UI does need stays: the card's action and its parameters (when the rule chose any), and who wrote a note.
        Assert.All(cards, x => Assert.True(x.ContainsKey("actionId")));
        Assert.Equal("Claude", cards.Single(x => x.Str("note") is not null).Str("noteBy"));
    }
}

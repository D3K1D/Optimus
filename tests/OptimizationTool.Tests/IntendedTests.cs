using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Tests;

/// <summary>Protocol v1.4 (§10): intended findings, up to three visible cards, user context for agents.</summary>
public class IntendedTests
{
    private static (TestHost Host, FakeWorld World) Start(FakeProvider? provider = null)
    {
        var world = new FakeWorld { FreeGiB = 10 };
        var modules = FakeAudit.Modules(world, new CollectorOptions(), sampler: false);
        return (TestHost.StartAgent(AgentSetup.Options(null, provider ?? new FakeProvider("anthropic_api")), modules), world);
    }

    private static async Task<(JsonObject Report, JsonObject Followup)> Scan(Client c)
    {
        var (scan, _) = await c.ScanAsync("audit.full");
        var report = await c.Call("report.get", new JsonObject { ["reportId"] = scan.Str("reportId") });
        return (report, await c.Call("followup.get", new JsonObject { ["reportId"] = scan.Str("reportId") }));
    }

    private static JsonObject Finding(JsonObject report, string ruleId) => report["findings"]!.AsArray().Single(f => f.Str("ruleId") == ruleId)!.AsObject();

    [Fact]
    public async Task Intended_IsAFindingScopedDisposition_ThatCarriesOverAndRestores()
    {
        var (host, world) = Start();
        await using var _ = host;
        var c = await host.ConnectAsync(minor: 4);
        await Rpc.OnboardLocal(c, uses: [], goals: []);
        var (r1, _) = await Scan(c);
        var fw = Finding(r1, "fw.public_off");
        JsonObject Set(string key, JsonObject finding, string scope, string? expires = null) => new()
        {
            ["idempotencyKey"] = key, ["reportId"] = r1.Str("reportId"), ["findingId"] = finding.Str("id"), ["status"] = "intended", ["scope"] = scope,
            ["expiresAt"] = expires,
        };
        Assert.Equal("scope", (await c.Fails("finding.setDisposition", Set("i0", fw, "rule"))).ErrorData.Str("field"));
        Assert.Equal("expiresAt", (await c.Fails("finding.setDisposition", Set("i1", fw, "finding", "2026-12-01T00:00:00Z"))).ErrorData.Str("field"));

        var f = await c.Call("finding.setDisposition", Set("i2", fw, "finding"));
        Assert.Equal("intended", f["decisions"]!.AsArray().Single(d => d.Str("findingId") == fw.Str("id")).Str("status"));
        Assert.NotEqual(fw.Str("id"), f["card"].Str("findingId"));
        SchemaTests.AssertValid("followup", f);

        var (r2, f2) = await Scan(c);
        Assert.Equal("intended", Finding(r2, "fw.public_off")["disposition"].Str("status"));
        Assert.Contains(f2["decisions"]!.AsArray(), d => d.Str("status") == "intended");
        SchemaTests.AssertValid("report", r2);

        // Clearing restores it to the queue, at the front.
        f2 = await c.Call("finding.clearDisposition", new JsonObject
        {
            ["idempotencyKey"] = "c1", ["reportId"] = r2.Str("reportId"), ["findingId"] = Finding(r2, "fw.public_off").Str("id"),
        });
        Assert.Equal(Finding(r2, "fw.public_off").Str("id"), f2["card"].Str("findingId"));

        // Severity rise reopens an intended finding (storage medium → high).
        var storage = Finding(r2, "storage.system_low");
        await c.Call("finding.setDisposition", new JsonObject
        {
            ["idempotencyKey"] = "i3", ["reportId"] = r2.Str("reportId"), ["findingId"] = storage.Str("id"), ["status"] = "intended", ["scope"] = "finding",
        });
        world.FreeGiB = 2;
        var (r3, _) = await Scan(c);
        Assert.Null(Finding(r3, "storage.system_low")["disposition"]);
    }

    [Fact]
    public async Task ReviewedRulesOfferIntended_AndAnyOfThreeVisibleCardsCanBeAnswered()
    {
        var (host, _) = Start();
        await using var _h = host;
        var c = await host.ConnectAsync(minor: 4);
        await Rpc.OnboardLocal(c, uses: [], goals: []);
        var (report, f) = await Scan(c);
        var reportId = report.Str("reportId");

        var visible = new[] { f["card"]! }.Concat(f["upcoming"]!.AsArray()).Select(x => x!.AsObject()).ToList();
        Assert.Equal(3, visible.Count);
        Assert.Equal(2, f["upcoming"]!.AsArray().Count);
        foreach (var card in visible)
        {
            var ruleId = report["findings"]!.AsArray().Single(x => x.Str("id") == card.Str("findingId")).Str("ruleId");
            var options = card["options"]!.AsArray().Select(o => (string)o!).ToList();
            Assert.Equal(Followups.IntendedRules.Contains(ruleId), options.Contains("Intended"));
        }
        SchemaTests.AssertValid("followup", f);

        // Answer the third visible card directly: "intended" maps to the cross-scan disposition.
        var third = visible[2];
        f = await c.Call("followup.respond", Rpc.Respond(reportId, (long)f["revision"]!, "card", third.Str("id"), option: "intended"));
        Assert.Equal("intended", f["decisions"]!.AsArray().Single(d => d.Str("cardId") == third.Str("id")).Str("status"));
        var reread = await c.Call("report.get", new JsonObject { ["reportId"] = reportId });
        Assert.Equal("intended", reread["findings"]!.AsArray().Single(x => x.Str("id") == third.Str("findingId"))!["disposition"].Str("status"));

        // The second visible card can be ignored; a card beyond the three cannot.
        var second = f["upcoming"]![0]!;
        f = await c.Call("followup.respond", Rpc.Respond(reportId, (long)f["revision"]!, "card", second.Str("id"), option: "ignore"));
        Assert.DoesNotContain(new[] { f["card"]! }.Concat(f["upcoming"]!.AsArray()), x => x.Str("id") == second.Str("id"));
        var all = report["findings"]!.AsArray().Select(x => Ids.Card(reportId, x.Str("id"))).ToList();
        var visibleNow = new[] { f["card"]! }.Concat(f["upcoming"]!.AsArray()).Select(x => x.Str("id")).ToHashSet();
        var decided = f["decisions"]!.AsArray().Select(d => d.Str("cardId")).ToHashSet();
        var hidden = all.FirstOrDefault(id => !visibleNow.Contains(id) && !decided.Contains(id));
        if (hidden is not null)
            Assert.Equal("card_not_visible", (await c.Fails("followup.respond", Rpc.Respond(reportId, (long)f["revision"]!, "card", hidden, option: "ignore"))).ErrorData.Str("reason"));
    }

    [Fact]
    public async Task Agents_SeeUsageAndGoalLabels_AndIntendedFindings_NeverCustomText()
    {
        var provider = new FakeProvider("anthropic_api", FakeProvider.Step(FakeProvider.Say("Noted.")));
        var (host, _) = Start(provider);
        await using var _h = host;
        var c = await host.ConnectAsync(minor: 4);
        var current = await c.Call("onboarding.get");
        await c.Call("onboarding.save", new JsonObject
        {
            ["expectedRevision"] = (long)current["revision"]!,
            ["patch"] = new JsonObject
            {
                ["uses"] = new JsonArray("Gaming", "Hosting / development", "My secret lab"), ["customUse"] = "private project name",
                ["goals"] = new JsonArray("Improve PC performance"), ["customGoal"] = "secret goal", ["provider"] = "local", ["step"] = "complete",
            },
        });
        var (report, _) = await Scan(c);
        await c.Call("finding.setDisposition", new JsonObject
        {
            ["idempotencyKey"] = "i", ["reportId"] = report.Str("reportId"), ["findingId"] = Finding(report, "fw.public_off").Str("id"),
            ["status"] = "intended", ["scope"] = "finding",
        });
        await AgentSetup.SetKey(c);
        var taskId = (await AgentSetup.StartTask(c, report.Str("reportId"))).Str("taskId");

        var caps = await host.Server.Engine.Agent.CallToolAsync(taskId, "list_capabilities", null, "in_app", null, default);
        Assert.Equal(["Gaming", "Hosting / development"], caps["userContext"]!["uses"]!.AsArray().Select(x => (string)x!));
        Assert.Equal(["Improve PC performance"], caps["userContext"]!["goals"]!.AsArray().Select(x => (string)x!));
        var findings = await host.Server.Engine.Agent.CallToolAsync(taskId, "get_findings", null, "in_app", null, default);
        Assert.Equal("intended", findings["findings"]!.AsArray().Single(x => x.Str("ruleId") == "fw.public_off")!["disposition"].Str("status"));

        await AgentSetup.Ask(c, taskId);
        await AgentSetup.TaskEvent(c, taskId, "agent.message");
        var request = provider.Requests.Single();
        Assert.Equal("prompt-v7", AgentService.SystemPromptVersion);
        Assert.Contains("intended", request.System);
        var context = ((UserText)request.Messages[0]).Text;
        Assert.Contains("Gaming, Hosting / development", context);
        Assert.DoesNotContain("Findings in scope", context); // m5 §4: findings come through get_findings, not the prompt
        foreach (var secret in new[] { "My secret lab", "private project name", "secret goal" })
        {
            Assert.DoesNotContain(secret, context);
            Assert.DoesNotContain(secret, caps.ToJsonString());
        }
    }
}

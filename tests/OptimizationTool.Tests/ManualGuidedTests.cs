using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Transport;

namespace OptimizationTool.Tests;

/// <summary>
/// Design v60: manual.open on a finding with guide lines but no Windows page creates a guided task with no page
/// (target {type: manual, id: the finding ID, label: its title}, rows [], no settingsUri/manualUri/settingsPath,
/// resources [manual:&lt;findingId&gt;]); manual.recheck works as for any manual task. Neither → -32013 no_settings.
/// </summary>
public class ManualGuidedTests
{
    private sealed class Language : IDisplayLanguage
    {
        public bool IsEnglish => true;
    }

    /// <summary>Two findings while <paramref name="present"/> holds: one with guide lines only, one with neither guide nor page.</summary>
    private static IAuditModule Guided(Func<bool> present) =>
        new FakeModule("guided", [new FakeCollector("fake.bios", "performance", _ => Task.FromResult(FakeCollector.One("bios.virtualization",
            new JsonObject { ["on"] = false }, "Virtualization is off.")))],
            [new FakeRule("bios.guided", "performance", ["fake.bios"], ctx =>
            {
                if (!present()) return RuleResult.Clear();
                var e = ctx.Get("bios.virtualization")!;
                return RuleResult.Found(
                    new FindingDraft(e.Subject, "Virtualization is off in the BIOS", "low", "high", [e.Id],
                        new CardDraft("Turn it on in the BIOS?", ["Ignore"], Guide: ["Restart into the BIOS.", "Turn on SVM or VT-x."])),
                    new FindingDraft(e.Subject + ".bare", "Nothing to guide", "low", "high", [e.Id], new CardDraft("Nothing here.", ["Ignore"], Guide: ["  "])));
            })]);

    /// <summary>Design v63 (ReportsMore README, "Engine sentence for a guided task"), character for character.</summary>
    private const string PageWhat = "Opens the right page in Windows Settings, where you make the change yourself. Optimus checks it afterwards.";
    private const string NoPageWhat = "You make this change yourself, outside Windows Settings. Optimus checks it afterwards.";

    [Fact]
    public async Task The_guided_task_what_says_whether_a_Windows_page_opens()
    {
        await using var host = TestHost.StartActions([], [Guided(() => true), Paged()]);
        var c = await host.ConnectAsync(minor: 3);
        var (scan, _) = await c.ScanAsync("audit.full");
        var report = await c.Call("report.get", new JsonObject { ["reportId"] = scan.Str("reportId") });
        async Task<JsonObject> Open(string title) => await c.Call("action.preview", new JsonObject
        {
            ["idempotencyKey"] = Guid.NewGuid().ToString(), ["actionId"] = ActionService.ManualOpen,
            ["findingId"] = report["findings"]!.AsArray().Single(f => f.Str("title") == title).Str("id"),
        });
        var page = await Open("Startup apps");
        Assert.Equal("settings", page["target"].Str("type"));
        Assert.Equal(PageWhat, page.Str("what"));
        Assert.Equal(NoPageWhat, (await Open("Virtualization is off in the BIOS")).Str("what"));
        // The catalog (and the app guide) describe manual.open with its Windows page.
        var catalog = (await c.Call("action.catalog"))["actions"]!.AsArray();
        Assert.Equal(PageWhat, catalog.Single(a => a.Str("actionId") == ActionService.ManualOpen).Str("what"));
    }

    private static IAuditModule Paged() =>
        new FakeModule("paged", [new FakeCollector("fake.startup", "performance", _ => Task.FromResult(FakeCollector.One("startup.count",
            new JsonObject { ["n"] = 9 }, "Nine startup apps.")))],
            [new FakeRule("startup.paged", "performance", ["fake.startup"], ctx =>
            {
                var e = ctx.Get("startup.count")!;
                return RuleResult.Found(new FindingDraft(e.Subject, "Startup apps", "low", "high", [e.Id],
                    new CardDraft("Trim them?", ["Ignore"], SettingsUri: "ms-settings:startupapps")));
            })]);

    [Fact]
    public async Task A_finding_with_guide_lines_and_no_page_gets_a_guided_task_and_rechecks()
    {
        var present = true;
        await using var host = TestHost.StartActions([], [Guided(() => present)], displayLanguage: new Language());
        var c = await host.ConnectAsync(minor: 3);
        var (scan, _) = await c.ScanAsync("audit.full");
        var report = await c.Call("report.get", new JsonObject { ["reportId"] = scan.Str("reportId") });
        var findings = report["findings"]!.AsArray().Select(x => x!.AsObject()).Where(x => x.Str("ruleId") == "bios.guided").ToList();
        var guided = findings.Single(f => f.Str("title") == "Virtualization is off in the BIOS");
        var bare = findings.Single(f => f.Str("title") == "Nothing to guide");
        Assert.Null(guided["settingsUri"]);

        var plan = await c.Call("action.preview", new JsonObject { ["idempotencyKey"] = Guid.NewGuid().ToString(), ["actionId"] = ActionService.ManualOpen, ["findingId"] = guided.Str("id") });
        SchemaTests.AssertValid("action-plan", plan);
        Assert.Equal("manual", plan.Str("kind"));
        Assert.Equal("awaiting_user", plan.Str("status"));
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["type"] = "manual", ["id"] = guided.Str("id"), ["label"] = guided.Str("title") }, plan["target"]));
        Assert.Empty(plan["rows"]!.AsArray());
        Assert.Equal(NoPageWhat, plan.Str("what")); // design v63
        Assert.Equal(["manual:" + guided.Str("id")], plan["resources"]!.AsArray().Select(r => r!.GetValue<string>()));
        var text = plan.ToJsonString();
        foreach (var field in new[] { "settingsUri", "manualUri", "settingsPath", "ms-settings:" })
            Assert.DoesNotContain(field == "ms-settings:" ? field : $"\"{field}\":\"", text);

        // Not approvable (a manual step), and Check again works as for any manual task.
        Assert.Equal("manual", (await c.Fails("action.approve", Act.ApproveParams(plan, await Act.Nonce(c, plan.Str("planId"))))).ErrorData.Str("reason"));
        Assert.Equal("awaiting_rescan", (await c.Call("manual.recheck", new JsonObject { ["findingId"] = guided.Str("id") })).Str("status"));
        present = false;
        Assert.Equal("verified_by_rescan", (await c.Call("manual.recheck", new JsonObject { ["findingId"] = guided.Str("id") })).Str("status"));
        var followup = await c.Call("followup.get", new JsonObject { ["reportId"] = scan.Str("reportId") });
        Assert.Equal("verified", followup["decisions"]!.AsArray().Single(d => d.Str("findingId") == guided.Str("id")).Str("status"));

        // Neither guide lines (blank ones don't count) nor a page: refused as before.
        var refused = await c.Fails("action.preview", new JsonObject { ["idempotencyKey"] = Guid.NewGuid().ToString(), ["actionId"] = ActionService.ManualOpen, ["findingId"] = bare.Str("id") });
        Assert.Equal(ErrorCodes.InvalidState, refused.Code);
        Assert.Equal("no_settings", refused.ErrorData.Str("reason"));
    }
}

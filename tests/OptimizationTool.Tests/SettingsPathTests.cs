using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Transport;
using OptimizationTool.Windows;

namespace OptimizationTool.Tests;

/// <summary>
/// Design v59: settingsPath (the page as Windows names it in English) beside every settingsUri / manualUri the host sends —
/// report findings, follow-up cards, a plan's ineligible, the manual.open plan's target, the operation and its
/// operation.state event — only when Windows' display language is English. Another language (or no language seam): absent,
/// never null. A page without a designed path gets none. The one table (SettingsPages) equals the fixture's SETTINGS_PATHS.
/// </summary>
public partial class SettingsPathTests
{
    private sealed class Language(bool english) : IDisplayLanguage
    {
        public bool IsEnglish => english;
    }

    /// <summary>The 13 designed pages and two without a path.</summary>
    private static readonly string[] Uris = [.. SettingsPages.All.Select(p => p.Uri), "ms-settings:", "ms-settings:network"];

    /// <summary>parameters {uri, mode}: "ineligible" refuses with that manualUri; otherwise it runs and fails with it as its manual route.</summary>
    private sealed class RouteHandler : IActionHandler
    {
        public const string Id = "test.route";
        /// <summary>Set after a preview: every resolution refuses with its uri as the manual route (the plan goes stale).</summary>
        public bool Refuse { get; set; }
        public string ActionId => Id;
        public string Version => "1.0.0";
        public string Label => "Route";
        public string Privilege => "none";
        public bool SupportsUndo => false;
        public string Kind => "setting";
        public string Group => "power";
        public string What => "A test route.";
        public IReadOnlyList<string> Flags => [];
        public JsonObject? ParameterSchema => null;
        public bool TakesLease => false;

        public Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct)
        {
            var uri = request.Parameters!["uri"]!.GetValue<string>();
            if (Refuse || request.Parameters["mode"]?.GetValue<string>() == "ineligible")
                return Task.FromResult(ActionResolution.Ineligible("manual_only", "Change it in Settings", uri));
            return Task.FromResult(new ActionResolution(true, new JsonObject { ["type"] = "route", ["id"] = uri, ["label"] = "Route" },
                new JsonObject { ["v"] = 1 }, new JsonObject { ["v"] = 2 }, [new PlanRow("Route", "1", "2")], [], [], null));
        }

        public Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct) => Task.FromResult(new JsonObject { ["v"] = 1 });

        public Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct) =>
            Task.FromResult(new ActionOutcome("failed", new JsonObject { ["manualUri"] = execution.Target["id"]!.GetValue<string>() }, "Windows refused the change.", "write_failed"));

        public ActionRequest? UndoRequest(JsonObject target, JsonObject before) => null;
    }

    private static IAuditModule Pages()
    {
        var facts = Uris.Select((u, i) => new Fact($"page.{i}", "Synthetic source", "measured", new JsonObject { ["uri"] = u }, u, null)).ToList();
        return new FakeModule("pages", [new FakeCollector("fake.pages", "performance", _ => Task.FromResult(new CollectorResult(facts, new CoverageReceipt("complete", facts.Count, null))))],
            [new FakeRule("pages.each", "performance", ["fake.pages"], ctx => RuleResult.Found([.. Uris.Select((u, i) =>
            {
                var e = ctx.Get($"page.{i}")!;
                return new FindingDraft(e.Subject, $"Page {i}", "low", "high", [e.Id], new CardDraft("Open it?", ["Ignore"], SettingsUri: u));
            })]))]);
    }

    /// <summary>Every place the host sends a Settings link, for each URI: (uri, where, the object that carries it).</summary>
    private static async Task<List<(string Uri, string Where, JsonObject Carrier)>> EveryPlace(bool? english)
    {
        await using var host = TestHost.StartActions([new FakeActionModule(new RouteHandler())], [Pages()],
            displayLanguage: english is { } e ? new Language(e) : null);
        var c = await host.ConnectAsync(minor: 3);
        var (scan, _) = await c.ScanAsync("audit.full");
        var reportId = scan.Str("reportId");
        var report = await c.Call("report.get", new JsonObject { ["reportId"] = reportId });
        SchemaTests.AssertValid("report", report);
        var found = new List<(string, string, JsonObject)>();
        foreach (var f in report["findings"]!.AsArray().Select(x => x!.AsObject()).Where(x => x.Str("ruleId") == "pages.each"))
        {
            var uri = f.Str("settingsUri");
            found.Add((uri, "finding", f));
            var view = await c.Call("followup.front", new JsonObject { ["reportId"] = reportId, ["cardIds"] = new JsonArray(Ids.Card(reportId, f.Str("id"))) });
            SchemaTests.AssertValid("followup", view);
            var card = view["card"]!.AsObject();
            Assert.Equal(uri, card.Str("settingsUri"));
            found.Add((uri, "card", card));

            var manual = await c.Call("action.preview", new JsonObject { ["idempotencyKey"] = Guid.NewGuid().ToString(), ["actionId"] = ActionService.ManualOpen, ["findingId"] = f.Str("id") });
            SchemaTests.AssertValid("action-plan", manual);
            found.Add((uri, "manual.open target", manual["target"]!.AsObject()));

            var ineligible = await c.Call("action.preview", new JsonObject
            {
                ["idempotencyKey"] = Guid.NewGuid().ToString(), ["actionId"] = RouteHandler.Id, ["parameters"] = new JsonObject { ["uri"] = uri, ["mode"] = "ineligible" },
            });
            SchemaTests.AssertValid("action-plan", ineligible);
            Assert.Equal(uri, ineligible["ineligible"].Str("manualUri"));
            found.Add((uri, "plan ineligible", ineligible["ineligible"]!.AsObject()));

            var plan = await c.Call("action.preview", new JsonObject
            {
                ["idempotencyKey"] = Guid.NewGuid().ToString(), ["actionId"] = RouteHandler.Id, ["parameters"] = new JsonObject { ["uri"] = uri },
            });
            var op = await Act.Approve(c, plan);
            var ended = await c.WaitForEvent(ev => ev.Str("type") == "operation.state" && ev["payload"].Str("planId") == plan.Str("planId") && ev["payload"].Str("state") == "failed");
            SchemaTests.AssertValid("event", ended);
            Assert.Equal(uri, ended["payload"].Str("manualUri"));
            found.Add((uri, "operation.state event", ended["payload"]!.AsObject()));
            var done = await c.Call("operation.get", new JsonObject { ["operationId"] = op.Str("operationId") });
            SchemaTests.AssertValid("operation", done);
            Assert.Equal(uri, done.Str("manualUri"));
            found.Add((uri, "operation", done));
        }
        Assert.Equal(Uris.Length * 6, found.Count);
        return found;
    }

    [Fact]
    public async Task English_Windows_sends_the_path_beside_every_Settings_link()
    {
        foreach (var (uri, where, carrier) in await EveryPlace(english: true))
        {
            var path = SettingsPages.EnglishPath(uri);
            if (path is null) Assert.False(carrier.ContainsKey("settingsPath"), $"{uri} at {where}: no designed path, no field");
            else Assert.Equal(path, carrier["settingsPath"]?.GetValue<string>());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Another_language_or_none_sends_no_path_anywhere(bool? english)
    {
        foreach (var (uri, where, carrier) in await EveryPlace(english))
            Assert.False(carrier.ContainsKey("settingsPath"), $"{uri} at {where}");
    }

    private static FixStepRequest Route(string uri, string? mode = null) =>
        new(RouteHandler.Id, new JsonObject { ["uri"] = uri, ["mode"] = mode }, null, null);

    private static void AssertPath(JsonObject? ineligible, string uri, bool english, string where)
    {
        Assert.NotNull(ineligible);
        Assert.Equal(uri, ineligible!.Str("manualUri"));
        if (english) Assert.Equal(SettingsPages.EnglishPath(uri), ineligible["settingsPath"]?.GetValue<string>());
        else Assert.False(ineligible.ContainsKey("settingsPath"), where);
    }

    /// <summary>
    /// A fix plan's blocked step (the plan's ineligible names the first blocked step's manual route) and stale_plan, for a
    /// single plan and a fix plan: settingsPath beside manualUri on English Windows, absent otherwise.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_blocked_fix_step_and_a_stale_plan_carry_the_path(bool english)
    {
        var route = new RouteHandler();
        await using var host = TestHost.StartActions([new FakeActionModule(route)], displayLanguage: new Language(english));
        var c = await host.ConnectAsync(minor: 3);
        var actions = host.Server.Engine.Actions;
        string first = SettingsPages.All[0].Uri, second = SettingsPages.All[1].Uri;

        // A fix plan with a blocked second step.
        var blocked = await actions.CreateFixAsync("Two pages", "Both.", "Claude", [Route(first), Route(second, "ineligible")], null, RequestedBy.User);
        SchemaTests.AssertValid("action-plan", blocked);
        Assert.Equal("ineligible", blocked.Str("status"));
        AssertPath(blocked["ineligible"] as JsonObject, second, english, "blocked fix step");
        AssertPath((await c.Call("action.get", new JsonObject { ["planId"] = blocked.Str("planId") }))["ineligible"] as JsonObject, second, english, "blocked fix step, stored");

        // stale_plan: a single plan, then a fix plan, whose target refuses at approval with its manual route.
        var single = await c.Call("action.preview", new JsonObject
        {
            ["idempotencyKey"] = Guid.NewGuid().ToString(), ["actionId"] = RouteHandler.Id, ["parameters"] = new JsonObject { ["uri"] = first },
        });
        var fix = await actions.CreateFixAsync("Two pages", "Both.", "Claude", [Route(second), Route(first)], null, RequestedBy.User);
        Assert.Equal("awaiting_user", fix.Str("status"));
        route.Refuse = true;
        foreach (var (plan, uri, where) in new[] { (single, first, "single stale_plan"), (fix, second, "fix stale_plan") })
        {
            var stale = await c.Fails("action.approve", Act.ApproveParams(plan, await Act.Nonce(c, plan.Str("planId"))));
            Assert.Equal(ErrorCodes.StaleRevision, stale.Code);
            Assert.Equal("stale_plan", stale.ErrorData.Str("reason"));
            var current = stale.ErrorData!["current"]!.AsObject();
            SchemaTests.AssertValid("action-plan", current);
            Assert.Equal("stale_plan", current["ineligible"].Str("code"));
            AssertPath(current["ineligible"] as JsonObject, uri, english, where);
            AssertPath((await c.Call("action.get", new JsonObject { ["planId"] = plan.Str("planId") }))["ineligible"] as JsonObject, uri, english, where + ", stored");
        }
    }

    [Fact]
    public void A_language_seam_that_throws_sends_no_path()
    {
        Assert.Null(new SettingsPaths(new Throwing()).For("ms-settings:startupapps"));
        Assert.Equal("Settings › Apps › Startup", new SettingsPaths(new Language(true)).For("ms-settings:startupapps"));
    }

    private sealed class Throwing : IDisplayLanguage
    {
        public bool IsEnglish => throw new InvalidOperationException();
    }

    [Theory]
    [InlineData(0x0409, true)] // en-US
    [InlineData(0x0809, true)] // en-GB
    [InlineData(0x1009, true)] // en-CA
    [InlineData(0x0407, false)] // de-DE
    [InlineData(0x040C, false)] // fr-FR
    [InlineData(0x0411, false)] // ja-JP
    public void Production_reads_the_primary_language_of_the_Windows_display_language(int langId, bool english) =>
        Assert.Equal(english, WindowsDisplayLanguage.IsEnglishLangId((ushort)langId));

    [GeneratedRegex(@"var SETTINGS_PATHS = \{(?<body>.*?)\};", RegexOptions.Singleline)]
    private static partial Regex FixtureTable();

    [GeneratedRegex(@"'(?<uri>[^']+)':\s*'(?<path>[^']+)'")]
    private static partial Regex FixtureRow();

    /// <summary>The fixture's SETTINGS_PATHS (ui/js/adapter-fixture.js) is the host's table, row for row, in order.</summary>
    [Fact]
    public void The_fixture_table_is_the_host_table()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OptimizationTool.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var js = File.ReadAllText(Path.Combine(dir!.FullName, "ui", "js", "adapter-fixture.js"));
        var body = FixtureTable().Match(js);
        Assert.True(body.Success, "SETTINGS_PATHS not found in adapter-fixture.js");
        var rows = FixtureRow().Matches(body.Groups["body"].Value).Select(m => (m.Groups["uri"].Value, m.Groups["path"].Value)).ToList();
        Assert.Equal(SettingsPages.All.Select(p => (p.Uri, p.EnglishPath)), rows);
    }
}

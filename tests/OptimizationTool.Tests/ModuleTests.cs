using System.Text.Json.Nodes;
using OptimizationTool.Engine;

namespace OptimizationTool.Tests;

/// <summary>M2 engine wiring over fake IAuditModules (protocol §9, contracts/m2-collectors.md §6).</summary>
public class ModuleTests
{
    private static readonly TimeSpan ScanCoordinatorTick = TimeSpan.FromSeconds(2);
    private static readonly CollectorOptions Fast =new() { SampleWindow = TimeSpan.FromMilliseconds(80), SampleInterval = TimeSpan.FromMilliseconds(10) };

    private static (TestHost Host, FakeWorld World) Start(FakeWorld? world = null, ManualTime? time = null, CollectorOptions? options = null, TimeSpan? deadline = null)
    {
        world ??= new FakeWorld();
        options ??= Fast;
        return (TestHost.StartModules(FakeAudit.Modules(world, options), time: time, collectorOptions: options, scanDeadline: deadline), world);
    }

    private static async Task<(JsonObject Scan, JsonObject Report)> Scan(Client c, string intent = "audit.full", string? baseReportId = null)
    {
        var p = Rpc.ScanParams(intent, "Begin scan", Guid.NewGuid().ToString());
        if (baseReportId is not null) p["baseReportId"] = baseReportId;
        var started = await c.Call("scan.start", p);
        await c.WaitForScanFinished(started.Str("scanId"));
        var scan = await c.Call("scan.get", new JsonObject { ["scanId"] = started.Str("scanId") });
        return (scan, await c.Call("report.get", new JsonObject { ["reportId"] = scan.Str("reportId") }));
    }

    private static JsonObject FindingOf(JsonObject report, string ruleId) =>
        report["findings"]!.AsArray().Single(f => f.Str("ruleId") == ruleId)!.AsObject();

    private static JsonObject Category(JsonObject report, string id) =>
        report["categories"]!.AsArray().Single(c => c.Str("id") == id)!.AsObject();

    [Fact]
    public async Task Hello_AdvertisesLiveSource_AndAcceptsA10Client()
    {
        var (host, _) = Start();
        await using var _h = host;
        var c = await host.ConnectAsync(); // the test client says 1.0
        Assert.Equal(1, (int)c.Hello!["protocol"]!["major"]!);
        Assert.Equal(4, (int)c.Hello["protocol"]!["minor"]!);
        Assert.Contains("audit.windows", c.Hello["features"]!.AsArray().Select(x => (string)x!));
    }

    [Fact]
    public async Task RulesEvaluate_WithStableFingerprints_AndRecordedOutcomes()
    {
        var (host, _) = Start();
        await using var _h = host;
        var c = await host.ConnectAsync();
        var (scan1, r1) = await Scan(c);
        var (_, r2) = await Scan(c);

        var fw1 = FindingOf(r1, "fw.public_off");
        var fw2 = FindingOf(r2, "fw.public_off");
        Assert.Equal(Ids.Fingerprint("fw.public_off", "security.fw.public"), fw1.Str("fingerprint"));
        Assert.Equal(fw1.Str("fingerprint"), fw2.Str("fingerprint")); // stable across scans
        Assert.NotEqual(fw1.Str("id"), fw2.Str("id"));                 // finding IDs are scan-local
        Assert.Equal(Ids.Finding(scan1.Str("scanId"), "fw.public_off", fw1.Str("fingerprint")), fw1.Str("id"));
        Assert.Equal("security.fw.public", fw1.Str("subject"));
        Assert.Equal("windowsdefender://network", fw1.Str("settingsUri"));
        Assert.Empty(fw1["counterevidence"]!.AsArray());
        Assert.Null(fw1["disposition"]);

        var rules = r1["rules"]!.AsArray().ToDictionary(r => r.Str("id"), r => r.Str("status"));
        Assert.Equal("finding", rules["fw.public_off"]);
        Assert.Equal("clear", rules["av.active_threat"]);
        Assert.Equal("insufficient", rules["privacy.optional_diag"]);
        Assert.Equal("inapplicable", rules["start.suggestions_on"]);
        Assert.Null(r1["baseReportId"]);
        Assert.Equal(r1.Str("reportId"), r2.Str("baseReportId")); // previous report of the same intent
    }

    [Fact]
    public async Task Categories_DistinguishClear_Insufficient_AndNotPlanned()
    {
        var world = new FakeWorld { FreeGiB = 100 };
        var (host, _) = Start(world);
        await using var _h = host;
        var c = await host.ConnectAsync();
        var (_, full) = await Scan(c);

        var storage = Category(full, "storage");
        Assert.Equal("complete", storage.Str("status"));
        Assert.Equal(0, (int)storage["findings"]!);
        Assert.Equal(1, (int)storage["rules"]!["clear"]!);

        var privacy = Category(full, "privacy_telemetry");
        Assert.Equal("none", privacy.Str("status"));
        Assert.Equal(0, (int)privacy["checked"]!);
        Assert.Equal(1, (int)privacy["planned"]!);
        Assert.Equal(1, (int)privacy["rules"]!["insufficient"]!);

        var misc = Category(full, "misc"); // no misc collector; its rule reads a customization collector
        Assert.Equal("complete", misc.Str("status"));
        Assert.Equal(1, (int)misc["rules"]!["inapplicable"]!);
        Assert.DoesNotContain(full["categories"]!.AsArray(), x => x.Str("id") == "diagnostics"); // nothing to plan

        var (scan, targeted) = await Scan(c, "audit.storage");
        Assert.Equal(["storage"], targeted["categories"]!.AsArray().Select(x => x.Str("id")));
        Assert.Equal(["fake.vol"], scan["coverage"]!.AsArray().Select(x => x.Str("collectorId")));
        Assert.Equal(["storage.system_low"], targeted["rules"]!.AsArray().Select(x => x.Str("id")));
    }

    [Fact]
    public async Task Cards_ComeFromCardDrafts_WithGenericAskTemplate()
    {
        var (host, _) = Start();
        await using var _h = host;
        var c = await host.ConnectAsync();
        await Rpc.OnboardLocal(c, uses: [], goals: []);
        var (_, report) = await Scan(c);
        var f = await c.Call("followup.get", new JsonObject { ["reportId"] = report.Str("reportId") });
        var card = f["card"]!;
        Assert.Equal("security", card.Str("category"));
        Assert.Equal("windowsdefender://network", card.Str("settingsUri"));
        Assert.Equal(["View steps", "Ask about this", "Intended", "Ignore"], card["options"]!.AsArray().Select(x => (string)x!)); // fw.public_off is a reviewed rule (v1.4)
        Assert.Null(card["ruleId"]); // internal ordering fields are not on the wire
        f = await c.Call("followup.respond", Rpc.Respond(report.Str("reportId"), (long)f["revision"]!, "card", card.Str("id"), text: "Why?"));
        Assert.Equal("From the report: Public firewall is off.", f["card"]!["reply"].Str("text"));
    }

    [Fact]
    public async Task QueueOrder_FollowsM2Policy()
    {
        var world = new FakeWorld { ActiveThreat = true, FreeGiB = 10 };
        var (host, _) = Start(world);
        await using var _h = host;
        var c = await host.ConnectAsync();

        async Task<List<string>> Order()
        {
            var (_, report) = await Scan(c);
            var reportId = report.Str("reportId");
            var f = await c.Call("followup.get", new JsonObject { ["reportId"] = reportId });
            var rules = new List<string>();
            while (f["card"] is JsonObject card)
            {
                rules.Add(report["findings"]!.AsArray().Single(x => x.Str("id") == card.Str("findingId")).Str("ruleId"));
                f = await c.Call("followup.respond", Rpc.Respond(reportId, (long)f["revision"]!, "card", card.Str("id"), option: "done"));
            }
            return rules;
        }

        await Rpc.OnboardLocal(c, uses: [], goals: []);
        Assert.Equal(["av.active_threat", "fw.public_off", "storage.system_low", "display.below_max_refresh", "perf.sustained_cpu", "perf.uptime_long"], await Order());

        await Rpc.OnboardLocal(c, uses: [], goals: ["Improve PC performance"]);
        Assert.Equal(["av.active_threat", "fw.public_off", "storage.system_low", "perf.sustained_cpu", "display.below_max_refresh", "perf.uptime_long"], await Order());
    }

    [Fact]
    public async Task IgnoredFinding_CarriesIntoLaterReports_AndReopensOnSeverityRise()
    {
        var world = new FakeWorld { FreeGiB = 10 };
        var (host, _) = Start(world);
        await using var _h = host;
        var c = await host.ConnectAsync();
        var (_, r1) = await Scan(c);
        var storage = FindingOf(r1, "storage.system_low");

        var f = await c.Call("finding.setDisposition", new JsonObject
        {
            ["idempotencyKey"] = "d1", ["reportId"] = r1.Str("reportId"), ["findingId"] = storage.Str("id"), ["status"] = "ignored", ["scope"] = "finding",
        });
        Assert.Contains(f["decisions"]!.AsArray(), d => d.Str("status") == "ignored");
        Assert.Equal("ignored", FindingOf(await c.Call("report.get", new JsonObject { ["reportId"] = r1.Str("reportId") }), "storage.system_low")["disposition"].Str("status"));

        var (_, r2) = await Scan(c);
        var carried = FindingOf(r2, "storage.system_low");
        Assert.Equal("ignored", carried["disposition"].Str("status"));
        Assert.Equal("finding", carried["disposition"].Str("scope"));
        var f2 = await c.Call("followup.get", new JsonObject { ["reportId"] = r2.Str("reportId") });
        Assert.Equal((int)f2["total"]! - 1, (int)f2["pendingCount"]!);
        Assert.Contains(f2["decisions"]!.AsArray(), d => d.Str("status") == "ignored");

        world.FreeGiB = 2; // medium → high
        var (_, r3) = await Scan(c);
        Assert.Null(FindingOf(r3, "storage.system_low")["disposition"]);
        var f3 = await c.Call("followup.get", new JsonObject { ["reportId"] = r3.Str("reportId") });
        Assert.Equal((int)f3["total"]!, (int)f3["pendingCount"]!);
    }

    [Fact]
    public async Task Snooze_ExpiresAndRuleScopeCarriesOver()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
        var (host, _) = Start(time: time);
        await using var _h = host;
        var c = await host.ConnectAsync();
        var (_, r1) = await Scan(c);
        var display = FindingOf(r1, "display.below_max_refresh");
        var fw = FindingOf(r1, "fw.public_off");

        JsonObject Set(string key, JsonObject finding, string status, string scope, string? expires) => new()
        {
            ["idempotencyKey"] = key, ["reportId"] = r1.Str("reportId"), ["findingId"] = finding.Str("id"),
            ["status"] = status, ["scope"] = scope, ["expiresAt"] = expires,
        };
        Assert.Equal(ErrorCodes.InvalidParams, (await c.Fails("finding.setDisposition", Set("bad1", display, "snoozed", "finding", null))).Code);
        Assert.Equal(ErrorCodes.InvalidParams, (await c.Fails("finding.setDisposition", Set("bad2", display, "snoozed", "finding", "2028-01-01T00:00:00Z"))).Code);

        var followup = await c.Call("finding.setDisposition", Set("s1", display, "snoozed", "finding", "2026-11-04T12:00:00Z"));
        Assert.Contains(followup["decisions"]!.AsArray(), d => d.Str("status") == "snoozed");
        await c.Call("finding.setDisposition", Set("s2", fw, "ignored", "rule", null));

        time.Advance(TimeSpan.FromHours(2));
        var (_, r2) = await Scan(c);
        Assert.Equal("snoozed", FindingOf(r2, "display.below_max_refresh")["disposition"].Str("status"));
        Assert.Equal("2026-11-04T12:00:00.000Z", FindingOf(r2, "display.below_max_refresh")["disposition"].Str("expiresAt"));
        Assert.Equal("rule", FindingOf(r2, "fw.public_off")["disposition"].Str("scope"));

        time.Advance(TimeSpan.FromDays(2));
        var (_, r3) = await Scan(c);
        Assert.Null(FindingOf(r3, "display.below_max_refresh")["disposition"]);
        Assert.Equal("ignored", FindingOf(r3, "fw.public_off")["disposition"].Str("status"));
        var f3 = await c.Call("followup.get", new JsonObject { ["reportId"] = r3.Str("reportId") });
        Assert.Equal((int)f3["total"]! - 1, (int)f3["pendingCount"]!);

        // Clearing it puts the card back at the front.
        f3 = await c.Call("finding.clearDisposition", new JsonObject
        {
            ["idempotencyKey"] = "c1", ["reportId"] = r3.Str("reportId"), ["findingId"] = FindingOf(r3, "fw.public_off").Str("id"),
        });
        Assert.Equal(FindingOf(r3, "fw.public_off").Str("id"), f3["card"].Str("findingId"));
        Assert.Equal((int)f3["total"]!, (int)f3["pendingCount"]!);
        Assert.Null(FindingOf(await c.Call("report.get", new JsonObject { ["reportId"] = r3.Str("reportId") }), "fw.public_off")["disposition"]);
    }

    [Fact]
    public async Task Rescan_ComparesAgainstItsBase()
    {
        var world = new FakeWorld();
        var (host, _) = Start(world);
        await using var _h = host;
        var c = await host.ConnectAsync();
        var (_, baseReport) = await Scan(c, "audit.security");
        var fwPrint = FindingOf(baseReport, "fw.public_off").Str("fingerprint");

        world.FirewallPublicOn = true;
        world.ActiveThreat = true;
        var (scan, rescan) = await Scan(c, "rescan", baseReportId: baseReport.Str("reportId"));
        Assert.Equal("audit.security", scan.Str("intent"));
        Assert.Equal(baseReport.Str("reportId"), rescan.Str("baseReportId"));
        Assert.Equal([fwPrint], rescan["comparison"]!["resolved"]!.AsArray().Select(x => (string)x!));
        Assert.Equal([Ids.Fingerprint("av.active_threat", "security.av.product.0a1b2c3d")], rescan["comparison"]!["new"]!.AsArray().Select(x => (string)x!));
        Assert.Equal(0, (int)rescan["comparison"]!["unchanged"]!);

        var compare = await c.Call("report.compare", new JsonObject { ["reportId"] = rescan.Str("reportId"), ["baseReportId"] = baseReport.Str("reportId") });
        Assert.Equal("fw.public_off", Assert.Single(compare["resolved"]!.AsArray()).Str("ruleId"));
        Assert.Equal("av.active_threat", Assert.Single(compare["new"]!.AsArray()).Str("ruleId"));
        Assert.Empty(compare["unchanged"]!.AsArray());

        Assert.Equal(ErrorCodes.InvalidParams, (await c.Fails("scan.start", Rpc.ScanParams("rescan", "", "k-no-base"))).Code);
    }

    [Fact]
    public async Task SkipCollector_KeepsSamplesAsPartialEvidence()
    {
        var options = new CollectorOptions { SampleWindow = TimeSpan.FromSeconds(30) };
        var (host, _) = Start(options: options);
        await using var _h = host;
        var c = await host.ConnectAsync();
        var started = await c.Call("scan.start", Rpc.ScanParams("audit.full", "Begin scan", "skip-1"));
        var scanId = started.Str("scanId");

        JsonObject? current = null;
        for (var i = 0; i < 200 && current?.Str("collectorId") != AuditCatalog.SamplerId; i++)
        {
            await Task.Delay(20);
            current = (await c.Call("scan.get", new JsonObject { ["scanId"] = scanId }))["progress"]!["current"] as JsonObject;
        }
        Assert.NotNull(current);
        Assert.True((bool)current["skippable"]!);
        Assert.Equal("Performance sample", current.Str("label"));
        Assert.InRange((long)current["remainingSeconds"]!, 1, 30);
        Assert.Contains(c.Events, e => e.Str("type") == "scan.progress" && e["payload"]!["collector"] is JsonObject);

        // While the sampler runs a scan.progress event arrives about every 2 s (start event + at least one tick).
        await Task.Delay(ScanCoordinatorTick + TimeSpan.FromMilliseconds(400));
        Assert.True(c.Events.Count(e => e.Str("type") == "scan.progress" && (string?)e["payload"]!["collector"]?["collectorId"] == AuditCatalog.SamplerId) >= 2);
        var notRunning = await c.Fails("scan.skipCollector", new JsonObject { ["scanId"] = scanId, ["collectorId"] = "fake.fw" });
        Assert.Equal(ErrorCodes.InvalidState, notRunning.Code);

        await c.Call("scan.skipCollector", new JsonObject { ["scanId"] = scanId, ["collectorId"] = AuditCatalog.SamplerId });
        await c.WaitForScanFinished(scanId);
        var scan = await c.Call("scan.get", new JsonObject { ["scanId"] = scanId });
        Assert.Equal("complete_with_gaps", scan.Str("status"));
        var cov = scan["coverage"]!.AsArray().Single(x => x.Str("collectorId") == AuditCatalog.SamplerId)!;
        Assert.Equal("skipped", cov.Str("status"));
        Assert.Equal(1, (int)cov["observations"]!);

        var report = await c.Call("report.get", new JsonObject { ["reportId"] = scan.Str("reportId") });
        var sample = report["evidence"]!.AsArray().Single(e => e.Str("collectorId") == AuditCatalog.SamplerId)!;
        Assert.Equal("local", sample.Str("sensitivity")); // carries a normalized path
        Assert.Equal("public", report["evidence"]!.AsArray().First(e => e.Str("collectorId") == "fake.fw").Str("sensitivity"));
        Assert.Equal("finding", report["rules"]!.AsArray().Single(r => r.Str("id") == "perf.sustained_cpu").Str("status"));
        Assert.Equal("partial", Category(report, "performance").Str("status"));
    }

    [Fact]
    public async Task OverallDeadline_TimesOutWhatIsStillRunning()
    {
        var (host, _) = Start(options: new CollectorOptions { SampleWindow = TimeSpan.FromSeconds(30) }, deadline: TimeSpan.FromMilliseconds(400));
        await using var _h = host;
        var c = await host.ConnectAsync();
        var (scan, report) = await Scan(c);
        Assert.Equal("complete_with_gaps", scan.Str("status"));
        var cov = scan["coverage"]!.AsArray().Single(x => x.Str("collectorId") == AuditCatalog.SamplerId)!;
        Assert.Equal("timed_out", cov.Str("status"));
        Assert.Contains("budget", cov.Str("reason"));
        Assert.Equal("timed_out", report["gaps"]!.AsArray().Single(g => g.Str("collectorId") == AuditCatalog.SamplerId).Str("status"));
    }

    [Fact]
    public async Task FindingGet_ReturnsEvidenceCoverageAndRule()
    {
        var (host, _) = Start();
        await using var _h = host;
        var c = await host.ConnectAsync();
        var (_, report) = await Scan(c);
        var fw = FindingOf(report, "fw.public_off");
        var detail = await c.Call("finding.get", new JsonObject { ["reportId"] = report.Str("reportId"), ["findingId"] = fw.Str("id") });
        Assert.Equal(fw.Str("id"), detail["finding"].Str("id"));
        Assert.Equal((string)fw["evidenceIds"]![0]!, Assert.Single(detail["evidence"]!.AsArray()).Str("id"));
        Assert.Equal("fake.fw", Assert.Single(detail["coverage"]!.AsArray()).Str("collectorId"));
        Assert.Equal(["fake.fw"], detail["rule"]!["inputs"]!.AsArray().Select(x => (string)x!));
        Assert.Equal(ErrorCodes.NotFound, (await c.Fails("finding.get", new JsonObject { ["reportId"] = report.Str("reportId"), ["findingId"] = "fnd-0" })).Code);
    }

    [Fact]
    public async Task BadRules_NeverProduceUncitedFindings()
    {
        var collectors = new ICollector[] { new FakeCollector("fake.one", "security", _ => Task.FromResult(FakeCollector.One("a.b", new JsonObject(), "x"))) };
        var rules = new IRule[]
        {
            new FakeRule("x.throws", "security", ["fake.one"], _ => throw new InvalidOperationException("boom")),
            new FakeRule("x.uncited", "security", ["fake.one"], _ => RuleResult.Found(new FindingDraft("a.b", "T", "low", "high", ["ev-0000000000000000"],
                new CardDraft("T?", ["Ignore"])))),
            new FakeRule("x.unplanned", "security", ["fake.missing"], _ => RuleResult.Clear()),
        };
        await using var host = TestHost.StartModules([new FakeModule("bad", collectors, rules)]);
        var c = await host.ConnectAsync();
        var (_, report) = await Scan(c);
        Assert.Empty(report["findings"]!.AsArray());
        var outcomes = report["rules"]!.AsArray().ToDictionary(r => r.Str("id"), r => r.Str("status"));
        Assert.Equal("insufficient", outcomes["x.throws"]);
        Assert.Equal("insufficient", outcomes["x.uncited"]);
        Assert.False(outcomes.ContainsKey("x.unplanned"));
    }

    [Fact]
    public async Task Inventory_RunsSteps_CapsWaveLabels_AndFeedsStarters()
    {
        var (host, _) = Start();
        await using var _h = host;
        var c = await host.ConnectAsync();
        await Rpc.OnboardLocal(c, consent: "granted");
        var inv = await c.Call("inventory.start", new JsonObject { ["idempotencyKey"] = "inv" });
        await c.WaitForEvent(e => e.Str("type") == "inventory.finished");
        var view = await c.Call("inventory.get", new JsonObject { ["inventoryId"] = inv.Str("inventoryId") });

        Assert.Equal("partial", view.Str("status"));
        var facts = c.Events.Where(e => e.Str("type") == "inventory.fact").Select(e => e["payload"]!).ToList();
        Assert.Equal(10, facts.Count(f => f.Str("group") == "Specs")); // 12 labels capped at 10
        Assert.Equal(["Specs", "Displays", "Network"], facts.Select(f => f.Str("group")).Distinct());
        Assert.Equal(12, view["facts"]!.AsArray().Count);
        Assert.Equal("timed_out", view["coverage"].Str("audio"));
        Assert.Equal("partial", view["coverage"].Str("network"));
        Assert.Equal(2, (int)view["capabilities"]!["displayCount"]!); // later step wins
        Assert.True((bool)view["capabilities"]!["hasWifi"]!);

        var starters = (await c.Call("starter.list"))["starters"]!.AsArray();
        Assert.Contains(starters, s => s.Str("id") == "s12");
    }
}

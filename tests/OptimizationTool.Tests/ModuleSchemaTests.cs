using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Windows;

namespace OptimizationTool.Tests;

/// <summary>v1.1 output of a module-source host validated against contracts/schemas, plus the Live smoke test.</summary>
public class ModuleSchemaTests
{
    [Fact]
    public async Task ModuleHostOutput_MatchesSchemas()
    {
        var world = new FakeWorld { ActiveThreat = true, FreeGiB = 10 };
        var options = new CollectorOptions { SampleWindow = TimeSpan.FromSeconds(30) };
        await using var host = TestHost.StartModules(FakeAudit.Modules(world, options), collectorOptions: options);
        var c = await host.ConnectAsync();
        SchemaTests.AssertValid("hello", c.Hello);
        await Rpc.OnboardLocal(c, consent: "granted");
        await c.Call("inventory.start", new JsonObject { ["idempotencyKey"] = "inv" });
        await c.WaitForEvent(e => e.Str("type") == "inventory.finished");

        // A running scan with the sampler as progress.current, then skipped.
        var started = await c.Call("scan.start", Rpc.ScanParams("audit.full", "Begin scan", "s1"));
        SchemaTests.AssertValid("scan", started);
        JsonObject running = started;
        for (var i = 0; i < 200 && (string?)running["progress"]!["current"]?["collectorId"] != AuditCatalog.SamplerId; i++)
        {
            await Task.Delay(20);
            running = await c.Call("scan.get", new JsonObject { ["scanId"] = started.Str("scanId") });
        }
        SchemaTests.AssertValid("scan", running);
        await Task.Delay(50);
        SchemaTests.AssertValid("scan", await c.Call("scan.skipCollector", new JsonObject { ["scanId"] = started.Str("scanId"), ["collectorId"] = AuditCatalog.SamplerId }));
        await c.WaitForScanFinished(started.Str("scanId"));
        var scan = await c.Call("scan.get", new JsonObject { ["scanId"] = started.Str("scanId") });
        SchemaTests.AssertValid("scan", scan);

        var reportId = scan.Str("reportId");
        var report = await c.Call("report.get", new JsonObject { ["reportId"] = reportId });
        SchemaTests.AssertValid("report", report);
        var finding = report["findings"]!.AsArray()[0]!;
        SchemaTests.AssertValid("finding", await c.Call("finding.get", new JsonObject { ["reportId"] = reportId, ["findingId"] = finding.Str("id") }));
        SchemaTests.AssertValid("evidence", await c.Call("evidence.get", new JsonObject { ["reportId"] = reportId, ["evidenceId"] = (string)finding["evidenceIds"]![0]! }));

        var followup = await c.Call("finding.setDisposition", new JsonObject
        {
            ["idempotencyKey"] = "d", ["reportId"] = reportId, ["findingId"] = finding.Str("id"), ["status"] = "snoozed", ["scope"] = "finding",
            ["expiresAt"] = Time.Format(DateTimeOffset.UtcNow.AddDays(3)),
        });
        SchemaTests.AssertValid("followup", followup);
        Assert.Equal(finding.Str("id"), followup["decisions"]![0].Str("findingId"));
        Assert.Equal("snoozed", followup["decisions"]![0].Str("status"));
        SchemaTests.AssertValid("report", await c.Call("report.get", new JsonObject { ["reportId"] = reportId })); // with a disposition
        SchemaTests.AssertValid("followup", await c.Call("finding.clearDisposition", new JsonObject { ["idempotencyKey"] = "c", ["reportId"] = reportId, ["findingId"] = finding.Str("id") }));

        world.ActiveThreat = false;
        var again = await c.Call("scan.start", Rpc.ScanParams("rescan", "", "s2").Also(p => p["baseReportId"] = reportId));
        await WaitAndSkip(c, again.Str("scanId"));
        var rescan = await c.Call("report.get", new JsonObject { ["reportId"] = (await c.Call("scan.get", new JsonObject { ["scanId"] = again.Str("scanId") })).Str("reportId") });
        SchemaTests.AssertValid("report", rescan);
        SchemaTests.AssertValid("compare", await c.Call("report.compare", new JsonObject { ["reportId"] = rescan.Str("reportId"), ["baseReportId"] = reportId }));

        foreach (var e in c.Events) SchemaTests.AssertValid("event", e);
    }

    private static async Task WaitAndSkip(Client c, string scanId)
    {
        for (var i = 0; i < 500; i++)
        {
            var scan = await c.Call("scan.get", new JsonObject { ["scanId"] = scanId });
            if (scan.Str("status") != "running") return;
            if ((string?)scan["progress"]!["current"]?["collectorId"] == AuditCatalog.SamplerId)
                try { await c.Call("scan.skipCollector", new JsonObject { ["scanId"] = scanId, ["collectorId"] = AuditCatalog.SamplerId }); }
                catch (OptimizationTool.Transport.JsonRpcException) { }
            await Task.Delay(20);
        }
    }

    /// <summary>Runs a real read-only Windows scan on this PC. Asserts shape only, never values.</summary>
    [Fact, Trait("Category", "Live")]
    public async Task Live_WindowsScan_CompletesWithCategoryStatuses()
    {
        var options = new CollectorOptions { SampleWindow = TimeSpan.FromSeconds(4), SampleInterval = TimeSpan.FromSeconds(1) };
        await using var host = TestHost.StartModules(WindowsModules.All, collectorOptions: options);
        var c = await host.ConnectAsync();
        Assert.Contains("audit.windows", c.Hello!["features"]!.AsArray().Select(x => (string)x!));
        var started = await c.Call("scan.start", Rpc.ScanParams("audit.full", "Begin scan", "live"));
        var finished = await c.WaitForScanFinished(started.Str("scanId"), timeoutMs: 150_000);
        Assert.Contains(finished["payload"].Str("status"), new[] { "complete", "complete_with_gaps" });
        var report = await c.Call("report.get", new JsonObject { ["reportId"] = finished["payload"].Str("reportId") });
        SchemaTests.AssertValid("report", report);
        Assert.All(report["categories"]!.AsArray(), cat => Assert.Contains(cat.Str("status"), new[] { "complete", "partial", "none" }));
    }
}

internal static class JsonObjectExtensions
{
    public static JsonObject Also(this JsonObject o, Action<JsonObject> change)
    {
        change(o);
        return o;
    }
}

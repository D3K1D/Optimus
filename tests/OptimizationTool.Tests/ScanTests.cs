using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using OptimizationTool.Engine;

namespace OptimizationTool.Tests;

public class ScanTests
{
    [Fact]
    public async Task RepeatStart_WithSameKeyAfterReconnect_ReturnsSameScan()
    {
        await using var host = TestHost.Start(speed: 0);
        var key = Guid.NewGuid().ToString();
        var first = await host.ConnectAsync();
        var scan = await first.Call("scan.start", Rpc.ScanParams("audit.full", "Begin scan", key));
        await first.WaitForScanFinished(scan.Str("scanId"));
        await first.Rpc.DisposeAsync(); // simulated disconnect

        var second = await host.ConnectAsync();
        var replay = await second.Call("scan.start", Rpc.ScanParams("audit.full", "Begin scan", key));
        Assert.Equal(scan.Str("scanId"), replay.Str("scanId"));

        // …and across a host restart.
        await host.RestartAsync();
        var third = await host.ConnectAsync();
        var again = await third.Call("scan.start", Rpc.ScanParams("audit.full", "Begin scan", key));
        Assert.Equal(scan.Str("scanId"), again.Str("scanId"));
        var list = await third.Call("report.list");
        Assert.Single(list["reports"]!.AsArray()); // no second scan was created
    }

    [Fact]
    public async Task SameKey_DifferentParams_IsMismatch()
    {
        await using var host = TestHost.Start(speed: 0);
        var c = await host.ConnectAsync();
        var key = Guid.NewGuid().ToString();
        var scan = await c.Call("scan.start", Rpc.ScanParams("audit.full", "Begin scan", key));
        await c.WaitForScanFinished(scan.Str("scanId"));

        var error = await c.Fails("scan.start", Rpc.ScanParams("audit.storage", "Where could I free up disk space?", key));
        Assert.Equal(ErrorCodes.IdempotencyMismatch, error.Code);
        Assert.Equal(key, error.ErrorData.Str("idempotencyKey"));
    }

    [Fact]
    public async Task SecondScan_WhileOneIsActive_IsRejected()
    {
        await using var host = TestHost.Start(speed: 1);
        var c = await host.ConnectAsync();
        var scan = await c.Call("scan.start", Rpc.ScanParams("audit.full", "Begin scan", Guid.NewGuid().ToString()));
        var error = await c.Fails("scan.start", Rpc.ScanParams("audit.storage", "", Guid.NewGuid().ToString()));
        Assert.Equal(ErrorCodes.ScanActive, error.Code);
        Assert.Equal(scan.Str("scanId"), error.ErrorData.Str("scanId"));
        await c.Call("scan.cancel", new JsonObject { ["scanId"] = scan.Str("scanId") });
    }

    [Fact]
    public async Task Cancel_KeepsCommittedEvidence_AndReportsPartial()
    {
        await using var host = TestHost.Start(speed: 0.3);
        var c = await host.ConnectAsync();
        var scan = await c.Call("scan.start", Rpc.ScanParams("audit.full", "Begin scan", Guid.NewGuid().ToString()));
        var scanId = scan.Str("scanId");
        Assert.Equal("running", scan.Str("status"));

        await c.WaitForEvent(e => e.Str("stream") == "scan:" + scanId && e.Str("type") == "scan.coverage" && e["payload"].Str("status") == "complete");
        var cancelled = await c.Call("scan.cancel", new JsonObject { ["scanId"] = scanId });
        Assert.Equal("cancelled", cancelled.Str("status"));
        var finished = await c.WaitForScanFinished(scanId);
        Assert.Equal("cancelled", finished["payload"].Str("status"));

        var report = await c.Call("report.get", new JsonObject { ["reportId"] = cancelled.Str("reportId") });
        Assert.Equal("partial", report.Str("status"));
        var coverage = report["coverage"]!.AsArray();
        var completed = coverage.Count(x => x.Str("status") == "complete");
        Assert.InRange(completed, 1, 8);
        Assert.Equal(completed, report["evidence"]!.AsArray().Count); // each fixture collector commits one item
        Assert.Contains(coverage, x => x.Str("status") is "cancelled" or "skipped");
        Assert.Equal(9, coverage.Count);
        Assert.All(report["findings"]!.AsArray(), f =>
            Assert.All(f!["evidenceIds"]!.AsArray(), id => Assert.Contains(report["evidence"]!.AsArray(), e => e.Str("id") == (string)id!)));
    }

    [Fact]
    public async Task CollectorTimeout_YieldsTimedOutCoverage_AndClientCanTellCompleteFromIncomplete()
    {
        await using var host = TestHost.Start(speed: 0);
        var c = await host.ConnectAsync();

        var (full, _) = await c.ScanAsync("audit.full");
        Assert.Equal("complete_with_gaps", full.Str("status"));
        var fan = full["coverage"]!.AsArray().Single(x => x.Str("collectorId") == "fixture.fan")!;
        Assert.Equal("timed_out", fan.Str("status"));
        Assert.Equal(0, (int)fan["observations"]!);
        Assert.NotNull((string?)fan["reason"]);
        Assert.Contains(full["progress"]!["categories"]!.AsArray(), x => x.Str("id") == "diagnostics" && x.Str("status") == "gap");

        var (security, _) = await c.ScanAsync("audit.security", "Check whether my PC is protected.");
        Assert.Equal("complete", security.Str("status"));
        Assert.All(security["coverage"]!.AsArray(), x => Assert.Equal("complete", x.Str("status")));
        var report = await c.Call("report.get", new JsonObject { ["reportId"] = security.Str("reportId") });
        Assert.Equal("complete", report.Str("status"));
        Assert.Empty(report["gaps"]!.AsArray());
    }

    [Fact]
    public async Task TargetedIntent_PlansOnlyItsCollectors_AndBuildsCardsOnlyFromThem()
    {
        await using var host = TestHost.Start(speed: 0);
        var c = await host.ConnectAsync();
        var (scan, _) = await c.ScanAsync("audit.storage", "Where could I free up disk space?");
        Assert.Equal("complete", scan.Str("status"));
        var coverage = Assert.Single(scan["coverage"]!.AsArray());
        Assert.Equal("fixture.volume", coverage.Str("collectorId"));
        Assert.Equal("storage", Assert.Single(scan["progress"]!["categories"]!.AsArray()).Str("id"));

        var report = await c.Call("report.get", new JsonObject { ["reportId"] = scan.Str("reportId") });
        Assert.Equal("storage.system_low", Assert.Single(report["findings"]!.AsArray()).Str("ruleId"));
        Assert.All(report["evidence"]!.AsArray(), e => Assert.Equal("storage", e.Str("category")));

        var followup = await c.Call("followup.get", new JsonObject { ["reportId"] = scan.Str("reportId") });
        Assert.Equal(1, (int)followup["total"]!);
        Assert.Equal("storage", followup["card"].Str("category"));
    }

    [Fact]
    public async Task UnknownIntent_IsInvalidParams()
    {
        await using var host = TestHost.Start(speed: 0);
        var c = await host.ConnectAsync();
        var error = await c.Fails("scan.start", Rpc.ScanParams("audit.everything", "", Guid.NewGuid().ToString()));
        Assert.Equal(ErrorCodes.InvalidParams, error.Code);
        Assert.Equal("intent", error.ErrorData.Str("field"));
    }

    [Fact]
    public async Task HostStoppedMidScan_ScanIsInterrupted_WithPartialReport()
    {
        await using var host = TestHost.Start(speed: 1);
        var c = await host.ConnectAsync();
        var scan = await c.Call("scan.start", Rpc.ScanParams("audit.full", "Begin scan", Guid.NewGuid().ToString()));
        await host.RestartAsync();

        var c2 = await host.ConnectAsync();
        var after = await c2.Call("scan.get", new JsonObject { ["scanId"] = scan.Str("scanId") });
        Assert.Equal("interrupted", after.Str("status"));
        var report = await c2.Call("report.get", new JsonObject { ["reportId"] = after.Str("reportId") });
        Assert.Equal("partial", report.Str("status"));
    }

    [Fact]
    public async Task ScanLeftRunningByACrashedHost_IsMarkedInterruptedOnStartup()
    {
        var dir = TestHost.NewDataDir();
        await using (var first = TestHost.Start(speed: 0, dataDir: dir)) { await first.ConnectAsync(); }

        using (var conn = new SqliteConnection($"Data Source={Path.Combine(dir, Store.FileName)};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO scans (scan_id, intent, text, status, started_at) VALUES ('scan-00000000000000aa', 'audit.storage', '', 'running', '2026-11-03T21:00:00.000Z');
                INSERT INTO scan_collectors (scan_id, collector_id, ord, category, version, status) VALUES ('scan-00000000000000aa', 'fixture.volume', 0, 'storage', '1.0.0', 'running');
                """;
            cmd.ExecuteNonQuery();
        }

        await using var host = TestHost.Start(speed: 0, dataDir: dir);
        var c = await host.ConnectAsync();
        var scan = await c.Call("scan.get", new JsonObject { ["scanId"] = "scan-00000000000000aa" });
        Assert.Equal("interrupted", scan.Str("status"));
        Assert.Equal("cancelled", Assert.Single(scan["coverage"]!.AsArray()).Str("status"));
        var report = await c.Call("report.get", new JsonObject { ["reportId"] = scan.Str("reportId") });
        Assert.Equal("partial", report.Str("status"));
        // A new scan can take the lease.
        var (next, _) = await c.ScanAsync("audit.storage");
        Assert.Equal("complete", next.Str("status"));
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}

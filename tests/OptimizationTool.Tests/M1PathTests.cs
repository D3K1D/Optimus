using System.Text.Json.Nodes;
using OptimizationTool.Engine;

namespace OptimizationTool.Tests;

/// <summary>
/// The first-PR journey over a real pipe: Local only → onboarding with Skip → Begin scan → open a cited finding →
/// follow-ups → restart the host → reopen the same report.
/// </summary>
public class M1PathTests
{
    [Fact]
    public async Task FullPath_SurvivesHostRestart()
    {
        await using var host = TestHost.Start(speed: 0);
        var c = await host.ConnectAsync();
        Assert.Equal(1, (int)c.Hello!["protocol"]!["major"]!);

        // Onboarding: provider Local only, skip inventory, step complete.
        var onboarding = await Rpc.OnboardLocal(c);
        Assert.Equal("complete", onboarding.Str("step"));
        Assert.Equal("Local only", onboarding["profile"]!["provider"].Str("label"));
        Assert.Equal("skipped", onboarding["profile"].Str("inventoryConsent"));

        // Begin scan → scan.finished.
        var (scan, finished) = await c.ScanAsync("audit.full", "Begin scan");
        Assert.Equal("complete_with_gaps", finished.Str("status"));
        Assert.Equal("complete_with_gaps", scan.Str("status"));
        var reportId = finished.Str("reportId");
        Assert.Equal(reportId, scan.Str("reportId"));
        Assert.Equal(Ids.Report(scan.Str("scanId")), reportId);

        // report.get: 8 findings, one timed-out gap.
        var report = await c.Call("report.get", new JsonObject { ["reportId"] = reportId });
        Assert.Equal("complete_with_gaps", report.Str("status"));
        Assert.Equal(8, (int)report["counts"]!["findings"]!);
        Assert.Equal(8, report["findings"]!.AsArray().Count);
        var gap = Assert.Single(report["gaps"]!.AsArray());
        Assert.Equal("timed_out", gap.Str("status"));
        Assert.Equal("fixture.fan", gap.Str("collectorId"));
        Assert.Equal("Fan speed", gap.Str("title"));

        // Open one cited finding's evidence.
        var firewall = report["findings"]!.AsArray().Single(f => f.Str("ruleId") == "fw.public_off")!;
        var evidenceId = (string)firewall["evidenceIds"]![0]!;
        var evidence = await c.Call("evidence.get", new JsonObject { ["reportId"] = reportId, ["evidenceId"] = evidenceId });
        Assert.Equal(evidenceId, evidence.Str("id"));
        Assert.Equal("fixture.firewall", evidence.Str("collectorId"));
        Assert.False((bool)evidence["value"]!["enabled"]!);
        Assert.Equal(Ids.Evidence(scan.Str("scanId"), "fixture.firewall", "firewall.profile.public"), evidenceId);

        // Follow-ups: c1 (firewall) leads; ignore it; a report-wide reply moves storage to the front.
        var followup = await c.Call("followup.get", new JsonObject { ["reportId"] = reportId });
        Assert.Equal(8, (int)followup["total"]!);
        Assert.Equal(firewall.Str("id"), followup["card"].Str("findingId"));
        var c1 = followup["card"].Str("id");
        followup = await c.Call("followup.respond", Rpc.Respond(reportId, (long)followup["revision"]!, "card", c1, option: "ignore"));
        Assert.Equal(7, (int)followup["pendingCount"]!);
        Assert.Equal("performance", followup["card"].Str("category")); // Gaming + performance: c2 next
        Assert.Equal("ignored", followup["decisions"]![0].Str("status"));

        followup = await c.Call("followup.respond", Rpc.Respond(reportId, (long)followup["revision"]!, "report", text: "Focus on storage first."));
        Assert.Equal("storage", followup["card"].Str("category"));
        Assert.Single(followup["decisions"]!.AsArray()); // decisions are kept

        var reportBefore = (await c.Call("report.get", new JsonObject { ["reportId"] = reportId })).ToJsonString();
        var followupBefore = followup.ToJsonString();

        // New host on the same data directory.
        await host.RestartAsync();
        var c2 = await host.ConnectAsync();
        var resumed = await c2.Call("onboarding.get");
        Assert.Equal("complete", resumed.Str("step"));
        Assert.Equal(reportId, resumed["resume"].Str("reportId"));
        Assert.Equal(scan.Str("scanId"), resumed["resume"].Str("scanId"));

        var reportAfter = (await c2.Call("report.get", new JsonObject { ["reportId"] = reportId })).ToJsonString();
        Assert.Equal(reportBefore, reportAfter);
        var followupAfter = (await c2.Call("followup.get", new JsonObject { ["reportId"] = reportId })).ToJsonString();
        Assert.Equal(followupBefore, followupAfter);

        var list = await c2.Call("report.list", new JsonObject { ["limit"] = 10 });
        Assert.Equal(reportId, list["reports"]![0].Str("reportId"));
    }

    [Fact]
    public async Task ScanEvents_AreSequencedFromOne_AndReplayable()
    {
        await using var host = TestHost.Start(speed: 0);
        var c = await host.ConnectAsync();
        var (scan, _) = await c.ScanAsync("audit.full");
        var stream = "scan:" + scan.Str("scanId");

        var pushed = c.Events.Where(e => e.Str("stream") == stream).Select(e => (long)e["seq"]!).ToList();
        Assert.Equal(Enumerable.Range(1, pushed.Count).Select(i => (long)i), pushed);

        var read = await c.Call("events.read", new JsonObject { ["stream"] = stream, ["afterSeq"] = 0, ["limit"] = 200 });
        Assert.False((bool)read["stale"]!);
        Assert.Equal(pushed.Count, read["events"]!.AsArray().Count);
        Assert.Equal((long)scan["eventCursor"]!["seq"]!, (long)read["nextSeq"]!);
        Assert.Equal("scan.finished", read["events"]!.AsArray()[^1].Str("type"));
        Assert.Equal(9, read["events"]!.AsArray().Count(e => e.Str("type") == "scan.coverage"));

        var tail = await c.Call("events.read", new JsonObject { ["stream"] = stream, ["afterSeq"] = pushed.Count - 1, ["limit"] = 5 });
        Assert.Single(tail["events"]!.AsArray());
    }
}

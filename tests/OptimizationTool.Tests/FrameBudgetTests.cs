using System.Text;
using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Host;
using OptimizationTool.Transport;

namespace OptimizationTool.Tests;

/// <summary>Responses over the 1 MiB frame cap settle with an error, and a large report is trimmed to fit (protocol §1, report.get).</summary>
public class FrameBudgetTests
{
    private static JsonObject BigReport(int evidenceCount, int displayChars)
    {
        var evidence = new JsonArray();
        for (var i = 0; i < evidenceCount; i++)
            evidence.Add(new JsonObject { ["id"] = $"ev-{i:D4}", ["display"] = new string('x', displayChars) });
        return new JsonObject
        {
            ["reportId"] = "report-1",
            ["findings"] = new JsonArray(new JsonObject
            {
                ["id"] = "fnd-1", ["evidenceIds"] = new JsonArray("ev-0900"), ["counterevidence"] = new JsonArray("ev-0950"),
            }),
            ["evidence"] = evidence,
        };
    }

    [Fact]
    public void LargeReport_KeepsCitedEvidence_AndCountsWhatWasLeftOut()
    {
        var report = ReportService.Bound(BigReport(1000, 2000), ReportService.MaxReportBytes);
        var ids = report["evidence"]!.AsArray().Select(e => e!["id"]!.GetValue<string>()).ToList();
        Assert.Contains("ev-0900", ids);
        Assert.Contains("ev-0950", ids);
        Assert.Equal("ev-0000", ids[0]); // order kept; the tail is what goes
        Assert.Equal(1000 - ids.Count, report["evidenceOmitted"]!.GetValue<int>());
        Assert.True(Encoding.UTF8.GetByteCount(report.ToJsonString()) <= ReportService.MaxReportBytes);
    }

    [Fact]
    public void SmallReport_IsUnchanged()
    {
        var report = ReportService.Bound(BigReport(10, 100), ReportService.MaxReportBytes);
        Assert.Equal(10, report["evidence"]!.AsArray().Count);
        Assert.False(report.ContainsKey("evidenceOmitted"));
    }

    [Fact]
    public void OversizeResponse_BecomesASmallErrorWithTheSameId()
    {
        var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 7, ["result"] = new string('y', Frame.MaxPayloadBytes + 10) };
        var bytes = Connection.Fit(response);
        Assert.True(bytes.Length < 1024);
        var error = JsonNode.Parse(bytes)!;
        Assert.Equal(7, error["id"]!.GetValue<int>());
        Assert.Equal(ErrorCodes.InternalError, error["error"]!["code"]!.GetValue<int>());
        Assert.Equal("response_too_large", error["error"]!["data"]!["reason"]!.GetValue<string>());
    }
}

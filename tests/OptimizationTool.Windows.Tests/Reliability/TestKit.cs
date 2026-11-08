using OptimizationTool.Engine;

namespace OptimizationTool.Windows.Tests.Reliability;

/// <summary>Builds rule contexts from facts produced by the normalizers (synthetic inputs only).</summary>
internal static class TestKit
{
    public const string ObservedAt = "2026-01-10T12:00:00Z";

    public static EvidenceItem Ev(string collectorId, Fact f) =>
        new(Ids.Evidence("scan-test", collectorId, f.Subject), f.Subject, collectorId, "diagnostics", f.Value.DeepClone(), f.Display, ObservedAt);

    public static RuleContext Ctx(string collectorId, CollectorResult result, string? coverage = null) =>
        new(result.Facts.Select(f => Ev(collectorId, f)).ToList(), new Dictionary<string, string> { [collectorId] = coverage ?? result.Coverage.Status }, Profile.Default);

    public static RuleContext Missing(string collectorId, string status = "timed_out") =>
        new([], new Dictionary<string, string> { [collectorId] = status }, Profile.Default);

    public static void AssertCard(FindingDraft f)
    {
        Assert.NotEmpty(f.EvidenceIds);
        Assert.Equal(new[] { "View steps", "Ask about this", "Ignore" }, f.Card.Options);
        Assert.InRange(f.Card.Guide!.Count, 2, 4);
        Assert.Contains("Would you like", f.Card.Text);
        string[] catalog = ["windowsdefender://threat", "windowsdefender://network", "ms-settings:windowsdefender", "ms-settings:startupapps",
            "ms-settings:powersleep", "ms-settings:storagesense", "ms-settings:appsfeatures", "ms-settings:privacy-feedback",
            "ms-settings:display-advanced", "ms-settings:personalization-start", "ms-settings:windowsupdate-history", "ms-settings:troubleshoot"];
        if (f.Card.SettingsUri is { } uri) Assert.Contains(uri, catalog);
        Assert.Contains(f.Severity, new[] { "high", "medium", "low", "info" });
        Assert.Contains(f.Confidence, new[] { "high", "medium", "low" });
    }
}

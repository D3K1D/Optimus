using System.Text.Json.Nodes;
using OptimizationTool.Engine;

namespace OptimizationTool.Windows.Tests.SystemDomain;

/// <summary>Builds rule contexts from synthetic facts (invented values only).</summary>
internal static class Kit
{
    public static EvidenceItem Ev(string subject, string collector, JsonObject value) =>
        new("ev-" + Ids.Hash16(collector, subject), subject, collector, "performance", value, "test", "2026-01-01T00:00:00Z");

    public static RuleContext Ctx(IEnumerable<EvidenceItem> evidence, params (string Collector, string Status)[] coverage) =>
        new(evidence.ToList(), coverage.ToDictionary(c => c.Collector, c => c.Status), Profile.Default);

    public static RuleContext FromFacts(string collector, CollectorResult result) =>
        Ctx(result.Facts.Select(f => Ev(f.Subject, collector, (JsonObject)f.Value)), (collector, result.Coverage.Status));

    public static JsonArray Arr(params double[] xs) => new(xs.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());

    public static void AssertCard(FindingDraft f)
    {
        Assert.Equal([f.ActionEligibility == "preview_available" ? "Preview change" : "View steps", "Ask about this", "Ignore"], f.Card.Options);
        Assert.Contains("Would you like to", f.Card.Text);
        Assert.NotNull(f.Card.Guide);
        Assert.InRange(f.Card.Guide!.Count, 2, 4);
        Assert.NotEmpty(f.EvidenceIds);
        string[] catalog = ["windowsdefender://threat", "windowsdefender://network", "ms-settings:windowsdefender", "ms-settings:startupapps",
            "ms-settings:powersleep", "ms-settings:storagesense", "ms-settings:appsfeatures", "ms-settings:privacy-feedback",
            "ms-settings:display-advanced", "ms-settings:personalization-start", "ms-settings:windowsupdate-history", "ms-settings:troubleshoot"];
        if (f.Card.SettingsUri is { } uri) Assert.Contains(uri, catalog);
    }
}

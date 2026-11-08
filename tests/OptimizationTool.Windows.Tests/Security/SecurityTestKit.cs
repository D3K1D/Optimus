using System.Text.Json.Nodes;
using OptimizationTool.Engine;

namespace OptimizationTool.Windows.Tests.Security;

/// <summary>Builds rule contexts from synthetic evidence (invented values only).</summary>
internal sealed class Ctx
{
    public const string ObservedAt = "2026-06-15T12:00:00Z";
    private readonly List<EvidenceItem> _evidence = [];
    private readonly Dictionary<string, string> _coverage = [];

    public Ctx Cover(string collectorId, string status = "complete") { _coverage[collectorId] = status; return this; }

    public Ctx Add(string collectorId, string subject, JsonObject value, string observedAt = ObservedAt)
    {
        _evidence.Add(new EvidenceItem(Ids.Evidence("scan-test", collectorId, subject), subject, collectorId, "security", value, "x", observedAt));
        return this;
    }

    public Ctx AddFacts(string collectorId, IEnumerable<Fact> facts, bool observedAtNow = false)
    {
        var at = observedAtNow ? DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture) : ObservedAt;
        foreach (var f in facts) Add(collectorId, f.Subject, (JsonObject)f.Value.DeepClone(), at);
        return this;
    }

    public RuleContext Build() => new(_evidence, _coverage, Profile.Default);
    public RuleContext Build(params string[] uses) => new(_evidence, _coverage, Profile.Default with { Uses = [.. uses] });

    public static void AssertCard(FindingDraft f)
    {
        Assert.Equal(["View steps", "Ask about this", "Ignore"], f.Card.Options);
        Assert.Contains("Would you like to", f.Card.Text);
        Assert.InRange(f.Card.Guide?.Count ?? 0, 2, 4);
        Assert.NotEmpty(f.EvidenceIds);
        string[] catalog =
        [
            "windowsdefender://threat", "windowsdefender://network", "ms-settings:windowsdefender", "ms-settings:startupapps",
            "ms-settings:powersleep", "ms-settings:storagesense", "ms-settings:appsfeatures", "ms-settings:privacy-feedback",
            "ms-settings:display-advanced", "ms-settings:personalization-start", "ms-settings:windowsupdate-history", "ms-settings:troubleshoot",
        ];
        if (f.Card.SettingsUri is { } uri) Assert.Contains(uri, catalog);
    }
}

internal static class Live
{
    public static async Task<CollectorResult> Run(ICollector c)
    {
        using var cts = new CancellationTokenSource(c.Timeout);
        return await c.CollectAsync(cts.Token).WaitAsync(cts.Token);
    }

    public static void AssertShape(CollectorResult r, params string[] allowed)
    {
        Assert.Contains(r.Coverage.Status, allowed);
        Assert.Equal(r.Facts.Count, r.Coverage.Observations);
        Assert.Equal(r.Facts.Select(f => f.Subject).Order(StringComparer.Ordinal), r.Facts.Select(f => f.Subject));
        Assert.Equal(r.Facts.Count, r.Facts.Select(f => f.Subject).Distinct().Count());
        foreach (var f in r.Facts)
        {
            Assert.Matches("^[a-z0-9_.]+$", f.Subject);
            Assert.False(string.IsNullOrWhiteSpace(f.Display));
            Assert.DoesNotContain(":\\", f.Display);          // no full paths in display text
            Assert.DoesNotContain(":\\", f.Value.ToJsonString()); // only normalized paths in values
            Assert.Contains(f.Quality, new[] { "measured", "reported", "estimated", "unavailable" });
        }
    }
}

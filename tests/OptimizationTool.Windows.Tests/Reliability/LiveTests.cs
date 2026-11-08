using System.Diagnostics;
using System.Text.RegularExpressions;
using OptimizationTool.Engine;
using OptimizationTool.Windows.Reliability;
using Xunit.Abstractions;

namespace OptimizationTool.Windows.Tests.Reliability;

/// <summary>Runs the real collectors on this PC. Asserts shape and coverage only, never values.</summary>
[Trait("Category", "Live")]
public class LiveTests(ITestOutputHelper output)
{
    private static readonly CollectorOptions Options = new();
    private static readonly Regex SubjectShape = new("^[a-z0-9][a-z0-9.\\-]*$");

    public static IEnumerable<object[]> CollectorIds() => new ReliabilityModule().Collectors(Options).Select(c => new object[] { c.Id });

    [Theory]
    [MemberData(nameof(CollectorIds))]
    public async Task Collector_runs_with_honest_coverage(string id)
    {
        var c = new ReliabilityModule().Collectors(Options).Single(x => x.Id == id);
        Assert.Matches("^win\\.[a-z]+\\.[a-z]+$", c.Id);
        Assert.Equal("1.0.0", c.Version);
        using var cts = new CancellationTokenSource(c.Timeout);
        var r = await c.CollectAsync(cts.Token);
        Assert.Contains(r.Coverage.Status, new[] { "complete", "partial", "denied", "unsupported" });
        Assert.Equal(r.Facts.Select(f => f.Subject).Order(StringComparer.Ordinal), r.Facts.Select(f => f.Subject));
        Assert.Equal(r.Facts.Count, r.Facts.Select(f => f.Subject).Distinct().Count());
        foreach (var f in r.Facts)
        {
            Assert.Matches(SubjectShape, f.Subject);
            Assert.Contains(f.Quality, new[] { "measured", "reported", "estimated", "unavailable" });
            Assert.False(string.IsNullOrWhiteSpace(f.Display));
            Assert.DoesNotContain(":\\", f.Display);
            Assert.DoesNotContain(":\\\\", f.Value.ToJsonString());
        }
        if (r.Coverage.Status == "complete" && id.StartsWith("win.events.", StringComparison.Ordinal))
            Assert.Contains(r.Facts, f => f.Subject.EndsWith(".coverage", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Harness_runs_module_and_rules()
    {
        var module = new ReliabilityModule();
        var evidence = new List<EvidenceItem>();
        var coverage = new Dictionary<string, string>();
        var total = Stopwatch.StartNew();
        foreach (var c in module.Collectors(Options))
        {
            var sw = Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(c.Timeout);
            var r = await c.CollectAsync(cts.Token);
            sw.Stop();
            coverage[c.Id] = r.Coverage.Status;
            evidence.AddRange(r.Facts.Select(f => new EvidenceItem(Ids.Evidence("scan-live", c.Id, f.Subject), f.Subject, c.Id, c.Category, f.Value, f.Display, DateTimeOffset.UtcNow.ToString("o"))));
            output.WriteLine($"{c.Id,-24} {r.Coverage.Status,-11} obs={r.Coverage.Observations,-5} facts={r.Facts.Count,-4} {sw.ElapsedMilliseconds,5} ms  {r.Coverage.Reason}");
            // Subjects only for event groups (hashed for apps); no names.
            if (c.Id.StartsWith("win.events.", StringComparison.Ordinal))
                foreach (var f in r.Facts.Where(f => !f.Subject.EndsWith(".coverage", StringComparison.Ordinal)).OrderByDescending(f => RuleKit.Int(f.Value["count"])).Take(8))
                    output.WriteLine($"    {f.Subject} x{RuleKit.Int(f.Value["count"])} ({RuleKit.Str(f.Value["level"])})");
            if (c.Id is "win.custom.display" or "win.privacy.diagdata" or "win.custom.start")
                foreach (var f in r.Facts) output.WriteLine($"    {f.Subject}: {f.Value.ToJsonString()}");
            if (c.Id is "win.devices.pnp" or "win.apps.installed")
                foreach (var f in r.Facts.Where(f => f.Subject.EndsWith(".summary", StringComparison.Ordinal))) output.WriteLine($"    {f.Subject}: {f.Value.ToJsonString()}");
        }
        output.WriteLine($"collectors total {total.ElapsedMilliseconds} ms");

        var ctx = new RuleContext(evidence, coverage, Profile.Default);
        foreach (var rule in module.Rules())
        {
            var res = rule.Evaluate(ctx);
            output.WriteLine($"{rule.Id,-28} {res.Status,-12} {string.Join(", ", res.Findings.Select(f => $"{f.Severity}/{f.Confidence} ev={f.EvidenceIds.Count}"))} {res.Reason}");
            foreach (var f in res.Findings)
            {
                Assert.NotEmpty(f.EvidenceIds);
                Assert.All(f.EvidenceIds, id => Assert.Contains(evidence, e => e.Id == id));
                TestKit.AssertCard(f);
            }
        }
    }
}

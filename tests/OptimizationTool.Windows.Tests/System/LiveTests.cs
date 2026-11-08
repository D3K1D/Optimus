using System.Diagnostics;
using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Windows.SystemDomain;
using Xunit.Abstractions;

namespace OptimizationTool.Windows.Tests.SystemDomain;

/// <summary>Runs the real collectors on this PC. Asserts shape and coverage only, never machine values.</summary>
[Trait("Category", "Live")]
public class SystemLiveTests(ITestOutputHelper output)
{
    private static readonly CollectorOptions Short = new() { SampleWindow = TimeSpan.FromSeconds(6), SampleInterval = TimeSpan.FromSeconds(2) };
    private static readonly string[] Ok = ["complete", "partial"];

    private static async Task<CollectorResult> Run(ICollector c)
    {
        using var cts = new CancellationTokenSource(c.Timeout);
        return await c.CollectAsync(cts.Token);
    }

    private static void AssertShape(ICollector c, CollectorResult r)
    {
        Assert.Matches(@"^win\.[a-z]+\.[a-z]+$", c.Id);
        Assert.Equal("1.0.0", c.Version);
        Assert.Contains(r.Coverage.Status, Ok);
        Assert.Equal(r.Facts.Count, r.Coverage.Observations);
        Assert.Equal(r.Facts.Select(f => f.Subject).OrderBy(s => s, StringComparer.Ordinal), r.Facts.Select(f => f.Subject));
        Assert.Equal(r.Facts.Count, r.Facts.Select(f => f.Subject).Distinct().Count());
        foreach (var f in r.Facts)
        {
            Assert.Matches(@"^[a-z0-9_.]+$", f.Subject);
            Assert.Contains(f.Quality, new[] { "measured", "reported", "estimated", "unavailable" });
            Assert.IsType<JsonObject>(f.Value);
            Assert.False(string.IsNullOrWhiteSpace(f.Display));
            Assert.DoesNotContain(":\\", f.Display);
            Assert.DoesNotContain(Environment.UserName, f.Display, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Os()
    {
        var c = new OsCollector(Short);
        var r = await Run(c);
        AssertShape(c, r);
        Assert.Contains(r.Facts, f => f.Subject == "system.uptime");
    }

    [Fact]
    public async Task Hardware()
    {
        var c = new HardwareCollector(Short);
        var r = await Run(c);
        AssertShape(c, r);
        Assert.Contains(r.Facts, f => f.Subject == "system.cpu");
        Assert.Contains(r.Facts, f => f.Subject == "system.memory");
    }

    [Fact]
    public async Task Power()
    {
        var c = new PowerCollector(Short);
        var r = await Run(c);
        AssertShape(c, r);
        Assert.Contains(r.Facts, f => f.Subject == "power.active_scheme");
        Assert.Contains(r.Facts, f => f.Subject == "power.source");
    }

    [Fact]
    public async Task Volumes()
    {
        var c = new VolumesCollector(Short);
        var r = await Run(c);
        AssertShape(c, r);
        Assert.Contains(r.Facts, f => f.Value["isSystem"]?.GetValue<bool>() == true);
    }

    [Fact]
    public async Task Sampler()
    {
        var c = new PerfSampler(Short);
        Assert.True(c.Timeout > Short.SampleWindow);
        var r = await Run(c);
        AssertShape(c, r);
        var s = r.Facts.Single(f => f.Subject == "perf.sample");
        Assert.True(s.Value["skippable"]!.GetValue<bool>());
        Assert.Equal(3, s.Value["intervals"]!.GetValue<int>());
        Assert.Contains(r.Facts, f => f.Subject == "perf.cpu");
        Assert.Contains(r.Facts, f => f.Subject.StartsWith("perf.process.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sampler_cancellation_returns_partial_promptly()
    {
        var c = new PerfSampler(new CollectorOptions { SampleWindow = TimeSpan.FromSeconds(60), SampleInterval = TimeSpan.FromSeconds(1) });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3.5));
        var sw = Stopwatch.StartNew();
        var r = await c.CollectAsync(cts.Token);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6), $"took {sw.Elapsed}");
        Assert.Equal("partial", r.Coverage.Status);
        Assert.Contains("stopped early", r.Coverage.Reason);
        Assert.True(r.Facts.Single(f => f.Subject == "perf.sample").Value["intervals"]!.GetValue<int>() >= 2);
    }

    /// <summary>Harness: the whole module with a 10 s window; prints what it found (counts and coverage).</summary>
    [Fact]
    public async Task Module_harness()
    {
        var options = new CollectorOptions { SampleWindow = TimeSpan.FromSeconds(10), SampleInterval = TimeSpan.FromSeconds(2) };
        var module = new SystemModule();
        var evidence = new List<EvidenceItem>();
        var coverage = new Dictionary<string, string>();
        var collectors = module.Collectors(options).ToList();
        var results = await Task.WhenAll(collectors.Select(async c =>
        {
            var sw = Stopwatch.StartNew();
            var r = await Run(c);
            return (c, r, sw.Elapsed);
        }));
        foreach (var (c, r, took) in results)
        {
            AssertShape(c, r);
            coverage[c.Id] = r.Coverage.Status;
            output.WriteLine($"{c.Id}: {r.Coverage.Status} ({r.Facts.Count} facts, {took.TotalSeconds:0.0} s){(r.Coverage.Reason is { } why ? " - " + why : "")}");
            foreach (var f in r.Facts)
            {
                output.WriteLine($"  {f.Subject} [{f.Quality}] {f.Display}");
                evidence.Add(new EvidenceItem("ev-" + Ids.Hash16(c.Id, f.Subject), f.Subject, c.Id, c.Category, f.Value, f.Display, DateTime.UtcNow.ToString("O")));
            }
        }
        var sample = evidence.Single(e => e.Subject == "perf.sample").Value;
        output.WriteLine($"sampler overhead: {sample["overheadCpuMs"]} ms CPU, {sample["overheadWallMs"]} ms wall, {sample["overheadPercentOfOneCore"]}% of one core; " +
                         $"tool top: {sample["thisToolTopConsumer"]}, defender top: {sample["defenderTopConsumer"]}");

        var ctx = new RuleContext(evidence, coverage, Profile.Default);
        foreach (var rule in module.Rules())
        {
            var res = rule.Evaluate(ctx);
            Assert.Contains(res.Status, new[] { "finding", "clear", "insufficient", "inapplicable" });
            output.WriteLine($"rule {rule.Id}: {res.Status}{(res.Reason is { } why ? " - " + why : "")}");
            foreach (var f in res.Findings)
            {
                Assert.All(f.EvidenceIds, id => Assert.Contains(evidence, e => e.Id == id));
                output.WriteLine($"  [{f.Severity}/{f.Confidence}] {f.Title}: {f.Card.Text}");
            }
        }
    }
}

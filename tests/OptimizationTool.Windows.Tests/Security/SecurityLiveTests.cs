using OptimizationTool.Engine;
using OptimizationTool.Windows.Security;
using OptimizationTool.Windows.Security.Collectors;
using Xunit.Abstractions;

namespace OptimizationTool.Windows.Tests.Security;

/// <summary>Runs each security collector on this machine. Asserts shape and coverage only, never values.</summary>
[Trait("Category", "Live")]
public sealed class SecurityLiveTests(ITestOutputHelper output)
{
    private static readonly CollectorOptions Options = new();

    [Fact]
    public async Task Wsc() => Live.AssertShape(await Live.Run(new WscCollector(Options)), "complete", "partial");

    [Fact]
    public async Task Defender() => Live.AssertShape(await Live.Run(new DefenderCollector(Options)), "complete", "partial", "unsupported", "denied");

    [Fact]
    public async Task Firewall() => Live.AssertShape(await Live.Run(new FirewallCollector(Options)), "complete", "partial");

    [Fact]
    public async Task Listeners() => Live.AssertShape(await Live.Run(new ListenersCollector(Options)), "complete", "partial");

    [Fact]
    public async Task Startup() => Live.AssertShape(await Live.Run(new StartupCollector(Options)), "complete", "partial");

    [Fact]
    public async Task Uac() => Live.AssertShape(await Live.Run(new UacCollector(Options)), "complete", "partial");

    [Fact]
    public async Task SecureBoot() => Live.AssertShape(await Live.Run(new SecureBootCollector(Options)), "complete", "partial", "unsupported");

    /// <summary>Harness: every collector + every rule on this PC, printed to test output for a sanity check.</summary>
    [Fact]
    public async Task Harness()
    {
        var module = new SecurityModule();
        var ctx = new Ctx();
        foreach (var c in module.Collectors(Options))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = await Live.Run(c);
            ctx.Cover(c.Id, r.Coverage.Status);
            ctx.AddFacts(c.Id, r.Facts.Select(f => f with { }), observedAtNow: true);
            output.WriteLine($"{c.Id}: {r.Coverage.Status} facts={r.Facts.Count} {sw.ElapsedMilliseconds} ms {r.Coverage.Reason}");
            foreach (var g in r.Facts.GroupBy(f => string.Join('.', f.Subject.Split('.').Take(f.Subject.StartsWith("startup.", StringComparison.Ordinal) ? 2 : 3))))
                output.WriteLine($"    {g.Key}: {g.Count()}");
            // Counts and states only (no names, paths or addresses).
            foreach (var f in r.Facts.Where(f => f.Subject is "startup.summary" or "net.listeners.summary" or "security.defender.threats"))
                output.WriteLine($"    {f.Subject} = {f.Value.ToJsonString()}");
            if (c.Id == StartupCollector.CollectorId)
                output.WriteLine("    signatures: " + string.Join(", ", r.Facts.Where(f => f.Value["signature"] is not null)
                    .GroupBy(f => f.Value["signature"]!.ToString()).Select(g => $"{g.Key}={g.Count()}")));
            if (Environment.GetEnvironmentVariable("OT_SECURITY_DUMP") == "1")
                foreach (var f in r.Facts) output.WriteLine($"      {f.Subject} | {f.Display} | {f.Value.ToJsonString()}");
            if (c.Id == DefenderCollector.CollectorId &&r.Facts.FirstOrDefault(f => f.Subject == "security.defender.status") is { } s)
                output.WriteLine($"    mode={s.Value["mode"]} rtp={s.Value["realTimeProtection"]} ageDays={s.Value["signatureAgeDays"]}");
        }
        var context = ctx.Build();
        foreach (var rule in module.Rules())
        {
            var result = rule.Evaluate(context);
            output.WriteLine($"{rule.Id}: {result.Status} findings={result.Findings.Count} {result.Reason}");
            foreach (var f in result.Findings)
            {
                Ctx.AssertCard(f);
                output.WriteLine($"    [{f.Severity}/{f.Confidence}] {f.Title}");
            }
        }
    }
}

using System.Text.Json.Nodes;
using OptimizationTool.Engine;

namespace OptimizationTool.Tests;

/// <summary>
/// Mutable synthetic machine state that fake collectors read. Tests change it between scans. Invented values only;
/// nothing here comes from a real PC.
/// </summary>
public sealed class FakeWorld
{
    public volatile bool FirewallPublicOn;
    public volatile bool ActiveThreat;
    public int FreeGiB = 40;
    public volatile bool DisplayBelowMax = true;
    public volatile bool InventorySlowStepHangs = true;
}

public sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private long _ticks = start.UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}

public sealed class FakeCollector(string id, string category, Func<CancellationToken, Task<CollectorResult>> run, string? label = null) : ICollector
{
    public string Id => id;
    public string Version => "1.0.0";
    public string Category => category;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public string Label => label ?? id;
    public Task<CollectorResult> CollectAsync(CancellationToken ct) => run(ct);

    public static CollectorResult One(string subject, JsonNode value, string display, string status = "complete") =>
        new([new Fact(subject, "Synthetic source", "measured", value, display, null)], new CoverageReceipt(status, 1, null));
}

/// <summary>A sampler that runs for the sample window; on skip it returns the samples taken so far.</summary>
public sealed class FakeSampler(CollectorOptions options) : ISkippableCollector
{
    public string Id => AuditCatalog.SamplerId;
    public string Version => "1.0.0";
    public string Category => "performance";
    public TimeSpan Timeout => options.SampleWindow + TimeSpan.FromSeconds(10);
    public string Label => "Performance sample";
    public TimeSpan? ExpectedDuration => options.SampleWindow;

    public async Task<CollectorResult> CollectAsync(CancellationToken ct)
    {
        var samples = 0;
        var end = DateTime.UtcNow + options.SampleWindow;
        try
        {
            while (DateTime.UtcNow < end)
            {
                await Task.Delay(10, ct);
                samples++;
            }
        }
        catch (OperationCanceledException) when (samples > 0)
        {
            return Result(samples, "partial");
        }
        return Result(samples, "complete");
    }

    private static CollectorResult Result(int samples, string status) =>
        new([new Fact("perf.sample", "Synthetic sampler", "measured", new JsonObject { ["samples"] = samples, ["imagePath"] = @"%ProgramFiles%\Synthetic\app.exe" },
            $"{samples} samples.", "PT1S")], new CoverageReceipt(status, 1, null));
}

public sealed class FakeRule(string id, string category, string[] inputs, Func<RuleContext, RuleResult> evaluate) : IRule
{
    public string Id => id;
    public string Version => "1.0.0";
    public string Category => category;
    public IReadOnlyList<string> Inputs => inputs;
    public RuleResult Evaluate(RuleContext context) => evaluate(context);
}

public sealed class FakeStep(string group, Func<CancellationToken, Task<InventoryStepResult>> run, TimeSpan? timeout = null) : IInventoryStep
{
    public string Group => group;
    public TimeSpan Timeout => timeout ?? TimeSpan.FromSeconds(5);
    public Task<InventoryStepResult> RunAsync(CancellationToken ct) => run(ct);
}

public sealed class FakeModule(string name, IReadOnlyList<ICollector> collectors, IReadOnlyList<IRule> rules, IReadOnlyList<IInventoryStep>? steps = null) : IAuditModule
{
    public string Name => name;
    public IEnumerable<ICollector> Collectors(CollectorOptions options) => collectors;
    public IEnumerable<IRule> Rules() => rules;
    public IEnumerable<IInventoryStep> InventorySteps(CollectorOptions options) => steps ?? [];
}

/// <summary>The standard fake audit: security, storage, performance (sampler), privacy (denied), customization.</summary>
public static class FakeAudit
{
    public const string Settings = "ms-settings:storagesense";

    public static IReadOnlyList<IAuditModule> Modules(FakeWorld world, CollectorOptions options, bool sampler = true)
    {
        var collectors = new List<ICollector>
        {
            new FakeCollector("fake.fw", "security", _ => Task.FromResult(FakeCollector.One("security.fw.public",
                new JsonObject { ["enabled"] = world.FirewallPublicOn }, world.FirewallPublicOn ? "Public firewall is on." : "Public firewall is off.")), "Firewall"),
            new FakeCollector("fake.av", "security", _ => Task.FromResult(FakeCollector.One("security.av.product.0a1b2c3d",
                new JsonObject { ["threat"] = world.ActiveThreat }, world.ActiveThreat ? "A threat needs action." : "No threats need action.")), "Antivirus"),
            new FakeCollector("fake.vol", "storage", _ => Task.FromResult(FakeCollector.One("volume.c",
                new JsonObject { ["freeGiB"] = world.FreeGiB }, $"{world.FreeGiB} GiB free.")), "Drives"),
            new FakeCollector("fake.diag", "privacy_telemetry", _ => Task.FromResult(new CollectorResult([],
                new CoverageReceipt("denied", 0, "Needs admin."))), "Diagnostic data"),
            new FakeCollector("fake.display", "customization", _ => Task.FromResult(FakeCollector.One("display.2.refresh",
                new JsonObject { ["currentHz"] = world.DisplayBelowMax ? 60 : 144, ["maxHz"] = 144 }, "Display 2 refresh.")), "Displays"),
        };
        if (sampler) collectors.Add(new FakeSampler(options));

        var rules = new List<IRule>
        {
            new FakeRule("av.active_threat", "security", ["fake.av"], ctx =>
            {
                var e = ctx.Get("security.av.product.0a1b2c3d");
                if (e is null) return RuleResult.Insufficient("No antivirus fact.");
                return (bool)e.Value["threat"]! ? RuleResult.Found(Draft(e, "Threat needs action", "high", "windowsdefender://threat")) : RuleResult.Clear();
            }),
            new FakeRule("fw.public_off", "security", ["fake.fw"], ctx =>
            {
                if (ctx.CoverageOf("fake.fw") is not ("complete" or "partial")) return RuleResult.Insufficient("Firewall not read.");
                var e = ctx.Get("security.fw.public");
                if (e is null) return RuleResult.Insufficient("No firewall fact.");
                return (bool)e.Value["enabled"]! ? RuleResult.Clear() : RuleResult.Found(Draft(e, "Public firewall is off", "high", "windowsdefender://network"));
            }),
            new FakeRule("storage.system_low", "storage", ["fake.vol"], ctx =>
            {
                var e = ctx.Get("volume.c");
                if (e is null) return RuleResult.Insufficient("No volume fact.");
                var free = (int)e.Value["freeGiB"]!;
                return free >= 15 ? RuleResult.Clear() : RuleResult.Found(Draft(e, "System drive is low", free < 5 ? "high" : "medium", Settings));
            }),
            new FakeRule("privacy.optional_diag", "privacy_telemetry", ["fake.diag"], ctx =>
                ctx.CoverageOf("fake.diag") is "complete" or "partial" ? RuleResult.Clear() : RuleResult.Insufficient("Diagnostic data not read.")),
            new FakeRule("display.below_max_refresh", "customization", ["fake.display"], ctx =>
            {
                var e = ctx.Get("display.2.refresh");
                if (e is null) return RuleResult.Insufficient("No display fact.");
                return (int)e.Value["currentHz"]! < (int)e.Value["maxHz"]! ? RuleResult.Found(Draft(e, "Display below max refresh", "low", "ms-settings:display-advanced")) : RuleResult.Clear();
            }),
            new FakeRule("start.suggestions_on", "misc", ["fake.display"], ctx => RuleResult.Inapplicable("No Start menu fact in this fake.")),
        };
        if (sampler)
        {
            rules.Add(new FakeRule("perf.sustained_cpu", "performance", [AuditCatalog.SamplerId], ctx =>
            {
                if (ctx.CoverageOf(AuditCatalog.SamplerId) is not ("complete" or "partial")) return RuleResult.Insufficient("No samples.");
                var e = ctx.Get("perf.sample");
                return e is null ? RuleResult.Insufficient("No samples.") : RuleResult.Found(Draft(e, "Busy background app", "low", "ms-settings:startupapps"));
            }));
            rules.Add(new FakeRule("perf.uptime_long", "performance", [AuditCatalog.SamplerId], ctx =>
                ctx.Get("perf.sample") is { } e ? RuleResult.Found(Draft(e, "Long uptime", "info", null)) : RuleResult.Insufficient("No samples.")));
        }

        var steps = new List<IInventoryStep>
        {
            new FakeStep("Specs", _ => Task.FromResult(new InventoryStepResult(
                Enumerable.Range(1, 12).Select(i => new InventoryFact("computer", $"{i} things")).ToList(),
                [new Fact("inventory.cpu", "Synthetic", "measured", new JsonObject { ["cores"] = 8 }, "8-core CPU", null)],
                new CoverageReceipt("complete", 1, null), new JsonObject { ["displayCount"] = 1, ["gpuCount"] = 1 }))),
            new FakeStep("Displays", _ => Task.FromResult(new InventoryStepResult(
                [new InventoryFact("display", "2 displays")], [], new CoverageReceipt("complete", 1, null), new JsonObject { ["displayCount"] = 2 }))),
            new FakeStep("Audio", async ct =>
            {
                if (world.InventorySlowStepHangs) await Task.Delay(Timeout.Infinite, ct);
                return new InventoryStepResult([new InventoryFact("audio", "Speakers detected")], [], new CoverageReceipt("complete", 1, null), []);
            }, TimeSpan.FromMilliseconds(100)),
            new FakeStep("Network", _ => Task.FromResult(new InventoryStepResult(
                [new InventoryFact("network", "Wi-Fi adapter detected")], [], new CoverageReceipt("partial", 1, "One adapter unreadable."), new JsonObject { ["hasWifi"] = true }))),
        };

        return [new FakeModule("fake", collectors, rules, steps)];
    }

    private static FindingDraft Draft(EvidenceItem e, string title, string severity, string? settingsUri) =>
        new(e.Subject, title, severity, "high", [e.Id],
            new CardDraft($"{title}. Would you like to review it?", ["View steps", "Ask about this", "Ignore"], ["Open Settings.", "Review the item."], settingsUri));
}

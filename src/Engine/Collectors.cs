using System.Text.Json.Nodes;

namespace OptimizationTool.Engine;

/// <summary>
/// A read-only data source. M1 ships fixture collectors; M2 adds native Windows ones behind the same interface.
/// A collector returns facts plus a coverage receipt even when it read zero rows. The coordinator enforces
/// <see cref="Timeout"/> and cancellation; a collector should honour the token it is given.
/// </summary>
public interface ICollector
{
    string Id { get; }
    string Version { get; }
    string Category { get; }
    TimeSpan Timeout { get; }

    /// <summary>Short user-facing name for progress and gaps, e.g. "Fan speed". Defaults to the ID.</summary>
    string Label => Id;

    /// <summary>Short name shown when the collector leaves a gap. Defaults to <see cref="Label"/>.</summary>
    string GapTitle => Label;

    /// <summary>Where the missing data would have come from, e.g. "Sensors".</summary>
    string GapSource => Label;

    /// <summary>Sentence shown for the gap when the collector times out or fails.</summary>
    string GapDisplay => $"{GapTitle} was unavailable.";

    Task<CollectorResult> CollectAsync(CancellationToken ct);
}

/// <summary>
/// A long-running collector the user may skip (protocol §9 scan.skipCollector), e.g. the performance sampler.
/// On skip the coordinator cancels only this collector's token and waits briefly for it to return the samples it
/// already has; those are committed and its coverage is recorded as skipped.
/// </summary>
public interface ISkippableCollector : ICollector
{
    /// <summary>Expected run time, for progress.current.remainingSeconds; null when unknown.</summary>
    TimeSpan? ExpectedDuration { get; }
}

/// <summary>
/// One observation. <see cref="Subject"/> is the typed subject key the evidence ID and rules use.
/// <see cref="Sensitivity"/> is public|local (protocol §9); null lets the engine infer it (normalized paths → local).
/// </summary>
public sealed record Fact(string Subject, string Source, string Quality, JsonNode Value, string Display, string? Interval,
    string? Sensitivity = null);

/// <summary>Coverage receipt status values: complete|partial|denied|unsupported|timed_out|failed|skipped|cancelled.</summary>
public sealed record CoverageReceipt(string Status, int Observations, string? Reason);

public sealed record CollectorResult(IReadOnlyList<Fact> Facts, CoverageReceipt Coverage);

/// <summary>
/// Replays one fixture collector. Its delay is scaled by the speed factor. A fixture collector whose delay exceeds
/// its timeout simply keeps working past the deadline, so the coordinator's timeout is what stops it.
/// </summary>
public sealed class FixtureCollector : ICollector
{
    /// <summary>Lower bound for scaled fixture timeouts so speed 0 still exercises the timeout path.</summary>
    public static readonly TimeSpan MinScaledTimeout = TimeSpan.FromMilliseconds(25);

    /// <summary>Default deadline for fixture collectors that declare none.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly CollectorDef _def;
    private readonly TimeSpan _delay;

    public FixtureCollector(CollectorDef def, double speed)
    {
        _def = def;
        speed = Math.Max(0, speed);
        Timeout = def.TimeoutMs is int t ? Max(TimeSpan.FromMilliseconds(t * speed), MinScaledTimeout) : DefaultTimeout;
        _delay = TimeSpan.FromMilliseconds(def.DelayMs * speed);
        // Keep "slower than its deadline" true at any speed (including 0) without touching the timeout logic.
        if (def.TimeoutMs is int timeoutMs && def.DelayMs > timeoutMs)
            _delay = Timeout + Max(TimeSpan.FromMilliseconds((def.DelayMs - timeoutMs) * speed), TimeSpan.FromMilliseconds(100));
    }

    public string Id => _def.Id;
    public string Version => _def.Version;
    public string Category => _def.Category;
    public TimeSpan Timeout { get; }
    public string Label => _def.Gap?.Title ?? _def.Evidence.FirstOrDefault()?.Source ?? _def.Id;
    public string GapTitle => Label;
    public string GapSource => _def.Gap?.Source ?? _def.Evidence.FirstOrDefault()?.Source ?? _def.Id;
    public string GapDisplay => _def.Gap?.Display ?? $"{GapTitle} was unavailable.";

    public async Task<CollectorResult> CollectAsync(CancellationToken ct)
    {
        if (_delay > TimeSpan.Zero) await Task.Delay(_delay, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var facts = _def.Evidence
            .Select(e => new Fact(e.Subject, e.Source, e.Quality, e.Value.DeepClone(), e.Display, _def.Interval))
            .ToList();
        var status = _def.Outcome is "complete" or "partial" ? _def.Outcome : "complete";
        return new CollectorResult(facts, new CoverageReceipt(status, facts.Count, null));
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}

/// <summary>
/// What a scan can use: collectors (plan order), rules (catalog order) and inventory steps. Built either from the
/// M1 fixture or from M2 <see cref="IAuditModule"/>s (contracts/m2-collectors.md §6).
/// </summary>
public sealed class AuditCatalog
{
    /// <summary>The sampler; skippable even if it does not implement <see cref="ISkippableCollector"/>.</summary>
    public const string SamplerId = "win.perf.sample";

    public required bool IsFixture { get; init; }
    public required IReadOnlyList<ICollector> Collectors { get; init; }
    public required IReadOnlyList<IRule> Rules { get; init; }

    /// <summary>Module inventory steps in UI order; null in fixture mode (the fixture inventory is replayed).</summary>
    public IReadOnlyList<IInventoryStep>? InventorySteps { get; init; }

    public CollectorOptions Options { get; init; } = new();

    /// <summary>The modules this catalog came from (null for the fixture); diagnostics re-create collectors with per-call options.</summary>
    public IReadOnlyList<IAuditModule>? Modules { get; init; }

    public static AuditCatalog FromFixture(Fixture fixture, double speed)
    {
        var collectors = fixture.Collectors.Select(c => (ICollector)new FixtureCollector(c, speed)).ToList();
        return new AuditCatalog
        {
            IsFixture = true,
            Collectors = collectors,
            Rules = fixture.Rules.Select(r => (IRule)new FixtureRule(r, fixture)).ToList(),
        };
    }

    public static AuditCatalog FromModules(IEnumerable<IAuditModule> modules, CollectorOptions options)
    {
        var list = modules.ToList();
        var collectors = list.SelectMany(m => m.Collectors(options)).ToList();
        var duplicate = collectors.GroupBy(c => c.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new InvalidOperationException($"Collector {duplicate.Key} is registered twice.");
        var rules = list.SelectMany(m => m.Rules()).ToList();
        var dupRule = rules.GroupBy(r => r.Id).FirstOrDefault(g => g.Count() > 1);
        if (dupRule is not null) throw new InvalidOperationException($"Rule {dupRule.Key} is registered twice.");
        return new AuditCatalog
        {
            IsFixture = false,
            Collectors = collectors,
            Rules = rules,
            InventorySteps = list.SelectMany(m => m.InventorySteps(options)).ToList(),
            Options = options,
            Modules = list,
        };
    }

    public ICollector? Find(string id) => Collectors.FirstOrDefault(c => c.Id == id);

    /// <summary>
    /// Collectors a scan of these categories runs: every collector in the categories, plus the inputs of every rule
    /// presented in them (so a rule never sees only part of its inputs). Plan order is catalog order.
    /// </summary>
    public IReadOnlyList<ICollector> Plan(IReadOnlyCollection<string> categories)
    {
        var inputs = Rules.Where(r => categories.Contains(r.Category)).SelectMany(r => r.Inputs).ToHashSet();
        return Collectors.Where(c => categories.Contains(c.Category) || inputs.Contains(c.Id)).ToList();
    }

    public static bool IsSkippable(ICollector c) => c is ISkippableCollector || c.Id == SamplerId;

    /// <summary>
    /// Expensive collectors (the sampler, event-log queries, every fixture collector to keep M1 timing) share two
    /// slots; cheap ones (registry and API reads) run alongside them in their own four slots.
    /// </summary>
    public static bool IsExpensive(ICollector c) =>
        c is FixtureCollector || IsSkippable(c) || c.Id.StartsWith("win.events.", StringComparison.Ordinal);

    public int RuleOrder(string ruleId)
    {
        for (var i = 0; i < Rules.Count; i++) if (Rules[i].Id == ruleId) return i;
        return int.MaxValue;
    }
}

/// <summary>A fixture rule as an <see cref="IRule"/>: a finding when every evidence key it names was observed.</summary>
internal sealed class FixtureRule(RuleDef def, Fixture fixture) : IRule
{
    public RuleDef Def { get; } = def;
    public string Id => Def.RuleId;
    public string Version => Def.Version;
    public string Category => Def.Category;

    public IReadOnlyList<string> Inputs { get; } = def.EvidenceKeys
        .Select(k => fixture.Collectors.First(c => c.Evidence.Any(e => e.Key == k)).Id).Distinct().ToList();

    public RuleResult Evaluate(RuleContext context)
    {
        var cited = new List<string>();
        string? first = null;
        foreach (var key in Def.EvidenceKeys)
        {
            var subject = fixture.SubjectForKey(key);
            var item = context.Get(subject);
            if (item is null) return RuleResult.Insufficient($"No observation for {subject}.");
            first ??= subject;
            cited.Add(item.Id);
        }
        var card = Def.Card;
        return RuleResult.Found(new FindingDraft(first!, Def.Title, Def.Severity, Def.Confidence, cited,
            new CardDraft(card["text"]!.GetValue<string>(), Json.Strings(card["options"]), card["guide"] is null ? null : Json.Strings(card["guide"])),
            ActionEligibility: Def.ActionEligibility));
    }
}

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OptimizationTool.Engine;

/// <summary>Typed view of contracts/fixtures/m1-simulated-audit.json (embedded in this assembly).</summary>
public sealed class Fixture
{
    public required string FixtureVersion { get; init; }
    public required IReadOnlyList<Category> Categories { get; init; }
    public required IReadOnlyList<Category> ScanCategories { get; init; }
    public required IReadOnlyList<CollectorDef> Collectors { get; init; }
    public required IReadOnlyList<RuleDef> Rules { get; init; }
    public required JsonArray QueueOrder { get; init; }
    public required IReadOnlyDictionary<string, string[]> IntentCategories { get; init; }
    public required IReadOnlyList<(Regex Pattern, string Category)> Focus { get; init; }
    public required IReadOnlyList<StarterDef> Starters { get; init; }
    public required IReadOnlyList<AskTemplate> AskTemplates { get; init; }
    public required InventoryDef Inventory { get; init; }

    private static Fixture? _default;
    public static Fixture Default => _default ??= LoadEmbedded();

    public static Fixture LoadEmbedded()
    {
        using var stream = typeof(Fixture).Assembly.GetManifestResourceStream("m1-simulated-audit.json")
            ?? throw new InvalidOperationException("Embedded fixture missing.");
        return Parse(JsonNode.Parse(stream)!.AsObject());
    }

    public static Fixture Parse(JsonObject root)
    {
        static Category Cat(JsonNode? n) => new(n!["id"]!.GetValue<string>(), n["label"]!.GetValue<string>());
        static string? S(JsonNode? n) => n?.GetValue<string>();
        const RegexOptions rx = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        return new Fixture
        {
            FixtureVersion = root["fixtureVersion"]!.GetValue<string>(),
            Categories = root["categories"]!.AsArray().Select(Cat).ToList(),
            ScanCategories = root["scanCategories"]!.AsArray().Select(Cat).ToList(),
            ScanWaveWords = Json.Strings(root["scanWaveWords"]),
            Collectors = root["collectors"]!.AsArray().Select(c => new CollectorDef(
                S(c!["id"])!, S(c["version"])!, S(c["category"])!, c["delayMs"]?.GetValue<int>() ?? 0,
                S(c["outcome"]) ?? "complete", c["timeoutMs"]?.GetValue<int>(), S(c["interval"]),
                (c["evidence"] as JsonArray ?? []).Select(e => new EvidenceDef(
                    S(e!["key"])!, S(e["subject"])!, S(e["source"])!, S(e["quality"]) ?? "measured",
                    e["value"]!.DeepClone(), S(e["display"])!)).ToList(),
                c["gap"] is JsonObject g ? new GapDef(S(g["key"])!, S(g["title"])!, S(g["source"])!, S(g["display"])!) : null)).ToList(),
            Rules = root["rules"]!.AsArray().Select(r => new RuleDef(
                S(r!["ruleId"])!, S(r["version"])!, S(r["category"])!, Json.Strings(r["evidence"]),
                S(r["severity"])!, S(r["confidence"])!, S(r["actionEligibility"])!, S(r["title"])!,
                r["card"]!.AsObject())).ToList(),
            QueueOrder = root["queuePolicy"]!["order"]!.AsArray(),
            IntentCategories = root["intentCategories"]!.AsObject()
                .ToDictionary(p => p.Key, p => Json.Strings(p.Value).ToArray()),
            Focus = root["focus"]!.AsArray()
                .Select(f => (new Regex(f![0]!.GetValue<string>(), rx), f[1]!.GetValue<string>())).ToList(),
            Starters = root["starters"]!.AsArray().Select(s => new StarterDef(
                S(s!["id"])!, S(s["text"])!, S(s["intent"])!, S(s["needsUse"]),
                s["needsInventory"] as JsonObject)).ToList(),
            AskTemplates = root["askTemplates"]!.AsArray()
                .Select(a => new AskTemplate(S(a!["card"])!, new Regex(S(a["pattern"])!, rx), S(a["reply"])!)).ToList(),
            Inventory = ParseInventory(root["inventory"]!.AsObject()),
        };
    }

    private static InventoryDef ParseInventory(JsonObject inv) => new(
        inv["stepDelayMs"]?.GetValue<int>() ?? 0,
        inv["status"]?.GetValue<string>() ?? "complete",
        inv["steps"]!.AsArray().Select(s => new InventoryStep(
            s!["name"]!.GetValue<string>(),
            s["facts"]!.AsArray().Select(f => (f!["kind"]!.GetValue<string>(), f["label"]!.GetValue<string>())).ToList())).ToList(),
        inv["coverage"]!.AsObject(),
        inv["capabilities"]!.AsObject());

    public required IReadOnlyList<string> ScanWaveWords { get; init; }

    /// <summary>Keyword → category for the fixture's scanWaveWords. First match wins (Kernel-Power is diagnostics, not power).</summary>
    private static readonly (Regex Pattern, string Category)[] WaveWordCategories =
    [
        (new Regex("kernel|crash|restart", RegexOptions.IgnoreCase), "diagnostics"),
        (new Regex("display|hz|start suggestions", RegexOptions.IgnoreCase), "customization"),
        (new Regex("diagnostic data|telemetry", RegexOptions.IgnoreCase), "privacy_telemetry"),
        (new Regex("gib|free|disk", RegexOptions.IgnoreCase), "storage"),
        (new Regex("defender|firewall|update", RegexOptions.IgnoreCase), "security"),
        (new Regex("power|startup", RegexOptions.IgnoreCase), "performance"),
    ];

    public static string? WaveWordCategory(string word) =>
        WaveWordCategories.FirstOrDefault(m => m.Pattern.IsMatch(word)).Category;

    /// <summary>scanWaveWords for a scan: all of them for audit.full, otherwise those in its planned categories.</summary>
    public IReadOnlyList<string> WaveWordsFor(IEnumerable<string> plannedCategories, string intent)
    {
        if (intent == "audit.full") return ScanWaveWords;
        var cats = plannedCategories.ToHashSet();
        return ScanWaveWords.Where(w => WaveWordCategory(w) is { } c && cats.Contains(c)).ToList();
    }

    public string CategoryLabel(string id) => Categories.FirstOrDefault(c => c.Id == id)?.Label ?? id;

    public int CategoryOrder(string id)
    {
        for (var i = 0; i < Categories.Count; i++) if (Categories[i].Id == id) return i;
        return int.MaxValue;
    }

    /// <summary>Categories a scan of this intent covers; null for an unknown intent. audit.full covers all.</summary>
    public string[]? CategoriesFor(string intent) =>
        intent == "audit.full" ? Categories.Select(c => c.Id).ToArray()
        : IntentCategories.TryGetValue(intent, out var cats) ? cats : null;

    /// <summary>The prototype's focusFor: the categories a typed message is about.</summary>
    public IReadOnlyList<string> FocusCategories(string text) =>
        Focus.Where(f => f.Pattern.IsMatch(text)).Select(f => f.Category).Distinct().ToList();

    /// <summary>Evidence subject for a fixture evidence key (e1…); rules reference keys, the engine matches subjects.</summary>
    public string SubjectForKey(string key) =>
        Collectors.SelectMany(c => c.Evidence).First(e => e.Key == key).Subject;
}

public sealed record Category(string Id, string Label);

public sealed record CollectorDef(string Id, string Version, string Category, int DelayMs, string Outcome,
    int? TimeoutMs, string? Interval, IReadOnlyList<EvidenceDef> Evidence, GapDef? Gap);

public sealed record EvidenceDef(string Key, string Subject, string Source, string Quality, JsonNode Value, string Display);

public sealed record GapDef(string Key, string Title, string Source, string Display);

public sealed record RuleDef(string RuleId, string Version, string Category, IReadOnlyList<string> EvidenceKeys,
    string Severity, string Confidence, string ActionEligibility, string Title, JsonObject Card)
{
    public string CardKey => Card["key"]!.GetValue<string>();
}

public sealed record StarterDef(string Id, string Text, string Intent, string? NeedsUse, JsonObject? NeedsInventory);

public sealed record AskTemplate(string Card, Regex Pattern, string Reply);

public sealed record InventoryStep(string Name, IReadOnlyList<(string Kind, string Label)> Facts);

public sealed record InventoryDef(int StepDelayMs, string Status, IReadOnlyList<InventoryStep> Steps, JsonObject Coverage, JsonObject Capabilities);

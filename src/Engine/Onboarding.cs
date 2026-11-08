using System.Text.Json.Nodes;

namespace OptimizationTool.Engine;

/// <summary>The saved onboarding profile (protocol §6 onboarding.*), stored as one JSON row with a revision.</summary>
public sealed record Profile(string Step, JsonObject? Provider, List<string> Uses, string CustomUse,
    List<string> Goals, string CustomGoal, string InventoryConsent)
{
    public static Profile Default => new("provider", null, [], "", [], "", "unset");

    public JsonObject ToJson() => new()
    {
        ["step"] = Step,
        ["profile"] = new JsonObject
        {
            ["provider"] = Provider?.DeepClone(),
            ["uses"] = Json.Array(Uses),
            ["customUse"] = CustomUse,
            ["goals"] = Json.Array(Goals),
            ["customGoal"] = CustomGoal,
            ["inventoryConsent"] = InventoryConsent,
        },
    };

    public static Profile FromJson(JsonNode node)
    {
        var p = node["profile"]!;
        return new Profile(node["step"]!.GetValue<string>(), p["provider"] as JsonObject is { } prov ? (JsonObject)prov.DeepClone() : null,
            Json.Strings(p["uses"]), p["customUse"]?.GetValue<string>() ?? "", Json.Strings(p["goals"]),
            p["customGoal"]?.GetValue<string>() ?? "", p["inventoryConsent"]?.GetValue<string>() ?? "unset");
    }
}

internal sealed class OnboardingService(Store store, Fixture fixture)
{
    public static readonly string[] Steps = ["provider", "use", "goal", "specs", "complete"];
    public static readonly string[] Consents = ["unset", "granted", "skipped"];

    /// <summary>The app's usage and goal labels (v1.4 adds "Hosting / development"). Agents only ever see these, never custom text.</summary>
    public static readonly string[] UsageLabels = ["Work", "Gaming", "Editing", "Browsing", "Hosting / development"];
    public static readonly string[] GoalLabels = ["Check for security concerns", "Improve PC performance", "Optimize Windows", "Customize Windows"];

    /// <summary>{uses, goals} for agent context: known labels only (custom text and unknown strings are dropped).</summary>
    public static JsonObject UserContext(Profile profile) => new()
    {
        ["uses"] = Json.Array(profile.Uses.Where(UsageLabels.Contains)),
        ["goals"] = Json.Array(profile.Goals.Where(GoalLabels.Contains)),
    };

    /// <summary>Provider values and the labels the server reports for them. M1 stores the choice only.</summary>
    public static readonly IReadOnlyDictionary<string, string> Providers = new Dictionary<string, string>
    {
        ["local"] = "Local only", ["later"] = "Later", ["claude-code"] = "Claude Code", ["codex"] = "Codex", ["api"] = "API key", ["chatgpt"] = "ChatGPT",
    };

    private const int MaxChoices = 16, MaxChoiceLength = 100, MaxCustomLength = 200;

    public void EnsureRow() => store.Write(db =>
    {
        if (db.Scalar("SELECT 1 FROM onboarding WHERE id = 1") is null)
            db.Exec("INSERT INTO onboarding (id, revision, state, updated_at) VALUES (1, 1, $s, $t)",
                ("$s", Json.Write(Profile.Default.ToJson())), ("$t", db.Now()));
    });

    public static (long Revision, Profile Profile) Read(Db db)
    {
        var row = db.Query("SELECT revision, state FROM onboarding WHERE id = 1", r => (r.GetInt64(0), r.GetString(1))).FirstOrDefault();
        if (row.Item2 is null) return (1, Profile.Default);
        try { return (row.Item1, Profile.FromJson(Json.Parse(row.Item2)!)); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or NullReferenceException)
        {
            throw RpcError.Corrupt("onboarding", "profile");
        }
    }

    public JsonObject Get() => store.Read(View);

    private static JsonObject View(Db db)
    {
        var (revision, profile) = Read(db);
        var view = new JsonObject { ["revision"] = revision };
        foreach (var (k, v) in profile.ToJson()) view[k] = v?.DeepClone();
        var latestScan = db.Scalar("SELECT scan_id FROM scans ORDER BY rowid DESC LIMIT 1") as string;
        var latestReport = db.Scalar("SELECT report_id FROM reports ORDER BY rowid DESC LIMIT 1") as string;
        var latestInventory = db.Scalar("SELECT inventory_id FROM inventories ORDER BY rowid DESC LIMIT 1") as string;
        view["resume"] = new JsonObject { ["inventoryId"] = latestInventory, ["scanId"] = latestScan, ["reportId"] = latestReport };
        return view;
    }

    public JsonObject Save(JsonObject p)
    {
        var expected = P.Int(p, "expectedRevision")!.Value;
        if (p["patch"] is not JsonObject patch) throw RpcError.Params("patch", "patch must be an object.");

        return store.Write(db =>
        {
            var (revision, current) = Read(db);
            var next = Apply(current, patch);
            if (expected != revision)
                throw new RpcError(ErrorCodes.StaleRevision, "The onboarding profile changed.", new JsonObject { ["current"] = View(db) });
            db.Exec("UPDATE onboarding SET revision = $r, state = $s, updated_at = $t WHERE id = 1",
                ("$r", revision + 1), ("$s", Json.Write(next.ToJson())), ("$t", db.Now()));
            return View(db);
        });
    }

    private static Profile Apply(Profile profile, JsonObject patch)
    {
        foreach (var (key, value) in patch)
        {
            var field = "patch." + key;
            profile = key switch
            {
                "step" => profile with { Step = OneOf(value, field, Steps) },
                "provider" => profile with { Provider = ParseProvider(value, field) },
                "uses" => profile with { Uses = Choices(value, field) },
                "goals" => profile with { Goals = Choices(value, field) },
                "customUse" => profile with { CustomUse = Text(value, field, MaxCustomLength) },
                "customGoal" => profile with { CustomGoal = Text(value, field, MaxCustomLength) },
                "inventoryConsent" => profile with { InventoryConsent = OneOf(value, field, Consents) },
                _ => throw RpcError.Params(field, $"Unknown onboarding field {key}."),
            };
        }
        return profile;
    }

    private static string Text(JsonNode? value, string field, int max)
    {
        if (value is not JsonValue v || !v.TryGetValue<string>(out var s)) throw RpcError.Params(field, $"{field} must be a string.");
        return s.Length <= max ? s : throw RpcError.Params(field, $"{field} is longer than {max} characters.");
    }

    private static string OneOf(JsonNode? value, string field, string[] allowed)
    {
        var s = Text(value, field, 64);
        return allowed.Contains(s) ? s : throw RpcError.Params(field, $"{field} must be one of: {string.Join(", ", allowed)}.");
    }

    private static List<string> Choices(JsonNode? value, string field)
    {
        if (value is not JsonArray a || a.Count > MaxChoices) throw RpcError.Params(field, $"{field} must be an array of at most {MaxChoices} strings.");
        var list = new List<string>();
        foreach (var item in a)
        {
            var s = Text(item, field, MaxChoiceLength).Trim();
            if (s.Length > 0 && !list.Contains(s)) list.Add(s);
        }
        return list;
    }

    /// <summary>Accepts "local", {"value":"local"} or {"value":"local","label":…}; the label is always the server's.</summary>
    private static JsonObject? ParseProvider(JsonNode? value, string field)
    {
        if (value is null) return null;
        var v = value is JsonObject o ? o["value"] : value;
        var s = Text(v, field, 32);
        if (!Providers.TryGetValue(s, out var label)) throw RpcError.Params(field, $"Unknown provider {s}.");
        return new JsonObject { ["value"] = s, ["label"] = label };
    }

    /// <summary>The prototype's buildStarters, over the saved uses and the latest finished inventory's capabilities.</summary>
    public JsonObject Starters(JsonObject p)
    {
        P.Int(p, "profileRevision", required: false); // accepted for cache keys; starters always use the current profile
        return store.Read(db =>
        {
            var (_, profile) = Read(db);
            var caps = db.Query("SELECT capabilities FROM inventories WHERE status IN ('complete','partial') AND capabilities IS NOT NULL ORDER BY rowid DESC LIMIT 1",
                r => r.GetString(0)).FirstOrDefault() is { } c ? Json.Parse(c) as JsonObject : null;

            bool Eligible(StarterDef s) =>
                (s.NeedsUse is null || HasWord(profile.Uses, s.NeedsUse)) && (s.NeedsInventory is null || Holds(s.NeedsInventory, caps));

            var ok = fixture.Starters.Where(Eligible).ToList();
            var personal = ok.Where(s => s.NeedsUse is not null || s.NeedsInventory is not null).ToList();
            var general = ok.Where(s => s.NeedsUse is null && s.NeedsInventory is null).ToList();
            var output = new List<StarterDef>();
            var gi = 0;
            if (general.Count > 0) output.Add(general[gi++]);
            foreach (var s in personal)
            {
                output.Add(s);
                if (gi < general.Count) output.Add(general[gi++]);
            }
            output.AddRange(general.Skip(gi));

            var starters = new JsonArray();
            foreach (var s in output.DistinctBy(s => s.Text))
                starters.Add(new JsonObject
                {
                    ["id"] = s.Id, ["text"] = s.Text, ["intent"] = s.Intent,
                    ["basis"] = s.NeedsUse is not null ? "profile" : s.NeedsInventory is not null ? "inventory" : "general",
                });
            return new JsonObject { ["starters"] = starters };
        });
    }

    /// <summary>Case-insensitive substring over the space-joined list (the prototype's hasWord).</summary>
    public static bool HasWord(IEnumerable<string> list, string word) =>
        string.Join(' ', list).Contains(word, StringComparison.OrdinalIgnoreCase);

    private static bool Holds(JsonObject needs, JsonObject? caps)
    {
        if (caps is null) return false;
        foreach (var (key, want) in needs)
        {
            if (caps[key] is not JsonValue have || want is not JsonValue w) return false;
            if (have.TryGetValue<double>(out var h) && w.TryGetValue<double>(out var x)) { if (h < x) return false; }
            else if (Json.Write(have) != Json.Write(w)) return false;
        }
        return true;
    }
}

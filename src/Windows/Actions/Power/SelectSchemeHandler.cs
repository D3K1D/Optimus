using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.SystemDomain;

namespace OptimizationTool.Windows.Actions.Power;

/// <summary>
/// power.select_scheme: activates one installed power scheme (and its AC/DC policies) for the current user, then
/// reads it back. Never touches power mode / overlay, OEM modes, CPU limits or individual settings. Never retries.
/// </summary>
internal sealed class SelectSchemeHandler(IPowerApi api) : IActionHandler
{
    public const string Id = "power.select_scheme";
    private const string ManualUri = "ms-settings:powersleep";
    private static readonly string[] Personalities = ["balanced", "high_performance", "power_saver"];

    public string ActionId => Id;
    public string Version => "1.0.0";
    public string Label => "Change power plan";
    public string Privilege => "none";
    public bool SupportsUndo => true;
    public string Kind => "setting";

    public Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct) =>
        Task.Run(() => Resolve(request), ct);

    public Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct) =>
        Task.Run(() =>
        {
            var schemes = api.EnumerateSchemes();
            return State(api.GetActiveScheme(), schemes);
        }, ct);

    public Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct) =>
        Task.Run(() => Execute(execution), ct);

    public ActionRequest? UndoRequest(JsonObject target, JsonObject before) =>
        SchemeId(before) is { } id ? new ActionRequest(Id, new JsonObject { ["schemeGuid"] = id.ToString("D") }, null, null) : null;

    internal ActionResolution Resolve(ActionRequest request)
    {
        if (request.ActionId != Id) return ActionResolution.Ineligible("wrong_action", "This request is for a different action.");

        IReadOnlyList<SchemeInfo> schemes;
        try { schemes = api.EnumerateSchemes(); }
        catch (Exception) { return ActionResolution.Ineligible("read_failed", "Windows didn’t list the power plans.", ManualUri); }

        // Target: an explicit GUID, or a personality (agent requests and the battery finding pick Balanced).
        SchemeInfo? target;
        var guidText = request.Target?["schemeGuid"] is JsonValue g && g.TryGetValue<string>(out var gs) ? gs
                     : request.Target?["id"] is JsonValue i && i.TryGetValue<string>(out var ids) ? ids : null;
        var personality = request.Parameters?["personality"] is JsonValue p && p.TryGetValue<string>(out var ps) ? ps : null;
        if (guidText is null && personality is null && request.FindingSubject == "power.active_scheme") personality = "balanced";

        if (guidText is not null)
        {
            if (!Guid.TryParse(guidText, out var guid)) return ActionResolution.Ineligible("invalid_target", "That isn’t a power plan ID.");
            target = schemes.FirstOrDefault(s => s.Guid == guid);
            if (target is null) return ActionResolution.Ineligible("not_installed", "That power plan isn’t installed.", ManualUri);
        }
        else if (personality is not null)
        {
            if (!Personalities.Contains(personality)) return ActionResolution.Ineligible("invalid_target", "Unknown kind of power plan.");
            var builtIn = personality switch
            {
                "balanced" => PowerCollector.Balanced,
                "high_performance" => PowerCollector.HighPerformance,
                _ => PowerCollector.PowerSaver,
            };
            target = schemes.FirstOrDefault(s => s.Guid == builtIn) ?? schemes.FirstOrDefault(s => s.Personality == personality);
            if (target is null)
                return ActionResolution.Ineligible("not_installed",
                    schemes.Count <= 1 ? "This PC has one power plan. Use Power mode in Settings instead." : "No matching power plan is installed.", ManualUri);
        }
        else return ActionResolution.Ineligible("invalid_target", "No power plan was chosen.");

        if (api.PolicyForcesActiveScheme()) return ActionResolution.Ineligible("policy", "A policy on this PC sets the power plan.");
        if (api.ManagedActiveScheme()) return ActionResolution.Ineligible("managed", "Your organization manages the power plan.");

        var activeGuid = api.GetActiveScheme();
        if (activeGuid is null) return ActionResolution.Ineligible("read_failed", "Windows didn’t report the active power plan.", ManualUri);
        if (activeGuid == target.Guid) return ActionResolution.Ineligible("already_active", $"{PlanName(target)} is already active.");

        var active = schemes.FirstOrDefault(s => s.Guid == activeGuid);
        var beforeLabel = active is null ? "Current plan" : PlanName(active);
        var afterLabel = PlanName(target);
        var source = SafeSource();

        return new ActionResolution(
            Eligible: true,
            Target: new JsonObject { ["type"] = "power_scheme", ["id"] = target.Guid.ToString("D"), ["schemeGuid"] = target.Guid.ToString("D"), ["label"] = afterLabel },
            Before: State(activeGuid, schemes),
            After: State(target.Guid, schemes),
            Rows: [new PlanRow("Active power plan", beforeLabel, afterLabel)],
            Context: [new("Power", ContextText(source))],
            Tradeoffs: Tradeoffs(active?.Personality, target.Personality, source?.HasBattery != false),
            UndoConditions: $"Only while {afterLabel} is still active and {beforeLabel} is still installed");
    }

    internal ActionOutcome Execute(ActionExecution ex)
    {
        var after = SchemeId(ex.After) ?? (Guid.TryParse(ex.Target["schemeGuid"]?.ToString(), out var t) ? t : (Guid?)null);
        var before = SchemeId(ex.Before);
        if (after is null) return new ActionOutcome("failed", new JsonObject(), "No power plan was chosen.", "invalid_target");

        IReadOnlyList<SchemeInfo> schemes = [];
        uint rc;
        try { rc = api.SetActiveScheme(after.Value); }
        catch (Exception) { rc = uint.MaxValue; }

        Guid? now;
        try
        {
            schemes = api.EnumerateSchemes();
            now = api.GetActiveScheme();
        }
        catch (Exception) { now = null; }

        var observed = State(now, schemes);
        var label = (now is { } n ? schemes.FirstOrDefault(s => s.Guid == n) : null) is { } s0 ? PlanName(s0) : "Another plan";
        if (now == after)
            return new ActionOutcome("verified", observed, $"{label} is active, read back from Windows.");
        if (now is not null && now == before)
            return new ActionOutcome("failed", observed, $"{label} is still active.", rc == 0 ? "not_applied" : "write_failed",
                rc == 0 ? null : $"Windows error {rc}");
        return new ActionOutcome("indeterminate", observed,
            now is null ? "Windows didn’t report the active power plan after the change." : $"{label} is active, which wasn’t expected.",
            "unexpected_state", rc == 0 ? null : $"Windows error {rc}");
    }

    private SourceInfo? SafeSource()
    {
        try { return api.GetSource(); } catch (Exception) { return null; }
    }

    private static JsonObject State(Guid? guid, IReadOnlyList<SchemeInfo> schemes)
    {
        if (guid is not { } g) return new JsonObject { ["activeScheme"] = null };
        var s = schemes.FirstOrDefault(x => x.Guid == g);
        return new JsonObject { ["activeScheme"] = new JsonObject { ["id"] = g.ToString("D"), ["label"] = s is null ? null : PlanName(s) } };
    }

    private static Guid? SchemeId(JsonObject? state) =>
        state?["activeScheme"]?["id"] is JsonValue v && v.TryGetValue<string>(out var s) && Guid.TryParse(s, out var g) ? g : null;

    internal static string PlanName(SchemeInfo s) => s.Name ?? s.Personality switch
    {
        "balanced" => "Balanced",
        "high_performance" => s.Guid == PowerCollector.UltimatePerformance ? "Ultimate Performance" : "High performance",
        "power_saver" => "Power saver",
        _ => "Custom plan",
    };

    internal static string ContextText(SourceInfo? s) => s switch
    {
        null => "Not reported",
        { Source: "battery", BatteryPercent: { } p } => $"On battery · {p}%",
        { Source: "battery" } => "On battery",
        { Source: "ac", HasBattery: true, BatteryPercent: { } p } => $"Plugged in · {p}%",
        { Source: "ac" } => "Plugged in",
        _ => "Not reported",
    };

    /// <summary>Downsides of the target relative to the current plan, in plain words.</summary>
    internal static IReadOnlyList<string> Tradeoffs(string? from, string to, bool hasBattery) => to switch
    {
        "high_performance" => hasBattery
            ? ["Shorter battery life", "More heat and fan noise"]
            : ["More heat and fan noise", "Higher power use"],
        "balanced" when from == "high_performance" => ["Slightly lower peak speed"],
        "balanced" when from == "power_saver" => hasBattery ? ["Shorter battery life"] : ["Higher power use"],
        "balanced" => ["Plan settings change to Windows defaults"],
        "power_saver" => ["Lower speed", "Dimmer screen on some PCs"],
        _ => ["Uses this custom plan’s own settings"],
    };
}

/// <summary>System domain's M4 handlers.</summary>
public sealed class PowerActionModule : IActionModule
{
    public IEnumerable<IActionHandler> Handlers(ActionContext context) => [new SelectSchemeHandler(new WindowsPowerApi())];
}

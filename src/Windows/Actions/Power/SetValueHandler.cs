using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;

namespace OptimizationTool.Windows.Actions.Power;

/// <summary>
/// power.set_value {settingId, ac?, dc?} (contracts/m9-ai-powers.md §3.2): sets one <see cref="PowerSettingsCatalog"/>
/// setting's plugged-in and/or on-battery value in the active power plan, re-activates the plan so it applies, and reads
/// both values back. Undo restores both previous values while they are still what was written. Never retries.
/// </summary>
internal sealed class SetValueHandler(IPowerApi api, IReadOnlyList<PowerSettingRow>? rows = null) : IActionHandler
{
    public const string Id = "power.set_value";
    private const string ManualUri = "ms-settings:powersleep";
    private const uint ErrorFileNotFound = 2;
    private readonly IReadOnlyList<PowerSettingRow> _rows = rows ?? PowerSettingsCatalog.Rows;

    public string ActionId => Id;
    public string Version => "1.0.0";
    public string Label => "Change a power setting";
    public string Privilege => "none";
    public bool SupportsUndo => true;
    public string Kind => "setting";
    public string Group => "power";
    public string What => "Sets one listed power setting, such as when the screen turns off, plugged in and on battery.";
    public IReadOnlyList<string> Flags => [];
    public JsonObject? ParameterSchema => new()
    {
        ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("settingId"),
        ["properties"] = new JsonObject
        {
            ["settingId"] = new JsonObject { ["enum"] = new JsonArray(_rows.Select(r => (JsonNode?)r.SettingId).ToArray()) },
            ["ac"] = new JsonObject { ["type"] = "string", ["maxLength"] = 16 },
            ["dc"] = new JsonObject { ["type"] = "string", ["maxLength"] = 16 },
        },
        // The values each setting accepts, plugged in (ac) and on battery (dc); send at least one of them.
        ["oneOf"] = new JsonArray(_rows.Select(r => (JsonNode?)new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["settingId"] = new JsonObject { ["const"] = r.SettingId },
                ["ac"] = new JsonObject { ["enum"] = new JsonArray(r.Choices.Select(c => (JsonNode?)c.Value).ToArray()) },
                ["dc"] = new JsonObject { ["enum"] = new JsonArray(r.Choices.Select(c => (JsonNode?)c.Value).ToArray()) },
            },
        }).ToArray()),
    };
    // PCI Express link state and the timeouts change what a scan or benchmark measures.
    public bool TakesLease => true;

    private PowerSettingRow? Row(string? id) => _rows.FirstOrDefault(r => r.SettingId == id);

    public Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct) => Task.Run(() => Resolve(request), ct);

    public Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct) => Task.Run(() =>
    {
        var row = Row(Str(target["id"])) ?? throw new ArgumentException("Unknown power setting.", nameof(target));
        var scheme = Guid.TryParse(Str(target["scheme"]), out var g) ? g : throw new ArgumentException("No power plan.", nameof(target));
        var (ac, dc, rc) = ReadBoth(scheme, row);
        if (rc != 0) throw new IOException($"Windows error {rc}.");
        return State(scheme, ac, dc);
    }, ct);

    public Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct) =>
        Task.Run(() => Execute(execution), ct);

    public ActionRequest? UndoRequest(JsonObject target, JsonObject before)
    {
        if (Row(Str(target["id"])) is not { } row || Long(before["ac"]) is not { } ac || Long(before["dc"]) is not { } dc || Str(before["scheme"]) is not { } scheme) return null;
        return new ActionRequest(Id, new JsonObject { ["type"] = "power_setting", ["id"] = row.SettingId, ["scheme"] = scheme },
            new JsonObject { ["settingId"] = row.SettingId, ["restore"] = new JsonObject { ["ac"] = ac, ["dc"] = dc } }, null) { Undo = true };
    }

    internal ActionResolution Resolve(ActionRequest request)
    {
        if (request.ActionId != Id) return ActionResolution.Ineligible("wrong_action", "This request is for a different action.");
        var p = request.Parameters;
        if (p is null || Row(Str(p["settingId"])) is not { } row) return Invalid("Choose one of the listed power settings.");

        // The desired values: listed choices (either side may be left as it is), or an engine-built undo's recorded values.
        uint? wantAc = null, wantDc = null;
        if (request.Undo && p["restore"] is JsonObject restore)
        {
            if (p.Count != 2 || restore.Count != 2 || Long(restore["ac"]) is not { } rAc || Long(restore["dc"]) is not { } rDc
                || rAc < 0 || rDc < 0 || rAc > row.MaxRestore || rDc > row.MaxRestore)
                return Invalid("The recorded values can't be restored.");
            (wantAc, wantDc) = ((uint)rAc, (uint)rDc);
        }
        else
        {
            if (p["restore"] is not null || p.Any(kv => kv.Key is not ("settingId" or "ac" or "dc"))) return Invalid("Send settingId with ac and/or dc.");
            if (p["ac"] is null && p["dc"] is null) return Invalid("Send a value for plugged in (ac), on battery (dc), or both.");
            if (p["ac"] is not null)
            {
                if (Choice(row, p["ac"]) is not { } c) return Invalid($"That isn't one of the choices for {row.Label}.");
                wantAc = c.Index;
            }
            if (p["dc"] is not null)
            {
                if (Choice(row, p["dc"]) is not { } c) return Invalid($"That isn't one of the choices for {row.Label}.");
                wantDc = c.Index;
            }
        }

        // The plan: a fresh request changes the active plan; the recheck must still find that plan active; an undo
        // restores the plan it changed (whether or not it is active now).
        Guid? active;
        try { active = api.GetActiveScheme(); }
        catch (Exception) { active = null; }
        if (active is null) return ActionResolution.Ineligible("read_failed", "Windows didn't report the active power plan.", ManualUri);
        Guid scheme = active.Value;
        if (request.Recheck || request.Undo)
        {
            if (request.Target is not { } t || Str(t["id"]) != row.SettingId || !Guid.TryParse(Str(t["scheme"]), out var planned))
                return Invalid("The target doesn't match the setting.");
            if (!request.Undo && planned != active) return ActionResolution.Ineligible("changed_since", "The active power plan changed. Preview again.", ManualUri);
            scheme = planned;
        }

        try
        {
            if (api.SettingManaged(row.Setting)) return ActionResolution.Ineligible("managed", $"A policy on this PC sets {row.Label}.", ManualUri);
        }
        catch (Exception) { return ActionResolution.Ineligible("read_failed", "Windows didn't report the power policy.", ManualUri); }

        var (ac, dc, rc) = ReadBoth(scheme, row);
        if (rc == ErrorFileNotFound) return ActionResolution.Ineligible("unsupported", $"This PC's power plan has no {row.Label} setting.", ManualUri);
        if (rc != 0) return ActionResolution.Ineligible("read_failed", "Windows didn't report the current setting.", ManualUri);

        var newAc = wantAc ?? ac;
        var newDc = wantDc ?? dc;
        if (newAc == ac && newDc == dc) return ActionResolution.Ineligible("already_set", $"{row.Label} already has that value.");

        var plan = SafePlanName(scheme);
        var planRows = new List<PlanRow>();
        if (newAc != ac) planRows.Add(new PlanRow($"{row.Label} (plugged in)", PowerSettingsCatalog.Text(row, ac), PowerSettingsCatalog.Text(row, newAc)));
        if (newDc != dc) planRows.Add(new PlanRow($"{row.Label} (on battery)", PowerSettingsCatalog.Text(row, dc), PowerSettingsCatalog.Text(row, newDc)));
        return new ActionResolution(
            Eligible: true,
            Target: new JsonObject { ["type"] = "power_setting", ["id"] = row.SettingId, ["scheme"] = scheme.ToString("D"), ["label"] = row.Label },
            Before: State(scheme, ac, dc), After: State(scheme, newAc, newDc),
            Rows: planRows,
            Context: [new("Power plan", plan)],
            Tradeoffs: row.Tradeoffs,
            UndoConditions: "You can undo this unless something else changes these settings first.",
            Resources: [row.Resource(scheme)]);
    }

    internal ActionOutcome Execute(ActionExecution ex)
    {
        var row = Row(Str(ex.Target["id"]));
        if (row is null || !Guid.TryParse(Str(ex.Target["scheme"]), out var scheme) || Long(ex.After["ac"]) is not { } ac || Long(ex.After["dc"]) is not { } dc
            || Long(ex.Before["ac"]) is not { } beforeAc || Long(ex.Before["dc"]) is not { } beforeDc)
            return new ActionOutcome("failed", new JsonObject(), "Nothing changed.", "invalid_arguments");

        uint rc = 0;
        try
        {
            if (ac != beforeAc) rc = api.WriteValue(scheme, row.Subgroup, row.Setting, true, (uint)ac);
            if (rc == 0 && dc != beforeDc) rc = api.WriteValue(scheme, row.Subgroup, row.Setting, false, (uint)dc);
            // Re-activating the same plan applies the new values; a plan that is no longer active is never switched to.
            if (rc == 0 && api.GetActiveScheme() == scheme) rc = api.SetActiveScheme(scheme);
        }
        catch (Exception) { rc = uint.MaxValue; }

        uint nowAc, nowDc, readRc;
        try { (nowAc, nowDc, readRc) = ReadBoth(scheme, row); }
        catch (Exception) { readRc = uint.MaxValue; nowAc = nowDc = 0; }
        if (readRc != 0) return new ActionOutcome("indeterminate", new JsonObject(), $"Windows didn't report {row.Label} after the change.", "unexpected_state", rc == 0 ? null : $"Windows error {rc}");

        var observed = State(scheme, nowAc, nowDc);
        var text = $"{row.Label}: {PowerSettingsCatalog.Text(row, nowAc)} plugged in, {PowerSettingsCatalog.Text(row, nowDc)} on battery";
        if (JsonNode.DeepEquals(observed, ex.After)) return new ActionOutcome("verified", observed, text + ", read back from Windows.");
        if (JsonNode.DeepEquals(observed, ex.Before))
            return new ActionOutcome("failed", observed, text + "; nothing changed.", rc == 0 ? "not_applied" : "write_failed", rc == 0 ? null : $"Windows error {rc}");
        return new ActionOutcome("indeterminate", observed, text + ", which wasn't expected.", "unexpected_state", rc == 0 ? null : $"Windows error {rc}");
    }

    private (uint Ac, uint Dc, uint Rc) ReadBoth(Guid scheme, PowerSettingRow row)
    {
        var rc = api.ReadValue(scheme, row.Subgroup, row.Setting, true, out var ac);
        if (rc != 0) return (0, 0, rc);
        rc = api.ReadValue(scheme, row.Subgroup, row.Setting, false, out var dc);
        return (ac, dc, rc);
    }

    private static JsonObject State(Guid scheme, uint ac, uint dc) =>
        new() { ["scheme"] = scheme.ToString("D"), ["ac"] = (long)ac, ["dc"] = (long)dc };

    private string SafePlanName(Guid scheme)
    {
        try { return api.EnumerateSchemes().FirstOrDefault(s => s.Guid == scheme) is { } s ? SelectSchemeHandler.PlanName(s) : "Current plan"; }
        catch (Exception) { return "Current plan"; }
    }

    private static PowerChoice? Choice(PowerSettingRow row, JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) ? row.Choices.FirstOrDefault(c => c.Value == s) : null;

    private static ActionResolution Invalid(string message) => ActionResolution.Ineligible("invalid_arguments", message);

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? Long(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number
        && long.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var l) ? l : null;
}

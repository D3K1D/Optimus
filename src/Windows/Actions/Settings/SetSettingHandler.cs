using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;

namespace OptimizationTool.Windows.Actions.Settings;

/// <summary>
/// setting.set {settingId, value} (contracts/m9-ai-powers.md §3.2, §3.3): sets one user-scope <see cref="SettingsCatalog"/>
/// row to one of its listed values, then reads it back through the same store. Undo restores the exact previous data
/// or absence; the engine offers it only while the value is still what was written. Never retries.
/// </summary>
internal sealed class SetSettingHandler(IRegistryStore store, ISettingNotifier notifier, Func<int> osBuild, IReadOnlyList<SettingRow>? rows = null) : IActionHandler
{
    public const string Id = "setting.set";
    private const string ManualUri = "ms-settings:";
    private readonly IReadOnlyList<SettingRow> _rows = rows ?? SettingsCatalog.Rows;

    public string ActionId => Id;
    public const string HandlerVersion = "1.0.0", LabelText = "Change a Windows setting";
    public string Version => HandlerVersion;
    public string Label => LabelText;
    public string Privilege => "none";
    public bool SupportsUndo => true;
    public string Kind => "setting";
    public string Group => "settings";
    public string What => "Sets one listed Windows setting to one of its listed values; a setting for the whole PC needs administrator approval.";
    public IReadOnlyList<string> Flags => [];
    public JsonObject? ParameterSchema => new()
    {
        ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("settingId", "value"),
        ["properties"] = new JsonObject
        {
            ["settingId"] = new JsonObject { ["enum"] = new JsonArray(_rows.Select(r => (JsonNode?)r.SettingId).ToArray()) },
            ["value"] = new JsonObject { ["type"] = "string", ["maxLength"] = 32 },
        },
        // The values each setting accepts (the guide lists them under action:setting.set).
        ["oneOf"] = new JsonArray(_rows.Select(r => (JsonNode?)new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["settingId"] = new JsonObject { ["const"] = r.SettingId },
                ["value"] = new JsonObject { ["enum"] = new JsonArray(r.Values.Select(v => (JsonNode?)v.Value).ToArray()) },
            },
        }).ToArray()),
    };
    // Several rows change what a scan or benchmark measures (background recording, transparency): take the lease.
    public bool TakesLease => true;

    /// <summary>A listed row: user scope (HKCU) or machine scope (HKLM, administrator: through the broker, m9 §3.2).</summary>
    private SettingRow? Row(string? id) => _rows.FirstOrDefault(r => r.SettingId == id && (r.Scope == "user" && r.Hive == "HKCU" || r.Scope == "machine" && r.Hive == "HKLM"));

    /// <summary>Machine rows run elevated: this handler is registered in the broker too (m9 §3.5).</summary>
    public bool CanElevate => true;

    public Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct) => Task.Run(() => Resolve(request), ct);

    public Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct) => Task.Run(() =>
    {
        var row = Row(Str(target["id"])) ?? throw new ArgumentException("Unknown setting.", nameof(target));
        return State(row, store.Read(row.Location));
    }, ct);

    public Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct) =>
        Task.Run(() => Execute(execution), ct);

    public ActionRequest? UndoRequest(JsonObject target, JsonObject before)
    {
        if (Row(Str(target["id"])) is not { } row) return null;
        return new ActionRequest(Id, new JsonObject { ["type"] = "setting", ["id"] = row.SettingId },
            new JsonObject
            {
                ["settingId"] = row.SettingId,
                ["restore"] = new JsonObject { ["present"] = before["present"]?.DeepClone(), ["type"] = before["type"]?.DeepClone(), ["data"] = before["data"]?.DeepClone() },
            }, null) { Undo = true };
    }

    internal ActionResolution Resolve(ActionRequest request)
    {
        if (request.ActionId != Id) return ActionResolution.Ineligible("wrong_action", "This request is for a different action.");
        var p = request.Parameters;
        if (p is null) return Invalid("Choose a setting and a value.");
        var row = Row(Str(p["settingId"]));
        if (row is null) return Invalid("That setting isn't in the list Optimus can change.");

        // The desired data: a listed value, or (an engine-built undo only) the exact recorded preimage.
        RegData desired;
        if (request.Undo && p["restore"] is JsonObject restore)
        {
            if (p.Count != 2 || Restore(row, restore) is not { } r) return Invalid("The recorded value can't be restored.");
            desired = r;
        }
        else
        {
            if (p.Count != 2 || p["restore"] is not null) return Invalid("Choose a setting and a value.");
            var value = row.Values.FirstOrDefault(v => v.Value == Str(p["value"]));
            if (value is null) return Invalid($"That isn't one of the choices for {row.Label}.");
            desired = Data(row, value);
        }
        if (request.Target is { } t && Str(t["id"]) is { } tid && tid != row.SettingId) return Invalid("The target doesn't match the setting.");

        if (osBuild() < row.MinBuild) return ActionResolution.Ineligible("unsupported", $"{row.Label} needs a newer version of Windows.");
        try
        {
            if (row.PolicyKeys.Any(store.Exists)) return ActionResolution.Ineligible("managed", $"A policy on this PC sets {row.Label}.");
        }
        catch (Exception) { return ActionResolution.Ineligible("read_failed", "Windows didn't report the setting's policy.", ManualUri); }

        RegData current;
        try { current = store.Read(row.Location); }
        catch (Exception) { return ActionResolution.Ineligible("read_failed", "Windows didn't report the current setting.", ManualUri); }
        if (current.Present && current.Type != row.Type)
            return ActionResolution.Ineligible("unexpected_value", $"{row.Label} holds a value Optimus doesn't recognise.", ManualUri);

        var before = State(row, current);
        var after = State(row, desired);
        if (Same(before, after)) return ActionResolution.Ineligible("already_set", $"{row.Label} is already {Text(row, desired)}.");

        return new ActionResolution(
            Eligible: true,
            Target: new JsonObject { ["type"] = "setting", ["id"] = row.SettingId, ["label"] = row.Label },
            Before: before, After: after,
            Rows: [new PlanRow(row.Label, Text(row, current), Text(row, desired))],
            Context: [new("Applies to", row.Scope == "machine" ? "Everyone on this PC" : "Your account")],
            Tradeoffs: row.Tradeoffs,
            UndoConditions: $"You can undo this while {row.Label} is still {Text(row, desired)}.",
            Flags: row.Flags,
            Resources: [row.Resource],
            Privilege: row.Scope == "machine" ? "admin" : null);
    }

    internal ActionOutcome Execute(ActionExecution ex)
    {
        var row = Row(Str(ex.Target["id"]));
        if (row is null) return new ActionOutcome("failed", new JsonObject(), "Nothing changed.", "invalid_arguments", "Unknown setting.");
        var desired = FromState(ex.After);
        if (desired is null || desired.Present && desired.Type != row.Type)
            return new ActionOutcome("failed", ex.Before.DeepClone().AsObject(), "Nothing changed.", "invalid_arguments");

        RegData? read = null;
        string? error = null;
        try { read = desired.Present ? store.Write(row.Location, row.Type, desired.Data!) : store.Delete(row.Location); }
        catch (Exception e) { error = e.GetType().Name; }
        if (read is null)
            try { read = store.Read(row.Location); }
            catch (Exception) { read = null; }

        if (read is null)
            return new ActionOutcome("indeterminate", new JsonObject(), $"Windows didn't report {row.Label} after the change.", "unexpected_state", error);
        var observed = State(row, read);
        if (Same(observed, ex.After))
        {
            if (row.Notify != "none")
                try { notifier.Notify(row.Notify); } catch (Exception) { /* the value is verified; a missed broadcast only delays the refresh */ }
            return new ActionOutcome("verified", observed, $"{row.Label} is {Text(row, read)}, read back from Windows.");
        }
        if (Same(observed, ex.Before))
            return new ActionOutcome("failed", observed, $"{row.Label} is still {Text(row, read)}.", error is null ? "not_applied" : "write_failed", error);
        return new ActionOutcome("indeterminate", observed, $"{row.Label} is {Text(row, read)}, which wasn't expected.", "unexpected_state", error);
    }

    // ---------------------------------------------------------------- shapes

    private static ActionResolution Invalid(string message) => ActionResolution.Ineligible("invalid_arguments", message);

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool Same(JsonNode a, JsonNode b) => JsonNode.DeepEquals(a, b);

    private static RegData Data(SettingRow row, SettingValue value) =>
        value.Data is null ? RegData.Absent : new RegData(true, row.Type, value.Data.DeepClone());

    /// <summary>The target's state as plans, receipts and drift checks compare it: {present, type, data, value}.</summary>
    internal static JsonObject State(SettingRow row, RegData d) => new()
    {
        ["present"] = d.Present, ["type"] = d.Type, ["data"] = d.Data?.DeepClone(), ["value"] = Listed(row, d)?.Value,
    };

    private static SettingValue? Listed(SettingRow row, RegData d) =>
        row.Values.FirstOrDefault(v => d.Present ? v.Data is not null && d.Type == row.Type && Same(v.Data, d.Data!) : v.Data is null);

    private static RegData? FromState(JsonObject s)
    {
        if (s["present"] is not JsonValue pv || !pv.TryGetValue<bool>(out var present)) return null;
        if (!present) return RegData.Absent;
        return Str(s["type"]) is { } type && s["data"] is JsonValue data ? new RegData(true, type, data.DeepClone()) : null;
    }

    /// <summary>An undo's preimage: absent, or data of the row's own type (a dword in range, or a short string).</summary>
    private static RegData? Restore(SettingRow row, JsonObject restore)
    {
        var d = FromState(restore);
        if (d is null || !d.Present) return d;
        if (d.Type != row.Type) return null;
        return row.Type switch
        {
            "dword" when d.Data is JsonValue v && v.TryGetValue<long>(out var n) && n is >= 0 and <= uint.MaxValue => RegData.Dword((uint)n),
            "string" when d.Data is JsonValue v && v.TryGetValue<string>(out var s) && s.Length <= 1024 => RegData.String(s),
            _ => null,
        };
    }

    /// <summary>What the user sees for a value: its listed label, "Not set" when absent, else the raw data.</summary>
    internal static string Text(SettingRow row, RegData d)
    {
        if (!d.Present) return row.Values.FirstOrDefault(v => v.Data is null)?.Label ?? "Not set";
        return Listed(row, d)?.Label ?? (d.Type == "dword" ? $"Value {d.Data}" : "Another value");
    }
}

/// <summary>The settings domain's handlers (setting.set over <see cref="SettingsCatalog"/>).</summary>
public sealed class SettingsActionModule : IActionModule
{
    public IEnumerable<IActionHandler> Handlers(ActionContext context) =>
        [new SetSettingHandler(new WindowsRegistryStore(), new WindowsSettingNotifier(), () => Environment.OSVersion.Version.Build)];
}

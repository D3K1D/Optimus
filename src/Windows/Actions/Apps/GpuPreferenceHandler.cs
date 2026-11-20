using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Processes;
using OptimizationTool.Windows.Actions.Protection;
using OptimizationTool.Windows.Actions.Settings;

namespace OptimizationTool.Windows.Actions.Apps;

/// <summary>How many hardware graphics adapters this PC has (DXGI, software adapters left out); null when it can't be read.</summary>
internal interface IGpuAdapters
{
    int? HardwareAdapterCount();
}

/// <summary>Real count through the win.system.hardware collector's own DXGI reader. Read-only.</summary>
internal sealed class WindowsGpuAdapters : IGpuAdapters
{
    public int? HardwareAdapterCount()
    {
        try { return SystemDomain.HardwareCollector.ReadGpus(CancellationToken.None).Count; }
        catch (Exception) { return null; }
    }
}

/// <summary>
/// app.set_gpu_preference {targetRef, preference} (contracts/m9-ai-powers.md §3.2): the per-app graphics preference that
/// Settings › System › Display › Graphics writes: HKCU\Software\Microsoft\DirectX\UserGpuPreferences, value name = the
/// app's full path, REG_SZ data "GpuPreference=N;" (0 let Windows decide, 1 power saving, 2 high performance), possibly
/// with other "Name=Value;" pairs Windows keeps in the same string (they are preserved). The app is a running process
/// named by sample evidence; its full path is read live from that exact process (never from a caller) and pinned in the
/// plan target. Refused: a PC with one graphics adapter, protected and hardware-list apps, Windows' own programs. Undo
/// restores the exact previous data or absence. Messages and receipts never name the app or its path (the AI reads them);
/// only the plan rows show the app's file name.
/// </summary>
internal sealed class GpuPreferenceHandler(IRegistryStore store, IProcessControl processes, IGpuAdapters gpus, ISecurityProducts? products = null,
    string? windowsDirectory = null, IFileFacts? files = null) : IActionHandler
{
    public const string Id = "app.set_gpu_preference";

    private static string? PublisherOf(IFileFacts? files, string path)
    {
        try { return files?.Publisher(path); } catch (Exception) { return null; }
    }
    public const string CollectorId = "win.perf.sample";
    internal const string Key = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string ManualUri = "ms-settings:display-advancedgraphics";
    private const int MaxData = 2048;
    private readonly string _windows = (windowsDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows)).TrimEnd('\\') + "\\";

    internal static readonly (string Value, int N, string Label)[] Preferences =
    [
        ("default", 0, "Let Windows decide"), ("power_saving", 1, "Power saving"), ("high_performance", 2, "High performance"),
    ];

    public string ActionId => Id;
    public string Version => "1.1.0";
    public string Label => "Choose an app's graphics processor";
    public string Privilege => "none";
    public bool SupportsUndo => true;
    public string Kind => "setting";
    public string Group => "apps";
    public string What => "Sets which graphics processor Windows gives one app, as the Graphics page in Settings does; the app uses it the next time it starts.";
    public IReadOnlyList<string> Flags => [];
    public JsonObject? ParameterSchema => new()
    {
        ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("preference"),
        ["properties"] = new JsonObject { ["preference"] = new JsonObject { ["enum"] = new JsonArray(Preferences.Select(p => (JsonNode?)p.Value).ToArray()) } },
    };
    public bool TakesLease => false;
    public string? TargetKind => "process (from a performance sample)";

    internal static RegLocation At(string path) => new("HKCU", 64, Key, path);

    public Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct) => Task.Run(() => Resolve(request), ct);

    public Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct) => Task.Run(() =>
    {
        var path = FromTarget(target) ?? throw new ArgumentException("Not an app target.", nameof(target));
        return State(store.Read(At(path))) ?? new JsonObject { ["present"] = true, ["unreadable"] = true };
    }, ct);

    public Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct) =>
        Task.Run(() => Execute(execution), ct);

    public ActionRequest? UndoRequest(JsonObject target, JsonObject before)
    {
        if (FromTarget(target) is null || before["present"] is not JsonValue) return null;
        return new ActionRequest(Id, (JsonObject)target.DeepClone(),
            new JsonObject { ["restore"] = new JsonObject { ["present"] = before["present"]?.DeepClone(), ["data"] = before["data"]?.DeepClone() } }, null) { Undo = true };
    }

    // ---------------------------------------------------------------- resolve

    internal ActionResolution Resolve(ActionRequest request)
    {
        if (request.ActionId != Id) return ActionResolution.Ineligible("wrong_action", "This request is for a different action.");
        var p = request.Parameters;

        // The desired preference, or (an engine-built undo only) the exact recorded preimage.
        int? wanted = null;
        RegData? restore = null;
        if (request.Undo)
        {
            if (p is not { Count: 1 } || p["restore"] is not JsonObject r || Restore(r) is not { } d) return Invalid("The recorded value can't be restored.");
            restore = d;
        }
        else
        {
            if (p is not { Count: 1 } || Str(p["preference"]) is not { } pref || Preferences.FirstOrDefault(x => x.Value == pref) is not { Value: not null } choice)
                return Invalid("Choose default, power_saving or high_performance.");
            wanted = choice.N;
        }

        // The app: this handler's own plan target (recheck, undo), or a running process named by sample evidence.
        string path;
        if (request.Recheck || request.Undo)
        {
            if (request.Target is null || FromTarget(request.Target) is not { } t) return Unknown();
            path = t;
        }
        else
        {
            if (Locate(request.Evidence) is not { } located) return Unknown();
            if (located.Refusal is { } refusal) return refusal;
            path = located.Path!;
        }
        var label = FileName(path);

        if (path.StartsWith(_windows, StringComparison.OrdinalIgnoreCase)) return ActionResolution.Ineligible(Protection.Protection.ProtectedCode, "This is part of Windows.", ManualUri);
        // The executable's signer, read here (never from the evidence): a security product's helper with a generic file name is protected too.
        if (ProtectedTargets.Process(path, PublisherOf(files, path), (products ?? NoSecurityProducts.Instance).Names()) is { } prot)
            return ActionResolution.Ineligible(prot.Code, prot.Reason, ManualUri);

        int? adapters;
        try { adapters = gpus.HardwareAdapterCount(); }
        catch (Exception) { adapters = null; }
        if (adapters is null) return ActionResolution.Ineligible("read_failed", "Windows didn't report the graphics adapters.", ManualUri);
        if (adapters < 2) return ActionResolution.Ineligible("unsupported", "This PC has one graphics processor, so there is nothing to choose.", ManualUri);

        RegData current;
        try { current = store.Read(At(path)); }
        catch (Exception) { return ActionResolution.Ineligible("read_failed", "Windows didn't report the app's graphics setting.", ManualUri); }
        if (State(current) is not { } before)
            return ActionResolution.Ineligible("unexpected_value", "The app's graphics setting holds a value Optimus doesn't recognise.", ManualUri);

        RegData desired = restore ?? With(current, wanted!.Value);
        var after = State(desired)!;
        if (JsonNode.DeepEquals(before, after) || restore is null && PreferenceOf(before) == wanted)
            return ActionResolution.Ineligible("already_set", $"The app's graphics preference is already {LabelOf(PreferenceOf(before))}.");

        return new ActionResolution(
            Eligible: true,
            Target: new JsonObject { ["type"] = "app", ["id"] = IdOf(path), ["path"] = path, ["label"] = label },
            Before: before, After: after,
            Rows: [new PlanRow("Graphics preference", LabelOf(PreferenceOf(before)), LabelOf(PreferenceOf(after))), new PlanRow("App", label, label)],
            Context: [new("Applies to", "Your account"), new("Takes effect", "The next time the app starts")],
            Tradeoffs: wanted == 2 ? ["High performance uses more power; on a laptop the battery runs down faster."]
                : wanted == 1 ? ["Power saving can make games and 3D apps slower."] : [],
            UndoConditions: $"You can undo this while this app’s graphics setting is still {LabelOf(PreferenceOf(after))}.",
            Resources: [Resource(path)]);
    }

    /// <summary>The running process the sample evidence names: same PID, creation time and image name now. Null = unknown.</summary>
    private (string? Path, ActionResolution? Refusal)? Locate(TargetEvidence? ev)
    {
        if (ev is null || ev.CollectorId != CollectorId || !ev.Subject.StartsWith("perf.process.", StringComparison.Ordinal)) return null;
        var v = ev.Value;
        if (Long(v["pid"]) is not { } pid || pid is <= 4 or > uint.MaxValue || Long(v["createTime"]) is not { } created || created <= 0
            || Str(v["image"]) is not { Length: > 0 and <= 260 } image)
            return null;
        ProcessInfo? live;
        try { live = processes.Query((uint)pid); }
        catch (Exception) { return (null, ActionResolution.Ineligible("read_failed", "Windows didn't report the app.", ManualUri)); }
        if (live is null || live.CreateTime != created)
            return (null, ActionResolution.Ineligible("not_found", "That app isn't running any more. Open it, then check again.", ManualUri));
        if (!string.Equals(FileName(live.ImagePath), image, StringComparison.OrdinalIgnoreCase)) return null;
        if (!ValidPath(live.ImagePath)) return (null, ActionResolution.Ineligible("unsupported", "Optimus can't set a graphics preference for this program.", ManualUri));
        return (live.ImagePath, null);
    }

    // ---------------------------------------------------------------- execute

    internal ActionOutcome Execute(ActionExecution ex)
    {
        if (FromTarget(ex.Target) is not { } path || FromState(ex.After) is not { } desired)
            return new ActionOutcome("failed", new JsonObject(), "Nothing changed.", "invalid_arguments");
        RegData? read = null;
        string? error = null;
        try { read = desired.Present ? store.Write(At(path), "string", desired.Data!) : store.Delete(At(path)); }
        catch (Exception e) { error = e.GetType().Name; }
        if (read is null)
            try { read = store.Read(At(path)); }
            catch (Exception) { read = null; }
        if (read is null || State(read) is not { } observed)
            return new ActionOutcome("indeterminate", new JsonObject(), "Windows didn't report the app's graphics setting after the change.", "unexpected_state", error);
        var text = LabelOf(PreferenceOf(observed));
        if (JsonNode.DeepEquals(observed, ex.After)) return new ActionOutcome("verified", observed, $"The app's graphics preference is {text}, read back from Windows.");
        if (JsonNode.DeepEquals(observed, ex.Before))
            return new ActionOutcome("failed", observed, $"The app's graphics preference is still {text}.", error is null ? "not_applied" : "write_failed", error);
        return new ActionOutcome("indeterminate", observed, "The app's graphics setting has a value that wasn't expected.", "unexpected_state", error);
    }

    // ---------------------------------------------------------------- data

    /// <summary>The state plans compare: {present, data, preference}; null when the value isn't a string Optimus can read.</summary>
    internal static JsonObject? State(RegData d)
    {
        if (!d.Present) return new JsonObject { ["present"] = false, ["data"] = null, ["preference"] = "default" };
        if (d.Type != "string" || d.Data is not JsonValue v || !v.TryGetValue<string>(out var s) || Parse(s) is not { } pairs) return null;
        var n = pairs.FirstOrDefault(kv => kv.Key.Equals("GpuPreference", StringComparison.OrdinalIgnoreCase)).Value;
        var pref = n is null ? "default" : Preferences.FirstOrDefault(x => x.N.ToString(System.Globalization.CultureInfo.InvariantCulture) == n).Value;
        if (pref is null) return null;
        return new JsonObject { ["present"] = true, ["data"] = s, ["preference"] = pref };
    }

    private static RegData? FromState(JsonObject s)
    {
        if (s["present"] is not JsonValue pv || !pv.TryGetValue<bool>(out var present)) return null;
        if (!present) return RegData.Absent;
        return Str(s["data"]) is { } data && Parse(data) is not null ? RegData.String(data) : null;
    }

    /// <summary>"Name=Value;" pairs in order; null when the text isn't that shape (or is too long).</summary>
    internal static List<KeyValuePair<string, string>>? Parse(string s)
    {
        if (s.Length > MaxData || s.Any(char.IsControl)) return null;
        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var part in s.Split(';'))
        {
            if (part.Length == 0) continue;
            var eq = part.IndexOf('=');
            if (eq <= 0 || part.IndexOf('=', eq + 1) >= 0) return null;
            var name = part[..eq];
            if (!name.All(char.IsAsciiLetterOrDigit)) return null;
            if (pairs.Any(kv => kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase))) return null;
            pairs.Add(new(name, part[(eq + 1)..]));
        }
        return pairs;
    }

    /// <summary>The current data with GpuPreference set to <paramref name="n"/> (other pairs kept in order), or "GpuPreference=n;".</summary>
    internal static RegData With(RegData current, int n)
    {
        var pairs = current.Present && current.Data is JsonValue v && v.TryGetValue<string>(out var s) ? Parse(s) ?? [] : [];
        var value = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var i = pairs.FindIndex(kv => kv.Key.Equals("GpuPreference", StringComparison.OrdinalIgnoreCase));
        if (i >= 0) pairs[i] = new(pairs[i].Key, value);
        else pairs.Add(new("GpuPreference", value));
        var sb = new StringBuilder();
        foreach (var kv in pairs) sb.Append(kv.Key).Append('=').Append(kv.Value).Append(';');
        return RegData.String(sb.ToString());
    }

    private static RegData? Restore(JsonObject r)
    {
        if (r.Count != 2 || r["present"] is not JsonValue pv || !pv.TryGetValue<bool>(out var present)) return null;
        if (!present) return r["data"] is null ? RegData.Absent : null;
        return Str(r["data"]) is { } data && Parse(data) is not null ? RegData.String(data) : null;
    }

    private static int PreferenceOf(JsonObject state) => Preferences.First(p => p.Value == Str(state["preference"])).N;

    private static string LabelOf(int n) => Preferences.First(p => p.N == n).Label;

    internal static string IdOf(string path) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..16];

    /// <summary>The value's lock key: the preferences key plus a hash of the path (one spelling per app; the path itself stays out).</summary>
    internal static string Resource(string path) => $@"reg:64:HKCU\SOFTWARE\MICROSOFT\DIRECTX\USERGPUPREFERENCES\{IdOf(path)}";

    private static string? FromTarget(JsonObject t) =>
        Str(t["type"]) == "app" && Str(t["path"]) is { } path && ValidPath(path) && Str(t["id"]) == IdOf(path) ? path : null;

    /// <summary>A full local path to an .exe: drive-rooted, no relative parts, no control characters, at most 1024 characters.</summary>
    internal static bool ValidPath(string path) =>
        path.Length is > 7 and <= 1024 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\'
        && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !path.Any(char.IsControl) && !path.Contains("\\..\\", StringComparison.Ordinal)
        && !path.Contains("\\.\\", StringComparison.Ordinal) && path.IndexOfAny(['"', '<', '>', '|', '*', '?', '/']) < 0;

    private static ActionResolution Invalid(string message) => ActionResolution.Ineligible("invalid_arguments", message);

    private static ActionResolution Unknown() =>
        ActionResolution.Ineligible(ActionService.UnknownTarget, "Optimus can't match that target to one running app from its sample.");

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? Long(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number
        && long.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var l) ? l : null;

    internal static string FileName(string path)
    {
        var i = path.LastIndexOfAny(['\\', '/']);
        return i >= 0 ? path[(i + 1)..] : path;
    }
}

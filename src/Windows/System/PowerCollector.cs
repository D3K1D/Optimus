using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using OptimizationTool.Engine;

namespace OptimizationTool.Windows.SystemDomain;

internal sealed record PowerSchemeRaw(Guid Guid, string? Name, int? Personality);
internal sealed record PowerStatusRaw(byte AcLineStatus, byte BatteryFlag, byte BatteryPercent, byte SystemStatusFlag);
internal sealed record PowerRaw(
    IReadOnlyList<PowerSchemeRaw>? Schemes, Guid? Active, PowerStatusRaw? Status,
    bool OverlaySupported, Guid? Overlay, IReadOnlyList<string> Failures);

/// <summary>
/// win.system.power: power schemes (PowerEnumerate), active scheme, AC/DC + battery, and the Windows power mode
/// overlay where the build exposes it. Scheme, power mode and OEM modes are separate capabilities; this reads the
/// first two and never changes either.
/// </summary>
internal sealed class PowerCollector(CollectorOptions options) : SystemCollector<PowerRaw>(options)
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid UltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    // Overlay ("power mode") GUIDs; an all-zero overlay means the balanced / recommended mode.
    private static readonly Guid OverlayBestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid OverlayBetterBattery = new("3af9b8d9-7c97-431d-ad78-34a8bfea439f");
    private static readonly Guid OverlayBestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    private static readonly Guid NoSubgroup = new("fea3413e-7e05-4911-9a71-700331f1c294");
    private static readonly Guid SchemePersonality = new("245d8541-3943-4422-b025-13a784f679b7");
    private const int SchemeCap = 32;

    public override string Id => "win.system.power";
    public override string Category => "performance";
    public override string GapTitle => "Power plan";
    public override string GapSource => "Windows power management";

    protected override unsafe PowerRaw Read(CancellationToken ct)
    {
        var failures = new List<string>();
        List<PowerSchemeRaw>? schemes = null;
        try { schemes = ReadSchemes(ct); } catch (Exception e) when (e is not OperationCanceledException) { failures.Add("schemes"); }

        Guid? active = null;
        if (Native.PowerGetActiveScheme(0, out var p) == Native.ErrorSuccess && p != null)
        {
            active = *p;
            Native.LocalFree((nint)p);
        }
        else failures.Add("active scheme");

        PowerStatusRaw? status = null;
        if (Native.GetSystemPowerStatus(out var s))
            status = new PowerStatusRaw(s.ACLineStatus, s.BatteryFlag, s.BatteryLifePercent, s.SystemStatusFlag);
        else failures.Add("power source");

        var (supported, overlay) = ReadOverlay();
        return new PowerRaw(schemes, active, status, supported, overlay, failures);
    }

    internal static unsafe List<PowerSchemeRaw> ReadSchemes(CancellationToken ct)
    {
        var list = new List<PowerSchemeRaw>();
        for (uint i = 0; i < SchemeCap; i++)
        {
            ct.ThrowIfCancellationRequested();
            Guid g;
            uint size = (uint)sizeof(Guid);
            var rc = Native.PowerEnumerate(0, null, null, Native.AccessScheme, i, (byte*)&g, ref size);
            if (rc == Native.ErrorNoMoreItems) break;
            if (rc != Native.ErrorSuccess) throw new global::System.ComponentModel.Win32Exception((int)rc);
            list.Add(new PowerSchemeRaw(g, ReadName(g), ReadPersonality(g)));
        }
        return list;
    }

    private static unsafe string? ReadName(Guid scheme)
    {
        uint size = 0;
        if (Native.PowerReadFriendlyName(0, &scheme, null, null, null, ref size) != Native.ErrorSuccess || size is 0 or > 4096) return null;
        var buf = stackalloc byte[(int)size];
        if (Native.PowerReadFriendlyName(0, &scheme, null, null, buf, ref size) != Native.ErrorSuccess) return null;
        var name = new string((char*)buf).Trim();
        return name.Length == 0 ? null : name;
    }

    private static unsafe int? ReadPersonality(Guid scheme)
    {
        Guid sub = NoSubgroup, setting = SchemePersonality;
        return Native.PowerReadACValueIndex(0, &scheme, &sub, &setting, out var v) == Native.ErrorSuccess ? (int)v : null;
    }

    private static unsafe (bool Supported, Guid? Overlay) ReadOverlay()
    {
        // PowerGetEffectiveOverlayScheme is undocumented-but-exported on Windows 10 1709+; probe it, don't assume.
        if (!NativeLibrary.TryLoad("powrprof.dll", out var lib)) return (false, null);
        try
        {
            if (!NativeLibrary.TryGetExport(lib, "PowerGetEffectiveOverlayScheme", out var fn)) return (false, null);
            Guid g;
            var rc = ((delegate* unmanaged[Stdcall]<Guid*, uint>)fn)(&g);
            return rc == Native.ErrorSuccess ? (true, g) : (false, null);
        }
        finally { NativeLibrary.Free(lib); }
    }

    internal static string PersonalityName(int? p, Guid guid)
    {
        if (guid == UltimatePerformance || guid == HighPerformance) return "high_performance";
        if (guid == Balanced) return "balanced";
        if (guid == PowerSaver) return "power_saver";
        return p switch { 0 => "power_saver", 1 => "high_performance", 2 => "balanced", _ => "unknown" };
    }

    private static bool IsBuiltIn(Guid g) => g == Balanced || g == HighPerformance || g == PowerSaver || g == UltimatePerformance;

    private static string OverlayName(Guid g) =>
        g == Guid.Empty ? "balanced" :
        g == OverlayBestEfficiency ? "best_power_efficiency" :
        g == OverlayBetterBattery ? "better_battery" :
        g == OverlayBestPerformance ? "best_performance" : "other";

    public override CollectorResult Normalize(PowerRaw raw)
    {
        var facts = new List<Fact>();
        var gaps = new List<string>(raw.Failures);

        if (raw.Schemes is { } schemes)
        {
            var arr = new JsonArray(schemes.Select(s => (JsonNode?)new JsonObject
            {
                ["guid"] = s.Guid.ToString("D"), ["name"] = s.Name, ["personality"] = PersonalityName(s.Personality, s.Guid),
                ["builtIn"] = IsBuiltIn(s.Guid), ["active"] = s.Guid == raw.Active,
            }).ToArray());
            var hasBalanced = schemes.Any(s => s.Guid == Balanced);
            facts.Add(new Fact("power.schemes", "Windows power scheme inventory", "measured",
                new JsonObject { ["count"] = schemes.Count, ["hasBalanced"] = hasBalanced, ["schemes"] = arr },
                schemes.Count == 1 ? "One power plan is available." : $"{schemes.Count} power plans are available.", null));
        }

        if (raw.Active is { } active)
        {
            var match = raw.Schemes?.FirstOrDefault(s => s.Guid == active);
            var personality = PersonalityName(match?.Personality, active);
            var name = match?.Name ?? personality.Replace('_', ' ');
            facts.Add(new Fact("power.active_scheme", "Windows power scheme inventory", "measured",
                new JsonObject
                {
                    ["guid"] = active.ToString("D"), ["name"] = match?.Name, ["personality"] = personality,
                    ["builtIn"] = IsBuiltIn(active), ["ultimate"] = active == UltimatePerformance,
                },
                $"The active power plan is {name}.", null));
        }

        if (raw.Status is { } st)
        {
            string source = st.AcLineStatus switch { 0 => "battery", 1 => "ac", _ => "unknown" };
            bool? hasBattery = st.BatteryFlag == 255 ? null : (st.BatteryFlag & 128) == 0;
            int? percent = hasBattery == true && st.BatteryPercent <= 100 ? st.BatteryPercent : null;
            var display = source switch
            {
                "battery" => percent is { } bp ? $"Running on battery at {bp}%." : "Running on battery.",
                "ac" when hasBattery == true => percent is { } bp ? $"Plugged in, battery at {bp}%." : "Plugged in.",
                "ac" => "Plugged in; no battery reported.",
                _ => "Power source not reported.",
            };
            facts.Add(new Fact("power.source", "System power status", "measured",
                new JsonObject
                {
                    ["source"] = source, ["hasBattery"] = hasBattery, ["batteryPercent"] = percent,
                    ["batterySaverOn"] = (st.SystemStatusFlag & 1) != 0,
                },
                display, null));
        }

        facts.Add(raw.OverlaySupported && raw.Overlay is { } ov
            ? new Fact("power.mode", "Windows power mode", "measured",
                new JsonObject { ["supported"] = true, ["mode"] = OverlayName(ov), ["overlayGuid"] = ov.ToString("D") },
                $"Power mode is {OverlayName(ov).Replace('_', ' ')}.", null)
            : new Fact("power.mode", "Windows power mode", "unavailable",
                new JsonObject { ["supported"] = false }, "Power mode is not reported on this PC.", null));

        if (gaps.Count == 0) return Sys.Result(facts, "complete", raw.OverlaySupported ? null : "power mode not exposed");
        if (raw.Schemes is null && raw.Active is null && raw.Status is null) return Sys.Result(facts, "failed", "power APIs unavailable");
        return Sys.Result(facts, "partial", string.Join(", ", gaps) + " unavailable");
    }
}

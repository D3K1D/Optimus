using System.Runtime.InteropServices;
using Microsoft.Win32;
using OptimizationTool.Windows.SystemDomain;

namespace OptimizationTool.Windows.Actions.Power;

internal sealed record SchemeInfo(Guid Guid, string? Name, string Personality);

/// <summary>Power source as shown in plan context. Source: battery | ac | unknown.</summary>
internal sealed record SourceInfo(string Source, bool? HasBattery, int? BatteryPercent);

/// <summary>
/// The handler's read/write seam. <see cref="SetActiveScheme"/> is the only write. Tests use a fake; no test may
/// call the real implementation's write.
/// </summary>
internal interface IPowerApi
{
    IReadOnlyList<SchemeInfo> EnumerateSchemes();
    Guid? GetActiveScheme();
    /// <summary>Win32 error code from PowerSetActiveScheme (0 = success).</summary>
    uint SetActiveScheme(Guid scheme);
    SourceInfo? GetSource();
    /// <summary>Group policy forces the active scheme (HKLM\SOFTWARE\Policies\Microsoft\Power\PowerSettings\ActivePowerScheme).</summary>
    bool PolicyForcesActiveScheme();
    /// <summary>An MDM power policy sets the active scheme (PolicyManager current device Power).</summary>
    bool ManagedActiveScheme();

    // power.set_value (m9 §3.2): one value index of one listed setting in one scheme, AC or DC.

    /// <summary>Win32 error code from PowerReadACValueIndex / PowerReadDCValueIndex (0 = success).</summary>
    uint ReadValue(Guid scheme, Guid subgroup, Guid setting, bool ac, out uint value);
    /// <summary>Win32 error code from PowerWriteACValueIndex / PowerWriteDCValueIndex (0 = success). The value applies once the scheme is re-activated.</summary>
    uint WriteValue(Guid scheme, Guid subgroup, Guid setting, bool ac, uint value);
    /// <summary>A group policy or MDM policy sets this power setting (or the active scheme), so a write would not stick.</summary>
    bool SettingManaged(Guid setting);
}

internal static partial class PowerWriteNative
{
    // The single write in this handler: current user, no elevation.
    [LibraryImport("powrprof.dll")]
    public static partial uint PowerSetActiveScheme(nint userRootPowerKey, in Guid schemeGuid);

    // power.set_value's writes: one listed setting's AC or DC value index in the current user's scheme.
    [LibraryImport("powrprof.dll")]
    public static partial uint PowerWriteACValueIndex(nint rootPowerKey, in Guid schemeGuid, in Guid subGroupGuid, in Guid settingGuid, uint valueIndex);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerWriteDCValueIndex(nint rootPowerKey, in Guid schemeGuid, in Guid subGroupGuid, in Guid settingGuid, uint valueIndex);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerReadACValueIndex(nint rootPowerKey, in Guid schemeGuid, in Guid subGroupGuid, in Guid settingGuid, out uint valueIndex);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerReadDCValueIndex(nint rootPowerKey, in Guid schemeGuid, in Guid subGroupGuid, in Guid settingGuid, out uint valueIndex);
}

/// <summary>Real Windows implementation (reads reuse the M2 power collector's readers).</summary>
internal sealed class WindowsPowerApi : IPowerApi
{
    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Power\PowerSettings";
    private const string MdmPowerKey = @"SOFTWARE\Microsoft\PolicyManager\current\device\Power";

    public IReadOnlyList<SchemeInfo> EnumerateSchemes() =>
        PowerCollector.ReadSchemes(CancellationToken.None)
            .Select(s => new SchemeInfo(s.Guid, s.Name, PowerCollector.PersonalityName(s.Personality, s.Guid))).ToList();

    public unsafe Guid? GetActiveScheme()
    {
        if (Native.PowerGetActiveScheme(0, out var p) != Native.ErrorSuccess || p == null) return null;
        try { return *p; }
        finally { Native.LocalFree((nint)p); }
    }

    public uint SetActiveScheme(Guid scheme) => PowerWriteNative.PowerSetActiveScheme(0, scheme);

    public SourceInfo? GetSource()
    {
        if (!Native.GetSystemPowerStatus(out var s)) return null;
        var source = s.ACLineStatus switch { 0 => "battery", 1 => "ac", _ => "unknown" };
        bool? hasBattery = s.BatteryFlag == 255 ? null : (s.BatteryFlag & 128) == 0;
        int? pct = hasBattery == true && s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : null;
        return new SourceInfo(source, hasBattery, pct);
    }

    public bool PolicyForcesActiveScheme()
    {
        using var key = Registry.LocalMachine.OpenSubKey(PolicyKey);
        if (key is null) return false;
        return key.GetValue("ActivePowerScheme") is not null || key.GetSubKeyNames().Contains("ActivePowerScheme", StringComparer.OrdinalIgnoreCase);
    }

    public bool ManagedActiveScheme()
    {
        using var key = Registry.LocalMachine.OpenSubKey(MdmPowerKey);
        return key is not null && key.GetValueNames().Any(n => n.Contains("ActivePowerScheme", StringComparison.OrdinalIgnoreCase)
                                                             || n.Contains("SelectPowerScheme", StringComparison.OrdinalIgnoreCase));
    }

    public uint ReadValue(Guid scheme, Guid subgroup, Guid setting, bool ac, out uint value) => ac
        ? PowerWriteNative.PowerReadACValueIndex(0, scheme, subgroup, setting, out value)
        : PowerWriteNative.PowerReadDCValueIndex(0, scheme, subgroup, setting, out value);

    public uint WriteValue(Guid scheme, Guid subgroup, Guid setting, bool ac, uint value) => ac
        ? PowerWriteNative.PowerWriteACValueIndex(0, scheme, subgroup, setting, value)
        : PowerWriteNative.PowerWriteDCValueIndex(0, scheme, subgroup, setting, value);

    /// <summary>
    /// Group policy stores a power setting under PowerSettings\&lt;setting GUID&gt;; MDM power policies live under the
    /// PolicyManager Power key (any value there counts: Optimus does not map each CSP name to a GUID, so it stays out).
    /// </summary>
    public bool SettingManaged(Guid setting)
    {
        if (PolicyForcesActiveScheme()) return true;
        using (var gp = Registry.LocalMachine.OpenSubKey(PolicyKey + "\\" + setting.ToString("D")))
            if (gp is not null) return true;
        using var mdm = Registry.LocalMachine.OpenSubKey(MdmPowerKey);
        return mdm is not null && mdm.GetValueNames().Length > 0;
    }
}

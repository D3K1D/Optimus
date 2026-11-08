using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using OptimizationTool.Engine;

namespace OptimizationTool.Windows.Reliability;

/// <summary>One present PnP device. <see cref="InstanceId"/> is only hashed into the subject, never stored.</summary>
internal sealed record RawDevice(string InstanceId, string? Name, string? Class, string? Provider, string? DriverVersion, DateTimeOffset? DriverDate,
    bool StatusRead, int ProblemCode);

internal sealed record RawDevices(IReadOnlyList<RawDevice> Devices, bool Truncated, int StatusFailures);

internal sealed class PnpCollector(CollectorOptions options) : ReliabilityCollector(options)
{
    public const int DeviceCap = 3000;

    public override string Id => "win.devices.pnp";
    public override string Category => "diagnostics";
    public override string GapTitle => "Devices";
    public override string GapSource => "Device Manager";

    protected override CollectorResult Collect(CancellationToken ct) => PnpNormalizer.Normalize(PnpReader.Read(ct), DateTimeOffset.UtcNow);
}

internal static class PnpReader
{
    public static RawDevices Read(CancellationToken ct)
    {
        var set = Native.SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_ALLCLASSES);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == Native.ERROR_ACCESS_DENIED) throw new UnauthorizedAccessException();
            throw new ExternalException("SetupDiGetClassDevs", err);
        }
        var list = new List<RawDevice>();
        var truncated = false;
        var statusFailures = 0;
        try
        {
            var data = new Native.SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<Native.SP_DEVINFO_DATA>() };
            for (var i = 0; Native.SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                ct.ThrowIfCancellationRequested();
                if (list.Count >= PnpCollector.DeviceCap) { truncated = true; break; }
                var id = InstanceId(set, ref data);
                if (id is null) continue;
                var ok = Native.CM_Get_DevNode_Status(out var status, out var problem, data.DevInst, 0) == Native.CR_SUCCESS;
                if (!ok) statusFailures++;
                list.Add(new RawDevice(id,
                    String(set, ref data, Native.DEVPKEY_Device_FriendlyName) ?? String(set, ref data, Native.DEVPKEY_Device_DeviceDesc),
                    String(set, ref data, Native.DEVPKEY_Device_Class),
                    String(set, ref data, Native.DEVPKEY_Device_DriverProvider),
                    String(set, ref data, Native.DEVPKEY_Device_DriverVersion),
                    FileTime(set, ref data, Native.DEVPKEY_Device_DriverDate),
                    ok, ok && (status & Native.DN_HAS_PROBLEM) != 0 ? problem : 0));
                data = new Native.SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<Native.SP_DEVINFO_DATA>() };
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        return new RawDevices(list, truncated, statusFailures);
    }

    private static string? InstanceId(IntPtr set, ref Native.SP_DEVINFO_DATA data)
    {
        var buf = new char[512];
        return Native.SetupDiGetDeviceInstanceIdW(set, ref data, buf, buf.Length, out var req) && req > 0 ? new string(buf, 0, req - 1) : null;
    }

    private static byte[]? Prop(IntPtr set, ref Native.SP_DEVINFO_DATA data, Native.DEVPROPKEY key, int wantType)
    {
        Native.SetupDiGetDevicePropertyW(set, ref data, in key, out var type, null, 0, out var size, 0);
        if (size <= 0 || size > 1 << 16) return null;
        var buf = new byte[size];
        if (!Native.SetupDiGetDevicePropertyW(set, ref data, in key, out type, buf, size, out _, 0) || type != wantType) return null;
        return buf;
    }

    private static string? String(IntPtr set, ref Native.SP_DEVINFO_DATA data, Native.DEVPROPKEY key)
    {
        var b = Prop(set, ref data, key, Native.DEVPROP_TYPE_STRING);
        if (b is null) return null;
        var s = System.Text.Encoding.Unicode.GetString(b).TrimEnd('\0');
        return s.Length == 0 ? null : s;
    }

    private static DateTimeOffset? FileTime(IntPtr set, ref Native.SP_DEVINFO_DATA data, Native.DEVPROPKEY key)
    {
        var b = Prop(set, ref data, key, Native.DEVPROP_TYPE_FILETIME);
        if (b is not { Length: 8 }) return null;
        var ft = BitConverter.ToInt64(b, 0);
        try { return ft > 0 ? DateTimeOffset.FromFileTime(ft).ToUniversalTime() : null; }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}

internal static class PnpNormalizer
{
    private static readonly TimeSpan OldDriver = TimeSpan.FromDays(730);

    /// <summary>
    /// Summary fact (counts by class, problem count, drivers older than 2 years) plus one fact per device with a problem code.
    /// Per-device facts for healthy devices are left out to keep evidence small; the summary carries their counts.
    /// </summary>
    public static CollectorResult Normalize(RawDevices raw, DateTimeOffset now)
    {
        var facts = new List<Fact>();
        foreach (var d in raw.Devices.Where(d => d.ProblemCode != 0))
        {
            var name = R.Clean(d.Name, 80);
            var v = new JsonObject
            {
                ["name"] = name.Length > 0 ? name : null,
                ["class"] = R.Clean(d.Class, 40),
                ["problemCode"] = d.ProblemCode,
                ["provider"] = d.Provider is null ? null : R.Clean(d.Provider, 60),
                ["driverVersion"] = d.DriverVersion is null ? null : R.Clean(d.DriverVersion, 40),
                ["driverDate"] = d.DriverDate is { } dd ? dd.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
            };
            var label = name.Length > 0 ? name : "A device";
            facts.Add(new Fact($"devices.pnp.{R.Hash8(d.InstanceId.ToUpperInvariant())}", "Device Manager", "measured", v,
                $"{label} reports problem code {d.ProblemCode}.", null));
        }

        var byClass = new JsonObject();
        foreach (var c in raw.Devices.GroupBy(d => string.IsNullOrWhiteSpace(d.Class) ? "Unknown" : R.Clean(d.Class, 40))
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
            byClass[c.Key] = c.Count();
        var withDriver = raw.Devices.Count(d => d.DriverVersion is not null);
        var old = raw.Devices.Count(d => d.DriverDate is { } dd && now - dd > OldDriver);
        var problems = facts.Count;
        facts.Add(new Fact("devices.pnp.summary", "Device Manager", "measured", new JsonObject
        {
            ["deviceCount"] = raw.Devices.Count,
            ["withDriverCount"] = withDriver,
            ["problemCount"] = problems,
            ["driversOlderThan2YearsCount"] = old,
            // Microsoft inbox drivers carry a fixed old date (e.g. 2006), so the third-party count is the meaningful one.
            ["thirdPartyDriversOlderThan2YearsCount"] = raw.Devices.Count(d => d.DriverDate is { } dd && now - dd > OldDriver &&
                d.Provider is { } p && !p.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)),
            ["byClass"] = byClass,
        }, $"{R.Plural(raw.Devices.Count, "device", "devices")} present, {R.Plural(problems, "with a problem", "with problems")}.", null));

        var reasons = new List<string>();
        if (raw.Truncated) reasons.Add($"row cap: first {PnpCollector.DeviceCap} devices read");
        if (raw.StatusFailures > 0) reasons.Add($"status unreadable for {raw.StatusFailures} devices");
        return new CollectorResult(R.Sorted(facts),
            new CoverageReceipt(reasons.Count > 0 ? "partial" : "complete", raw.Devices.Count, reasons.Count > 0 ? string.Join("; ", reasons) : null));
    }
}

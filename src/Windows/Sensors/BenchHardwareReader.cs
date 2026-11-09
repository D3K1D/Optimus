using System.Globalization;
using Microsoft.Win32;
using OptimizationTool.Engine.Bench;

namespace OptimizationTool.Windows.Sensors;

/// <summary>One Win32_PhysicalMemory row (SMBIOS values, no serials or part numbers).</summary>
internal sealed record MemoryModuleRaw(long? CapacityBytes, long? Speed, long? ConfiguredClockSpeed, long? SmbiosMemoryType);

/// <summary>Everything DescribeAsync read; any field may be missing.</summary>
internal sealed class HardwareRaw
{
    public string? CpuNameWmi, CpuNameRegistry;
    public long? NominalMHzWmi, NominalMHzRegistry;
    public int LogicalProcessors;
    public DxgiAdapterInfo? Gpu;
    public string? GpuDriverVersion;
    public long? InstalledMemoryBytes, TotalPhysBytes, AvailPhysBytes;
    public List<MemoryModuleRaw>? Modules;
    public string? Volume;
    public long? FreeBytes;
    public string? DiskModel;
    public long? DiskSizeBytes, DiskMediaType, DiskBusType;
    public string? WindowsBuild;
}

/// <summary>Pure: raw hardware facts → <see cref="BenchHardware"/>. Missing values become null / "unknown".</summary>
internal static class HardwareNormalizer
{
    public const string Unknown = "unknown";

    /// <summary>
    /// SMBIOS speeds are MT/s (SMBIOS ≥ 3.1 "Speed"/"Configured Memory Speed"). Some firmware still reports the
    /// memory clock in MHz (half the transfer rate). For DDR3/DDR4/DDR5 and LPDDR4/5, a value below the type's lowest
    /// standard transfer rate whose double is at or above it is taken as MHz and doubled. Other types are left as is.
    /// 0 / missing → null.
    /// </summary>
    public static int? MtPerSecond(long? value, long? smbiosType)
    {
        if (value is not (> 0 and < 100_000)) return null;
        var v = (int)value.Value;
        int? min = smbiosType switch
        {
            24 => 800,   // DDR3
            26 => 1600,  // DDR4
            30 => 1600,  // LPDDR4
            34 => 3200,  // DDR5
            35 => 3200,  // LPDDR5
            _ => null,
        };
        return min is { } m && v < m && v * 2 >= m ? v * 2 : v;
    }

    public static string Clean(string? s, int max = 120)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        t = new string(t.Where(c => !char.IsControl(c)).ToArray());
        return t.Length > max ? t[..max] : t;
    }

    /// <summary>
    /// The disk model from an MSFT_PhysicalDisk row: the vendor <c>Model</c> only. <c>FriendlyName</c> is never used,
    /// since a user can rename a disk (it may carry a person's or PC's name). Missing → null (the engine shows the media).
    /// </summary>
    public static string? DiskModelFrom(IReadOnlyDictionary<string, object?> row) =>
        row.TryGetValue("Model", out var m) && NullIfEmpty(Clean(Wmi.Str(m))) is { } model ? model : null;

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    public static BenchHardware Build(HardwareRaw r)
    {
        var cpu = NullIfEmpty(Clean(r.CpuNameWmi)) ?? NullIfEmpty(Clean(r.CpuNameRegistry)) ?? Unknown;
        double? nominal = r.NominalMHzWmi is > 0 and var w ? w : r.NominalMHzRegistry is > 0 and var g ? g : null;

        var modules = r.Modules ?? [];
        var capacity = modules.Sum(m => m.CapacityBytes is > 0 and var c ? c : 0);
        var ramBytes = capacity > 0 ? capacity : r.InstalledMemoryBytes ?? r.TotalPhysBytes ?? 0;
        var moduleCount = modules.Count(m => m.CapacityBytes is > 0);
        var configured = modules.Select(m => MtPerSecond(m.ConfiguredClockSpeed, m.SmbiosMemoryType)).Where(v => v is not null).Min();
        var rated = modules.Select(m => MtPerSecond(m.Speed, m.SmbiosMemoryType)).Where(v => v is not null).Min();

        var gpu = r.Gpu;
        return new BenchHardware(
            cpu, Math.Max(1, r.LogicalProcessors), nominal,
            gpu is null ? null : NullIfEmpty(Clean(gpu.Description)), gpu is null ? null : NullIfEmpty(Clean(r.GpuDriverVersion, 40)), gpu?.LuidHex,
            ramBytes, moduleCount, configured, rated,
            r.Volume ?? Unknown, NullIfEmpty(Clean(r.DiskModel)), r.DiskSizeBytes is > 0 ? r.DiskSizeBytes : null,
            r.DiskMediaType is null && r.DiskBusType is null ? Unknown : DiskNormalizer.Media(r.DiskMediaType, r.DiskBusType),
            r.FreeBytes ?? 0, r.AvailPhysBytes ?? 0, r.WindowsBuild ?? Unknown);
    }
}

/// <summary>
/// Host entry point for the benchmark's hardware facts (contracts/m6-benchmark.md §2, §5). Reads registry, DXGI,
/// GlobalMemoryStatusEx, RtlGetVersion and bounded WMI (Win32_Processor, Win32_PhysicalMemory, MSFT_PhysicalDisk)
/// as the current user. Never serials, PC or user names. Returns within ~5 s; throws only on cancellation.
/// </summary>
public sealed class BenchHardwareReader : IBenchHardware
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(4.5);
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public async Task<BenchHardware> DescribeAsync(string benchFolder, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var raw = new HardwareRaw();
        string? volume = null;
        try { volume = VolumeDisk.VolumeOf(benchFolder); } catch (Exception) { }
        lock (raw) raw.Volume = volume;

        var local = Task.Run(() => ReadLocal(raw, benchFolder), CancellationToken.None);
        var wmi = OnThread(() => ReadWmi(raw, volume));
        var all = Task.WhenAll(local, wmi);
        try { await Task.WhenAny(all, Task.Delay(Budget, ct)).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        ct.ThrowIfCancellationRequested();
        _ = all.ContinueWith(t => _ = t.Exception, TaskScheduler.Default); // observe late failures

        lock (raw) return HardwareNormalizer.Build(raw);
    }

    private static Task OnThread(Action work)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            try { work(); tcs.TrySetResult(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        })
        { IsBackground = true, Name = "Optimus bench hardware" };
        t.Start();
        return tcs.Task;
    }

    private static void Set(HardwareRaw raw, Action<HardwareRaw> set)
    {
        lock (raw) set(raw);
    }

    private static void Try(Action a)
    {
        try { a(); } catch (Exception) { /* missing value → unknown */ }
    }

    /// <summary>Free bytes for the benchmark folder itself (its nearest existing folder), so a volume mounted in a
    /// folder, with no drive letter, still reports its space. Null when it cannot be read.</summary>
    internal static long? FreeBytesOf(string folder)
    {
        var dir = ExistingAncestor(folder);
        if (dir is null) return null;
        return Native.GetDiskFreeSpaceEx(dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + "\\", out var free, out _, out _) ? (long)free : null;
    }

    internal static string? ExistingAncestor(string folder)
    {
        string? dir;
        try { dir = Path.GetFullPath(folder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        while (dir is not null && !Directory.Exists(dir)) dir = Path.GetDirectoryName(dir);
        return dir;
    }

    private static unsafe void ReadLocal(HardwareRaw raw, string benchFolder)
    {
        Try(() =>
        {
            var logical = (int)Native.GetActiveProcessorCount(Native.AllProcessorGroups);
            Set(raw, r => r.LogicalProcessors = logical > 0 ? logical : Environment.ProcessorCount);
        });
        Try(() =>
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var name = key?.GetValue("ProcessorNameString") as string;
            long? mhz = key?.GetValue("~MHz") is int i ? i : null;
            Set(raw, r => { r.CpuNameRegistry = name; r.NominalMHzRegistry = mhz; });
        });
        Try(() =>
        {
            var ms = new Native.MemoryStatusEx { Length = (uint)sizeof(Native.MemoryStatusEx) };
            long? total = null, avail = null, installed = null;
            if (Native.GlobalMemoryStatusEx(ref ms)) { total = (long)ms.TotalPhys; avail = (long)ms.AvailPhys; }
            if (Native.GetPhysicallyInstalledSystemMemory(out var kb)) installed = (long)kb * 1024;
            Set(raw, r => { r.TotalPhysBytes = total; r.AvailPhysBytes = avail; r.InstalledMemoryBytes = installed; });
        });
        Try(() =>
        {
            var v = new Native.OsVersionInfoW { OsVersionInfoSize = (uint)sizeof(Native.OsVersionInfoW) };
            if (Native.RtlGetVersion(ref v) == 0)
            {
                var build = string.Create(CultureInfo.InvariantCulture, $"{v.MajorVersion}.{v.MinorVersion}.{v.BuildNumber}");
                Set(raw, r => r.WindowsBuild = build);
            }
        });
        Try(() =>
        {
            if (FreeBytesOf(benchFolder) is { } free) Set(raw, r => r.FreeBytes = free);
        });
        Try(() =>
        {
            var gpu = Dxgi.HighPerformance(Dxgi.Adapters());
            var driver = gpu is null ? null : DriverVersion(gpu.VendorId, gpu.DeviceId, gpu.Description);
            Set(raw, r => { r.Gpu = gpu; r.GpuDriverVersion = driver; });
        });
    }

    /// <summary>DriverVersion from the display class key whose MatchingDeviceId names this vendor/device (else same description).</summary>
    private static string? DriverVersion(uint vendor, uint device, string description)
    {
        using var cls = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
        if (cls is null) return null;
        var token = $"ven_{vendor:x4}&dev_{device:x4}";
        string? byDesc = null;
        foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsAsciiDigit)).Take(64))
        {
            try
            {
                using var k = cls.OpenSubKey(sub);
                if (k is null) continue;
                var version = k.GetValue("DriverVersion") as string;
                if (k.GetValue("MatchingDeviceId") is string m && m.Contains(token, StringComparison.OrdinalIgnoreCase)) return version;
                if (byDesc is null && k.GetValue("DriverDesc") is string d && string.Equals(d.Trim(), description, StringComparison.OrdinalIgnoreCase)) byDesc = version;
            }
            catch (Exception) { }
        }
        return byDesc;
    }

    private static void ReadWmi(HardwareRaw raw, string? volume)
    {
        Try(() =>
        {
            using var cim = Wmi.Connect(@"root\cimv2");
            Try(() =>
            {
                var cpu = cim.Query("SELECT Name, MaxClockSpeed FROM Win32_Processor", ["Name", "MaxClockSpeed"], 8).FirstOrDefault();
                if (cpu is not null) Set(raw, r => { r.CpuNameWmi = Wmi.Str(cpu["Name"]); r.NominalMHzWmi = Wmi.Long(cpu["MaxClockSpeed"]); });
            });
            Try(() =>
            {
                var rows = cim.Query("SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType FROM Win32_PhysicalMemory",
                    ["Capacity", "Speed", "ConfiguredClockSpeed", "SMBIOSMemoryType"], 64);
                var modules = rows.Select(m => new MemoryModuleRaw(Wmi.Long(m["Capacity"]), Wmi.Long(m["Speed"]),
                    Wmi.Long(m["ConfiguredClockSpeed"]), Wmi.Long(m["SMBIOSMemoryType"]))).ToList();
                Set(raw, r => r.Modules = modules);
            });
        });
        if (volume is null) return;
        Try(() =>
        {
            if (VolumeDisk.DiskNumber(volume) is not { } disk) return;
            using var storage = Wmi.Connect(@"root\Microsoft\Windows\Storage");
            var id = disk.ToString(CultureInfo.InvariantCulture);
            var row = storage.Query($"SELECT DeviceId, Model, Size, MediaType, BusType FROM MSFT_PhysicalDisk WHERE DeviceId = '{id}'",
                ["Model", "Size", "MediaType", "BusType"], 4).FirstOrDefault();
            if (row is null) return;
            var model = HardwareNormalizer.DiskModelFrom(row);
            Set(raw, r =>
            {
                r.DiskModel = model;
                r.DiskSizeBytes = Wmi.Long(row["Size"]);
                r.DiskMediaType = Wmi.Long(row["MediaType"]);
                r.DiskBusType = Wmi.Long(row["BusType"]);
            });
        });
    }
}

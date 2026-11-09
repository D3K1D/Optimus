using System.Security.Principal;

namespace OptimizationTool.Windows.Sensors;

/// <summary>Volume → physical disk number, via IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS on a zero-access handle.</summary>
internal static unsafe class VolumeDisk
{
    /// <summary>"C:" style volume name from a folder path, or null for a mount folder / unknown.</summary>
    public static string? VolumeOf(string folder)
    {
        string? root = null;
        try
        {
            var buf = stackalloc char[300];
            if (Native.GetVolumePathName(Path.GetFullPath(folder), buf, 300)) root = new string(buf);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        root ??= Path.GetPathRoot(folder);
        return Letter(root);
    }

    /// <summary>"C:\" or "C:" → "C:"; anything else (mount folders, UNC) → null.</summary>
    public static string? Letter(string? root)
    {
        if (root is null) return null;
        var r = root.TrimEnd('\\');
        return r.Length == 2 && char.IsAsciiLetter(r[0]) && r[1] == ':' ? char.ToUpperInvariant(r[0]) + ":" : null;
    }

    /// <summary>The single physical disk number backing the volume, or null (spanned, virtual, or unreadable).</summary>
    public static int? DiskNumber(string volume)
    {
        using var h = Native.CreateFile(@"\\.\" + volume, 0, Native.FileShareReadWrite, 0, Native.OpenExisting, 0, 0);
        if (h.IsInvalid) return null;
        var buf = stackalloc byte[8 + 24 * 4];
        if (!Native.DeviceIoControl(h, Native.IoctlVolumeGetVolumeDiskExtents, null, 0, buf, 8 + 24 * 4, out var got, 0) || got < 32) return null;
        var count = *(uint*)buf;
        return count == 1 ? (int)*(uint*)(buf + 8) : null;
    }
}

/// <summary>Pure: disk media from MSFT_PhysicalDisk MediaType / BusType.</summary>
internal static class DiskNormalizer
{
    public const int BusTypeNvme = 17;

    /// <summary>nvme | ssd | hdd | unknown. BusType 17 (NVMe) wins; MediaType 4 = SSD, 3 = HDD, 5 = SCM (as ssd).</summary>
    public static string Media(long? mediaType, long? busType) => busType == BusTypeNvme ? "nvme" : mediaType switch
    {
        4 or 5 => "ssd",
        3 => "hdd",
        _ => "unknown",
    };

    /// <summary>Reliability-counter temperature in °C, or null when not reported (0) or implausible.</summary>
    public static double? TempC(long? raw) => raw is > 0 and < 150 ? raw : null;

    /// <summary>Disk slot y-scales (MB/s) by media; fixed for the test.</summary>
    public static (double Seq, double Random) Scale(string media) => media switch
    {
        "nvme" => (8000, 3000),
        "ssd" => (600, 500),
        "hdd" => (300, 50),
        _ => (3500, 1000),
    };
}

/// <summary>disk.temp from MSFT_StorageReliabilityCounter (usually needs admin). Refreshed every 5 s off the sampler path.</summary>
internal sealed class DiskTempSource : ISensorSource
{
    private const string Ns = @"root\Microsoft\Windows\Storage";
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(5);
    private readonly int _disk;
    private readonly Timer _timer;
    private double _value; // NaN = none
    private long _stamp;
    private int _busy;

    private DiskTempSource(int disk, double first)
    {
        _disk = disk;
        _value = first;
        _stamp = Environment.TickCount64;
        _timer = new Timer(_ => Refresh(), null, Period, Period);
    }

    public string Name => "disk.wmi";
    public IReadOnlyList<string> SlotIds { get; } = ["temp"];

    /// <summary>One bounded read: the temperature, or the reason it is unavailable.</summary>
    public static (double? TempC, string? Reason) Probe(int disk)
    {
        try
        {
            if (ReadOnce(disk) is { } t) return (t, null);
            return (null, IsElevated() ? SlotReasons.NotSupported : SlotReasons.NeedsAdmin);
        }
        catch (Exception ex) when (Wmi.IsAccessDenied(ex)) { return (null, SlotReasons.NeedsAdmin); }
        catch (Exception) { return (null, SlotReasons.NotSupported); }
    }

    public static DiskTempSource Create(int disk, double first) => new(disk, first);

    private static double? ReadOnce(int disk)
    {
        using var wmi = Wmi.Connect(Ns);
        var rows = wmi.Query("SELECT DeviceId, Temperature FROM MSFT_StorageReliabilityCounter", ["DeviceId", "Temperature"], 64);
        var id = disk.ToString(global::System.Globalization.CultureInfo.InvariantCulture);
        var row = rows.FirstOrDefault(r => Wmi.Str(r["DeviceId"]) == id);
        return row is null ? null : DiskNormalizer.TempC(Wmi.Long(row["Temperature"]));
    }

    private static bool IsElevated()
    {
        using var me = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void Refresh()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            if (ReadOnce(_disk) is { } t)
            {
                Volatile.Write(ref _value, t);
                Interlocked.Exchange(ref _stamp, Environment.TickCount64);
            }
        }
        catch (Exception) { /* keep the last value until it goes stale */ }
        finally { Volatile.Write(ref _busy, 0); }
    }

    public SourceReading Read()
    {
        var fresh = Environment.TickCount64 - Interlocked.Read(ref _stamp) < 15_000;
        return new SourceReading(new Dictionary<string, double?> { ["temp"] = fresh && Volatile.Read(ref _value) is var v && !double.IsNaN(v) ? v : null });
    }

    public void Dispose() => _timer.Dispose();
}

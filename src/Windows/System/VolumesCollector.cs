using System.Text.Json.Nodes;
using OptimizationTool.Engine;

namespace OptimizationTool.Windows.SystemDomain;

internal sealed record VolumeRaw(char Letter, bool Ready, long CapacityBytes, long FreeBytes, string? FileSystem, bool IsSystem, bool? SeekPenalty, int? BusType);
internal sealed record VolumesRaw(IReadOnlyList<VolumeRaw> Volumes);

/// <summary>
/// win.system.volumes: fixed lettered volumes, capacity/free bytes, system volume flag, and media type where the
/// storage stack reports it to a zero-access handle (seek penalty → HDD/SSD; no admin needed). Labels are not read.
/// </summary>
internal sealed class VolumesCollector(CollectorOptions options) : SystemCollector<VolumesRaw>(options)
{
    private const int VolumeCap = 26;

    public override string Id => "win.system.volumes";
    public override string Category => "storage";
    public override string GapTitle => "Drive space";
    public override string GapSource => "Windows volumes";

    protected override VolumesRaw Read(CancellationToken ct)
    {
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var list = new List<VolumeRaw>();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed).Take(VolumeCap))
        {
            ct.ThrowIfCancellationRequested();
            var letter = char.ToUpperInvariant(d.Name[0]);
            if (!char.IsAsciiLetter(letter)) continue;
            var isSystem = string.Equals(d.Name, systemRoot, StringComparison.OrdinalIgnoreCase);
            var (seek, bus) = ReadMedia(letter);
            try
            {
                if (!d.IsReady) { list.Add(new VolumeRaw(letter, false, 0, 0, null, isSystem, seek, bus)); continue; }
                list.Add(new VolumeRaw(letter, true, d.TotalSize, d.AvailableFreeSpace, d.DriveFormat, isSystem, seek, bus));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                list.Add(new VolumeRaw(letter, false, 0, 0, null, isSystem, seek, bus));
            }
        }
        return new VolumesRaw(list);
    }

    private static unsafe (bool? SeekPenalty, int? BusType) ReadMedia(char letter)
    {
        try
        {
            using var h = Native.CreateFile($@"\\.\{letter}:", 0, Native.FileShareReadWrite, 0, Native.OpenExisting, 0, 0);
            if (h.IsInvalid) return (null, null);
            bool? seek = null;
            int? bus = null;
            var query = stackalloc int[3]; // STORAGE_PROPERTY_QUERY { PropertyId, QueryType, AdditionalParameters[1] }
            query[0] = Native.StorageDeviceSeekPenaltyProperty;
            query[1] = 0;
            query[2] = 0;
            var seekOut = stackalloc byte[12]; // DEVICE_SEEK_PENALTY_DESCRIPTOR { Version, Size, BOOLEAN IncursSeekPenalty }
            if (Native.DeviceIoControl(h, Native.IoctlStorageQueryProperty, query, 12, seekOut, 12, out var got, 0) && got >= 9)
                seek = seekOut[8] != 0;

            query[0] = Native.StorageDeviceProperty;
            var devOut = stackalloc byte[64]; // STORAGE_DEVICE_DESCRIPTOR header; BusType at offset 28. Strings not read.
            if (Native.DeviceIoControl(h, Native.IoctlStorageQueryProperty, query, 12, devOut, 64, out got, 0) && got >= 32)
                bus = *(int*)(devOut + 28);
            return (seek, bus);
        }
        catch (Exception) { return (null, null); }
    }

    internal static string BusName(int? bus) => bus switch
    {
        null => "unknown",
        1 => "scsi", 3 => "ata", 7 => "usb", 8 => "raid", 9 => "iscsi", 10 => "sas", 11 => "sata", 12 => "sd", 13 => "mmc",
        14 => "virtual", 15 => "file_backed_virtual", 16 => "spaces", 17 => "nvme", 18 => "scm", 19 => "ufs",
        _ => "other",
    };

    public override CollectorResult Normalize(VolumesRaw raw)
    {
        var facts = new List<Fact>();
        var notReady = new List<char>();
        foreach (var v in raw.Volumes)
        {
            if (!v.Ready) { notReady.Add(v.Letter); continue; }
            var bus = BusName(v.BusType);
            var media = bus == "nvme" ? "ssd" : v.SeekPenalty switch { true => "hdd", false => "ssd", null => "unknown" };
            var freePercent = v.CapacityBytes > 0 ? Sys.Round(100.0 * v.FreeBytes / v.CapacityBytes) : 0;
            var value = new JsonObject
            {
                ["letter"] = v.Letter.ToString(), ["isSystem"] = v.IsSystem, ["capacityBytes"] = v.CapacityBytes,
                ["freeBytes"] = v.FreeBytes, ["freePercent"] = freePercent, ["fileSystem"] = v.FileSystem,
                ["mediaType"] = media, ["busType"] = bus,
            };
            var which = v.IsSystem ? "The system drive" : $"Drive {v.Letter}:";
            facts.Add(new Fact($"volume.{char.ToLowerInvariant(v.Letter)}", "Windows volume capacity", "measured", value,
                $"{which} has {Sys.Gib(v.FreeBytes)} GiB free out of {Sys.Gib(v.CapacityBytes)} GiB.", null));
        }

        if (notReady.Count == 0) return Sys.Result(facts, "complete", null);
        return Sys.Result(facts, "partial", $"{notReady.Count} fixed volume(s) not readable (locked or not ready)");
    }
}

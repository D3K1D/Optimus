using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Windows.Sensors;
using OptimizationTool.Windows.SystemDomain;

namespace OptimizationTool.Windows.Inventory;

/// <summary>
/// The hardware sources as read. Rows are property bags of the named properties only; <see cref="Status"/> per source
/// (cpu, gpu, firmware, memory, disks, monitors, network): ok | denied | unavailable | timed_out | failed.
/// </summary>
internal sealed record HardwareInventoryRaw(
    CpuRaw? Cpu, IReadOnlyList<GpuRaw>? Gpus, Bag? Firmware,
    IReadOnlyList<Bag>? Memory, IReadOnlyList<Bag>? Disks, IReadOnlyList<Bag>? Monitors, IReadOnlyList<Bag>? Adapters,
    IReadOnlyDictionary<string, string> Status);

/// <summary>
/// win.inventory.hardware (m9 §6.10): CPU, GPUs, PC model, motherboard, BIOS/UEFI, RAM modules, drives, monitors and
/// network adapters: maker, model, version. CPU and GPUs come from the same readers as win.system.hardware; the board
/// and firmware from the registry's BIOS key; RAM, drives, monitors and adapters from WMI, each source under its own
/// time limit. Only named properties are queried and only an allowlist of them is kept: no serial numbers, instance
/// paths, MAC addresses, unique IDs or system names.
/// </summary>
internal sealed class HardwareInventoryCollector(Func<CancellationToken, HardwareInventoryRaw>? read = null, IReadOnlyList<LocalIdentity>? identities = null)
    : InventoryCollector<HardwareInventoryRaw>(read ?? HardwareInventoryReader.Read, identities)
{
    public const int Cap = 100;
    public override string Area => "hardware";
    public override TimeSpan Timeout => TimeSpan.FromSeconds(40);
    public override string GapTitle => "Hardware list";
    protected override bool Sta => true;

    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        ["cpu"] = "Processor details", ["gpu"] = "Graphics details", ["firmware"] = "Board and firmware details",
        ["memory"] = "Memory module details", ["disks"] = "Drive details", ["monitors"] = "Monitor details", ["network"] = "Network adapter details",
    };

    private static readonly string[] Placeholders = ["to be filled by o.e.m.", "system product name", "system manufacturer", "default string", "not applicable", "none", "o.e.m.", "oem", "base board product name", "unknown"];

    internal override CollectorResult Normalize(HardwareInventoryRaw raw)
    {
        var ids = Identities;
        var items = new ItemList(Cap);
        string? C(string? s, int max = 100) => InventoryKit.Clean(s, ids, max) is { } v && !Placeholders.Contains(v.ToLowerInvariant()) ? v : null;
        // m9 review R4b: a field read from a dedicated model property is exempt from the serial-run and key-shape rules.
        string? M(string? s, int max = 100) => InventoryKit.Clean(s, ids, max, model: true) is { } v && !Placeholders.Contains(v.ToLowerInvariant()) ? v : null;
        string? V(string? s) => InventoryKit.Version(s, ids) is { } v && !Placeholders.Contains(v.ToLowerInvariant()) ? v : null;
        // Where an item's name came from (local evidence only, never projected): "model" when read from a dedicated model
        // property (the check-up then applies the model rule to it), "name" for any fallback or composed name (full rule).
        static (string, JsonNode?) Src(bool fromModel) => (ScanInventory.ModelSource, fromModel ? ScanInventory.FromModel : ScanInventory.FromName);

        if (raw.Cpu is { } cpu && M(cpu.Name) is { } cpuName)
            items.Add(InventoryKit.Item(("kind", "cpu"), ("name", cpuName), ("publisher", CpuMaker(C(cpu.Vendor))),
                ("cores", cpu.Cores), ("threads", cpu.LogicalProcessors), Src(true)));

        foreach (var g in raw.Gpus ?? [])
            if (M(g.Name) is { } gpuName)
                items.Add(InventoryKit.Item(("kind", "gpu"), ("name", gpuName), ("publisher", GpuMaker(g.VendorId) ?? C(g.DriverProvider, 60)),
                    ("memoryBytes", g.DedicatedBytes > 0 ? (long)g.DedicatedBytes : (long?)null), ("driverVersion", V(g.DriverVersion)), Src(true)));

        string? model = null;
        if (raw.Firmware is { } fw)
        {
            var systemName = M(fw.Str("SystemProductName"));
            var systemMaker = C(fw.Str("SystemManufacturer"), 60);
            if (systemName is not null)
                items.Add(InventoryKit.Item(("kind", "system"), ("name", systemName), ("publisher", systemMaker), ("family", M(fw.Str("SystemFamily"), 60)), Src(true)));
            var board = M(fw.Str("BaseBoardProduct"));
            if (board is not null)
                items.Add(InventoryKit.Item(("kind", "board"), ("name", board), ("publisher", C(fw.Str("BaseBoardManufacturer"), 60)), ("version", V(fw.Str("BaseBoardVersion"))), Src(true)));
            model = board ?? systemName;
            var biosVersion = V(fw.Str("BIOSVersion"));
            if (biosVersion is not null || C(fw.Str("BIOSVendor")) is not null)
                items.Add(InventoryKit.Item(("kind", "firmware"), ("name", model is null ? "BIOS" : model + " BIOS"), ("publisher", C(fw.Str("BIOSVendor"), 60)),
                    ("version", biosVersion), ("date", HardwareCollector.NormalizeDate(fw.Str("BIOSReleaseDate"))), Src(model is not null)));
        }

        foreach (var m in raw.Memory ?? [])
        {
            var size = InventoryKit.Positive(m.Long("Capacity"));
            var speed = InventoryKit.Positive(m.Long("ConfiguredClockSpeed")) ?? InventoryKit.Positive(m.Long("Speed"));
            var type = MemoryType(m.Long("SMBIOSMemoryType"));
            var parts = new List<string>();
            if (type is not null) parts.Add(speed is { } s1 ? $"{type}-{s1.ToString(CultureInfo.InvariantCulture)}" : type);
            if (size is { } b) parts.Add($"{(b / (1L << 30)).ToString(CultureInfo.InvariantCulture)} GB");
            parts.Add("memory module");
            items.Add(InventoryKit.Item(("kind", "memory"), ("name", string.Join(' ', parts)), ("publisher", C(m.Str("Manufacturer"), 60)),
                ("model", M(m.Str("PartNumber"), 60)), ("sizeBytes", size), ("speedMts", speed), Src(false)));
        }

        foreach (var d in raw.Disks ?? [])
        {
            var fromModel = M(d.Str("Model"));
            var name = fromModel ?? C(d.Str("FriendlyName"));
            if (name is null) continue;
            items.Add(InventoryKit.Item(("kind", "disk"), ("name", name), ("publisher", C(d.Str("Manufacturer"), 60)),
                ("version", V(d.Str("FirmwareVersion")) ?? V(d.Str("FirmwareRevision"))), ("media", MediaType(d.Long("MediaType"))),
                ("bus", BusType(d.Long("BusType")) ?? C(d.Str("InterfaceType"), 20)), ("sizeBytes", InventoryKit.Positive(d.Long("Size"))), Src(fromModel is not null)));
        }

        foreach (var mon in raw.Monitors ?? [])
        {
            var maker = Edid(mon.TryGetValue("ManufacturerName", out var mk) ? mk : null);
            var friendly = M(Edid(mon.TryGetValue("UserFriendlyName", out var fn) ? fn : null));
            var name = friendly ?? (maker is null ? null : C($"{MonitorMaker(maker) ?? maker} monitor"));
            if (name is null) continue;
            items.Add(InventoryKit.Item(("kind", "monitor"), ("name", name), ("publisher", maker is null ? null : C(MonitorMaker(maker) ?? maker, 60)),
                ("model", M(Edid(mon.TryGetValue("ProductCodeID", out var pc) ? pc : null), 20)), ("year", mon.Long("YearOfManufacture") is { } y and > 1990 and < 2200 ? y : (long?)null), Src(friendly is not null)));
        }

        foreach (var a in raw.Adapters ?? [])
            if (C(a.Str("Name")) is { } adapter)
                items.Add(InventoryKit.Item(("kind", "network"), ("name", adapter), ("publisher", C(a.Str("Manufacturer"), 60)), Src(false)));

        var fact = InventoryKit.Fact(Area, "Windows hardware inventory", items, raw.Status,
            $"{InventoryKit.Plural(items.Total, "hardware item", "hardware items")} listed{(model is null ? "" : $" for {model}")}.");
        return InventoryKit.Result(fact, raw.Status, Labels, items);
    }

    private static string? CpuMaker(string? vendor) => vendor switch
    {
        null => null,
        "GenuineIntel" => "Intel",
        "AuthenticAMD" => "AMD",
        _ => vendor,
    };

    internal static string? GpuMaker(uint vendorId) => vendorId switch
    {
        0x10de => "NVIDIA",
        0x1002 or 0x1022 => "AMD",
        0x8086 => "Intel",
        0x1414 => "Microsoft",
        0x5143 => "Qualcomm",
        _ => null,
    };

    private static string? MemoryType(long? smbios) => smbios switch
    {
        20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 34 => "DDR5", 27 => "LPDDR", 28 => "LPDDR2", 29 => "LPDDR3", 30 => "LPDDR4", 35 => "LPDDR5",
        _ => null,
    };

    private static string? MediaType(long? m) => m switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => null };

    private static string? BusType(long? b) => b switch
    {
        1 => "SCSI", 3 => "ATA", 6 => "Fibre Channel", 7 => "USB", 8 => "RAID", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 17 => "NVMe", 18 => "SCM", 19 => "UFS",
        _ => null,
    };

    /// <summary>WmiMonitorID fields are UInt16 arrays of characters (zero-padded).</summary>
    internal static string? Edid(object? v)
    {
        if (v is string s) return s.TrimEnd('\0').Trim() is { Length: > 0 } t ? t : null;
        if (v is not System.Collections.IEnumerable list) return null;
        var sb = new System.Text.StringBuilder();
        foreach (var c in list)
        {
            if (sb.Length >= 64) break;
            long code;
            try { code = Convert.ToInt64(c, CultureInfo.InvariantCulture); }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return null; }
            if (code == 0) break;
            if (code is < 32 or > 126) return null;
            sb.Append((char)code);
        }
        return sb.Length == 0 ? null : sb.ToString().Trim();
    }

    /// <summary>The usual makers behind EDID manufacturer codes; other codes are kept as they are.</summary>
    private static string? MonitorMaker(string code) => code.ToUpperInvariant() switch
    {
        "DEL" => "Dell", "SAM" or "SEC" => "Samsung", "GSM" => "LG", "LGD" => "LG Display", "ACR" => "Acer", "AUS" => "ASUS", "BNQ" => "BenQ",
        "HWP" or "HPN" => "HP", "LEN" => "Lenovo", "AOC" => "AOC", "PHL" => "Philips", "MSI" => "MSI", "VSC" => "ViewSonic", "SNY" => "Sony",
        "AUO" => "AU Optronics", "BOE" => "BOE", "SHP" => "Sharp", "CMN" => "Innolux", "IVM" => "iiyama", "GBT" => "Gigabyte", "APP" => "Apple",
        "HKC" => "HKC", "NEC" => "NEC", "EIZ" or "ENC" => "EIZO", "MEI" => "Panasonic", "XMI" => "Xiaomi", "HSD" => "HannStar", "SDC" => "Samsung Display",
        _ => null,
    };
}

/// <summary>Windows calls only (read-only, standard user): registry, DXGI and WMI with named properties.</summary>
internal static class HardwareInventoryReader
{
    private static readonly TimeSpan SourceLimit = TimeSpan.FromSeconds(8);
    private const int RowCap = 64;

    private static readonly string[] FirmwareValues = ["BIOSVendor", "BIOSVersion", "BIOSReleaseDate", "SystemManufacturer", "SystemProductName", "SystemFamily",
        "BaseBoardManufacturer", "BaseBoardProduct", "BaseBoardVersion"];
    // Never SerialNumber, PartNumber is a model code; never Tag, DeviceLocator or BankLabel (slot names are harmless, but not needed).
    private static readonly string[] MemoryProps = ["Manufacturer", "PartNumber", "Capacity", "Speed", "ConfiguredClockSpeed", "SMBIOSMemoryType"];
    private static readonly string[] DiskProps = ["FriendlyName", "Model", "Manufacturer", "FirmwareVersion", "MediaType", "BusType", "Size"];
    private static readonly string[] DiskFallbackProps = ["Model", "Manufacturer", "FirmwareRevision", "InterfaceType", "Size"];
    private static readonly string[] MonitorProps = ["ManufacturerName", "UserFriendlyName", "ProductCodeID", "YearOfManufacture"];
    private static readonly string[] AdapterProps = ["Name", "Manufacturer"];

    public static HardwareInventoryRaw Read(CancellationToken ct)
    {
        var status = new Dictionary<string, string>(StringComparer.Ordinal);
        var cpu = InventoryKit.Source("cpu", SourceLimit, status, ct, HardwareCollector.ReadCpu);
        var gpus = InventoryKit.Source("gpu", SourceLimit, status, ct, () => HardwareCollector.ReadGpus(ct));
        var firmware = InventoryKit.Source("firmware", SourceLimit, status, ct, () =>
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS", writable: false);
            var bag = new Bag();
            foreach (var name in FirmwareValues) bag[name] = key?.GetValue(name) as string;
            return bag;
        });
        var memory = InventoryKit.Source("memory", SourceLimit, status, ct, () => Query(@"root\cimv2", "Win32_PhysicalMemory", MemoryProps, null));
        var disks = InventoryKit.Source("disks", SourceLimit, status, ct, () =>
        {
            try { return Query(@"root\Microsoft\Windows\Storage", "MSFT_PhysicalDisk", DiskProps, null); }
            catch (Exception ex) when (InventoryKit.StatusOf(ex) != "denied") { return Query(@"root\cimv2", "Win32_DiskDrive", DiskFallbackProps, null); }
        });
        var monitors = InventoryKit.Source("monitors", SourceLimit, status, ct, () => Query(@"root\wmi", "WmiMonitorID", MonitorProps, "Active = TRUE"));
        var adapters = InventoryKit.Source("network", SourceLimit, status, ct, () => Query(@"root\cimv2", "Win32_NetworkAdapter", AdapterProps, "PhysicalAdapter = TRUE"));
        return new HardwareInventoryRaw(cpu, gpus, firmware, memory, disks, monitors, adapters, status);
    }

    private static List<Bag> Query(string ns, string cls, string[] props, string? where)
    {
        using var wmi = Wmi.Connect(ns);
        var wql = $"SELECT {string.Join(", ", props)} FROM {cls}{(where is null ? "" : " WHERE " + where)}";
        return wmi.Query(wql, props, RowCap).Select(r => new Bag(r)).ToList();
    }
}

using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using OptimizationTool.Engine;

namespace OptimizationTool.Windows.SystemDomain;

internal sealed record CpuRaw(string? Name, string? Vendor, long? NominalMhz, int? Packages, int? Cores, int? PerformanceCores, int? EfficiencyCores, int LogicalProcessors);
internal sealed record MemoryRaw(ulong? InstalledBytes, ulong? UsableBytes);
internal sealed record GpuRaw(string Name, uint VendorId, uint DeviceId, ulong DedicatedBytes, ulong SharedBytes, string? DriverVersion, string? DriverDate, string? DriverProvider);
internal sealed record FirmwareRaw(string? BiosVendor, string? BiosVersion, string? BiosReleaseDate, string? SystemManufacturer, string? SystemProductName, string? SystemFamily);
internal sealed record HardwareRaw(CpuRaw? Cpu, MemoryRaw? Memory, IReadOnlyList<GpuRaw>? Gpus, string? GpuError, FirmwareRaw? Firmware, IReadOnlyList<string> Failures);

/// <summary>
/// win.system.hardware: CPU (registry + processor topology), RAM, GPUs (DXGI adapters joined with the display class
/// driver key), firmware and model (HARDWARE\DESCRIPTION\System\BIOS). Serial numbers are never read.
/// </summary>
internal sealed class HardwareCollector(CollectorOptions options) : SystemCollector<HardwareRaw>(options)
{
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const int DriverKeyCap = 64;

    public override string Id => "win.system.hardware";
    public override string Category => "performance";
    public override string GapTitle => "Hardware details";
    public override string GapSource => "Windows hardware inventory";

    protected override HardwareRaw Read(CancellationToken ct)
    {
        var failures = new List<string>();
        CpuRaw? cpu = Try(ReadCpu, "cpu", failures);
        ct.ThrowIfCancellationRequested();
        MemoryRaw? mem = Try(ReadMemory, "memory", failures);
        string? gpuError = null;
        IReadOnlyList<GpuRaw>? gpus = null;
        try { gpus = ReadGpus(ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { gpuError = ex.GetType().Name; }
        FirmwareRaw? fw = Try(ReadFirmware, "firmware", failures);
        return new HardwareRaw(cpu, mem, gpus, gpuError, fw, failures);
    }

    private static T? Try<T>(Func<T> read, string what, List<string> failures) where T : class
    {
        try { return read(); }
        catch (Exception) { failures.Add(what); return null; }
    }

    private static unsafe CpuRaw ReadCpu()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        var name = Sys.RegString(key, "ProcessorNameString");
        var vendor = Sys.RegString(key, "VendorIdentifier");
        var mhz = Sys.RegNumber(key, "~MHz");
        var logical = (int)Native.GetActiveProcessorCount(Native.AllProcessorGroups);
        if (logical <= 0) logical = Environment.ProcessorCount;

        int? cores = null, packages = null, perf = null, eff = null;
        var classes = ReadRelation(Native.RelationProcessorCore);
        if (classes is not null)
        {
            cores = classes.Count;
            var max = classes.Max();
            if (classes.Distinct().Count() > 1)
            {
                perf = classes.Count(c => c == max);
                eff = classes.Count - perf;
            }
        }
        packages = ReadRelation(Native.RelationProcessorPackage)?.Count;
        return new CpuRaw(name, vendor, mhz, packages, cores, perf, eff, logical);
    }

    /// <summary>Walks SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX; returns the EfficiencyClass of each entry.</summary>
    private static unsafe List<byte>? ReadRelation(int relation)
    {
        uint len = 0;
        Native.GetLogicalProcessorInformationEx(relation, null, ref len);
        if (len == 0 || len > 4 << 20) return null;
        var buf = new byte[len];
        fixed (byte* p = buf)
        {
            if (!Native.GetLogicalProcessorInformationEx(relation, p, ref len)) return null;
            var list = new List<byte>();
            uint off = 0;
            while (off + 8 <= len)
            {
                var size = *(uint*)(p + off + 4);
                if (size == 0) break;
                // PROCESSOR_RELATIONSHIP starts at +8: BYTE Flags; BYTE EfficiencyClass.
                list.Add(*(p + off + 9));
                off += size;
            }
            return list;
        }
    }

    private static MemoryRaw ReadMemory()
    {
        ulong? installed = Native.GetPhysicallyInstalledSystemMemory(out var kb) ? kb * 1024 : null;
        var ms = new Native.MemoryStatusEx { Length = (uint)global::System.Runtime.InteropServices.Marshal.SizeOf<Native.MemoryStatusEx>() };
        ulong? usable = Native.GlobalMemoryStatusEx(ref ms) ? ms.TotalPhys : null;
        return new MemoryRaw(installed, usable);
    }

    private static unsafe List<GpuRaw> ReadGpus(CancellationToken ct)
    {
        var drivers = ReadDisplayDrivers();
        var gpus = new List<GpuRaw>();
        var hr = Native.CreateDXGIFactory1(Native.IidDxgiFactory1, out var factory);
        if (hr < 0) throw new global::System.ComponentModel.Win32Exception(hr);
        try
        {
            for (uint i = 0; i < 16; i++)
            {
                ct.ThrowIfCancellationRequested();
                hr = Native.EnumAdapters1(factory, i, out var adapter);
                if (hr == Native.DxgiErrorNotFound) break;
                if (hr < 0) throw new global::System.ComponentModel.Win32Exception(hr);
                try
                {
                    if (Native.GetDesc1(adapter, out var d) < 0) continue;
                    if ((d.Flags & Native.DxgiAdapterFlagSoftware) != 0) continue;
                    var name = new string(d.Description).TrimEnd('\0').Trim();
                    uint vendor = d.VendorId, device = d.DeviceId;
                    // The same physical adapter can appear once per LUID; keep one entry per vendor/device/name.
                    if (gpus.Any(g => g.VendorId == vendor && g.DeviceId == device && g.Name == name)) continue;
                    var drv = drivers.FirstOrDefault(x => x.Matches(vendor, device))
                              ?? drivers.FirstOrDefault(x => string.Equals(x.Desc, name, StringComparison.OrdinalIgnoreCase));
                    gpus.Add(new GpuRaw(name, vendor, device, d.DedicatedVideoMemory, d.SharedSystemMemory,
                        drv?.Version, drv?.Date, drv?.Provider));
                }
                finally { Native.ComRelease(adapter); }
            }
        }
        finally { Native.ComRelease(factory); }
        return gpus;
    }

    private sealed record DriverKey(string? Desc, string? MatchingId, string? Version, string? Date, string? Provider)
    {
        public bool Matches(uint vendor, uint device) =>
            MatchingId is { } m && m.Contains($"ven_{vendor:x4}&dev_{device:x4}", StringComparison.OrdinalIgnoreCase);
    }

    private static List<DriverKey> ReadDisplayDrivers()
    {
        var list = new List<DriverKey>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (cls is null) return list;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsAsciiDigit)).Take(DriverKeyCap))
            {
                try
                {
                    using var k = cls.OpenSubKey(sub);
                    if (k is null) continue;
                    list.Add(new DriverKey(Sys.RegString(k, "DriverDesc"), Sys.RegString(k, "MatchingDeviceId"),
                        Sys.RegString(k, "DriverVersion"), NormalizeDate(Sys.RegString(k, "DriverDate")), Sys.RegString(k, "ProviderName")));
                }
                catch (Exception) { /* a single unreadable driver key just leaves that GPU without a version */ }
            }
        }
        catch (Exception) { }
        return list;
    }

    private static FirmwareRaw ReadFirmware()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        return new FirmwareRaw(Sys.RegString(key, "BIOSVendor"), Sys.RegString(key, "BIOSVersion"),
            NormalizeDate(Sys.RegString(key, "BIOSReleaseDate")), Sys.RegString(key, "SystemManufacturer"),
            Sys.RegString(key, "SystemProductName"), Sys.RegString(key, "SystemFamily"));
    }

    /// <summary>Registry dates are "M-D-YYYY" or "MM/DD/YYYY"; returns ISO yyyy-MM-dd or null.</summary>
    internal static string? NormalizeDate(string? s)
    {
        if (s is null) return null;
        string[] formats = ["M-d-yyyy", "MM-dd-yyyy", "M/d/yyyy", "MM/dd/yyyy", "yyyy-MM-dd"];
        return DateTime.TryParseExact(s.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
    }

    public override CollectorResult Normalize(HardwareRaw raw)
    {
        var facts = new List<Fact>();
        var gaps = new List<string>(raw.Failures);

        if (raw.Cpu is { } c)
        {
            var value = new JsonObject
            {
                ["name"] = c.Name, ["vendor"] = c.Vendor, ["nominalMhz"] = c.NominalMhz, ["packages"] = c.Packages,
                ["cores"] = c.Cores, ["logicalProcessors"] = c.LogicalProcessors,
                ["performanceCores"] = c.PerformanceCores, ["efficiencyCores"] = c.EfficiencyCores,
            };
            var cores = c.Cores is { } n ? $"{n} cores, " : "";
            facts.Add(new Fact("system.cpu", "Processor registry and topology", "reported", value,
                $"{c.Name ?? "Processor"}: {cores}{c.LogicalProcessors} logical processors.", null));
        }

        if (raw.Memory is { } m && (m.InstalledBytes ?? m.UsableBytes) is { } total)
        {
            var value = new JsonObject { ["installedBytes"] = m.InstalledBytes, ["usableBytes"] = m.UsableBytes };
            facts.Add(new Fact("system.memory", "Memory status", "measured", value, $"{Sys.Gib(total)} GiB of memory installed.", null));
        }
        else if (raw.Memory is not null) gaps.Add("memory");

        if (raw.Gpus is { } gpus)
        {
            var seen = new HashSet<string>();
            foreach (var g in gpus)
            {
                var subject = $"system.gpu.{Sys.Hash8($"{g.VendorId:x4}:{g.DeviceId:x4}:{g.Name}")}";
                if (!seen.Add(subject)) continue;
                var value = new JsonObject
                {
                    ["name"] = g.Name, ["vendorId"] = $"{g.VendorId:x4}", ["deviceId"] = $"{g.DeviceId:x4}",
                    ["dedicatedMemoryBytes"] = g.DedicatedBytes, ["sharedMemoryBytes"] = g.SharedBytes,
                    ["driverVersion"] = g.DriverVersion, ["driverDate"] = g.DriverDate, ["driverProvider"] = g.DriverProvider,
                };
                var drv = g.DriverVersion is { } v ? $", driver {v}{(g.DriverDate is { } dd ? $" ({dd})" : "")}" : "";
                facts.Add(new Fact(subject, "DXGI adapters and display driver key", "reported", value, $"{g.Name}{drv}.", null));
            }
        }
        else gaps.Add("gpu");

        if (raw.Firmware is { } f && (f.BiosVendor ?? f.BiosVersion ?? f.SystemManufacturer) is not null)
        {
            var value = new JsonObject
            {
                ["biosVendor"] = f.BiosVendor, ["biosVersion"] = f.BiosVersion, ["biosReleaseDate"] = f.BiosReleaseDate,
                ["systemManufacturer"] = f.SystemManufacturer, ["systemProductName"] = f.SystemProductName, ["systemFamily"] = f.SystemFamily,
            };
            var model = string.Join(' ', new[] { f.SystemManufacturer, f.SystemProductName }.Where(s => s is not null));
            var fw = $"firmware {f.BiosVersion ?? "unknown"}{(f.BiosReleaseDate is { } d ? $" ({d})" : "")}";
            facts.Add(new Fact("system.firmware", "Firmware registry", "reported", value,
                model.Length > 0 ? $"{model}, {fw}." : $"Firmware {f.BiosVersion ?? "unknown"}.", null));
        }
        else if (raw.Firmware is not null) gaps.Add("firmware");

        if (gaps.Count == 0) return Sys.Result(facts, "complete", null);
        if (facts.Count == 0) return Sys.Result(facts, "failed", raw.GpuError ?? "no hardware source readable");
        var reason = string.Join(", ", gaps.Distinct()) + " unavailable" + (raw.GpuError is { } e ? $" ({e})" : "");
        return Sys.Result(facts, "partial", reason);
    }
}

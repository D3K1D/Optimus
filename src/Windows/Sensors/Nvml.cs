using System.Text;
using OptimizationTool.Windows.Security.Common;

namespace OptimizationTool.Windows.Sensors;

/// <summary>
/// NVML (NVIDIA only). nvml.dll is loaded by full path, from System32, else %ProgramFiles%\NVIDIA Corporation\NVSMI,
/// and only when its Authenticode signature is valid and the signer is NVIDIA Corporation. Loaded at most once per
/// process and never unloaded (a timed-out call may still be inside it).
/// </summary>
internal sealed unsafe class NvmlLibrary
{
    private const int Success = 0;
    private static readonly Lock LoadGate = new();
    private static NvmlLibrary? _loaded;
    private static bool _tried;
    private static readonly Lock RefGate = new();
    private int _refs;

    private delegate* unmanaged<int> _init, _shutdown;
    private delegate* unmanaged<uint*, int> _count;
    private delegate* unmanaged<uint, nint*, int> _handleByIndex;
    private delegate* unmanaged<nint, byte*, int> _pciInfo;
    private delegate* unmanaged<nint, int, uint*, int> _temperature, _threshold, _clock, _maxClock;
    private delegate* unmanaged<nint, uint*, int> _power, _powerLimit, _fan;
    private delegate* unmanaged<nint, ulong*, int> _reasons;

    /// <summary>The candidate paths in order (full paths only; nothing from the search path).</summary>
    public static IEnumerable<string> CandidatePaths()
    {
        yield return Path.Combine(Environment.SystemDirectory, "nvml.dll");
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(pf)) yield return Path.Combine(pf, "NVIDIA Corporation", "NVSMI", "nvml.dll");
    }

    public static bool IsNvidiaSigner(SignatureInfo info) =>
        info.Status == "valid" && info.Signer is { } s && s.StartsWith("NVIDIA Corporation", StringComparison.OrdinalIgnoreCase);

    /// <summary>The verified, loaded library, or null (not installed, not NVIDIA-signed, or missing exports).</summary>
    public static NvmlLibrary? Load()
    {
        lock (LoadGate)
        {
            if (_tried) return _loaded;
            _tried = true;
            using var checker = new SignatureChecker();
            foreach (var path in CandidatePaths())
            {
                if (!File.Exists(path)) continue;
                if (!IsNvidiaSigner(checker.Check(path))) continue;
                var module = Native.LoadLibraryEx(path, 0, Native.LoadLibrarySearchDllLoadDir | Native.LoadLibrarySearchSystem32);
                if (module == 0) continue;
                var lib = new NvmlLibrary();
                if (lib.Bind(module)) { _loaded = lib; break; }
            }
            return _loaded;
        }
    }

    private bool Bind(nint m)
    {
        nint P(string name) => Native.GetProcAddress(m, name);
        _init = (delegate* unmanaged<int>)P("nvmlInit_v2");
        _shutdown = (delegate* unmanaged<int>)P("nvmlShutdown");
        _count = (delegate* unmanaged<uint*, int>)P("nvmlDeviceGetCount_v2");
        _handleByIndex = (delegate* unmanaged<uint, nint*, int>)P("nvmlDeviceGetHandleByIndex_v2");
        var pci = P("nvmlDeviceGetPciInfo_v3");
        if (pci == 0) pci = P("nvmlDeviceGetPciInfo_v2");
        _pciInfo = (delegate* unmanaged<nint, byte*, int>)pci;
        _temperature = (delegate* unmanaged<nint, int, uint*, int>)P("nvmlDeviceGetTemperature");
        _threshold = (delegate* unmanaged<nint, int, uint*, int>)P("nvmlDeviceGetTemperatureThreshold");
        _clock = (delegate* unmanaged<nint, int, uint*, int>)P("nvmlDeviceGetClockInfo");
        _maxClock = (delegate* unmanaged<nint, int, uint*, int>)P("nvmlDeviceGetMaxClockInfo");
        _power = (delegate* unmanaged<nint, uint*, int>)P("nvmlDeviceGetPowerUsage");
        _powerLimit = (delegate* unmanaged<nint, uint*, int>)P("nvmlDeviceGetEnforcedPowerLimit");
        _fan = (delegate* unmanaged<nint, uint*, int>)P("nvmlDeviceGetFanSpeed");
        var reasons = P("nvmlDeviceGetCurrentClocksEventReasons");
        if (reasons == 0) reasons = P("nvmlDeviceGetCurrentClocksThrottleReasons");
        _reasons = (delegate* unmanaged<nint, ulong*, int>)reasons;
        return _init != null && _shutdown != null && _count != null && _handleByIndex != null && _pciInfo != null;
    }

    /// <summary>nvmlInit (ref-counted by NVML itself; we keep our own count for Release).</summary>
    public bool Acquire()
    {
        lock (RefGate)
        {
            if (_init() != Success) return false;
            _refs++;
            return true;
        }
    }

    public void Release()
    {
        lock (RefGate)
        {
            if (_refs == 0) return;
            _refs--;
            _shutdown();
        }
    }

    public List<(nint Handle, NvmlPci Pci)> Devices()
    {
        var list = new List<(nint, NvmlPci)>();
        uint n;
        if (_count(&n) != Success) return list;
        var buf = stackalloc byte[256];
        for (uint i = 0; i < n && i < 16; i++)
        {
            nint h;
            if (_handleByIndex(i, &h) != Success) continue;
            // nvmlPciInfo_t: busIdLegacy[16], domain, bus, device, pciDeviceId, pciSubSystemId, busId[32] (68 bytes).
            new Span<byte>(buf, 256).Clear();
            if (_pciInfo(h, buf) != Success) continue;
            var u = (uint*)(buf + 16);
            var busId = Encoding.ASCII.GetString(buf + 36, 32).TrimEnd('\0');
            list.Add((h, new NvmlPci(u[0], u[1], u[2], u[3], busId)));
        }
        return list;
    }

    private const int TemperatureGpu = 0, ThresholdSlowdown = 1, ClockGraphics = 0;

    public uint? Temperature(nint h) { uint v; return _temperature != null && _temperature(h, TemperatureGpu, &v) == Success ? v : null; }
    public uint? SlowdownThreshold(nint h) { uint v; return _threshold != null && _threshold(h, ThresholdSlowdown, &v) == Success ? v : null; }
    public uint? GraphicsClock(nint h) { uint v; return _clock != null && _clock(h, ClockGraphics, &v) == Success ? v : null; }
    public uint? MaxGraphicsClock(nint h) { uint v; return _maxClock != null && _maxClock(h, ClockGraphics, &v) == Success ? v : null; }
    public uint? PowerMilliwatts(nint h) { uint v; return _power != null && _power(h, &v) == Success ? v : null; }
    public uint? PowerLimitMilliwatts(nint h) { uint v; return _powerLimit != null && _powerLimit(h, &v) == Success ? v : null; }
    public uint? FanPercent(nint h) { uint v; return _fan != null && _fan(h, &v) == Success ? v : null; }
    public ulong? Reasons(nint h) { ulong v; return _reasons != null && _reasons(h, &v) == Success ? v : null; }
}

/// <summary>One NVML device matched to the benchmark adapter; holds one nvmlInit reference until disposed.</summary>
internal sealed class NvmlDevice : IDisposable
{
    private readonly NvmlLibrary _lib;
    private readonly nint _handle;
    private int _disposed;

    private NvmlDevice(NvmlLibrary lib, nint handle)
    {
        _lib = lib;
        _handle = handle;
    }

    /// <summary>Initialises NVML and returns the device matching <paramref name="adapter"/>, or null.</summary>
    public static NvmlDevice? Open(NvmlLibrary lib, AdapterPci adapter)
    {
        if (!lib.Acquire()) return null;
        try
        {
            var devices = lib.Devices();
            if (GpuNormalizer.Match(adapter, devices.Select(d => d.Pci).ToList()) is { } i) return new NvmlDevice(lib, devices[i].Handle);
        }
        catch (Exception) { /* fall through: NVML unused */ }
        lib.Release();
        return null;
    }

    public double? TemperatureC() => _lib.Temperature(_handle) is { } t and > 0 and < 200 ? t : null;
    public double? SlowdownTempC() => _lib.SlowdownThreshold(_handle) is { } t and > 0 and < 200 ? t : null;
    public double? GraphicsClockMHz() => _lib.GraphicsClock(_handle);
    public double? MaxGraphicsClockMHz() => _lib.MaxGraphicsClock(_handle) is { } m and > 0 ? m : null;
    public double? PowerMilliwatts() => _lib.PowerMilliwatts(_handle);
    public double? PowerLimitW() => _lib.PowerLimitMilliwatts(_handle) is { } mw and > 0 ? Math.Round(mw / 1000.0, 0) : null;
    public double? FanPercent() => _lib.FanPercent(_handle);
    public ulong? ClocksEventReasons() => _lib.Reasons(_handle);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _lib.Release();
    }
}

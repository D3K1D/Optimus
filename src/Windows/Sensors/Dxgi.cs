using System.Globalization;

namespace OptimizationTool.Windows.Sensors;

internal sealed record DxgiAdapterInfo(string Description, uint VendorId, uint DeviceId, long Luid, bool Software)
{
    public string LuidHex => Luids.Format(Luid);
}

/// <summary>LUIDs as 16 hex chars: HighPart (8) then LowPart (8), i.e. the 64-bit value, uppercase.</summary>
internal static class Luids
{
    public static string Format(long luid) => ((ulong)luid).ToString("X16", CultureInfo.InvariantCulture);

    public static long? Parse(string? hex) =>
        hex is { Length: 16 } && ulong.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v) ? (long)v : null;
}

/// <summary>DXGI adapter enumeration (read-only Get* calls).</summary>
internal static unsafe class Dxgi
{
    private const int Cap = 16;

    /// <summary>Adapters in high-performance order (IDXGIFactory6), or plain order when Factory6 is unavailable.</summary>
    public static List<DxgiAdapterInfo> Adapters()
    {
        var list = new List<DxgiAdapterInfo>();
        if (Native.CreateDXGIFactory1(Native.IidDxgiFactory6, out var f6) >= 0 && f6 != 0)
        {
            try
            {
                for (uint i = 0; i < Cap; i++)
                {
                    var hr = Native.EnumAdapterByGpuPreference(f6, i, Native.DxgiGpuPreferenceHighPerformance, out var a);
                    if (hr < 0) break;
                    try { if (Describe(a) is { } d) list.Add(d); }
                    finally { Native.ComRelease(a); }
                }
            }
            finally { Native.ComRelease(f6); }
            if (list.Count > 0) return list;
        }

        var rc = Native.CreateDXGIFactory1(Native.IidDxgiFactory1, out var f1);
        if (rc < 0) throw new global::System.ComponentModel.Win32Exception(rc);
        try
        {
            for (uint i = 0; i < Cap; i++)
            {
                var hr = Native.EnumAdapters1(f1, i, out var a);
                if (hr < 0) break;
                try { if (Describe(a) is { } d) list.Add(d); }
                finally { Native.ComRelease(a); }
            }
        }
        finally { Native.ComRelease(f1); }
        return list;
    }

    private static DxgiAdapterInfo? Describe(nint adapter)
    {
        if (Native.GetDesc1(adapter, out var d) < 0) return null;
        var name = new string(d.Description).TrimEnd('\0').Trim();
        return new DxgiAdapterInfo(name, d.VendorId, d.DeviceId, d.AdapterLuid, (d.Flags & Native.DxgiAdapterFlagSoftware) != 0);
    }

    /// <summary>The benchmark adapter: the first non-software adapter in high-performance order.</summary>
    public static DxgiAdapterInfo? HighPerformance(IEnumerable<DxgiAdapterInfo> adapters) =>
        adapters.FirstOrDefault(a => !a.Software && a.VendorId != 0x1414 /* Microsoft Basic Render */);
}

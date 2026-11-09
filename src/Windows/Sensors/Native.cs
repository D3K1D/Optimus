using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OptimizationTool.Windows.Sensors;

// P/Invoke for the Sensors domain (M6a). Every call here is a read or a notification subscription; nothing writes
// Windows state.
internal static unsafe partial class Native
{
    // ---- pdh (English counter names so localized Windows works) ----

    public const uint PdhFmtDouble = 0x00000200;
    public const uint PdhFmtNoCap100 = 0x00008000;
    public const uint PdhMoreData = 0x800007D2;
    public const uint PdhCstatusValidData = 0, PdhCstatusNewData = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct PdhFmtCounterValue
    {
        public uint CStatus;
        public double DoubleValue;
    }

    /// <summary>PDH_FMT_COUNTERVALUE_ITEM_W (x64: name pointer, then the 16-byte value).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PdhFmtCounterValueItem
    {
        public char* Name;
        public PdhFmtCounterValue Value;
    }

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhOpenQueryW(string? dataSource, nuint userData, out nint query);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhAddEnglishCounterW(nint query, string fullCounterPath, nuint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bufferSize, out uint itemCount, byte* items);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhCloseQuery(nint query);

    // ---- kernel32: power, memory, volumes, modules ----

    [StructLayout(LayoutKind.Sequential)]
    public struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetActiveProcessorCount(ushort groupNumber);

    public const ushort AllProcessorGroups = 0xFFFF;

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumePathName(string fileName, char* volumePathName, uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpaceEx(string directory, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);

    public const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
    public const uint FileShareReadWrite = 0x3;
    public const uint OpenExisting = 3;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(SafeFileHandle device, uint code, void* inBuffer, uint inSize, void* outBuffer, uint outSize, out uint returned, nint overlapped);

    public const uint LoadLibrarySearchDllLoadDir = 0x00000100;
    public const uint LoadLibrarySearchSystem32 = 0x00000800;

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint LoadLibraryEx(string fileName, nint file, uint flags);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint GetProcAddress(nint module, string procName);

    // ---- ntdll: real OS version (no manifest shim) ----

    [StructLayout(LayoutKind.Sequential)]
    public struct OsVersionInfoW
    {
        public uint OsVersionInfoSize;
        public uint MajorVersion;
        public uint MinorVersion;
        public uint BuildNumber;
        public uint PlatformId;
        public fixed char CsdVersion[128];
    }

    [LibraryImport("ntdll.dll")]
    public static partial int RtlGetVersion(ref OsVersionInfoW info);

    // ---- powrprof: AC/DC change callback (no window, no thread of our own) ----

    public static readonly Guid GuidAcdcPowerSource = new("5d3e9a59-e9D5-4b00-a6bd-ff34ff516548");
    public const uint DeviceNotifyCallback = 2;
    public const uint PbtPowerSettingChange = 0x8013;

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceNotifySubscribeParameters
    {
        public delegate* unmanaged<nint, uint, nint, uint> Callback;
        public nint Context;
    }

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerSettingRegisterNotification(in Guid settingGuid, uint flags, DeviceNotifySubscribeParameters* recipient, out nint registrationHandle);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerSettingUnregisterNotification(nint registrationHandle);

    // ---- gdi32 D3DKMT: adapter perf data (read-only queries) ----

    public const int KmtqaitypeAdapterAddress = 6;
    public const int KmtqaitypeAdapterPerfData = 62;
    public const int KmtqaitypeAdapterPerfDataCaps = 63;

    [StructLayout(LayoutKind.Sequential)]
    public struct D3dkmtOpenAdapterFromLuid
    {
        public long AdapterLuid;
        public uint Adapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3dkmtQueryAdapterInfo
    {
        public uint Adapter;
        public int Type;
        public void* PrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    /// <summary>D3DKMT_ADAPTER_PERFDATA: memory frequency in Hz, Power in tenths of a percent, Temperature in tenths of °C.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct D3dkmtAdapterPerfData
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency;
        public ulong MaxMemoryFrequency;
        public ulong MaxMemoryFrequencyOC;
        public ulong MemoryBandwidth;
        public ulong PcieBandwidth;
        public uint FanRpm;
        public uint Power;
        public uint Temperature;
        public byte PowerStateOverride;
    }

    /// <summary>D3DKMT_ADAPTER_PERFDATACAPS: TemperatureMax/Warning in tenths of °C.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct D3dkmtAdapterPerfDataCaps
    {
        public uint PhysicalAdapterIndex;
        public ulong MaxMemoryBandwidth;
        public ulong MaxPcieBandwidth;
        public uint MaxFanRpm;
        public uint TemperatureMax;
        public uint TemperatureWarning;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3dkmtAdapterAddress
    {
        public uint BusNumber;
        public uint DeviceNumber;
        public uint FunctionNumber;
    }

    [LibraryImport("gdi32.dll")]
    public static partial int D3DKMTOpenAdapterFromLuid(ref D3dkmtOpenAdapterFromLuid open);

    [LibraryImport("gdi32.dll")]
    public static partial int D3DKMTQueryAdapterInfo(ref D3dkmtQueryAdapterInfo query);

    [LibraryImport("gdi32.dll")]
    public static partial int D3DKMTCloseAdapter(ref uint adapter);

    // ---- dxgi: adapters (COM through the vtable; read-only calls only) ----

    public static readonly Guid IidDxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    public static readonly Guid IidDxgiFactory6 = new("c1b6694f-ff09-44a9-b03c-77900a0a1d17");
    public static readonly Guid IidDxgiAdapter1 = new("29038f61-3839-4626-91fd-086879011a05");
    public const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    public const uint DxgiAdapterFlagSoftware = 2;
    public const int DxgiGpuPreferenceHighPerformance = 2;

    [LibraryImport("dxgi.dll")]
    public static partial int CreateDXGIFactory1(in Guid riid, out nint factory);

    [StructLayout(LayoutKind.Sequential)]
    public struct DxgiAdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    public static uint ComRelease(nint obj)
    {
        if (obj == 0) return 0;
        var vtbl = *(nint**)obj;
        return ((delegate* unmanaged[Stdcall]<nint, uint>)vtbl[2])(obj);
    }

    /// <summary>IDXGIFactory1::EnumAdapters1 (vtable slot 12).</summary>
    public static int EnumAdapters1(nint factory, uint index, out nint adapter)
    {
        var vtbl = *(nint**)factory;
        nint a = 0;
        var hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)vtbl[12])(factory, index, &a);
        adapter = a;
        return hr;
    }

    /// <summary>IDXGIFactory6::EnumAdapterByGpuPreference (vtable slot 29).</summary>
    public static int EnumAdapterByGpuPreference(nint factory6, uint index, int preference, out nint adapter)
    {
        var vtbl = *(nint**)factory6;
        nint a = 0;
        var iid = IidDxgiAdapter1;
        var hr = ((delegate* unmanaged[Stdcall]<nint, uint, int, Guid*, nint*, int>)vtbl[29])(factory6, index, preference, &iid, &a);
        adapter = a;
        return hr;
    }

    /// <summary>IDXGIAdapter1::GetDesc1 (vtable slot 10).</summary>
    public static int GetDesc1(nint adapter, out DxgiAdapterDesc1 desc)
    {
        var vtbl = *(nint**)adapter;
        DxgiAdapterDesc1 d;
        var hr = ((delegate* unmanaged[Stdcall]<nint, DxgiAdapterDesc1*, int>)vtbl[10])(adapter, &d);
        desc = d;
        return hr;
    }
}

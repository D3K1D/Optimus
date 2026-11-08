using System.Runtime.InteropServices;

// Namespace is not "...Windows.System": that would shadow the BCL System namespace for sibling domains.
namespace OptimizationTool.Windows.SystemDomain;

// P/Invoke for the System & performance domain. Every call here is a read; nothing writes Windows state.
internal static unsafe partial class Native
{
    public const int ErrorSuccess = 0;
    public const int ErrorMoreData = 234;
    public const int ErrorNoMoreItems = 259;

    // ---- kernel32: memory, CPU times, power status, topology ----

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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetActiveProcessorCount(ushort groupNumber);

    public const ushort AllProcessorGroups = 0xFFFF;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetLogicalProcessorInformationEx(int relationship, byte* buffer, ref uint returnedLength);

    public const int RelationProcessorCore = 0;
    public const int RelationProcessorPackage = 3;

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetThreadTimes(nint thread, out long creation, out long exit, out long kernel, out long user);

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

    // ---- psapi: commit charge ----

    [StructLayout(LayoutKind.Sequential)]
    public struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal;
        public nuint CommitLimit;
        public nuint CommitPeak;
        public nuint PhysicalTotal;
        public nuint PhysicalAvailable;
        public nuint SystemCache;
        public nuint KernelTotal;
        public nuint KernelPaged;
        public nuint KernelNonpaged;
        public nuint PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetPerformanceInfo(out PerformanceInformation info, uint size);

    // ---- ntdll: one-call process snapshot (no per-process handles, works as standard user) ----

    public const int SystemProcessInformationClass = 5;
    public const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    [LibraryImport("ntdll.dll")]
    public static partial int NtQuerySystemInformation(int infoClass, void* buffer, uint length, out uint returnLength);

    // SYSTEM_PROCESS_INFORMATION field offsets (x64).
    public const int SpiNextEntryOffset = 0;
    public const int SpiCreateTime = 32;
    public const int SpiUserTime = 40;
    public const int SpiKernelTime = 48;
    public const int SpiImageNameLength = 56;
    public const int SpiImageNameBuffer = 64;
    public const int SpiUniqueProcessId = 80;
    public const int SpiWorkingSetSize = 144;

    // ---- user32: foreground window owner, display devices ----

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    // ---- pdh: disk counters (English names so localized Windows works) ----

    public const uint PdhFmtDouble = 0x00000200;
    public const uint PdhFmtNoCap100 = 0x00008000;

    [StructLayout(LayoutKind.Sequential)]
    public struct PdhFmtCounterValue
    {
        public uint CStatus;
        public double DoubleValue;
    }

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhOpenQueryW(string? dataSource, nuint userData, out nint query);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhAddEnglishCounterW(nint query, string fullCounterPath, nuint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out PdhFmtCounterValue value);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhCloseQuery(nint query);

    // ---- powrprof: schemes, active scheme, overlay ----

    public const uint AccessScheme = 16;

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerEnumerate(nint rootPowerKey, Guid* schemeGuid, Guid* subGroupGuid, uint accessFlags, uint index, byte* buffer, ref uint bufferSize);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerReadFriendlyName(nint rootPowerKey, Guid* schemeGuid, Guid* subGroupGuid, Guid* settingGuid, byte* buffer, ref uint bufferSize);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerGetActiveScheme(nint userRootPowerKey, out Guid* activePolicyGuid);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerReadACValueIndex(nint rootPowerKey, Guid* schemeGuid, Guid* subGroupGuid, Guid* settingGuid, out uint acValueIndex);

    [LibraryImport("kernel32.dll")]
    public static partial nint LocalFree(nint mem);

    // ---- storage IOCTLs (opened with zero access rights: no admin needed) ----

    public const uint IoctlStorageQueryProperty = 0x002D1400;
    public const int StorageDeviceProperty = 0;
    public const int StorageDeviceSeekPenaltyProperty = 7;
    public const uint FileShareReadWrite = 0x3;
    public const uint OpenExisting = 3;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint code, void* inBuffer, uint inSize, void* outBuffer, uint outSize, out uint returned, nint overlapped);

    // ---- dxgi: adapters (COM through the vtable; read-only Get* calls only) ----

    public static readonly Guid IidDxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    [LibraryImport("dxgi.dll")]
    public static partial int CreateDXGIFactory1(in Guid riid, out nint factory);

    public const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    public const uint DxgiAdapterFlagSoftware = 2;

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

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OptimizationTool.Bench;

internal static partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FILE_STORAGE_INFO
    {
        public uint LogicalBytesPerSector;
        public uint PhysicalBytesPerSectorForAtomicity;
        public uint PhysicalBytesPerSectorForPerformance;
        public uint FileSystemEffectivePhysicalBytesPerSectorForAtomicity;
        public uint Flags;
        public uint ByteOffsetForSectorAlignment;
        public uint ByteOffsetForPartitionAlignment;
    }

    internal const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    internal const uint CREATE_NEW = 1;
    internal const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    internal const uint FILE_FLAG_WRITE_THROUGH = 0x80000000, FILE_FLAG_OVERLAPPED = 0x40000000,
        FILE_FLAG_NO_BUFFERING = 0x20000000, FILE_FLAG_DELETE_ON_CLOSE = 0x04000000;
    internal const int FileStorageInfo = 16;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition,
        uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetDiskFreeSpaceEx(string directory, out ulong freeToCaller, out ulong total, out ulong totalFree);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetVolumePathName(string path, char* buffer, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FILE_STORAGE_INFO info, uint size);

    /// <summary>(available, total) physical memory, or null when the call fails.</summary>
    internal static (ulong Available, ulong Total)? Memory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref m) ? (m.ullAvailPhys, m.ullTotalPhys) : null;
    }

    /// <summary>True when available physical memory is below 10 % of total (§4 RAM stop rule). Unscaled.</summary>
    internal static bool MemoryLow() => Memory() is { } m && m.Total > 0 && m.Available * 10 < m.Total;

    /// <summary>"C:" for a path on C:\, the mount folder for a mounted volume; null when unknown.</summary>
    internal static unsafe string? VolumeOf(string path)
    {
        var buf = stackalloc char[1024];
        if (!GetVolumePathName(path, buf, 1024)) return null;
        var s = new string(buf).TrimEnd('\\');
        return s.Length == 0 ? null : s;
    }
}

/// <summary>SplitMix64: tiny deterministic generator for fixed-seed data (no floating point).</summary>
internal struct SplitMix64(ulong seed)
{
    private ulong _state = seed;

    public ulong Next()
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform-enough value in [0, bound) by multiply-shift (bound &gt; 0).</summary>
    public ulong Below(ulong bound) => (ulong)(((UInt128)Next() * bound) >> 64);
}

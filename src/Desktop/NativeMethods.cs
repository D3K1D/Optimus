using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OptimizationTool.Desktop;

internal static partial class NativeMethods
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* exeName, ref uint size);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static uint ServerProcessId(PipeStream pipe) =>
        GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) ? pid : throw new IOException($"GetNamedPipeServerProcessId failed ({Marshal.GetLastPInvokeError()}).");

    /// <summary>Full Win32 image path of a process, or null if it cannot be queried.</summary>
    public static unsafe string? ProcessImagePath(uint pid)
    {
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle.IsInvalid) return null;
        var buffer = new char[32768];
        var size = (uint)buffer.Length;
        fixed (char* p = buffer)
        {
            if (!QueryFullProcessImageName(handle, 0, p, ref size)) return null;
        }
        return new string(buffer, 0, (int)size);
    }

    /// <summary>Dark title bar (DWMWA_USE_IMMERSIVE_DARK_MODE). Best effort.</summary>
    public static void UseDarkTitleBar(IntPtr hwnd)
    {
        var on = 1;
        _ = DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
    }
}

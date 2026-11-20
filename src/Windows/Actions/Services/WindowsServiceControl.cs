using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using OptimizationTool.Windows.Security.Common;

namespace OptimizationTool.Windows.Actions.Services;

/// <summary>
/// The Service Control Manager, through advapi32 (NEVER RUN on the development PC). Reads: EnumServicesStatusExW,
/// QueryServiceConfigW, QueryServiceConfig2W (delayed auto-start), QueryServiceStatusEx, EnumDependentServicesW (active)
/// and the svchost ServiceDll from the service's registry key. Writes (the broker only): ChangeServiceConfigW with only
/// the start type, ChangeServiceConfig2W for the delayed flag, ControlService(STOP), StartServiceW. Nothing else.
/// </summary>
public sealed class WindowsServiceControl : IServiceControl
{
    private const uint SC_MANAGER_CONNECT = 0x0001, SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    private const uint SERVICE_QUERY_CONFIG = 0x0001, SERVICE_CHANGE_CONFIG = 0x0002, SERVICE_QUERY_STATUS = 0x0004, SERVICE_ENUMERATE_DEPENDENTS = 0x0008,
        SERVICE_START = 0x0010, SERVICE_STOP = 0x0020;
    private const uint SERVICE_NO_CHANGE = 0xFFFFFFFF, SERVICE_CONTROL_STOP = 1, SERVICE_ACTIVE = 1, SERVICE_TYPE_ALL = 0x3F, SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;
    private const int SC_STATUS_PROCESS_INFO = 0, ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    private const int MaxServices = 3000;
    private readonly PathRoots _roots = PathRoots.FromEnvironment();

    public IReadOnlyList<string> Names()
    {
        using var scm = Manager(SC_MANAGER_ENUMERATE_SERVICE);
        uint resume = 0;
        EnumServicesStatusExW(scm, SC_STATUS_PROCESS_INFO, SERVICE_TYPE_ALL, 3 /* SERVICE_STATE_ALL */, IntPtr.Zero, 0, out var needed, out _, ref resume, null);
        if (needed == 0) return [];
        var size = needed + 8192;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            resume = 0;
            if (!EnumServicesStatusExW(scm, SC_STATUS_PROCESS_INFO, SERVICE_TYPE_ALL, 3, buf, size, out _, out var count, ref resume, null))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var names = new List<string>();
            var stride = IntPtr.Size * 2 + 36 + (IntPtr.Size == 8 ? 4 : 0); // ENUM_SERVICE_STATUS_PROCESSW
            for (var i = 0; i < count && i < MaxServices; i++)
                if (Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf + i * stride, 0)) is { Length: > 0 } name) names.Add(name);
            return names;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public ServiceInfo? Read(string name)
    {
        using var scm = Manager(SC_MANAGER_CONNECT);
        using var svc = OpenServiceW(scm, name, SERVICE_QUERY_CONFIG | SERVICE_QUERY_STATUS | SERVICE_ENUMERATE_DEPENDENTS);
        if (svc.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            return err == ERROR_SERVICE_DOES_NOT_EXIST ? null : throw new Win32Exception(err);
        }

        QueryServiceConfigW(svc, IntPtr.Zero, 0, out var needed);
        var cfg = Marshal.AllocHGlobal((int)Math.Max(needed, 64));
        int type, start;
        string? binary, account, display;
        try
        {
            if (!QueryServiceConfigW(svc, cfg, Math.Max(needed, 64), out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            // QUERY_SERVICE_CONFIGW: dwServiceType, dwStartType, dwErrorControl, lpBinaryPathName, lpLoadOrderGroup, dwTagId,
            // lpDependencies, lpServiceStartName, lpDisplayName.
            type = Marshal.ReadInt32(cfg, 0);
            start = Marshal.ReadInt32(cfg, 4);
            var p = IntPtr.Size == 8 ? 16 : 12;
            binary = Marshal.PtrToStringUni(Marshal.ReadIntPtr(cfg, p));
            var ptr = IntPtr.Size;
            var afterTag = p + 2 * ptr + 4 + (IntPtr.Size == 8 ? 4 : 0);
            account = Marshal.PtrToStringUni(Marshal.ReadIntPtr(cfg, afterTag + ptr));
            display = Marshal.PtrToStringUni(Marshal.ReadIntPtr(cfg, afterTag + 2 * ptr));
        }
        finally { Marshal.FreeHGlobal(cfg); }

        var delayed = false;
        var d = Marshal.AllocHGlobal(64);
        try { if (QueryServiceConfig2W(svc, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, d, 64, out _)) delayed = Marshal.ReadInt32(d) != 0; }
        finally { Marshal.FreeHGlobal(d); }

        var status = new SERVICE_STATUS_PROCESS();
        if (!QueryServiceStatusEx(svc, SC_STATUS_PROCESS_INFO, ref status, (uint)Marshal.SizeOf<SERVICE_STATUS_PROCESS>(), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        var image = LaunchString.Parse(binary, _roots, File.Exists, Environment.SystemDirectory).Image;
        return new ServiceInfo(name, display, type, start, delayed, binary, image, ServiceDll(name), account, (int)status.dwCurrentState, Dependents(svc));
    }

    private string? ServiceDll(string name)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name, writable: false);
            using var p = key?.OpenSubKey("Parameters", writable: false);
            var raw = p?.GetValue("ServiceDll", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string
                      ?? key?.GetValue("ServiceDll", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            return raw is null ? null : LaunchString.Parse(raw, _roots, File.Exists, Environment.SystemDirectory).Image;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    private static IReadOnlyList<string> Dependents(SafeServiceHandle svc)
    {
        EnumDependentServicesW(svc, SERVICE_ACTIVE, IntPtr.Zero, 0, out var needed, out _);
        if (needed == 0) return [];
        var buf = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!EnumDependentServicesW(svc, SERVICE_ACTIVE, buf, needed, out _, out var count)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var stride = IntPtr.Size * 2 + 28 + (IntPtr.Size == 8 ? 4 : 0); // ENUM_SERVICE_STATUSW
            var list = new List<string>();
            for (var i = 0; i < count && i < 256; i++)
                if (Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf + i * stride, 0)) is { Length: > 0 } n) list.Add(n);
            return list;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void SetStartType(string name, int startType, bool delayed)
    {
        if (startType is not (2 or 3 or 4)) throw new ArgumentOutOfRangeException(nameof(startType));
        using var scm = Manager(SC_MANAGER_CONNECT);
        using var svc = Open(scm, name, SERVICE_CHANGE_CONFIG | SERVICE_QUERY_CONFIG);
        if (!ChangeServiceConfigW(svc, SERVICE_NO_CHANGE, (uint)startType, SERVICE_NO_CHANGE, null, null, IntPtr.Zero, null, null, null, null))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (startType == 2)
        {
            var info = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(info, delayed ? 1 : 0);
                if (!ChangeServiceConfig2W(svc, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { Marshal.FreeHGlobal(info); }
        }
    }

    public void Stop(string name, TimeSpan wait)
    {
        using var scm = Manager(SC_MANAGER_CONNECT);
        using var svc = Open(scm, name, SERVICE_STOP | SERVICE_QUERY_STATUS);
        var status = new SERVICE_STATUS();
        if (!ControlService(svc, SERVICE_CONTROL_STOP, ref status)) throw new Win32Exception(Marshal.GetLastWin32Error());
        WaitFor(svc, 1 /* STOPPED */, wait);
    }

    public void Start(string name, TimeSpan wait)
    {
        using var scm = Manager(SC_MANAGER_CONNECT);
        using var svc = Open(scm, name, SERVICE_START | SERVICE_QUERY_STATUS);
        if (!StartServiceW(svc, 0, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        WaitFor(svc, 4 /* RUNNING */, wait);
    }

    private static void WaitFor(SafeServiceHandle svc, uint state, TimeSpan wait)
    {
        var until = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < until)
        {
            var s = new SERVICE_STATUS_PROCESS();
            if (!QueryServiceStatusEx(svc, SC_STATUS_PROCESS_INFO, ref s, (uint)Marshal.SizeOf<SERVICE_STATUS_PROCESS>(), out _)) return;
            if (s.dwCurrentState == state) return;
            Thread.Sleep(250);
        }
    }

    private static SafeServiceHandle Manager(uint access)
    {
        var h = OpenSCManagerW(null, null, access);
        return h.IsInvalid ? throw new Win32Exception(Marshal.GetLastWin32Error()) : h;
    }

    private static SafeServiceHandle Open(SafeServiceHandle scm, string name, uint access)
    {
        var h = OpenServiceW(scm, name, access);
        return h.IsInvalid ? throw new Win32Exception(Marshal.GetLastWin32Error()) : h;
    }

    internal sealed class SafeServiceHandle() : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode, dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS_PROCESS
    {
        public uint dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode, dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint, dwProcessId, dwServiceFlags;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenServiceW(SafeServiceHandle scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumServicesStatusExW(SafeServiceHandle scm, int infoLevel, uint serviceType, uint state, IntPtr buffer, uint bufSize,
        out uint bytesNeeded, out uint count, ref uint resume, string? group);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfigW(SafeServiceHandle svc, IntPtr config, uint size, out uint needed);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig2W(SafeServiceHandle svc, uint level, IntPtr buffer, uint size, out uint needed);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(SafeServiceHandle svc, int level, ref SERVICE_STATUS_PROCESS status, uint size, out uint needed);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDependentServicesW(SafeServiceHandle svc, uint state, IntPtr buffer, uint size, out uint needed, out uint count);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfigW(SafeServiceHandle svc, uint type, uint start, uint errorControl, string? binaryPath, string? loadOrderGroup,
        IntPtr tagId, string? dependencies, string? startName, string? password, string? displayName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2W(SafeServiceHandle svc, uint level, IntPtr info);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(SafeServiceHandle svc, uint control, ref SERVICE_STATUS status);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceW(SafeServiceHandle svc, uint argc, IntPtr argv);
}

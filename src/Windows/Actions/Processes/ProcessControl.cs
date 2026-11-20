using System.Runtime.InteropServices;
using System.Security.Principal;

namespace OptimizationTool.Windows.Actions.Processes;

/// <summary>
/// One running process as Windows reports it now. CreateTime is the creation FILETIME (a reused PID has another);
/// UserSid and Elevated come from its token and are null when the token can't be read (another account, or elevated).
/// </summary>
internal sealed record ProcessInfo(uint Pid, long CreateTime, string ImagePath, uint SessionId, string? UserSid, bool? Elevated);

/// <summary>Terminate result: ended | gone (already exited) | mismatch (another process holds the PID) | denied | timeout | error.</summary>
internal sealed record TerminateResult(string Status, int Win32Error = 0);

/// <summary>
/// process.end's seam. <see cref="Terminate"/> is the only write: it opens the PID once, re-checks creation time and
/// image path on that same handle, and only then ends it. Tests use a fake; no test may call the real Terminate.
/// </summary>
internal interface IProcessControl
{
    uint SelfPid { get; }
    uint CurrentSession { get; }
    string? CurrentUserSid { get; }
    /// <summary>The process with this PID now, or null when none is running or it can't be opened at all.</summary>
    ProcessInfo? Query(uint pid);
    TerminateResult Terminate(uint pid, long createTime, string imagePath, TimeSpan wait);
}

/// <summary>Real implementation (kernel32/advapi32): limited-information handles, never debug privileges.</summary>
internal sealed partial class WindowsProcessControl : IProcessControl
{
    private const uint ProcessTerminate = 0x0001, ProcessQueryLimited = 0x1000, Synchronize = 0x00100000, TokenQuery = 0x0008;
    private const int TokenUserClass = 1, TokenElevationClass = 20;
    private const uint WaitObject0 = 0, WaitTimeout = 0x102, StillActive = 259;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(nint process, uint flags, char* name, ref int size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ProcessIdToSessionId(uint pid, out uint session);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(nint token, int infoClass, nint buffer, int length, out int returned);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    public uint SelfPid { get; } = (uint)Environment.ProcessId;

    public uint CurrentSession => ProcessIdToSessionId(SelfPid, out var s) ? s : uint.MaxValue;

    public string? CurrentUserSid
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return id.User?.Value;
        }
    }

    public ProcessInfo? Query(uint pid)
    {
        var h = OpenProcess(ProcessQueryLimited, false, pid);
        if (h == 0) return null;
        try { return Describe(h, pid); }
        finally { CloseHandle(h); }
    }

    private static unsafe ProcessInfo? Describe(nint h, uint pid)
    {
        if (!GetProcessTimes(h, out var created, out _, out _, out _)) return null;
        if (!GetExitCodeProcess(h, out var code) || code != StillActive) return null; // exited (its handle can outlive it)
        var buf = stackalloc char[1024];
        var size = 1024;
        if (!QueryFullProcessImageName(h, 0, buf, ref size)) return null;
        var image = new string(buf, 0, size);
        var session = ProcessIdToSessionId(pid, out var s) ? s : uint.MaxValue;
        var (sid, elevated) = Token(h);
        return new ProcessInfo(pid, created, image, session, sid, elevated);
    }

    private static (string? Sid, bool? Elevated) Token(nint process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token)) return (null, null);
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            string? sid = null;
            bool? elevated = null;
            if (GetTokenInformation(token, TokenUserClass, buffer, 256, out _))
                sid = new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value; // TOKEN_USER.User.Sid
            if (GetTokenInformation(token, TokenElevationClass, buffer, 4, out _))
                elevated = Marshal.ReadInt32(buffer) != 0;
            return (sid, elevated);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            CloseHandle(token);
        }
    }

    public TerminateResult Terminate(uint pid, long createTime, string imagePath, TimeSpan wait)
    {
        var h = OpenProcess(ProcessTerminate | ProcessQueryLimited | Synchronize, false, pid);
        if (h == 0)
        {
            var err = Marshal.GetLastPInvokeError();
            return err == 87 ? new("gone") : new("denied", err); // ERROR_INVALID_PARAMETER: no such process
        }
        try
        {
            // Identity on the same handle that ends it: a PID reused since the check is never ended.
            var now = Describe(h, pid);
            if (now is null) return new("gone");
            if (now.CreateTime != createTime || !string.Equals(now.ImagePath, imagePath, StringComparison.OrdinalIgnoreCase)) return new("mismatch");
            if (!TerminateProcess(h, 1)) return new("error", Marshal.GetLastPInvokeError());
            var ms = (uint)Math.Clamp(wait.TotalMilliseconds, 0, 30_000);
            return WaitForSingleObject(h, ms) switch
            {
                WaitObject0 => new("ended"),
                WaitTimeout => new("timeout"),
                _ => new("error", Marshal.GetLastPInvokeError()),
            };
        }
        finally { CloseHandle(h); }
    }
}

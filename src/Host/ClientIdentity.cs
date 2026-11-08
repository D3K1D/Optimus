using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace OptimizationTool.Host;

/// <summary>Who is on the other end of an accepted pipe connection (protocol §1 "Client identity").</summary>
public sealed record ClientInfo(uint ProcessId, SecurityIdentifier? User, uint SessionId, string? ImagePath);

public static class ClientIdentity
{
    /// <summary>The check itself: same token user SID and same logon session as the host.</summary>
    public static bool IsAllowed(SecurityIdentifier? clientUser, uint clientSession, SecurityIdentifier serverUser, uint serverSession) =>
        clientUser is not null && clientUser.Equals(serverUser) && clientSession == serverSession;

    public static (SecurityIdentifier User, uint Session) Current()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!ProcessIdToSessionId((uint)Environment.ProcessId, out var session)) throw new Win32Exception();
        return (identity.User!, session);
    }

    /// <summary>Reads the client PID from the pipe, then the user SID from that process's token.</summary>
    public static ClientInfo Inspect(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid)) throw new Win32Exception();
        if (!ProcessIdToSessionId(pid, out var session)) throw new Win32Exception();

        using var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process.IsInvalid) throw new Win32Exception();

        SecurityIdentifier? user = null;
        if (OpenProcessToken(process, TokenQuery, out var token))
        {
            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                user = identity.User;
        }

        string? image = null;
        var buffer = new char[1024];
        var size = buffer.Length;
        if (QueryFullProcessImageNameW(process, 0, buffer, ref size)) image = new string(buffer, 0, size);
        return new ClientInfo(pid, user, session, image);
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, [Out] char[] name, ref int size);
}

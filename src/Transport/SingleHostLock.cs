using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace OptimizationTool.Transport;

/// <summary>Exit codes of OptimizationTool.Host.exe that the shell acts on.</summary>
public static class HostExitCodes
{
    /// <summary>Another host already serves this pipe name (same Windows session): connect to it.</summary>
    public const int AlreadyServing = 2;
    /// <summary>Another host of this user already uses this data folder, in another Windows session.</summary>
    public const int OtherSession = 3;
    /// <summary>The data folder was written by a newer build (database schema too new): the user must update; do not relaunch.</summary>
    public const int NeedsUpdate = 4;
    public const int Usage = 64;
}

/// <summary>
/// One host per user and data folder, across Windows sessions. The pipe (and its mutex) is per logon session, but the
/// database is per user: two sessions of one user (fast user switching, Remote Desktop) would run two hosts on one
/// database, each marking the other's live work as interrupted at startup. This lock is a named mutex in the
/// <c>Global\</c> namespace, so every session of the PC sees it.
/// <para>
/// A host that crashes or is killed never blocks the next start: Windows abandons its mutex, and the next
/// <see cref="TryAcquire"/> takes an abandoned mutex as its own. The mutex's ACL names the user, so the same user
/// elevated and not elevated can both open it (an elevated process's default ACL would shut the other out).
/// </para>
/// <para>Thread-affine like every mutex: acquire and dispose on the same thread (the host's main thread).</para>
/// </summary>
public sealed class SingleHostLock : IDisposable
{
    private readonly Mutex _mutex;
    private bool _disposed;

    private SingleHostLock(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// <c>Global\OptimizationTool.Host.&lt;user SID&gt;.&lt;data folder hash&gt;</c>. The folder is compared as Windows does
    /// (full path, case-insensitive, no trailing separator), so the installed app and a dev build on the default
    /// data folder share one lock, and a host on its own <c>--data-dir</c> has its own.
    /// </summary>
    public static string NameFor(string userSid, string dataDir)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir)).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)), 0, 8).ToLowerInvariant();
        return $@"Global\OptimizationTool.Host.{userSid}.{hash}";
    }

    public static string NameForCurrentUser(string dataDir) => NameFor(CurrentUser().Value, dataDir);

    private static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new InvalidOperationException("No user SID for the current identity.");
    }

    /// <summary>The user (elevated or not) and SYSTEM may use the mutex; nobody else is named.</summary>
    public static MutexSecurity CreateSecurity(SecurityIdentifier user)
    {
        var security = new MutexSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new MutexAccessRule(user, MutexRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), MutexRights.FullControl, AccessControlType.Allow));
        return security;
    }

    /// <summary>The lock, or null while another live process holds it. Never waits.</summary>
    public static SingleHostLock? TryAcquire(string name)
    {
        Mutex mutex;
        try
        {
            mutex = MutexAcl.Create(initiallyOwned: false, name, out _, CreateSecurity(CurrentUser()));
        }
        catch (UnauthorizedAccessException)
        {
            return null; // it exists and this process may not open it: it is somebody's, not ours
        }
        try
        {
            if (!mutex.WaitOne(0))
            {
                mutex.Dispose();
                return null;
            }
        }
        catch (AbandonedMutexException)
        {
            // Its owner died without releasing it (crash or kill): the wait still gave it to this thread.
        }
        return new SingleHostLock(mutex);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { /* not this thread's to release: closing the handle is all that is left */ }
        _mutex.Dispose();
    }
}

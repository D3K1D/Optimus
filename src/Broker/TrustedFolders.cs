using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace OptimizationTool.Broker;

internal enum AceKind { Allow, Deny, Other }

/// <summary>One DACL entry as read from a handle. <see cref="AceKind.Other"/> (object, callback, compound…) is never trusted.</summary>
internal sealed record AceFact(SecurityIdentifier? Sid, uint Mask, AceKind Kind);

/// <summary>What the broker learned about a file or folder through one handle that did not follow reparse points.</summary>
internal sealed record ObjectFacts(bool IsReparsePoint, bool IsDirectory, SecurityIdentifier? Owner, bool DaclProtected, IReadOnlyList<AceFact> Aces);

/// <summary>A folder held open (no delete sharing, so it can't be renamed or removed while held) with the facts read through that handle.</summary>
internal interface IHeldFolder : IDisposable
{
    string Path { get; }
    ObjectFacts Facts { get; }
}

/// <summary>A secret file read through one handle: the facts and the bytes come from the same open, never from two path lookups.</summary>
internal sealed record SecretRead(ObjectFacts Facts, byte[] Bytes, bool TooLong);

/// <summary>
/// The broker's Windows file security (m9 §13 R1), behind a seam so tests never set an ACL or an owner on this PC. It only
/// reports facts and creates new objects with their security applied at creation; it never changes the security of
/// anything that already exists. What counts as trusted is decided in <see cref="TrustPolicy"/>.
/// </summary>
internal interface ITrustedFileSystem
{
    /// <summary>Opens an existing folder without following reparse points (share read/write, not delete); null when nothing is there.</summary>
    IHeldFolder? OpenFolder(string path);
    /// <summary>Creates a new folder (fails if anything exists) with owner Administrators, a protected DACL: SYSTEM and
    /// Administrators full control, the interactive user read and execute (inherited by what is created in it); returns it held.</summary>
    IHeldFolder CreateFolder(string path);
    /// <summary>Renames the entry to <paramref name="newName"/> in its own folder. Throws when it can't (e.g. a handle blocks it).</summary>
    void Retire(string path, string newName);
    /// <summary>Opens an existing file without following reparse points (share read only) and reads up to
    /// <paramref name="maxBytes"/> from that handle; null when nothing is there.</summary>
    SecretRead? ReadSecret(string path, int maxBytes);
    /// <summary>Creates a new file (CreateNew) only SYSTEM and Administrators can open: owner Administrators, protected, no other entry.</summary>
    FileStream CreateSecret(string path);
    /// <summary>Atomically replaces <paramref name="path"/> with <paramref name="source"/> (a rename in the same folder).</summary>
    void ReplaceSecret(string source, string path);
    /// <summary>Opens the lock file exclusively (no sharing at all) without following reparse points, creating it as a secret
    /// (as <see cref="CreateSecret"/>) when it is missing; the facts are read through that handle. Returns null while any
    /// other handle (another broker) has it open. Throws on any other failure.</summary>
    IHeldLock? TryHoldLock(string path);
}

/// <summary>An exclusively held lock file (see <see cref="ITrustedFileSystem.TryHoldLock"/>), with the facts read through its handle.</summary>
internal interface IHeldLock : IDisposable
{
    ObjectFacts Facts { get; }
}

/// <summary>What the broker accepts as its own (m9 §13 R1). A standard user can never make an object owned by Administrators
/// or SYSTEM, so that owner proves elevated provenance; the DACL check proves nobody else can change it now.</summary>
internal static class TrustPolicy
{
    public static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    public static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    public static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    /// <summary>Read and execute (list, read data/attributes/EA, traverse, read permissions, synchronize) plus GENERIC_READ/GENERIC_EXECUTE.</summary>
    public const uint ReadExecute = 0x001200A9 | 0x80000000 | 0x20000000;

    private static bool AdminOrSystem(SecurityIdentifier? sid) => sid is not null && (sid.Equals(Admins) || sid.Equals(SystemSid));

    /// <summary>The trust root (%ProgramData%): a real folder owned by SYSTEM, TrustedInstaller or Administrators.</summary>
    public static bool IsSystemFolder(ObjectFacts f) =>
        !f.IsReparsePoint && f.IsDirectory && f.Owner is { } o && (AdminOrSystem(o) || o.Equals(TrustedInstaller));

    /// <summary>A folder of the broker's chain: not a reparse point, owner Administrators or SYSTEM, protected DACL, and no
    /// allow entry for anyone but SYSTEM/Administrators beyond read and execute.</summary>
    public static bool IsTrustedFolder(ObjectFacts f) =>
        !f.IsReparsePoint && f.IsDirectory && AdminOrSystem(f.Owner) && f.DaclProtected
        && f.Aces.All(a => a.Kind == AceKind.Deny || (a.Kind == AceKind.Allow && (AdminOrSystem(a.Sid) || (a.Mask & ~ReadExecute) == 0)));

    /// <summary>A secret file (key, ledger): a regular file owned by Administrators or SYSTEM, protected, only they have any access.</summary>
    public static bool IsSecret(ObjectFacts f) =>
        !f.IsReparsePoint && !f.IsDirectory && AdminOrSystem(f.Owner) && f.DaclProtected
        && f.Aces.All(a => a.Kind == AceKind.Deny || (a.Kind == AceKind.Allow && AdminOrSystem(a.Sid)));

    /// <summary><c>&lt;name&gt;.untrusted-&lt;yyyyMMddHHmmss&gt;-&lt;8 hex&gt;</c>.</summary>
    public static string RetiredName(string name) =>
        name + ".untrusted-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
}

/// <summary>
/// A trusted chain of folders from the trust root (%ProgramData%) down to <see cref="Path"/> (m9 §13 R1). Nothing that
/// already exists is ever re-secured or adopted: each folder below the root is either created with its security at
/// creation, or opened through a handle that does not follow reparse points and validated through that handle
/// (<see cref="TrustPolicy.IsTrustedFolder"/>). One that fails is retired (renamed <c>&lt;name&gt;.untrusted-…</c>) and a fresh
/// one created; if it can't be retired, this throws and the broker writes nothing. Every folder stays held (no delete
/// sharing) until disposed, so the chain can't be renamed or swapped underneath.
/// </summary>
internal sealed class TrustedChain : IDisposable
{
    private readonly List<IHeldFolder> _held;

    private TrustedChain(string path, List<IHeldFolder> held) { Path = path; _held = held; }

    /// <summary>The broker's trust root: %ProgramData% (validated by <see cref="TrustPolicy.IsSystemFolder"/> on every open).</summary>
    public static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    public string Path { get; }

    /// <summary>Names of entries that were retired while opening (for tests and diagnostics).</summary>
    public IReadOnlyList<string> Retired { get; private init; } = [];

    public static TrustedChain Open(ITrustedFileSystem fs, string trustRoot, string path)
    {
        var rootFull = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(trustRoot));
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        var rel = System.IO.Path.GetRelativePath(rootFull, full);
        if (rel == "." || System.IO.Path.IsPathRooted(rel) || rel.Split(System.IO.Path.DirectorySeparatorChar).Any(n => n is "" or "." or ".."))
            throw new IOException("The folder is not below the trust root.");
        var held = new List<IHeldFolder>();
        var retired = new List<string>();
        try
        {
            var root = fs.OpenFolder(rootFull) ?? throw new IOException("The trust root is missing.");
            held.Add(root);
            if (!TrustPolicy.IsSystemFolder(root.Facts)) throw new IOException("The trust root is not a system folder.");
            var current = rootFull;
            foreach (var name in rel.Split(System.IO.Path.DirectorySeparatorChar))
            {
                var child = System.IO.Path.Combine(current, name);
                var h = fs.OpenFolder(child);
                if (h is not null && !TrustPolicy.IsTrustedFolder(h.Facts))
                {
                    h.Dispose();
                    h = null;
                    var to = TrustPolicy.RetiredName(name);
                    fs.Retire(child, to); // throws when blocked: fail closed
                    retired.Add(System.IO.Path.Combine(current, to));
                }
                if (h is null)
                {
                    h = fs.CreateFolder(child);
                    if (!TrustPolicy.IsTrustedFolder(h.Facts))
                    {
                        h.Dispose();
                        throw new IOException("A newly created folder did not have the broker's security.");
                    }
                }
                held.Add(h);
                current = child;
            }
            return new TrustedChain(full, held) { Retired = retired };
        }
        catch
        {
            foreach (var h in Enumerable.Reverse(held)) h.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var h in Enumerable.Reverse(_held)) h.Dispose();
        _held.Clear();
    }
}

/// <summary>
/// The real Windows side of <see cref="ITrustedFileSystem"/>. Reviewed, never run by the test suite (it would set owners
/// and ACLs on this PC). Every check is made on a handle opened with FILE_FLAG_OPEN_REPARSE_POINT; every new object gets
/// its security descriptor at creation.
/// </summary>
internal sealed class WindowsTrustedFileSystem(SecurityIdentifier user) : ITrustedFileSystem
{
    private const uint READ_CONTROL = 0x00020000, SYNCHRONIZE = 0x00100000, FILE_READ_ATTRIBUTES = 0x0080, GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000, FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
    private const int ERROR_FILE_NOT_FOUND = 2, ERROR_PATH_NOT_FOUND = 3;
    private const uint MOVEFILE_REPLACE_EXISTING = 1, MOVEFILE_WRITE_THROUGH = 8;
    private const uint GENERIC_WRITE = 0x40000000, OPEN_ALWAYS = 4, FILE_ATTRIBUTE_NORMAL = 0x80;
    private const int ERROR_SHARING_VIOLATION = 32, ERROR_LOCK_VIOLATION = 33;

    public IHeldFolder? OpenFolder(string path)
    {
        var h = CreateFileW(path, READ_CONTROL | FILE_READ_ATTRIBUTES | SYNCHRONIZE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (h.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            h.Dispose();
            if (error is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND) return null;
            throw new IOException("The folder could not be opened.", new Win32Exception(error));
        }
        try { return new Held(path, h, Facts(h)); }
        catch { h.Dispose(); throw; }
    }

    public IHeldFolder CreateFolder(string path)
    {
        var security = new DirectorySecurity();
        security.SetOwner(TrustPolicy.Admins);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(TrustPolicy.SystemSid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(TrustPolicy.Admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        var sd = security.GetSecurityDescriptorBinaryForm();
        var pin = GCHandle.Alloc(sd, GCHandleType.Pinned);
        try
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = pin.AddrOfPinnedObject(), bInheritHandle = 0 };
            // CreateDirectoryW fails when anything already has this name: nothing is ever adopted.
            if (!CreateDirectoryW(path, ref sa)) throw new IOException("The folder could not be created.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally { pin.Free(); }
        return OpenFolder(path) ?? throw new IOException("The new folder disappeared.");
    }

    public void Retire(string path, string newName)
    {
        var to = Path.Combine(Path.GetDirectoryName(path)!, newName);
        if (!MoveFileExW(path, to, 0)) throw new IOException("The untrusted entry could not be moved aside.", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    public SecretRead? ReadSecret(string path, int maxBytes)
    {
        using var h = CreateFileW(path, GENERIC_READ | READ_CONTROL | FILE_READ_ATTRIBUTES | SYNCHRONIZE, FILE_SHARE_READ, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (h.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND) return null;
            throw new IOException("The file could not be opened.", new Win32Exception(error));
        }
        var facts = Facts(h);
        if (facts.IsReparsePoint || facts.IsDirectory) return new SecretRead(facts, [], false);
        // The bytes come from this same handle (no second path lookup).
        var buffer = new byte[maxBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = RandomAccess.Read(h, buffer.AsSpan(read), read);
            if (n <= 0) break;
            read += n;
        }
        return read > maxBytes ? new SecretRead(facts, [], true) : new SecretRead(facts, buffer[..read], false);
    }

    public FileStream CreateSecret(string path)
    {
        var security = new FileSecurity();
        security.SetOwner(TrustPolicy.Admins);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(TrustPolicy.SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(TrustPolicy.Admins, FileSystemRights.FullControl, AccessControlType.Allow));
        // Security applied at creation, CreateNew: never an existing file.
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.Read, FileShare.None, 4096, FileOptions.WriteThrough, security);
    }

    public void ReplaceSecret(string source, string path)
    {
        if (!MoveFileExW(source, path, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
            throw new IOException("The file could not be replaced.", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    public IHeldLock? TryHoldLock(string path)
    {
        // The secret's security descriptor (as CreateSecret): applied only if OPEN_ALWAYS creates the file. An existing
        // file keeps its own security, which the caller validates from the facts read through this handle.
        var security = new FileSecurity();
        security.SetOwner(TrustPolicy.Admins);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(TrustPolicy.SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(TrustPolicy.Admins, FileSystemRights.FullControl, AccessControlType.Allow));
        var sd = security.GetSecurityDescriptorBinaryForm();
        var pin = GCHandle.Alloc(sd, GCHandleType.Pinned);
        SafeFileHandle h;
        try
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = pin.AddrOfPinnedObject(), bInheritHandle = 0 };
            // Share mode 0: while this handle is open no other open of the file succeeds (ERROR_SHARING_VIOLATION).
            // FILE_FLAG_OPEN_REPARSE_POINT: a link is opened itself, never followed (and then fails the secret check).
            h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE | READ_CONTROL | FILE_READ_ATTRIBUTES | SYNCHRONIZE, 0, ref sa, OPEN_ALWAYS,
                FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        }
        finally { pin.Free(); }
        if (h.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            h.Dispose();
            if (error is ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION) return null;
            throw new IOException("The lock file could not be opened.", new Win32Exception(error));
        }
        try { return new HeldLock(h, Facts(h)); }
        catch { h.Dispose(); throw; }
    }

    private sealed class HeldLock(SafeFileHandle handle, ObjectFacts facts) : IHeldLock
    {
        public ObjectFacts Facts { get; } = facts;
        public void Dispose() => handle.Dispose();
    }

    /// <summary>Attributes (FileAttributeTagInfo) and owner + DACL (GetSecurityInfo), both through the given handle.</summary>
    private static ObjectFacts Facts(SafeFileHandle h)
    {
        if (!GetFileInformationByHandleEx(h, 9 /* FileAttributeTagInfo */, out var tag, (uint)Marshal.SizeOf<FILE_ATTRIBUTE_TAG_INFO>()))
            throw new IOException("The attributes could not be read.", new Win32Exception(Marshal.GetLastWin32Error()));
        var error = GetSecurityInfo(h, 1 /* SE_FILE_OBJECT */, 0x1 | 0x4 /* OWNER | DACL */, out _, out _, out _, out _, out var sd);
        if (error != 0) throw new IOException("The security could not be read.", new Win32Exception((int)error));
        byte[] bytes;
        try
        {
            bytes = new byte[GetSecurityDescriptorLength(sd)];
            Marshal.Copy(sd, bytes, 0, bytes.Length);
        }
        finally { LocalFree(sd); }
        var raw = new RawSecurityDescriptor(bytes, 0);
        var aces = new List<AceFact>();
        if (raw.DiscretionaryAcl is null)
            aces.Add(new AceFact(new SecurityIdentifier(WellKnownSidType.WorldSid, null), 0x10000000 /* GENERIC_ALL */, AceKind.Allow)); // no DACL: everyone
        else
            foreach (GenericAce ace in raw.DiscretionaryAcl)
                aces.Add(ace is CommonAce c
                    ? new AceFact(c.SecurityIdentifier, unchecked((uint)c.AccessMask),
                        c.AceQualifier == AceQualifier.AccessAllowed ? AceKind.Allow : c.AceQualifier == AceQualifier.AccessDenied ? AceKind.Deny : AceKind.Other)
                    : new AceFact(null, 0, AceKind.Other));
        return new ObjectFacts((tag.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0, (tag.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0, raw.Owner,
            raw.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected), aces);
    }

    private sealed class Held(string path, SafeFileHandle handle, ObjectFacts facts) : IHeldFolder
    {
        public string Path { get; } = path;
        public ObjectFacts Facts { get; } = facts;
        public void Dispose() => handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ATTRIBUTE_TAG_INFO
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, ref SECURITY_ATTRIBUTES security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, ref SECURITY_ATTRIBUTES security);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string from, string to, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FILE_ATTRIBUTE_TAG_INFO info, uint size);

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, int objectType, uint info, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);
}

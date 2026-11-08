using System.Globalization;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace OptimizationTool.Broker;

/// <summary>The one write: INetFwPolicy2.FirewallEnabled[NET_FW_PROFILE2_PUBLIC] in the local store. Nothing else.</summary>
internal sealed class WindowsPublicFirewallWriter : IPublicFirewallWriter
{
    private const int NET_FW_PROFILE2_PUBLIC = 4;

    public void SetPublicEnabled(bool enabled)
    {
        var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!;
        var policy = Activator.CreateInstance(type)!;
        try
        {
            type.InvokeMember("FirewallEnabled", BindingFlags.SetProperty, null, policy, [NET_FW_PROFILE2_PUBLIC, enabled], CultureInfo.InvariantCulture);
        }
        finally { Marshal.ReleaseComObject(policy); }
    }
}

/// <summary>
/// <c>%ProgramData%\OptimizationTool\receipts</c>. Each run re-applies a protected ACL (owner Administrators;
/// SYSTEM + Administrators full; the interactive user read; no inheritance from ProgramData, which lets users
/// create folders) and refuses reparse points, so a user-planted junction can't redirect an elevated write.
/// Receipts are created with CreateNew under random names, never overwritten.
/// </summary>
internal sealed class ProtectedReceiptStore(string root, SecurityIdentifier user) : IReceiptStore
{
    private const int ReadCap = 500;

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OptimizationTool", "receipts");

    public string Write(JsonObject receipt)
    {
        EnsureDirectory();
        var id = "rcpt-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        receipt["receiptId"] = id;
        var path = Path.Combine(root, id + ".json");
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var bytes = Encoding.UTF8.GetBytes(receipt.ToJsonString());
        fs.Write(bytes);
        fs.Flush(flushToDisk: true);
        return id;
    }

    public JsonObject? LatestVerified(string field)
    {
        if (!Directory.Exists(root) || IsReparse(root)) return null;
        JsonObject? best = null;
        string? bestAt = null;
        foreach (var file in Directory.EnumerateFiles(root, "rcpt-*.json").Take(ReadCap))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Length > 64 * 1024 || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject o) continue;
                if ((string?)o["field"] != field || (string?)o["state"] != "verified") continue;
                var at = (string?)o["at"];
                if (best is null || string.CompareOrdinal(at, bestAt) > 0) { best = o; bestAt = at; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { }
        }
        return best;
    }

    /// <summary>Also the home of the nonce ledger.</summary>
    public void EnsureDirectory()
    {
        var parent = Path.GetDirectoryName(root)!;
        foreach (var dir in new[] { parent, root })
        {
            if (Directory.Exists(dir) && IsReparse(dir)) throw new IOException("Refusing a reparse point in the receipts path.");
            Directory.CreateDirectory(dir);
            var security = new DirectorySecurity();
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            security.SetOwner(admins);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(dir).SetAccessControl(security);
            if (IsReparse(dir)) throw new IOException("Refusing a reparse point in the receipts path.");
        }
    }

    private static bool IsReparse(string path) => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
}

/// <summary>Hashes of used nonces, kept in the protected receipts folder (last 1000).</summary>
internal sealed class FileNonceLedger(ProtectedReceiptStore store, string root) : INonceLedger
{
    private string Path_ => Path.Combine(root, "nonces.log");

    public bool WasUsed(string nonce)
    {
        if (!File.Exists(Path_)) return false;
        var h = Hash(nonce);
        return File.ReadLines(Path_).Take(2000).Any(l => l == h);
    }

    public void MarkUsed(string nonce)
    {
        store.EnsureDirectory();
        var lines = File.Exists(Path_) ? File.ReadLines(Path_).Take(2000).ToList() : [];
        lines.Add(Hash(nonce));
        File.WriteAllLines(Path_, lines.TakeLast(1000));
    }

    private static string Hash(string nonce) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(nonce)));
}

/// <summary>Identity of this process and of the pipe server.</summary>
internal static class PeerIdentity
{
    public static PeerInfo Self()
    {
        using var id = WindowsIdentity.GetCurrent();
        ProcessIdToSessionId((uint)Environment.ProcessId, out var session);
        return new PeerInfo((uint)Environment.ProcessId, Environment.ProcessPath, session, id.User);
    }

    public static PeerInfo Server(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)) return new PeerInfo(0, null, uint.MaxValue, null);
        if (!ProcessIdToSessionId(pid, out var session)) session = uint.MaxValue;
        using var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (process.IsInvalid) return new PeerInfo(pid, null, session, null);
        SecurityIdentifier? user = null;
        if (OpenProcessToken(process, 0x0008 /* TOKEN_QUERY */, out var token))
        {
            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                user = identity.User;
        }
        string? image = null;
        var buffer = new char[1024];
        var size = buffer.Length;
        if (QueryFullProcessImageNameW(process, 0, buffer, ref size)) image = new string(buffer, 0, size);
        return new PeerInfo(pid, image, session, user);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint pid, out uint session);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, [Out] char[] name, ref int size);
}

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using OptimizationTool.Engine;

namespace OptimizationTool.Windows.Actions.Scripts;

/// <summary>
/// A script staged for one run (contracts/m9-ai-powers.md §5.4): its own new folder and file, both held open until the run
/// ends. The file is opened without write or delete sharing, so nobody can change, replace or remove it while it runs; the
/// folder handle denies delete, so it can't be renamed or removed either.
/// </summary>
internal sealed class StagedScript(string folder, string scriptPath, SafeFileHandle file, SafeFileHandle directory, IDisposable? root = null) : IDisposable
{
    public string Folder { get; } = folder;
    public string ScriptPath { get; } = scriptPath;

    public void Dispose()
    {
        file.Dispose();
        directory.Dispose();
        root?.Dispose();
    }
}

/// <summary>
/// Stages the exact approved bytes (§5.4 "Run folder and file"): a new folder with a random name under the runs root
/// (current user and SYSTEM only), no reparse point anywhere on the path, the file created new, its identity recorded, then
/// re-opened read-only without write or delete sharing, its identity compared and its bytes re-hashed against the approved
/// sha256. Anything that does not match is not run (hash_mismatch). Writes only inside the runs root.
/// </summary>
internal static class ScriptStage
{
    public const string FileName = "script.ps1";

    /// <param name="afterWrite">Test hook: runs between writing and re-opening (a swapped or changed file must be caught).</param>
    /// <param name="trustedRoot">Null: the user-scope runner (folders restricted to the current user and SYSTEM). The broker
    /// passes its trusted-folder provisioning (m9 §5.4, §13 R1): it makes the runs root trusted (created with its security
    /// at creation, or validated through a held handle, an untrusted one retired and replaced, never re-secured) and holds
    /// it until disposed; the staged script owns that hold. Each run folder is then created inside it and inherits its security.</param>
    public static (StagedScript? Staged, string? Problem) Prepare(string runsRoot, byte[] bytes, string sha256, Action<string>? afterWrite = null,
        Func<string, IDisposable>? trustedRoot = null)
    {
        string folder;
        SafeFileHandle? dir = null;
        IDisposable? hold = null;
        try
        {
            var root = Path.GetFullPath(runsRoot);
            if (Directory.Exists(root) && PowerShellInterpreter.HasReparsePoint(root)) return (null, "folder");
            if (trustedRoot is not null) hold = trustedRoot(root);
            else if (!Directory.Exists(root))
            {
                Directory.CreateDirectory(root);
                Acl.RestrictDirectory(root);
            }
            if (PowerShellInterpreter.HasReparsePoint(root)) { hold?.Dispose(); return (null, "folder"); }
            folder = Path.Combine(root, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)));
            if (Directory.Exists(folder) || File.Exists(folder)) { hold?.Dispose(); return (null, "folder"); }
            Directory.CreateDirectory(folder);
            if (trustedRoot is null) Acl.RestrictDirectory(folder);
            if (PowerShellInterpreter.HasReparsePoint(folder)) { hold?.Dispose(); return (null, "folder"); }
            dir = ScriptNative.OpenDirectory(folder);
            if (dir.IsInvalid) { dir.Dispose(); hold?.Dispose(); return (null, "folder"); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            dir?.Dispose();
            hold?.Dispose();
            return (null, "folder");
        }

        var path = Path.Combine(folder, FileName);
        var staged = false;
        try
        {
            (uint, ulong) written;
            using (var w = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                w.Write(bytes);
                w.Flush(flushToDisk: true);
                written = ScriptNative.FileId(w.SafeFileHandle);
            }
            afterWrite?.Invoke(path);
            var held = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var ok = false;
            try
            {
                if (ScriptNative.FileId(held) != written) return (null, "hash_mismatch");
                var length = RandomAccess.GetLength(held);
                if (length != bytes.Length || length > 64 * 1024) return (null, "hash_mismatch");
                var back = new byte[length];
                var read = 0;
                while (read < back.Length)
                {
                    var n = RandomAccess.Read(held, back.AsSpan(read), read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read != back.Length || Convert.ToHexStringLower(SHA256.HashData(back)) != sha256) return (null, "hash_mismatch");
                ok = true;
                staged = true;
                return (new StagedScript(folder, path, held, dir, hold), null);
            }
            finally
            {
                if (!ok) held.Dispose();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, "hash_mismatch");
        }
        finally
        {
            if (!staged) { dir.Dispose(); hold?.Dispose(); } // on success the StagedScript owns them
        }
    }
}

internal static class ScriptNative
{
    private const uint FILE_LIST_DIRECTORY = 0x0001, SYNCHRONIZE = 0x00100000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000, FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out BY_HANDLE_FILE_INFORMATION info);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll")]
    public static extern uint GetOEMCP();

    /// <summary>A handle on the folder that lets others read and write in it but never delete or rename it.</summary>
    public static SafeFileHandle OpenDirectory(string folder) =>
        CreateFileW(folder, FILE_LIST_DIRECTORY | SYNCHRONIZE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);

    /// <summary>The file's identity: volume serial number and file index.</summary>
    public static (uint Volume, ulong Index) FileId(SafeFileHandle file) =>
        GetFileInformationByHandle(file, out var info)
            ? (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow)
            : throw new IOException("The file's identity could not be read.");
}

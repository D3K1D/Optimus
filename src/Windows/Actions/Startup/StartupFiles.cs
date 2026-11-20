using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using OptimizationTool.Windows.Security;
using OptimizationTool.Windows.Security.Common;

namespace OptimizationTool.Windows.Actions.Startup;

/// <summary>
/// The local key that ties startup evidence to its entry without keeping the name in evidence: SHA-256 (hex) of the
/// exact value or file name, upper-cased (registry value and file names compare without case).
/// </summary>
internal static class StartupKeys
{
    public static string Of(string name) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name.ToUpperInvariant())));

    public static bool IsKey(string? s) => s is { Length: 64 } && s.All(char.IsAsciiHexDigitLower);
}

/// <summary>A shortcut's stored launch configuration (read with STGM_READ; never resolved).</summary>
internal sealed record ShortcutInfo(string? Target, string? Arguments, string? WorkingDirectory);

/// <summary>What a launch string starts: the image, and a host process's payload file (identified: false when it can't be named).</summary>
internal sealed record LaunchInfo(string? Image, string? Payload, bool PayloadIdentified);

/// <summary>
/// The startup handler's file seam: the user's Startup folder, its files, shortcuts and file hashes. Read-only; the
/// handler's only write is the StartupApproved registry value. Tests use a fake.
/// </summary>
internal interface IStartupFiles
{
    /// <summary>The current user's Startup folder, or null.</summary>
    string? UserStartupFolder();
    /// <summary>The all-users (common) Startup folder, or null.</summary>
    string? CommonStartupFolder();
    /// <summary>File names directly in <paramref name="folder"/> (no subfolders); empty when it is missing.</summary>
    IReadOnlyList<string> List(string folder);
    bool Exists(string path);
    ShortcutInfo? ReadShortcut(string path);
    /// <summary>SHA-256 (hex) of a readable regular file up to the size cap, else null (missing, a folder, unreadable, too big).</summary>
    string? Sha256(string path);
    /// <summary>The leaf signer of a validly signed file (Authenticode, offline), else null. The broker reads it itself (m9 §3.7).</summary>
    string? Publisher(string path);
    /// <summary>
    /// Expands the whitelisted %TOKEN%s of a launch string and finds the image it starts and, for a host process
    /// (rundll32, regsvr32, a script host, PowerShell -File), the payload file it runs; PayloadIdentified is false when
    /// the host's arguments can't be tied to one file (<see cref="Security.Common.LaunchString.HostedPayload"/>).
    /// </summary>
    LaunchInfo ParseLaunch(string? command, string? workingDirectory);
}

/// <summary>Real implementation: known folders, directory listing, IShellLinkW (read-only), SHA-256 of files ≤ 512 MiB.</summary>
internal sealed class WindowsStartupFiles : IStartupFiles
{
    private const long MaxHashBytes = 512L << 20;
    private readonly PathRoots _roots = PathRoots.FromEnvironment();

    public string? CommonStartupFolder()
    {
        var f = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
        return string.IsNullOrEmpty(f) ? null : f;
    }

    public string? UserStartupFolder()
    {
        var f = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        return string.IsNullOrEmpty(f) ? null : f;
    }

    public IReadOnlyList<string> List(string folder) =>
        Directory.Exists(folder) ? Directory.EnumerateFiles(folder).Take(500).Select(Path.GetFileName).OfType<string>().ToList() : [];

    public bool Exists(string path) => File.Exists(path);

    public ShortcutInfo? ReadShortcut(string path)
    {
        object? link = null;
        try
        {
            link = Activator.CreateInstance(Type.GetTypeFromCLSID(Native.CLSID_ShellLink, throwOnError: true)!)!;
            if (((IPersistFile)link).Load(path, Native.STGM_READ) != 0) return null;
            var sl = (IShellLinkW)link;
            static string? Read(Func<char[], int, int> get)
            {
                var buf = new char[2048];
                return get(buf, buf.Length) == 0 && new string(buf).TrimEnd('\0') is { Length: > 0 } s ? s : null;
            }
            return new ShortcutInfo(
                Read((b, n) => sl.GetPath(b, n, IntPtr.Zero, Native.SLGP_RAWPATH)),
                Read(sl.GetArguments),
                Read(sl.GetWorkingDirectory));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException) { return null; }
        finally { Automation.Release(link); }
    }

    public string? Sha256(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxHashBytes || info.Attributes.HasFlag(FileAttributes.Directory)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    public string? Publisher(string path) => new Protection.WindowsFileFacts().Publisher(path);

    public LaunchInfo ParseLaunch(string? command, string? workingDirectory)
    {
        var (image, rest) = LaunchString.Split(command, _roots, File.Exists, Environment.SystemDirectory);
        var (payload, identified) = LaunchString.HostedPayload(image, rest, workingDirectory, File.Exists, Environment.SystemDirectory, _roots);
        return new LaunchInfo(image, payload, identified);
    }
}

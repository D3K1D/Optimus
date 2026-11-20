using System.Security.Cryptography;
using OptimizationTool.Windows.Security.Common;

namespace OptimizationTool.Windows.Actions.Protection;

/// <summary>
/// What the administrator handlers read about a file they would switch off or block (m9 §3.2): whether it exists, its
/// SHA-256 (the execution fingerprint) and its Authenticode publisher (the protected lists). Host and broker each read it
/// themselves; the broker never takes a publisher from the host. Read-only; tests use a fake.
/// </summary>
public interface IFileFacts
{
    bool Exists(string path);
    /// <summary>SHA-256 (lowercase hex) of a readable regular file up to the size cap, else null.</summary>
    string? Sha256(string path);
    /// <summary>The leaf signer's display name of a validly signed file (embedded or catalog), else null.</summary>
    string? Publisher(string path);
}

/// <summary>Real implementation: file reads and WinVerifyTrust (offline), as the collectors and the startup handler use them.</summary>
public sealed class WindowsFileFacts : IFileFacts
{
    private const long MaxHashBytes = 512L << 20;

    public bool Exists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
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

    public string? Publisher(string path)
    {
        using var checker = new SignatureChecker();
        var s = checker.Check(path);
        return s.Status == "valid" ? s.Signer : null;
    }

    /// <summary>A validly signed Microsoft file (Windows or Microsoft Corporation; WHQL hardware signatures do not count).</summary>
    public static bool IsMicrosoft(string path)
    {
        using var checker = new SignatureChecker();
        return checker.Check(path).IsMicrosoft;
    }
}

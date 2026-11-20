using System.Diagnostics;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Windows.Security.Common;

namespace OptimizationTool.Windows.Security;

/// <summary>
/// m9 §6.1: the identity of the file a finding's evidence names, read locally for research subjects. The evidence path is
/// normalized ("%ProgramFiles%\App\app.exe"); only the whitelisted roots are expanded back, and a path whose root was not
/// kept ("…\app.exe") is "unknown". Authenticode through <see cref="SignatureChecker"/> (offline, no revocation check);
/// the signer and the product name are returned only for a valid signature. Nothing is written, nothing leaves the PC.
/// </summary>
public sealed class WindowsFileIdentity : IFileIdentity
{
    private readonly PathRoots _roots;
    private readonly Func<string, string?> _productName;

    public WindowsFileIdentity() : this(PathRoots.FromEnvironment(), null) { }

    internal WindowsFileIdentity(PathRoots roots, Func<string, string?>? productName)
    {
        _roots = roots;
        _productName = productName ?? ProductName;
    }

    public FileIdentityInfo Read(string evidencePath)
    {
        var full = Resolve(evidencePath);
        if (full is null) return FileIdentityInfo.Unknown;
        SignatureInfo sig;
        using (var checker = new SignatureChecker()) sig = checker.Check(full);
        return sig.Status switch
        {
            "valid" => new FileIdentityInfo("valid", sig.Signer, _productName(full)),
            "unsigned" or "invalid" => new FileIdentityInfo("none", null, null),
            _ => FileIdentityInfo.Unknown,
        };
    }

    /// <summary>The full local path for a normalized evidence path, or null when it cannot be rebuilt exactly.</summary>
    internal string? Resolve(string evidencePath)
    {
        if (string.IsNullOrWhiteSpace(evidencePath) || !evidencePath.StartsWith('%')) return null; // "…\name" kept no root
        var full = _roots.ExpandKnown(evidencePath);
        if (full.Contains('%') || full.Contains("..", StringComparison.Ordinal) || !Path.IsPathFullyQualified(full) || full.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        return full;
    }

    private static string? ProductName(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).ProductName; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
}

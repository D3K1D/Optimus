using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Providers;

/// <summary>
/// API keys per provider kind. remember=true: current-user DPAPI blob at &lt;dataDir&gt;\secrets\&lt;kind&gt;.bin, entropy
/// bound to the user SID and kind, file and folder ACL current user + SYSTEM only. remember=false: this process's memory only.
/// Keys never appear in exceptions or ToString. A remembered save that fails (IOException, UnauthorizedAccessException,
/// CryptographicException) throws, leaves no temp file and keeps nothing in memory; remember=false never fails on disk errors.
/// </summary>
public sealed partial class DpapiCredentialStore : ICredentialStore
{
    readonly string _secretsDir;
    readonly Dictionary<string, string> _session = new(StringComparer.Ordinal);
    readonly Lock _gate = new();

    public DpapiCredentialStore(string dataDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDir);
        _secretsDir = Path.Combine(dataDir, "secrets");
    }

    internal string PathFor(string kind) => Path.Combine(_secretsDir, ValidKind(kind) + ".bin");

    public void Save(string kind, string apiKey, bool remember)
    {
        var path = PathFor(kind);
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("The API key is empty.", nameof(apiKey));
        lock (_gate)
        {
            if (remember)
            {
                Directory.CreateDirectory(_secretsDir);
                Acl.RestrictDirectory(_secretsDir);
                var plain = Encoding.UTF8.GetBytes(apiKey);
                var entropy = Entropy(kind);
                var temp = path + ".tmp";
                try
                {
                    var blob = ProtectedData.Protect(plain, entropy, DataProtectionScope.CurrentUser);
                    File.WriteAllBytes(temp, blob);
                    Acl.RestrictFile(temp);
                    File.Move(temp, path, overwrite: true);
                }
                catch
                {
                    // A half-written blob never stays behind (a locked folder, a full disk, antivirus, a roaming profile).
                    try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    throw;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plain);
                }
            }
            else
            {
                // This session only. An older remembered key that cannot be removed is still never loaded while this one is held.
                try { if (File.Exists(path)) File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            _session[kind] = apiKey;
        }
    }

    public ProviderCredential? Load(string kind)
    {
        var path = PathFor(kind);
        lock (_gate)
        {
            if (_session.TryGetValue(kind, out var key)) return new ProviderCredential(kind, key);
            if (!File.Exists(path)) return null;
            byte[] plain;
            try
            {
                plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy(kind), DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                return null; // another user's or a corrupted blob: treat as not configured
            }
            catch (IOException)
            {
                return null;
            }
            try
            {
                var apiKey = Encoding.UTF8.GetString(plain);
                _session[kind] = apiKey;
                return new ProviderCredential(kind, apiKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
    }

    public void Delete(string kind)
    {
        var path = PathFor(kind);
        lock (_gate)
        {
            _session.Remove(kind);
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }

    public bool Has(string kind)
    {
        var path = PathFor(kind);
        lock (_gate) return _session.ContainsKey(kind) || File.Exists(path);
    }

    public override string ToString() => nameof(DpapiCredentialStore);

    /// <summary>Secondary entropy: SHA-256 of a fixed label, the current user's SID and the kind.</summary>
    internal static byte[] Entropy(string kind)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("No user SID for the current process.");
        return SHA256.HashData(Encoding.UTF8.GetBytes($"OptimizationTool.credential.v1|{sid}|{kind}"));
    }

    static string ValidKind(string kind)
    {
        if (kind is null || !KindPattern().IsMatch(kind)) throw new ArgumentException("Unknown provider kind.", nameof(kind));
        return kind;
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex KindPattern();
}

using OptimizationTool.Windows.Security.Collectors;

namespace OptimizationTool.Windows.Actions.Protection;

/// <summary>
/// The antivirus and firewall products Windows Security Center reports now (m9 §3.2 protected lists: "the products Windows
/// Security Center reports, plus a compiled vendor list"). Read through this seam so tests use a fake; an unreadable list
/// is empty, and the compiled lists still apply.
/// </summary>
public interface ISecurityProducts
{
    /// <summary>The registered products' display names (antivirus and firewall); empty when Security Center can't be read.</summary>
    IReadOnlyList<string> Names();
}

/// <summary>No live list (tests that don't exercise it, and callers without one).</summary>
public sealed class NoSecurityProducts : ISecurityProducts
{
    public static readonly NoSecurityProducts Instance = new();
    public IReadOnlyList<string> Names() => [];
}

/// <summary>
/// Real list through the win.security.wsc collector's own reader (IWSCProductList), cached for a minute so a preview and
/// its recheck don't each start COM. Read-only.
/// </summary>
public sealed class WindowsSecurityProducts : ISecurityProducts
{
    public static readonly WindowsSecurityProducts Shared = new();
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(1);
    private readonly object _gate = new();
    private IReadOnlyList<string>? _names;
    private DateTime _readAt;

    public IReadOnlyList<string> Names()
    {
        lock (_gate)
        {
            if (_names is not null && DateTime.UtcNow - _readAt < Fresh) return _names;
            var names = new List<string>();
            try
            {
                foreach (var provider in new[] { Security.Native.WSC_SECURITY_PROVIDER_ANTIVIRUS, Security.Native.WSC_SECURITY_PROVIDER_FIREWALL })
                {
                    var (products, _) = WscCollector.Products(provider);
                    foreach (var p in products ?? []) if (!string.IsNullOrWhiteSpace(p.Name)) names.Add(p.Name);
                }
            }
            catch (Exception) { /* Security Center unavailable: the compiled lists still apply */ }
            _names = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _readAt = DateTime.UtcNow;
            return _names;
        }
    }
}

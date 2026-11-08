using OptimizationTool.Engine;
using OptimizationTool.Windows.Security.Collectors;
using OptimizationTool.Windows.Security.Rules;

namespace OptimizationTool.Windows.Security;

/// <summary>Security domain: endpoint protection, firewall, listeners, persistence, UAC, Secure Boot (read-only).</summary>
public sealed class SecurityModule : IAuditModule
{
    public string Name => "security";

    public IEnumerable<ICollector> Collectors(CollectorOptions options) =>
    [
        new WscCollector(options),
        new DefenderCollector(options),
        new FirewallCollector(options),
        new ListenersCollector(options),
        new StartupCollector(options),
        new UacCollector(options),
        new SecureBootCollector(options),
    ];

    public IEnumerable<IRule> Rules() =>
    [
        new FwPublicOffRule(),
        new FwProfileOffRule(),
        new AvNoneActiveRule(),
        new AvRtpOffRule(),
        new AvSignaturesStaleRule(),
        new AvActiveThreatRule(),
        new UacDisabledRule(),
        new SecureBootOffRule(),
        new WildcardListenerPublicRule(),
        new StartupManyEntriesRule(),
    ];

    public IEnumerable<IInventoryStep> InventorySteps(CollectorOptions options) => [];
}

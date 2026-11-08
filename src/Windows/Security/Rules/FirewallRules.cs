using OptimizationTool.Engine;
using OptimizationTool.Windows.Security.Collectors;
using OptimizationTool.Windows.Security.Common;

namespace OptimizationTool.Windows.Security.Rules;

/// <summary>Shared firewall logic: a third-party firewall registered and on with Security Center takes over.</summary>
internal static class FirewallLogic
{
    public static bool ThirdPartyFirewallOn(RuleContext c) =>
        c.WithPrefix("security.fw.product.").Any(e =>
            J.Str(e.Value["state"]) == "on" && J.Bool(e.Value["isMicrosoft"]) == false);

    public static RuleResult Evaluate(RuleContext c, string profile, string ruleId, Func<EvidenceItem, (string Severity, string Text)?> decide)
    {
        if (!RuleKit.Usable(c, FirewallCollector.CollectorId)) return RuleKit.NotCovered(FirewallCollector.CollectorId);
        var e = c.Get($"firewall.profile.{profile}");
        if (e is null) return RuleResult.Insufficient($"No {profile} firewall profile evidence.");
        var enabled = J.Bool(e.Value["enabled"]);
        if (enabled is null) return RuleResult.Insufficient($"{profile} firewall state unknown.");
        if (enabled == true) return RuleResult.Clear();
        if (ThirdPartyFirewallOn(c)) return RuleResult.Clear(); // another firewall is registered and on
        if (decide(e) is not { } d) return RuleResult.Clear();

        var title = char.ToUpperInvariant(profile[0]) + profile[1..];
        var evidence = new List<string> { e.Id };
        if (c.Get("firewall.active_profiles") is { } active) evidence.Add(active.Id);
        var wscKnown = RuleKit.Usable(c, WscCollector.CollectorId);
        return RuleResult.Found(new FindingDraft(
            $"firewall.profile.{profile}", $"{title} firewall profile is off", d.Severity, wscKnown ? "high" : "medium", evidence,
            RuleKit.Card(d.Text, "windowsdefender://network",
                "Open Windows Security › Firewall & network protection.",
                $"Choose {title} network.",
                "Turn Microsoft Defender Firewall on.")));
    }
}

/// <summary>fw.public_off — the Public profile is off and no other firewall is registered as on.</summary>
internal sealed class FwPublicOffRule : IRule
{
    public string Id => "fw.public_off";
    public string Version => "1.0.0";
    public string Category => "security";
    public IReadOnlyList<string> Inputs { get; } = [FirewallCollector.CollectorId, WscCollector.CollectorId];

    public RuleResult Evaluate(RuleContext context) =>
        FirewallLogic.Evaluate(context, "public", Id, _ =>
            ("high", "Windows says its firewall for public networks is off. Would you like to review the setting?"));
}

/// <summary>fw.profile_off — the Private or Domain profile is off (medium for Private or an active Domain profile).</summary>
internal sealed class FwProfileOffRule : IRule
{
    public string Id => "fw.profile_off";
    public string Version => "1.0.0";
    public string Category => "security";
    public IReadOnlyList<string> Inputs { get; } = [FirewallCollector.CollectorId, WscCollector.CollectorId];

    public RuleResult Evaluate(RuleContext context)
    {
        var results = new[] { "private", "domain" }.Select(p => FirewallLogic.Evaluate(context, p, Id, e =>
        {
            var active = J.Bool(e.Value["active"]) == true;
            var severity = p == "private" || active ? "medium" : "low";
            return (severity, $"Windows says its firewall for {p} networks is off. Would you like to review the setting?");
        })).ToList();
        var findings = results.SelectMany(r => r.Findings).ToArray();
        if (findings.Length > 0) return RuleResult.Found(findings);
        return results.FirstOrDefault(r => r.Status == "insufficient") ?? RuleResult.Clear();
    }
}

using System.Globalization;
using OptimizationTool.Engine;
using OptimizationTool.Windows.Security.Collectors;
using OptimizationTool.Windows.Security.Common;

namespace OptimizationTool.Windows.Security.Rules;

/// <summary>uac.disabled — an explicit EnableLUA = 0 (high), or admin requests approved without asking (medium).</summary>
internal sealed class UacDisabledRule : IRule
{
    public string Id => "uac.disabled";
    public string Version => "1.0.0";
    public string Category => "security";
    public IReadOnlyList<string> Inputs { get; } = [UacCollector.CollectorId];

    public RuleResult Evaluate(RuleContext c)
    {
        if (!RuleKit.Usable(c, UacCollector.CollectorId)) return RuleKit.NotCovered(UacCollector.CollectorId);
        var e = c.Get("security.uac");
        if (e is null) return RuleResult.Insufficient("No UAC evidence.");
        var enabled = J.Bool(e.Value["enabled"]);
        if (enabled is null) return RuleResult.Insufficient("UAC setting has an unexpected type.");
        string[] guide =
        [
            "Open Start and search for “Change User Account Control settings”.",
            "Move the slider to the default level (second from top).",
            "Choose OK, then restart your PC.",
        ];
        if (enabled == false)
            return RuleResult.Found(new FindingDraft("security.uac.enable_lua", "User Account Control is off", "high", "high", [e.Id],
                RuleKit.Card("Windows says User Account Control is off, so apps can make changes without asking. Would you like to review it?", null, guide)));
        if (J.Long(e.Value["consentPromptBehaviorAdmin"]) == 0)
            return RuleResult.Found(new FindingDraft("security.uac.consent_admin", "Admin requests are approved without asking", "medium", "high", [e.Id],
                RuleKit.Card("Windows approves admin requests without asking you. Would you like to review the setting?", null, guide)));
        return RuleResult.Clear();
    }
}

/// <summary>secureboot.off — UEFI with Secure Boot off (low), or legacy BIOS boot (info).</summary>
internal sealed class SecureBootOffRule : IRule
{
    public string Id => "secureboot.off";
    public string Version => "1.0.0";
    public string Category => "security";
    public IReadOnlyList<string> Inputs { get; } = [SecureBootCollector.CollectorId];

    public RuleResult Evaluate(RuleContext c)
    {
        if (!RuleKit.Usable(c, SecureBootCollector.CollectorId)) return RuleKit.NotCovered(SecureBootCollector.CollectorId);
        var e = c.Get("security.secureboot");
        if (e is null) return RuleResult.Insufficient("No Secure Boot evidence.");
        var firmware = J.Str(e.Value["firmware"]);
        var enabled = J.Bool(e.Value["secureBootEnabled"]);
        if (firmware == "uefi" && enabled == true) return RuleResult.Clear();
        if (firmware == "uefi" && enabled == false)
            return RuleResult.Found(new FindingDraft("security.secureboot", "Secure Boot is off", "low", "high", [e.Id],
                RuleKit.Card("Secure Boot is off. It helps block harmful code that loads before Windows. Would you like to see how to check it?",
                    "ms-settings:windowsdefender",
                    "Open Windows Security › Device security.",
                    "Check what it says under Secure boot.",
                    "To turn it on, use your PC maker’s firmware (UEFI) settings.")));
        if (firmware == "bios")
            return RuleResult.Found(new FindingDraft("security.secureboot", "PC starts in legacy BIOS mode", "info", "high", [e.Id],
                RuleKit.Card("This PC starts in legacy BIOS mode, so Secure Boot can’t protect startup. Would you like to learn what this means?",
                    "ms-settings:windowsdefender",
                    "Open Windows Security › Device security.",
                    "Check whether Secure boot is listed.",
                    "Ask your PC maker before changing firmware boot mode.")));
        return RuleResult.Insufficient("Secure Boot state unknown.");
    }
}

/// <summary>
/// net.wildcard_listener_public — a non-system app accepts TCP connections on all networks while the Public
/// profile is active. Review item only (info): a local bind says nothing about reachability or intent.
/// </summary>
internal sealed class WildcardListenerPublicRule : IRule
{
    private const int FindingCap = 5;
    public string Id => "net.wildcard_listener_public";
    public string Version => "1.0.0";
    public string Category => "security";
    public IReadOnlyList<string> Inputs { get; } = [ListenersCollector.CollectorId, FirewallCollector.CollectorId];

    public RuleResult Evaluate(RuleContext c)
    {
        if (!RuleKit.Usable(c, ListenersCollector.CollectorId)) return RuleKit.NotCovered(ListenersCollector.CollectorId);
        if (!RuleKit.Usable(c, FirewallCollector.CollectorId)) return RuleKit.NotCovered(FirewallCollector.CollectorId);
        var active = c.Get("firewall.active_profiles");
        if (active is null) return RuleResult.Insufficient("Active network profile unknown.");
        if (J.Bool(active.Value["public"]) != true) return RuleResult.Inapplicable("The Public network profile is not active.");
        if (c.Get("net.listeners.summary") is null) return RuleResult.Insufficient("No listener evidence.");

        var groups = c.WithPrefix("net.listener.tcp.")
            .Where(e => J.Str(e.Value["scope"]) == "wildcard" && J.Bool(e.Value["isSystem"]) == false && J.Str(e.Value["image"]) is { Length: > 0 })
            .GroupBy(e => J.Str(e.Value["image"])!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (groups.Count == 0) return RuleResult.Clear();

        var findings = groups.Take(FindingCap).Select(g =>
        {
            var app = PathRoots.FileName(g.Key);
            var ports = g.Select(e => J.Long(e.Value["port"]) ?? 0).Distinct().OrderBy(p => p).ToList();
            var portText = ports.Count == 1 ? $"port {ports[0].ToString(CultureInfo.InvariantCulture)}" : $"{ports.Count} ports";
            var evidence = g.Select(e => e.Id).Append(active.Id).ToList();
            var purpose = ListenerPurpose.Likely(c.Profile, ports);
            var text = purpose is null
                ? $"{app} accepts connections on {portText} from other devices while you’re on a public network. Would you like to review it?"
                : $"{app} accepts connections on all networks, likely for {purpose}. Would you like to review it?";
            return new FindingDraft("net.listener.app." + Subjects.Hash8(g.Key), $"{app} accepts connections on all networks", "info", "medium", evidence,
                RuleKit.Card(text,
                    "windowsdefender://network",
                    "Open Windows Security › Firewall & network protection.",
                    "Choose Allow an app through firewall.",
                    $"Check whether {app} is allowed on Public networks."));
        }).ToArray();
        return RuleResult.Found(findings);
    }
}

/// <summary>startup.many_entries — more than 10 apps start at sign-in. Info only; never claims an app is unused.</summary>
internal sealed class StartupManyEntriesRule : IRule
{
    public const int Threshold = 10;
    public string Id => "startup.many_entries";
    public string Version => "1.0.0";
    public string Category => "performance";
    public IReadOnlyList<string> Inputs { get; } = [StartupCollector.CollectorId];

    public RuleResult Evaluate(RuleContext c)
    {
        if (!RuleKit.Usable(c, StartupCollector.CollectorId)) return RuleKit.NotCovered(StartupCollector.CollectorId);
        var s = c.Get("startup.summary");
        if (s is null) return RuleResult.Insufficient("No startup summary.");
        var count = J.Long(s.Value["signInEnabledCount"]);
        if (count is null) return RuleResult.Insufficient("Startup count unknown.");
        if (count <= Threshold) return RuleResult.Clear();
        return RuleResult.Found(new FindingDraft("startup.sign_in", "Many apps start at sign-in", "info", "high", [s.Id],
            RuleKit.Card($"{count.Value.ToString(CultureInfo.InvariantCulture)} apps start when you sign in. Would you like to review them?",
                "ms-settings:startupapps",
                "Open Settings › Apps › Startup.",
                "Turn off apps you don’t need right after sign-in.")));
    }
}

/// <summary>
/// Profile-aware wording for net.wildcard_listener_public (protocol-v1 §10 "Profile"): a listener whose ports are
/// all typical for the user's stated use may be on purpose. Severity is unchanged (info); only the card says so.
/// </summary>
internal static class ListenerPurpose
{
    private static readonly (long Lo, long Hi)[] GamePorts = [(27000, 27100), (7777, 7779), (25565, 25565), (3074, 3074), (3478, 3480)];

    /// <summary>Same matching as the engine's onboarding starters: case-insensitive substring over the joined labels.</summary>
    private static bool HasWord(IEnumerable<string> list, string word) => string.Join(' ', list).Contains(word, StringComparison.OrdinalIgnoreCase);

    /// <summary>"games" | "development" | null. Every port must fit, so a game client that also exposes 3389 stays unchanged.</summary>
    public static string? Likely(Profile profile, IReadOnlyList<long> ports)
    {
        if (ports.Count == 0) return null;
        if (HasWord(profile.Uses, "gam") && ports.All(p => GamePorts.Any(r => p >= r.Lo && p <= r.Hi)))
            return "games";
        if ((HasWord(profile.Uses, "hosting") || HasWord(profile.Uses, "development"))
            && ports.All(p => OptimizationTool.Engine.Agent.Exposure.Tags("tcp", p).Contains("dev_server")))
            return "development";
        return null;
    }
}
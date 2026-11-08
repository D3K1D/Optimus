namespace OptimizationTool.Engine.Agent;

/// <summary>
/// Titles for findings that leave the PC. Rule titles and card text can contain app, process or device names
/// ("Resolve.exe keeps crashing"), so with share.names off a finding is sent with a fixed generic phrase for its
/// rule; unmapped rules get "&lt;category label&gt; finding". With names on, the rule's title is sanitized and the
/// result stays marked untrusted. Card text is never shared.
/// </summary>
public static class FindingTitles
{
    /// <summary>Generic phrases by rule ID (src/Windows/**/Rules and the M1 fixture rules).</summary>
    public static readonly IReadOnlyDictionary<string, string> Generic = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Security
        ["av.active_threat"] = "A threat needs action",
        ["av.none_active"] = "No active antivirus",
        ["av.rtp_off"] = "Real-time protection is off",
        ["av.signatures_stale"] = "Antivirus definitions are out of date",
        ["fw.public_off"] = "The public network firewall is off",
        ["fw.profile_off"] = "A firewall profile is off",
        ["net.wildcard_listener_public"] = "An app accepts connections on all networks",
        ["secureboot.off"] = "Secure Boot is off",
        ["uac.disabled"] = "User Account Control is off",
        ["startup.many_entries"] = "Many apps start at sign-in",
        ["startup.many_unused"] = "Many apps start at sign-in",
        // System and performance
        ["power.high_perf_on_battery"] = "High performance power plan on battery",
        ["storage.system_low"] = "The system drive is low on space",
        ["storage.volume_low"] = "A drive is low on space",
        ["perf.sustained_cpu"] = "A background process kept the CPU busy",
        ["perf.memory_pressure"] = "Memory is under pressure",
        ["perf.uptime_long"] = "The PC has not restarted in a long time",
        // Reliability, apps and settings
        ["reliability.kernel_power"] = "Unexpected restarts",
        ["reliability.app_crashes"] = "An app keeps crashing",
        ["reliability.update_failures"] = "Windows updates are failing",
        ["devices.problem_code"] = "A device reports a problem",
        ["apps.large_installs"] = "Some apps use a lot of space",
        ["privacy.optional_diag"] = "Optional diagnostic data is on",
        ["display.below_max_refresh"] = "A display runs below its maximum refresh rate",
        ["start.suggestions_on"] = "Start suggestions are on",
    };

    public static bool IsMapped(string ruleId) => Generic.ContainsKey(ruleId);

    public const int MaxTitle = 120;

    /// <summary>The title to share for a finding under the given names setting.</summary>
    public static string For(string ruleId, string category, string ruleTitle, bool shareNames) =>
        (shareNames ? FromRule(ruleTitle) : null) ?? GenericFor(ruleId, category);

    /// <summary>
    /// The rule title as it may leave with share.names on: run through the same sanitizer as shared names (no path
    /// separators or markup; null when it carries a path, address, email, credential or identifier-like token).
    /// </summary>
    public static string? FromRule(string ruleTitle) => Sanitize.Phrase(ruleTitle, MaxTitle);

    public static string GenericFor(string ruleId, string category) =>
        Generic.TryGetValue(ruleId, out var phrase) ? phrase : $"{Fixture.Default.CategoryLabel(category)} finding";
}

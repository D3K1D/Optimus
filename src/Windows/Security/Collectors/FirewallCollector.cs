using System.Text.Json.Nodes;
using Microsoft.Win32;
using OptimizationTool.Engine;
using OptimizationTool.Windows.Security.Common;

namespace OptimizationTool.Windows.Security.Collectors;

internal sealed record FirewallProfileRaw(bool? Enabled, int? DefaultInbound, bool? BlockAllInbound, int? PolicyEnableFirewall, string? Error);

internal sealed record FirewallRaw(int? CurrentProfileTypes, IReadOnlyDictionary<string, FirewallProfileRaw> Profiles, string? Error);

/// <summary>
/// win.security.firewall — Windows Firewall profile settings from the <c>HNetCfg.FwPolicy2</c> automation object
/// (getters only), the currently active network categories, and whether Group Policy sets the profile state
/// (<c>HKLM\SOFTWARE\Policies\Microsoft\WindowsFirewall</c>), in which case the policy value is the effective one.
/// </summary>
internal sealed class FirewallCollector(CollectorOptions options) : SecurityCollector<FirewallRaw>(options)
{
    public const string CollectorId = "win.security.firewall";
    internal static readonly (string Name, int Type, string PolicyKey)[] ProfileTypes =
    [
        ("domain", 1, "DomainProfile"), ("private", 2, "PrivateProfile"), ("public", 4, "PublicProfile"),
    ];

    public override string Id => CollectorId;
    public override string GapTitle => "Firewall";
    public override string GapSource => "Windows Firewall";
    public override string GapDisplay => "Windows Firewall settings could not be read.";

    internal override FirewallRaw Read(CancellationToken ct)
    {
        object? policy = null;
        try
        {
            policy = Automation.Create("HNetCfg.FwPolicy2");
            int? current = null;
            string? error = null;
            try { current = Convert.ToInt32(Automation.Get(policy, "CurrentProfileTypes"), System.Globalization.CultureInfo.InvariantCulture); }
            catch (Exception ex) when (ex is not OperationCanceledException) { error = Automation.Hex(Automation.HResult(ex)); }

            var profiles = new Dictionary<string, FirewallProfileRaw>();
            foreach (var (name, type, key) in ProfileTypes)
            {
                ct.ThrowIfCancellationRequested();
                int? gp = ReadPolicy(key);
                try
                {
                    var enabled = Automation.Get(policy, "FirewallEnabled", type) is bool e ? e : (bool?)null;
                    var inbound = Automation.Get(policy, "DefaultInboundAction", type) is { } a ? Convert.ToInt32(a, System.Globalization.CultureInfo.InvariantCulture) : (int?)null;
                    var blockAll = Automation.Get(policy, "BlockAllInboundTraffic", type) is bool b ? b : (bool?)null;
                    profiles[name] = new FirewallProfileRaw(enabled, inbound, blockAll, gp, null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    profiles[name] = new FirewallProfileRaw(null, null, null, gp, Automation.Hex(Automation.HResult(ex)));
                }
            }
            return new FirewallRaw(current, profiles, error);
        }
        finally { Automation.Release(policy); }
    }

    private static int? ReadPolicy(string profileKey)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Policies\Microsoft\WindowsFirewall\" + profileKey, writable: false);
            return key?.GetValue("EnableFirewall") is int v ? v : null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    internal override CollectorResult Normalize(FirewallRaw raw)
    {
        var facts = new FactSet("Windows Firewall effective profiles");
        var problems = new List<string>();
        if (raw.CurrentProfileTypes is { } mask)
        {
            facts.Add("firewall.active_profiles", new JsonObject
            {
                ["domain"] = (mask & 1) != 0,
                ["private"] = (mask & 2) != 0,
                ["public"] = (mask & 4) != 0,
            }, ActiveDisplay(mask));
        }
        else problems.Add($"Active network categories unavailable ({raw.Error ?? "unknown"}).");

        foreach (var (name, type, _) in ProfileTypes)
        {
            if (!raw.Profiles.TryGetValue(name, out var p) || p.Error is not null)
            {
                problems.Add($"{Title(name)} profile unreadable ({p?.Error ?? "missing"}).");
                continue;
            }
            var managed = p.PolicyEnableFirewall is not null;
            var effective = managed ? p.PolicyEnableFirewall != 0 : p.Enabled;
            bool? active = raw.CurrentProfileTypes is { } m ? (m & type) != 0 : null;
            facts.Add($"firewall.profile.{name}", new JsonObject
            {
                ["profile"] = Title(name),
                ["enabled"] = effective,
                ["localEnabled"] = p.Enabled,
                ["active"] = active,
                ["defaultInbound"] = p.DefaultInbound switch { 0 => "block", 1 => "allow", _ => null },
                ["blockAllInbound"] = p.BlockAllInbound,
                ["managedByPolicy"] = managed,
                ["policyOwner"] = managed ? "group_policy" : "local",
            }, $"Windows reports that the {Title(name)} firewall profile is {(effective == true ? "on" : effective == false ? "off" : "unknown")}"
               + (active == true ? " and in use now." : ".") + (managed ? " It is set by policy." : ""));
        }

        var status = problems.Count == 0 ? "complete" : facts.Count == 0 ? "failed" : "partial";
        return facts.Result(status, problems.Count == 0 ? null : string.Join(" ", problems));
    }

    private static string Title(string name) => char.ToUpperInvariant(name[0]) + name[1..];

    private static string ActiveDisplay(int mask)
    {
        var names = ProfileTypes.Where(p => (mask & p.Type) != 0).Select(p => Title(p.Name)).ToList();
        return names.Count == 0 ? "No network profile is active." : $"Active network profile: {string.Join(", ", names)}.";
    }
}

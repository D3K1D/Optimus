using System.Runtime.InteropServices;
using Microsoft.Win32;
using OptimizationTool.Engine;
using OptimizationTool.Windows.Security.Collectors;
using OptimizationTool.Windows.Security.Common;

namespace OptimizationTool.Windows.Actions.Firewall;

/// <summary>
/// Everything the Public-firewall handler's eligibility depends on, read from live state. Nullable fields are
/// "could not tell"; the plan treats every unknown as ineligible (offer guidance, never guess).
/// </summary>
public sealed record PublicFirewallSnapshot(
    bool? LocalEnabled,           // local persistent store FirewallPolicy\PublicProfile\EnableFirewall (null = no explicit value)
    bool? EffectiveEnabled,       // INetFwPolicy2.FirewallEnabled[Public]
    bool? PublicActive,           // INetFwPolicy2.CurrentProfileTypes has Public
    bool PolicyConfigured,        // Group Policy sets EnableFirewall for Public
    bool MdmConfigured,           // MDM firewall store sets EnableFirewall for Public
    bool? OtherFirewallOn,        // a non-Microsoft firewall is registered with Security Center and on (null = WSC unreadable)
    bool ConsoleSession,          // this process runs in the active console session
    bool RemoteSession,           // SM_REMOTESESSION
    bool Managed,                 // domain-joined or MDM-enrolled
    string? ReadError = null,
    IReadOnlyList<ListeningProgram>? Listening = null,   // null = could not read listeners (ambiguous)
    IReadOnlyList<InboundAllowRule>? InboundRules = null); // enabled inbound Allow rules that apply to Public; null = unreadable

/// <summary>A process accepting traffic on a non-loopback address. ImagePath is a full local path (never evidence).</summary>
public sealed record ListeningProgram(string? ImagePath, string Protocol, int Port, bool IsSystem, IReadOnlyList<string> Services);

/// <summary>An enabled inbound Allow rule whose profiles include Public. Protocol: tcp | udp | any | other.</summary>
public sealed record InboundAllowRule(string Name, string? ApplicationPath, string? ServiceName, string? LocalPorts, string Protocol, bool BuiltIn,
    bool PackageScoped = false); // scoped to an app package (AppContainer SID); not attributed to a listener

/// <summary>Read-only access to the Public firewall state (fakes in tests).</summary>
public interface IPublicFirewallReader
{
    PublicFirewallSnapshot Read(CancellationToken ct);
}

/// <summary>
/// Live reader. Local store: <c>HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\PublicProfile</c>;
/// effective state and active profiles: <c>HNetCfg.FwPolicy2</c> getters; policy: the GP and MDM firewall stores;
/// provider: Security Center firewall products; session: <c>WTSGetActiveConsoleSessionId</c> and <c>SM_REMOTESESSION</c>;
/// managed: domain join (<c>NetGetJoinInformation</c>) or an MDM enrollment. Nothing is written.
/// </summary>
public sealed class WindowsPublicFirewallReader : IPublicFirewallReader
{
    private const string LocalStore = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\PublicProfile";
    private const string MdmStore = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\Mdm\PublicProfile";
    private const string GpStore = @"SOFTWARE\Policies\Microsoft\WindowsFirewall\PublicProfile";
    public const int NET_FW_PROFILE2_PUBLIC = 4;

    public PublicFirewallSnapshot Read(CancellationToken ct)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            bool? local = Dword(hklm, LocalStore, "EnableFirewall") is { } l ? l != 0 : null;
            var gp = Dword(hklm, GpStore, "EnableFirewall") is not null;
            var mdm = Dword(hklm, MdmStore, "EnableFirewall") is not null;
            ct.ThrowIfCancellationRequested();

            bool? effective = null, active = null;
            object? policy = null;
            try
            {
                policy = Automation.Create("HNetCfg.FwPolicy2");
                effective = Automation.Get(policy, "FirewallEnabled", NET_FW_PROFILE2_PUBLIC) is bool e ? e : null;
                active = Automation.Get(policy, "CurrentProfileTypes") is int mask ? (mask & NET_FW_PROFILE2_PUBLIC) != 0 : null;
            }
            finally { Automation.Release(policy); }
            ct.ThrowIfCancellationRequested();

            var wsc = new WscCollector(new CollectorOptions()).Read(ct);
            bool? other = wsc.FwProducts is null ? null
                : wsc.FwProducts.Any(p => p.State == 0 && !WscCollector.IsMicrosoft(Text.Clean(p.Name)));

            var session = CurrentSession();
            var console = session is { } s && s == WTSGetActiveConsoleSessionId();
            var remote = GetSystemMetrics(SM_REMOTESESSION) != 0;
            ct.ThrowIfCancellationRequested();
            var listening = InboundReader.Listening(ct);
            var rules = InboundReader.PublicAllowRules(ct);
            return new PublicFirewallSnapshot(local, effective, active, gp, mdm, other, console, remote, IsManaged(hklm), null, listening, rules);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new PublicFirewallSnapshot(null, null, null, false, false, null, false, false, false, ex.GetType().Name);
        }
    }

    private static int? Dword(RegistryKey root, string path, string name)
    {
        using var key = root.OpenSubKey(path, writable: false);
        return key?.GetValue(name) is int v ? v : null;
    }

    private static uint? CurrentSession() =>
        ProcessIdToSessionId((uint)Environment.ProcessId, out var s) ? s : null;

    /// <summary>Domain join, or an MDM enrollment (an enrollment with a management server, not the local built-ins).</summary>
    private static bool IsManaged(RegistryKey hklm)
    {
        if (NetGetJoinInformation(null, out var name, out var status) == 0)
        {
            NetApiBufferFree(name);
            if (status == 3) return true; // NetSetupDomainName
        }
        using var enrollments = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Enrollments", writable: false);
        if (enrollments is null) return false;
        foreach (var sub in enrollments.GetSubKeyNames().Take(200))
        {
            using var e = enrollments.OpenSubKey(sub, writable: false);
            if (e is null || e.GetValue("EnrollmentState") is not int state || state != 1) continue;
            var type = e.GetValue("EnrollmentType") as int?;
            if (type is 6 or 13) return true; // MDM full / Azure AD joined MDM
            if (e.GetValue("DiscoveryServiceFullURL") is string url && url.Length > 0) return true;
            if (e.GetValue("ProviderID") is string p && p.Equals("MS DM Server", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private const int SM_REMOTESESSION = 0x1000;

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint pid, out uint session);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetGetJoinInformation(string? server, out IntPtr name, out int status);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}

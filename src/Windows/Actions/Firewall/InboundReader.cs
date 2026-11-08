using System.Globalization;
using System.Runtime.InteropServices;
using OptimizationTool.Engine;
using OptimizationTool.Windows.Security.Collectors;
using OptimizationTool.Windows.Security.Common;

namespace OptimizationTool.Windows.Actions.Firewall;

/// <summary>
/// Read-only inbound-dependence inputs: non-loopback listeners (the M2 IP Helper listener reader, with the services
/// hosted in each PID) and the enabled inbound Allow rules that apply to Public (<c>INetFwPolicy2.Rules</c>, getters
/// only). Either returns null when it can't be read, which the plan treats as ambiguous.
/// </summary>
internal static class InboundReader
{
    private const int RuleCap = 5000;

    public static IReadOnlyList<ListeningProgram>? Listening(CancellationToken ct)
    {
        try
        {
            var raw = new ListenersCollector(new CollectorOptions()).Read(ct);
            if (raw.TableErrors.Count == 4) return null;
            return FromRaw(raw, ServicesByPid(), Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
    }

    /// <summary>Pure: listener rows → programs on non-loopback addresses (TCP all; UDP only non-system).</summary>
    internal static IReadOnlyList<ListeningProgram> FromRaw(ListenersRaw raw, IReadOnlyDictionary<uint, IReadOnlyList<string>> services, string windowsDir)
    {
        {
            var windows = windowsDir.TrimEnd('\\') + "\\";
            var result = new Dictionary<string, ListeningProgram>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in raw.Rows)
            {
                if (ListenersCollector.Scope(row.Family, row.Address) == "loopback") continue;
                raw.Processes.TryGetValue(row.Pid, out var proc);
                var image = proc is { ImagePath: { } path } && !(proc.StartUtc is { } st && st > raw.SnapshotUtc) ? path : null;
                var system = row.Pid == 4 || (image?.StartsWith(windows, StringComparison.OrdinalIgnoreCase) ?? false);
                if (row.Protocol == "udp" && system) continue; // UDP: only non-system endpoints count
                var key = string.Join('|', image ?? "pid:" + row.Pid.ToString(CultureInfo.InvariantCulture), row.Protocol, row.Port.ToString(CultureInfo.InvariantCulture));
                result.TryAdd(key, new ListeningProgram(image, row.Protocol, row.Port, system,
                    services.TryGetValue(row.Pid, out var s) ? s : []));
            }
            return result.Values.ToList();
        }
    }

    public static IReadOnlyList<InboundAllowRule>? PublicAllowRules(CancellationToken ct)
    {
        object? policy = null, rules = null;
        try
        {
            policy = Automation.Create("HNetCfg.FwPolicy2");
            rules = Automation.Get(policy, "Rules")!;
            var roots = PathRoots.FromEnvironment();
            var packaged = PackageScopedNames();
            var list = new List<InboundAllowRule>();
            foreach (var rule in Automation.Items(rules, RuleCap))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (Automation.Get(rule, "Enabled") is not true) continue;
                    if (Int(Automation.Get(rule, "Direction")) != 1) continue;   // NET_FW_RULE_DIR_IN
                    if (Int(Automation.Get(rule, "Action")) != 1) continue;      // NET_FW_ACTION_ALLOW
                    if ((Int(Automation.Get(rule, "Profiles")) & WindowsPublicFirewallReader.NET_FW_PROFILE2_PUBLIC) == 0) continue;
                    var protocol = Int(Automation.Get(rule, "Protocol")) switch { 6 => "tcp", 17 => "udp", 256 => "any", _ => "other" };
                    var grouping = Automation.Get(rule, "Grouping") as string;
                    list.Add(new InboundAllowRule(
                        Text.Clean(Automation.Get(rule, "Name") as string, 120),
                        Automation.Get(rule, "ApplicationName") is string app && app.Length > 0 ? roots.ExpandKnown(app) : null,
                        Automation.Get(rule, "ServiceName") as string,
                        Automation.Get(rule, "LocalPorts") as string,
                        protocol,
                        BuiltIn: grouping is not null && grouping.StartsWith('@'),
                        PackageScoped: (Optional(rule, "LocalAppPackageId") is string pkg && pkg.Length > 0)
                            || (Automation.Get(rule, "ApplicationName") is not string { Length: > 0 }
                                && packaged.Contains(Automation.Get(rule, "Name") as string ?? ""))));
                }
                finally { Automation.Release(rule); }
            }
            return list;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
        finally
        {
            Automation.Release(rules);
            Automation.Release(policy);
        }
    }

    /// <summary>
    /// INetFwRule does not expose a package scope (PFN / AppPkgId) for many Store-app rules, so read the names of
    /// inbound allow rules scoped that way from the local rule store (read-only) and treat app-less COM rules with
    /// those names as package-scoped.
    /// </summary>
    private static HashSet<string> PackageScopedNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules", writable: false);
            if (key is null) return names;
            foreach (var value in key.GetValueNames().Take(RuleCap))
            {
                if (key.GetValue(value) is not string s || s.Length > 8192) continue;
                var fields = s.Split('|');
                if (!fields.Contains("Dir=In") || !fields.Contains("Action=Allow")) continue;
                if (!fields.Any(f => f.StartsWith("PFN=", StringComparison.Ordinal) || f.StartsWith("AppPkgId=", StringComparison.Ordinal))) continue;
                if (fields.Any(f => f.StartsWith("App=", StringComparison.Ordinal))) continue;
                if (fields.FirstOrDefault(f => f.StartsWith("Name=", StringComparison.Ordinal)) is { } n) names.Add(n[5..]);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        return names;
    }

    /// <summary>INetFwRule3 properties are absent on older rule objects; treat a failure as "not set".</summary>
    private static object? Optional(object rule, string name)
    {
        try { return Automation.Get(rule, name); }
        catch (Exception ex) when (ex is System.Reflection.TargetInvocationException or COMException or MissingMethodException) { return null; }
    }

    private static int Int(object? o) => o is null ? 0 : Convert.ToInt32(o, CultureInfo.InvariantCulture);

    /// <summary>PID → service names of running Win32 services (EnumServicesStatusEx; works as a standard user).</summary>
    private static Dictionary<uint, IReadOnlyList<string>> ServicesByPid()
    {
        var map = new Dictionary<uint, List<string>>();
        var scm = OpenSCManagerW(null, null, 0x0004 /* SC_MANAGER_ENUMERATE_SERVICE */);
        if (scm == IntPtr.Zero) return [];
        try
        {
            uint resume = 0;
            EnumServicesStatusExW(scm, 0, 0x30, 1, IntPtr.Zero, 0, out var needed, out _, ref resume, null);
            if (needed == 0) return [];
            var buf = Marshal.AllocHGlobal((int)needed + 4096);
            try
            {
                resume = 0;
                if (!EnumServicesStatusExW(scm, 0 /* SC_ENUM_PROCESS_INFO */, 0x30 /* SERVICE_WIN32 */, 1 /* SERVICE_ACTIVE */,
                        buf, needed + 4096, out _, out var count, ref resume, null)) return [];
                // ENUM_SERVICE_STATUS_PROCESSW (x64): lpServiceName@0, lpDisplayName@8, SERVICE_STATUS_PROCESS@16 (dwProcessId at +28) → 56 bytes
                for (var i = 0; i < count; i++)
                {
                    var entry = buf + i * 56;
                    var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(entry, 0));
                    var pid = (uint)Marshal.ReadInt32(entry, 16 + 28);
                    if (name is null || pid == 0) continue;
                    if (!map.TryGetValue(pid, out var l)) map[pid] = l = [];
                    l.Add(name);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { CloseServiceHandle(scm); }
        return map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumServicesStatusExW(IntPtr scm, int infoLevel, uint serviceType, uint state, IntPtr buffer, uint bufSize,
        out uint bytesNeeded, out uint count, ref uint resume, string? group);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}

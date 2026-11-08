using OptimizationTool.Engine;
using OptimizationTool.Windows.Security.Collectors;
using OptimizationTool.Windows.Security.Common;

namespace OptimizationTool.Windows.Tests.Security;

/// <summary>Normalize steps with synthetic DTOs (all values invented).</summary>
public sealed class SecurityNormalizeTests
{
    private static readonly CollectorOptions Options = new();

    internal static readonly PathRoots Roots = new(
    [
        ("SystemRoot", @"Q:\Win"), ("ProgramFiles", @"Q:\Apps"), ("ProgramFiles(x86)", @"Q:\Apps86"),
        ("ProgramData", @"Q:\Data"), ("LOCALAPPDATA", @"Q:\Users\pat\AppData\Local"),
        ("APPDATA", @"Q:\Users\pat\AppData\Roaming"), ("USERPROFILE", @"Q:\Users\pat"),
    ]);

    private static void AssertSorted(CollectorResult r)
    {
        Assert.Equal(r.Facts.Select(f => f.Subject).Order(StringComparer.Ordinal), r.Facts.Select(f => f.Subject));
        Assert.Equal(r.Facts.Count, r.Coverage.Observations);
    }

    // ---------------- paths and launch strings

    [Theory]
    [InlineData(@"Q:\Apps\Tool\tool.exe", @"%ProgramFiles%\Tool\tool.exe")]
    [InlineData(@"Q:\Users\pat\AppData\Local\Thing\run.exe", @"%LOCALAPPDATA%\Thing\run.exe")]
    [InlineData(@"Q:\Users\pat\Desktop\x.exe", @"%USERPROFILE%\Desktop\x.exe")]
    [InlineData(@"\\?\Q:\Win\System32\svc.exe", @"%SystemRoot%\System32\svc.exe")]
    [InlineData(@"R:\Private\pat-stuff\app.exe", "\u2026\\app.exe")]
    public void Normalize_paths(string input, string expected) => Assert.Equal(expected, Roots.Normalize(input));

    [Fact]
    public void Launch_string_quoted_with_arguments_keeps_no_arguments()
    {
        var p = LaunchString.Parse("\"Q:\\Apps\\My App\\app.exe\" --token=secret", Roots, _ => false, @"Q:\Win\System32");
        Assert.Equal(@"Q:\Apps\My App\app.exe", p.Image);
        Assert.True(p.HasArguments);
        Assert.Null(p.Payload);
    }

    [Fact]
    public void Launch_string_unquoted_with_spaces_resolves_by_existing_file()
    {
        var exists = (string s) => s == @"Q:\Apps\My App\app.exe";
        var p = LaunchString.Parse(@"Q:\Apps\My App\app.exe -min", Roots, exists, @"Q:\Win\System32");
        Assert.Equal(@"Q:\Apps\My App\app.exe", p.Image);
        Assert.True(p.HasArguments);
    }

    [Fact]
    public void Launch_string_expands_only_whitelisted_tokens()
    {
        var p = LaunchString.Parse(@"%ProgramFiles%\A\a.exe", Roots, _ => false, null);
        Assert.Equal(@"Q:\Apps\A\a.exe", p.Image);
        var q = LaunchString.Parse(@"%EVIL%\b.exe", Roots, _ => false, null);
        Assert.Equal(@"%EVIL%\b.exe", q.Image);
    }

    [Fact]
    public void Launch_string_rundll32_payload_is_separate()
    {
        var exists = (string s) => s == @"Q:\Win\System32\rundll32.exe";
        var p = LaunchString.Parse("rundll32.exe \"Q:\\Users\\pat\\AppData\\Local\\x\\y.dll\",Start", Roots, exists, @"Q:\Win\System32");
        Assert.Equal(@"Q:\Win\System32\rundll32.exe", p.Image);
        Assert.Equal(@"Q:\Users\pat\AppData\Local\x\y.dll", p.Payload);
        Assert.True(p.HasArguments);
    }

    [Fact]
    public void Text_clean_strips_control_characters_and_caps()
    {
        Assert.Equal("a b c", Text.Clean("a\u202Eb\nc"));
        Assert.Equal(10, Text.Clean(new string('x', 50), 10).Length);
        Assert.Equal("Task <SID> for <user>", Text.Redact("Task S-1-5-21-111-222-333-1001 for pat", "pat"));
    }

    // ---------------- WSC

    [Fact]
    public void Wsc_products_and_health()
    {
        var raw = new WscRaw(0, 0, 0, 0,
            [new("Contoso AV", 0, 1, true, "{11111111-0000-0000-0000-000000000001}"), new("Windows Defender", 1, 1, false, "{d68ddc3a-831f-4fae-9e44-da132c1acf46}")], null,
            [new("Windows Firewall", 0, 0, true, null)], null);
        var r = new WscCollector(Options).Normalize(raw);
        Assert.Equal("complete", r.Coverage.Status);
        AssertSorted(r);
        var av = r.Facts.Where(f => f.Subject.StartsWith("security.av.product.", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, av.Count);
        Assert.Contains(av, f => J.Str(f.Value["role"]) == "primary" && J.Str(f.Value["name"]) == "Contoso AV");
        Assert.Contains(av, f => J.Bool(f.Value["isMicrosoft"]) == true && J.Str(f.Value["state"]) == "off");
    }

    [Fact]
    public void Wsc_s_false_is_not_usable_health()
    {
        var r = new WscCollector(Options).Normalize(new WscRaw(1, 2, 0, 0, [], null, [], null));
        Assert.Equal("partial", r.Coverage.Status);
        var h = r.Facts.Single(f => f.Subject == "security.wsc.health.antivirus");
        Assert.False(J.Bool(h.Value["usable"]));
        Assert.Equal("unavailable", J.Str(h.Value["health"]));
    }

    // ---------------- Defender

    private static DefenderStatusRaw Status(string mode = "Normal", bool rtp = true) =>
        new(mode, true, true, rtp, "1.400.1.0", new DateTime(2026, 6, 14, 0, 0, 0, DateTimeKind.Utc), 1, null, null, true);

    [Fact]
    public void Defender_standard_user_empty_threats_is_partial_not_clear()
    {
        var r = new DefenderCollector(Options).Normalize(new DefenderRaw(Status(), null, [], [], null, Elevated: false, Truncated: false));
        Assert.Equal("partial", r.Coverage.Status);
        Assert.False(J.Bool(r.Facts.Single(f => f.Subject == "security.defender.threats").Value["readable"]));
        Assert.Equal("primary", J.Str(r.Facts.Single(f => f.Subject == "security.defender.status").Value["mode"]));
    }

    [Fact]
    public void Defender_detection_vs_disposition()
    {
        ThreatDetectionRaw[] det =
        [
            new(10, 1, 0, new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc), null),   // detected → action needed
            new(11, 3, 0, new DateTime(2026, 6, 9, 0, 0, 0, DateTimeKind.Utc), null),    // quarantined → history
            new(12, 5, 0, null, null),                                                  // allowed by user → history
        ];
        ThreatRaw[] thr = [new(10, "Trojan:Win32/Example.A", 8, 5, false), new(11, "PUA:Win32/Sample", 27, 1, false), new(12, "PUA:Win32/Kept", 27, 1, false)];
        var r = new DefenderCollector(Options).Normalize(new DefenderRaw(Status(), null, det, thr, null, Elevated: true, Truncated: false));
        Assert.Equal("complete", r.Coverage.Status);
        AssertSorted(r);
        var open = r.Facts.Where(f => f.Subject.StartsWith("security.defender.threat.", StringComparison.Ordinal)).ToList();
        Assert.Single(open);
        Assert.Equal("malware", J.Str(open[0].Value["classification"]));
        var sum = r.Facts.Single(f => f.Subject == "security.defender.threats");
        Assert.Equal(1, J.Long(sum.Value["actionNeededCount"]));
        Assert.Equal(2, J.Long(sum.Value["historyCount"]));
    }

    [Theory]
    [InlineData("Normal", "primary")]
    [InlineData("Passive", "passive")]
    [InlineData("EDR Block Mode", "passive")]
    [InlineData("SxS Passive Mode", "side_by_side")]
    [InlineData("Not running", "disabled")]
    public void Defender_modes(string running, string expected) => Assert.Equal(expected, DefenderCollector.Mode(Status(running)));

    [Fact]
    public void Defender_missing_namespace_is_unsupported()
    {
        var r = new DefenderCollector(Options).Normalize(new DefenderRaw(null, "0x8004100E", null, null, null, false, false));
        Assert.Equal("unsupported", r.Coverage.Status);
        Assert.Empty(r.Facts);
    }

    [Fact]
    public void Dmtf_parse() =>
        Assert.Equal(new DateTime(2026, 1, 2, 2, 4, 5, DateTimeKind.Utc), DefenderCollector.Dmtf("20260102030405.000000+060"));

    // ---------------- Firewall

    [Fact]
    public void Firewall_policy_overrides_local_state()
    {
        var raw = new FirewallRaw(4, new Dictionary<string, FirewallProfileRaw>
        {
            ["domain"] = new(true, 0, false, null, null),
            ["private"] = new(true, 0, false, null, null),
            ["public"] = new(true, 0, false, 0, null),
        }, null);
        var r = new FirewallCollector(Options).Normalize(raw);
        Assert.Equal("complete", r.Coverage.Status);
        AssertSorted(r);
        var pub = r.Facts.Single(f => f.Subject == "firewall.profile.public");
        Assert.False(J.Bool(pub.Value["enabled"]));
        Assert.True(J.Bool(pub.Value["managedByPolicy"]));
        Assert.True(J.Bool(pub.Value["active"]));
    }

    [Fact]
    public void Firewall_unreadable_profile_is_partial()
    {
        var raw = new FirewallRaw(2, new Dictionary<string, FirewallProfileRaw> { ["private"] = new(true, 0, false, null, null) }, null);
        Assert.Equal("partial", new FirewallCollector(Options).Normalize(raw).Coverage.Status);
    }

    // ---------------- Listeners

    [Fact]
    public void Listeners_scope_owner_and_pid_reuse()
    {
        var snap = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        ListenerRowRaw[] rows =
        [
            new("tcp", "ipv4", [0, 0, 0, 0], 5000, 100),
            new("tcp", "ipv4", [127, 0, 0, 1], 5001, 100),
            new("tcp", "ipv6", new byte[16], 135, 4),
            new("udp", "ipv4", [10, 1, 2, 3], 5353, 200),
            new("tcp", "ipv4", [0, 0, 0, 0], 6000, 300),
        ];
        var procs = new Dictionary<uint, ProcessRaw>
        {
            [100] = new(@"Q:\Apps\Dev\server.exe", snap.AddHours(-1), false),
            [200] = new(@"Q:\Win\System32\svchost.exe", snap.AddHours(-2), false),
            [300] = new(@"Q:\Apps\Other\late.exe", snap.AddMinutes(1), false), // started after snapshot → reused PID
        };
        var r = new ListenersCollector(Options, Roots).Normalize(new ListenersRaw(rows, procs, snap, []));
        AssertSorted(r);
        var tcp = r.Facts.Where(f => f.Subject.StartsWith("net.listener.tcp.", StringComparison.Ordinal)).ToList();
        Assert.Contains(tcp, f => J.Long(f.Value["port"]) == 5000 && J.Str(f.Value["scope"]) == "wildcard" && J.Str(f.Value["image"]) == @"%ProgramFiles%\Dev\server.exe");
        Assert.Contains(tcp, f => J.Long(f.Value["port"]) == 5001 && J.Str(f.Value["scope"]) == "loopback");
        Assert.Contains(tcp, f => J.Long(f.Value["port"]) == 135 && J.Bool(f.Value["isSystem"]) == true);
        Assert.Contains(tcp, f => J.Long(f.Value["port"]) == 6000 && J.Bool(f.Value["imageKnown"]) == false);
        Assert.Contains(r.Facts, f => f.Subject.StartsWith("net.listener.udp.5353.", StringComparison.Ordinal) && J.Str(f.Value["scope"]) == "specific");
        Assert.Equal("partial", r.Coverage.Status); // one owner unidentified
        Assert.DoesNotContain(r.Facts, f => f.Value.ToJsonString().Contains("10.1.2.3"));
    }

    // ---------------- Startup

    [Fact]
    public void Startup_normalizes_paths_counts_and_never_keeps_arguments()
    {
        var valid = new SignatureInfo("valid", "Contoso Ltd", false);
        StartupItemRaw[] items =
        [
            new("run", "hkcu", "64", "Helper", "REG_SZ", @"Q:\Users\pat\AppData\Local\Helper\helper.exe", true, null, valid, true, "logon"),
            new("run", "hklm", "32", "Tray", "REG_SZ", @"Q:\Apps86\Tray\tray.exe", false, null, new("unsigned", null, false), false, "logon"),
            new("folder", "user", null, "Notes", null, @"Q:\Apps\Notes\notes.exe", false, null, valid, true, "logon"),
            new("task", @"\Vendor", null, "Updater S-1-5-21-1-2-3-1001", "exec", @"Q:\Apps\Vendor\up.exe", true, null, valid, true, "logon"),
            new("service", "services", null, "Contoso Service", "ContosoSvc", @"Q:\Apps\Contoso\svc.exe", false, null, valid, true, "boot"),
        ];
        var c = new StartupCollector(Options, Roots) { UserName = "pat" };
        var r = c.Normalize(new StartupRaw(items, [], 0, 0, 3, 50, false));
        Assert.Equal("complete", r.Coverage.Status);
        AssertSorted(r);
        var sum = r.Facts.Single(f => f.Subject == "startup.summary");
        Assert.Equal(5, J.Long(sum.Value["total"]));
        Assert.Equal(4, J.Long(sum.Value["signInCount"]));
        Assert.Equal(3, J.Long(sum.Value["signInEnabledCount"])); // the disabled Tray entry doesn't count
        Assert.Equal(1, J.Long(sum.Value["unsignedCount"]));
        var all = string.Join("\n", r.Facts.Select(f => f.Value.ToJsonString() + f.Display));
        Assert.DoesNotContain("Q:\\", all);
        Assert.DoesNotContain("pat", all);
        Assert.DoesNotContain("S-1-5-21", all);
        Assert.Contains(r.Facts, f => f.Subject.StartsWith("startup.run.hkcu.", StringComparison.Ordinal)
            && J.Str(f.Value["image"]) == @"%LOCALAPPDATA%\Helper\helper.exe" && J.Bool(f.Value["hasArguments"]) == true);
    }

    [Fact]
    public void Startup_denied_task_folders_make_partial()
    {
        var r = new StartupCollector(Options, Roots).Normalize(new StartupRaw([], [], 2, 0, 0, 0, false));
        Assert.Equal("partial", r.Coverage.Status);
        Assert.Contains("administrator", r.Coverage.Reason);
    }

    // ---------------- UAC and Secure Boot

    [Fact]
    public void Uac_absent_value_resolves_to_default_enabled()
    {
        var r = new UacCollector(Options).Normalize(new UacRaw(new(null, null), new(5, "REG_DWORD"), new(1, "REG_DWORD"), "Core", null));
        Assert.True(J.Bool(r.Facts.Single().Value["enabled"]));
        Assert.False(J.Bool(r.Facts.Single().Value["enableLuaPresent"]));
    }

    [Fact]
    public void Uac_explicit_zero()
    {
        var r = new UacCollector(Options).Normalize(new UacRaw(new(0, "REG_DWORD"), new(5, "REG_DWORD"), new(1, "REG_DWORD"), "Core", null));
        Assert.False(J.Bool(r.Facts.Single().Value["enabled"]));
    }

    [Theory]
    [InlineData(2, 1, "complete", true)]
    [InlineData(2, 0, "complete", false)]
    [InlineData(1, null, "complete", false)]
    [InlineData(0, null, "unsupported", null)]
    public void SecureBoot_states(int firmware, int? value, string coverage, bool? enabled)
    {
        var r = new SecureBootCollector(Options).Normalize(new SecureBootRaw(firmware, value, value is not null));
        Assert.Equal(coverage, r.Coverage.Status);
        Assert.Equal(enabled, J.Bool(r.Facts.Single().Value["secureBootEnabled"]));
    }
}

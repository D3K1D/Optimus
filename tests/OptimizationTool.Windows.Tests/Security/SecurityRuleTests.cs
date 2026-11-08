using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Windows.Security.Collectors;
using OptimizationTool.Windows.Security.Rules;

namespace OptimizationTool.Windows.Tests.Security;

/// <summary>Each rule: positive, benign look-alike, missing input → insufficient, expected severity. Values invented.</summary>
public sealed class SecurityRuleTests
{
    private const string Fw = FirewallCollector.CollectorId;
    private const string Wsc = WscCollector.CollectorId;
    private const string Def = DefenderCollector.CollectorId;
    private const string Lst = ListenersCollector.CollectorId;
    private const string Su = StartupCollector.CollectorId;
    private const string Uac = UacCollector.CollectorId;
    private const string Sb = SecureBootCollector.CollectorId;

    private static FindingDraft Single(RuleResult r)
    {
        Assert.Equal("finding", r.Status);
        var f = Assert.Single(r.Findings);
        Ctx.AssertCard(f);
        return f;
    }

    private static JsonObject Profile(string name, bool enabled, bool active = true) =>
        new() { ["profile"] = name, ["enabled"] = enabled, ["active"] = active, ["managedByPolicy"] = false };

    private static Ctx Firewall(bool publicOn, bool privateOn = true, bool domainOn = true, bool publicActive = true) => new Ctx()
        .Cover(Fw).Cover(Wsc)
        .Add(Fw, "firewall.profile.public", Profile("Public", publicOn, publicActive))
        .Add(Fw, "firewall.profile.private", Profile("Private", privateOn, !publicActive))
        .Add(Fw, "firewall.profile.domain", Profile("Domain", domainOn, false))
        .Add(Fw, "firewall.active_profiles", new JsonObject { ["public"] = publicActive, ["private"] = !publicActive, ["domain"] = false });

    // ---------------- fw.public_off

    [Fact]
    public void FwPublic_positive_high()
    {
        var f = Single(new FwPublicOffRule().Evaluate(Firewall(publicOn: false).Build()));
        Assert.Equal("high", f.Severity);
        Assert.Equal("Windows says its firewall for public networks is off. Would you like to review the setting?", f.Card.Text);
        Assert.Equal("windowsdefender://network", f.Card.SettingsUri);
    }

    [Fact]
    public void FwPublic_off_but_third_party_firewall_on_is_clear()
    {
        var ctx = Firewall(publicOn: false).Add(Wsc, "security.fw.product.aaaa0001",
            new JsonObject { ["name"] = "Contoso Firewall", ["state"] = "on", ["isMicrosoft"] = false });
        Assert.Equal("clear", new FwPublicOffRule().Evaluate(ctx.Build()).Status);
    }

    [Fact]
    public void FwPublic_on_is_clear() => Assert.Equal("clear", new FwPublicOffRule().Evaluate(Firewall(publicOn: true).Build()).Status);

    [Fact]
    public void FwPublic_missing_is_insufficient()
    {
        Assert.Equal("insufficient", new FwPublicOffRule().Evaluate(new Ctx().Cover(Fw, "failed").Build()).Status);
        Assert.Equal("insufficient", new FwPublicOffRule().Evaluate(new Ctx().Cover(Fw).Build()).Status);
    }

    // ---------------- fw.profile_off

    [Fact]
    public void FwProfile_private_off_medium()
    {
        var f = Single(new FwProfileOffRule().Evaluate(Firewall(publicOn: true, privateOn: false).Build()));
        Assert.Equal("medium", f.Severity);
        Assert.Contains("private networks", f.Card.Text);
    }

    [Fact]
    public void FwProfile_inactive_domain_off_low()
    {
        var f = Single(new FwProfileOffRule().Evaluate(Firewall(publicOn: true, domainOn: false).Build()));
        Assert.Equal("low", f.Severity);
    }

    [Fact]
    public void FwProfile_public_off_alone_is_not_this_rule() =>
        Assert.Equal("clear", new FwProfileOffRule().Evaluate(Firewall(publicOn: false).Build()).Status);

    [Fact]
    public void FwProfile_missing_is_insufficient() =>
        Assert.Equal("insufficient", new FwProfileOffRule().Evaluate(new Ctx().Cover(Fw, "denied").Build()).Status);

    // ---------------- av.none_active

    private static Ctx WscCtx(string health, params (string Name, string State)[] products)
    {
        var c = new Ctx().Cover(Wsc)
            .Add(Wsc, "security.wsc.health.antivirus", new JsonObject { ["usable"] = true, ["health"] = health })
            .Add(Wsc, "security.wsc.products.av", new JsonObject { ["count"] = products.Length });
        var i = 0;
        foreach (var (name, state) in products)
            c.Add(Wsc, $"security.av.product.0000000{i++}", new JsonObject
            {
                ["name"] = name, ["state"] = state, ["isDefault"] = i == 1, ["signatures"] = "up_to_date", ["isMicrosoft"] = false,
            });
        return c;
    }

    [Fact]
    public void AvNone_positive_high()
    {
        var f = Single(new AvNoneActiveRule().Evaluate(WscCtx("poor", ("Contoso AV", "off")).Build()));
        Assert.Equal("high", f.Severity);
        Assert.Equal("windowsdefender://threat", f.Card.SettingsUri);
    }

    [Fact]
    public void AvNone_third_party_on_is_clear() =>
        Assert.Equal("clear", new AvNoneActiveRule().Evaluate(WscCtx("good", ("Windows Defender", "off"), ("Contoso AV", "on")).Build()).Status);

    [Fact]
    public void AvNone_unusable_health_is_insufficient()
    {
        var c = new Ctx().Cover(Wsc).Add(Wsc, "security.wsc.health.antivirus", new JsonObject { ["usable"] = false, ["health"] = "unavailable" });
        Assert.Equal("insufficient", new AvNoneActiveRule().Evaluate(c.Build()).Status);
        Assert.Equal("insufficient", new AvNoneActiveRule().Evaluate(new Ctx().Build()).Status);
    }

    // ---------------- av.rtp_off

    private static Ctx DefCtx(string mode = "primary", bool rtp = true, string updated = "2026-06-14T00:00:00Z", string coverage = "complete") =>
        new Ctx().Cover(Def, coverage).Add(Def, "security.defender.status", new JsonObject
        {
            ["mode"] = mode, ["realTimeProtection"] = rtp, ["signatureUpdatedAt"] = updated, ["signatureAgeDays"] = 1,
        });

    [Fact]
    public void Rtp_positive_high()
    {
        var f = Single(new AvRtpOffRule().Evaluate(DefCtx(rtp: false).Build()));
        Assert.Equal("high", f.Severity);
    }

    [Fact]
    public void Rtp_off_in_passive_mode_is_inapplicable() =>
        Assert.Equal("inapplicable", new AvRtpOffRule().Evaluate(DefCtx("passive", rtp: false).Build()).Status);

    [Fact]
    public void Rtp_on_is_clear() => Assert.Equal("clear", new AvRtpOffRule().Evaluate(DefCtx().Build()).Status);

    [Fact]
    public void Rtp_missing_is_insufficient() =>
        Assert.Equal("insufficient", new AvRtpOffRule().Evaluate(new Ctx().Cover(Def, "failed").Build()).Status);

    // ---------------- av.signatures_stale

    [Fact]
    public void Signatures_twelve_days_old_medium()
    {
        var f = Single(new AvSignaturesStaleRule().Evaluate(DefCtx(updated: "2026-06-03T00:00:00Z").Build()));
        Assert.Equal("medium", f.Severity);
        Assert.Contains("12 days old", f.Card.Text);
    }

    [Fact]
    public void Signatures_seven_days_is_clear() =>
        Assert.Equal("clear", new AvSignaturesStaleRule().Evaluate(DefCtx(updated: "2026-06-08T12:00:00Z").Build()).Status);

    [Fact]
    public void Signatures_third_party_out_of_date_via_wsc()
    {
        var c = DefCtx("passive").Cover(Wsc).Add(Wsc, "security.av.product.0000000a",
            new JsonObject { ["name"] = "Contoso AV", ["state"] = "on", ["isDefault"] = true, ["signatures"] = "out_of_date" });
        var f = Single(new AvSignaturesStaleRule().Evaluate(c.Build()));
        Assert.Equal("medium", f.Severity);
        Assert.Equal("medium", f.Confidence);
    }

    [Fact]
    public void Signatures_missing_is_insufficient() =>
        Assert.Equal("insufficient", new AvSignaturesStaleRule().Evaluate(new Ctx().Build()).Status);

    // ---------------- av.active_threat

    private static Ctx Threats(bool readable, params (string Name, string Classification, bool ActionNeeded)[] threats)
    {
        var c = DefCtx().Add(Def, "security.defender.threats", new JsonObject { ["readable"] = readable });
        var i = 0;
        foreach (var (name, cls, action) in threats)
            c.Add(Def, $"security.defender.threat.0000000{i++}", new JsonObject
            {
                ["threatName"] = name, ["classification"] = cls, ["actionNeeded"] = action, ["status"] = "detected",
            });
        return c;
    }

    [Fact]
    public void Threat_malware_needs_action_high()
    {
        var f = Single(new AvActiveThreatRule().Evaluate(Threats(true, ("Trojan:Win32/Example.A", "malware", true)).Build()));
        Assert.Equal("high", f.Severity);
        Assert.Equal("windowsdefender://threat", f.Card.SettingsUri);
    }

    [Fact]
    public void Threat_pua_keeps_classification_medium()
    {
        var f = Single(new AvActiveThreatRule().Evaluate(Threats(true, ("PUA:Win32/Sample", "pua", true)).Build()));
        Assert.Equal("medium", f.Severity);
        Assert.Contains("potentially unwanted", f.Card.Text);
    }

    [Fact]
    public void Threat_none_open_is_clear() =>
        Assert.Equal("clear", new AvActiveThreatRule().Evaluate(Threats(true).Build()).Status);

    [Fact]
    public void Threat_unreadable_history_is_insufficient_not_clear() =>
        Assert.Equal("insufficient", new AvActiveThreatRule().Evaluate(Threats(false).Build()).Status);

    // ---------------- uac.disabled

    private static Ctx UacCtx(bool? enabled, int consent = 5) => new Ctx().Cover(Uac)
        .Add(Uac, "security.uac", new JsonObject { ["enabled"] = enabled, ["consentPromptBehaviorAdmin"] = consent });

    [Fact]
    public void Uac_off_high()
    {
        var f = Single(new UacDisabledRule().Evaluate(UacCtx(false).Build()));
        Assert.Equal("high", f.Severity);
        Assert.Null(f.Card.SettingsUri);
    }

    [Fact]
    public void Uac_never_notify_medium() =>
        Assert.Equal("medium", Single(new UacDisabledRule().Evaluate(UacCtx(true, consent: 0).Build())).Severity);

    [Fact]
    public void Uac_default_is_clear() => Assert.Equal("clear", new UacDisabledRule().Evaluate(UacCtx(true).Build()).Status);

    [Fact]
    public void Uac_missing_is_insufficient()
    {
        Assert.Equal("insufficient", new UacDisabledRule().Evaluate(new Ctx().Build()).Status);
        Assert.Equal("insufficient", new UacDisabledRule().Evaluate(UacCtx(null).Build()).Status);
    }

    // ---------------- secureboot.off

    private static Ctx SbCtx(string firmware, bool? enabled) => new Ctx().Cover(Sb)
        .Add(Sb, "security.secureboot", new JsonObject { ["firmware"] = firmware, ["secureBootEnabled"] = enabled });

    [Fact]
    public void SecureBoot_uefi_off_low() => Assert.Equal("low", Single(new SecureBootOffRule().Evaluate(SbCtx("uefi", false).Build())).Severity);

    [Fact]
    public void SecureBoot_bios_info() => Assert.Equal("info", Single(new SecureBootOffRule().Evaluate(SbCtx("bios", false).Build())).Severity);

    [Fact]
    public void SecureBoot_on_is_clear() => Assert.Equal("clear", new SecureBootOffRule().Evaluate(SbCtx("uefi", true).Build()).Status);

    [Fact]
    public void SecureBoot_missing_is_insufficient() =>
        Assert.Equal("insufficient", new SecureBootOffRule().Evaluate(new Ctx().Cover(Sb, "timed_out").Build()).Status);

    // ---------------- net.wildcard_listener_public

    private static Ctx ListenerCtx(bool publicActive, string scope, bool system, string? image = "%ProgramFiles%\\Dev\\server.exe")
    {
        return new Ctx().Cover(Lst).Cover(Fw)
            .Add(Fw, "firewall.active_profiles", new JsonObject { ["public"] = publicActive, ["private"] = !publicActive, ["domain"] = false })
            .Add(Lst, "net.listeners.summary", new JsonObject { ["tcpCount"] = 1 })
            .Add(Lst, "net.listener.tcp.5000.0000000a", new JsonObject
            {
                ["protocol"] = "tcp", ["port"] = 5000, ["scope"] = scope, ["isSystem"] = system, ["image"] = image,
            });
    }

    [Fact]
    public void Listener_wildcard_on_public_is_info_review()
    {
        var f = Single(new WildcardListenerPublicRule().Evaluate(ListenerCtx(true, "wildcard", false).Build()));
        Assert.Equal("info", f.Severity);
        Assert.Contains("server.exe", f.Card.Text);
        Assert.DoesNotContain("malware", f.Card.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Listener_loopback_dev_server_is_clear() =>
        Assert.Equal("clear", new WildcardListenerPublicRule().Evaluate(ListenerCtx(true, "loopback", false).Build()).Status);

    [Fact]
    public void Listener_system_wildcard_is_clear() =>
        Assert.Equal("clear", new WildcardListenerPublicRule().Evaluate(ListenerCtx(true, "wildcard", true, "%SystemRoot%\\System32\\svchost.exe").Build()).Status);

    [Fact]
    public void Listener_private_network_is_inapplicable() =>
        Assert.Equal("inapplicable", new WildcardListenerPublicRule().Evaluate(ListenerCtx(false, "wildcard", false).Build()).Status);

    // Profile-aware wording (protocol-v1 §10 "Profile")

    private static Ctx PortCtx(string image, int port) => new Ctx().Cover(Lst).Cover(Fw)
        .Add(Fw, "firewall.active_profiles", new JsonObject { ["public"] = true, ["private"] = false, ["domain"] = false })
        .Add(Lst, "net.listeners.summary", new JsonObject { ["tcpCount"] = 1 })
        .Add(Lst, $"net.listener.tcp.{port}.0000000b", new JsonObject
        {
            ["protocol"] = "tcp", ["port"] = port, ["scope"] = "wildcard", ["isSystem"] = false, ["image"] = image,
        });

    private const string Steam = "%ProgramFiles(x86)%\\Steam\\steam.exe";

    [Fact]
    public void Listener_gaming_profile_game_port_says_likely_for_games()
    {
        var f = Single(new WildcardListenerPublicRule().Evaluate(PortCtx(Steam, 27015).Build("Gaming")));
        Assert.Equal("info", f.Severity);
        Assert.Equal("steam.exe accepts connections on all networks, likely for games. Would you like to review it?", f.Card.Text);
    }

    [Fact]
    public void Listener_no_profile_is_unchanged() =>
        Assert.Equal("steam.exe accepts connections on port 27015 from other devices while you’re on a public network. Would you like to review it?",
            Single(new WildcardListenerPublicRule().Evaluate(PortCtx(Steam, 27015).Build())).Card.Text);

    [Fact]
    public void Listener_hosting_profile_dev_port_says_likely_for_development()
    {
        var f = Single(new WildcardListenerPublicRule().Evaluate(PortCtx("%LOCALAPPDATA%\\Dev\\node.exe", 5173).Build("Hosting / development")));
        Assert.Equal("info", f.Severity);
        Assert.Equal("node.exe accepts connections on all networks, likely for development. Would you like to review it?", f.Card.Text);
    }

    [Theory]
    [InlineData("Gaming")]
    [InlineData("Hosting / development")]
    [InlineData("Gaming", "Hosting / development")]
    public void Listener_remote_desktop_port_is_unchanged_for_any_profile(params string[] uses)
    {
        var f = Single(new WildcardListenerPublicRule().Evaluate(PortCtx("%ProgramFiles%\\Remote\\rd.exe", 3389).Build(uses)));
        Assert.Equal("rd.exe accepts connections on port 3389 from other devices while you’re on a public network. Would you like to review it?", f.Card.Text);
    }

    [Fact]
    public void Listener_missing_is_insufficient() =>
        Assert.Equal("insufficient", new WildcardListenerPublicRule().Evaluate(new Ctx().Cover(Lst).Build()).Status);

    // ---------------- startup.many_entries

    private static Ctx StartupCtx(int enabled) => new Ctx().Cover(Su, "partial")
        .Add(Su, "startup.summary", new JsonObject { ["signInEnabledCount"] = enabled, ["signInCount"] = enabled + 3 });

    [Fact]
    public void Startup_many_is_info_and_never_claims_unused()
    {
        var f = Single(new StartupManyEntriesRule().Evaluate(StartupCtx(14).Build()));
        Assert.Equal("info", f.Severity);
        Assert.Equal("14 apps start when you sign in. Would you like to review them?", f.Card.Text);
        Assert.Equal("ms-settings:startupapps", f.Card.SettingsUri);
        Assert.DoesNotContain("unused", f.Card.Text + f.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Startup_ten_is_clear() => Assert.Equal("clear", new StartupManyEntriesRule().Evaluate(StartupCtx(10).Build()).Status);

    [Fact]
    public void Startup_missing_is_insufficient() =>
        Assert.Equal("insufficient", new StartupManyEntriesRule().Evaluate(new Ctx().Cover(Su, "failed").Build()).Status);

    // ---------------- module contract

    [Fact]
    public void Module_ids_versions_and_inputs()
    {
        var m = new OptimizationTool.Windows.Security.SecurityModule();
        var collectors = m.Collectors(new CollectorOptions()).ToList();
        Assert.All(collectors, c => { Assert.StartsWith("win.security.", c.Id); Assert.Equal("1.0.0", c.Version); Assert.True(c.Timeout > TimeSpan.Zero); });
        var ids = collectors.Select(c => c.Id).ToHashSet();
        Assert.All(m.Rules(), r => { Assert.Equal("1.0.0", r.Version); Assert.All(r.Inputs, i => Assert.Contains(i, ids)); });
        Assert.Equal(10, m.Rules().Select(r => r.Id).Distinct().Count());
    }
}

using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Broker;
using OptimizationTool.Windows.Actions.Firewall;

namespace OptimizationTool.Windows.Tests.Actions.Admin;

/// <summary>firewall.block_app (m9 §3.2), host half and broker half, with fake firewall rules, state and file reads.</summary>
public sealed class BlockAppTests
{
    private const string Program = @"Q:\Program Files\Contoso\server.exe";

    private static (BlockAppHandler H, FakeRules Rules, FakeFirewallState Fw, FakeFiles Files) New()
    {
        var rules = new FakeRules();
        var fw = new FakeFirewallState();
        var files = new FakeFiles();
        files.Hashes[Program] = Admin.Hash;
        return (new BlockAppHandler(rules, fw, files, Expand), rules, fw, files);
    }

    /// <summary>The evidence's normalized path, expanded with a fixed root (the real handler uses this PC's known folders).</summary>
    private static string? Expand(string? s) => s is { } v && v.StartsWith("%ProgramFiles%", StringComparison.Ordinal) ? @"Q:\Program Files" + v["%ProgramFiles%".Length..] : null;

    private static TargetEvidence Ev(string? image = @"%ProgramFiles%\Contoso\server.exe", bool system = false, string collector = "win.security.listeners") =>
        new("ev-1", collector, "net.listener.tcp.8080.0badc0de", new JsonObject
        {
            ["protocol"] = "tcp", ["port"] = 8080, ["scope"] = "wildcard", ["image"] = image, ["imageKnown"] = image is not null, ["isSystem"] = system,
        }, "2026-11-12T08:00:00Z");

    private static ActionRequest Fresh(TargetEvidence? ev = null) => new(BlockAppHandler.Id, null, null, null) { Evidence = ev ?? Ev() };

    [Fact]
    public void Eligible_block_names_one_Optimus_rule_for_the_program()
    {
        var (h, _, _, _) = New();
        var r = h.Resolve(Fresh());
        Assert.True(r.Eligible, r.IneligibleMessage);
        Assert.Equal("admin", r.Privilege);
        Assert.Equal([ActionFlags.Network], h.Flags);
        var name = r.Target.Str("ruleName")!;
        Assert.StartsWith("Optimus: block inbound · server.exe · ", name);
        Assert.Equal(["fw:rule:" + name.ToUpperInvariant()], r.Resources);
        Assert.False(r.Before["present"]!.GetValue<bool>());
        var after = r.After;
        Assert.Equal("in|block|Optimus", string.Join('|', after.Str("direction"), after.Str("action"), after.Str("group")));
        Assert.Equal(0x7FFFFFFF, after["profiles"]!.GetValue<int>());
        // The id is fixed in the plan: a recheck of the plan's target names the same rule.
        var again = h.Resolve(new ActionRequest(BlockAppHandler.Id, r.Target, null, null) { Recheck = true });
        Assert.True(JsonNode.DeepEquals(again.After, r.After));
    }

    [Fact]
    public void Ineligible_paths_protected_system_hardware_already_set_managed_other_firewall_not_found_unknown()
    {
        // A system component (the listener said so), or a protected program by its live publisher.
        Assert.Equal("protected", New().H.Resolve(Fresh(Ev(system: true))).IneligibleCode);
        var (sec, _, _, files) = New();
        files.Publishers[Program] = "Kaspersky Lab";
        Assert.Equal("protected", sec.Resolve(Fresh()).IneligibleCode);
        var (hw, _, _, hwFiles) = New();
        hwFiles.Hashes[@"Q:\Program Files\NZXT CAM\NZXT CAM.exe"] = Admin.Hash;
        Assert.Equal("hardware_limit", hw.Resolve(Fresh(Ev(@"%ProgramFiles%\NZXT CAM\NZXT CAM.exe"))).IneligibleCode);
        // Optimus already blocks it.
        var (dup, rules, _, _) = New();
        rules.Rules.Add(new FirewallRuleInfo("Optimus: block inbound · server.exe · 0a0b0c0d", Program, "in", "block", 0x7FFFFFFF, true, 256, "Optimus"));
        Assert.Equal("already_set", dup.Resolve(Fresh()).IneligibleCode);
        // Windows Firewall must be the firewall in charge, on an unmanaged PC.
        var (other, _, fw, _) = New();
        fw.State = fw.State with { OtherFirewallOn = true };
        Assert.Equal("other_provider", other.Resolve(Fresh()).IneligibleCode);
        fw.State = fw.State with { OtherFirewallOn = null };
        Assert.Equal("provider_unknown", other.Resolve(Fresh()).IneligibleCode);
        fw.State = fw.State with { OtherFirewallOn = false, PolicyConfigured = true };
        Assert.Equal("managed", other.Resolve(Fresh()).IneligibleCode);
        fw.State = fw.State with { PolicyConfigured = false, Managed = true };
        Assert.Equal("managed", other.Resolve(Fresh()).IneligibleCode);
        // The program is gone, or the evidence can't name a program.
        var (gone, _, _, goneFiles) = New();
        goneFiles.Hashes.Clear();
        Assert.Equal("not_found", gone.Resolve(Fresh()).IneligibleCode);
        Assert.Equal(ActionService.UnknownTarget, New().H.Resolve(Fresh(Ev("\u2026\\server.exe"))).IneligibleCode);
        Assert.Equal(ActionService.UnknownTarget, New().H.Resolve(Fresh(Ev(collector: "win.security.startup"))).IneligibleCode);
        Assert.Equal("invalid_arguments", New().H.Resolve(new ActionRequest(BlockAppHandler.Id, null, new JsonObject { ["mode"] = "remove" }, null) { Evidence = Ev() }).IneligibleCode);
    }

    [Fact]
    public void Broker_adds_the_rule_and_undo_removes_exactly_that_rule()
    {
        var (h, rules, fw, files) = New();
        var r = h.Resolve(Fresh());
        var broker = new BlockAppBrokerHandler(rules, fw, files);
        var step = Admin.Step(BlockAppHandler.Id, r, null);
        var live = broker.Resolve(step);
        Assert.True(live.Eligible, live.IneligibleMessage);
        Assert.Null(broker.Protect(live));
        Assert.Equal(step.ContentHash, BrokerContent.Hash(broker.ActionId, broker.Version, live.Target, live.Before, live.After, null));
        Assert.Equal("verified", broker.WriteAndReadBack(live, step, CancellationToken.None).State);
        var added = Assert.Single(rules.Rules);
        Assert.Equal((Program, "in", "block", true), (added.ApplicationPath, added.Direction, added.Action, added.Enabled));
        // Someone else's rule for the same program is never touched.
        rules.Rules.Add(new FirewallRuleInfo("Contoso server", Program, "in", "allow", 7, true, 6, null));

        var undo = h.UndoRequest(r.Target, r.Before)!;
        var ur = h.Resolve(undo);
        Assert.True(ur.Eligible, ur.IneligibleMessage);
        var ustep = Admin.Step(BlockAppHandler.Id, ur, undo.Parameters);
        Assert.True(broker.IsUndo(ustep));
        Assert.Null(broker.Protect(broker.Resolve(ustep)));

        // The rule was edited (a profile removed): undo refused.
        rules.Rules[0] = added with { Profiles = 4 };
        Assert.Equal("changed_since", h.Resolve(undo).IneligibleCode);
        Assert.Equal("changed_since", broker.Resolve(ustep).IneligibleCode);
        rules.Rules[0] = added;
        Assert.Equal("verified", broker.WriteAndReadBack(broker.Resolve(ustep), ustep, CancellationToken.None).State);
        Assert.Equal(["Contoso server"], rules.Rules.Select(x => x.Name));
        Assert.Equal(["add:" + added.Name, "remove:" + added.Name], rules.Writes);
        // Gone already.
        Assert.Equal("already_set", h.Resolve(undo).IneligibleCode);
    }

    public static TheoryData<string> ScopeChanges => ["remoteAddresses", "localAddresses", "interfaceTypes", "interfaces", "remotePorts", "service", "edge", "unreadable"];

    [Theory]
    [MemberData(nameof(ScopeChanges))]
    public void Undo_is_refused_when_only_the_rules_scope_changed_or_cant_be_read(string change)
    {
        var (h, rules, fw, files) = New();
        var r = h.Resolve(Fresh());
        var broker = new BlockAppBrokerHandler(rules, fw, files);
        var step = Admin.Step(BlockAppHandler.Id, r, null);
        Assert.Equal("verified", broker.WriteAndReadBack(broker.Resolve(step), step, CancellationToken.None).State);
        var added = Assert.Single(rules.Rules);
        var undo = h.UndoRequest(r.Target, r.Before)!;
        var ustep = Admin.Step(BlockAppHandler.Id, h.Resolve(undo), undo.Parameters);
        Assert.True(broker.Resolve(ustep).Eligible);
        // A rule Windows reads back with an empty port list or "All" interface types is the same unrestricted rule.
        rules.Rules[0] = added with { LocalPorts = "", RemotePorts = "*", InterfaceTypes = "all", LocalAddresses = "" };
        Assert.True(broker.Resolve(ustep).Eligible);

        rules.Rules[0] = change switch
        {
            "remoteAddresses" => added with { RemoteAddresses = "LocalSubnet" }, // an administrator narrowed it: it no longer blocks everyone
            "localAddresses" => added with { LocalAddresses = "192.168.1.10" },
            "interfaceTypes" => added with { InterfaceTypes = "Wireless" },
            "interfaces" => added with { Interfaces = "Ethernet 2" },
            "remotePorts" => added with { RemotePorts = "443" },
            "service" => added with { ServiceName = "ContosoSvc" },
            "edge" => added with { EdgeTraversal = true },
            _ => added with { ScopeRead = false },
        };
        Assert.Equal("changed_since", h.Resolve(undo).IneligibleCode);
        Assert.Equal("changed_since", broker.Resolve(ustep).IneligibleCode);
        Assert.Equal(["add:" + added.Name], rules.Writes); // nothing removed
    }

    [Fact]
    public void A_write_that_does_not_stick_fails_and_forged_targets_are_refused()
    {
        var (h, rules, _, _) = New();
        var r = h.Resolve(Fresh());
        rules.Ignore = true;
        Assert.Equal("failed", h.Execute(Admin.Exec(r)).State);
        // A target that names another rule or a relative program is not this handler's.
        var t = (JsonObject)r.Target.DeepClone();
        t["ruleName"] = "Allow everything";
        t["id"] = "ALLOW EVERYTHING";
        Assert.Equal(ActionService.UnknownTarget, h.Resolve(new ActionRequest(BlockAppHandler.Id, t, null, null) { Recheck = true }).IneligibleCode);
        var rel = (JsonObject)r.Target.DeepClone();
        rel["program"] = "server.exe";
        Assert.Equal(ActionService.UnknownTarget, h.Resolve(new ActionRequest(BlockAppHandler.Id, rel, null, null) { Recheck = true }).IneligibleCode);
    }

    [Fact]
    public void Broker_rechecks_protection_even_when_the_host_let_it_through()
    {
        var (_, rules, fw, files) = New();
        var broker = new BlockAppBrokerHandler(rules, fw, files);
        var add = new JsonObject { ["present"] = true };
        files.Publishers[Program] = "CrowdStrike, Inc.";
        Assert.Equal("protected", broker.Protect(new ActionResolution(true, new JsonObject { ["program"] = Program }, new JsonObject(), add, [], [], [], null))!.Code);
        Assert.Equal("protected", broker.Protect(new ActionResolution(true, new JsonObject { ["program"] = @"Q:\Program Files\x\OptimizationTool.Host.exe" }, new JsonObject(), add, [], [], [], null))!.Code);
        Assert.Equal("protected", broker.Protect(new ActionResolution(true, new JsonObject(), new JsonObject(), add, [], [], [], null))!.Code);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Equal("protected", broker.Protect(new ActionResolution(true, new JsonObject { ["program"] = Path.Combine(windows, "System32", "svchost.exe") }, new JsonObject(), add, [], [], [], null))!.Code);
    }
}

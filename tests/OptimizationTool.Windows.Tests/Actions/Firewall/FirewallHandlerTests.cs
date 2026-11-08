using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Firewall;

namespace OptimizationTool.Windows.Tests.Actions.Firewall;

/// <summary>firewall.enable_public with fake state and fake elevation. Nothing here writes or elevates.</summary>
public sealed class FirewallHandlerTests
{
    private sealed class FakeReader(PublicFirewallSnapshot s) : IPublicFirewallReader
    {
        public int Reads { get; private set; }
        public PublicFirewallSnapshot Read(CancellationToken ct) { Reads++; return s; }
    }

    private sealed class FakeElevation : IElevation
    {
        public List<(string ActionId, ActionExecution Execution)> Calls { get; } = [];
        public Task<ActionOutcome> RunAsync(string actionId, ActionExecution execution, CancellationToken ct)
        {
            Calls.Add((actionId, execution));
            return Task.FromResult(new ActionOutcome("verified", (JsonObject)execution.After.DeepClone(), "ok"));
        }
    }

    private static PublicFirewallSnapshot Off => new(false, false, true, false, false, false, true, false, false, null, [], []);

    private static FirewallEnablePublicHandler Handler(PublicFirewallSnapshot s, bool enabled = true, FakeElevation? elevation = null) =>
        new(new ActionContext(elevation ?? new FakeElevation(), enabled), new FakeReader(s));

    private static ActionRequest Enable => new(FirewallPlan.ActionId, null, null, "firewall.profile.public");

    [Fact]
    public async Task Gate_off_is_ineligible_with_manual_route_and_reads_nothing()
    {
        var reader = new FakeReader(Off);
        var h = new FirewallEnablePublicHandler(new ActionContext(new FakeElevation(), false), reader);
        var r = await h.ResolveAsync(Enable, CancellationToken.None);
        Assert.False(r.Eligible);
        Assert.Equal("disabled_pending_review", r.IneligibleCode);
        Assert.Equal("windowsdefender://network", r.ManualUri);
        Assert.Equal(0, reader.Reads);
    }

    [Fact]
    public async Task Eligible_plan_shape()
    {
        var r = await Handler(Off).ResolveAsync(Enable, CancellationToken.None);
        Assert.True(r.Eligible);
        Assert.Equal("public", FirewallPlan.Str(r.Target["id"]));
        Assert.Equal("""{"publicProfile":{"localEnabled":false,"effectiveEnabled":false}}""", r.Before.ToJsonString());
        Assert.Equal("""{"publicProfile":{"localEnabled":true,"effectiveEnabled":true}}""", r.After.ToJsonString());
        var row = Assert.Single(r.Rows);
        Assert.Equal(("Off", "On"), (row.Before, row.After));
        Assert.NotEmpty(r.Tradeoffs);
    }

    public static TheoryData<string, PublicFirewallSnapshot> Ineligible => new()
    {
        { "already_on", Off with { LocalEnabled = true, EffectiveEnabled = true } },
        { "already_on", Off with { EffectiveEnabled = true } },
        { "not_explicit", Off with { LocalEnabled = null } },
        { "public_not_active", Off with { PublicActive = false } },
        { "policy_managed", Off with { PolicyConfigured = true } },
        { "policy_managed", Off with { MdmConfigured = true } },
        { "managed_device", Off with { Managed = true } },
        { "other_provider", Off with { OtherFirewallOn = true } },
        { "provider_unknown", Off with { OtherFirewallOn = null } },
        { "remote_session", Off with { RemoteSession = true } },
        { "remote_session", Off with { ConsoleSession = false } },
        { "unreadable", Off with { ReadError = "COMException" } },
    };

    [Theory]
    [MemberData(nameof(Ineligible))]
    public async Task Each_eligibility_condition_is_enforced(string code, PublicFirewallSnapshot s)
    {
        var r = await Handler(s).ResolveAsync(Enable, CancellationToken.None);
        Assert.False(r.Eligible);
        Assert.Equal(code, r.IneligibleCode);
        Assert.Equal("windowsdefender://network", r.ManualUri);
    }

    [Theory]
    [InlineData("""{"id":"private"}""", null)]
    [InlineData("""{"id":"public","path":"x"}""", null)]
    [InlineData(null, """{"mode":"delete"}""")]
    [InlineData(null, """{"mode":"enable","profile":"domain"}""")]
    public async Task Hostile_targets_and_parameters_are_refused(string? target, string? parameters)
    {
        var req = new ActionRequest(FirewallPlan.ActionId,
            target is null ? null : JsonNode.Parse(target)!.AsObject(), parameters is null ? null : JsonNode.Parse(parameters)!.AsObject(), null);
        var r = await Handler(Off).ResolveAsync(req, CancellationToken.None);
        Assert.Equal("bad_request", r.IneligibleCode);
    }

    [Fact]
    public async Task Undo_is_a_fresh_disable_plan_with_a_warning_row()
    {
        var h = Handler(Off with { LocalEnabled = true, EffectiveEnabled = true });
        var undo = h.UndoRequest(FirewallPlan.Target(), FirewallPlan.State(false, false))!;
        Assert.Equal("disable", FirewallPlan.Str(undo.Parameters!["mode"]));
        var r = await h.ResolveAsync(undo, CancellationToken.None);
        Assert.True(r.Eligible);
        Assert.Contains(r.Rows, x => x.Label == "Warning");
        Assert.Equal("""{"publicProfile":{"localEnabled":false,"effectiveEnabled":false}}""", r.After.ToJsonString());
    }

    [Fact]
    public async Task Undo_after_drift_is_changed_since()
    {
        var h = Handler(Off with { EffectiveEnabled = true, LocalEnabled = false });
        var r = await h.ResolveAsync(h.UndoRequest(FirewallPlan.Target(), FirewallPlan.State(false, false))!, CancellationToken.None);
        Assert.Equal("changed_since", r.IneligibleCode);
    }

    [Fact]
    public async Task Execute_goes_only_through_elevation()
    {
        var elevation = new FakeElevation();
        var h = Handler(Off, elevation: elevation);
        var plan = await h.ResolveAsync(Enable, CancellationToken.None);
        var outcome = await h.ExecuteAsync(new ActionExecution("plan-1", "hash", plan.Target, plan.Before, plan.After, null), null, CancellationToken.None);
        Assert.Equal("verified", outcome.State);
        var call = Assert.Single(elevation.Calls);
        Assert.Equal(FirewallPlan.ActionId, call.ActionId);
        Assert.Equal("hash", call.Execution.PlanHash);
    }

    [Fact]
    public async Task Execute_with_gate_off_never_elevates()
    {
        var elevation = new FakeElevation();
        var h = Handler(Off, enabled: false, elevation: elevation);
        var outcome = await h.ExecuteAsync(new ActionExecution("p", "h", FirewallPlan.Target(), FirewallPlan.State(false, false), FirewallPlan.State(true, true), null), null, CancellationToken.None);
        Assert.Equal("failed", outcome.State);
        Assert.Empty(elevation.Calls);
    }

    [Fact]
    public async Task Read_target_shape()
    {
        var o = await Handler(Off).ReadTargetAsync(FirewallPlan.Target(), CancellationToken.None);
        Assert.False(FirewallPlan.Bool(o["publicProfile"]!["effectiveEnabled"]));
        await Assert.ThrowsAsync<ArgumentException>(() => Handler(Off).ReadTargetAsync(new JsonObject { ["id"] = "domain" }, CancellationToken.None));
    }

    [Fact]
    public void Content_hash_ignores_key_order_and_labels_but_not_values()
    {
        var a = FirewallPlan.ContentHash(FirewallPlan.ActionId, "enable", FirewallPlan.Target(), FirewallPlan.State(false, false), FirewallPlan.State(true, true));
        var reordered = JsonNode.Parse("""{"publicProfile":{"effectiveEnabled":false,"localEnabled":false}}""")!.AsObject();
        var b = FirewallPlan.ContentHash(FirewallPlan.ActionId, "enable", new JsonObject { ["id"] = "public" }, reordered, FirewallPlan.State(true, true));
        Assert.Equal(a, b);
        Assert.NotEqual(a, FirewallPlan.ContentHash(FirewallPlan.ActionId, "disable", FirewallPlan.Target(), FirewallPlan.State(false, false), FirewallPlan.State(true, true)));
        Assert.NotEqual(a, FirewallPlan.ContentHash(FirewallPlan.ActionId, "enable", FirewallPlan.Target(), FirewallPlan.State(false, true), FirewallPlan.State(true, true)));
    }

    // ---------------- inbound dependence (all values invented)

    private const string Game = @"Q:\Games\Arena\server.exe";
    private static readonly InboundAllowRule GameRule = new("Arena Server", Game, null, null, "tcp", false);

    [Fact]
    public async Task Active_server_with_public_allow_rule_is_inbound_in_use()
    {
        var s = Off with { Listening = [new ListeningProgram(Game, "tcp", 27015, false, [])], InboundRules = [GameRule] };
        var r = await Handler(s).ResolveAsync(Enable, CancellationToken.None);
        Assert.False(r.Eligible);
        Assert.Equal("inbound_in_use", r.IneligibleCode);
        Assert.Equal("windowsdefender://network", r.ManualUri);
        Assert.StartsWith("1 program ", r.IneligibleMessage);
        var row = Assert.Single(r.Context);
        Assert.Equal("server.exe · TCP 27015", row.Value);
        Assert.NotEmpty(r.Rows); // guidance the UI can still show
    }

    [Fact]
    public async Task Rule_but_nothing_listening_is_eligible_with_trade_off_line()
    {
        var s = Off with { InboundRules = [GameRule, new("Core Networking", null, null, "68", "udp", true)] };
        var r = await Handler(s).ResolveAsync(Enable, CancellationToken.None);
        Assert.True(r.Eligible);
        Assert.Contains("May block incoming connections for: server.exe and 1 Windows feature.", r.Tradeoffs);
    }

    [Fact]
    public async Task Loopback_only_listener_is_ignored()
    {
        var snap = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var raw = new OptimizationTool.Windows.Security.Collectors.ListenersRaw(
            [
                new("tcp", "ipv4", [127, 0, 0, 1], 3000, 100),      // dev server, loopback only
                new("tcp", "ipv6", [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1], 3000, 100),
                new("udp", "ipv4", [0, 0, 0, 0], 5353, 200),        // system UDP → ignored
                new("udp", "ipv4", [0, 0, 0, 0], 27016, 300),       // app UDP → counts
            ],
            new Dictionary<uint, OptimizationTool.Windows.Security.Collectors.ProcessRaw>
            {
                [100] = new(@"Q:\Dev\node.exe", snap.AddHours(-1), false),
                [200] = new(@"Q:\Win\System32\svchost.exe", snap.AddHours(-1), false),
                [300] = new(Game, snap.AddHours(-1), false),
            }, snap, []);
        var listening = InboundReader.FromRaw(raw, new Dictionary<uint, IReadOnlyList<string>>(), @"Q:\Win");
        var only = Assert.Single(listening);
        Assert.Equal(("udp", 27016), (only.Protocol, only.Port));

        var s = Off with { Listening = listening.Where(l => l.ImagePath != Game).ToList(), InboundRules = [new("Dev", @"Q:\Dev\node.exe", null, "3000", "tcp", false)] };
        Assert.True((await Handler(s).ResolveAsync(Enable, CancellationToken.None)).Eligible);
    }

    [Fact]
    public async Task Built_in_windows_rule_on_a_system_listener_does_not_block_the_plan()
    {
        var s = Off with
        {
            Listening = [new ListeningProgram(@"Q:\Win\System32\svchost.exe", "tcp", 7680, true, ["dosvc"])],
            InboundRules = [new("Delivery Optimization (TCP-In)", @"Q:\Win\System32\svchost.exe", "dosvc", "7680", "tcp", BuiltIn: true)],
        };
        var r = await Handler(s).ResolveAsync(Enable, CancellationToken.None);
        Assert.True(r.Eligible);
        Assert.Contains("May block incoming connections for: 1 Windows feature.", r.Tradeoffs);
    }

    [Fact]
    public async Task Unreadable_listeners_or_rules_are_ambiguous()
    {
        Assert.Equal("inbound_unknown", (await Handler(Off with { Listening = null }).ResolveAsync(Enable, CancellationToken.None)).IneligibleCode);
        Assert.Equal("inbound_unknown", (await Handler(Off with { InboundRules = null }).ResolveAsync(Enable, CancellationToken.None)).IneligibleCode);
    }

    [Theory]
    [InlineData("tcp", null, null, "*", true)]           // allow-all on TCP
    [InlineData("udp", null, null, null, false)]         // protocol differs
    [InlineData("any", null, null, "27000-27100", true)] // port range
    [InlineData("tcp", null, null, "80,443", false)]
    [InlineData("tcp", null, "Arena", null, true)]       // service hosted in the PID
    [InlineData("tcp", null, "Other", null, false)]
    [InlineData("tcp", @"Q:\Other\app.exe", null, null, false)]
    [InlineData("tcp", null, null, "RPC", false)]        // dynamic keyword can't be mapped
    public void Rule_matching(string protocol, string? app, string? service, string? ports, bool expected)
    {
        var p = new ListeningProgram(Game, "tcp", 27015, false, ["Arena"]);
        Assert.Equal(expected, FirewallPlan.Matches(new InboundAllowRule("r", app, service, ports, protocol, false), p));
    }

    [Fact]
    public void Module_registered()
    {
        Assert.Contains(WindowsModules.Actions, m => m is FirewallActionModule);
        var h = Assert.Single(new FirewallActionModule().Handlers(new ActionContext(new FakeElevation(), false)));
        Assert.Equal(("firewall.enable_public", "admin", true), (h.ActionId, h.Privilege, h.SupportsUndo));
    }
}

/// <summary>Live: only ResolveAsync and ReadTargetAsync (reads). Never executes.</summary>
[Trait("Category", "Live")]
public sealed class FirewallHandlerLiveTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private sealed class NoElevation : IElevation
    {
        public Task<ActionOutcome> RunAsync(string actionId, ActionExecution execution, CancellationToken ct) =>
            throw new InvalidOperationException("Live tests must never execute.");
    }

    [Fact]
    public async Task Resolve_and_read_on_this_pc()
    {
        var h = new FirewallEnablePublicHandler(new ActionContext(new NoElevation(), true));
        var r = await h.ResolveAsync(new ActionRequest(FirewallPlan.ActionId, null, null, null), CancellationToken.None);
        output.WriteLine($"eligible={r.Eligible} code={r.IneligibleCode}");
        var snap = new WindowsPublicFirewallReader().Read(CancellationToken.None);
        Assert.NotNull(snap.Listening);
        Assert.NotNull(snap.InboundRules);
        output.WriteLine($"listening={snap.Listening!.Count} publicAllowRules={snap.InboundRules!.Count} builtIn={snap.InboundRules.Count(x => x.BuiltIn)} inUse={FirewallPlan.InboundInUse(snap.Listening, snap.InboundRules).Count}");
        if (Environment.GetEnvironmentVariable("OT_FW_DUMP") == "1")
            foreach (var l in snap.Listening)
                foreach (var rule in snap.InboundRules.Where(x => FirewallPlan.Matches(x, l)).Take(2))
                    output.WriteLine($"  {l.Protocol} {l.Port} sys={l.IsSystem} img={(l.ImagePath is null ? "-" : Path.GetFileName(l.ImagePath))} <= rule app={rule.ApplicationPath is not null} svc={rule.ServiceName} ports={rule.LocalPorts} proto={rule.Protocol} builtIn={rule.BuiltIn} name={rule.Name}");
        if (!r.Eligible) Assert.Equal("windowsdefender://network", r.ManualUri);
        var state = await h.ReadTargetAsync(FirewallPlan.Target(), CancellationToken.None);
        Assert.NotNull(state["publicProfile"]);
        var gated = await new FirewallEnablePublicHandler(new ActionContext(new NoElevation(), false))
            .ResolveAsync(new ActionRequest(FirewallPlan.ActionId, null, null, null), CancellationToken.None);
        Assert.Equal("disabled_pending_review", gated.IneligibleCode);
    }
}

using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Power;
using OptimizationTool.Windows.SystemDomain;

namespace OptimizationTool.Windows.Tests.Actions.Power;

/// <summary>In-memory power state. Never touches Windows.</summary>
internal sealed class FakePowerApi : IPowerApi
{
    public List<SchemeInfo> Schemes { get; } =
    [
        new(PowerCollector.Balanced, "Balanced", "balanced"),
        new(PowerCollector.HighPerformance, "High performance", "high_performance"),
        new(PowerCollector.PowerSaver, "Power saver", "power_saver"),
    ];
    public Guid? Active { get; set; } = PowerCollector.HighPerformance;
    public SourceInfo? Source { get; set; } = new("battery", true, 64);
    public bool Policy { get; set; }
    public bool Managed { get; set; }
    public int SetCalls { get; private set; }
    /// <summary>What the write does: default sets Active to the requested scheme and returns 0.</summary>
    public Func<Guid, (uint Rc, Guid? Result)>? OnSet { get; set; }

    public IReadOnlyList<SchemeInfo> EnumerateSchemes() => Schemes;
    public Guid? GetActiveScheme() => Active;
    public uint SetActiveScheme(Guid scheme)
    {
        SetCalls++;
        var (rc, result) = OnSet?.Invoke(scheme) ?? (0u, scheme);
        Active = result;
        return rc;
    }
    public SourceInfo? GetSource() => Source;
    public bool PolicyForcesActiveScheme() => Policy;
    public bool ManagedActiveScheme() => Managed;
}

public class SelectSchemeHandlerTests
{
    private static readonly string Bal = PowerCollector.Balanced.ToString("D");
    private static readonly string High = PowerCollector.HighPerformance.ToString("D");

    private static ActionRequest ByGuid(string g) => new(SelectSchemeHandler.Id, new JsonObject { ["schemeGuid"] = g }, null, null);
    private static ActionRequest ByPersonality(string p) => new(SelectSchemeHandler.Id, null, new JsonObject { ["personality"] = p }, null);

    private static ActionExecution Exec(ActionResolution r) => new("plan-1", "hash", r.Target, r.Before, r.After, null);

    [Fact]
    public async Task Resolve_by_guid_builds_plan()
    {
        var api = new FakePowerApi();
        var r = await new SelectSchemeHandler(api).ResolveAsync(ByGuid(Bal), CancellationToken.None);
        Assert.True(r.Eligible);
        Assert.Equal("power_scheme", r.Target["type"]!.GetValue<string>());
        Assert.Equal(Bal, r.Target["id"]!.GetValue<string>());
        Assert.Equal(High, r.Before["activeScheme"]!["id"]!.GetValue<string>());
        Assert.Equal("Balanced", r.After["activeScheme"]!["label"]!.GetValue<string>());
        var row = Assert.Single(r.Rows);
        Assert.Equal(new PlanRow("Active power plan", "High performance", "Balanced"), row);
        Assert.Equal(new KeyValuePair<string, string>("Power", "On battery · 64%"), Assert.Single(r.Context));
        Assert.Equal(["Slightly lower peak speed"], r.Tradeoffs);
        Assert.Contains("Balanced", r.UndoConditions);
        Assert.Equal(0, api.SetCalls);
    }

    [Fact]
    public async Task Resolve_by_personality_and_by_finding_subject_pick_balanced()
    {
        var h = new SelectSchemeHandler(new FakePowerApi());
        Assert.Equal(Bal, (await h.ResolveAsync(ByPersonality("balanced"), default)).Target["id"]!.GetValue<string>());
        var fromFinding = await h.ResolveAsync(new ActionRequest(SelectSchemeHandler.Id, null, null, "power.active_scheme"), default);
        Assert.Equal(Bal, fromFinding.Target["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Personality_falls_back_to_a_custom_scheme_with_that_personality()
    {
        var custom = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var api = new FakePowerApi { Active = PowerCollector.Balanced };
        api.Schemes.RemoveAll(s => s.Guid == PowerCollector.HighPerformance);
        api.Schemes.Add(new(custom, "Gaming", "high_performance"));
        var r = await new SelectSchemeHandler(api).ResolveAsync(ByPersonality("high_performance"), default);
        Assert.True(r.Eligible);
        Assert.Equal(custom.ToString("D"), r.Target["id"]!.GetValue<string>());
        Assert.Contains("Shorter battery life", r.Tradeoffs);
    }

    [Theory]
    [InlineData("not-a-guid", "invalid_target")]
    [InlineData("99999999-9999-9999-9999-999999999999", "not_installed")]
    public async Task Bad_targets_are_ineligible(string guid, string code)
    {
        var r = await new SelectSchemeHandler(new FakePowerApi()).ResolveAsync(ByGuid(guid), default);
        Assert.False(r.Eligible);
        Assert.Equal(code, r.IneligibleCode);
    }

    [Fact]
    public async Task Ineligible_cases()
    {
        Assert.Equal("already_active", (await new SelectSchemeHandler(new FakePowerApi()).ResolveAsync(ByGuid(High), default)).IneligibleCode);
        Assert.Equal("policy", (await new SelectSchemeHandler(new FakePowerApi { Policy = true }).ResolveAsync(ByGuid(Bal), default)).IneligibleCode);
        Assert.Equal("managed", (await new SelectSchemeHandler(new FakePowerApi { Managed = true }).ResolveAsync(ByGuid(Bal), default)).IneligibleCode);
        Assert.Equal("invalid_target", (await new SelectSchemeHandler(new FakePowerApi()).ResolveAsync(ByPersonality("turbo"), default)).IneligibleCode);
        Assert.Equal("invalid_target", (await new SelectSchemeHandler(new FakePowerApi()).ResolveAsync(new(SelectSchemeHandler.Id, null, null, null), default)).IneligibleCode);
        Assert.Equal("read_failed", (await new SelectSchemeHandler(new FakePowerApi { Active = null }).ResolveAsync(ByGuid(Bal), default)).IneligibleCode);

        var single = new FakePowerApi { Active = PowerCollector.Balanced };
        single.Schemes.RemoveAll(s => s.Guid != PowerCollector.Balanced);
        var r = await new SelectSchemeHandler(single).ResolveAsync(ByPersonality("high_performance"), default);
        Assert.Equal("not_installed", r.IneligibleCode);
        Assert.Equal("ms-settings:powersleep", r.ManualUri);
    }

    [Fact]
    public async Task Execute_verified_reads_back()
    {
        var api = new FakePowerApi();
        var h = new SelectSchemeHandler(api);
        var plan = await h.ResolveAsync(ByGuid(Bal), default);
        var o = await h.ExecuteAsync(Exec(plan), null, default);
        Assert.Equal("verified", o.State);
        Assert.Equal(Bal, o.Observed["activeScheme"]!["id"]!.GetValue<string>());
        Assert.Equal("Balanced is active, read back from Windows.", o.ReceiptText);
        Assert.Equal(1, api.SetCalls);
    }

    [Fact]
    public async Task Execute_failed_when_unchanged_and_never_retries()
    {
        var api = new FakePowerApi { OnSet = _ => (5u, PowerCollector.HighPerformance) }; // access denied, no change
        var h = new SelectSchemeHandler(api);
        var o = await h.ExecuteAsync(Exec(await h.ResolveAsync(ByGuid(Bal), default)), null, default);
        Assert.Equal("failed", o.State);
        Assert.Equal("write_failed", o.Code);
        Assert.Equal(1, api.SetCalls);
    }

    [Fact]
    public async Task Execute_indeterminate_on_third_value_or_unreadable()
    {
        var api = new FakePowerApi { OnSet = _ => (0u, PowerCollector.PowerSaver) };
        var h = new SelectSchemeHandler(api);
        var plan = await h.ResolveAsync(ByGuid(Bal), default);
        Assert.Equal("indeterminate", (await h.ExecuteAsync(Exec(plan), null, default)).State);

        var api2 = new FakePowerApi { OnSet = _ => (0u, null) };
        var h2 = new SelectSchemeHandler(api2);
        Assert.Equal("indeterminate", (await h2.ExecuteAsync(Exec(await h2.ResolveAsync(ByGuid(Bal), default)), null, default)).State);
        Assert.Equal(1, api.SetCalls);
        Assert.Equal(1, api2.SetCalls);
    }

    [Fact]
    public async Task Undo_selects_the_before_scheme_and_round_trips()
    {
        var api = new FakePowerApi();
        var h = new SelectSchemeHandler(api);
        var plan = await h.ResolveAsync(ByGuid(Bal), default);
        await h.ExecuteAsync(Exec(plan), null, default);

        var undo = h.UndoRequest(plan.Target, plan.Before);
        Assert.NotNull(undo);
        Assert.Equal(High, undo!.Target!["schemeGuid"]!.GetValue<string>());
        var undoPlan = await h.ResolveAsync(undo, default);
        Assert.True(undoPlan.Eligible);
        Assert.Equal(["More heat and fan noise", "Shorter battery life"], undoPlan.Tradeoffs.Order());
        Assert.Equal("verified", (await h.ExecuteAsync(Exec(undoPlan), null, default)).State);
        Assert.Equal(PowerCollector.HighPerformance, api.Active);

        Assert.Null(h.UndoRequest(plan.Target, new JsonObject()));
    }

    [Fact]
    public async Task Read_target_matches_before_after_shape()
    {
        var h = new SelectSchemeHandler(new FakePowerApi());
        var state = await h.ReadTargetAsync(new JsonObject { ["schemeGuid"] = Bal }, default);
        Assert.Equal(High, state["activeScheme"]!["id"]!.GetValue<string>());
        Assert.Equal("High performance", state["activeScheme"]!["label"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("ac", true, 80, "Plugged in · 80%")]
    [InlineData("ac", false, null, "Plugged in")]
    [InlineData("battery", true, null, "On battery")]
    public void Context_text(string source, bool battery, int? pct, string expected) =>
        Assert.Equal(expected, SelectSchemeHandler.ContextText(new SourceInfo(source, battery, pct)));

    [Fact]
    public void Module_registers_the_handler()
    {
        Assert.Contains(WindowsModules.Actions, m => m is PowerActionModule);
        var h = Assert.Single(new PowerActionModule().Handlers(new ActionContext(null!, false)));
        Assert.Equal("power.select_scheme", h.ActionId);
        Assert.Equal("none", h.Privilege);
        Assert.True(h.SupportsUndo);
        Assert.Equal("setting", h.Kind);
    }
}

/// <summary>Live: resolve and read only. Never executes (no test may change this PC's power plan).</summary>
[Trait("Category", "Live")]
public class SelectSchemeHandlerLiveTests
{
    [Fact]
    public async Task Resolve_and_read_on_this_pc()
    {
        var h = new SelectSchemeHandler(new WindowsPowerApi());
        var state = await h.ReadTargetAsync(new JsonObject(), default);
        Assert.True(Guid.TryParse(state["activeScheme"]!["id"]!.GetValue<string>(), out _));

        var r = await h.ResolveAsync(new ActionRequest(SelectSchemeHandler.Id, null, new JsonObject { ["personality"] = "balanced" }, null), default);
        if (r.Eligible)
        {
            Assert.Equal("Active power plan", Assert.Single(r.Rows).Label);
            Assert.Equal(state.ToJsonString(), r.Before.ToJsonString());
            Assert.NotEmpty(r.Tradeoffs);
        }
        else Assert.Contains(r.IneligibleCode, new[] { "already_active", "not_installed", "policy", "managed" });

        // Asking for the active scheme itself is always ineligible.
        var active = state["activeScheme"]!["id"]!.GetValue<string>();
        Assert.Equal("already_active", (await h.ResolveAsync(new(SelectSchemeHandler.Id, new JsonObject { ["schemeGuid"] = active }, null, null), default)).IneligibleCode);
    }
}

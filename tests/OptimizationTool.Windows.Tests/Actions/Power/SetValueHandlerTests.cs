using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Power;
using OptimizationTool.Windows.SystemDomain;

namespace OptimizationTool.Windows.Tests.Actions.Power;

/// <summary>power.set_value with the in-memory <see cref="FakePowerApi"/>. Never touches Windows.</summary>
public class SetValueHandlerTests
{
    private static readonly Guid High = PowerCollector.HighPerformance;
    private static readonly Guid Bal = PowerCollector.Balanced;
    private static PowerSettingRow Row(string id) => PowerSettingsCatalog.Rows.Single(r => r.SettingId == id);

    private static (SetValueHandler H, FakePowerApi Api) New()
    {
        var api = new FakePowerApi();
        foreach (var row in PowerSettingsCatalog.Rows)
            foreach (var scheme in new[] { High, Bal })
            {
                api.Values[(scheme, row.Setting, true)] = row.Timeout ? 600u : 1u;
                api.Values[(scheme, row.Setting, false)] = row.Timeout ? 300u : 1u;
            }
        return (new SetValueHandler(api), api);
    }

    private static ActionRequest Req(string id, string? ac = null, string? dc = null)
    {
        var p = new JsonObject { ["settingId"] = id };
        if (ac is not null) p["ac"] = ac;
        if (dc is not null) p["dc"] = dc;
        return new(SetValueHandler.Id, null, p, null);
    }

    private static JsonObject Rt(JsonObject o) => (JsonObject)JsonNode.Parse(o.ToJsonString())!;
    private static ActionExecution Exec(ActionResolution r) => new("plan-1", "hash", Rt(r.Target), Rt(r.Before), Rt(r.After), null);

    [Fact]
    public void The_table_is_the_reviewed_allowlist_and_never_reaches_the_processor()
    {
        Assert.Equal(["power.display_off", "power.sleep", "power.hibernate", "power.lid_close", "power.power_button", "power.usb_selective_suspend", "power.pcie_link_state"],
            PowerSettingsCatalog.Rows.Select(r => r.SettingId));
        Assert.DoesNotContain(PowerSettingsCatalog.Rows, r => r.Subgroup == PowerSettingsCatalog.SubProcessor);
        foreach (var r in PowerSettingsCatalog.Rows)
        {
            Assert.Equal(r.Choices.Count, r.Choices.Select(c => c.Value).Distinct().Count());
            Assert.Equal(r.Choices.Count, r.Choices.Select(c => c.Index).Distinct().Count());
            Assert.All(r.Choices, c => Assert.True(c.Index <= r.MaxRestore));
            Assert.True(ResourceKeys.Valid(r.Resource(High)));
            Assert.True(ResourceKeys.Conflict(r.Resource(High), SelectSchemeHandler.Resource(High))); // a plan switch excludes a value change on that plan
            Assert.False(ResourceKeys.Conflict(r.Resource(High), SelectSchemeHandler.Resource(Bal)));
            foreach (var word in new[] { "processor", "cpu", "boost", "frequency", "parking", "throttl", "voltage", "fan", "cooling" })
                Assert.DoesNotContain(word, r.SettingId + r.Label, StringComparison.OrdinalIgnoreCase);
        }
        var (h, _) = New();
        var schema = h.ParameterSchema!;
        Assert.Equal(PowerSettingsCatalog.Rows.Count, schema["oneOf"]!.AsArray().Count);
        Assert.Contains(WindowsModules.Actions.SelectMany(m => m.Handlers(new ActionContext(null!, false))), x => x.ActionId == SetValueHandler.Id);
    }

    [Fact]
    public async Task Sets_one_side_applies_the_plan_reads_back_and_undo_restores_both()
    {
        var (h, api) = New();
        Assert.Equal("already_set", h.Resolve(Req("power.display_off", dc: "5")).IneligibleCode); // 5 minutes = 300 s already

        var r = h.Resolve(Req("power.display_off", ac: "never", dc: "2"));
        Assert.True(r.Eligible, r.IneligibleMessage);
        Assert.Equal(High.ToString("D"), r.Target["scheme"]!.GetValue<string>());
        Assert.Equal(new JsonObject { ["scheme"] = High.ToString("D"), ["ac"] = 600L, ["dc"] = 300L }.ToJsonString(), r.Before.ToJsonString());
        Assert.Equal(new JsonObject { ["scheme"] = High.ToString("D"), ["ac"] = 0L, ["dc"] = 120L }.ToJsonString(), r.After.ToJsonString());
        Assert.Equal(2, r.Rows.Count);
        Assert.Equal(("10 minutes", "Never"), (r.Rows[0].Before, r.Rows[0].After));
        Assert.Equal(("5 minutes", "2 minutes"), (r.Rows[1].Before, r.Rows[1].After));
        Assert.Equal($"power:{High.ToString("D").ToUpperInvariant()}:3C0BC021-C8A8-4E07-A973-6B14CBCB2B7E", Assert.Single(r.Resources!));

        var setCalls = api.SetCalls;
        var o = h.Execute(Exec(r));
        Assert.Equal("verified", o.State);
        Assert.Equal(0u, api.Values[(High, Row("power.display_off").Setting, true)]);
        Assert.Equal(120u, api.Values[(High, Row("power.display_off").Setting, false)]);
        Assert.Equal(setCalls + 1, api.SetCalls); // re-activated the same plan to apply
        Assert.Equal(High, api.Active);
        Assert.True(JsonNode.DeepEquals(o.Observed, await h.ReadTargetAsync(Rt(r.Target), default)));

        var undo = h.UndoRequest(Rt(r.Target), Rt(r.Before))!;
        Assert.True(undo.Undo);
        var u = h.Resolve(undo);
        Assert.True(u.Eligible, u.IneligibleMessage);
        Assert.Equal("verified", h.Execute(Exec(u)).State);
        Assert.Equal(600u, api.Values[(High, Row("power.display_off").Setting, true)]);
        Assert.Equal(300u, api.Values[(High, Row("power.display_off").Setting, false)]);
    }

    [Fact]
    public void Undo_restores_a_value_outside_the_choices_but_only_when_the_engine_builds_it()
    {
        var (h, api) = New();
        var lid = Row("power.lid_close");
        api.Values[(High, lid.Setting, true)] = 4; // not one of the listed choices
        var r = h.Resolve(Req("power.lid_close", ac: "sleep"));
        Assert.True(r.Eligible);
        Assert.Equal("Other value (4)", r.Rows[0].Before);
        Assert.Equal("verified", h.Execute(Exec(r)).State);
        var undo = h.UndoRequest(Rt(r.Target), Rt(r.Before))!;
        Assert.True(h.Resolve(undo).Eligible);
        // The same restore from a caller (Undo not set by the engine) is refused.
        Assert.Equal("invalid_arguments", h.Resolve(undo with { Undo = false }).IneligibleCode);
        // And a restore beyond the setting's range is refused even for the engine.
        var bad = undo with { Parameters = new JsonObject { ["settingId"] = "power.lid_close", ["restore"] = new JsonObject { ["ac"] = 99, ["dc"] = 1 } } };
        Assert.Equal("invalid_arguments", h.Resolve(bad).IneligibleCode);
    }

    [Fact]
    public void Ineligible_paths_and_hostile_inputs()
    {
        var (h, api) = New();
        Assert.Equal("invalid_arguments", h.Resolve(Req("power.processor_max")).IneligibleCode);
        Assert.Equal("invalid_arguments", h.Resolve(Req("POWER.DISPLAY_OFF", ac: "5")).IneligibleCode);
        Assert.Equal("invalid_arguments", h.Resolve(Req("power.display_off")).IneligibleCode);            // neither side
        Assert.Equal("invalid_arguments", h.Resolve(Req("power.display_off", ac: "7")).IneligibleCode);    // not a listed step
        Assert.Equal("invalid_arguments", h.Resolve(Req("power.display_off", ac: " 5")).IneligibleCode);
        Assert.Equal("invalid_arguments", h.Resolve(Req("power.lid_close", ac: "2")).IneligibleCode);      // an index, not a choice
        Assert.Equal("invalid_arguments", h.Resolve(new ActionRequest(SetValueHandler.Id, null,
            new JsonObject { ["settingId"] = "power.sleep", ["ac"] = "5", ["subgroup"] = PowerSettingsCatalog.SubProcessor.ToString() }, null)).IneligibleCode);
        Assert.Equal("invalid_arguments", h.Resolve(new ActionRequest(SetValueHandler.Id, null,
            new JsonObject { ["settingId"] = "power.sleep", ["ac"] = 5 }, null)).IneligibleCode);
        Assert.Equal("already_set", h.Resolve(Req("power.sleep", ac: "10", dc: "5")).IneligibleCode);

        api.ManagedSettings.Add(Row("power.sleep").Setting);
        Assert.Equal("managed", h.Resolve(Req("power.sleep", ac: "30")).IneligibleCode);
        api.ManagedSettings.Clear();
        api.Policy = true;
        Assert.Equal("managed", h.Resolve(Req("power.sleep", ac: "30")).IneligibleCode);
        api.Policy = false;

        api.Values.Remove((High, Row("power.lid_close").Setting, true));
        Assert.Equal("unsupported", h.Resolve(Req("power.lid_close", ac: "sleep")).IneligibleCode);
        api.Active = null;
        Assert.Equal("read_failed", h.Resolve(Req("power.sleep", ac: "30")).IneligibleCode);
        Assert.Equal(0, api.ValueWrites);
    }

    [Fact]
    public void A_plan_switch_between_preview_and_approval_is_refused_and_never_reverted()
    {
        var (h, api) = New();
        var r = h.Resolve(Req("power.sleep", ac: "30"));
        api.Active = Bal;
        Assert.Equal("changed_since", h.Resolve(new ActionRequest(SetValueHandler.Id, Rt(r.Target), Req("power.sleep", ac: "30").Parameters, null) { Recheck = true }).IneligibleCode);
        // Even if it ran, the write goes to the approved plan and the other active plan is never switched away.
        var calls = api.SetCalls;
        Assert.Equal("verified", h.Execute(Exec(r)).State);
        Assert.Equal(Bal, api.Active);
        Assert.Equal(calls, api.SetCalls);
    }

    [Fact]
    public void A_write_that_does_not_stick_is_failed()
    {
        var (h, api) = New();
        var r = h.Resolve(Req("power.usb_selective_suspend", ac: "disabled"));
        api.OnWrite = _ => 5u;
        var o = h.Execute(Exec(r));
        Assert.Equal("failed", o.State);
        Assert.Equal("write_failed", o.Code);
    }
}

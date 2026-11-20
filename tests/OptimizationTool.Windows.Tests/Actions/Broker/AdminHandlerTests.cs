using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Engine.Actions.Scripts;
using OptimizationTool.Windows.Actions.Broker;
using OptimizationTool.Windows.Actions.RestorePoint;
using OptimizationTool.Windows.Actions.Settings;
using OptimizationTool.Windows.Actions.Startup;
using OptimizationTool.Windows.Tests.Actions.Settings;
using OptimizationTool.Windows.Tests.Actions.Startup;

namespace OptimizationTool.Windows.Tests.Actions.Broker;

/// <summary>
/// The first administrator handlers on the broker registry (m9 §3.2, §3.5), host half and broker half, with fakes only:
/// a fake registry, fake startup files, a fake restore-point system and a fake broker. Nothing is written to this PC.
/// </summary>
public sealed class AdminHandlerTests
{
    private static readonly RegLocation FastStartup = SettingsCatalog.Find("startup.fast_startup")!.Location;

    private static BrokerStepRequest StepOf(int n, string actionId, string version, ActionResolution r, JsonObject? parameters, ElevatedUndoOf? undoOf = null) =>
        BrokerStepRequest.From(new ElevatedStep(n, actionId, version, r.Target, r.Before, r.After, parameters, undoOf));

    // ---------------------------------------------------------------- setting.set machine rows

    [Fact]
    public void Fast_startup_is_a_machine_row_resolved_as_administrator_on_both_sides()
    {
        var reg = new FakeRegistryStore();
        reg.Set(FastStartup, RegData.Dword(1));
        var host = new SetSettingHandler(reg, new NoNotify(), () => 26100);
        var p = new JsonObject { ["settingId"] = "startup.fast_startup", ["value"] = "off" };
        var r = host.Resolve(new ActionRequest(SetSettingHandler.Id, null, p, null));
        Assert.True(r.Eligible);
        Assert.Equal("admin", r.Privilege);
        Assert.Equal("Everyone on this PC", r.Context.Single().Value);
        Assert.Equal([@"reg:64:HKLM\SYSTEM\CURRENTCONTROLSET\CONTROL\SESSION MANAGER\POWER\HIBERBOOTENABLED"], r.Resources);
        Assert.True(((IActionHandler)host).CanElevate);

        // The broker half re-resolves the same plan from live state, writes HKLM and reads back.
        var broker = new MachineSettingBrokerHandler(reg, () => 26100);
        var step = StepOf(1, SetSettingHandler.Id, host.Version, r, p);
        var again = broker.Resolve(step);
        Assert.Equal(step.ContentHash, BrokerContent.Hash(broker.ActionId, broker.Version, again.Target, again.Before, again.After, p));
        var outcome = broker.WriteAndReadBack(again, step, CancellationToken.None);
        Assert.Equal("verified", outcome.State);
        Assert.Equal(0L, reg.Read(FastStartup).Data!.GetValue<long>());

        // Undo restores the recorded preimage (engine-built restore), still administrator.
        var undo = host.UndoRequest(r.Target, r.Before)!;
        var ur = host.Resolve(undo);
        Assert.True(ur.Eligible);
        Assert.Equal("admin", ur.Privilege);
        Assert.True(broker.IsUndo(StepOf(1, SetSettingHandler.Id, host.Version, ur, undo.Parameters)));
    }

    [Fact]
    public void Machine_row_policy_and_user_rows_in_the_broker()
    {
        var reg = new FakeRegistryStore();
        reg.Policies.Add(FakeRegistryStore.K(new RegLocation("HKLM", 64, @"SOFTWARE\Policies\Microsoft\Windows\System", "HiberbootEnabled")));
        var host = new SetSettingHandler(reg, new NoNotify(), () => 26100);
        Assert.Equal("managed", host.Resolve(new ActionRequest(SetSettingHandler.Id, null, new JsonObject { ["settingId"] = "startup.fast_startup", ["value"] = "off" }, null)).IneligibleCode);

        // A user-scope row never runs elevated: the broker half refuses it.
        var userParams = new JsonObject { ["settingId"] = "appearance.transparency", ["value"] = "off" };
        var user = host.Resolve(new ActionRequest(SetSettingHandler.Id, null, userParams, null));
        Assert.True(user.Eligible);
        Assert.Null(user.Privilege);
        var refused = new MachineSettingBrokerHandler(reg, () => 26100).Resolve(StepOf(1, SetSettingHandler.Id, "1.0.0", user, userParams));
        Assert.Equal("not_admin_scope", refused.IneligibleCode);
    }

    // ---------------------------------------------------------------- startup.disable all-users entries

    private const string Tool = @"Q:\Program Files\Contoso\agent.exe";
    private const string Hash = "1111111111111111111111111111111111111111111111111111111111111111";

    private static TargetEvidence Ev(string source, string location, string localName, string? view = "64") =>
        new("ev-1", StartupDisableHandler.CollectorId, $"startup.{source}.{location}.0badc0de", new JsonObject
        {
            ["source"] = source, ["location"] = location, ["view"] = view, ["name"] = "Agent", ["enabled"] = true, ["entryKey"] = StartupKeys.Of(localName),
        }, "2026-11-10T08:00:00Z");

    private static (StartupDisableHandler H, FakeRegistryStore Reg, FakeStartupFiles Files) Startup()
    {
        var reg = new FakeRegistryStore();
        var files = new FakeStartupFiles();
        files.Files[Tool] = Hash;
        reg.Set(StartupDisableHandler.Run("machine_run", "Contoso Agent"), RegData.String($"\"{Tool}\" /bg"));
        return (new StartupDisableHandler(reg, files), reg, files);
    }

    [Fact]
    public void All_users_run_entry_is_administrator_scope_and_the_broker_writes_HKLM()
    {
        var (h, reg, files) = Startup();
        var r = h.Resolve(new ActionRequest(StartupDisableHandler.Id, null, null, null) { Evidence = Ev("run", "hklm", "Contoso Agent") });
        Assert.True(r.Eligible, r.IneligibleMessage);
        Assert.Equal("admin", r.Privilege);
        Assert.Equal("machine_run", r.Target["kind"]!.GetValue<string>());
        Assert.All(r.Resources!, k => Assert.StartsWith(@"reg:64:HKLM\", k));
        Assert.Equal("Startup apps for everyone on this PC", r.Context[0].Value);

        var broker = new MachineStartupBrokerHandler(reg, files);
        var step = StepOf(1, StartupDisableHandler.Id, h.Version, r, null);
        var live = broker.Resolve(step);
        Assert.True(live.Eligible);
        Assert.Null(broker.Protect(live));
        Assert.Equal(step.ContentHash, BrokerContent.Hash(broker.ActionId, broker.Version, live.Target, live.Before, live.After, null));
        Assert.Equal("verified", broker.WriteAndReadBack(live, step, CancellationToken.None).State);
        var sw = reg.Read(StartupDisableHandler.Approved("machine_run", "Contoso Agent"));
        Assert.Equal(3, Convert.FromBase64String(sw.Data!.GetValue<string>())[0]); // Explorer's "off"
        Assert.False(reg.Read(StartupDisableHandler.Approved("run", "Contoso Agent")).Present); // nothing per-user

        // Undo needs the recorded fingerprint and re-enables with runs_code, still administrator.
        var undo = h.UndoRequest(r.Target, r.Before)!;
        var ur = h.Resolve(undo);
        Assert.True(ur.Eligible);
        Assert.Equal("admin", ur.Privilege);
        Assert.Contains(ActionFlags.RunsCode, ur.Flags!);
        Assert.True(broker.IsUndo(StepOf(1, StartupDisableHandler.Id, h.Version, ur, null)));
    }

    [Fact]
    public void All_users_rundll32_shortcut_undo_in_the_broker_is_refused_when_only_the_dll_changed()
    {
        const string rundll = @"Q:\Windows\System32\rundll32.exe", dll = @"Q:\Program Files\Contoso\helper.dll";
        var (h, reg, files) = Startup();
        var lnk = Path.Combine(FakeStartupFiles.CommonFolder, "Contoso.lnk");
        files.Files[lnk] = Hash;
        files.Files[rundll] = Hash;
        files.Files[dll] = Hash;
        files.Shortcuts[lnk] = new ShortcutInfo(rundll, $"\"{dll}\",Start", null);
        var r = h.Resolve(new ActionRequest(StartupDisableHandler.Id, null, null, null) { Evidence = Ev("folder", "common", "Contoso.lnk") });
        Assert.True(r.Eligible, r.IneligibleMessage);
        Assert.Equal(dll, r.Before["fingerprint"]!["payload"]!.GetValue<string>());
        var broker = new MachineStartupBrokerHandler(reg, files);
        var off = broker.Resolve(StepOf(1, StartupDisableHandler.Id, h.Version, r, null));
        Assert.Equal("verified", broker.WriteAndReadBack(off, StepOf(1, StartupDisableHandler.Id, h.Version, r, null), CancellationToken.None).State);

        var ur = h.Resolve(h.UndoRequest(r.Target, r.Before)!);
        Assert.True(ur.Eligible, ur.IneligibleMessage);
        var undoStep = StepOf(1, StartupDisableHandler.Id, h.Version, ur, null);
        Assert.True(broker.Resolve(undoStep).Eligible);
        files.Files[dll] = "2222222222222222222222222222222222222222222222222222222222222222"; // only the DLL changed
        Assert.Equal("changed_since", broker.Resolve(undoStep).IneligibleCode);
    }

    [Fact]
    public void All_users_entries_policy_32bit_view_hardware_tools_and_user_entries_in_the_broker()
    {
        var (h, reg, files) = Startup();
        reg.Policies.Add(FakeRegistryStore.K(new RegLocation("HKLM", 64, @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "DisableLocalMachineRun")));
        Assert.Equal("managed", h.Resolve(new ActionRequest(StartupDisableHandler.Id, null, null, null) { Evidence = Ev("run", "hklm", "Contoso Agent") }).IneligibleCode);
        Assert.Equal("unsupported", h.Resolve(new ActionRequest(StartupDisableHandler.Id, null, null, null) { Evidence = Ev("run", "hklm", "Contoso Agent", view: "32") }).IneligibleCode);

        var (h2, reg2, files2) = Startup();
        reg2.Set(StartupDisableHandler.Run("machine_run", "Afterburner"), RegData.String(@"""Q:\Program Files (x86)\MSI Afterburner\MSIAfterburner.exe"" /s"));
        Assert.Equal("hardware_limit", h2.Resolve(new ActionRequest(StartupDisableHandler.Id, null, null, null) { Evidence = Ev("run", "hklm", "Afterburner") }).IneligibleCode);

        // The broker's own list, even for a resolution the host let through.
        var fake = new ActionResolution(true, new JsonObject { ["label"] = "Afterburner" }, new JsonObject { ["fingerprint"] = new JsonObject { ["image"] = @"Q:\x\MSIAfterburner.exe" } }, [], [], [], [], null);
        Assert.Equal("hardware_limit", new MachineStartupBrokerHandler(reg2, files2).Protect(fake)!.Code);

        // m9 §3.7 gap closed: the broker reads the image's publisher itself; a security product's entry is refused there even
        // when the host's resolution carried no publisher at all.
        var (h3, reg3, files3) = Startup();
        var r3 = h3.Resolve(new ActionRequest(StartupDisableHandler.Id, null, null, null) { Evidence = Ev("run", "hklm", "Contoso Agent") });
        Assert.True(r3.Eligible);
        var b3 = new MachineStartupBrokerHandler(reg3, files3);
        var live3 = b3.Resolve(StepOf(1, StartupDisableHandler.Id, h3.Version, r3, null));
        Assert.Null(b3.Protect(live3));
        files3.Publishers[Tool] = "Sophos Limited";
        Assert.Equal("protected", b3.Protect(live3)!.Code);
        // The shared handler reads the image's publisher itself too, so the broker's own resolution already refuses it.
        Assert.Equal("protected", b3.Resolve(StepOf(1, StartupDisableHandler.Id, h3.Version, r3, null)).IneligibleCode);

        // A per-user entry never runs elevated.
        reg.Policies.Clear();
        reg.Set(StartupDisableHandler.Run("Notes"), RegData.String($"\"{Tool}\""));
        var user = h.Resolve(new ActionRequest(StartupDisableHandler.Id, null, null, null) { Evidence = Ev("run", "hkcu", "Notes") });
        Assert.True(user.Eligible);
        Assert.Equal("not_admin_scope", new MachineStartupBrokerHandler(reg, files).Resolve(StepOf(1, StartupDisableHandler.Id, "1.0.0", user, null)).IneligibleCode);
    }

    // ---------------------------------------------------------------- restore_point.create

    private sealed class FakeSystem : IRestorePointSystem
    {
        public string? Off;
        public RestorePointResult Result = new("created", new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero));
        public int Creates;
        public string? Unavailable() => Off;
        public RestorePointResult Create(string description) { Creates++; return Result; }
    }

    [Fact]
    public async Task Restore_point_host_handler_is_administrator_provider_and_reads_availability()
    {
        var sys = new FakeSystem();
        var h = new RestorePointHandler(sys);
        Assert.Equal(("admin", "provider", false), (h.Privilege, h.Kind, h.SupportsUndo));
        var r = await h.ResolveAsync(new ActionRequest(RestorePointPlan.ActionId, null, null, null), CancellationToken.None);
        Assert.True(r.Eligible);
        Assert.Equal([RestorePointPlan.Resource], r.Resources);
        sys.Off = RestorePointPlan.ProtectionOff;
        Assert.Equal("protection_off", (await h.ResolveAsync(new ActionRequest(RestorePointPlan.ActionId, null, null, null), CancellationToken.None)).IneligibleCode);
        Assert.Equal("invalid_arguments", (await h.ResolveAsync(new ActionRequest(RestorePointPlan.ActionId, null, new JsonObject { ["x"] = 1 }, null), CancellationToken.None)).IneligibleCode);
        Assert.Equal("failed", (await h.ExecuteAsync(new ActionExecution("p", "h", r.Target, r.Before, r.After, null), null, CancellationToken.None)).State); // never in the host
        Assert.Equal(0, sys.Creates);
    }

    [Theory]
    [InlineData("created", "completed", "created")]
    [InlineData("reused", "completed", "reused")]
    [InlineData("failed", "failed", "failed")]
    public void Restore_point_broker_handler_outcomes(string windows, string state, string result)
    {
        var sys = new FakeSystem { Result = new RestorePointResult(windows, windows == "failed" ? null : DateTimeOffset.UnixEpoch) };
        var b = new RestorePointBrokerHandler(sys);
        var r = RestorePointPlan.Resolve(null);
        var step = StepOf(1, RestorePointPlan.ActionId, b.Version, r, null);
        Assert.True(b.Resolve(step).Eligible);
        var o = b.WriteAndReadBack(b.Resolve(step), step, CancellationToken.None);
        Assert.Equal(state, o.State);
        Assert.Equal(result, o.Observed["result"]!.GetValue<string>());
        Assert.Equal(1, sys.Creates);
    }

    // ---------------------------------------------------------------- the scripts' pre-step through the broker (m9 §5.2)

    private sealed class FakeBroker(Func<ElevatedRequest, ElevatedResult> answer) : IElevation
    {
        public List<ElevatedRequest> Requests { get; } = [];
        public Task<ElevatedResult> RunAsync(ElevatedRequest request, CancellationToken stop, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
        public ElevatedJournal? Journal(string planId) => null;
    }

    private static ElevatedResult One(string state, JsonObject observed, string? code = null) => new([(1, new ActionOutcome(state, observed, "x", code))], "rcpt-0000000000000001", true);

    [Theory]
    [InlineData("completed", "created", "created")]
    [InlineData("completed", "reused", "reused")]
    [InlineData("failed", "failed", "failed")]
    [InlineData("declined", null, "declined")]
    [InlineData("indeterminate", null, "failed")]
    public async Task Script_pre_step_runs_restore_point_create_through_the_broker(string state, string? result, string expected)
    {
        var observed = new JsonObject { ["result"] = result, ["at"] = result is "created" or "reused" ? "2026-11-12T10:00:00Z" : null };
        var broker = new FakeBroker(_ => One(state, observed, state == "declined" ? "uac_cancelled" : null));
        var step = new BrokerRestorePointStep(broker, new FakeSystem());
        Assert.Equal(new RestorePointAvailability(true, null), step.Availability());
        var outcome = await step.CreateAsync(CancellationToken.None);
        Assert.Equal(expected, outcome.Result);
        Assert.Equal(expected is "created" or "reused", outcome.At is not null);
        var sent = Assert.Single(Assert.Single(broker.Requests).Steps);
        Assert.Equal((RestorePointPlan.ActionId, true), (sent.ActionId, sent.Long));
    }

    [Fact]
    public async Task Script_pre_step_with_System_Protection_off_is_unavailable_and_never_asks_the_broker()
    {
        var broker = new FakeBroker(_ => throw new InvalidOperationException());
        var step = new BrokerRestorePointStep(broker, new FakeSystem { Off = RestorePointPlan.ProtectionOff });
        Assert.Equal(new RestorePointAvailability(false, "protection_off"), step.Availability());
        Assert.Equal("failed", (await step.CreateAsync(CancellationToken.None)).Result);
        Assert.Empty(broker.Requests);
    }

    private sealed class NoNotify : ISettingNotifier
    {
        public void Notify(string kind) { }
    }
}

using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Broker;
using OptimizationTool.Windows.Actions.Startup;
using OptimizationTool.Windows.Actions.Tasks;

namespace OptimizationTool.Windows.Tests.Actions.Admin;

/// <summary>task.disable (m9 §3.2), host half and broker half, with a fake Task Scheduler and fake file reads.</summary>
public sealed class TaskDisableTests
{
    private const string Exe = @"Q:\Program Files\Contoso\updater.exe";
    private const string Path_ = @"\Contoso\Updater";

    private static TaskInfo Task(string path = Path_, bool enabled = true, string? exe = Exe, string kind = "exec", string args = "/check") =>
        new(path, enabled, "S-1-5-18", 1, 5, [new TaskActionInfo(kind, exe, args, null, kind == "exec" ? exe : null)], ["logon", "daily"]);

    private static (TaskDisableHandler H, FakeScheduler S, FakeFiles F) New(TaskInfo? t = null)
    {
        var s = new FakeScheduler();
        var f = new FakeFiles();
        f.Hashes[Exe] = Admin.Hash;
        var task = t ?? Task();
        s.Tasks[task.Path] = task;
        return (new TaskDisableHandler(s, f), s, f);
    }

    private static TargetEvidence Ev(string path = Path_, string location = @"\Contoso", string source = "task") =>
        new("ev-1", "win.security.startup", $"startup.{source}.0badc0de", new JsonObject
        {
            ["source"] = source, ["location"] = location, ["name"] = "Updater", ["entryKey"] = StartupKeys.Of(path),
        }, "2026-11-12T08:00:00Z");

    private static ActionRequest Fresh(TargetEvidence? ev = null) => new(TaskDisableHandler.Id, null, null, null) { Evidence = ev ?? Ev() };

    [Fact]
    public async Task Eligible_disable_records_actions_principal_triggers_and_hashes()
    {
        var (h, _, _) = New();
        var r = h.Resolve(Fresh());
        Assert.True(r.Eligible, r.IneligibleMessage);
        Assert.Equal(("admin", "admin"), (h.Privilege, r.Privilege));
        Assert.Equal(["task:" + Path_.ToUpperInvariant()], r.Resources);
        Assert.Null(r.Flags);
        Assert.True(r.Before["enabled"]!.GetValue<bool>());
        Assert.False(r.After["enabled"]!.GetValue<bool>());
        var fp = r.Before["fingerprint"]!.AsObject();
        Assert.Equal(Path_, fp.Str("path"));
        var action = fp["actions"]![0]!.AsObject();
        Assert.Equal(string.Join('|', Exe, "/check", Admin.Hash), string.Join('|', action.Str("path"), action.Str("arguments"), action.Str("imageSha256")));
        Assert.Equal("S-1-5-18", fp["principal"]!.AsObject().Str("userId"));
        Assert.Equal(["logon", "daily"], fp["triggers"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Equal("Updater", r.Target.Str("label"));
        Assert.Equal("failed", (await h.ExecuteAsync(Admin.Exec(r), null, CancellationToken.None)).State); // never in the host
    }

    [Fact]
    public void Ineligible_paths_microsoft_protected_hardware_already_set_not_found_unknown()
    {
        // \Microsoft\ tasks are never listed or touched.
        var (ms, _, _) = New(Task(@"\Microsoft\Windows\Defrag\ScheduledDefrag"));
        Assert.Equal("protected", ms.Resolve(Fresh(Ev(@"\Microsoft\Windows\Defrag\ScheduledDefrag", @"\Microsoft\Windows\Defrag"))).IneligibleCode);
        // A task that runs a security product (publisher read live).
        var (sec, _, f) = New();
        f.Publishers[Exe] = "Malwarebytes Inc";
        Assert.Equal("protected", sec.Resolve(Fresh()).IneligibleCode);
        // A hardware tool's task.
        var (hw, s, hf) = New(Task(@"\ASUS\ArmouryCrate Startup", exe: @"Q:\Program Files\ASUS\ArmouryCrate.exe"));
        hf.Hashes[@"Q:\Program Files\ASUS\ArmouryCrate.exe"] = Admin.Hash;
        Assert.Equal("hardware_limit", hw.Resolve(Fresh(Ev(@"\ASUS\ArmouryCrate Startup", @"\ASUS"))).IneligibleCode);
        // Already off; gone; not a task; no evidence.
        Assert.Equal("already_set", New(Task(enabled: false)).H.Resolve(Fresh()).IneligibleCode);
        var (gone, gs, _) = New();
        gs.Tasks.Clear();
        Assert.Equal("not_found", gone.Resolve(Fresh()).IneligibleCode);
        Assert.Equal(ActionService.UnknownTarget, New().H.Resolve(Fresh(Ev(source: "service"))).IneligibleCode);
        Assert.Equal(ActionService.UnknownTarget, New().H.Resolve(new ActionRequest(TaskDisableHandler.Id, null, null, null)).IneligibleCode);
        Assert.Equal("invalid_arguments", New().H.Resolve(new ActionRequest(TaskDisableHandler.Id, null, new JsonObject { ["x"] = 1 }, null) { Evidence = Ev() }).IneligibleCode);
        _ = s;
    }

    [Fact]
    public void Broker_disables_and_undo_reenables_only_while_the_fingerprint_matches()
    {
        var (h, s, f) = New();
        var r = h.Resolve(Fresh());
        var broker = new TaskBrokerHandler(s, f);
        var step = Admin.Step(TaskDisableHandler.Id, r, null);
        var live = broker.Resolve(step);
        Assert.True(live.Eligible);
        Assert.Null(broker.Protect(live));
        Assert.Equal(step.ContentHash, BrokerContent.Hash(broker.ActionId, broker.Version, live.Target, live.Before, live.After, null));
        Assert.Equal("verified", broker.WriteAndReadBack(live, step, CancellationToken.None).State);
        Assert.Equal(["off:" + Path_], s.Writes);

        var undo = h.UndoRequest(r.Target, r.Before)!;
        var ur = h.Resolve(undo);
        Assert.True(ur.Eligible, ur.IneligibleMessage);
        Assert.Equal([ActionFlags.RunsCode], ur.Flags);
        var ustep = Admin.Step(TaskDisableHandler.Id, ur, null);
        Assert.True(broker.IsUndo(ustep));

        // The action's arguments changed: a different launch configuration, undo refused.
        s.Tasks[Path_] = Task(enabled: false, args: "/install evil");
        Assert.Equal("changed_since", h.Resolve(undo).IneligibleCode);
        Assert.Equal("changed_since", broker.Resolve(ustep).IneligibleCode);
        // The program's bytes changed.
        s.Tasks[Path_] = Task(enabled: false);
        f.Hashes[Exe] = Admin.Hash2;
        Assert.Equal("changed_since", h.Resolve(undo).IneligibleCode);
        // Unchanged: re-enabled, read back.
        f.Hashes[Exe] = Admin.Hash;
        Assert.Equal("verified", broker.WriteAndReadBack(broker.Resolve(ustep), ustep, CancellationToken.None).State);
        Assert.True(s.Tasks[Path_].Enabled);
    }

    [Fact]
    public void Task_running_rundll32_binds_the_dll_and_undo_is_refused_when_only_the_dll_changed()
    {
        const string rundll = @"Q:\Windows\System32\rundll32.exe", dll = @"Q:\Program Files\Contoso\helper.dll";
        var (h, s, f) = New(Task(exe: rundll, args: $"\"{dll}\",Update"));
        f.Hashes[rundll] = Admin.Hash;
        f.Hashes[dll] = Admin.Hash;
        var r = h.Resolve(Fresh());
        Assert.True(r.Eligible, r.IneligibleMessage);
        Assert.Equal(dll, r.Before["fingerprint"]!["actions"]![0]!.AsObject().Str("payload"));
        var broker = new TaskBrokerHandler(s, f);
        var step = Admin.Step(TaskDisableHandler.Id, r, null);
        Assert.Equal("verified", broker.WriteAndReadBack(broker.Resolve(step), step, CancellationToken.None).State);
        var ur = h.Resolve(h.UndoRequest(r.Target, r.Before)!);
        Assert.True(ur.Eligible, ur.IneligibleMessage);
        var ustep = Admin.Step(TaskDisableHandler.Id, ur, null);
        f.Hashes[dll] = Admin.Hash2; // only the DLL changed
        Assert.Equal("changed_since", broker.Resolve(ustep).IneligibleCode);

        // A host whose arguments are a command: no payload can be named, no undo.
        var (h2, _, f2) = New(Task(exe: @"Q:\Windows\System32\cmd.exe", args: "/c updater.exe"));
        f2.Hashes[@"Q:\Windows\System32\cmd.exe"] = Admin.Hash;
        Assert.False(h2.Resolve(Fresh()).UndoSupported);
    }

    [Fact]
    public void Undo_is_refused_when_only_the_group_principal_changed_or_wasnt_recorded()
    {
        var (h, s, f) = New(Task() with { UserId = null, GroupId = "S-1-5-32-545" }); // runs for the Users group
        var r = h.Resolve(Fresh());
        Assert.Equal("S-1-5-32-545", r.Before["fingerprint"]!["principal"]!.AsObject().Str("groupId"));
        var broker = new TaskBrokerHandler(s, f);
        var step = Admin.Step(TaskDisableHandler.Id, r, null);
        Assert.Equal("verified", broker.WriteAndReadBack(broker.Resolve(step), step, CancellationToken.None).State);
        var undo = h.UndoRequest(r.Target, r.Before)!;
        var ustep = Admin.Step(TaskDisableHandler.Id, h.Resolve(undo), null);
        Assert.True(broker.Resolve(ustep).Eligible);

        s.Tasks[Path_] = s.Tasks[Path_] with { GroupId = "S-1-5-32-544" }; // re-registered for Administrators: nothing else changed
        Assert.Equal("changed_since", h.Resolve(undo).IneligibleCode);
        Assert.Equal("changed_since", broker.Resolve(ustep).IneligibleCode);

        // A principal that couldn't be read in full, or a receipt from before groupId was recorded: no undo.
        var (h2, _, _) = New(Task() with { PrincipalRead = false });
        Assert.False(h2.Resolve(Fresh()).UndoSupported);
        var old = (JsonObject)r.Before.DeepClone();
        old["fingerprint"]!["principal"]!.AsObject().Remove("groupId");
        Assert.Null(h.UndoRequest(r.Target, old));
    }

    [Fact]
    public void A_program_change_between_the_brokers_resolve_and_its_write_means_no_write()
    {
        var (h, s, f) = New();
        var r = h.Resolve(Fresh());
        var broker = new TaskBrokerHandler(s, f);
        var step = Admin.Step(TaskDisableHandler.Id, r, null);
        Assert.Equal("verified", broker.WriteAndReadBack(broker.Resolve(step), step, CancellationToken.None).State);
        var ustep = Admin.Step(TaskDisableHandler.Id, h.Resolve(h.UndoRequest(r.Target, r.Before)!), null);
        var live = broker.Resolve(ustep);
        Assert.True(live.Eligible, live.IneligibleMessage);
        f.Hashes[Exe] = Admin.Hash2;
        var o = broker.WriteAndReadBack(live, ustep, CancellationToken.None);
        Assert.Equal(("failed", "changed_since"), (o.State, o.Code));
        Assert.Equal(["off:" + Path_], s.Writes);
        Assert.False(s.Tasks[Path_].Enabled);
    }

    [Fact]
    public void No_program_hash_or_a_com_handler_means_no_undo_and_failed_writes_are_reported()
    {
        var (h, s, f) = New();
        f.Hashes[Exe] = null;
        var r = h.Resolve(Fresh());
        Assert.False(r.UndoSupported);
        Assert.Null(h.UndoRequest(r.Target, r.Before));
        var (com, _, _) = New(Task(kind: "com_handler", exe: null));
        Assert.False(com.Resolve(Fresh()).UndoSupported);

        f.Hashes[Exe] = Admin.Hash;
        r = h.Resolve(Fresh());
        s.Ignore = true;
        Assert.Equal("failed", h.Execute(Admin.Exec(r)).State);
    }

    [Fact]
    public void Broker_rechecks_protection_even_when_the_host_let_it_through()
    {
        var (_, s, f) = New();
        var broker = new TaskBrokerHandler(s, f);
        var ms = new ActionResolution(true, TaskDisableHandler.Target(@"\Microsoft\Windows\X", "X"), new JsonObject(), [], [], [], [], null);
        Assert.Equal("protected", broker.Protect(ms)!.Code);
        f.Publishers[Exe] = "Sophos Limited";
        var r = new ActionResolution(true, TaskDisableHandler.Target(Path_, "Updater"), new JsonObject(), [], [], [], [], null);
        Assert.Equal("protected", broker.Protect(r)!.Code);
        var hw = new ActionResolution(true, TaskDisableHandler.Target(@"\Vendor\Fan", "Fan"), new JsonObject
        {
            ["fingerprint"] = new JsonObject { ["actions"] = new JsonArray(new JsonObject { ["image"] = @"Q:\x\ThrottleStop.exe" }) },
        }, [], [], [], [], null);
        Assert.Equal("hardware_limit", broker.Protect(hw)!.Code);
    }
}

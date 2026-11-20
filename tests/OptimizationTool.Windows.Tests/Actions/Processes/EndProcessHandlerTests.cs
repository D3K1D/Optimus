using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Processes;

namespace OptimizationTool.Windows.Tests.Actions.Processes;

/// <summary>An in-memory process table. Never opens or ends a real process.</summary>
internal sealed class FakeProcessControl : IProcessControl
{
    public const string Me = "S-1-5-21-1-2-3-1001";
    public readonly Dictionary<uint, ProcessInfo> Running = [];
    public uint SelfPid { get; set; } = 500;
    public uint CurrentSession { get; set; } = 1;
    public string? CurrentUserSid { get; set; } = Me;
    public readonly List<(uint Pid, long Created, string Image)> Terminated = [];
    /// <summary>What Terminate does: default removes the process when its identity matches.</summary>
    public Func<uint, TerminateResult>? OnTerminate;

    public ProcessInfo? Query(uint pid) => Running.TryGetValue(pid, out var p) ? p : null;

    public TerminateResult Terminate(uint pid, long createTime, string imagePath, TimeSpan wait)
    {
        Terminated.Add((pid, createTime, imagePath));
        if (OnTerminate is not null) return OnTerminate(pid);
        if (!Running.TryGetValue(pid, out var p)) return new("gone");
        if (p.CreateTime != createTime || !string.Equals(p.ImagePath, imagePath, StringComparison.OrdinalIgnoreCase)) return new("mismatch");
        Running.Remove(pid);
        return new("ended");
    }
}

public class EndProcessHandlerTests
{
    private const uint Pid = 4242;
    private const long Created = 133_700_000_000_000_000;
    private const string Image = @"Q:\Apps\Editor\editor.exe";

    private static (EndProcessHandler H, FakeProcessControl P) New()
    {
        var p = new FakeProcessControl();
        p.Running[Pid] = new ProcessInfo(Pid, Created, Image, 1, FakeProcessControl.Me, false);
        return (new EndProcessHandler(p), p);
    }

    private static TargetEvidence Ev(uint pid = Pid, long created = Created, string image = "editor.exe", string collector = EndProcessHandler.CollectorId, string subject = "perf.process.0badc0de") =>
        new("ev-1", collector, subject, new JsonObject
        {
            ["image"] = image, ["pid"] = pid, ["startTime"] = "2024-09-01T00:00:00Z", ["createTime"] = created, ["cpuAvgPercent"] = 40.5,
        }, "2026-11-10T08:00:00Z");

    private static ActionRequest Fresh(TargetEvidence? ev) => new(EndProcessHandler.Id, null, null, null) { Evidence = ev };

    private static JsonObject Rt(JsonObject o) => (JsonObject)JsonNode.Parse(o.ToJsonString())!;

    private static ActionExecution Exec(ActionResolution r) => new("plan-1", "hash", Rt(r.Target), Rt(r.Before), Rt(r.After), null);

    [Fact]
    public void Metadata_matches_the_contract()
    {
        var (h, _) = New();
        Assert.Equal("process.end", h.ActionId);
        Assert.Equal([ActionFlags.Irreversible, ActionFlags.DataLoss], h.Flags);
        Assert.False(h.SupportsUndo);
        Assert.Equal("none", h.Privilege);
        Assert.NotNull(((IActionHandler)h).TargetKind);
        Assert.Contains(WindowsModules.Actions, m => m is ProcessActionModule);
    }

    [Fact]
    public async Task Ends_the_exact_instance_rechecked_in_the_lock_and_verifies_it_is_gone()
    {
        var (h, p) = New();
        var r = h.Resolve(Fresh(Ev()));
        Assert.True(r.Eligible, r.IneligibleMessage);
        Assert.Equal(Image, r.Target["imagePath"]!.GetValue<string>());
        Assert.Equal($"process:{Pid}:{Created}", Assert.Single(r.Resources!));
        Assert.Contains(r.Rows, row => row.Before == "editor.exe");
        Assert.Null(h.UndoRequest(r.Target, r.Before));

        var recheck = h.Resolve(new ActionRequest(EndProcessHandler.Id, Rt(r.Target), null, null) { Recheck = true });
        Assert.True(recheck.Eligible);
        Assert.True(JsonNode.DeepEquals(r.Before, recheck.Before));

        var o = h.Execute(Exec(r));
        Assert.Equal("verified", o.State);
        Assert.DoesNotContain("editor", o.ReceiptText); // the AI reads receipts
        Assert.Equal((Pid, Created, Image), Assert.Single(p.Terminated));
        Assert.False((await h.ReadTargetAsync(Rt(r.Target), default))["running"]!.GetValue<bool>());
    }

    [Fact]
    public void A_reused_pid_is_never_ended()
    {
        var (h, p) = New();
        var r = h.Resolve(Fresh(Ev()));
        // The process exits and another starts with the same PID before approval.
        p.Running[Pid] = new ProcessInfo(Pid, Created + 1, Image, 1, FakeProcessControl.Me, false);
        Assert.Equal("not_found", h.Resolve(new ActionRequest(EndProcessHandler.Id, Rt(r.Target), null, null) { Recheck = true }).IneligibleCode);
        p.Running[Pid] = new ProcessInfo(Pid, Created, @"Q:\Other\editor.exe", 1, FakeProcessControl.Me, false);
        Assert.Equal("not_found", h.Resolve(new ActionRequest(EndProcessHandler.Id, Rt(r.Target), null, null) { Recheck = true }).IneligibleCode);
        // And if it happens between the lock check and the end, the handle check refuses: nothing is ended.
        var o = h.Execute(Exec(r));
        Assert.Equal("verified", o.State); // the approved instance is gone
        Assert.True(p.Running.ContainsKey(Pid)); // the new one still runs
    }

    [Fact]
    public void Refuses_protected_foreign_and_elevated_processes()
    {
        var (h, p) = New();
        void Live(ProcessInfo info) => p.Running[info.Pid] = info;
        string? Code(string image, uint pid = Pid) => h.Resolve(Fresh(Ev(pid, image: image))).IneligibleCode;

        Live(new(Pid, Created, @"C:\Windows\explorer.exe", 1, FakeProcessControl.Me, false));
        Assert.Equal("protected", Code("explorer.exe"));
        Live(new(Pid, Created, @"C:\Program Files\Optimus\OptimizationTool.Desktop.exe", 1, FakeProcessControl.Me, false));
        Assert.Equal("protected", Code("OptimizationTool.Desktop.exe"));
        Live(new(Pid, Created, @"C:\Program Files\MSI Afterburner\MSIAfterburner.exe", 1, FakeProcessControl.Me, false));
        Assert.Equal("hardware_limit", Code("MSIAfterburner.exe"));
        Live(new(Pid, Created, @"C:\ProgramData\Microsoft\Windows Defender\MsMpEng.exe", 0, "S-1-5-18", null));
        Assert.Equal("protected", Code("MsMpEng.exe"));

        Live(new(Pid, Created, Image, 2, FakeProcessControl.Me, false));
        Assert.Equal("other_session", Code("editor.exe"));
        Live(new(Pid, Created, Image, 1, "S-1-5-21-1-2-3-1002", false));
        Assert.Equal("other_user", Code("editor.exe"));
        Live(new(Pid, Created, Image, 1, null, null));
        Assert.Equal("other_user", Code("editor.exe"));
        Live(new(Pid, Created, Image, 1, FakeProcessControl.Me, true));
        Assert.Equal("elevated", Code("editor.exe"));

        p.SelfPid = Pid;
        Live(new(Pid, Created, @"C:\Program Files\Renamed\anything.exe", 1, FakeProcessControl.Me, false));
        Assert.Equal("protected", Code("anything.exe"));
        Assert.Empty(p.Terminated);
    }

    [Fact]
    public void Unknown_targets_and_hostile_inputs()
    {
        var (h, p) = New();
        Assert.Equal(ActionService.UnknownTarget, h.Resolve(Fresh(null)).IneligibleCode);
        Assert.Equal(ActionService.UnknownTarget, h.Resolve(Fresh(Ev(collector: "win.security.startup"))).IneligibleCode);
        Assert.Equal(ActionService.UnknownTarget, h.Resolve(Fresh(Ev(subject: "perf.sample"))).IneligibleCode);
        Assert.Equal(ActionService.UnknownTarget, h.Resolve(Fresh(Ev(image: "other.exe"))).IneligibleCode); // same PID and start, other image
        Assert.Equal(ActionService.UnknownTarget, h.Resolve(Fresh(Ev(pid: 4))).IneligibleCode);              // System
        Assert.Equal(ActionService.UnknownTarget, h.Resolve(Fresh(Ev(created: 0))).IneligibleCode);          // no exact start time
        var noCreate = Ev();
        noCreate.Value.Remove("createTime");
        Assert.Equal(ActionService.UnknownTarget, h.Resolve(Fresh(noCreate)).IneligibleCode);
        var hostile = Ev();
        hostile.Value["pid"] = "4242; taskkill /im *";
        Assert.Equal(ActionService.UnknownTarget, h.Resolve(Fresh(hostile)).IneligibleCode);
        Assert.Equal("not_found", h.Resolve(Fresh(Ev(pid: 9999))).IneligibleCode);
        Assert.Equal("not_found", h.Resolve(Fresh(Ev(created: Created - 1))).IneligibleCode);

        // A raw target from a caller is never trusted, and a forged plan target must agree with itself.
        var raw = new JsonObject { ["type"] = "process", ["id"] = $"{Pid}:{Created}", ["pid"] = Pid, ["createTime"] = Created, ["imagePath"] = Image };
        Assert.Equal(ActionService.UnknownTarget, h.Resolve(new ActionRequest(EndProcessHandler.Id, raw, null, null)).IneligibleCode);
        var forged = (JsonObject)raw.DeepClone();
        forged["id"] = "1:2";
        Assert.Equal(ActionService.UnknownTarget, h.Resolve(new ActionRequest(EndProcessHandler.Id, forged, null, null) { Recheck = true }).IneligibleCode);
        Assert.Equal("invalid_arguments", h.Resolve(Fresh(Ev()) with { Parameters = new JsonObject { ["pid"] = 1 } }).IneligibleCode);
        Assert.Empty(p.Terminated);
    }

    [Fact]
    public void A_process_that_survives_is_failed()
    {
        var (h, p) = New();
        var r = h.Resolve(Fresh(Ev()));
        p.OnTerminate = _ => new TerminateResult("denied", 5);
        var o = h.Execute(Exec(r));
        Assert.Equal("failed", o.State);
        Assert.Equal("access_denied", o.Code);
    }
}

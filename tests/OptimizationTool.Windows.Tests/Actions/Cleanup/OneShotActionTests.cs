using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Cleanup;
using OptimizationTool.Windows.Actions.Network;
using OptimizationTool.Windows.Actions.Processes;
using OptimizationTool.Windows.Actions.Shell;
using OptimizationTool.Windows.Tests.Actions.Processes;

namespace OptimizationTool.Windows.Tests.Actions.Cleanup;

internal sealed class FakeBin : IRecycleBin
{
    public RecycleBinInfo? Now = new(5, 12_345_678);
    public int Empties;
    public Func<RecycleBinInfo?>? AfterEmpty;
    public RecycleBinInfo? Query() => Now;
    public int Empty() { Empties++; Now = AfterEmpty is null ? new RecycleBinInfo(0, 0) : AfterEmpty(); return 0; }
}

internal sealed class FakeDns(bool ok = true) : IDnsCache
{
    public int Calls;
    public bool Flush() { Calls++; return ok; }
}

internal sealed class FakeShell(FakeProcessControl processes) : IShellControl
{
    public bool RemoteSession { get; set; }
    public uint? Pid { get; set; }
    /// <summary>What happens when the shell is ended: by default Windows brings a new one back by itself.</summary>
    public bool ComesBack = true;
    public int Starts;
    public uint? ShellPid()
    {
        if (Pid is { } p && !processes.Running.ContainsKey(p) && ComesBack) Respawn();
        return Pid is { } q && processes.Running.ContainsKey(q) ? q : null;
    }
    public bool StartExplorer() { Starts++; Respawn(); return true; }
    private void Respawn()
    {
        var pid = (uint)(Pid ?? 100) + 1;
        processes.Running[pid] = new ProcessInfo(pid, 2000 + pid, @"Q:\Windows\explorer.exe", 1, FakeProcessControl.Me, false);
        Pid = pid;
    }
}

public class OneShotActionTests
{
    private static JsonObject Rt(JsonObject o) => (JsonObject)JsonNode.Parse(o.ToJsonString())!;
    private static ActionExecution Exec(ActionResolution r) => new("plan-1", "hash", Rt(r.Target), Rt(r.Before), Rt(r.After), null);

    // ---------------------------------------------------------------- recycle bin

    [Fact]
    public void RecycleBin_previews_count_and_size_empties_and_verifies()
    {
        var bin = new FakeBin();
        var h = new EmptyRecycleBinHandler(bin);
        Assert.Equal([ActionFlags.Irreversible, ActionFlags.DataLoss], h.Flags);
        Assert.False(h.SupportsUndo);
        var r = h.Resolve(new ActionRequest(EmptyRecycleBinHandler.Id, null, null, null));
        Assert.True(r.Eligible);
        Assert.Equal(5, r.Before["items"]!.GetValue<long>());
        Assert.Equal(12_345_678, r.Before["bytes"]!.GetValue<long>());
        Assert.Equal("5 items · 11.8 MB", r.Rows[0].Before.Replace(',', '.'));
        var o = h.Execute(Exec(r));
        Assert.Equal("verified", o.State);
        Assert.Equal(1, bin.Empties);
        Assert.Null(h.UndoRequest(r.Target, r.Before));
        Assert.Equal("already_set", h.Resolve(new ActionRequest(EmptyRecycleBinHandler.Id, null, null, null)).IneligibleCode);
    }

    [Fact]
    public void RecycleBin_that_changed_since_the_preview_is_never_emptied()
    {
        var bin = new FakeBin();
        var h = new EmptyRecycleBinHandler(bin);
        var r = h.Resolve(new ActionRequest(EmptyRecycleBinHandler.Id, null, null, null));
        bin.Now = new RecycleBinInfo(5, 12_345_679); // same count, another size
        var recheck = h.Resolve(new ActionRequest(EmptyRecycleBinHandler.Id, Rt(r.Target), null, null) { Recheck = true });
        Assert.False(JsonNode.DeepEquals(recheck.Before, r.Before)); // the engine's approve recheck → stale_plan
        var o = h.Execute(Exec(r));
        Assert.Equal("failed", o.State);
        Assert.Equal("stale_plan", o.Code);
        Assert.Equal(0, bin.Empties);
    }

    [Fact]
    public void RecycleBin_refuses_hostile_input_and_reports_partial_results()
    {
        var bin = new FakeBin();
        var h = new EmptyRecycleBinHandler(bin);
        Assert.Equal("invalid_arguments", h.Resolve(new ActionRequest(EmptyRecycleBinHandler.Id, null, new JsonObject { ["drive"] = "D:" }, null)).IneligibleCode);
        Assert.Equal("invalid_arguments", h.Resolve(new ActionRequest(EmptyRecycleBinHandler.Id, new JsonObject { ["path"] = @"C:\" }, null, null)).IneligibleCode);
        bin.Now = null;
        Assert.Equal("read_failed", h.Resolve(new ActionRequest(EmptyRecycleBinHandler.Id, null, null, null)).IneligibleCode);
        bin.Now = new RecycleBinInfo(5, 100);
        var r = h.Resolve(new ActionRequest(EmptyRecycleBinHandler.Id, null, null, null));
        bin.AfterEmpty = () => new RecycleBinInfo(2, 40);
        Assert.Equal("indeterminate", h.Execute(Exec(r)).State);
    }

    // ---------------------------------------------------------------- dns

    [Fact]
    public void FlushDns_is_a_one_shot_without_undo()
    {
        var dns = new FakeDns();
        var h = new FlushDnsHandler(dns);
        Assert.Equal("provider", h.Kind);
        Assert.False(h.SupportsUndo);
        var r = FlushDnsHandler.Resolve(new ActionRequest(FlushDnsHandler.Id, null, null, null));
        Assert.True(r.Eligible);
        Assert.Equal(0, dns.Calls);
        var o = h.Execute();
        Assert.Equal("completed", o.State);
        Assert.Equal("flushed", o.Observed["result"]!.GetValue<string>());
        Assert.Contains("Nothing else was changed", o.ReceiptText);
        Assert.Equal("failed", new FlushDnsHandler(new FakeDns(ok: false)).Execute().State);
        Assert.Equal("invalid_arguments", FlushDnsHandler.Resolve(new ActionRequest(FlushDnsHandler.Id, null, new JsonObject { ["server"] = "1.1.1.1" }, null)).IneligibleCode);
        Assert.Equal("not_supported", FlushDnsHandler.Resolve(new ActionRequest(FlushDnsHandler.Id, null, null, null) { Undo = true }).IneligibleCode);
    }

    // ---------------------------------------------------------------- explorer

    private static (ExplorerRestartHandler H, FakeShell S, FakeProcessControl P) Explorer()
    {
        var p = new FakeProcessControl();
        p.Running[100] = new ProcessInfo(100, 1000, @"Q:\Windows\explorer.exe", 1, FakeProcessControl.Me, false);
        var s = new FakeShell(p) { Pid = 100 };
        return (new ExplorerRestartHandler(s, p, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(10)), s, p);
    }

    [Fact]
    public async Task Explorer_restarts_and_verifies_a_new_shell()
    {
        var (h, s, p) = Explorer();
        Assert.Equal([ActionFlags.DataLoss], h.Flags);
        Assert.False(h.SupportsUndo);
        Assert.True(h.Resolve(new ActionRequest(ExplorerRestartHandler.Id, null, null, null)).Eligible);
        var o = await h.ExecuteCoreAsync(null, default);
        Assert.Equal("completed", o.State);
        Assert.Equal("restarted", o.Observed["result"]!.GetValue<string>());
        Assert.Equal(0, s.Starts);
        Assert.Single(p.Terminated);
        Assert.Equal(100u, p.Terminated[0].Pid);
    }

    [Fact]
    public async Task Explorer_is_started_when_Windows_does_not_bring_it_back()
    {
        var (h, s, _) = Explorer();
        s.ComesBack = false;
        var o = await h.ExecuteCoreAsync(null, default);
        Assert.Equal("completed", o.State);
        Assert.Equal("started", o.Observed["result"]!.GetValue<string>());
        Assert.Equal(1, s.Starts);
    }

    [Fact]
    public async Task Explorer_is_report_only_and_not_running_is_a_completed_result()
    {
        var (h, s, p) = Explorer();
        Assert.True(((IActionHandler)h).ReportsOnly);
        s.ComesBack = false;
        var shellless = new FakeShellNoStart(s);
        var h2 = new ExplorerRestartHandler(shellless, p, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(10));
        var progress = new List<string>();
        var o = await h2.ExecuteCoreAsync(new SyncProgress(progress), default);
        Assert.Equal("completed", o.State); // design v59: never indeterminate
        Assert.Equal("not_running", o.Observed["result"]!.GetValue<string>());
        Assert.Null(o.Code);
        Assert.Equal(["Restarting Windows Explorer"], progress);
    }

    [Fact]
    public async Task Explorer_that_could_not_be_ended_fails_with_result_failed()
    {
        var (h, _, p) = Explorer();
        p.OnTerminate = _ => new TerminateResult("timeout");
        var o = await h.ExecuteCoreAsync(null, default);
        Assert.Equal(("failed", "failed", "not_applied"), (o.State, o.Observed["result"]!.GetValue<string>(), o.Code));
        Assert.Equal("Windows Explorer wasn't restarted; nothing changed.", o.ReceiptText);
    }

    /// <summary>A shell whose StartExplorer reports success but no shell ever comes back.</summary>
    private sealed class FakeShellNoStart(FakeShell inner) : IShellControl
    {
        public bool RemoteSession => inner.RemoteSession;
        public uint? ShellPid() => inner.ShellPid();
        public bool StartExplorer() => true;
    }

    private sealed class SyncProgress(List<string> lines) : IProgress<string>
    {
        public void Report(string value) => lines.Add(value);
    }

    [Fact]
    public void Explorer_refusals()
    {
        var (h, s, p) = Explorer();
        s.RemoteSession = true;
        Assert.Equal("remote_session", h.Resolve(new ActionRequest(ExplorerRestartHandler.Id, null, null, null)).IneligibleCode);
        s.RemoteSession = false;
        p.Running[100] = new ProcessInfo(100, 1000, @"Q:\Shells\othershell.exe", 1, FakeProcessControl.Me, false);
        Assert.Equal("unsupported", h.Resolve(new ActionRequest(ExplorerRestartHandler.Id, null, null, null)).IneligibleCode);
        p.Running[100] = new ProcessInfo(100, 1000, @"Q:\Windows\explorer.exe", 1, FakeProcessControl.Me, true);
        Assert.Equal("elevated", h.Resolve(new ActionRequest(ExplorerRestartHandler.Id, null, null, null)).IneligibleCode);
        s.ComesBack = false;
        p.Running.Clear();
        Assert.Equal("not_found", h.Resolve(new ActionRequest(ExplorerRestartHandler.Id, null, null, null)).IneligibleCode);
        Assert.Equal("invalid_arguments", h.Resolve(new ActionRequest(ExplorerRestartHandler.Id, null, new JsonObject { ["pid"] = 100 }, null)).IneligibleCode);
        Assert.Empty(p.Terminated);
    }
}

using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Broker;
using OptimizationTool.Windows.Actions.Protection;
using OptimizationTool.Windows.Actions.Repair;
using OptimizationTool.Windows.Actions.Services;

namespace OptimizationTool.Broker.Tests;

/// <summary>
/// The P3b-2 handlers inside a broker session (m9 §3.5): undo only against the broker's own record, the broker's own
/// protected lists, a long repair with progress frames, stop and its budget, and the 45 s write watchdog. Fakes only.
/// </summary>
public sealed class AdminSessionTests
{
    private const string Image = @"Q:\Program Files\Contoso\agent.exe";

    private sealed class Scm : IServiceControl
    {
        public readonly Dictionary<string, ServiceInfo> S = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<string> Names() => [.. S.Keys];
        public ServiceInfo? Read(string name) => S.TryGetValue(name, out var s) ? s : null;
        public void SetStartType(string name, int startType, bool delayed) => S[name] = S[name] with { StartType = startType };
        public void Stop(string name, TimeSpan wait) => S[name] = S[name] with { State = 1 };
        public void Start(string name, TimeSpan wait) => S[name] = S[name] with { State = 4 };
    }

    private sealed class Files : IFileFacts
    {
        public readonly Dictionary<string, string> Pub = [];
        public bool Exists(string path) => true;
        public string? Sha256(string path) => new('1', 64);
        public string? Publisher(string path) => Pub.TryGetValue(path, out var p) ? p : null;
    }

    private sealed class Tool : IRepairTool
    {
        public List<string> Lines = [];
        public TaskCompletionSource? Gate;
        public volatile bool Killed;
        public List<IReadOnlyList<string>> Args = [];
        public (string? Path, string? Problem) Locate(string fileName) => (@"Q:\Windows\System32\" + fileName, null);
        public async Task<int> RunAsync(string path, IReadOnlyList<string> arguments, Action<string> onLine, CancellationToken ct)
        {
            lock (Args) Args.Add(arguments);
            foreach (var l in Lines) { await Task.Delay(1100, ct); onLine(l); } // one progress frame per second at most
            if (Gate is { } g)
                try { await g.Task.WaitAsync(ct); }
                catch (OperationCanceledException) { Killed = true; throw; }
            return 0;
        }
    }

    private static ServiceInfo Svc(string name = "ContosoAgent") => new(name, "Contoso Agent", 0x10, 2, false, $"\"{Image}\"", Image, null, "LocalSystem", 4, []);

    private static (Rig Rig, Scm Scm, Files Files) ServiceRig()
    {
        var rig = new Rig();
        var scm = new Scm();
        scm.S["ContosoAgent"] = Svc();
        var files = new Files();
        rig.Handlers.Add(new ServiceBrokerHandler(scm, files));
        return (rig, scm, files);
    }

    private static BrokerStepRequest Step(ActionResolution r, JsonObject? p, ElevatedUndoOf? undoOf = null) =>
        Kit.Step(1, ServiceDisableHandler.Id, ServiceDisableHandler.HandlerVersion, r.Target, r.Before, r.After, p, undoOf);

    [Fact]
    public async Task Service_disable_runs_and_its_undo_needs_the_brokers_own_record()
    {
        var (rig, scm, files) = ServiceRig();
        var host = new ServiceDisableHandler(scm, files);
        var p = new JsonObject { ["startType"] = "disabled", ["stop"] = true };
        var r = host.Resolve(new ActionRequest(ServiceDisableHandler.Id, ServiceDisableHandler.Target("ContosoAgent", "Contoso Agent"), p, null) { Recheck = true });
        Assert.True(r.Eligible);
        var (result, sent, _) = await rig.Run(Kit.Request(null, Step(r, p)).ToJson());
        Assert.Equal("verified", sent!.Steps.Single().State);
        Assert.True(rig.Confirm.Shown.Single().NeedsTick); // data_loss: the broker window's own tick
        Assert.Contains("May lose unsaved work", rig.Confirm.Shown.Single().Steps.Single().Badges);
        Assert.True(result.Wrote);

        var undo = host.UndoRequest(r.Target, r.Before)!;
        var ur = host.Resolve(undo);
        Assert.True(ur.Eligible);
        // Without undoOf (a preimage from the host alone): refused.
        var (_, noRef, _) = await rig.Run(Kit.Request(Kit.Nonce('b'), Step(ur, undo.Parameters)).ToJson(), nonce: Kit.Nonce('b'));
        Assert.Equal("no_record", noRef!.Steps.Single().Code);
        // Naming a record the broker doesn't have: refused.
        var (_, fake, _) = await rig.Run(Kit.Request(Kit.Nonce('c'), Step(ur, undo.Parameters, new ElevatedUndoOf("rcpt-00000000000000ff", 1))).ToJson(), nonce: Kit.Nonce('c'));
        Assert.Equal("no_record", fake!.Steps.Single().Code);
        // With the broker's own record: restored.
        var (_, ok, _) = await rig.Run(Kit.Request(Kit.Nonce('d'), Step(ur, undo.Parameters, new ElevatedUndoOf(sent.ReceiptId!, 1))).ToJson(), nonce: Kit.Nonce('d'));
        Assert.Equal("verified", ok!.Steps.Single().State);
        Assert.Equal((2, 4), (scm.S["ContosoAgent"].StartType, scm.S["ContosoAgent"].State));
    }

    [Fact]
    public async Task The_broker_refuses_a_protected_service_the_host_let_through()
    {
        var (rig, scm, files) = ServiceRig();
        var host = new ServiceDisableHandler(scm, files);
        var p = new JsonObject { ["startType"] = "manual", ["stop"] = false };
        var r = host.Resolve(new ActionRequest(ServiceDisableHandler.Id, ServiceDisableHandler.Target("ContosoAgent", "Contoso Agent"), p, null) { Recheck = true });
        Assert.True(r.Eligible);
        files.Pub[Image] = "Bitdefender SRL"; // the broker reads the publisher itself
        var (result, sent, _) = await rig.Run(Kit.Request(null, Step(r, p)).ToJson());
        Assert.Equal("protected", sent!.Steps.Single().Code);
        Assert.Empty(rig.Confirm.Shown); // refused before its window
        Assert.False(result.Wrote);
        Assert.Equal(2, scm.S["ContosoAgent"].StartType);
    }

    [Fact]
    public async Task A_long_repair_sends_progress_frames_extends_the_budget_and_says_so_on_the_window()
    {
        var rig = new Rig();
        var tool = new Tool { Lines = ["Verification 10% complete.", "Verification 60% complete.", "Windows Resource Protection did not find any integrity violations."] };
        rig.Handlers.Add(new RepairBrokerHandler(RepairKind.Sfc, tool));
        var r = RepairPlan.Resolve(RepairKind.Sfc, tool, null, null);
        var step = Kit.Step(1, RepairKind.Sfc.ActionId, RepairPlan.HandlerVersion, r.Target, r.Before, r.After, null);
        var budgets = new List<TimeSpan>();
        var pipe = await Duplex.WithFrames(Kit.Request(null, step).ToJson());
        var session = new BrokerSession(new BrokerSessionOptions(Kit.Nonce(), Kit.HostImage, Kit.Self, rig.Handlers, rig.Journal, rig.Ledger, rig.Confirm)
        {
            ExtendBudget = budgets.Add,
        });
        var result = await session.RunAsync(pipe, Kit.Host, CancellationToken.None);
        Assert.Equal("verified", result.Code); // completed counts as done
        Assert.Equal(["/scannow"], Assert.Single(tool.Args));
        Assert.True(Assert.Single(budgets) >= TimeSpan.FromMinutes(61));
        Assert.True(rig.Confirm.Shown.Single().Steps.Single().Repair);
        Assert.Contains(BrokerConfirmView.RepairLine, rig.Confirm.Shown.Single().Lines().Select(l => l.Text));
        Assert.Contains("Takes a while", rig.Confirm.Shown.Single().Steps.Single().Badges);

        // The output: progress frames for step 1, then the one outcome, last.
        pipe.Output.Position = 0;
        var frames = new List<JsonNode?>();
        while (pipe.Output.Position < pipe.Output.Length) frames.Add(await BrokerProtocol.ReadAsync(pipe.Output, CancellationToken.None));
        var progress = frames.Where(BrokerProgress.Is).Select(f => BrokerProgress.Parse(f, Kit.Nonce())!.Value).ToList();
        Assert.Contains((1, "Checking Windows files · 10%"), progress);
        Assert.Contains((1, "Checking Windows files · 60%"), progress);
        var last = BrokerResponse.Parse(frames[^1])!;
        Assert.Equal(("completed", "no_violations"), (last.Steps.Single().State, last.Steps.Single().Observed["result"]!.GetValue<string>()));
        Assert.Equal("completed", rig.Journal.Sessions.Values.Single().Outcomes[1].State);
    }

    [Fact]
    public async Task A_stop_frame_ends_a_running_repair()
    {
        var rig = new Rig();
        var tool = new Tool { Gate = new TaskCompletionSource() };
        rig.Handlers.Add(new RepairBrokerHandler(RepairKind.Dism, tool));
        var r = RepairPlan.Resolve(RepairKind.Dism, tool, null, null);
        var step = Kit.Step(1, RepairKind.Dism.ActionId, RepairPlan.HandlerVersion, r.Target, r.Before, r.After, null);
        var pipe = await Duplex.WithFrames(Kit.Request(null, step).ToJson());
        var running = rig.Session().RunAsync(pipe, Kit.Host, CancellationToken.None);
        while (tool.Args.Count == 0) await Task.Delay(5);
        await pipe.Push(BrokerStop.ToJson(Kit.Nonce()));
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(tool.Killed);
        pipe.Output.Position = 0;
        JsonNode? last = null;
        while (pipe.Output.Position < pipe.Output.Length) last = await BrokerProtocol.ReadAsync(pipe.Output, CancellationToken.None); // progress frames, then the outcome
        var sent = BrokerResponse.Parse(last);
        Assert.Equal(("failed", "stopped"), (sent!.Steps.Single().State, sent.Steps.Single().Code));
        Assert.Equal(["/Online", "/Cleanup-Image", "/RestoreHealth"], tool.Args.Single());
        _ = result;
    }

    [Fact]
    public async Task A_write_that_hangs_is_cut_by_the_watchdog_and_nothing_further_starts()
    {
        var rig = new Rig("test.a", "test.b");
        var hang = new ManualResetEventSlim();
        rig.Test("test.a").OnWrite = () => hang.Wait(TimeSpan.FromSeconds(20));
        var pipe = await Duplex.WithFrames(Kit.Request(null, Kit.Test(1, "test.a", 0, 1), Kit.Test(2, "test.b", 0, 1)).ToJson());
        var session = new BrokerSession(new BrokerSessionOptions(Kit.Nonce(), Kit.HostImage, Kit.Self, rig.Handlers, rig.Journal, rig.Ledger, rig.Confirm)
        {
            StepDeadline = TimeSpan.FromMilliseconds(200),
        });
        var result = await session.RunAsync(pipe, Kit.Host, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        hang.Set();
        Assert.Equal("write_timeout", result.Code);
        var sent = await pipe.Response();
        Assert.Equal(("indeterminate", "write_timeout"), (sent!.Steps[0].State, sent.Steps[0].Code));
        Assert.Equal("skipped", sent.Steps[1].State);
        Assert.Equal(0, rig.Test("test.b").Writes);
        Assert.Equal("indeterminate", rig.Journal.Sessions.Values.Single().Outcomes[1].State); // journalled
    }
}

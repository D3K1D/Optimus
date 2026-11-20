using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Apps;
using OptimizationTool.Windows.Actions.Cleanup;
using OptimizationTool.Windows.Actions.Network;
using OptimizationTool.Windows.Actions.Processes;
using OptimizationTool.Windows.Actions.Settings;
using OptimizationTool.Windows.Actions.Shell;
using OptimizationTool.Windows.SystemDomain.Rules;

namespace OptimizationTool.Tests;

/// <summary>
/// The last user-scope batch of m9 §3.2 through the engine, with the real handlers over in-memory seams (nothing here
/// touches this PC): the storage rule's card previews cleanup.empty_recycle_bin, a changed bin makes the approval stale,
/// and which of the new kinds a fix plan accepts.
/// </summary>
public sealed class UserActionEngineTests
{
    internal sealed class Bin : IRecycleBin
    {
        public long Items = 3, Bytes = 3_000_000;
        public int Empties;
        public RecycleBinInfo? Query() => new(Items, Bytes);
        public int Empty() { Empties++; Items = 0; Bytes = 0; return 0; }
    }

    private sealed class Dns : IDnsCache { public int Flushes; public bool Flush() { Flushes++; return true; } }

    private sealed class NoShell : IShellControl
    {
        public bool RemoteSession => false;
        public uint? ShellPid() => null;
        public bool StartExplorer() => false;
    }

    private sealed class NoProcesses : IProcessControl
    {
        public uint SelfPid => 1;
        public uint CurrentSession => 1;
        public string? CurrentUserSid => "S-1-5-21-1";
        public ProcessInfo? Query(uint pid) => pid == 4242 ? new ProcessInfo(4242, 99, @"Q:\Games\game.exe", 1, "S-1-5-21-1", false) : null;
        public TerminateResult Terminate(uint pid, long createTime, string imagePath, TimeSpan wait) => throw new InvalidOperationException("never in this test");
    }

    private sealed class Registry : IRegistryStore
    {
        public readonly Dictionary<string, RegData> Values = new(StringComparer.OrdinalIgnoreCase);
        private static string K(RegLocation at) => $"{at.Hive}|{at.Key}|{at.Name}";
        public RegData Read(RegLocation at) => Values.TryGetValue(K(at), out var d) ? d : RegData.Absent;
        public RegData Write(RegLocation at, string type, JsonNode data) { Values[K(at)] = new RegData(true, type, data.DeepClone()); return Read(at); }
        public RegData Delete(RegLocation at) { Values.Remove(K(at)); return Read(at); }
        public bool Exists(RegLocation at) => false;
        public IReadOnlyList<string> ValueNames(RegLocation key) => [];
    }

    private sealed class TwoGpus : IGpuAdapters { public int? HardwareAdapterCount() => 2; }

    private static IReadOnlyList<IAuditModule> Storage(double freeGiB) =>
    [
        new FakeModule("storage", [new FakeCollector("win.system.volumes", "storage", _ => Task.FromResult(FakeCollector.One("volume.c",
            new JsonObject { ["letter"] = "C", ["isSystem"] = true, ["freeBytes"] = freeGiB * 1073741824d, ["capacityBytes"] = 500 * 1073741824d }, "C: measured.")), "Drives")],
            [new SystemVolumeLowRule()]),
    ];

    [Fact]
    public async Task StorageCard_PreviewsEmptyingTheBin_AndAChangedBinIsStale()
    {
        var bin = new Bin();
        await using var host = TestHost.StartActions([new FakeActionModule(new EmptyRecycleBinHandler(bin))], Storage(3));
        var c = await host.ConnectAsync(minor: 3);
        var (scan, _) = await c.ScanAsync("audit.full");
        var reportId = scan.Str("reportId");
        var report = await c.Call("report.get", new JsonObject { ["reportId"] = reportId });
        var finding = report["findings"]!.AsArray().Single(f => f.Str("ruleId") == "storage.system_low")!;
        var card = (await c.Call("followup.get", new JsonObject { ["reportId"] = reportId }))["card"]!;
        Assert.Equal(finding.Str("id"), card.Str("findingId"));
        Assert.Equal(EmptyRecycleBinHandler.Id, card.Str("actionId"));
        Assert.Contains("Preview change", card["options"]!.AsArray().Select(o => o!.GetValue<string>()));

        // Preview from the card (no parameters, no target): eligible, with the bin's count and size.
        var plan = await Act.Preview(c, EmptyRecycleBinHandler.Id, scheme: null, findingId: finding.Str("id"));
        Assert.Equal("awaiting_user", plan.Str("status"));
        Assert.Equal(3, plan["before"]!["items"]!.GetValue<long>());
        Assert.Equal(["irreversible", "data_loss"], plan["flags"]!.AsArray().Select(f => f!.GetValue<string>()));
        Assert.False(plan["undo"]!["supported"]!.GetValue<bool>());

        // Something was deleted into the bin after the preview: stale, nothing emptied.
        bin.Items = 4;
        var stale = await c.Fails("action.approve", Act.ApproveParams(plan, await Act.Nonce(c, plan.Str("planId"))));
        Assert.Equal("stale_plan", stale.ErrorData.Str("reason"));
        Assert.Equal(0, bin.Empties);

        var again = await Act.Preview(c, EmptyRecycleBinHandler.Id, scheme: null, findingId: finding.Str("id"));
        var op = await Act.Approve(c, again);
        Assert.Equal("verified", op.Str("state"));
        Assert.Equal(1, bin.Empties);
        Assert.False(op["undo"]!["available"]!.GetValue<bool>());
        Assert.Equal("already_set", (await Act.Preview(c, EmptyRecycleBinHandler.Id, scheme: null, findingId: finding.Str("id")))["ineligible"].Str("code"));
    }

    [Fact]
    public async Task FixPlans_TakeTheSettingKinds_AndRefuseTheOneShotOperations()
    {
        var bin = new Bin();
        var registry = new Registry();
        var dns = new Dns();
        await using var host = TestHost.StartActions([new FakeActionModule(
            new EmptyRecycleBinHandler(bin), new FlushDnsHandler(dns), new ExplorerRestartHandler(new NoShell(), new NoProcesses()),
            new GpuPreferenceHandler(registry, new NoProcesses(), new TwoGpus()))]);
        var c = await host.ConnectAsync(minor: 3);
        var actions = host.Server.Engine.Actions;
        var ev = new TargetEvidence("ev-9", "win.perf.sample", "perf.process.12345678", new JsonObject { ["image"] = "game.exe", ["pid"] = 4242, ["createTime"] = 99 }, null);

        var refused = await actions.CreateFixAsync("Tidy up", "Why not.", "Claude",
            [new FixStepRequest(EmptyRecycleBinHandler.Id, null, null, null), new FixStepRequest(FlushDnsHandler.Id, null, null, null),
             new FixStepRequest(ExplorerRestartHandler.Id, null, null, null)], null, RequestedBy.User);
        Assert.Equal("ineligible", refused.Str("status"));
        Assert.Equal(["not_supported", "not_supported"], refused["ineligible"]!["steps"]!.AsArray().Select(s => s.Str("code")));
        Assert.Equal([2, 3], refused["ineligible"]!["steps"]!.AsArray().Select(s => s!["n"]!.GetValue<int>()));

        var fix = await actions.CreateFixAsync("Tidy up", "Why not.", "Claude",
            [new FixStepRequest(EmptyRecycleBinHandler.Id, null, null, null),
             new FixStepRequest(GpuPreferenceHandler.Id, new JsonObject { ["preference"] = "high_performance" }, null, new TargetRef("ev-9", ev))], null, RequestedBy.User);
        Assert.Equal("awaiting_user", fix.Str("status"));
        var op = await Act.Approve(c, fix);
        var final = await c.WaitForEvent(e => e.Str("type") == "operation.state" && e["payload"].Str("planId") == fix.Str("planId") && e["payload"]!["step"] is null
            && e["payload"].Str("state") is "verified" or "failed" or "indeterminate" or "partial");
        Assert.Equal("verified", final["payload"].Str("state"));
        Assert.Equal(1, bin.Empties);
        Assert.Equal("GpuPreference=2;", Assert.Single(registry.Values).Value.Data!.GetValue<string>());
        Assert.Equal(0, dns.Flushes);
        var got = await c.Call("operation.get", new JsonObject { ["operationId"] = op.Str("operationId") });
        Assert.DoesNotContain("game.exe", got["steps"]!.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FlushDns_RunsAsAOneShotOperation_WithoutUndo()
    {
        var dns = new Dns();
        await using var host = TestHost.StartActions([new FakeActionModule(new FlushDnsHandler(dns))]);
        var c = await host.ConnectAsync(minor: 3);
        var plan = await Act.Preview(c, FlushDnsHandler.Id, scheme: null);
        Assert.Equal("provider", plan.Str("kind"));
        var op = await Act.Approve(c, plan);
        var done = await c.WaitForEvent(e => e.Str("type") == "operation.state" && e["payload"].Str("operationId") == op.Str("operationId") && e["payload"].Str("state") == "completed"); // Windows' own report: nothing to read back, never verified
        Assert.Contains("Nothing else was changed", done["payload"]!["receipt"].Str("text"));
        var final = await c.Call("operation.get", new JsonObject { ["operationId"] = op.Str("operationId") });
        Assert.Equal("flushed", final.Str("result"));
        Assert.False(final["undo"]!["available"]!.GetValue<bool>());
        Assert.Equal(1, dns.Flushes);
        SchemaTests.AssertValid("operation", final);
    }
}

using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Defender;
using OptimizationTool.Windows.Reliability;

namespace OptimizationTool.Windows.Tests.Actions.Defender;

/// <summary>A fake Defender. Nothing here starts a real update or scan.</summary>
internal sealed class FakeDefender : IDefenderSystem
{
    public DefenderStatusRead Before = new(State(), null);
    public DefenderStatusRead? After;
    public ToolPath Tool = new(@"X:\fake\MpCmdRun.exe", null);
    public int Exit;
    public bool Hang;
    public DefenderEvents Events = new(true, [], 0, 0, 0, 0);
    public List<IReadOnlyList<string>> Runs { get; } = [];
    public bool Killed;
    /// <summary>Completes when the hung tool has seen its cancellation (the kill).</summary>
    public TaskCompletionSource KilledSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _ran;

    public static DefenderState State(string mode = "primary", string? sig = "1.400.10.0", DateTime? quick = null, DateTime? full = null) =>
        new(mode, true, sig, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 2, quick ?? new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), full);

    public Task<DefenderStatusRead> ReadStatusAsync(CancellationToken ct) => Task.FromResult(_ran && After is not null ? After : Before);
    public ToolPath ResolveTool() => Tool;

    public async Task<int> RunAsync(string path, IReadOnlyList<string> args, CancellationToken ct)
    {
        Runs.Add(args);
        _ran = true;
        if (Hang)
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { Killed = true; KilledSignal.TrySetResult(); throw; }
        }
        return Exit;
    }

    public DefenderEvents ReadEventsSince(DateTimeOffset since, CancellationToken ct) => Events;
}

public class DefenderActionTests
{
    private static readonly JsonObject Confirm = new() { ["confirmLong"] = true };

    private static DefenderHandler H(DefenderOp op, FakeDefender f) => new(op, f, tick: TimeSpan.FromMilliseconds(20));

    private static ActionRequest Req(DefenderHandler h, JsonObject? parameters = null) => new(h.ActionId, null, parameters, null);

    private static async Task<(ActionOutcome Outcome, List<string> Progress)> Run(DefenderHandler h, FakeDefender f, JsonObject? parameters = null, CancellationToken ct = default)
    {
        var res = await h.ResolveAsync(Req(h, parameters), ct);
        Assert.True(res.Eligible, res.IneligibleCode);
        var lines = new List<string>();
        var outcome = await h.ExecuteAsync(new ActionExecution("plan-x", "hash", res.Target, res.Before, res.After, parameters), new SyncProgress(lines), ct);
        return (outcome, lines);
    }

    private sealed class SyncProgress(List<string> lines) : IProgress<string>
    {
        public void Report(string value) { lock (lines) lines.Add(value); }
    }

    [Fact]
    public async Task Catalog_shape()
    {
        var f = new FakeDefender();
        var hs = new[] { H(DefenderOp.UpdateSignatures, f), H(DefenderOp.QuickScan, f), H(DefenderOp.FullScan, f) };
        Assert.Equal(new[] { "defender.update_signatures", "defender.quick_scan", "defender.full_scan" }, hs.Select(h => h.ActionId));
        Assert.All(hs, h =>
        {
            Assert.Equal("provider", h.Kind);
            Assert.Equal("none", h.Privilege);
            Assert.False(h.SupportsUndo);
            Assert.Null(h.UndoRequest(new JsonObject(), new JsonObject()));
        });
        Assert.Equal(new[] { "-SignatureUpdate" }, hs[0].Arguments);
        Assert.Equal(new[] { "-Scan", "-ScanType", "1" }, hs[1].Arguments);
        Assert.Equal(new[] { "-Scan", "-ScanType", "2" }, hs[2].Arguments);
        var full = await new DefenderActionModule().Handlers(new ActionContext(null!, false)).ToAsyncList();
        Assert.Equal(3, full.Count);
    }

    [Fact]
    public async Task Resolve_update_describes_download_and_before()
    {
        var f = new FakeDefender();
        var r = await H(DefenderOp.UpdateSignatures, f).ResolveAsync(Req(H(DefenderOp.UpdateSignatures, f)), default);
        Assert.True(r.Eligible);
        Assert.Equal("1.400.10.0", r.Before["signatureVersion"]!.GetValue<string>());
        Assert.Equal("signatures", r.Target["id"]!.GetValue<string>());
        Assert.Contains(r.Context, kv => kv.Key == "Download");
        Assert.Contains(r.Tradeoffs, t => t.Contains("internet", StringComparison.Ordinal));
        Assert.Equal("1.400.10.0 · 2 days old", Assert.Single(r.Rows).Before);
        Assert.Empty(f.Runs);
    }

    [Theory]
    [InlineData("passive", "not_primary")]
    [InlineData("side_by_side", "not_primary")]
    [InlineData("disabled", "not_primary")]
    public async Task Resolve_requires_primary(string mode, string code)
    {
        var f = new FakeDefender { Before = new(FakeDefender.State(mode), null) };
        var r = await H(DefenderOp.QuickScan, f).ResolveAsync(Req(H(DefenderOp.QuickScan, f)), default);
        Assert.False(r.Eligible);
        Assert.Equal(code, r.IneligibleCode);
        Assert.Equal(DefenderHandler.ManualUri, r.ManualUri);
    }

    [Fact]
    public async Task Resolve_other_ineligible_paths()
    {
        var unavailable = new FakeDefender { Before = new(null, "unsupported") };
        Assert.Equal("defender_unavailable", (await H(DefenderOp.QuickScan, unavailable).ResolveAsync(Req(H(DefenderOp.QuickScan, unavailable)), default)).IneligibleCode);
        var stopped = new FakeDefender { Before = new(FakeDefender.State() with { ServiceEnabled = false }, null) };
        Assert.Equal("service_stopped", (await H(DefenderOp.QuickScan, stopped).ResolveAsync(Req(H(DefenderOp.QuickScan, stopped)), default)).IneligibleCode);
        var untrusted = new FakeDefender { Tool = new(null, "untrusted") };
        Assert.Equal("tool_untrusted", (await H(DefenderOp.QuickScan, untrusted).ResolveAsync(Req(H(DefenderOp.QuickScan, untrusted)), default)).IneligibleCode);
        var f = new FakeDefender();
        var h = H(DefenderOp.QuickScan, f);
        Assert.Equal("bad_target", (await h.ResolveAsync(new ActionRequest(h.ActionId, new JsonObject { ["id"] = "full_scan" }, null, null), default)).IneligibleCode);
        Assert.Equal("bad_request", (await h.ResolveAsync(new ActionRequest("defender.full_scan", null, null, null), default)).IneligibleCode);
    }

    [Fact]
    public async Task Full_scan_needs_explicit_confirmation_and_warns()
    {
        var f = new FakeDefender();
        var h = H(DefenderOp.FullScan, f);
        Assert.Equal("confirmation_required", (await h.ResolveAsync(Req(h), default)).IneligibleCode);
        Assert.Equal("confirmation_required", (await h.ResolveAsync(Req(h, new JsonObject { ["confirmLong"] = "true" }), default)).IneligibleCode);
        var ok = await h.ResolveAsync(Req(h, Confirm), default);
        Assert.True(ok.Eligible);
        Assert.Contains(ok.Tradeoffs, t => t.Contains("long time", StringComparison.Ordinal) && t.Contains("disk", StringComparison.Ordinal));
        Assert.Equal("Not recorded", ok.Rows.Single().Before);
        var outcome = await h.ExecuteAsync(new ActionExecution("p", "h", ok.Target, ok.Before, ok.After, null), null, default);
        Assert.Equal("failed", outcome.State);
        Assert.Equal("confirmation_required", outcome.Code);
        Assert.Empty(f.Runs);
    }

    [Fact]
    public async Task Update_changed_version_completes()
    {
        var f = new FakeDefender { After = new(FakeDefender.State(sig: "1.400.20.0"), null) };
        var (o, progress) = await Run(H(DefenderOp.UpdateSignatures, f), f);
        Assert.Equal("completed", o.State);
        Assert.Equal("updated", o.Observed["result"]!.GetValue<string>());
        Assert.Contains("1.400.20.0", o.ReceiptText);
        Assert.Equal("Updating definitions", progress[0]);
        Assert.Equal(new[] { "-SignatureUpdate" }, Assert.Single(f.Runs));
    }

    [Fact]
    public async Task Update_same_version_exit_zero_is_already_current_and_nonzero_fails()
    {
        var f = new FakeDefender();
        var (o, _) = await Run(H(DefenderOp.UpdateSignatures, f), f);
        Assert.Equal("completed", o.State);
        Assert.Equal("already_current", o.Observed["result"]!.GetValue<string>());

        var bad = new FakeDefender { Exit = unchecked((int)0x8050A004) };
        var (o2, _) = await Run(H(DefenderOp.UpdateSignatures, bad), bad);
        Assert.Equal("failed", o2.State);
        Assert.StartsWith("exit_0x", o2.Code);
    }

    [Fact]
    public async Task Access_denied_exit_is_needs_admin_with_manual_route()
    {
        var f = new FakeDefender { Exit = unchecked((int)0x80070005) };
        var (o, _) = await Run(H(DefenderOp.QuickScan, f), f);
        Assert.Equal("failed", o.State);
        Assert.Equal("needs_admin", o.Code);
        Assert.Equal(DefenderHandler.ManualUri, o.Observed["manualUri"]!.GetValue<string>());
    }

    private static readonly DateTimeOffset Soon = DateTimeOffset.UtcNow.AddSeconds(5);

    [Fact]
    public async Task Finished_scan_with_unreadable_history_reports_threats_unknown_not_no_threats()
    {
        var f = new FakeDefender
        {
            After = new(FakeDefender.State(quick: DateTime.UtcNow.AddMinutes(1)), null),
            Events = new(false, [new(1000, "{U}", "quick", Soon), new(1001, "{U}", "quick", Soon.AddMinutes(2))], 0, 0, 0, 0),
        };
        var (o, _) = await Run(H(DefenderOp.QuickScan, f), f);
        Assert.Equal("completed", o.State);
        Assert.Equal("threats_unknown", o.Observed["result"]!.GetValue<string>());
        Assert.DoesNotContain("No threats", o.ReceiptText);
        Assert.Equal(DefenderHandler.ManualUri, o.Observed["manualUri"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(false, false, 1)]
    public void Incomplete_event_history_is_not_readable(bool capHit, bool interrupted, int unreadable)
    {
        Assert.False(DefenderEventParser.FromChannel(new RawChannel("ok", null, [], capHit, interrupted, unreadable)).Readable);
        Assert.True(DefenderEventParser.FromChannel(new RawChannel("ok", null, [], false, false, 0)).Readable);
        Assert.False(DefenderEventParser.FromChannel(new RawChannel("denied", "x", [], false, false, 0)).Readable);
    }

    [Fact]
    public async Task Quick_scan_correlates_scan_id_and_reports_threats_without_remediating()
    {
        var f = new FakeDefender
        {
            After = new(FakeDefender.State(quick: DateTime.UtcNow.AddMinutes(1)), null),
            Exit = 2,
            Events = new(true, [new(1000, "{A}", "quick", Soon), new(1001, "{A}", "quick", Soon.AddMinutes(2))], 2, 1, 0, 0),
        };
        var (o, progress) = await Run(H(DefenderOp.QuickScan, f), f);
        Assert.Equal("completed", o.State);
        Assert.Equal("threats_found", o.Observed["result"]!.GetValue<string>());
        Assert.Equal("{A}", o.Observed["scanId"]!.GetValue<string>());
        Assert.Equal("completed", o.Observed["scanRecord"]!.GetValue<string>());
        Assert.Equal(2, o.Observed["threats"]!["count"]!.GetValue<int>());
        Assert.Equal(1, o.Observed["threats"]!["pua"]!.GetValue<int>());
        Assert.Contains("2 threats", o.ReceiptText);
        Assert.Equal("Scanning · quick", progress[0]);
        Assert.Single(f.Runs); // only the scan; nothing else is started
    }

    [Fact]
    public async Task Scan_clean_completes_and_start_without_completion_is_indeterminate()
    {
        var clean = new FakeDefender { After = new(FakeDefender.State(quick: DateTime.UtcNow.AddMinutes(1)), null) };
        var (o, _) = await Run(H(DefenderOp.QuickScan, clean), clean);
        Assert.Equal("completed", o.State);
        Assert.Equal("Quick scan finished. No threats found.", o.ReceiptText);

        var unconfirmed = new FakeDefender { Events = new(true, [new(1000, "{B}", "quick", Soon)], 0, 0, 0, 0) };
        var (o2, _) = await Run(H(DefenderOp.QuickScan, unconfirmed), unconfirmed);
        Assert.Equal("indeterminate", o2.State);
        Assert.Equal("started", o2.Observed["scanRecord"]!.GetValue<string>());
    }

    [Fact]
    public async Task Scan_stopped_record_fails()
    {
        var f = new FakeDefender { Events = new(true, [new(1000, "{C}", "full", Soon), new(1002, "{C}", "full", Soon.AddMinutes(3))], 0, 0, 0, 0) };
        var (o, _) = await Run(H(DefenderOp.FullScan, f), f, Confirm);
        Assert.Equal("failed", o.State);
        Assert.Equal("scan_stopped", o.Code);
    }

    [Fact]
    public async Task Cancel_kills_the_tool_and_is_indeterminate_with_progress_ticks()
    {
        // No timers: the cancel is sent by the first progress tick itself, so a tick has always been seen and the tool is
        // always running when it arrives, however slowly this machine schedules the test.
        var f = new FakeDefender { Hang = true };
        using var cts = new CancellationTokenSource();
        var h = H(DefenderOp.FullScan, f);
        var res = await h.ResolveAsync(Req(h, Confirm), default);
        Assert.True(res.Eligible, res.IneligibleCode);
        var progress = new List<string>();
        var onTick = new CancelOnTick(progress, cts);
        var o = await h.ExecuteAsync(new ActionExecution("plan-x", "hash", res.Target, res.Before, res.After, Confirm), onTick, cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("indeterminate", o.State);
        Assert.Equal("cancelled", o.Code);
        await f.KilledSignal.Task.WaitAsync(TimeSpan.FromSeconds(60)); // the kill is observed, not assumed to be instant
        Assert.True(f.Killed);
        lock (progress) Assert.Contains(progress, p => p.StartsWith("Scanning · full · ", StringComparison.Ordinal));
    }

    /// <summary>Records progress and cancels as soon as a timed tick ("Scanning · full · N min") arrives.</summary>
    private sealed class CancelOnTick(List<string> lines, CancellationTokenSource cts) : IProgress<string>
    {
        public void Report(string value)
        {
            lock (lines) lines.Add(value);
            if (value.StartsWith("Scanning · full · ", StringComparison.Ordinal)) cts.Cancel();
        }
    }

    [Fact]
    public async Task Execute_rechecks_eligibility_before_running()
    {
        var f = new FakeDefender();
        var h = H(DefenderOp.QuickScan, f);
        var res = await h.ResolveAsync(Req(h), default);
        f.Before = new(FakeDefender.State("passive"), null);
        var o = await h.ExecuteAsync(new ActionExecution("p", "h", res.Target, res.Before, res.After, null), null, default);
        Assert.Equal("failed", o.State);
        Assert.Equal("not_primary", o.Code);
        Assert.Empty(f.Runs);
    }

    [Fact]
    public async Task Read_target_matches_before_shape()
    {
        var f = new FakeDefender();
        var h = H(DefenderOp.QuickScan, f);
        var res = await h.ResolveAsync(Req(h), default);
        Assert.Equal(res.Before.ToJsonString(), (await h.ReadTargetAsync(res.Target, default)).ToJsonString());
    }

    [Fact]
    public void Event_parser_counts_detections_and_scan_kinds_without_names()
    {
        static RawEvent E(int id, params (string, string)[] d) =>
            new("Microsoft-Windows-Windows Defender", id, 4, DateTimeOffset.UtcNow, d.Select(x => new KeyValuePair<string?, string>(x.Item1, x.Item2)).ToList());
        var ev = DefenderEventParser.Parse([
            E(1000, ("Scan ID", "{X}"), ("Scan Parameters", "Quick Scan"), ("Scan Resources", @"C:\secret")),
            E(1001, ("Scan ID", "{X}"), ("Scan Parameters", "Quick Scan")),
            E(1116, ("Detection ID", "{D1}"), ("Threat Name", "Fake:Test/Name"), ("Category Name", "Potentially Unwanted Software")),
            E(1116, ("Detection ID", "{D2}"), ("Threat Name", "Fake:Test/Other"), ("Category Name", "Trojan")),
            E(1116, ("Detection ID", "{D2}"), ("Category Name", "Trojan")),
            E(2001),
        ]);
        Assert.Equal(2, ev.Scans.Count);
        Assert.All(ev.Scans, s => Assert.Equal("quick", s.Kind));
        Assert.Equal(2, ev.Detections);
        Assert.Equal(1, ev.PuaDetections);
        Assert.Equal(1, ev.SignatureFailures);
    }
}

internal static class AsyncListExtensions
{
    public static Task<List<T>> ToAsyncList<T>(this IEnumerable<T> items) => Task.FromResult(items.ToList());
}

using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Engine.Bench;
using OptimizationTool.Transport;

namespace OptimizationTool.Tests;

/// <summary>M6a benchmark runs end to end over the pipe, with in-process fakes only: no test ever runs a real workload.</summary>
public class BenchTests
{
    internal static BenchTimings FastTimings(double plannedSeconds = 3) => new()
    {
        CoolDown = TimeSpan.FromMilliseconds(20), StopGrace = TimeSpan.FromMilliseconds(400), HeartbeatTimeout = TimeSpan.FromMilliseconds(500),
        SampleInterval = TimeSpan.FromMilliseconds(20), SensorReadTimeout = TimeSpan.FromMilliseconds(100), HelloTimeout = TimeSpan.FromSeconds(2),
        Repeat = TimeSpan.FromMilliseconds(500), SnapshotSamples = 3,
        Planned = BenchParts.RunOrder.ToDictionary(p => p, _ => TimeSpan.FromSeconds(plannedSeconds)),
    };

    internal static (BenchOptions Options, FakeWorkerScript Script, FakeSensorHubFactory Sensors, FakeBenchHardware Hardware) Fakes(
        BenchTimings? timings = null, Action<FakeSensorHub>? configure = null)
    {
        var script = new FakeWorkerScript { PartDuration = TimeSpan.FromMilliseconds(120), HeartbeatInterval = TimeSpan.FromMilliseconds(50) };
        var sensors = new FakeSensorHubFactory { Configure = configure };
        var hardware = new FakeBenchHardware();
        return (new BenchOptions
        {
            Sensors = sensors, Hardware = hardware, Launcher = new FakeBenchWorkerLauncher(script), Timings = timings ?? FastTimings(), OptimusVersion = "test",
        }, script, sensors, hardware);
    }

    internal static JsonObject StartParams(string? key = null, params string[] parts)
    {
        var p = new JsonObject { ["idempotencyKey"] = key ?? Guid.NewGuid().ToString() };
        if (parts.Length > 0) p["parts"] = new JsonArray(parts.Select(x => (JsonNode?)x).ToArray());
        return p;
    }

    internal static Task<JsonObject> Finished(Client c, string runId, int timeoutMs = 15000) =>
        c.WaitForEvent(e => e.Str("stream") == "bench:" + runId && e.Str("type") == "bench.finished", timeoutMs);

    internal static async Task<JsonObject> RunToEnd(Client c, JsonObject? p = null)
    {
        var run = await c.Call("bench.start", p ?? StartParams());
        await Finished(c, run.Str("runId"));
        return await c.Call("bench.get", new JsonObject { ["runId"] = run.Str("runId") });
    }

    private static async Task<JsonObject> WaitState(Client c, string runId, Func<JsonObject, bool> match, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var run = await c.Call("bench.get", new JsonObject { ["runId"] = runId });
            if (match(run)) return run;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("state not reached: " + run.ToJsonString());
            await Task.Delay(20);
        }
    }

    private static JsonObject PartOf(JsonObject run, string part) => run["tests"]!.AsArray().First(p => p.Str("part") == part)!.AsObject();
    private static JsonObject ResultPart(JsonObject run, string part) => run["result"]!["parts"]!.AsArray().First(p => p.Str("part") == part)!.AsObject();

    [Fact]
    public async Task FullRun_IsDone_WithScoreVerdictsEvidenceAndStateOnlyEvents()
    {
        var (options, script, sensors, _) = Fakes();
        await using var host = TestHost.StartBench(options);
        var c = await host.ConnectAsync(minor: 5);
        Assert.Equal(5, (int)c.Hello!["protocol"]!["minor"]!);

        var caps = await c.Call("bench.capabilities");
        Assert.Equal("online", caps["power"].Str("ac"));
        Assert.All(caps["parts"]!.AsArray(), p => Assert.True((bool)p!["testable"]!));
        Assert.Contains(caps["sensors"]!.AsArray(), s => s.Str("id") == "gpu.temp" && s.Str("source") == "d3dkmt");
        Assert.Equal("1", caps.Str("benchVersion"));
        Assert.Null(caps["activeRunId"]);

        var run = await RunToEnd(c);
        var runId = run.Str("runId");
        SchemaTests.AssertValid("bench-run", run);
        Assert.Matches("^bench-[0-9a-f]{16}$", runId);
        Assert.Equal("done", run.Str("state"));
        Assert.Equal(new[] { "ram", "cpu", "gpu", "disk" }, run["tests"]!.AsArray().Select(p => p.Str("part")));
        Assert.All(run["tests"]!.AsArray(), p => Assert.Equal("done", p.Str("state")));
        Assert.Equal(4, run["metrics"]!["gpu"]!.AsArray().Count);
        Assert.Equal("gpu.temp", run["metrics"]!["gpu"]![1].Str("id"));
        Assert.Null(run["live"]);

        var result = run["result"]!.AsObject();
        Assert.Equal(10000, (double)result["score"]!);
        Assert.False((bool)result["partial"]!);
        Assert.Null(result["comparison"]); // first run: nothing to compare
        Assert.Null(result["fanGuidance"]);
        foreach (var part in BenchParts.RunOrder)
        {
            var rp = ResultPart(run, part);
            Assert.Equal(1000, (double)rp["score"]!);
            Assert.Equal("no_issue", rp.Str("verdict"));
            Assert.NotEmpty(rp["evidenceIds"]!.AsArray());
        }
        Assert.Contains(ResultPart(run, "cpu")["missingSensors"]!.AsArray(), m => m.Str("id") == "temp" && m.Str("reason") == "needs_advanced_sensors");
        Assert.Equal("d3dkmt", ResultPart(run, "gpu")["figures"]!["temp"].Str("source"));
        Assert.Equal(BenchScoring.References["cpu"]["multi"], (double)ResultPart(run, "cpu")["figures"]!["multi"]!["value"]!);
        Assert.NotNull(ResultPart(run, "disk")["figures"]!["random"]);
        Assert.False((bool)result["differentSetup"]!);

        // Evidence: ev- + H(runId ‖ part ‖ metric), looked up with bench.evidence.
        var evidenceId = "ev-" + Ids.Hash16(runId, "cpu", "single");
        Assert.Contains(ResultPart(run, "cpu")["evidenceIds"]!.AsArray(), e => (string?)e == evidenceId);
        var ev = await c.Call("bench.evidence", new JsonObject { ["runId"] = runId, ["evidenceId"] = evidenceId });
        Assert.Equal("bench.cpu", ev.Str("collectorId"));
        Assert.Equal("performance", ev.Str("category"));
        Assert.Equal("measured", ev.Str("quality"));
        var missing = await c.Fails("bench.evidence", new JsonObject { ["runId"] = runId, ["evidenceId"] = "ev-0000000000000000" });
        Assert.Equal(ErrorCodes.NotFound, missing.Code);

        // Durable events are state changes only; events.read serves the bench stream.
        var events = await c.Call("events.read", new JsonObject { ["stream"] = "bench:" + runId, ["afterSeq"] = 0, ["limit"] = 200 });
        var types = events["events"]!.AsArray().Select(e => e.Str("type")).ToList();
        foreach (var e in events["events"]!.AsArray()) SchemaTests.AssertValid("event", e!.DeepClone());
        Assert.All(types, t => Assert.Contains(t, new[] { "bench.progress", "bench.finished" }));
        Assert.Single(types, "bench.finished");

        var list = await c.Call("bench.list", new JsonObject { ["limit"] = 10 });
        var row = Assert.Single(list["runs"]!.AsArray())!;
        Assert.Equal(runId, row.Str("runId"));
        Assert.Equal(10000, (double)row["score"]!);
        Assert.Equal(4, row["parts"]!.AsArray().Count);

        // The worker got the seam's commands and the folder for disk only; it was told to exit.
        var commands = script.Commands.Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        Assert.Equal(new[] { "ram", "cpu", "gpu", "disk" }, commands.Where(x => x.Str("cmd") == "run").Select(x => x.Str("part")));
        Assert.EndsWith("bench", commands.First(x => x.Str("part") == "disk").Str("folder"));
        Assert.Null(commands.First(x => x.Str("part") == "cpu")["folder"]);
        Assert.Equal("exit", commands[^1].Str("cmd"));
        Assert.All(sensors.Created, h => Assert.True(h.Disposed));
        Assert.True(Directory.Exists(Path.Combine(host.DataDir, "bench")));
    }

    [Fact]
    public async Task FailedStartCommit_ReleasesTheLeaseAndHub()
    {
        var (options, _, sensors, _) = Fakes();
        await using var host = TestHost.StartBench(options);
        var c = await host.ConnectAsync();
        static void Sql(TestHost host, string sql)
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(host.DataDir, OptimizationTool.Engine.Store.FileName)};Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        // The idempotency insert runs after the run row is written and the lease taken: make it fail.
        Sql(host, "CREATE TRIGGER fail_bench BEFORE INSERT ON idempotency WHEN NEW.method = 'bench.start' BEGIN SELECT RAISE(ABORT, 'boom'); END;");
        await c.Fails("bench.start", StartParams());
        Assert.All(sensors.Created, h => Assert.True(h.Disposed));
        Sql(host, "DROP TRIGGER fail_bench;");

        // Nothing stays "bench_active": a scan and a new benchmark both start.
        var run = await RunToEnd(c);
        Assert.Equal("done", run.Str("state"));
        await c.Call("scan.start", Rpc.ScanParams("audit.full", "Begin scan", Guid.NewGuid().ToString()));
    }

    [Fact]
    public async Task SecondRun_IsCompared_PartialRunIsNot()
    {
        var (options, _, _, _) = Fakes();
        await using var host = TestHost.StartBench(options);
        var c = await host.ConnectAsync();
        var first = await RunToEnd(c);
        var second = await RunToEnd(c);
        var comparison = second["result"]!["comparison"]!;
        Assert.Equal(first.Str("runId"), comparison.Str("prevRunId"));
        Assert.Equal(10000, (double)comparison["prevScore"]!);
        Assert.Equal(0, (double)comparison["deltas"]!["cpu"]!["pct"]!);
        Assert.Equal(1000, (double)comparison["deltas"]!["cpu"]!["prev"]!);
        Assert.NotNull(comparison["prevFinishedAt"]);

        var partial = await RunToEnd(c, StartParams(null, "cpu", "ram"));
        Assert.True((bool)partial["result"]!["partial"]!);
        Assert.Equal(10000, (double)partial["result"]!["score"]!);
        Assert.Null(partial["result"]!["comparison"]);
        Assert.Equal(new[] { "ram", "cpu" }, partial["tests"]!.AsArray().Select(p => p.Str("part"))); // run order, not request order
    }

    [Fact]
    public async Task DifferentSetup_IsNotCompared()
    {
        var (options, _, _, hardware) = Fakes();
        await using var host = TestHost.StartBench(options);
        var c = await host.ConnectAsync();
        var first = await RunToEnd(c);
        hardware.Hardware = FakeBenchHardware.Default with { GpuDriverVersion = "99.0" };
        var second = await RunToEnd(c);
        Assert.NotEqual(first.Str("configId"), second.Str("configId"));
        Assert.Null(second["result"]!["comparison"]);
        Assert.True((bool)second["result"]!["differentSetup"]!);
    }

    [Fact]
    public async Task Start_IsIdempotent_AndValidatesParts()
    {
        var (options, _, _, hardware) = Fakes();
        await using var host = TestHost.StartBench(options);
        var c = await host.ConnectAsync();

        var a = await c.Call("bench.start", StartParams("k1", "cpu"));
        var b = await c.Call("bench.start", StartParams("k1", "cpu"));
        Assert.Equal(a.Str("runId"), b.Str("runId"));
        Assert.Equal(ErrorCodes.IdempotencyMismatch, (await c.Fails("bench.start", StartParams("k1", "gpu"))).Code);
        await Finished(c, a.Str("runId"));

        foreach (var bad in new JsonNode?[] { new JsonArray(), new JsonArray("cpu", "cpu"), new JsonArray("fan"), JsonValue.Create("cpu"), new JsonArray(1) })
        {
            var e = await c.Fails("bench.start", new JsonObject { ["idempotencyKey"] = Guid.NewGuid().ToString(), ["parts"] = bad });
            Assert.Equal(ErrorCodes.InvalidParams, e.Code);
        }
        Assert.Equal(ErrorCodes.InvalidParams, (await c.Fails("bench.start", new JsonObject())).Code); // no key

        hardware.Hardware = FakeBenchHardware.Default with { GpuModel = null, GpuLuid = null, DiskFreeBytes = 1L << 30 };
        var notTestable = await c.Fails("bench.start", StartParams(null, "gpu"));
        Assert.Equal(ErrorCodes.InvalidParams, notTestable.Code);
        var caps = await c.Call("bench.capabilities");
        Assert.Equal("no_gpu", caps["parts"]!.AsArray().First(p => p.Str("id") == "gpu").Str("reason"));
        Assert.Equal("needs_space", caps["parts"]!.AsArray().First(p => p.Str("id") == "disk").Str("reason"));
        var run = await RunToEnd(c); // omitted parts = all testable
        Assert.Equal(new[] { "ram", "cpu" }, run["tests"]!.AsArray().Select(p => p.Str("part")));

        Assert.Equal(ErrorCodes.NotFound, (await c.Fails("bench.get", new JsonObject { ["runId"] = "bench-0000000000000000" })).Code);
        Assert.Equal(ErrorCodes.InvalidParams, (await c.Fails("bench.list", new JsonObject { ["limit"] = 51 })).Code);
    }

    [Theory]
    [InlineData("offline", "absent", true)]
    [InlineData("unknown", "present", true)]
    [InlineData("unknown", "absent", false)]
    [InlineData("online", "present", false)]
    public async Task Power_BlocksStartOnBattery(string ac, string battery, bool blocked)
    {
        var (options, _, _, _) = Fakes(configure: h => h.PowerState = new PowerState(ac, battery));
        await using var host = TestHost.StartBench(options);
        var c = await host.ConnectAsync();
        if (blocked)
        {
            var e = await c.Fails("bench.start", StartParams(null, "cpu"));
            Assert.Equal(ErrorCodes.InvalidState, e.Code);
            Assert.Equal("on_battery", e.ErrorData.Str("reason"));
            Assert.Null(host.Server.Engine.Lease.Current); // nothing left held
        }
        else Assert.Equal("done", (await RunToEnd(c, StartParams(null, "cpu"))).Str("state"));
    }

    [Fact]
    public async Task Cancel_DuringAPart_StopsTheWorker_AndEndsCancelled()
    {
        // No short timers decide the outcome: the watchdog, the hard deadline and the stop grace are far longer than
        // the test (they only bound a real failure), and the live assertions wait for the state they check.
        var roomy = FastTimings(plannedSeconds: 60) with { HeartbeatTimeout = TimeSpan.FromSeconds(60), StopGrace = TimeSpan.FromSeconds(60) };
        var (options, script, _, _) = Fakes(roomy);
        script.Hang.Add("cpu");
        await using var host = TestHost.StartBench(options);
        var c = await host.ConnectAsync();
        var run = await c.Call("bench.start", StartParams());
        var runId = run.Str("runId");
        // The part turns "running" before its live buffer is reset and before the sampler's first read: wait for a sample.
        var running = await WaitState(c, runId, r => PartOf(r, "cpu").Str("state") == "running" && r["live"] is JsonObject live
            && live.Str("part") == "cpu" && live["samples"]?["cpu.clock"] is JsonArray { Count: > 0 }, timeoutMs: 60000);
        Assert.Equal("running", running.Str("state"));
        Assert.Equal("cpu", running["live"]!.Str("part"));
        SchemaTests.AssertValid("bench-run", running);
        Assert.NotEmpty(running["live"]!["samples"]!["cpu.clock"]!.AsArray());
        Assert.Equal("done", PartOf(running, "ram").Str("state"));

        var cancel = await c.Call("bench.cancel", new JsonObject { ["idempotencyKey"] = "c1", ["runId"] = runId });
        Assert.Contains(cancel.Str("state"), new[] { "stopping", "cancelled" });
        await Finished(c, runId, timeoutMs: 60000);
        var end = await c.Call("bench.get", new JsonObject { ["runId"] = runId });
        Assert.Equal("cancelled", end.Str("state"));
        Assert.Equal("cancelled", PartOf(end, "cpu").Str("state"));
        Assert.Equal("skipped", PartOf(end, "gpu").Str("state"));
        Assert.Null(end["result"]);
        Assert.Contains(script.Commands, l => l.Contains("\"stop\"", StringComparison.Ordinal));
        Assert.Equal(0, script.Kills); // it answered "stopped" within the grace period

        // Replaying the cancel returns the run as it is; a done/cancelled run is not listed.
        Assert.Equal("cancelled", (await c.Call("bench.cancel", new JsonObject { ["idempotencyKey"] = "c1", ["runId"] = runId })).Str("state"));
        Assert.Empty((await c.Call("bench.list"))["runs"]!.AsArray());
        Assert.Null(host.Server.Engine.Lease.Current);
        var events = await c.Call("events.read", new JsonObject { ["stream"] = "bench:" + runId });
        Assert.Contains(events["events"]!.AsArray(), e => e.Str("type") == "bench.progress" && e!["payload"].Str("state") == "stopping");
    }

    [Fact]
    public async Task Cancel_DuringCoolDown_SkipsTheRest()
    {
        var timings = FastTimings() with { CoolDown = TimeSpan.FromSeconds(5) };
        var (options, _, _, _) = Fakes(timings);
        await using var host = TestHost.StartBench(options);
        var c = await host.ConnectAsync();
        var runId = (await c.Call("bench.start", StartParams(null, "ram", "cpu"))).Str("runId");
        await WaitState(c, runId, r => PartOf(r, "ram").Str("state") == "done");
        await c.Call("bench.cancel", new JsonObject { ["idempotencyKey"] = Guid.NewGuid().ToString(), ["runId"] = runId });
        await Finished(c, runId, 4000); // well before the 5 s cool-down would end
        var end = await c.Call("bench.get", new JsonObject { ["runId"] = runId });
        Assert.Equal("cancelled", end.Str("state"));
        Assert.Equal("skipped", PartOf(end, "cpu").Str("state"));
    }

    [Fact]
    public async Task Cancel_WorkerIgnoresStop_IsKilledAfterGrace()
    {
        var (options, script, _, _) = Fakes();
        script.Hang.Add("ram");
        script.IgnoreStop = true;
        await using var host = TestHost.StartBench(options);
        var c = await host.ConnectAsync();
        var runId = (await c.Call("bench.start", StartParams(null, "ram"))).Str("runId");
        await WaitState(c, runId, r => PartOf(r, "ram").Str("state") == "running");
        await c.Call("bench.cancel", new JsonObject { ["idempotencyKey"] = Guid.NewGuid().ToString(), ["runId"] = runId });
        await Finished(c, runId);
        Assert.Equal("cancelled", (await c.Call("bench.get", new JsonObject { ["runId"] = runId })).Str("state"));
        Assert.True(script.Kills >= 1);
    }

    [Fact]
    public async Task WorkerCrash_FailsThatPart_TheNextPartGetsANewWorker()
    {
        var (options, script, _, _) = Fakes();
        script.Crash.Add("cpu");
        await using var host = TestHost.StartBench(options);
        var c = await host.ConnectAsync();
        var run = await RunToEnd(c);
        Assert.Equal("done", run.Str("state"));
        Assert.Equal("failed", PartOf(run, "cpu").Str("state"));
        Assert.Equal("worker_exited", PartOf(run, "cpu").Str("reason"));
        Assert.Equal("done", PartOf(run, "gpu").Str("state"));
        Assert.Equal(2, script.Launches);
        Assert.True((bool)run["result"]!["partial"]!);
        Assert.Equal("unmeasured", ResultPart(run, "cpu").Str("verdict"));
        Assert.Null(ResultPart(run, "cpu")["score"]);
    }

    [Fact]
    public async Task NoHeartbeat_KillsTheWorker_PartFailed()
    {
        var (options, script, _, _) = Fakes();
        script.Silent.Add("gpu");
        script.Hang.Add("gpu");
        await using var host = TestHost.StartBench(options);
        var c = await host.ConnectAsync();
        var run = await RunToEnd(c, StartParams(null, "gpu", "disk"));
        Assert.Equal("failed", PartOf(run, "gpu").Str("state"));
        Assert.Equal("no_heartbeat", PartOf(run, "gpu").Str("reason"));
        Assert.Equal("done", PartOf(run, "disk").Str("state"));
        Assert.True(script.Kills >= 1);
    }

    [Fact]
    public async Task HardDeadline_KillsTheWorker_PartFailed()
    {
        var (options, script, _, _) = Fakes(FastTimings(plannedSeconds: 0.3));
        script.Hang.Add("ram");
        await using var host = TestHost.StartBench(options);
        var c = await host.ConnectAsync();
        var run = await RunToEnd(c, StartParams(null, "ram"));
        Assert.Equal("failed", run.Str("state")); // nothing measured
        Assert.Equal("nothing_measured", run.Str("reason"));
        Assert.Equal("deadline", PartOf(run, "ram").Str("reason"));
        Assert.True(script.Kills >= 1);
    }

    [Fact]
    public async Task WorkerFailure_IsRecordedWithItsReason()
    {
        var (options, script, _, _) = Fakes();
        script.Fail["disk"] = "too_slow";
        await using var host = TestHost.StartBench(options);
        var c = await host.ConnectAsync();
        var run = await RunToEnd(c, StartParams(null, "cpu", "disk"));
        Assert.Equal("too_slow", PartOf(run, "disk").Str("reason"));
        Assert.Equal("unmeasured", ResultPart(run, "disk").Str("verdict"));
        Assert.Equal("too_slow", ResultPart(run, "disk").Str("reason"));
    }

    [Fact]
    public async Task AcGoesOffline_MidRun_StopsOnBattery()
    {
        var (options, script, sensors, _) = Fakes();
        script.Hang.Add("cpu");
        await using var host = TestHost.StartBench(options);
        var c = await host.ConnectAsync();
        var runId = (await c.Call("bench.start", StartParams())).Str("runId");
        await WaitState(c, runId, r => PartOf(r, "cpu").Str("state") == "running");
        var hub = sensors.Created.Last();
        hub.SetPower(new PowerState("offline", "present"));
        await Finished(c, runId);
        var end = await c.Call("bench.get", new JsonObject { ["runId"] = runId });
        Assert.Equal("stopped_on_battery", end.Str("state"));
        Assert.Equal("on_battery", end.Str("reason"));
        Assert.Equal("on_battery", PartOf(end, "cpu").Str("reason"));
        Assert.Equal("skipped", PartOf(end, "disk").Str("state"));
    }

    [Fact]
    public async Task PowerLost_WithoutNotification_IsCaughtByThePoll()
    {
        var (options, script, sensors, _) = Fakes();
        script.Hang.Add("ram");
        await using var host = TestHost.StartBench(options);
        var c = await host.ConnectAsync();
        var runId = (await c.Call("bench.start", StartParams(null, "ram"))).Str("runId");
        await WaitState(c, runId, r => PartOf(r, "ram").Str("state") == "running");
        sensors.Created.Last().PowerState = new PowerState("unknown", "present"); // status lost on a PC with a battery
        await Finished(c, runId);
        Assert.Equal("stopped_on_battery", (await c.Call("bench.get", new JsonObject { ["runId"] = runId })).Str("state"));
    }

    [Fact]
    public async Task ThermalStop_StopsOnlyTheGpuPart_RunningHot_WithFanGuidance()
    {
        var (options, script, _, _) = Fakes(configure: h => h.Sample = (part, i) => part == "gpu"
            ? new SensorSample(new Dictionary<string, double?> { ["temp"] = 80 + i, ["clock"] = 2.4 }, new GpuThrottle(90, false, false), null)
            : FakeSensorHub.DefaultSample(part, i));
        script.Hang.Add("gpu");
        await using var host = TestHost.StartBench(options);
        var c = await host.ConnectAsync();
        var run = await RunToEnd(c);
        Assert.Equal("done", run.Str("state"));
        Assert.Equal("cancelled", PartOf(run, "gpu").Str("state"));
        Assert.Equal("thermal_stop", PartOf(run, "gpu").Str("reason"));
        Assert.Equal("done", PartOf(run, "disk").Str("state"));
        var gpu = ResultPart(run, "gpu");
        Assert.Equal("running_hot", gpu.Str("verdict"));
        SchemaTests.AssertValid("bench-run", run);
        Assert.Null(gpu["score"]);
        var guidance = run["result"]!["fanGuidance"]!;
        Assert.Equal("any", guidance.Str("brand"));
        Assert.Equal(4, guidance["tips"]!.AsArray().Count);
        Assert.True((bool)guidance["pump"]!);
        Assert.Single(script.Commands, l => l.Contains("\"part\":\"gpu\"", StringComparison.Ordinal)); // never repeated
    }

    [Fact]
    public async Task SlowerThanBefore_IsConfirmedByOneRepeat()
    {
        var (options, script, _, _) = Fakes();
        await using var host = TestHost.StartBench(options);
        var c = await host.ConnectAsync();
        for (var i = 0; i < 3; i++) await RunToEnd(c, StartParams(null, "cpu", "disk"));

        // Now multi-thread is at half and stays there on the repeat.
        var cpuRef = BenchScoring.References["cpu"];
        script.Results = part => part == "cpu" ? new Dictionary<string, double> { ["single"] = cpuRef["single"], ["multi"] = cpuRef["multi"] / 2 } : BenchScoring.References[part];
        var slow = await RunToEnd(c, StartParams(null, "cpu", "disk"));
        var cpu = ResultPart(slow, "cpu");
        Assert.Equal("slower_than_before", cpu.Str("verdict"));
        Assert.Contains("repeat_confirmed", Engine.Json.Strings(cpu["why"]));
        var repeats = script.Commands.Select(l => JsonNode.Parse(l)!.AsObject()).Where(x => x["repeat"] is not null).ToList();
        Assert.Equal("multi", Assert.Single(repeats).Str("repeat"));

        // The repeat comes back normal: not confirmed → no_issue with the neutral observation.
        script.RepeatValue = (_, _) => cpuRef["multi"];
        var unconfirmed = await RunToEnd(c, StartParams(null, "cpu", "disk"));
        var cpu2 = ResultPart(unconfirmed, "cpu");
        Assert.Equal("no_issue", cpu2.Str("verdict"));
        Assert.Contains(cpu2["observations"]!.AsArray(), o => o.Str("id") == "lower_than_before_unconfirmed");
    }

    [Fact]
    public async Task Disk_IsNeverRepeated()
    {
        var (options, script, _, _) = Fakes();
        await using var host = TestHost.StartBench(options);
        var c = await host.ConnectAsync();
        for (var i = 0; i < 2; i++) await RunToEnd(c, StartParams(null, "disk"));
        script.Results = part => part == "disk" ? new Dictionary<string, double> { ["seqWrite"] = 200, ["seqRead"] = 300, ["rand4k"] = 5 } : BenchScoring.References[part];
        var run = await RunToEnd(c, StartParams(null, "disk"));
        Assert.DoesNotContain(script.Commands, l => l.Contains("\"repeat\":\"", StringComparison.Ordinal));
        var disk = ResultPart(run, "disk");
        Assert.Equal("no_issue", disk.Str("verdict"));
        Assert.Contains(disk["observations"]!.AsArray(), o => o.Str("id") == "lower_than_before_unconfirmed");
        Assert.Contains(disk["observations"]!.AsArray(), o => o.Str("id") == "disk_below_typical_for_type");
    }

    [Fact]
    public async Task HostRestart_TurnsUnfinishedRowsInterrupted_AndPrunesOldOnes()
    {
        var dir = TestHost.NewDataDir();
        using (var store = Store.Open(dir))
            store.Write(db =>
            {
                foreach (var (id, state, at) in new[] { ("bench-00000000000000aa", "running", "2026-11-09T10:00:00.000Z"),
                                                       ("bench-00000000000000bb", "cancelled", "2020-01-01T00:00:00.000Z") })
                {
                    var body = new JsonObject
                    {
                        ["runId"] = id, ["state"] = state, ["reason"] = null, ["startedAt"] = at, ["finishedAt"] = null, ["benchVersion"] = "1",
                        ["configId"] = "cfg-x", ["tests"] = new JsonArray(new JsonObject { ["part"] = "cpu", ["state"] = "running", ["reason"] = null },
                            new JsonObject { ["part"] = "gpu", ["state"] = "queued", ["reason"] = null }), ["metrics"] = new JsonObject(),
                        ["result"] = null,
                    };
                    db.Exec("INSERT INTO bench_runs (run_id, state, started_at, bench_version, config_id, body) VALUES ($r, $s, $a, '1', 'cfg-x', $b)",
                        ("$r", id), ("$s", state), ("$a", at), ("$b", body.ToJsonString()));
                }
            });
        var (options, _, _, _) = Fakes();
        await using var host = TestHost.StartBench(options, dataDir: dir);
        var c = await host.ConnectAsync();
        var run = await c.Call("bench.get", new JsonObject { ["runId"] = "bench-00000000000000aa" });
        Assert.Equal("interrupted", run.Str("state"));
        Assert.Equal("cancelled", PartOf(run, "cpu").Str("state"));
        Assert.Equal("skipped", PartOf(run, "gpu").Str("state"));
        Assert.Equal(ErrorCodes.NotFound, (await c.Fails("bench.get", new JsonObject { ["runId"] = "bench-00000000000000bb" })).Code); // kept 7 days only
        await host.StopAsync();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task HostShutdown_MidRun_EndsInterrupted()
    {
        var (options, script, _, _) = Fakes();
        script.Hang.Add("cpu");
        await using var host = TestHost.StartBench(options);
        var c = await host.ConnectAsync();
        var runId = (await c.Call("bench.start", StartParams())).Str("runId");
        await WaitState(c, runId, r => PartOf(r, "cpu").Str("state") == "running");
        await host.RestartAsync();
        c = await host.ConnectAsync();
        Assert.Equal("interrupted", (await c.Call("bench.get", new JsonObject { ["runId"] = runId })).Str("state"));
    }

    // ---------------------------------------------------------------- lease symmetry

    [Fact]
    public async Task Lease_BenchBlocksScan_ScanBlocksBench()
    {
        var (options, script, _, _) = Fakes();
        script.Hang.Add("cpu");
        await using var host = TestHost.StartBench(options, speed: 1); // fixture scans take a few seconds
        var c = await host.ConnectAsync();

        var runId = (await c.Call("bench.start", StartParams(null, "cpu"))).Str("runId");
        var scanBusy = await c.Fails("scan.start", Rpc.ScanParams("audit.full", "Begin scan", Guid.NewGuid().ToString()));
        Assert.Equal(ErrorCodes.InvalidState, scanBusy.Code);
        Assert.Equal("bench_active", scanBusy.ErrorData.Str("reason"));
        Assert.Equal(runId, scanBusy.ErrorData.Str("runId"));
        var benchBusy = await c.Fails("bench.start", StartParams(null, "ram"));
        Assert.Equal("bench_active", benchBusy.ErrorData.Str("reason"));
        var caps = await c.Call("bench.capabilities");
        Assert.Equal(runId, caps.Str("activeRunId"));
        Assert.All(caps["parts"]!.AsArray(), p => Assert.Equal("busy", p.Str("reason")));
        await c.Call("bench.cancel", new JsonObject { ["idempotencyKey"] = Guid.NewGuid().ToString(), ["runId"] = runId });
        await Finished(c, runId);

        var scan = await c.Call("scan.start", Rpc.ScanParams("audit.full", "Begin scan", Guid.NewGuid().ToString()));
        var busy = await c.Fails("bench.start", StartParams(null, "cpu"));
        Assert.Equal(ErrorCodes.ScanActive, busy.Code);
        Assert.Equal(scan.Str("scanId"), busy.ErrorData.Str("scanId"));
        await c.Call("scan.cancel", new JsonObject { ["scanId"] = scan.Str("scanId") });
        script.Hang.Clear();
        Assert.Equal("done", (await RunToEnd(c, StartParams(null, "cpu"))).Str("state")); // the lease came back
    }

    [Fact]
    public async Task Lease_ActionExecutionBlocksBench_AndBenchBlocksActionExecution()
    {
        var pc = new FakePc();
        var (options, script, _, _) = Fakes();
        await using var host = TestHost.StartBench(options, modules: [], actions: [new FakeActionModule(new FakeSchemeHandler(pc), new FakeScanHandler(pc))]);
        var c = await host.ConnectAsync(minor: 5);

        // A Defender scan executing holds the lease: bench.start gets action_active with its operation.
        var scanPlan = await Act.Preview(c, "defender.quick_scan", scheme: null);
        var op = await Act.Approve(c, scanPlan);
        var busy = await c.Fails("bench.start", StartParams(null, "cpu"));
        Assert.Equal("action_active", busy.ErrorData.Str("reason"));
        Assert.Equal(op.Str("operationId"), busy.ErrorData.Str("operationId"));
        pc.ProviderGate.TrySetResult();
        await c.WaitForEvent(e => e.Str("type") == "operation.state" && e["payload"].Str("state") == "verified");
        await WaitUntil(() => host.Server.Engine.Lease.Current is null);

        // A benchmark running: approving power.select_scheme is refused busy and the plan stays waiting.
        script.Hang.Add("cpu");
        var runId = (await c.Call("bench.start", StartParams(null, "cpu"))).Str("runId");
        var plan = await Act.Preview(c);
        var refused = await c.Fails("action.approve", Act.ApproveParams(plan, await Act.Nonce(c, plan.Str("planId"))));
        Assert.Equal("bench_active", refused.ErrorData.Str("reason"));
        Assert.Equal("awaiting_user", (await c.Call("action.get", new JsonObject { ["planId"] = plan.Str("planId") })).Str("status"));
        Assert.Equal(0, pc.Writes);
        await c.Call("bench.cancel", new JsonObject { ["idempotencyKey"] = Guid.NewGuid().ToString(), ["runId"] = runId });
        await Finished(c, runId);
        await Act.Approve(c, plan); // now it runs
        Assert.Equal(1, pc.Writes);
    }

    [Fact]
    public async Task Lease_SnapshotIsRefusedDuringABenchmark_AndBlocksABenchmarkWhileItRuns()
    {
        var (options, script, sensors, _) = Fakes(FastTimings() with { SampleInterval = TimeSpan.FromMilliseconds(200), SnapshotSamples = 4 });
        script.Hang.Add("cpu");
        await using var host = TestHost.StartBench(options);
        var c = await host.ConnectAsync();
        var bench = host.Server.Engine.Bench;

        var snapshot = await bench.SnapshotAsync("call-1", default);
        Assert.Equal("complete", snapshot!.Status);
        Assert.Contains(snapshot.Facts, f => f.Subject == "sensors.gpu.temp");
        Assert.Contains(snapshot.Facts, f => f.Subject == "sensors.cpu.clock");
        Assert.DoesNotContain(snapshot.Facts, f => f.Subject.StartsWith("sensors.cpu.temp", StringComparison.Ordinal)); // unavailable slot

        // While a snapshot runs, bench.start is busy (snapshot_active).
        var running = bench.SnapshotAsync("call-2", default);
        await WaitUntil(() => host.Server.Engine.Lease.Current is { Kind: WorkLease.Snapshot });
        var busy = await c.Fails("bench.start", StartParams(null, "cpu"));
        Assert.Equal("snapshot_active", busy.ErrorData.Str("reason"));
        await running;

        var runId = (await c.Call("bench.start", StartParams(null, "cpu"))).Str("runId");
        var refused = await Assert.ThrowsAsync<BenchBusyException>(() => bench.SnapshotAsync("call-3", default));
        Assert.Equal(runId, refused.Holder.Id);
        await c.Call("bench.cancel", new JsonObject { ["idempotencyKey"] = Guid.NewGuid().ToString(), ["runId"] = runId });
        await Finished(c, runId);
    }

    [Fact]
    public async Task Snapshot_HasOneDeadline_AndReportsWhatItRead()
    {
        // 50 samples at 100 ms would take 5 s per part; the 400 ms deadline ends the call with what was read so far.
        var (options, _, _, _) = Fakes(FastTimings() with { SampleInterval = TimeSpan.FromMilliseconds(100), SnapshotSamples = 50, SnapshotDeadline = TimeSpan.FromMilliseconds(400) });
        await using var host = TestHost.StartBench(options);
        var bench = host.Server.Engine.Bench;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var snapshot = await bench.SnapshotAsync("call-1", default);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"took {watch.Elapsed}");
        Assert.Equal("complete", snapshot!.Status);
        Assert.Contains(snapshot.Facts, f => f.Subject == "sensors.cpu.clock");
        Assert.Null(host.Server.Engine.Lease.Current);
    }

    [Fact]
    public async Task Snapshot_SlowPreparation_EndsAtTheDeadline_AndCancelReleasesTheLease()
    {
        var (options, _, sensors, _) = Fakes(FastTimings() with { SnapshotDeadline = TimeSpan.FromMilliseconds(300) },
            hub => hub.PrepareDelay = TimeSpan.FromMinutes(5));
        await using var host = TestHost.StartBench(options);
        var bench = host.Server.Engine.Bench;

        // The deadline covers preparation: a probe that would take minutes ends the call as failed, lease released.
        var late = await bench.SnapshotAsync("call-1", default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("failed", late!.Status);
        Assert.Null(host.Server.Engine.Lease.Current);
        await WaitUntil(() => sensors.Created.All(h => h.Disposed));

        // A caller that cancels during preparation gets its cancellation at once, and the lease is free again.
        using var cts = new CancellationTokenSource();
        var before = sensors.Created.Count;
        var running = bench.SnapshotAsync("call-2", cts.Token);
        await WaitUntil(() => sensors.Created.Count > before && sensors.Created[^1].Prepares > 0);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Null(host.Server.Engine.Lease.Current);
        await WaitUntil(() => sensors.Created.All(h => h.Disposed));
    }

    [Fact]
    public async Task SensorSourceLostMidRun_IsListedAsMissing_LiveAndInTheResult()
    {
        // The GPU fan source stops answering during the part: from the 2nd sample on the hub reports it as failed.
        var (options, script, _, _) = Fakes(configure: hub => hub.Sample = (part, i) =>
        {
            var s = FakeSensorHub.DefaultSample(part, i);
            if (part != BenchParts.Gpu || i < 1) return s;
            var values = s.Values.ToDictionary(kv => kv.Key, kv => kv.Key == "fan" ? null : kv.Value);
            return s with { Values = values, Failed = ["fan"] };
        });
        script.PartDuration = TimeSpan.FromMilliseconds(500); // room for several samples
        await using var host = TestHost.StartBench(options);
        var c = await host.ConnectAsync();
        var run = await RunToEnd(c, StartParams(null, "gpu"));
        var fan = run["metrics"]!["gpu"]!.AsArray().First(m => m.Str("id") == "gpu.fan")!;
        Assert.False((bool)fan["available"]!);
        Assert.Equal("source_failed", fan.Str("reason"));
        var missing = ResultPart(run, "gpu")["missingSensors"]!.AsArray();
        Assert.Contains(missing, m => m.Str("id") == "fan" && m.Str("reason") == "source_failed");
        Assert.DoesNotContain(missing, m => m.Str("id") == "temp");
    }

    internal static async Task WaitUntil(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Unsupported_WithoutSeams()
    {
        await using var host = TestHost.Start();
        var c = await host.ConnectAsync();
        var caps = await c.Call("bench.capabilities");
        Assert.All(caps["parts"]!.AsArray(), p => Assert.Equal("unsupported", p.Str("reason")));
        var e = await c.Fails("bench.start", StartParams());
        Assert.Equal("unsupported", e.ErrorData.Str("reason"));
    }

    [Fact]
    public async Task AgentClients_CannotCallBenchMethods()
    {
        var (options, _, _, _) = Fakes();
        await using var host = TestHost.StartBench(options);
        var agent = await host.ConnectAsync(kind: "agent");
        Assert.Equal(ErrorCodes.MethodNotAllowed, (await agent.Fails("bench.start", StartParams())).Code);
        Assert.Contains("bench.start", OptimizationTool.Host.HostServer.Allowlists["desktop"]);
    }
}

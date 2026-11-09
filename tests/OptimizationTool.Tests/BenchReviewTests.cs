using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Engine.Bench;

namespace OptimizationTool.Tests;

/// <summary>Fixes from the M6a engine review: power memory, thermal consistency, comparable history, live points, capabilities cache, citations.</summary>
public class BenchReviewTests
{
    private static JsonObject Get(string runId) => new() { ["runId"] = runId };
    private static JsonObject TestOf(JsonObject run, string part) => run["tests"]!.AsArray().First(p => p.Str("part") == part)!.AsObject();
    private static JsonObject ResultPart(JsonObject run, string part) => run["result"]!["parts"]!.AsArray().First(p => p.Str("part") == part)!.AsObject();

    private static async Task<JsonObject> WaitRunning(Client c, string runId, string part)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var run = await c.Call("bench.get", Get(runId));
            if (TestOf(run, part).Str("state") == "running") return run;
            if (DateTime.UtcNow > deadline) throw new TimeoutException(run.ToJsonString());
            await Task.Delay(20);
        }
    }

    // ---------------------------------------------------------------- 1. power

    [Fact]
    public async Task Power_BatteryThenUnknownUnknown_MidRun_StopsOnBattery()
    {
        var (options, script, sensors, _) = BenchTests.Fakes(configure: h => h.PowerState = new PowerState("online", "present"));
        script.Hang.Add("cpu");
        await using var host = TestHost.StartBench(options);
        var c = await host.ConnectAsync();
        var runId = (await c.Call("bench.start", BenchTests.StartParams(null, "cpu"))).Str("runId");
        await WaitRunning(c, runId, "cpu");
        sensors.Created.Last().PowerState = new PowerState("unknown", "unknown"); // GetSystemPowerStatus failed
        await BenchTests.Finished(c, runId);
        var end = await c.Call("bench.get", Get(runId));
        Assert.Equal("stopped_on_battery", end.Str("state"));
        Assert.Equal("on_battery", end.Str("reason"));
    }

    [Fact]
    public async Task Power_UnknownUnknown_IsRefused_OnceThisSessionSawABattery_ButAllowedOnADesktop()
    {
        var power = new PowerState("unknown", "unknown");
        var (options, _, sensors, _) = BenchTests.Fakes();
        sensors.Configure = h => h.PowerState = power;
        await using var host = TestHost.StartBench(options);
        var c = await host.ConnectAsync();

        // No battery ever seen: the desktop case is allowed.
        Assert.Equal("done", (await BenchTests.RunToEnd(c, BenchTests.StartParams(null, "cpu"))).Str("state"));

        power = new PowerState("online", "present");
        var caps = await c.Call("bench.capabilities");
        Assert.Equal("present", caps["power"].Str("battery"));

        power = new PowerState("unknown", "unknown");
        var e = await c.Fails("bench.start", BenchTests.StartParams(null, "cpu"));
        Assert.Equal("on_battery", e.ErrorData.Str("reason"));
        Assert.Equal("present", (await c.Call("bench.capabilities"))["power"].Str("battery"));
    }

    [Fact]
    public async Task Power_BatteryFoundByCapabilitiesDuringARun_ThenUnknown_StopsOnBattery()
    {
        var power = new PowerState("unknown", "unknown");
        var (options, script, sensors, _) = BenchTests.Fakes();
        sensors.Configure = h => h.PowerState = power;
        script.Hang.Add("cpu");
        await using var host = TestHost.StartBench(options);
        var c = await host.ConnectAsync();
        var runId = (await c.Call("bench.start", BenchTests.StartParams(null, "cpu"))).Str("runId"); // allowed: nothing known yet
        await WaitRunning(c, runId, "cpu");
        Assert.Equal("running", (await c.Call("bench.get", Get(runId))).Str("state"));

        power = new PowerState("online", "present");
        Assert.Equal("present", (await c.Call("bench.capabilities"))["power"].Str("battery")); // the run's own hub still reads unknown/unknown
        await BenchTests.Finished(c, runId);
        Assert.Equal("stopped_on_battery", (await c.Call("bench.get", Get(runId))).Str("state"));
    }

    [Fact]
    public void Blocks_UsesKnownBattery()
    {
        Assert.True(BenchService.Blocks(new PowerState("offline", "absent"), false));
        Assert.True(BenchService.Blocks(new PowerState("unknown", "present"), false));
        Assert.True(BenchService.Blocks(new PowerState("unknown", "unknown"), true));
        Assert.False(BenchService.Blocks(new PowerState("unknown", "unknown"), false));
        Assert.False(BenchService.Blocks(new PowerState("online", "present"), true));
    }

    // ---------------------------------------------------------------- 2. thermal

    private static SensorSample HotGpu(int i) =>
        new(new Dictionary<string, double?> { ["temp"] = 80 + i, ["clock"] = 2.4 }, new GpuThrottle(90, false, false), null);

    [Fact]
    public async Task GpuOnlyThermalStop_IsDone_WithResultRunningHotEvidenceAndGuidance()
    {
        var (options, script, _, _) = BenchTests.Fakes(configure: h => h.Sample = (part, i) => part == "gpu" ? HotGpu(i) : FakeSensorHub.DefaultSample(part, i));
        script.Hang.Add("gpu");
        await using var host = TestHost.StartBench(options);
        var c = await host.ConnectAsync();
        var run = await BenchTests.RunToEnd(c, BenchTests.StartParams(null, "gpu"));
        SchemaTests.AssertValid("bench-run", run);
        Assert.Equal("done", run.Str("state"));
        var result = run["result"]!;
        Assert.Null(result["score"]);
        Assert.True((bool)result["partial"]!);
        Assert.Empty(result["partsScored"]!.AsArray());
        var gpu = ResultPart(run, "gpu");
        Assert.Equal("running_hot", gpu.Str("verdict"));
        Assert.NotEmpty(gpu["evidenceIds"]!.AsArray()); // the sensor readings that show it
        Assert.NotNull(result["fanGuidance"]);
        Assert.Single((await c.Call("bench.list"))["runs"]!.AsArray());
    }

    [Fact]
    public async Task ThermallyStoppedRepeat_UnscoresThePart()
    {
        var gpuRef = BenchScoring.References["gpu"];
        var (options, script, _, _) = BenchTests.Fakes(configure: h => h.Sample = (part, i) =>
            part == "gpu" && h.Prepares >= 2 ? HotGpu(9) : FakeSensorHub.DefaultSample(part, i)); // hot only during the repeat
        await using var host = TestHost.StartBench(options);
        var c = await host.ConnectAsync();
        for (var i = 0; i < 3; i++) await BenchTests.RunToEnd(c, BenchTests.StartParams(null, "gpu"));

        script.Results = part => part == "gpu" ? new Dictionary<string, double> { ["fma"] = gpuRef["fma"] / 2, ["copy"] = gpuRef["copy"] } : BenchScoring.References[part];
        script.Hang.Clear();
        var slow = await BenchTests.RunToEnd(c, BenchTests.StartParams(null, "gpu"));
        Assert.Contains(script.Commands, l => l.Contains("\"repeat\":\"fma\"", StringComparison.Ordinal));
        var gpu = ResultPart(slow, "gpu");
        Assert.Equal("running_hot", gpu.Str("verdict"));
        Assert.Null(gpu["score"]);
        Assert.Null(slow["result"]!["score"]);
        Assert.True((bool)slow["result"]!["partial"]!);
        Assert.Null(slow["result"]!["comparison"]);
        Assert.Equal("thermal_stop", TestOf(slow, "gpu").Str("reason"));
        Assert.Equal(89, (double)gpu["figures"]!["temp"]!["value"]!); // the repeat's peak, not the cooler first test
        var temp = await c.Call("bench.evidence", new JsonObject { ["runId"] = slow.Str("runId"), ["evidenceId"] = "ev-" + Ids.Hash16(slow.Str("runId"), "gpu", "temp") });
        Assert.Equal(89, (double)temp["value"]!["value"]!);
    }

    // ---------------------------------------------------------------- 3. history comparability

    [Fact]
    public async Task VerdictHistory_NeedsTheSameSuccessfulPartSet()
    {
        var cpuRef = BenchScoring.References["cpu"];
        var (options, script, _, _) = BenchTests.Fakes();
        await using var host = TestHost.StartBench(options);
        var c = await host.ConnectAsync();
        for (var i = 0; i < 3; i++) await BenchTests.RunToEnd(c, BenchTests.StartParams(null, "cpu"));

        script.Results = part => part == "cpu" ? new Dictionary<string, double> { ["single"] = cpuRef["single"], ["multi"] = cpuRef["multi"] / 2 } : BenchScoring.References[part];
        var run = await BenchTests.RunToEnd(c, BenchTests.StartParams(null, "cpu", "disk")); // another part set: not comparable
        var cpu = ResultPart(run, "cpu");
        Assert.Equal("no_issue", cpu.Str("verdict"));
        Assert.DoesNotContain(cpu["observations"]!.AsArray(), o => o.Str("id") == "lower_than_before_unconfirmed");
        Assert.DoesNotContain(script.Commands, l => l.Contains("\"repeat\":\"", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- 4. live points

    [Fact]
    public async Task LiveScore_IsNormalisedToPoints()
    {
        var (options, script, _, _) = BenchTests.Fakes();
        script.LiveScore = (part, subtest) => BenchScoring.References[part][subtest] * 2; // raw ops/s, twice the reference
        script.Hang.Add("cpu");
        await using var host = TestHost.StartBench(options);
        var c = await host.ConnectAsync();
        var runId = (await c.Call("bench.start", BenchTests.StartParams(null, "cpu"))).Str("runId");
        JsonObject run;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        do
        {
            run = await c.Call("bench.get", Get(runId));
            await Task.Delay(20);
        } while (TestOf(run, "cpu")["liveScore"] is null && DateTime.UtcNow < deadline);
        Assert.Equal(2000, (double)TestOf(run, "cpu")["liveScore"]!);
        Assert.Equal(2000, (double)run["live"]!["score"]!);
        await c.Call("bench.cancel", new JsonObject { ["idempotencyKey"] = Guid.NewGuid().ToString(), ["runId"] = runId });
        await BenchTests.Finished(c, runId);
    }

    // ---------------------------------------------------------------- 5. capabilities cache

    [Fact]
    public async Task Capabilities_ShareOneCachedDescribe_RefreshedAfterARun()
    {
        var (options, _, _, hardware) = BenchTests.Fakes();
        hardware.Delay = TimeSpan.FromMilliseconds(300);
        await using var host = TestHost.StartBench(options);
        var c1 = await host.ConnectAsync();
        var c2 = await host.ConnectAsync();
        await Task.WhenAll(c1.Call("bench.capabilities"), c2.Call("bench.capabilities"), c1.Call("bench.capabilities"));
        Assert.Equal(1, hardware.Describes);
        await c1.Call("bench.capabilities");
        Assert.Equal(1, hardware.Describes);

        await BenchTests.RunToEnd(c1, BenchTests.StartParams(null, "cpu")); // bench.start reads the hardware itself
        var afterStart = hardware.Describes;
        await c1.Call("bench.capabilities");
        Assert.Equal(afterStart + 1, hardware.Describes); // refreshed after the run
    }

    // ---------------------------------------------------------------- 6. citations through agent.get

    [Fact]
    public async Task AgentGet_ResolvesReleasedBenchEvidence()
    {
        var provider = new FakeProvider("anthropic_api");
        var (options, _, _, _) = BenchTests.Fakes();
        await using var host = TestHost.StartBench(options, new AgentOptions
        {
            Providers = [provider], Credentials = new MemoryCredentialStore(), RetryDelay = TimeSpan.FromMilliseconds(20),
        }, [.. FakeAudit.Modules(new FakeWorld(), new CollectorOptions(), sampler: false)]);
        var c = await host.ConnectAsync();
        var reportId = await AgentSetup.Report(c);
        var run = await BenchTests.RunToEnd(c, BenchTests.StartParams(null, "cpu"));
        await AgentSetup.SetKey(c);
        var taskId = (await AgentSetup.StartTask(c, reportId)).Str("taskId");
        var evidenceId = "ev-" + Ids.Hash16(run.Str("runId"), "cpu", "single");

        Assert.DoesNotContain((await c.Call("agent.get", new JsonObject { ["taskId"] = taskId }))["evidence"]!.AsArray(), e => e.Str("id") == evidenceId);
        await host.Server.Engine.Agent.CallToolAsync(taskId, "get_benchmarks", [], "in_app", null, default);
        var task = await c.Call("agent.get", new JsonObject { ["taskId"] = taskId });
        var item = Assert.Single(task["evidence"]!.AsArray(), e => e.Str("id") == evidenceId)!;
        Assert.Equal("bench.cpu.single", item.Str("subject"));
        Assert.NotNull(item["display"]);
    }
}

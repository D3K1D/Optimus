using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Engine.Bench;

namespace OptimizationTool.Tests;

/// <summary>M6a AI (contracts/m6-benchmark.md §8): get_benchmarks, get_fan_guidance, ask context, Do: benchmark, voltage warning.</summary>
public class BenchAgentTests
{
    private static IReadOnlyList<IAuditModule> Modules() => [.. FakeAudit.Modules(new FakeWorld(), new CollectorOptions(), sampler: false)];

    private static AgentOptions Agent(FakeProvider provider) => new()
    {
        Providers = [provider], Credentials = new MemoryCredentialStore(), Project = null, // the real projection
        RetryDelay = TimeSpan.FromMilliseconds(20),
    };

    [Fact]
    public async Task SensorsSnapshot_IsOfferedForGrants_OnlyWhenBenchSensorsExist()
    {
        var provider = new FakeProvider("anthropic_api");
        var (options, _, _, _) = BenchTests.Fakes();
        await using (var host = TestHost.StartBench(options, Agent(provider), Modules()))
        {
            var c = await host.ConnectAsync(minor: 5);
            var ids = (await c.Call("provider.list"))["diagnostics"]!.AsArray().Select(d => d.Str("id")).ToList();
            Assert.Contains("sensors.snapshot", ids);
        }
        await using (var host = TestHost.StartBench(new OptimizationTool.Engine.Bench.BenchOptions(), Agent(provider), Modules()))
        {
            var c = await host.ConnectAsync(minor: 5);
            var ids = (await c.Call("provider.list"))["diagnostics"]!.AsArray().Select(d => d.Str("id")).ToList();
            Assert.DoesNotContain("sensors.snapshot", ids);
        }
    }

    [Fact]
    public async Task InvestigateWithAi_ReadsBenchmarks_CitesThem_SuggestsARun_AndWarnsOnVoltage()
    {
        string? cited = null;
        var provider = new FakeProvider("anthropic_api",
            FakeProvider.Step(FakeProvider.Call(("get_benchmarks", new JsonObject()), ("get_fan_guidance", new JsonObject { ["part"] = "gpu" }))),
            (req, _) =>
            {
                cited = FakeProvider.LastEvidenceIds(req).First();
                return Task.FromResult(FakeProvider.Say($"Your GPU scored as expected [{cited}]. Undervolting is sometimes discussed, but Optimus never applies it.\n" +
                    "Do: benchmark\nNext: Is my GPU running hot? | What is a good score?"));
            });
        var (options, _, _, _) = BenchTests.Fakes();
        await using var host = TestHost.StartBench(options, Agent(provider), Modules());
        var c = await host.ConnectAsync(minor: 5);
        var reportId = await AgentSetup.Report(c);
        var run = await BenchTests.RunToEnd(c);
        var runId = run.Str("runId");
        await AgentSetup.SetKey(c);
        var taskId = (await AgentSetup.StartTask(c, reportId)).Str("taskId");

        await c.Call("agent.ask", new JsonObject
        {
            ["idempotencyKey"] = Guid.NewGuid().ToString(), ["taskId"] = taskId, ["text"] = "Is this good?",
            ["context"] = new JsonObject { ["benchRunId"] = runId, ["part"] = "gpu" },
        });
        var message = (await AgentSetup.TaskEvent(c, taskId, "agent.message"))["payload"]!.AsObject();
        foreach (var e in (await c.Call("events.read", new JsonObject { ["stream"] = "task:" + taskId }))["events"]!.AsArray()) SchemaTests.AssertValid("event", e!.DeepClone());

        // The prompt names the part; the system prompt is v8 and lists the new tools.
        var first = provider.Requests.First();
        Assert.StartsWith("[prompt-v8]", first.System);
        Assert.Contains("get_benchmarks", first.System);
        Assert.Contains("Do: benchmark", first.System);
        var asked = first.Messages.OfType<UserText>().Last().Text;
        Assert.Contains("The user is looking at the benchmark result for the GPU.", asked);
        Assert.Contains(runId, asked);
        Assert.Contains(first.Tools, t => t.Name == "get_benchmarks");
        Assert.Contains(first.Tools, t => t.Name == "get_fan_guidance");

        // Tool results: projected runs with evidence, and host guidance (codes, no curves).
        var results = provider.Requests.Last().Messages.OfType<ToolResults>().Last().Results;
        var benchmarks = results[0].Content.AsObject();
        SchemaTests.AssertValid("tool-result", benchmarks);
        Assert.False(results[0].IsError);
        var runJson = Assert.Single(benchmarks["runs"]!.AsArray())!;
        Assert.Equal(runId, runJson.Str("runId"));
        Assert.Equal(10000, (double)runJson["score"]!);
        var gpu = runJson["parts"]!.AsArray().First(p => p.Str("part") == "gpu")!;
        Assert.Equal("no_issue", gpu.Str("verdict"));
        Assert.Contains(gpu["evidence"]!.AsArray(), e => e!["value"].Str("device") == FakeBenchHardware.Default.GpuModel);
        var guidance = results[1].Content.AsObject();
        Assert.Equal("gpu", guidance.Str("part"));
        Assert.Equal("any", guidance["guidance"].Str("brand"));

        // The answer: citation kept, tag stripped into suggest, voltage warning, follow-ups parsed.
        Assert.Contains(cited!, Engine.Json.Strings(message["citations"]));
        Assert.Equal("benchmark", message.Str("suggest"));
        Assert.Equal(new[] { "voltage" }, Engine.Json.Strings(message["warnings"]));
        Assert.DoesNotContain("Do: benchmark", message.Str("text"));
        Assert.Equal(2, message["followUps"]!.AsArray().Count);

        // The question carries its context for the history.
        var question = (await AgentSetup.TaskEvent(c, taskId, "agent.question"))["payload"]!;
        Assert.Equal(runId, question["context"].Str("benchRunId"));
    }

    [Fact]
    public async Task AskContext_IsValidated_AndContextRunIsCitableWithoutATool()
    {
        var provider = new FakeProvider("anthropic_api", (req, _) => Task.FromResult(FakeProvider.Say("Fine.")));
        var (options, _, _, _) = BenchTests.Fakes();
        await using var host = TestHost.StartBench(options, Agent(provider), Modules());
        var c = await host.ConnectAsync();
        var reportId = await AgentSetup.Report(c);
        var cpuOnly = await BenchTests.RunToEnd(c, BenchTests.StartParams(null, "cpu"));
        await AgentSetup.SetKey(c);
        var taskId = (await AgentSetup.StartTask(c, reportId)).Str("taskId");

        JsonObject Ask(JsonNode? context) => new()
        {
            ["idempotencyKey"] = Guid.NewGuid().ToString(), ["taskId"] = taskId, ["text"] = "Why?", ["context"] = context,
        };
        Assert.Equal(ErrorCodes.NotFound, (await c.Fails("agent.ask", Ask(new JsonObject { ["benchRunId"] = "bench-0000000000000000", ["part"] = "cpu" }))).Code);
        Assert.Equal(ErrorCodes.InvalidParams, (await c.Fails("agent.ask", Ask(new JsonObject { ["benchRunId"] = cpuOnly.Str("runId"), ["part"] = "gpu" }))).Code);
        Assert.Equal(ErrorCodes.InvalidParams, (await c.Fails("agent.ask", Ask(new JsonObject { ["benchRunId"] = cpuOnly.Str("runId"), ["part"] = "fan" }))).Code);
        Assert.Equal(ErrorCodes.InvalidParams, (await c.Fails("agent.ask", Ask(new JsonObject { ["benchRunId"] = cpuOnly.Str("runId"), ["part"] = "cpu", ["x"] = 1 }))).Code);
        Assert.Equal(ErrorCodes.InvalidParams, (await c.Fails("agent.ask", Ask(JsonValue.Create("cpu")))).Code);

        await c.Call("agent.ask", Ask(new JsonObject { ["benchRunId"] = cpuOnly.Str("runId"), ["part"] = "cpu" }));
        await AgentSetup.TaskEvent(c, taskId, "agent.message");
        var released = host.Server.Engine.Store.Read(db => db.Query("SELECT evidence_id FROM agent_released WHERE task_id = $t", r => r.GetString(0), ("$t", taskId)));
        Assert.Contains("ev-" + Ids.Hash16(cpuOnly.Str("runId"), "cpu", "single"), released);
    }

    [Fact]
    public async Task GetBenchmarks_ValidatesArguments_AndIsBudgetFree()
    {
        var (options, _, _, _) = BenchTests.Fakes();
        var provider = new FakeProvider("anthropic_api");
        await using var host = TestHost.StartBench(options, Agent(provider), Modules());
        var c = await host.ConnectAsync();
        var reportId = await AgentSetup.Report(c);
        await AgentSetup.SetKey(c);
        var taskId = (await AgentSetup.StartTask(c, reportId)).Str("taskId");
        var agent = host.Server.Engine.Agent;

        var none = await agent.CallToolAsync(taskId, "get_benchmarks", [], "in_app", null, default);
        Assert.Empty(none["runs"]!.AsArray());
        Assert.NotNull(none["note"]);

        var r1 = await BenchTests.RunToEnd(c, BenchTests.StartParams(null, "ram"));
        await BenchTests.RunToEnd(c, BenchTests.StartParams(null, "cpu"));
        var latest = await agent.CallToolAsync(taskId, "get_benchmarks", new JsonObject { ["limit"] = 5 }, "in_app", null, default);
        Assert.Equal(2, latest["runs"]!.AsArray().Count);
        var one = await agent.CallToolAsync(taskId, "get_benchmarks", new JsonObject { ["runId"] = r1.Str("runId") }, "in_app", null, default);
        Assert.Equal(r1.Str("runId"), Assert.Single(one["runs"]!.AsArray()).Str("runId"));

        foreach (var bad in new[]
                 {
                     new JsonObject { ["runId"] = r1.Str("runId"), ["limit"] = 1 }, new JsonObject { ["limit"] = 6 },
                     new JsonObject { ["runId"] = "scan-1" }, new JsonObject { ["other"] = 1 },
                 })
            Assert.Equal("invalid_arguments", (await agent.CallToolAsync(taskId, "get_benchmarks", bad, "in_app", null, default))["error"].Str("code"));
        Assert.Equal("invalid_arguments", (await agent.CallToolAsync(taskId, "get_benchmarks", new JsonObject { ["runId"] = "bench-0000000000000000" }, "in_app", null, default))["error"].Str("code"));
        Assert.Equal("invalid_arguments", (await agent.CallToolAsync(taskId, "get_fan_guidance", new JsonObject { ["part"] = "fan" }, "in_app", null, default))["error"].Str("code"));

        var task = await c.Call("agent.get", new JsonObject { ["taskId"] = taskId });
        Assert.Equal(0, (int)task["used"]!["diagnosticCalls"]!); // context tools spend no diagnostic budget
    }

    [Fact]
    public async Task SensorsSnapshot_RunsAsADiagnostic_AndIsRefusedBusyDuringABenchmark()
    {
        var (options, script, _, _) = BenchTests.Fakes(BenchTests.FastTimings() with { SampleInterval = TimeSpan.FromMilliseconds(30) });
        var provider = new FakeProvider("anthropic_api");
        await using var host = TestHost.StartBench(options, Agent(provider), Modules());
        var c = await host.ConnectAsync();
        var reportId = await AgentSetup.Report(c);
        await AgentSetup.SetKey(c);
        var taskId = (await AgentSetup.StartTask(c, reportId, grant: AgentSetup.Grant(["sensors.snapshot"]))).Str("taskId");
        var agent = host.Server.Engine.Agent;
        var args = new JsonObject { ["diagnosticId"] = "sensors.snapshot" };

        var ok = await agent.CallToolAsync(taskId, "run_diagnostic", args, "in_app", null, default);
        Assert.Null(ok["error"]);
        Assert.Equal("complete", ok.Str("status"));
        Assert.Contains(ok["evidence"]!.AsArray(), e => e.Str("subject") == "sensors.gpu.temp");

        script.Hang.Add("cpu");
        var runId = (await c.Call("bench.start", BenchTests.StartParams(null, "cpu"))).Str("runId");
        var busy = await agent.CallToolAsync(taskId, "run_diagnostic", (JsonObject)args.DeepClone(), "in_app", null, default);
        Assert.Equal("busy", busy["error"].Str("code"));
        await c.Call("bench.cancel", new JsonObject { ["idempotencyKey"] = Guid.NewGuid().ToString(), ["runId"] = runId });
        await BenchTests.Finished(c, runId);
    }

    [Fact]
    public void SystemPrompt_IsV8_WithTheVoltageRule()
    {
        Assert.Equal("prompt-v8", AgentService.SystemPromptVersion);
        Assert.Contains("never recommend them as a fix", AgentService.SystemPrompt);
        Assert.Contains("Optimus never applies them", AgentService.SystemPrompt);
        Assert.Contains("get_fan_guidance", AgentService.SystemPrompt);
    }
}

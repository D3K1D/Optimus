using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Actions;

namespace OptimizationTool.Tests;

/// <summary>
/// m9 P3b-2 on the host: a long administrator tool (shaped like repair.sfc) is a single provider action through the broker,
/// its progress lines reach the operation, operation.stop becomes the broker's stop, its own deadline travels with the step,
/// it is never a fix step, and without --enable-admin-actions it is unavailable. The broker is a fake.
/// </summary>
public class AdminRepairEngineTests
{
    private sealed class FakeRepair : IActionHandler
    {
        public string ActionId => "repair.sfc";
        public string Version => "1.0.0";
        public string Label => "Check and repair Windows files";
        public string Privilege => "admin";
        public bool SupportsUndo => false;
        public string Kind => "provider";
        public string Group => "repair";
        public string What => "Runs System File Checker.";
        public IReadOnlyList<string> Flags => [ActionFlags.Restart, ActionFlags.Long];
        public JsonObject? ParameterSchema => null;
        public bool TakesLease => true;
        public TimeSpan? LongDeadline => TimeSpan.FromMinutes(60);
        public bool ReportsOnly => true; // as the real repair handlers: a tool's report, never a verified change
        public Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct) =>
            Task.FromResult(new ActionResolution(true, new JsonObject { ["type"] = "repair", ["id"] = "sfc" }, new JsonObject(), new JsonObject { ["operation"] = "sfc" },
                [new PlanRow("Windows files", "Not checked now", "Checked")], [], [], null, Resources: ["repair:sfc"]));
        public Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct) => Task.FromResult(new JsonObject());
        public Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct) =>
            Task.FromResult(new ActionOutcome("failed", [], "Runs through the broker.", "admin_only"));
        public ActionRequest? UndoRequest(JsonObject target, JsonObject before) => null;
    }

    /// <summary>Reports progress, then waits until stopped (or released) like a running tool in the broker.</summary>
    private sealed class LongBroker : FakeElevation
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Result = "no_violations";

        public override async Task<ElevatedResult> RunAsync(ElevatedRequest request, CancellationToken stop, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request);
            request.Progress?.Report((1, "Checking Windows files · 40%"));
            try { await Release.Task.WaitAsync(stop); }
            catch (OperationCanceledException)
            {
                return new ElevatedResult([(1, new ActionOutcome("failed", new JsonObject { ["result"] = "stopped" }, "Stopped.", "stopped"))], "rcpt-00000000000000c1", true);
            }
            return new ElevatedResult([(1, new ActionOutcome("completed", new JsonObject { ["result"] = Result }, "The tool finished."))], "rcpt-00000000000000c2", true);
        }
    }

    private static Task<JsonObject> Preview(Client c, string? findingId = null)
    {
        var p = new JsonObject { ["idempotencyKey"] = Guid.NewGuid().ToString(), ["actionId"] = "repair.sfc" };
        if (findingId is not null) p["findingId"] = findingId;
        return c.Call("action.preview", p);
    }

    private static Task<JsonObject> Ended(Client c, string planId) =>
        c.WaitForEvent(e => e.Str("type") == "operation.state" && e["payload"].Str("planId") == planId
            && e["payload"].Str("state") is "verified" or "completed" or "failed" or "indeterminate");

    [Fact]
    public async Task A_repair_that_could_not_repair_leaves_its_finding_open_and_is_never_verified()
    {
        var broker = new LongBroker { Result = "could_not_repair" };
        broker.Release.SetResult();
        IAuditModule module = new FakeModule("files", [new FakeCollector("fake.files", "performance", _ => Task.FromResult(FakeCollector.One("files.integrity",
            new JsonObject { ["damaged"] = true }, "Windows files are damaged.")))],
            [new FakeRule("files.damaged", "performance", ["fake.files"], ctx =>
            {
                var e = ctx.Get("files.integrity")!;
                return RuleResult.Found(new FindingDraft(e.Subject, "Damaged Windows files", "medium", "high", [e.Id],
                    new CardDraft("Check and repair?", ["Preview change", "Ignore"], ActionId: "repair.sfc")));
            })]);
        await using var host = TestHost.StartActions([new FakeActionModule(new FakeRepair())], [module], elevation: broker, enableAdmin: true);
        var c = await host.ConnectAsync(minor: 3);
        var (scan, _) = await c.ScanAsync("audit.full");
        var reportId = scan.Str("reportId");
        var finding = (await c.Call("report.get", new JsonObject { ["reportId"] = reportId }))["findings"]!.AsArray().Single(f => f.Str("ruleId") == "files.damaged")!;

        var plan = await Preview(c, finding.Str("id"));
        Assert.Equal(finding.Str("id"), plan.Str("findingId"));
        var op = await Act.Approve(c, plan);
        var final = await Ended(c, plan.Str("planId"));
        Assert.Equal("completed", final["payload"].Str("state"));

        var done = await c.Call("operation.get", new JsonObject { ["operationId"] = op.Str("operationId") });
        Assert.Equal(("completed", "could_not_repair"), (done.Str("state"), done.Str("result")));
        Assert.Null(done["error"]);
        var receipt = done["receipt"]!.AsObject();
        Assert.False(receipt.ContainsKey("verifiedAt")); // what the tool reported, not a verified change
        Assert.NotEqual("", receipt.Str("completedAt"));
        Assert.Equal("The tool finished.", receipt.Str("text"));
        SchemaTests.AssertValid("operation", done);
        Assert.False(done["undo"]!["available"]!.GetValue<bool>());
        Assert.Equal("completed", (await c.Call("action.get", new JsonObject { ["planId"] = plan.Str("planId") })).Str("status"));
        // The finding's card is not closed by the tool's report: no verified decision (Check again stays the way to confirm).
        var followup = await c.Call("followup.get", new JsonObject { ["reportId"] = reportId });
        Assert.DoesNotContain(followup["decisions"]!.AsArray(), d => d.Str("findingId") == finding.Str("id") && d.Str("status") == "verified");
    }

    [Fact]
    public async Task Progress_reaches_the_operation_and_stop_ends_the_tool_through_the_broker()
    {
        var broker = new LongBroker();
        await using var host = TestHost.StartActions([new FakeActionModule(new FakeRepair())], elevation: broker, enableAdmin: true);
        var c = await host.ConnectAsync(minor: 3);
        var plan = await Preview(c);
        Assert.Equal("awaiting_user", plan.Str("status"));
        Assert.Equal("provider", plan.Str("kind"));
        var op = await Act.Approve(c, plan);
        await c.WaitForEvent(e => e.Str("type") == "operation.state" && e["payload"].Str("progress") == "Checking Windows files · 40%");
        var step = Assert.Single(Assert.Single(broker.Requests).Steps);
        Assert.Equal((true, TimeSpan.FromMinutes(60)), (step.Long, step.Deadline));

        await c.Call("operation.stop", new JsonObject { ["operationId"] = op.Str("operationId") });
        var endedEvent = await Ended(c, plan.Str("planId"));
        Assert.Equal("stopped", endedEvent["payload"].Str("result")); // the result name travels on the event too
        SchemaTests.AssertValid("event", endedEvent);
        var done = await c.Call("operation.get", new JsonObject { ["operationId"] = op.Str("operationId") });
        Assert.Equal(("failed", "cancelled", "stopped"), (done.Str("state"), done["error"].Str("code"), done.Str("result"))); // the fixture's shape: failed, cancelled
        Assert.Equal("cancelled", (await c.Call("action.get", new JsonObject { ["planId"] = plan.Str("planId") })).Str("status"));
    }

    /// <summary>m9 review R5: a Stop before the repair registers its stop signal ends it as a normal stop does; the broker is never asked.</summary>
    [Theory]
    [InlineData("queued")]
    [InlineData("registering")]
    public async Task An_early_stop_ends_the_repair_stopped_without_asking_the_broker(string stage)
    {
        var broker = new LongBroker();
        await using var host = TestHost.StartActions([new FakeActionModule(new FakeRepair())], elevation: broker, enableAdmin: true);
        var actions = host.Server.Engine.Actions;
        actions.RunSeam = (at, operationId) =>
        {
            if (at == stage) actions.OperationStop(new JsonObject { ["operationId"] = operationId });
            return Task.CompletedTask;
        };
        var c = await host.ConnectAsync(minor: 3);
        var plan = await Preview(c);
        var op = await Act.Approve(c, plan);
        var endedEvent = await Ended(c, plan.Str("planId"));
        Assert.Equal(("failed", "stopped"), (endedEvent["payload"].Str("state"), endedEvent["payload"].Str("result")));
        var done = await c.Call("operation.get", new JsonObject { ["operationId"] = op.Str("operationId") });
        SchemaTests.AssertValid("operation", done);
        Assert.Equal(("failed", "cancelled", "stopped"), (done.Str("state"), done["error"].Str("code"), done.Str("result"))); // as a normal stop
        Assert.Equal("cancelled", (await c.Call("action.get", new JsonObject { ["planId"] = plan.Str("planId") })).Str("status"));
        Assert.Empty(broker.Requests);                                   // no UAC prompt, no tool
        Assert.Equal(0, actions.PendingStopCount);
    }

    [Fact]
    public async Task Completed_is_the_tools_report_and_a_repair_is_never_a_fix_step_or_undone()
    {
        var broker = new LongBroker();
        broker.Release.SetResult();
        await using var host = TestHost.StartActions([new FakeActionModule(new FakeRepair())], elevation: broker, enableAdmin: true);
        var c = await host.ConnectAsync(minor: 3);
        var plan = await Preview(c);
        var op = await Act.Approve(c, plan);
        await Ended(c, plan.Str("planId"));
        var done = await c.Call("operation.get", new JsonObject { ["operationId"] = op.Str("operationId") });
        Assert.Equal("no_violations", done.Str("result"));
        Assert.False(done["undo"]!["available"]!.GetValue<bool>());

        var fix = await host.Server.Engine.Actions.CreateFixAsync("Repair", "Damaged files.", "Claude", [new FixStepRequest("repair.sfc", null, null, null)], null, RequestedBy.User);
        Assert.Equal("ineligible", fix.Str("status"));
        Assert.Equal("not_supported", fix["ineligible"]!["steps"]![0].Str("code"));
    }

    [Fact]
    public async Task Without_the_admin_flag_a_repair_is_unavailable_and_the_broker_is_never_asked()
    {
        var broker = new LongBroker();
        await using var host = TestHost.StartActions([new FakeActionModule(new FakeRepair())], elevation: broker, enableAdmin: false);
        var c = await host.ConnectAsync(minor: 3);
        var plan = await Preview(c);
        Assert.Equal("ineligible", plan.Str("status"));
        Assert.Equal(ActionService.AdminUnavailable, plan["ineligible"].Str("code"));
        Assert.Empty(broker.Requests);
    }
}

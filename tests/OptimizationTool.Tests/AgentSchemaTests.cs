using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Tests;

/// <summary>Live M3 host output validated against contracts/schemas (agent-task, provider-list, tool-result, review, events).</summary>
public class AgentSchemaTests
{
    [Fact]
    public async Task AgentOutput_MatchesSchemas()
    {
        var provider = new FakeProvider("anthropic_api",
            FakeProvider.Step(FakeProvider.Call(("list_capabilities", new JsonObject()), ("get_findings", new JsonObject()))),
            FakeProvider.Step(FakeProvider.Call(("run_diagnostic", new JsonObject { ["diagnosticId"] = "power.inventory" }),
                ("run_diagnostic", new JsonObject { ["diagnosticId"] = "startup.list" }))),
            (req, _) => Task.FromResult(FakeProvider.Say($"Done [{FakeProvider.LastEvidenceIds(req).First()}].")),
            (req, _) =>
            {
                var payload = JsonNode.Parse(((UserText)req.Messages[0]).Text)!;
                return Task.FromResult(FakeProvider.Say($"Point [{payload["findings"]![0]!["evidence"]![0].Str("id")}]."));
            });
        IReadOnlyList<IAuditModule> modules = [.. FakeAudit.Modules(new FakeWorld(), new CollectorOptions(), sampler: false), new DiagnosticModule()];
        await using var host = TestHost.StartAgent(AgentSetup.Options(null, provider), modules);
        var c = await host.ConnectAsync(minor: 2);
        var reportId = await AgentSetup.Report(c);
        await AgentSetup.SetKey(c);

        SchemaTests.AssertValid("provider-list", await c.Call("provider.list"));
        SchemaTests.AssertValidRef("provider-list.schema.json#/$defs/provider", await c.Call("provider.connect", new JsonObject { ["kind"] = "codex_mcp" }));
        SchemaTests.AssertValidRef("provider-list.schema.json#/$defs/provider", await c.Call("provider.connect", new JsonObject { ["kind"] = "anthropic_api" }));

        var task = await AgentSetup.StartTask(c, reportId);
        SchemaTests.AssertValid("agent-task", task);
        var taskId = task.Str("taskId");
        await AgentSetup.Ask(c, taskId);
        await AgentSetup.TaskEvent(c, taskId, "agent.message");
        SchemaTests.AssertValid("agent-task", await c.Call("agent.get", new JsonObject { ["taskId"] = taskId }));
        foreach (var t in (await c.Call("agent.active"))["tasks"]!.AsArray()) SchemaTests.AssertValid("agent-task", t);

        // Every tool result the model received (including the not_allowed error for startup.list).
        var results = provider.Requests.SelectMany(r => r.Messages.OfType<ToolResults>()).SelectMany(r => r.Results).Select(r => r.Content).ToList();
        Assert.True(results.Count >= 4);
        Assert.Contains(results, r => r["error"]?["code"]?.GetValue<string>() == "not_allowed");
        foreach (var r in results) SchemaTests.AssertValid("tool-result", r);
        var op = await host.Server.Engine.Agent.CallToolAsync(taskId, "run_diagnostic", new JsonObject { ["diagnosticId"] = "process.sample", ["window"] = 20 }, "in_app", null, default);
        SchemaTests.AssertValid("tool-result", op);
        JsonObject polled;
        do
        {
            await Task.Delay(20);
            polled = await host.Server.Engine.Agent.CallToolAsync(taskId, "get_operation", new JsonObject { ["operationId"] = op.Str("operationId") }, "in_app", null, default);
        } while (polled.Str("status") == "running");
        SchemaTests.AssertValid("tool-result", polled);
        SchemaTests.AssertValid("agent-task", await c.Call("agent.stop", new JsonObject { ["taskId"] = taskId }));

        var preview = await c.Call("review.preview", new JsonObject { ["recipientKind"] = "anthropic_api", ["reportId"] = reportId });
        SchemaTests.AssertValidRef("review.schema.json#/$defs/preview", preview);
        SchemaTests.AssertValidRef("review.schema.json#/$defs/submit", await c.Call("review.submit", new JsonObject { ["idempotencyKey"] = "k", ["previewId"] = preview.Str("previewId") }));
        var export = await c.Call("review.export", new JsonObject { ["reportId"] = reportId });
        SchemaTests.AssertValidRef("review.schema.json#/$defs/export", export);
        var exported = JsonNode.Parse(export.Str("json"))!;
        var finding = exported["findings"]![0]!;
        var answer = new JsonObject
        {
            ["v"] = 1, ["reviewId"] = export.Str("reviewId"),
            ["explanations"] = new JsonArray(new JsonObject
            {
                ["findingId"] = finding.Str("id"), ["evidenceIds"] = new JsonArray(finding["evidence"]![0].Str("id")), ["summary"] = "It is off.",
            }),
        };
        SchemaTests.AssertValidRef("review.schema.json#/$defs/import",
            await c.Call("review.import", new JsonObject { ["reviewId"] = export.Str("reviewId"), ["json"] = answer.ToJsonString() }));

        // Pairing events on the user stream.
        var agent = await host.ConnectAsync(kind: "agent", minor: 2);
        var pair = await agent.Call("agent.pair", new JsonObject { ["client"] = "codex" });
        await c.Call("agent.approvePairing", new JsonObject { ["pairingId"] = pair.Str("pairingId"), ["approve"] = true });
        await c.WaitForEvent(e => e.Str("type") == "agent.pairing" && e["payload"].Str("status") == "approved");

        foreach (var e in c.Events) SchemaTests.AssertValid("event", e);
        Assert.Contains(c.Events, e => e.Str("type") == "agent.tool");
        Assert.Contains(c.Events, e => e.Str("type") == "agent.turn");
        Assert.Contains(c.Events, e => e.Str("type") == "agent.task");
    }
}

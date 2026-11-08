using System.Text.Json.Nodes;
using Json.Schema;

namespace OptimizationTool.Tests;

/// <summary>Validates live host output against contracts/schemas/*.schema.json (copied next to the test assembly).</summary>
public class SchemaTests
{
    private static readonly Lazy<Dictionary<string, JsonSchema>> Schemas = new(() =>
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "schemas");
        var schemas = new Dictionary<string, JsonSchema>();
        foreach (var file in Directory.GetFiles(dir, "*.schema.json"))
        {
            var schema = JsonSchema.FromText(File.ReadAllText(file));
            schemas[Path.GetFileName(file).Replace(".schema.json", "", StringComparison.Ordinal)] = schema;
        }
        return schemas;
    });


    /// <summary>Validates against a sub-schema, e.g. "review.schema.json#/$defs/preview".</summary>
    internal static void AssertValidRef(string reference, JsonNode? instance)
    {
        var wrapper = JsonSchema.FromText($$"""{"$schema":"https://json-schema.org/draft/2020-12/schema","$id":"https://schemas.optimizationtool.invalid/v1/ref-{{Guid.NewGuid():N}}.json","$ref":"{{reference}}"}""");
        var options = new EvaluationOptions { OutputFormat = OutputFormat.List };
        foreach (var other in Schemas.Value.Values) options.SchemaRegistry.Register(other);
        var result = wrapper.Evaluate(instance, options);
        var errors = string.Join("\n", (result.Details ?? []).Where(d => d.Errors is not null)
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation} ({d.EvaluationPath}): {e.Value}")));
        Assert.True(result.IsValid, $"{reference} failed:\n{errors}\n{instance?.ToJsonString()}");
    }

    internal static void AssertValid(string name, JsonNode? instance)
    {
        var schema = Schemas.Value[name];
        var options = new EvaluationOptions { OutputFormat = OutputFormat.List };
        options.SchemaRegistry.Register(schema);
        foreach (var other in Schemas.Value.Values) options.SchemaRegistry.Register(other);
        var result = schema.Evaluate(instance, options);
        var errors = string.Join("\n", (result.Details ?? []).Where(d => d.Errors is not null)
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation} ({d.EvaluationPath}): {e.Value}")));
        Assert.True(result.IsValid, $"{name} schema failed:\n{errors}\n{instance?.ToJsonString()}");
    }

    [Fact]
    public void AllSchemaFiles_Load()
    {
        Assert.Equal(["action-plan", "agent-task", "compare", "event", "evidence", "finding", "followup", "hello", "onboarding", "operation", "provider-list", "report", "review", "scan", "tool-result"],
            Schemas.Value.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task LiveHostOutput_MatchesSchemas()
    {
        await using var host = TestHost.Start(speed: 0);
        var c = await host.ConnectAsync();
        AssertValid("hello", c.Hello);
        AssertValid("onboarding", await c.Call("onboarding.get"));
        AssertValid("onboarding", await Rpc.OnboardLocal(c, consent: "granted"));

        var inv = await c.Call("inventory.start", new JsonObject { ["idempotencyKey"] = "schema-inv" });
        await c.WaitForEvent(e => e.Str("type") == "inventory.finished");

        var started = await c.Call("scan.start", Rpc.ScanParams("audit.full", "Begin scan", "schema-scan"));
        AssertValid("scan", started);
        await c.WaitForScanFinished(started.Str("scanId"));
        var scan = await c.Call("scan.get", new JsonObject { ["scanId"] = started.Str("scanId") });
        AssertValid("scan", scan);

        var report = await c.Call("report.get", new JsonObject { ["reportId"] = scan.Str("reportId") });
        AssertValid("report", report);
        var evidenceId = (string)report["evidence"]![0]!["id"]!;
        AssertValid("evidence", await c.Call("evidence.get", new JsonObject { ["reportId"] = scan.Str("reportId"), ["evidenceId"] = evidenceId }));

        var followup = await c.Call("followup.get", new JsonObject { ["reportId"] = scan.Str("reportId") });
        AssertValid("followup", followup);
        followup = await c.Call("followup.respond", Rpc.Respond(scan.Str("reportId"), (long)followup["revision"]!, "card", followup["card"].Str("id"), text: "Who turned it off?"));
        AssertValid("followup", followup);
        for (var i = 0; i < 8; i++)
            followup = await c.Call("followup.respond", Rpc.Respond(scan.Str("reportId"), (long)followup["revision"]!, "card", followup["card"].Str("id"), option: "ignore"));
        AssertValid("followup", followup); // empty queue: card null

        // A cancelled scan and its partial report.
        await using var slow = TestHost.Start(speed: 1);
        var s = await slow.ConnectAsync();
        var running = await s.Call("scan.start", Rpc.ScanParams("audit.full", "Begin scan", "schema-cancel"));
        AssertValid("scan", running);
        var cancelled = await s.Call("scan.cancel", new JsonObject { ["scanId"] = running.Str("scanId") });
        AssertValid("scan", cancelled);
        AssertValid("report", await s.Call("report.get", new JsonObject { ["reportId"] = cancelled.Str("reportId") }));

        Assert.NotEmpty(c.Events);
        foreach (var e in c.Events.Concat(s.Events)) AssertValid("event", e);
        Assert.Contains(c.Events, e => e.Str("stream") == "inventory:" + inv.Str("inventoryId"));
    }

    [Fact]
    public void Schema_RejectsAWrongShape()
    {
        var schema = Schemas.Value["scan"];
        var options = new EvaluationOptions();
        foreach (var other in Schemas.Value.Values) options.SchemaRegistry.Register(other);
        var bad = JsonNode.Parse("""{"scanId":"scan-x","intent":"audit.full","status":"bogus"}""");
        Assert.False(schema.Evaluate(bad, options).IsValid);
    }
}

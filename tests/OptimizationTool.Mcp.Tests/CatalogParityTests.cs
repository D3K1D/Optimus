using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Mcp.Tests;

/// <summary>
/// contracts/m9-ai-powers.md §2 no-drift test (b): the MCP catalog equals the engine's model-facing tool specs under a
/// full grant: tool names, required arguments, enums, bounds, patterns and object shapes. Descriptions and defaults are
/// prose and may differ. The one intended difference: request_action.actionId is an enum of this host's actions in the
/// engine and a shape in the MCP catalog (the host checks the ID), so every engine ID must match the MCP pattern.
/// </summary>
public sealed class CatalogParityTests
{
    private static readonly string[] SomeActionIds = ["power.select_scheme", "defender.quick_scan", "setting.set", "firewall.enable_public", "manual.open"];

    private static readonly string[] ShapeKeys =
        ["type", "properties", "required", "enum", "const", "minimum", "maximum", "minItems", "maxItems", "maxLength", "pattern", "items", "additionalProperties"];

    /// <summary>The schema keys that constrain arguments, recursively; required sorted.</summary>
    private static JsonNode? Shape(JsonNode? node)
    {
        if (node is not JsonObject o) return node?.DeepClone();
        var shape = new JsonObject();
        foreach (var key in ShapeKeys.Where(k => o.ContainsKey(k)).Order(StringComparer.Ordinal))
            shape[key] = key switch
            {
                "properties" => new JsonObject(o["properties"]!.AsObject().OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Shape(p.Value)))),
                "items" => Shape(o["items"]),
                "required" => new JsonArray(o["required"]!.AsArray().Select(r => r!.GetValue<string>()).Order(StringComparer.Ordinal).Select(r => (JsonNode?)r).ToArray()),
                _ => o[key]!.DeepClone(),
            };
        return shape;
    }

    [Fact]
    public void McpCatalog_EqualsTheEngineSpecs_UnderAFullGrant()
    {
        var engine = Diagnostics.FullTools(SomeActionIds).ToDictionary(t => t.Name);
        var mcp = ToolCatalog.Tools.ToDictionary(t => t.Name);
        Assert.Equal(engine.Keys.Order(), mcp.Keys.Order());
        Assert.Equal(Diagnostics.ToolNames.Order(), mcp.Keys.Order());

        foreach (var (name, spec) in engine)
        {
            var engineSchema = (JsonObject)spec.InputSchema.DeepClone();
            var mcpSchema = JsonNode.Parse(mcp[name].InputSchema.GetRawText())!.AsObject();
            if (name == Diagnostics.RequestAction)
            {
                var ids = engineSchema["properties"]!["actionId"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
                Assert.Equal(SomeActionIds, ids);
                var pattern = mcpSchema["properties"]!["actionId"]!["pattern"]!.GetValue<string>();
                Assert.All(ids, id => Assert.Matches(pattern, id));
                Assert.True(ids.Max(i => i.Length) <= mcpSchema["properties"]!["actionId"]!["maxLength"]!.GetValue<int>());
                engineSchema["properties"]!["actionId"] = new JsonObject { ["type"] = "string", ["maxLength"] = Diagnostics.MaxActionId, ["pattern"] = Diagnostics.ActionIdPattern };
            }
            Assert.True(JsonNode.DeepEquals(Shape(engineSchema), Shape(mcpSchema)),
                $"{name}: engine {Shape(engineSchema)!.ToJsonString()} != mcp {Shape(mcpSchema)!.ToJsonString()}");
        }

        // The shared strings are the same strings.
        Assert.Equal(Diagnostics.ActionIdPattern, ToolCatalog.ActionIdPattern);
        Assert.Equal(Diagnostics.TopicPattern, ToolCatalog.TopicPattern);
        Assert.Equal(Diagnostics.All.Select(d => d.Id), ToolCatalog.Diagnostics);
        Assert.Equal(Diagnostics.Categories, ToolCatalog.Categories);
        Assert.Equal(Diagnostics.Severities, ToolCatalog.Severities);
        Assert.Contains(AgentService.GuidePointer, ToolCatalog.Instructions);
    }

    [Fact]
    public void GuideTopics_MatchTheTopicPattern()
    {
        string[] valid = ["overview", "features", "tools", "permissions", "changes", "web", "changes:security", "action:power.select_scheme", "action:setting.set"];
        Assert.All(valid, t => Assert.Matches(ToolCatalog.TopicPattern, t));
        Assert.All(new[] { "", "Overview", "changes:", "action:", "action:../x", "secrets" }, t => Assert.DoesNotMatch(ToolCatalog.TopicPattern, t));
        Assert.Matches(ToolCatalog.ActionIdPattern, "repair.dism_restorehealth");
        Assert.DoesNotMatch(ToolCatalog.ActionIdPattern, "power");
        Assert.DoesNotMatch(ToolCatalog.ActionIdPattern, "Power.Select");
        _ = Regex.IsMatch("x", ToolCatalog.TopicPattern); // a valid .NET regex
    }
}

using System.Text.Json.Nodes;

namespace OptimizationTool.Engine.Agent;

// M9 §2 (contracts/m9-ai-powers.md): the app guide for the AI. get_app_guide answers under the task's effective grant;
// guide.get (desktop) answers under the user's permissions record; every ask carries the generated permission line.
public sealed partial class AgentService
{
    private string? _guideVersion;

    /// <summary>The catalog as the guide sees it (every registered handler, then manual.open); empty without actions.</summary>
    internal IReadOnlyList<GuideAction> GuideActions() => Actions?.GuideActions() ?? [];

    /// <summary>g&lt;revision&gt;.&lt;hash&gt; over this host's stable definitions (the handlers never change while it runs).</summary>
    internal string GuideVersion => _guideVersion ??= AppGuide.Version(GuideActions());

    /// <summary>True when the task's AI can use tools: in-app kinds on an explain-only account cannot.</summary>
    private bool TaskHasTools(JsonObject task)
    {
        var kind = task["recipient"].Text("kind");
        return !(InAppKinds.Contains(kind) && ToolsDisabled(kind));
    }

    /// <summary>get_app_guide under the task's grant: the result body, or invalid_arguments naming the valid topics.</summary>
    private (JsonObject? Body, string ArrayKey, JsonObject? Error) GuideTool(JsonObject task, Grant grant, JsonObject args)
    {
        try
        {
            return (AppGuide.Get(args["topic"]?.GetValue<string>(), new GuideContext(grant, TaskHasTools(task), GuideActions(), GuideVersion, AdminAvailable)), "", null);
        }
        catch (ArgumentException ex)
        {
            return (null, "", ToolError("invalid_arguments", ex.Message));
        }
    }

    /// <summary>
    /// guide.get {topic?} (desktop) → {guideVersion, topic, text, topics}, under the user's permissions record and the
    /// selected provider (an explain-only account has no tools). Unknown topic → -32602 naming the valid ones.
    /// </summary>
    public JsonNode GuideGet(JsonObject p)
    {
        var topic = P.Str(p, "topic", required: false, maxLength: Diagnostics.MaxTopic);
        return _store.Read(db =>
        {
            var tools = Available(db)["tools"]!.GetValue<bool>();
            var grant = GrantFromPermissions(ReadPermissions(db), toolsOff: !tools);
            try { return (JsonNode)AppGuide.Get(topic, new GuideContext(grant, tools, GuideActions(), GuideVersion, AdminAvailable)); }
            catch (ArgumentException ex) { throw RpcError.Params("topic", ex.Message); }
        });
    }

    /// <summary>list_capabilities.actions: the changes this task may request now (empty when changes are off).</summary>
    private JsonArray CapabilityActions(Grant grant) => !grant.CanRequestChanges ? [] : new JsonArray(GuideActions()
        .Where(a => a.Requestable && a.Enabled)
        .Select(a => (JsonNode?)new JsonObject
        {
            ["actionId"] = a.ActionId, ["label"] = a.Label, ["admin"] = a.Privilege == "admin", ["undo"] = a.Undo, ["flags"] = Json.Array(a.Flags),
        }).ToArray());
}

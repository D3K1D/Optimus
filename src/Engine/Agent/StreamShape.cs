using System.Text.Json.Nodes;

namespace OptimizationTool.Engine.Agent;

/// <summary>
/// The structural test shared by the Claude Code stream guards (research, m9 §6.3, and chat, m6 §0): does a piece of an
/// event carry a tool call, a tool result or model message content? Claude Code adds status events over time
/// (rate_limit_event, system thinking_tokens, …); one that carries none of these, and is small, is informational and is
/// ignored. Anything else falls under the strict rules of its guard.
/// </summary>
public static class StreamShape
{
    /// <summary>Walk bounds: an event over any of them is not treated as informational.</summary>
    public const int MaxDepth = 16, MaxNodes = 5000, MaxEventChars = 64 * 1024;

    public enum Carries { Nothing, ToolOrContent, TooLarge }

    /// <summary>A tool call block type: tool_use, server_tool_use or any *_tool_use.</summary>
    public static bool IsToolCall(string? type) =>
        type is "tool_use" or "server_tool_use" || (type is not null && type.EndsWith("_tool_use", StringComparison.Ordinal));

    /// <summary>A tool call or tool result block type: the calls, tool_result or any *_tool_result.</summary>
    public static bool IsToolBlock(string? type) =>
        IsToolCall(type) || type is "tool_result" || (type is not null && type.EndsWith("_tool_result", StringComparison.Ordinal));

    /// <summary>
    /// A whole event that is not assistant, user or result: <see cref="Carries.Nothing"/> when it is at most
    /// <see cref="MaxEventChars"/> long and <see cref="Walk"/> finds nothing.
    /// </summary>
    public static Carries Event(JsonObject e, int lineLength) => lineLength > MaxEventChars ? Carries.TooLarge : Walk(e);

    /// <summary>
    /// Walks <paramref name="root"/> (at most <see cref="MaxDepth"/> deep and <see cref="MaxNodes"/> nodes): any object whose
    /// "type" is a tool block type (<see cref="IsToolBlock"/>), or any "message" object with content (a string or a list),
    /// is <see cref="Carries.ToolOrContent"/>. Other "type" values (e.g. "message", "direct") are plain data.
    /// </summary>
    public static Carries Walk(JsonNode? root)
    {
        var nodes = 0;
        return Visit(root, 0, ref nodes);
    }

    private static Carries Visit(JsonNode? n, int depth, ref int nodes)
    {
        if (++nodes > MaxNodes || depth > MaxDepth) return Carries.TooLarge;
        switch (n)
        {
            case JsonObject o:
                if (o["type"] is JsonValue tv && tv.TryGetValue<string>(out var t) && IsToolBlock(t)) return Carries.ToolOrContent;
                if (o["message"] is JsonObject m && m["content"] is JsonArray or JsonValue) return Carries.ToolOrContent;
                foreach (var (_, v) in o)
                    if (Visit(v, depth + 1, ref nodes) is var r and not Carries.Nothing) return r;
                return Carries.Nothing;
            case JsonArray a:
                foreach (var v in a)
                    if (Visit(v, depth + 1, ref nodes) is var r and not Carries.Nothing) return r;
                return Carries.Nothing;
            default:
                return Carries.Nothing;
        }
    }
}

using System.Text.Json.Nodes;

namespace OptimizationTool.Engine.Agent;

/// <summary>
/// The user's AI permissions (contracts/m9-ai-powers.md §1.1), one record per user in <c>settings</c> under
/// <see cref="Key"/>. They are the defaults every new task's grant is built from; a task's tools read that task's grant.
/// m9 §17.1: <c>do.admin</c> (administrator rights) is absent = false everywhere; it is never pre-selected.
/// </summary>
public sealed record Permissions(long Revision, bool Set, bool Specs, bool History, bool Names, bool Checks, string Changes, bool Scripts, bool Web, bool Admin = false)
{
    public const string Key = "ai.permissions";
    public const int Version = 1;
    public static readonly string[] ChangeLevels = ["off", "ask", "fix"];

    /// <summary>Before the user answers: specs, history and checks on; names off; no changes, scripts, web or administrator rights.</summary>
    public static Permissions Default { get; } = new(0, false, true, true, false, true, "off", false, false);

    /// <summary>off &lt; ask &lt; fix.</summary>
    public static int Level(string changes) => Array.IndexOf(ChangeLevels, changes);

    public JsonObject ToJson() => new()
    {
        ["version"] = Version, ["revision"] = Revision, ["set"] = Set,
        ["see"] = new JsonObject { ["specs"] = Specs, ["history"] = History, ["names"] = Names },
        ["do"] = new JsonObject { ["checks"] = Checks, ["changes"] = Changes, ["scripts"] = Scripts, ["web"] = Web, ["admin"] = Admin },
    };

    /// <summary>The stored record; anything unreadable falls back to the defaults (with the stored revision when it has one).</summary>
    public static Permissions FromStored(JsonNode? node)
    {
        if (node is not JsonObject o) return Default;
        var revision = o["revision"] is JsonValue rv && rv.TryGetValue<long>(out var r) && r >= 0 ? r : 0;
        try { return Parse(o, revision, o["set"] is JsonValue sv && sv.TryGetValue<bool>(out var s) && s, stored: true); }
        catch (RpcError) { return Default with { Revision = revision }; }
    }

    /// <summary>
    /// Validates a client's record: <c>see</c> and <c>do</c> with every field, booleans and the changes enum only; unknown
    /// fields are refused (<c>do.admin</c> is required here too; only a stored record may lack it). <c>version</c> (1), <c>revision</c> and <c>set</c> may be echoed back and are ignored (the host
    /// owns them). <c>scripts:true</c> requires <c>changes:"fix"</c>.
    /// </summary>
    public static Permissions Parse(JsonNode? node, long revision, bool set, bool stored = false)
    {
        if (node is not JsonObject o) throw RpcError.Params("permissions", "permissions must be an object.");
        Only(o, "permissions", "version", "revision", "set", "see", "do");
        if (o["version"] is { } v && !(v is JsonValue vv && vv.TryGetValue<int>(out var n) && n == Version))
            throw RpcError.Params("permissions.version", $"version must be {Version}.");
        if (o["see"] is not JsonObject see) throw RpcError.Params("permissions.see", "see must be an object.");
        if (o["do"] is not JsonObject @do) throw RpcError.Params("permissions.do", "do must be an object.");
        Only(see, "permissions.see", "specs", "history", "names");
        Only(@do, "permissions.do", "checks", "changes", "scripts", "web", "admin");
        var changes = @do["changes"] is JsonValue cv && cv.TryGetValue<string>(out var c) && ChangeLevels.Contains(c) ? c
            : throw RpcError.Params("permissions.do.changes", "changes must be off, ask or fix.");
        var scripts = Bool(@do, "permissions.do", "scripts");
        if (scripts && changes != "fix") throw RpcError.Params("permissions.do.scripts", "scripts needs changes set to fix.");
        return new Permissions(revision, set, Bool(see, "permissions.see", "specs"), Bool(see, "permissions.see", "history"), Bool(see, "permissions.see", "names"),
            Bool(@do, "permissions.do", "checks"), changes, scripts, Bool(@do, "permissions.do", "web"),
            // m9 §17.1 review: required on a save (missing or null → -32602); absent = false only in a stored record or grant.
            stored && @do["admin"] is null ? false : Bool(@do, "permissions.do", "admin"));
    }

    private static void Only(JsonObject o, string path, params string[] allowed)
    {
        var extra = o.Select(kv => kv.Key).FirstOrDefault(k => !allowed.Contains(k));
        if (extra is not null) throw RpcError.Params($"{path}.{extra}", $"Unknown field {extra}.");
    }

    private static bool Bool(JsonObject o, string path, string key) =>
        o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : throw RpcError.Params($"{path}.{key}", $"{key} must be true or false.");

    /// <summary>True when <paramref name="next"/> takes anything away (a see/do field goes from more to less, fix → ask → off included).</summary>
    public bool NarrowedBy(Permissions next) =>
        (Specs && !next.Specs) || (History && !next.History) || (Names && !next.Names) || (Checks && !next.Checks)
        || Level(next.Changes) < Level(Changes) || (Scripts && !next.Scripts) || (Web && !next.Web) || (Admin && !next.Admin);

    /// <summary>True when <paramref name="next"/> adds anything.</summary>
    public bool WidenedBy(Permissions next) => next.NarrowedBy(this);
}

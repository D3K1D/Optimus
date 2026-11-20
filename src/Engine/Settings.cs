using System.Text.Json.Nodes;

namespace OptimizationTool.Engine;

/// <summary>Per-user settings rows (migration v8, contracts/m9-ai-powers.md §1.1): one JSON value per key.</summary>
internal static class Settings
{
    /// <summary>The stored value, or null when the key has none or it does not parse.</summary>
    public static JsonNode? Get(Db db, string key)
    {
        if (db.Scalar("SELECT value FROM settings WHERE key = $k", ("$k", key)) is not string value) return null;
        try { return Json.Parse(value); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public static void Set(Db db, string key, JsonNode value) =>
        db.Exec("INSERT INTO settings (key, value, updated_at) VALUES ($k, $v, $t) ON CONFLICT (key) DO UPDATE SET value = $v, updated_at = $t",
            ("$k", key), ("$v", Json.Write(value)), ("$t", db.Now()));
}

using System.Globalization;
using System.Text.Json.Nodes;

namespace OptimizationTool.Engine;

/// <summary>
/// Cross-scan review dispositions (protocol §9 finding.setDisposition). Keyed by fingerprint (scope finding) or
/// rule ID (scope rule), so they carry into later reports. An expired one no longer applies; an ignored one stops
/// applying when the finding's severity rises above the severity it had when it was ignored.
/// </summary>
internal static class Dispositions
{
    /// <summary>intended (v1.4): "I set this up on purpose" — finding scope, no expiry, reopens on a severity rise like ignored.</summary>
    public static readonly string[] Statuses = ["ignored", "snoozed", "intended"];
    public static readonly string[] Scopes = ["finding", "rule"];
    public static readonly TimeSpan MaxSnooze = TimeSpan.FromDays(366);

    /// <summary>The disposition in force for a finding, or null. Finding scope wins over rule scope.</summary>
    public static JsonObject? Active(Db db, string fingerprint, string ruleId, string severity)
    {
        var now = db.Now();
        var rows = db.Query("SELECT scope, status, expires_at, severity FROM dispositions WHERE (scope = 'finding' AND key = $f) OR (scope = 'rule' AND key = $r)",
            r => (Scope: r.GetString(0), Status: r.GetString(1), Expires: r.IsDBNull(2) ? null : r.GetString(2), Severity: r.GetString(3)),
            ("$f", fingerprint), ("$r", ruleId));
        foreach (var row in rows.OrderBy(r => r.Scope == "finding" ? 0 : 1))
        {
            if (row.Expires is not null && string.CompareOrdinal(row.Expires, now) <= 0) continue;
            if (row.Status is "ignored" or "intended" && ReportBuilder.SeverityRank(severity) > ReportBuilder.SeverityRank(row.Severity)) continue;
            return new JsonObject { ["status"] = row.Status, ["scope"] = row.Scope, ["expiresAt"] = row.Expires };
        }
        return null;
    }

    public static void Set(Db db, JsonObject finding, string status, string scope, string? expiresAt)
    {
        var key = scope == "finding" ? finding.Text("fingerprint") : finding.Text("ruleId");
        db.Exec("""
            INSERT INTO dispositions (scope, key, rule_id, status, expires_at, severity, created_at) VALUES ($sc, $k, $r, $st, $e, $sv, $t)
            ON CONFLICT (scope, key) DO UPDATE SET status = $st, expires_at = $e, severity = $sv, created_at = $t
            """,
            ("$sc", scope), ("$k", key), ("$r", finding.Text("ruleId")), ("$st", status), ("$e", expiresAt),
            ("$sv", finding.Text("severity")), ("$t", db.Now()));
    }

    public static void Clear(Db db, JsonObject finding) =>
        db.Exec("DELETE FROM dispositions WHERE (scope = 'finding' AND key = $f) OR (scope = 'rule' AND key = $r)",
            ("$f", finding.Text("fingerprint")), ("$r", finding.Text("ruleId")));

    /// <summary>Validates expiresAt: required for snoozed, in the future, at most a year ahead; normalized to the wire format.</summary>
    public static string? ParseExpiry(string? text, string status, string now)
    {
        if (text is null)
            return status == "snoozed" ? throw RpcError.Params("expiresAt", "snoozed needs expiresAt.") : null;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
            throw RpcError.Params("expiresAt", "expiresAt must be an ISO-8601 time.");
        var current = DateTimeOffset.Parse(now, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        if (at <= current) throw RpcError.Params("expiresAt", "expiresAt must be in the future.");
        if (at - current > MaxSnooze) throw RpcError.Params("expiresAt", "expiresAt must be within a year.");
        return Time.Format(at);
    }
}

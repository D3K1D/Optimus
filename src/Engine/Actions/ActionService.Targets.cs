using System.Text.Json.Nodes;

namespace OptimizationTool.Engine.Actions;

/// <summary>
/// A <c>targetRef {evidenceId}</c> as the engine resolved it (contracts/m9-ai-powers.md §3.1). Evidence is null when the
/// ID is unknown, not released to the task, outside the named report or finding, or ambiguous: the plan is then
/// ineligible <c>unknown_target</c> and no handler sees the request.
/// </summary>
internal sealed record TargetRef(string EvidenceId, TargetEvidence? Evidence);

// Evidence-backed target refs (m9 §3.1). The caller names evidence by ID; the engine finds the stored, unprojected item
// and hands its local fields to the handler. Nothing here trusts a name, alias, path, key or PID from a caller.
public sealed partial class ActionService
{
    /// <summary>The ineligible code for a target that cannot be matched to evidence (m9 §3.1).</summary>
    public const string UnknownTarget = "unknown_target";

    /// <summary>
    /// The evidenceId of a <c>targetRef</c> argument: an object with exactly one string property, evidenceId (≤ 64
    /// characters, no control characters). Null when absent. Any other shape is invalid parameters: a targetRef is never
    /// a name, alias, path or key.
    /// </summary>
    internal static string? TargetRefId(JsonNode? node, string field)
    {
        if (node is null) return null;
        if (node is not JsonObject o || o.Count != 1 || o["evidenceId"] is not JsonValue v || !v.TryGetValue<string>(out var id)
            || id.Length is 0 or > 64 || id.Any(char.IsControl))
            throw RpcError.Params(field, "targetRef must be {\"evidenceId\": \"ev-…\"}.");
        return id;
    }

    /// <summary>A stored evidence body as a handler's target evidence (null when it lacks an object value or a collector).</summary>
    internal static TargetEvidence? EvidenceOf(JsonObject body)
    {
        if (body["value"] is not JsonObject value || body["collectorId"] is not JsonValue c || !c.TryGetValue<string>(out var collector)) return null;
        var subject = body["subject"] is JsonValue s && s.TryGetValue<string>(out var sub) ? sub : null;
        var id = body["id"] is JsonValue i && i.TryGetValue<string>(out var eid) ? eid : null;
        if (subject is null || id is null) return null;
        return new TargetEvidence(id, collector, subject, (JsonObject)value.DeepClone(), body["observedAt"] is JsonValue t && t.TryGetValue<string>(out var at) ? at : null);
    }

    /// <summary>The report's evidence bodies with this ID (more than one is ambiguous).</summary>
    private static List<JsonObject> ReportEvidence(JsonObject report, string evidenceId) =>
        (report["evidence"] as JsonArray ?? []).OfType<JsonObject>().Where(e => e["id"] is JsonValue v && v.TryGetValue<string>(out var id) && id == evidenceId).ToList();

    /// <summary>True when the finding cites the evidence (as evidence or counterevidence).</summary>
    internal static bool Cites(JsonObject finding, string evidenceId) =>
        Json.Strings(finding["evidenceIds"]).Concat(Json.Strings(finding["counterevidence"])).Contains(evidenceId);

    /// <summary>
    /// action.preview's targetRef: with a findingId, the evidence must be cited by that finding and stored once in its
    /// report; with only a reportId, stored once in that report. Neither named → null (the desktop must say where it
    /// found the target).
    /// </summary>
    private static TargetEvidence? DesktopEvidence(Db db, string evidenceId, string? findingId, string? reportId)
    {
        JsonObject report;
        if (findingId is not null)
        {
            if (FindFinding(db, findingId) is not { } found || !Cites(found.Finding, evidenceId)) return null;
            if (reportId is not null && reportId != found.ReportId) return null;
            report = found.Report;
        }
        else if (reportId is not null)
        {
            try { report = ReportService.Load(db, reportId); }
            catch (RpcError) { return null; }
        }
        else return null;
        var matches = ReportEvidence(report, evidenceId);
        return matches.Count == 1 ? EvidenceOf(matches[0]) : null;
    }

    /// <summary>
    /// request_action's targetRef for an agent task: the ID must name exactly one stored item (the task's own diagnostic
    /// evidence or its report's evidence), and that item must have been released to the task, or be cited by the
    /// in-scope finding the call names (<paramref name="finding"/>, already checked against the task's scope).
    /// </summary>
    internal static TargetEvidence? AgentEvidence(Db db, string taskId, JsonObject? report, JsonObject? finding, string evidenceId)
    {
        var bodies = db.Query("SELECT body FROM agent_evidence WHERE task_id = $t AND evidence_id = $e",
            r => Json.Parse(r.GetString(0)) as JsonObject, ("$t", taskId), ("$e", evidenceId)).OfType<JsonObject>().ToList();
        if (report is not null) bodies.AddRange(ReportEvidence(report, evidenceId));
        if (bodies.Count != 1) return null; // unknown or ambiguous
        var released = db.Scalar("SELECT 1 FROM agent_released WHERE task_id = $t AND evidence_id = $e", ("$t", taskId), ("$e", evidenceId)) is not null;
        if (!released && (finding is null || !Cites(finding, evidenceId))) return null;
        return EvidenceOf(bodies[0]);
    }
}

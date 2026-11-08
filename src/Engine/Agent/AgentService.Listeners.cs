using System.Text.Json.Nodes;

namespace OptimizationTool.Engine.Agent;

// run_diagnostic network.listeners target "exposed" (the default): instead of every endpoint row, a summary plus one
// item per (owner, protocol, bind scope) for what other devices can reach — TCP not bound to loopback, and UDP not
// bound to loopback that is non-system or on a port the exposure table tags. Built here from the collector's
// committed rows and stored as task evidence of its own (citing sourceEvidenceIds), so the M2 rules and the report
// are unchanged. Items go through the win.security.listeners projector (net.exposed.*: it aliases the app, tags
// exposure, caps ports and regenerates Display). The summary holds only counts and booleans and is released as built.
public sealed partial class AgentService
{
    public const string ExposedSummarySubject = "net.listeners.exposed.summary";
    public const int MaxPortsPerItem = 64; // the projector's cap

    private sealed record ListenerRow(string EvidenceId, string Protocol, string Scope, int Port, bool System, string? Image);

    /// <summary>Exposure tags that make an item worth a look first (media discovery is tagged but low concern).</summary>
    private static bool Risky(string protocol, int port) => Exposure.Tags(protocol, port).Any(t => t != "media_discovery");

    private List<JsonObject> DeriveExposedListeners(Db db, JsonObject task, string invocationId, List<JsonObject> raw)
    {
        var taskId = task.Text("taskId");
        var observedAt = raw.FirstOrDefault()?.Text("observedAt") ?? db.Now();
        var rows = raw.Where(b => b.Text("subject").StartsWith("net.listener.", StringComparison.Ordinal))
            .Select(ToRow).OfType<ListenerRow>().ToList();

        var exposed = rows.Where(r => r.Scope != "loopback" && (r.Protocol == "tcp" || !r.System || Risky(r.Protocol, r.Port)))
            .GroupBy(r => (Owner: r.Image ?? (r.System ? "system" : "unknown"), r.Protocol, r.Scope))
            .Select(g =>
            {
                var ports = g.Select(r => r.Port).Distinct().Order().ToList();
                return (g.Key.Owner, g.Key.Protocol, g.Key.Scope, Image: g.First().Image, System: g.Any(r => r.System), Ports: ports,
                    Risky: ports.Any(p => Risky(g.Key.Protocol, p)), Sources: g.Select(r => r.EvidenceId).ToList());
            })
            .OrderBy(x => x.Risky ? 0 : 1).ThenBy(x => x.Ports[0]).ThenBy(x => x.Protocol, StringComparer.Ordinal).ThenBy(x => x.Owner, StringComparer.Ordinal)
            .ToList();

        var summary = new JsonObject
        {
            ["totalListeners"] = rows.Count,
            ["tcp"] = rows.Count(r => r.Protocol == "tcp"),
            ["udp"] = rows.Count(r => r.Protocol == "udp"),
            ["loopbackOnly"] = rows.Count(r => r.Scope == "loopback"),
            ["reachableFromOtherDevices"] = rows.Count(r => r.Scope != "loopback"),
            ["exposedItems"] = exposed.Count,
            ["exposedWithRiskyPorts"] = exposed.Count(x => x.Risky),
        };
        if (PublicFirewall(db, task) is { } fw)
        {
            summary["publicProfileActive"] = fw.Active;
            if (fw.Enabled is bool on) summary["publicFirewallOn"] = on;
        }
        var sentence = $"{rows.Count} listeners: {summary["reachableFromOtherDevices"]} reachable from other devices, {summary["loopbackOnly"]} on this PC only.";

        var bodies = new List<JsonObject> { Summary(db, taskId, invocationId, summary, sentence, raw.Select(b => b.Text("id")), observedAt) };
        foreach (var x in exposed)
        {
            var value = new JsonObject
            {
                ["protocol"] = x.Protocol, ["scope"] = x.Scope,
                ["ports"] = new JsonArray(x.Ports.Take(MaxPortsPerItem).Select(p => (JsonNode?)p).ToArray()),
                ["isSystem"] = x.System, ["app"] = x.Image, // raw (normalized) image; the projector aliases it
            };
            var subject = $"net.exposed.{x.Protocol}.{x.Scope}.{Ids.Hash16(x.Owner)[..8]}";
            bodies.Add(Store(db, taskId, invocationId, subject, value, $"{x.Owner} accepts {x.Protocol} connections.", x.Sources, observedAt,
                ScanCoordinator.InferSensitivity(value), "inferred", projected: null));
        }
        return bodies;
    }

    private static ListenerRow? ToRow(JsonObject body)
    {
        if (body["value"] is not JsonObject v) return null;
        var protocol = v["protocol"]?.GetValue<string>();
        var scope = v["scope"]?.GetValue<string>();
        if (protocol is not ("tcp" or "udp") || scope is null || v["port"] is not JsonValue pv || Json.Number(pv) is not double port) return null;
        var system = v["isSystem"] is JsonValue sv && sv.TryGetValue<bool>(out var s) && s;
        var image = v["image"] is JsonValue iv && iv.TryGetValue<string>(out var i) ? i : null;
        return new ListenerRow(body.Text("id"), protocol, scope, (int)port, system, image);
    }

    /// <summary>The Public firewall profile from this task's own evidence, else the task's report; null if neither has it.</summary>
    private static (bool Active, bool? Enabled)? PublicFirewall(Db db, JsonObject task)
    {
        var body = db.Query("SELECT body FROM agent_evidence WHERE task_id = $t AND subject = 'firewall.profile.public' ORDER BY rowid DESC LIMIT 1",
            r => Json.Parse(r.GetString(0)) as JsonObject, ("$t", task.Text("taskId"))).FirstOrDefault();
        if (body is null)
            try
            {
                body = ReportService.Load(db, task["scope"].Text("reportId"))["evidence"]!.AsArray()
                    .FirstOrDefault(e => e.Text("subject") == "firewall.profile.public") as JsonObject;
            }
            catch (RpcError) { }
        if (body?["value"] is not JsonObject v || v["active"] is not JsonValue av || !av.TryGetValue<bool>(out var active)) return null;
        return (active, v["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var enabled) ? enabled : null);
    }

    /// <summary>The summary: counts and booleans only, released as built (no collector projector knows this shape).</summary>
    private static JsonObject Summary(Db db, string taskId, string invocationId, JsonObject value, string display, IEnumerable<string> sources, string observedAt)
    {
        var id = "ev-" + Ids.Hash16(taskId, invocationId, "exposed", ExposedSummarySubject);
        var projected = new JsonObject
        {
            ["id"] = id, ["subject"] = ExposedSummarySubject, ["source"] = "Windows network tables", ["observedAt"] = observedAt,
            ["quality"] = "measured", ["value"] = value.DeepClone(), ["display"] = display,
        };
        return Store(db, taskId, invocationId, ExposedSummarySubject, value, display, sources, observedAt, "public", "declared", projected);
    }

    private static JsonObject Store(Db db, string taskId, string invocationId, string subject, JsonObject value, string display, IEnumerable<string> sources,
        string observedAt, string sensitivity, string basis, JsonObject? projected)
    {
        var id = "ev-" + Ids.Hash16(taskId, invocationId, "exposed", subject);
        var body = new JsonObject
        {
            ["id"] = id, ["subject"] = subject, ["category"] = "security", ["source"] = "Windows network tables", ["observedAt"] = observedAt,
            ["interval"] = null, ["display"] = display, ["value"] = value.DeepClone(), ["collectorId"] = "win.security.listeners",
            ["collectorVersion"] = "gateway", ["quality"] = "measured", ["sensitivity"] = sensitivity, ["sensitivityBasis"] = basis,
            ["sourceEvidenceIds"] = Json.Array(sources.Where(s => s.Length > 0).Distinct()),
        };
        if (projected is not null) body["gatewayProjected"] = projected;
        db.Exec("INSERT OR IGNORE INTO agent_evidence (evidence_id, task_id, collector_id, subject, body) VALUES ($i, $t, $c, $s, $b)",
            ("$i", id), ("$t", taskId), ("$c", "win.security.listeners"), ("$s", subject), ("$b", Json.Write(body)));
        return body;
    }
}

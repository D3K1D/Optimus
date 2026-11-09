using System.Text.Json.Nodes;

namespace OptimizationTool.Engine;

/// <summary>
/// Builds the immutable report revision for a finished scan from its committed evidence and coverage, evaluates
/// every rule whose inputs were planned, and creates the follow-up queue. The report JSON is stored once, so
/// reopening returns identical bytes; dispositions (which change later) are overlaid when the report is read.
/// Arrays are ordered by plan order (evidence, coverage, gaps) and rule catalog order (findings, rules).
/// </summary>
internal sealed class ReportBuilder(Fixture fixture, AuditCatalog catalog)
{
    public static readonly string[] Severities = ["info", "low", "medium", "high", "critical"];
    public static int SeverityRank(string s) => Array.IndexOf(Severities, s);

    public string Build(Db db, string scanId, string status, string generatedAt)
    {
        var scan = db.Query("SELECT intent, text, base_report_id FROM scans WHERE scan_id = $s",
            r => (Intent: r.GetString(0), Text: r.GetString(1), Base: r.IsDBNull(2) ? null : r.GetString(2)), ("$s", scanId)).First();
        var reportId = Ids.Report(scanId);
        var (_, profile) = OnboardingService.Read(db);

        var evidence = db.Query("SELECT body FROM evidence WHERE scan_id = $s ORDER BY ord, evidence_id",
            r => (JsonObject)Json.Parse(r.GetString(0))!, ("$s", scanId));
        var coverage = ScanCoordinator.Coverage(db, scanId, finishedOnly: false);
        var coverageRows = coverage.Select(c => c!.AsObject()).ToList();

        // Rules see a skipped collector that kept samples as partial: its evidence is usable but incomplete.
        var ruleCoverage = coverageRows.ToDictionary(c => c.Text("collectorId"),
            c => c.Text("status") == "skipped" && (int)c["observations"]! > 0 ? "partial" : c.Text("status"));
        var items = evidence.Select(e => new EvidenceItem(e.Text("id"), e["subject"]?.GetValue<string>() ?? "", e.Text("collectorId"),
            e.Text("category"), e["value"]!.DeepClone(), e.Text("display"), e.Text("observedAt"))).ToList();
        var context = new RuleContext(items, ruleCoverage, profile);
        var evidenceIds = items.Select(i => i.Id).ToHashSet();

        var findings = new JsonArray();
        var cards = new JsonArray();
        var outcomes = new JsonArray();
        var seen = new HashSet<string>();
        foreach (var rule in catalog.Rules)
        {
            if (rule.Inputs.Any(i => !ruleCoverage.ContainsKey(i))) continue; // not part of this scan's plan

            RuleResult result;
            try { result = rule.Evaluate(context); }
            catch (Exception ex) { result = RuleResult.Insufficient($"Rule error ({ex.GetType().Name})."); }

            var produced = 0;
            foreach (var draft in result.Status == "finding" ? result.Findings : [])
            {
                var cited = draft.EvidenceIds.Where(evidenceIds.Contains).Distinct().ToList();
                if (cited.Count == 0 || SeverityRank(draft.Severity) < 0) continue; // never a finding without evidence from this scan
                var fingerprint = Ids.Fingerprint(rule.Id, draft.Subject);
                var findingId = Ids.Finding(scanId, rule.Id, fingerprint);
                if (!seen.Add(findingId)) continue;
                var finding = new JsonObject
                {
                    ["id"] = findingId, ["ruleId"] = rule.Id, ["ruleVersion"] = rule.Version, ["fingerprint"] = fingerprint,
                    ["subject"] = draft.Subject, ["category"] = rule.Category, ["title"] = draft.Title,
                    ["severity"] = draft.Severity, ["confidence"] = draft.Confidence, ["evidenceIds"] = Json.Array(cited),
                    ["counterevidence"] = Json.Array((draft.Counterevidence ?? []).Where(evidenceIds.Contains).Distinct()),
                    ["actionEligibility"] = draft.ActionEligibility, ["settingsUri"] = draft.Card.SettingsUri, ["disposition"] = null,
                };
                findings.Add(finding);
                cards.Add(rule is FixtureRule fr ? Followups.CardFromFixture(fr.Def, reportId, finding) : Followups.CardFromDraft(reportId, finding, draft));
                produced++;
            }
            var outcome = result.Status == "finding" && produced == 0 ? "insufficient" : result.Status;
            outcomes.Add(new JsonObject
            {
                ["id"] = rule.Id, ["version"] = rule.Version, ["category"] = rule.Category, ["inputs"] = Json.Array(rule.Inputs),
                ["status"] = outcome, ["reason"] = outcome == result.Status ? result.Reason : "No cited evidence.", ["findings"] = produced,
            });
        }

        var gaps = new JsonArray();
        foreach (var cov in coverageRows)
        {
            var covStatus = cov.Text("status");
            if (covStatus == "complete") continue;
            var collectorId = cov.Text("collectorId");
            var collector = catalog.Find(collectorId);
            var title = collector?.GapTitle ?? collectorId;
            // Say what actually happened: a partial or denied read has its own reason ("Threat history needs
            // administrator rights."), which is more honest than the collector's generic "unavailable" sentence.
            var reason = cov["reason"]?.GetValue<string>();
            var observed = cov["observations"]?.GetValue<int>() ?? 0;
            var display = covStatus switch
            {
                "skipped" when observed > 0 => $"{title} was stopped early.",
                "cancelled" or "skipped" => $"{title} was not checked.",
                "partial" or "denied" or "unsupported" when !string.IsNullOrWhiteSpace(reason) => reason!,
                _ => collector?.GapDisplay ?? $"{title} was unavailable.",
            };
            gaps.Add(new JsonObject
            {
                ["id"] = Ids.Gap(scanId, collectorId), ["category"] = cov.Text("category"), ["title"] = title,
                ["display"] = display, ["source"] = collector?.GapSource ?? collectorId, ["collectorId"] = collectorId, ["status"] = covStatus,
            });
        }

        var baseReportId = scan.Base ?? db.Scalar(
            "SELECT report_id FROM reports WHERE intent = $i AND scan_id <> $s ORDER BY rowid DESC LIMIT 1", ("$i", scan.Intent), ("$s", scanId)) as string;
        JsonObject? baseReport = null;
        if (baseReportId is not null)
            try { baseReport = ReportService.Load(db, baseReportId); } catch (RpcError) { baseReportId = null; }

        var report = new JsonObject
        {
            ["reportId"] = reportId, ["scanId"] = scanId, ["revision"] = 1, ["status"] = status,
            ["generatedAt"] = generatedAt, ["intent"] = scan.Intent,
            ["counts"] = new JsonObject { ["findings"] = findings.Count, ["gaps"] = gaps.Count },
            ["categories"] = Categories(coverageRows, ruleCoverage, outcomes, findings),
            ["baseReportId"] = baseReportId,
            ["comparison"] = null,
            ["evidence"] = new JsonArray(evidence.Select(e => (JsonNode?)e).ToArray()),
            ["findings"] = findings,
            ["rules"] = outcomes,
            ["coverage"] = coverage,
            ["gaps"] = gaps,
        };
        if (baseReport is not null)
        {
            var (added, resolved, unchanged) = Compare(report, baseReport);
            report["comparison"] = new JsonObject
            {
                ["new"] = Json.Array(added.Select(f => f.Text("fingerprint"))),
                ["resolved"] = Json.Array(resolved.Select(f => f.Text("fingerprint"))),
                ["unchanged"] = unchanged.Count,
            };
        }

        db.Exec("INSERT INTO reports (report_id, scan_id, generated_at, status, intent, body) VALUES ($r, $s, $g, $st, $i, $b)",
            ("$r", reportId), ("$s", scanId), ("$g", generatedAt), ("$st", status), ("$i", scan.Intent), ("$b", Json.Write(report)));

        Followups.Create(db, fixture, catalog, reportId, cards, profile, scan.Intent, scan.Text);
        return reportId;
    }

    /// <summary>
    /// One row per planned category (protocol §9 Report.categories). A category's collectors are the planned
    /// collectors in it plus the inputs of its evaluated rules (so Misc. shows the collector its rule reads).
    /// status: complete (all complete), none (none complete or partial), partial otherwise. The rules counts tell
    /// "evaluated and clear" from "could not evaluate" within a category.
    /// </summary>
    private JsonArray Categories(List<JsonObject> coverage, Dictionary<string, string> effective, JsonArray outcomes, JsonArray findings)
    {
        var byCollector = effective; // a skipped collector that kept samples counts as partial, as rules see it
        var ids = coverage.Select(c => c.Text("category")).Concat(outcomes.Select(o => o.Text("category"))).Distinct()
            .OrderBy(fixture.CategoryOrder).ThenBy(x => x, StringComparer.Ordinal);
        var rows = new JsonArray();
        foreach (var id in ids)
        {
            var rules = outcomes.Where(o => o.Text("category") == id).Select(o => o!.AsObject()).ToList();
            var collectors = coverage.Where(c => c.Text("category") == id).Select(c => c.Text("collectorId"))
                .Concat(rules.SelectMany(r => Json.Strings(r["inputs"]))).Distinct().Where(byCollector.ContainsKey).ToList();
            var checkedCount = collectors.Count(c => byCollector[c] is "complete" or "partial");
            var status = collectors.Count > 0 && collectors.All(c => byCollector[c] == "complete") ? "complete"
                : checkedCount == 0 ? "none" : "partial";
            int Count(string s) => rules.Count(r => r.Text("status") == s);
            rows.Add(new JsonObject
            {
                ["id"] = id, ["label"] = fixture.CategoryLabel(id), ["status"] = status,
                ["checked"] = checkedCount, ["planned"] = collectors.Count,
                ["findings"] = findings.Count(f => f.Text("category") == id),
                ["rules"] = new JsonObject
                {
                    ["finding"] = Count("finding"), ["clear"] = Count("clear"),
                    ["insufficient"] = Count("insufficient"), ["inapplicable"] = Count("inapplicable"),
                },
            });
        }
        return rows;
    }

    /// <summary>
    /// By fingerprint. A base finding counts as resolved only when its rule was evaluated (finding or clear) in the
    /// new report; one whose rule could not run is neither resolved nor unchanged.
    /// </summary>
    internal static (List<JsonObject> New, List<JsonObject> Resolved, List<string> Unchanged) Compare(JsonObject report, JsonObject baseReport)
    {
        var now = report["findings"]!.AsArray().Select(f => f!.AsObject()).ToList();
        var before = baseReport["findings"]!.AsArray().Select(f => f!.AsObject()).ToList();
        var nowPrints = now.Select(f => f.Text("fingerprint")).ToHashSet();
        var beforePrints = before.Select(f => f.Text("fingerprint")).ToHashSet();
        var evaluated = (report["rules"] as JsonArray ?? []).Where(r => r.Text("status") is "finding" or "clear").Select(r => r.Text("id")).ToHashSet();
        return (
            now.Where(f => !beforePrints.Contains(f.Text("fingerprint"))).ToList(),
            before.Where(f => !nowPrints.Contains(f.Text("fingerprint")) && evaluated.Contains(f.Text("ruleId"))).ToList(),
            now.Where(f => beforePrints.Contains(f.Text("fingerprint"))).Select(f => f.Text("fingerprint")).ToList());
    }
}

internal sealed class ReportService(Store store)
{
    /// <summary>report.get stays well under the 1 MiB frame cap (protocol §1), leaving room for the JSON-RPC envelope.</summary>
    internal const int MaxReportBytes = 768 * 1024;

    public JsonNode Get(string reportId) => Bound(store.Read(db => WithDispositions(db, Load(db, reportId))), MaxReportBytes);

    /// <summary>
    /// Keeps a large report openable: when it serializes past <paramref name="maxBytes"/>, evidence no finding cites is
    /// dropped from the end until it fits, and evidenceOmitted counts what was left out (each still opens with
    /// evidence.get). Cited evidence is always kept. A report that fits is returned unchanged, without the field.
    /// </summary>
    internal static JsonObject Bound(JsonObject report, int maxBytes)
    {
        if (report["evidence"] is not JsonArray evidence) return report;
        var total = System.Text.Encoding.UTF8.GetByteCount(report.ToJsonString());
        if (total <= maxBytes) return report;
        var cited = new HashSet<string>();
        foreach (var f in report["findings"] as JsonArray ?? [])
            foreach (var id in Json.Strings(f?["evidenceIds"]).Concat(Json.Strings(f?["counterevidence"]))) cited.Add(id);

        var items = evidence.ToList();
        var sizes = items.Select(e => (e is null ? 4 : System.Text.Encoding.UTF8.GetByteCount(e.ToJsonString())) + 1).ToArray();
        var used = total - sizes.Sum() + 32; // the rest of the report, plus room for evidenceOmitted
        var keep = new bool[items.Count];
        for (var i = 0; i < items.Count; i++)
            if (items[i]?["id"] is JsonValue v && v.TryGetValue<string>(out var id) && cited.Contains(id)) { keep[i] = true; used += sizes[i]; }
        for (var i = 0; i < items.Count; i++)
            if (!keep[i] && used + sizes[i] <= maxBytes) { keep[i] = true; used += sizes[i]; }

        evidence.Clear();
        var omitted = 0;
        for (var i = 0; i < items.Count; i++)
            if (keep[i]) evidence.Add(items[i]); else omitted++;
        if (omitted > 0) report["evidenceOmitted"] = omitted;
        return report;
    }

    internal static JsonObject Load(Db db, string reportId)
    {
        var body = db.Scalar("SELECT body FROM reports WHERE report_id = $r", ("$r", reportId)) as string
            ?? throw RpcError.NotFound("report", reportId);
        try
        {
            var report = Json.Parse(body) as JsonObject ?? throw RpcError.Corrupt("report", reportId);
            return Upgrade(report);
        }
        catch (System.Text.Json.JsonException)
        {
            throw RpcError.Corrupt("report", reportId);
        }
    }

    /// <summary>
    /// Reports stored before v1.1 lack the M2 fields; fill them (deterministically) so every reader sees one shape.
    /// A missing key is added; values a v1.1 report already has are left alone.
    /// </summary>
    private static JsonObject Upgrade(JsonObject report)
    {
        if (report.ContainsKey("rules")) return report;
        if (report["findings"] is not JsonArray findings || report["evidence"] is not JsonArray evidence) throw RpcError.Corrupt("report", report.Text("reportId"));
        report["categories"] = new JsonArray();
        report["baseReportId"] = null;
        report["comparison"] = null;
        report["rules"] = new JsonArray();
        foreach (var e in evidence)
            if (e is JsonObject o && !o.ContainsKey("sensitivity")) o["sensitivity"] = ScanCoordinator.InferSensitivity(o["value"]);
        foreach (var f in findings)
        {
            if (f is not JsonObject finding) continue;
            if (!finding.ContainsKey("subject")) finding["subject"] = null;
            if (!finding.ContainsKey("counterevidence")) finding["counterevidence"] = new JsonArray();
            if (!finding.ContainsKey("settingsUri")) finding["settingsUri"] = null;
            if (!finding.ContainsKey("disposition")) finding["disposition"] = null;
        }
        return report;
    }

    /// <summary>Sets each finding's current disposition (they change after the report is built).</summary>
    internal static JsonObject WithDispositions(Db db, JsonObject report)
    {
        foreach (var f in report["findings"]!.AsArray())
        {
            var finding = f!.AsObject();
            finding["disposition"] = Dispositions.Active(db, finding.Text("fingerprint"), finding.Text("ruleId"), finding.Text("severity"));
        }
        return report;
    }

    public JsonNode List(JsonObject p)
    {
        var limit = P.Int(p, "limit", required: false) ?? 20;
        if (limit is < 1 or > 50) throw RpcError.Params("limit", "limit must be 1 to 50.");
        var before = P.Str(p, "before", required: false, maxLength: 64);
        return store.Read(db =>
        {
            long? beforeRow = null;
            if (before is not null)
                beforeRow = db.Scalar("SELECT rowid FROM reports WHERE report_id = $r", ("$r", before)) as long?
                    ?? throw RpcError.NotFound("report", before);
            var rows = db.Query(
                "SELECT report_id, scan_id, generated_at, status, intent FROM reports WHERE ($b IS NULL OR rowid < $b) ORDER BY rowid DESC LIMIT $l",
                r => new JsonObject
                {
                    ["reportId"] = r.GetString(0), ["scanId"] = r.GetString(1), ["generatedAt"] = r.GetString(2),
                    ["status"] = r.GetString(3), ["intent"] = r.GetString(4),
                }, ("$b", beforeRow), ("$l", limit + 1));
            var next = rows.Count > limit ? rows[(int)limit - 1]["reportId"]!.GetValue<string>() : null;
            return new JsonObject
            {
                ["reports"] = new JsonArray(rows.Take((int)limit).Select(r => (JsonNode?)r).ToArray()),
                ["next"] = next,
            };
        });
    }

    public JsonNode Evidence(JsonObject p)
    {
        var reportId = P.Req(p, "reportId", 64);
        var evidenceId = P.Req(p, "evidenceId", 64);
        var report = store.Read(db => Load(db, reportId));
        return report["evidence"]!.AsArray().FirstOrDefault(e => e?["id"]?.GetValue<string>() == evidenceId)?.DeepClone()
            ?? throw RpcError.NotFound("evidence", evidenceId);
    }

    /// <summary>finding.get: the finding, its cited and counter evidence, its rule's input coverage and the rule.</summary>
    public JsonNode Finding(JsonObject p)
    {
        var reportId = P.Req(p, "reportId", 64);
        var findingId = P.Req(p, "findingId", 64);
        return store.Read(db =>
        {
            var report = WithDispositions(db, Load(db, reportId));
            var finding = report["findings"]!.AsArray().FirstOrDefault(f => f.Text("id") == findingId)?.AsObject()
                ?? throw RpcError.NotFound("finding", findingId);
            var ids = Json.Strings(finding["evidenceIds"]).Concat(Json.Strings(finding["counterevidence"])).ToHashSet();
            var rule = (report["rules"] as JsonArray ?? []).FirstOrDefault(r => r.Text("id") == finding.Text("ruleId"))?.AsObject();
            var inputs = rule is null ? [] : Json.Strings(rule["inputs"]);
            return new JsonObject
            {
                ["finding"] = finding.DeepClone(),
                ["evidence"] = new JsonArray(report["evidence"]!.AsArray().Where(e => ids.Contains(e.Text("id"))).Select(e => e!.DeepClone()).ToArray()),
                ["coverage"] = new JsonArray(report["coverage"]!.AsArray().Where(c => inputs.Contains(c.Text("collectorId"))).Select(c => c!.DeepClone()).ToArray()),
                ["rule"] = new JsonObject
                {
                    ["id"] = finding.Text("ruleId"), ["version"] = finding.Text("ruleVersion"), ["inputs"] = Json.Array(inputs),
                },
            };
        });
    }

    /// <summary>report.compare {reportId, baseReportId} → {new:[Finding], resolved:[Finding], unchanged:[fingerprint]}.</summary>
    public JsonNode Compare(JsonObject p)
    {
        var reportId = P.Req(p, "reportId", 64);
        var baseId = P.Req(p, "baseReportId", 64);
        return store.Read(db =>
        {
            var report = WithDispositions(db, Load(db, reportId));
            var baseReport = WithDispositions(db, Load(db, baseId));
            var (added, resolved, unchanged) = ReportBuilder.Compare(report, baseReport);
            return new JsonObject
            {
                ["new"] = new JsonArray(added.Select(f => (JsonNode?)f.DeepClone()).ToArray()),
                ["resolved"] = new JsonArray(resolved.Select(f => (JsonNode?)f.DeepClone()).ToArray()),
                ["unchanged"] = Json.Array(unchanged),
            };
        });
    }
}

internal static class JsonExtensions
{
    public static string Text(this JsonNode? node, string key) => node?[key]?.GetValue<string>() ?? "";
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace OptimizationTool.Engine.Agent;

// One-time report reviews (contracts/m3-agent.md §4, docs/ai-and-client.md "Evidence sharing and report review").
// A preview freezes the projected selection with its revision, digest and expiry; submit consumes it once with
// tools disabled; export/import is the paste-anywhere route, validated against the same frozen evidence.
public sealed partial class AgentService
{
    public static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ExportLifetime = TimeSpan.FromHours(24);
    public const int MaxReviewBytes = 131072;

    private sealed record Review(string Id, string Kind, string? Recipient, string ReportId, long Revision, string EvidenceDigest,
        JsonObject Payload, string ExpiresAt, string? ConsumedAt);

    private static Review? LoadReview(Db db, string id) =>
        db.Query("SELECT review_id, kind, recipient_kind, report_id, revision, evidence_digest, payload, expires_at, consumed_at FROM reviews WHERE review_id = $r",
            r => new Review(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetString(5),
                (JsonObject)Json.Parse(r.GetString(6))!, r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8)), ("$r", id)).FirstOrDefault();

    /// <summary>Digest of the report evidence a selection cites, so an import detects a changed evidence revision.</summary>
    private static string EvidenceDigest(JsonObject report, IEnumerable<JsonObject> findings)
    {
        var ids = findings.SelectMany(f => Json.Strings(f["evidenceIds"])).ToHashSet();
        var cited = report["evidence"]!.AsArray().Where(e => ids.Contains(e.Text("id"))).Select(e => e!.DeepClone()).ToArray();
        return Json.Digest(new JsonObject { ["revision"] = report["revision"]?.DeepClone(), ["evidence"] = new JsonArray(cited) });
    }

    /// <summary>The selection, projected under a fresh review-scoped alias map (names never shared in reviews).</summary>
    private (JsonObject Payload, JsonObject Report, List<JsonObject> Findings) BuildSelection(Db db, string scopeId, string reportId, List<string>? findingIds)
    {
        var report = ReportService.WithDispositions(db, ReportService.Load(db, reportId));
        var all = report["findings"]!.AsArray().Select(f => f!.AsObject()).ToList();
        if (findingIds is not null)
        {
            var unknown = findingIds.FirstOrDefault(id => all.All(f => f.Text("id") != id));
            if (unknown is not null) throw RpcError.NotFound("finding", unknown);
        }
        var chosen = all.Where(f => findingIds is null || findingIds.Contains(f.Text("id"))).ToList();
        var evidence = report["evidence"]!.AsArray().Select(e => e!.AsObject()).ToDictionary(e => e.Text("id"));
        var stats = new ProjectionStats();
        var projected = chosen.Select(f => (JsonNode?)ProjectFinding(db, scopeId, f, evidence, new ShareOptions(false), stats)).ToArray();
        var payload = new JsonObject
        {
            ["v"] = 1, ["reportId"] = reportId, ["revision"] = report["revision"]?.DeepClone(), ["generatedAt"] = report.Text("generatedAt"),
            ["untrusted"] = UntrustedNote, ["findings"] = new JsonArray(projected), ["redaction"] = Redaction(stats),
        };
        if (Bytes(payload) > MaxReviewBytes) throw RpcError.State("too_large", "Select fewer findings.");
        return (payload, report, chosen);
    }

    private static List<string>? FindingIds(JsonObject p)
    {
        if (p["findingIds"] is null) return null;
        if (p["findingIds"] is not JsonArray a || a.Count > 200) throw RpcError.Params("findingIds", "findingIds must be an array of IDs.");
        return a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) && s.Length <= 64 ? s : throw RpcError.Params("findingIds", "findingIds must be IDs.")).Distinct().ToList();
    }

    /// <summary>
    /// What a preview shares, by class (m3-agent §4 additions): findings and measurements are shared; app/device names
    /// appear only as aliases in reviews (names: shared false); paths, accounts and addresses are never shared (locked).
    /// </summary>
    private static JsonArray FieldClasses(Db db, string scopeId, JsonObject payload)
    {
        var findings = payload["findings"]!.AsArray();
        var measurements = findings.Sum(f => f!["evidence"]!.AsArray().Count);
        var withheld = findings.Sum(f => f!["withheld"]?.GetValue<int>() ?? 0);
        var names = (long)db.Scalar("SELECT COUNT(*) FROM agent_aliases WHERE scope_id = $s", ("$s", scopeId))!;
        static JsonObject Row(string id, string label, long count, bool shared, bool locked = false)
        {
            var row = new JsonObject { ["id"] = id, ["label"] = label, ["count"] = count, ["shared"] = shared };
            if (locked) row["locked"] = true;
            return row;
        }
        return
        [
            Row("findings", "Findings", findings.Count, true),
            Row("measurements", "Measurements and times", measurements, measurements > 0),
            Row("names", "App names", names, false),
            Row("paths_accounts_addresses", "Paths, accounts, addresses", withheld, false, locked: true),
        ];
    }

    public JsonNode Preview(JsonObject p)
    {
        var recipient = P.OneOf(p, "recipientKind", RecipientKinds);
        var reportId = P.Req(p, "reportId", 64);
        var findingIds = FindingIds(p);
        return _store.Write(db =>
        {
            var previewId = "prev-" + Ids.RandomHex();
            var (payload, report, chosen) = BuildSelection(db, previewId, reportId, findingIds);
            var expires = Time.Format(Now() + PreviewLifetime);
            var digest = Json.Digest(payload);
            db.Exec("""
                INSERT INTO reviews (review_id, kind, recipient_kind, report_id, revision, evidence_digest, payload, created_at, expires_at)
                VALUES ($r, 'preview', $k, $rep, $rev, $d, $p, $c, $e)
                """, ("$r", previewId), ("$k", recipient), ("$rep", reportId), ("$rev", report["revision"]?.GetValue<long>() ?? 1),
                ("$d", EvidenceDigest(report, chosen)), ("$p", Json.Write(payload)), ("$c", db.Now()), ("$e", expires));
            return (JsonNode)new JsonObject
            {
                ["previewId"] = previewId, ["expiresAt"] = expires, ["digest"] = digest, ["payload"] = payload.DeepClone(),
                ["bytes"] = Bytes(payload), ["fieldClasses"] = FieldClasses(db, previewId, payload),
            };
        });
    }

    /// <summary>review.submit: consumes the preview once, sends it with tools disabled, keeps only citations it contained.</summary>
    public async Task<JsonNode> SubmitAsync(JsonObject p)
    {
        var key = P.IdempotencyKey(p);
        var previewId = P.Req(p, "previewId", 64);
        var digest = Idempotency.Digest(p);
        var replay = _store.Read(db => db.Query("SELECT digest, result FROM idempotency WHERE method = 'review.submit' AND key = $k",
            r => (Digest: r.GetString(0), Result: r.GetString(1)), ("$k", key)).FirstOrDefault());
        if (replay.Digest is not null)
            return replay.Digest == digest ? Json.Parse(replay.Result)! : throw new RpcError(ErrorCodes.IdempotencyMismatch, "This idempotencyKey was used with different parameters.", new JsonObject { ["idempotencyKey"] = key });

        var review = _store.Write(db =>
        {
            var r = LoadReview(db, previewId);
            if (r is null || r.Kind != "preview") throw RpcError.NotFound("preview", previewId);
            if (r.ConsumedAt is not null) throw RpcError.State("consumed", "This preview was already sent.");
            if (Now() >= ParseTime(r.ExpiresAt)) throw RpcError.State("expired", "This preview expired. Preview again.");
            if (!InAppKinds.Contains(r.Recipient ?? "")) throw RpcError.State("not_in_app", "Use export for that recipient.");
            db.Exec("UPDATE reviews SET consumed_at = $t WHERE review_id = $r", ("$t", db.Now()), ("$r", previewId));
            return r;
        });

        var kind = review.Recipient!;
        var adapter = Adapter(kind);
        var credential = await CredentialAsync(kind, CancellationToken.None).ConfigureAwait(false);
        void Unconsume() => _store.Write(db => { db.Exec("UPDATE reviews SET consumed_at = NULL WHERE review_id = $r", ("$r", previewId)); });
        if (adapter is null || credential is null)
        {
            Unconsume();
            throw RpcError.State("provider_not_configured", "Connect that provider first.");
        }

        ProviderTurn turn;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var model = await ResolveModelAsync(kind, adapter, credential, null, cts.Token).ConfigureAwait(false);
            var request = new ProviderRequest(model, ReviewPrompt, [new UserText(Json.Write(review.Payload))], [], MaxOutputTokens);
            turn = await adapter.RunTurnAsync(credential, request, cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ProviderException or OperationCanceledException)
        {
            Unconsume(); // nothing was answered: the user can send the same preview again
            if (ex is ProviderException failed) RecordProviderOutcome(kind, failed.Code);
            var code = ex is ProviderException pe ? pe.Code : "timeout";
            throw new RpcError(ErrorCodes.InvalidState, CheckMessage(code), new JsonObject { ["reason"] = "provider_error", ["code"] = code });
        }

        RecordProviderOutcome(kind, null);
        var allowed = EvidenceIdsIn(review.Payload).ToHashSet();
        var lines = new JsonArray();
        var dropped = 0;
        foreach (var raw in (turn.Turn.Text ?? "").Split('\n'))
        {
            var (text, cited, d) = ValidateCitations(raw, allowed);
            dropped += d;
            if (text.Length > 0) lines.Add(new JsonObject { ["text"] = text, ["evidenceIds"] = Json.Array(cited) });
        }
        var result = new JsonObject
        {
            ["lines"] = lines,
            ["usage"] = new JsonObject { ["inputTokens"] = turn.Usage.InputTokens, ["outputTokens"] = turn.Usage.OutputTokens },
            ["dropped"] = dropped,
        };
        _store.Write(db => db.Exec("INSERT OR IGNORE INTO idempotency (method, key, digest, result, created_at) VALUES ('review.submit', $k, $d, $r, $t)",
            ("$k", key), ("$d", digest), ("$r", Json.Write(result)), ("$t", db.Now())));
        return result;
    }

    /// <summary>review.export: the paste-anywhere JSON (docs/ai-and-client.md), valid 24 hours, importable once.</summary>
    public JsonNode Export(JsonObject p)
    {
        var reportId = P.Req(p, "reportId", 64);
        var findingIds = FindingIds(p);
        return _store.Write(db =>
        {
            var reviewId = "r-" + Ids.RandomHex();
            var (payload, report, chosen) = BuildSelection(db, reviewId, reportId, findingIds);
            var expires = Time.Format(Now() + ExportLifetime);
            var export = new JsonObject
            {
                ["v"] = 1, ["reviewId"] = reviewId, ["expiresAt"] = expires, ["untrusted"] = UntrustedNote,
                ["findings"] = new JsonArray(payload["findings"]!.AsArray().Select(f => (JsonNode?)new JsonObject
                {
                    ["id"] = f.Text("id"), ["ruleId"] = f.Text("ruleId"), ["category"] = f.Text("category"), ["title"] = f.Text("title"), ["severity"] = f.Text("severity"),
                    ["evidence"] = f!["evidence"]!.DeepClone(), ["availableActionIds"] = new JsonArray(),
                }).ToArray()),
                ["responseShape"] = new JsonObject
                {
                    ["v"] = 1, ["reviewId"] = reviewId,
                    ["explanations"] = new JsonArray(new JsonObject
                    {
                        ["findingId"] = "<finding id>", ["evidenceIds"] = new JsonArray("<evidence id>"), ["summary"] = "<one or two sentences>",
                        ["uncertainty"] = "<what is unknown>", ["suggestedActionIds"] = new JsonArray(),
                    }),
                },
            };
            db.Exec("""
                INSERT INTO reviews (review_id, kind, recipient_kind, report_id, revision, evidence_digest, payload, created_at, expires_at)
                VALUES ($r, 'export', NULL, $rep, $rev, $d, $p, $c, $e)
                """, ("$r", reviewId), ("$rep", reportId), ("$rev", report["revision"]?.GetValue<long>() ?? 1),
                ("$d", EvidenceDigest(report, chosen)), ("$p", Json.Write(export)), ("$c", db.Now()), ("$e", expires));
            return (JsonNode)new JsonObject { ["reviewId"] = reviewId, ["json"] = Json.Write(export) };
        });
    }

    public const int MaxImportBytes = 262144, MaxImportTextLength = 2000;

    /// <summary>
    /// review.import: an unexpired, unconsumed export of this user's database, against an unchanged report revision
    /// and evidence; each explanation's finding and evidence IDs must be from that export. Text is annotation only.
    /// </summary>
    public JsonNode Import(JsonObject p)
    {
        var reviewId = P.Req(p, "reviewId", 64);
        var text = P.Req(p, "json", MaxImportBytes);
        JsonObject answer;
        try { answer = JsonNode.Parse(text) as JsonObject ?? throw RpcError.Params("json", "json must be an object."); }
        catch (System.Text.Json.JsonException) { throw RpcError.Params("json", "json is not valid JSON."); }
        if (answer["v"] is not JsonValue v || !v.TryGetValue<int>(out var version) || version != 1) throw RpcError.Params("json.v", "Unsupported version.");
        if (answer["reviewId"]?.GetValue<string>() != reviewId) throw RpcError.Params("json.reviewId", "The answer is for another review.");
        if (answer["explanations"] is not JsonArray explanations || explanations.Count > 200) throw RpcError.Params("json.explanations", "explanations must be an array.");

        return _store.Write(db =>
        {
            var review = LoadReview(db, reviewId);
            if (review is null || review.Kind != "export") throw RpcError.NotFound("review", reviewId);
            if (review.ConsumedAt is not null) throw RpcError.State("consumed", "This review was already imported.");
            if (Now() >= ParseTime(review.ExpiresAt)) throw RpcError.State("expired", "This review expired.");

            JsonObject report;
            try { report = ReportService.Load(db, review.ReportId); }
            catch (RpcError) { throw RpcError.State("revision_changed", "The report changed since the export."); }
            var exported = review.Payload["findings"]!.AsArray().Select(f => f!.AsObject()).ToList();
            var current = report["findings"]!.AsArray().Select(f => f!.AsObject()).Where(f => exported.Any(e => e.Text("id") == f.Text("id"))).ToList();
            if ((report["revision"]?.GetValue<long>() ?? 1) != review.Revision || current.Count != exported.Count || EvidenceDigest(report, current) != review.EvidenceDigest)
                throw RpcError.State("revision_changed", "The report changed since the export.");

            var lines = new JsonArray();
            var rejected = 0;
            foreach (var node in explanations)
            {
                if (node is not JsonObject e) { rejected++; continue; }
                var finding = exported.FirstOrDefault(f => f.Text("id") == (e["findingId"] as JsonValue)?.ToString());
                var evidenceIds = e["evidenceIds"] is JsonArray ea && ea.All(x => x is JsonValue xv && xv.TryGetValue<string>(out _)) ? Json.Strings(ea) : null;
                var known = finding is null ? [] : EvidenceIdsIn(finding).ToHashSet();
                var summary = e["summary"] is JsonValue sv && sv.TryGetValue<string>(out var s) ? s : null;
                var actions = e["suggestedActionIds"];
                if (finding is null || evidenceIds is null || evidenceIds.Any(id => !known.Contains(id)) || summary is null
                    || (actions is not null && (actions is not JsonArray aa || aa.Count > 0)))
                {
                    rejected++; // unknown IDs, or an action M3 does not offer
                    continue;
                }
                var uncertainty = e["uncertainty"] is JsonValue uv && uv.TryGetValue<string>(out var u) ? u : null;
                lines.Add(new JsonObject
                {
                    ["findingId"] = finding.Text("id"),
                    ["text"] = Annotation(summary),
                    ["uncertainty"] = uncertainty is null ? null : Annotation(uncertainty),
                    ["evidenceIds"] = Json.Array(evidenceIds.Distinct()),
                });
            }
            db.Exec("UPDATE reviews SET consumed_at = $t WHERE review_id = $r", ("$t", db.Now()), ("$r", reviewId));
            return (JsonNode)new JsonObject { ["lines"] = lines, ["rejected"] = rejected };
        });
    }

    private static string Annotation(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            if (sb.Length >= MaxImportTextLength) break;
            if (char.IsControl(ch) && ch != '\n') continue;
            if (char.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.Format) continue;
            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    internal static string Sha256(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}

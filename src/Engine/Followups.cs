using System.Text.Json.Nodes;

namespace OptimizationTool.Engine;

/// <summary>
/// The review queue for one report (protocol §6 followup.*, §9 dispositions). Stored as one JSON row per report
/// with a revision:
///   cards     – card content fixed when the report was built, plus internal ordering fields (stripped on read)
///   queue     – pending card IDs, front first
///   decisions – [{cardId, status, via?}] in the order first decided; via "disposition" marks a cross-scan one
///   replies   – cardId → template answer to the last card question
/// Fixture reports keep the M1 order (the prototype's buildQueue / focusFor). Module reports use the M2 order.
/// </summary>
internal sealed class Followups(Store store, Fixture fixture)
{
    public static readonly string[] Scopes = ["card", "report"];
    public static readonly string[] Options = ["ignore", "later", "done", "decline", "restore", "intended"];

    /// <summary>Cards shown at once (v1.4): the card plus up to two upcoming; any of them can be answered.</summary>
    public const int VisibleCards = 3;

    /// <summary>Reviewed rules whose cards offer "Intended" (protocol §10): settings people often choose on purpose.</summary>
    public static readonly IReadOnlySet<string> IntendedRules = new HashSet<string>
    {
        "net.wildcard_listener_public", "fw.public_off", "fw.profile_off", "startup.many_entries", "uac.disabled", "secureboot.off",
        "display.below_max_refresh", "start.suggestions_on", "privacy.optional_diag", "power.high_perf_on_battery",
    };

    /// <summary>Adds the "Intended" option (before "Ignore", or last) to cards of the reviewed rules.</summary>
    private static void OfferIntended(JsonObject card, string ruleId)
    {
        if (!IntendedRules.Contains(ruleId) || card["options"] is not JsonArray options) return;
        var labels = Json.Strings(options);
        if (labels.Contains("Intended")) return;
        var at = labels.IndexOf("Ignore");
        labels.Insert(at < 0 ? labels.Count : at, "Intended");
        card["options"] = Json.Array(labels);
    }
    private static readonly string[] Internal = ["key", "ruleId", "fingerprint", "severity", "subject", "ruleOrder", "noteTaskId", "aiTaskId"];

    public static JsonObject CardFromFixture(RuleDef rule, string reportId, JsonObject finding)
    {
        var c = rule.Card;
        var card = Base(reportId, finding, rule.Title, c["text"]!.GetValue<string>(), c["options"]!.DeepClone(), c["guide"]?.DeepClone(), null);
        card["preview"] = c["preview"]?.DeepClone();
        card["actionId"] = c["actionId"]?.DeepClone();
        card["key"] = rule.CardKey;
        OfferIntended(card, rule.RuleId);
        return card;
    }

    public static JsonObject CardFromDraft(string reportId, JsonObject finding, FindingDraft draft)
    {
        var card = Base(reportId, finding, draft.Title, draft.Card.Text, Json.Array(draft.Card.Options),
            draft.Card.Guide is null ? null : Json.Array(draft.Card.Guide), draft.Card.SettingsUri);
        // M4: a catalog action the card can preview, with the parameters the rule chose.
        card["actionId"] = draft.Card.ActionId;
        card["actionParameters"] = draft.Card.ActionParameters?.DeepClone();
        OfferIntended(card, finding.Text("ruleId"));
        return card;
    }

    private static JsonObject Base(string reportId, JsonObject finding, string title, string text, JsonNode options, JsonNode? guide, string? settingsUri) => new()
    {
        ["id"] = Ids.Card(reportId, finding.Text("id")),
        ["findingId"] = finding.Text("id"),
        ["category"] = finding.Text("category"),
        ["title"] = title,
        ["text"] = text,
        ["evidenceIds"] = finding["evidenceIds"]!.DeepClone(),
        ["options"] = options,
        ["guide"] = guide,
        ["preview"] = null,
        ["actionId"] = null,
        ["settingsUri"] = settingsUri,
        ["ruleId"] = finding.Text("ruleId"),
        ["fingerprint"] = finding.Text("fingerprint"),
        ["severity"] = finding.Text("severity"),
        ["subject"] = finding.Text("subject"),
    };

    public static void Create(Db db, Fixture fixture, AuditCatalog catalog, string reportId, JsonArray cards, Profile profile, string intent, string text)
    {
        var all = cards.Select(c => c!.AsObject()).ToList();
        var ordered = catalog.IsFixture ? FixtureOrder(fixture, all, profile, intent, text) : M2Order(catalog, all, profile);

        var queue = new List<string>();
        var decisions = new JsonArray();
        foreach (var card in ordered)
        {
            var disposition = Dispositions.Active(db, card.Text("fingerprint"), card.Text("ruleId"), card.Text("severity"));
            if (disposition is null) queue.Add(card.Text("id"));
            else decisions.Add(new JsonObject { ["cardId"] = card.Text("id"), ["status"] = disposition.Text("status"), ["via"] = "disposition" });
        }

        var state = new JsonObject
        {
            ["cards"] = cards,
            ["queue"] = Json.Array(queue),
            ["decisions"] = decisions,
            ["replies"] = new JsonObject(),
        };
        db.Exec("INSERT INTO followups (report_id, revision, state) VALUES ($r, 1, $s)", ("$r", reportId), ("$s", Json.Write(state)));
    }

    /// <summary>M1: a targeted intent's categories (or a typed request's focus) first, then the prototype's buildQueue.</summary>
    private static List<JsonObject> FixtureOrder(Fixture fixture, List<JsonObject> cards, Profile profile, string intent, string text)
    {
        var keyOrder = cards.Select(c => c.Text("key")).ToList(); // rule catalog order: c1…c8
        var frontCats = intent != "audit.full" && fixture.IntentCategories.TryGetValue(intent, out var cats)
            ? cats : fixture.FocusCategories(text);
        var front = cards.Where(c => frontCats.Contains(c.Text("category"))).Select(c => c.Text("key"));
        return front.Concat(BaseQueue(fixture, profile)).Concat(keyOrder).Distinct()
            .Select(k => cards.FirstOrDefault(c => c.Text("key") == k)).OfType<JsonObject>().ToList();
    }

    /// <summary>The prototype's buildQueue: security first; goals and usage order the rest.</summary>
    internal static List<string> BaseQueue(Fixture fixture, Profile profile)
    {
        var perf = OnboardingService.HasWord(profile.Goals, "performance") || OnboardingService.HasWord(profile.Goals, "optimi")
            || OnboardingService.HasWord(profile.Uses, "gam");
        var custom = OnboardingService.HasWord(profile.Goals, "custom") || OnboardingService.HasWord(profile.Uses, "gam");
        bool Holds(string cond) => cond switch
        {
            "perf" => perf, "!perf" => !perf, "custom" => custom, "!custom" => !custom, _ => false,
        };

        var keys = new List<string>();
        foreach (var item in fixture.QueueOrder)
        {
            if (item is JsonValue v) keys.Add(v.GetValue<string>());
            else if (item is JsonObject o && Holds(o["if"]!.GetValue<string>())) keys.AddRange(Json.Strings(o["then"]));
        }
        return keys.Distinct().ToList();
    }

    /// <summary>
    /// Protocol §9 "Queue order (M2)": active threats → other security by severity → the rest by severity, with the
    /// user's goal/usage categories first within a severity → info last. Ties: rule catalog order, then subject.
    /// </summary>
    internal static List<JsonObject> M2Order(AuditCatalog catalog, List<JsonObject> cards, Profile profile)
    {
        var preferred = new HashSet<string>();
        if (OnboardingService.HasWord(profile.Goals, "performance") || OnboardingService.HasWord(profile.Goals, "optimi")
            || OnboardingService.HasWord(profile.Uses, "gam")) preferred.Add("performance");
        if (OnboardingService.HasWord(profile.Goals, "custom")) preferred.Add("customization");
        if (OnboardingService.HasWord(profile.Goals, "secur")) preferred.Add("security");

        static int Band(JsonObject c) =>
            c.Text("ruleId") == "av.active_threat" ? 0
            : c.Text("severity") == "info" ? 3
            : c.Text("category") == "security" ? 1 : 2;

        return cards
            .OrderBy(Band)
            .ThenByDescending(c => ReportBuilder.SeverityRank(c.Text("severity")))
            .ThenBy(c => preferred.Contains(c.Text("category")) ? 0 : 1)
            .ThenBy(c => catalog.RuleOrder(c.Text("ruleId")))
            .ThenBy(c => c.Text("subject"), StringComparer.Ordinal)
            .ToList();
    }

    public JsonNode Get(string reportId) => store.Read(db => View(reportId, Load(db, reportId)));

    public JsonNode Respond(JsonObject p)
    {
        var key = P.IdempotencyKey(p);
        var reportId = P.Req(p, "reportId", 64);
        var expected = P.Int(p, "expectedRevision")!.Value;
        var scope = P.OneOf(p, "scope", Scopes);
        var cardId = P.Str(p, "cardId", required: false, maxLength: 64);
        var option = P.Str(p, "option", required: false, maxLength: 16);
        var text = P.Str(p, "text", required: false, maxLength: 500);
        if (option is not null && !Options.Contains(option))
            throw RpcError.Params("option", $"option must be one of: {string.Join(", ", Options)}.");
        if (scope == "card")
        {
            if (cardId is null) throw RpcError.Params("cardId", "cardId is required for scope card.");
            if ((option is null) == (text is null)) throw RpcError.Params("option", "Send exactly one of option or text.");
        }
        else if (string.IsNullOrWhiteSpace(text) || option is not null || cardId is not null)
        {
            throw RpcError.Params("text", "scope report takes text only.");
        }

        var (result, _) = store.Write(db => Idempotency.Run(db, "followup.respond", key, p, () =>
        {
            var (revision, state) = Load(db, reportId);
            if (expected != revision)
                throw new RpcError(ErrorCodes.StaleRevision, "The follow-up queue changed.", new JsonObject { ["current"] = View(reportId, (revision, state)) });

            if (scope == "report") Rerank(state, text!);
            else if (option == "intended") Intend(db, state, reportId, cardId!);
            else if (option is not null) Decide(state, cardId!, option);
            else Ask(db, state, reportId, cardId!, text!);

            return Save(db, reportId, revision, state);
        }));
        return result;
    }

    /// <summary>finding.setDisposition: stores the disposition and takes the affected cards out of this report's queue.</summary>
    public JsonNode SetDisposition(JsonObject p)
    {
        var key = P.IdempotencyKey(p);
        var reportId = P.Req(p, "reportId", 64);
        var findingId = P.Req(p, "findingId", 64);
        var status = P.OneOf(p, "status", Dispositions.Statuses);
        var scope = P.OneOf(p, "scope", Dispositions.Scopes);
        var expiresText = P.Str(p, "expiresAt", required: false, maxLength: 40);
        if (status == "intended" && scope != "finding") throw RpcError.Params("scope", "intended applies to one finding.");
        if (status == "intended" && expiresText is not null) throw RpcError.Params("expiresAt", "intended has no expiry.");

        var (result, _) = store.Write(db => Idempotency.Run(db, "finding.setDisposition", key, p, () =>
        {
            var expiresAt = Dispositions.ParseExpiry(expiresText, status, db.Now());
            var finding = FindFinding(db, reportId, findingId);
            Dispositions.Set(db, finding, status, scope, expiresAt);

            var (revision, state) = Load(db, reportId);
            var affected = state["cards"]!.AsArray().Select(c => c!.AsObject())
                .Where(c => scope == "rule" ? c.Text("ruleId") == finding.Text("ruleId") : c.Text("findingId") == findingId)
                .Select(c => c.Text("id")).ToList();
            SetQueue(state, IdList(Queue(state)).Where(id => !affected.Contains(id)));
            foreach (var cardId in affected) SetDecision(state, cardId, status, via: "disposition");
            return Save(db, reportId, revision, state);
        }));
        return result;
    }

    /// <summary>finding.clearDisposition: removes it; the card (and any other card it hid) re-enters the queue, its card first.</summary>
    public JsonNode ClearDisposition(JsonObject p)
    {
        var key = P.IdempotencyKey(p);
        var reportId = P.Req(p, "reportId", 64);
        var findingId = P.Req(p, "findingId", 64);

        var (result, _) = store.Write(db => Idempotency.Run(db, "finding.clearDisposition", key, p, () =>
        {
            var finding = FindFinding(db, reportId, findingId);
            Dispositions.Clear(db, finding);

            var (revision, state) = Load(db, reportId);
            var cards = state["cards"]!.AsArray().Select(c => c!.AsObject()).ToList();
            var reentering = Decisions(state).Select(d => d!.AsObject())
                .Where(d => d.Text("via") == "disposition")
                .Select(d => cards.First(c => c.Text("id") == d.Text("cardId")))
                .Where(c => Dispositions.Active(db, c.Text("fingerprint"), c.Text("ruleId"), c.Text("severity")) is null)
                .OrderBy(c => c.Text("findingId") == findingId ? 0 : 1)
                .Select(c => c.Text("id")).ToList();
            foreach (var cardId in reentering) SetDecision(state, cardId, "restored", via: null);
            var queue = IdList(Queue(state));
            SetQueue(state, reentering.Concat(queue.Where(id => !reentering.Contains(id))));
            return Save(db, reportId, revision, state);
        }));
        return result;
    }

    private static JsonObject FindFinding(Db db, string reportId, string findingId) =>
        ReportService.Load(db, reportId)["findings"]!.AsArray().FirstOrDefault(f => f.Text("id") == findingId)?.AsObject()
        ?? throw RpcError.NotFound("finding", findingId);

    /// <summary>
    /// M4: a verified change (or a manual task verified by re-scan) closes the finding's card: decision "verified",
    /// linked to its operation and whether it can be undone. The card leaves the queue; review state only.
    /// </summary>
    internal static void MarkVerified(Db db, string reportId, string findingId, string? operationId, bool undoAvailable)
    {
        if (db.Scalar("SELECT 1 FROM followups WHERE report_id = $r", ("$r", reportId)) is null) return;
        var (revision, state) = Load(db, reportId);
        var card = state["cards"]!.AsArray().FirstOrDefault(c => c.Text("findingId") == findingId)?.AsObject();
        if (card is null) return;
        var cardId = card.Text("id");
        SetDecision(state, cardId, "verified", via: operationId is null ? "rescan" : "action");
        var decision = Decisions(state).First(d => d.Text("cardId") == cardId)!.AsObject();
        decision["operationId"] = operationId;
        decision["undoAvailable"] = operationId is null ? null : undoAvailable;
        SetQueue(state, IdList(Queue(state)).Where(id => id != cardId));
        Save(db, reportId, revision, state);
    }

    /// <summary>The card's catalog action parameters for a finding (M4 preview from a card), or null.</summary>
    internal static (string? ActionId, JsonObject? Parameters) CardAction(Db db, string reportId, string findingId)
    {
        if (db.Scalar("SELECT state FROM followups WHERE report_id = $r", ("$r", reportId)) is not string text) return (null, null);
        var card = (Json.Parse(text)?["cards"] as JsonArray)?.FirstOrDefault(c => c.Text("findingId") == findingId);
        return (card?["actionId"]?.GetValue<string>(), card?["actionParameters"]?.DeepClone() as JsonObject);
    }


    // ---------------------------------------------------------------- M5: the deck as the AI sees and improves it

    /// <summary>The title get_cards shows for an AI card whose own title is withheld from the asking task.</summary>
    public const string AiTitleFallback = "A suggestion from the assistant";

    /// <summary>AI cards a task may add to a deck (m5-chat §5).</summary>
    public const int MaxAiCards = 5;

    /// <summary>
    /// get_cards: the deck in the order the user sees it (queued cards first, then decided ones), each with a status
    /// open | done | ignored | later | intended, its origin and AI note. Titles of report cards follow the names
    /// rule (generic phrase unless names are shared); card text and options never leave. AI titles and notes written by
    /// another task reach a task without share.names only when that task did not share names either. Any AI text that
    /// fails the sensitive-string check is withheld for every reader (the note is left out, the title is <see cref="AiTitleFallback"/>). Null when the
    /// report has no deck.
    /// </summary>
    internal static JsonArray? AgentCards(Db db, string reportId, bool shareNames, string? taskId)
    {
        if (db.Scalar("SELECT 1 FROM followups WHERE report_id = $r", ("$r", reportId)) is null) return null;
        var (_, state) = Load(db, reportId);
        var queue = IdList(Queue(state));
        var decided = Decisions(state).Select(d => (Id: d.Text("cardId"), Status: d.Text("status"))).Where(d => !queue.Contains(d.Id)).ToList();
        var listed = queue.Select(id => (Id: id, Status: "open")).Concat(decided).ToList();
        var seen = listed.Select(x => x.Id).ToHashSet();
        listed.AddRange(state["cards"]!.AsArray().Select(c => c.Text("id")).Where(id => !seen.Contains(id)).Select(id => (Id: id, Status: "open")));
        // Free text a model wrote leaves as is for the task that wrote it or one that shares names. To another task it
        // leaves only when its writer did not see names either (share.names off) and it passes the sensitive-string check.
        var writerShared = new Dictionary<string, bool>();
        bool WriterSharedNames(string? from)
        {
            if (from is null) return false;
            if (writerShared.TryGetValue(from, out var known)) return known;
            var shared = true; // an unknown or unreadable writer counts as one that saw names
            if (db.Scalar("SELECT body FROM agent_tasks WHERE task_id = $t", ("$t", from)) is string body)
            {
                try { shared = Json.Parse(body)?["grant"]?["share"]?["names"]?.GetValue<bool>() ?? true; }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException) { }
            }
            return writerShared[from] = shared;
        }
        string? AiText(string text, string? from)
        {
            if (Agent.Sanitize.LooksSensitive(text)) return null; // a path, address or credential never leaves, whoever wrote it
            if (shareNames || (from is not null && from == taskId)) return Agent.AgentService.Clean(text);
            return WriterSharedNames(from) ? null : Agent.AgentService.Clean(text);
        }
        var cards = new JsonArray();
        foreach (var (id, status) in listed)
        {
            var card = state["cards"]!.AsArray().FirstOrDefault(c => c.Text("id") == id)?.AsObject();
            if (card is null) continue;
            var ai = card.Text("origin") == "ai";
            var view = new JsonObject
            {
                ["cardId"] = id,
                ["findingId"] = card["findingId"]?.GetValue<string>() is { Length: > 0 } f ? f : null,
                ["title"] = ai ? AiText(card.Text("title"), card["aiTaskId"]?.GetValue<string>()) ?? AiTitleFallback : Agent.FindingTitles.For(card.Text("ruleId"), card.Text("category"), card.Text("title"), shareNames),
                ["category"] = card.Text("category"),
                ["status"] = status switch
                {
                    "ignored" or "declined" => "ignored", "done" or "verified" => "done", "intended" => "intended", "snoozed" or "later" => "later", _ => "open",
                },
                ["origin"] = ai ? "ai" : "report",
            };
            if (card["note"]?.GetValue<string>() is { Length: > 0 } note && AiText(note, card["noteTaskId"]?.GetValue<string>()) is { } shown) view["note"] = shown; // withheld: no note
            cards.Add(view);
        }
        return cards;
    }

    /// <summary>note_card: sets the card's AI note (one per card; a later note replaces it). False when the card is not in the deck.</summary>
    internal static bool NoteCard(Db db, string reportId, string cardId, string text, string taskId, string by)
    {
        if (db.Scalar("SELECT 1 FROM followups WHERE report_id = $r", ("$r", reportId)) is null) return false;
        var (revision, state) = Load(db, reportId);
        var card = state["cards"]!.AsArray().FirstOrDefault(c => c.Text("id") == cardId)?.AsObject();
        if (card is null) return false;
        card["note"] = text;
        card["noteBy"] = by;
        card["noteTaskId"] = taskId;
        Save(db, reportId, revision, state);
        return true;
    }

    /// <summary>add_card: appends an origin "ai" card (no action) to the end of the queue. Null when the task already added <see cref="MaxAiCards"/>.</summary>
    internal static string? AddAiCard(Db db, string reportId, string taskId, string title, string why, string category,
        IReadOnlyList<string> findingIds, IReadOnlyList<string> evidenceIds, string by)
    {
        if (db.Scalar("SELECT 1 FROM followups WHERE report_id = $r", ("$r", reportId)) is null) return null;
        var (revision, state) = Load(db, reportId);
        var cards = state["cards"]!.AsArray();
        var mine = cards.Count(c => c.Text("aiTaskId") == taskId);
        if (mine >= MaxAiCards) return null;
        var cardId = "card-" + Ids.Hash16(reportId, "ai", taskId, mine.ToString(System.Globalization.CultureInfo.InvariantCulture));
        cards.Add(new JsonObject
        {
            ["id"] = cardId, ["findingId"] = findingIds.Count > 0 ? findingIds[0] : null, ["category"] = category,
            ["title"] = title, ["text"] = why, ["why"] = why, ["evidenceIds"] = Json.Array(evidenceIds), ["options"] = Json.Array(["Ask about this", "Ignore"]),
            ["guide"] = null, ["preview"] = null, ["actionId"] = null, ["settingsUri"] = null,
            ["origin"] = "ai", ["findingIds"] = Json.Array(findingIds), ["addedBy"] = by,
            ["aiTaskId"] = taskId, ["ruleId"] = "", ["fingerprint"] = "", ["severity"] = "info", ["subject"] = "",
        });
        SetQueue(state, IdList(Queue(state)).Append(cardId));
        Save(db, reportId, revision, state);
        return cardId;
    }
    private static JsonObject Save(Db db, string reportId, long revision, JsonObject state)
    {
        db.Exec("UPDATE followups SET revision = $v, state = $s WHERE report_id = $r",
            ("$v", revision + 1), ("$s", Json.Write(state)), ("$r", reportId));
        return View(reportId, (revision + 1, state));
    }

    private static (long Revision, JsonObject State) Load(Db db, string reportId)
    {
        var row = db.Query("SELECT revision, state FROM followups WHERE report_id = $r", r => (r.GetInt64(0), r.GetString(1)), ("$r", reportId)).FirstOrDefault();
        if (row.Item2 is null) throw RpcError.NotFound("report", reportId);
        try
        {
            if (Json.Parse(row.Item2) is JsonObject s && s["cards"] is JsonArray && s["queue"] is JsonArray && s["decisions"] is JsonArray && s["replies"] is JsonObject)
                return (row.Item1, s);
        }
        catch (System.Text.Json.JsonException) { }
        throw RpcError.Corrupt("followup", reportId);
    }

    private static JsonArray Queue(JsonObject state) => state["queue"]!.AsArray();
    private static JsonArray Decisions(JsonObject state) => state["decisions"]!.AsArray();

    private static JsonObject Card(JsonObject state, string cardId) =>
        state["cards"]!.AsArray().FirstOrDefault(c => c!["id"]!.GetValue<string>() == cardId)?.AsObject()
        ?? throw RpcError.NotFound("card", cardId);

    private static List<string> IdList(JsonArray a) => a.Select(x => x!.GetValue<string>()).ToList();

    private static void SetQueue(JsonObject state, IEnumerable<string> ids) => state["queue"] = Json.Array(ids.ToList());

    private static void SetDecision(JsonObject state, string cardId, string status, string? via)
    {
        var existing = Decisions(state).FirstOrDefault(d => d.Text("cardId") == cardId)?.AsObject();
        if (existing is null) Decisions(state).Add(existing = new JsonObject { ["cardId"] = cardId });
        existing["status"] = status;
        if (via is null) existing.Remove("via"); else existing["via"] = via;
    }

    /// <summary>followup.respond option "intended": the same cross-scan disposition finding.setDisposition records.</summary>
    private static void Intend(Db db, JsonObject state, string reportId, string cardId)
    {
        var card = Card(state, cardId);
        if (!IdList(Queue(state)).Take(VisibleCards).Contains(cardId))
            throw RpcError.State("card_not_visible", "That card is not one of the visible cards.");
        Dispositions.Set(db, FindFinding(db, reportId, card.Text("findingId")), "intended", "finding", null);
        SetQueue(state, IdList(Queue(state)).Where(x => x != cardId));
        SetDecision(state, cardId, "intended", via: "disposition");
    }

    private static void Decide(JsonObject state, string cardId, string option)
    {
        Card(state, cardId);
        var queue = IdList(Queue(state));
        var existing = Decisions(state).FirstOrDefault(d => d.Text("cardId") == cardId)?.AsObject();

        if (option == "restore")
        {
            // A card ignored or put off ("later") in this report comes back to the front; cross-scan ones use clearDisposition.
            if (existing?["status"]?.GetValue<string>() is not ("ignored" or "later") || existing.Text("via") == "disposition")
                throw RpcError.State("not_ignored", "Only a card ignored or put off in this report can be restored here.");
            existing["status"] = "restored";
            SetQueue(state, new[] { cardId }.Concat(queue.Where(x => x != cardId)));
            return;
        }

        if (!queue.Take(VisibleCards).Contains(cardId))
            throw RpcError.State("card_not_visible", "That card is not the visible card.");

        // "later" leaves the deck for this report (decision "later", restorable); the next scan builds a fresh deck, so it returns there.
        SetDecision(state, cardId, option switch { "ignore" => "ignored", "decline" => "declined", "later" => "later", _ => "done" }, via: null);
        SetQueue(state, queue.Where(x => x != cardId));
    }

    /// <summary>Report-wide reply: pending cards in the focused categories move to the front, current order kept.</summary>
    private void Rerank(JsonObject state, string text)
    {
        var cats = fixture.FocusCategories(text);
        var queue = IdList(Queue(state));
        var cards = state["cards"]!.AsArray().Select(c => c!.AsObject()).ToList();
        var focused = cards.Any(c => c["key"] is not null)
            ? cards.Where(c => cats.Contains(c.Text("category"))).Select(c => c.Text("id")).Where(queue.Contains).ToList() // M1: catalog order
            : queue.Where(id => cats.Contains(Card(state, id).Text("category"))).ToList();
        SetQueue(state, focused.Concat(queue.Where(x => !focused.Contains(x))));
    }

    /// <summary>A card question gets the first matching fixture template (fixture cards) or "From the report: {display}".</summary>
    private void Ask(Db db, JsonObject state, string reportId, string cardId, string text)
    {
        var card = Card(state, cardId);
        var queue = IdList(Queue(state));
        if (!queue.Take(VisibleCards).Contains(cardId))
            throw RpcError.State("card_not_visible", "That card is not the visible card.");

        var key = card["key"]?.GetValue<string>();
        var evidenceIds = Json.Strings(card["evidenceIds"]);
        var report = ReportService.Load(db, reportId);
        var display = report["evidence"]!.AsArray().FirstOrDefault(e => e.Text("id") == evidenceIds.FirstOrDefault())?["display"]?.GetValue<string>() ?? "";
        var template = key is null ? "From the report: {display}"
            : fixture.AskTemplates.First(t => (t.Card == key || t.Card == "*") && t.Pattern.IsMatch(text)).Reply;
        state["replies"]![cardId] = new JsonObject
        {
            ["question"] = text,
            ["text"] = template.Replace("{display}", display, StringComparison.Ordinal),
            ["evidenceIds"] = Json.Array(evidenceIds),
        };
    }

    private static JsonObject View(string reportId, (long Revision, JsonObject State) loaded)
    {
        var (revision, state) = loaded;
        var cards = state["cards"]!.AsArray();
        var queue = IdList(Queue(state));
        JsonObject? CardView(string id)
        {
            var view = (JsonObject)Card(state, id).DeepClone();
            var sourceTaskId = view["aiTaskId"]?.GetValue<string>(); // m5 §5: the task that added an AI card (its evidence lives there)
            foreach (var field in Internal) view.Remove(field);
            view["settingsUri"] ??= null;
            // M5: every card says where it came from; an AI note (plain, untrusted text) and the label of who wrote it.
            view["origin"] ??= "report";
            view["sourceTaskId"] = view.Text("origin") == "ai" ? sourceTaskId : null;
            view["note"] ??= null;
            view["noteBy"] = view["note"] is null ? null : view["noteBy"]?.DeepClone();
            view["reply"] = state["replies"]![id]?.DeepClone();
            return view;
        }
        var card = queue.Count > 0 ? CardView(queue[0]) : null;
        // v1.4: the next ≤ 2 queued cards; respond and dispositions accept any of the three.
        var upcoming = new JsonArray(queue.Skip(1).Take(VisibleCards - 1).Select(id => (JsonNode?)CardView(id)).ToArray());
        return new JsonObject
        {
            ["reportId"] = reportId,
            ["revision"] = revision,
            ["total"] = cards.Count,
            ["pendingCount"] = queue.Count,
            ["card"] = card,
            ["upcoming"] = upcoming,
            ["decisions"] = new JsonArray(Decisions(state).Select(d => (JsonNode?)new JsonObject
            {
                ["cardId"] = d.Text("cardId"),
                // An AI card is not a finding's card: its decision has no finding, so Restore goes through followup.respond.
                ["findingId"] = Card(state, d.Text("cardId")) is var dc && dc.Text("origin") != "ai" && dc.Text("findingId") is { Length: > 0 } fid ? fid : null,
                ["status"] = d.Text("status"),
                ["title"] = Card(state, d.Text("cardId")).Text("title"),
                ["operationId"] = d!["operationId"]?.DeepClone(),
                ["undo"] = d!["undoAvailable"] is null ? null : new JsonObject { ["available"] = d["undoAvailable"]!.DeepClone() },
            }).ToArray()),
        };
    }
}

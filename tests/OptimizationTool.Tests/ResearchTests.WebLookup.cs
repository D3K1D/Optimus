using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Tests;

/// <summary>m9 §7 web_lookup (P4c), §6.2 rule 7 research in get_findings and §5.5 the web-taint mark. Fake provider only.</summary>
public sealed partial class ResearchTests
{
    internal static JsonObject WebGrant(bool web = true, bool names = false, int? lookups = null)
    {
        var g = AgentSetup.Grant([]);
        g["web"] = web;
        g["share"] = new JsonObject { ["names"] = names };
        if (lookups is not null) g["limits"] = new Limits(WebLookups: lookups.Value).ToJson();
        return g;
    }

    internal static Task<JsonObject> Lookup(TestHost host, string taskId, string findingId, string? turnId = null) =>
        host.Server.Engine.Agent.CallToolAsync(taskId, "web_lookup", new JsonObject { ["findingRef"] = findingId }, "in_app", turnId, default);

    private static async Task<bool> Tainted(Client c, string taskId)
    {
        var task = await c.Call("agent.get", new JsonObject { ["taskId"] = taskId });
        SchemaTests.AssertValid("agent-task", task);
        return task["webTainted"]?.GetValue<bool>() == true;
    }

    [Fact]
    public async Task Web_lookup_returns_the_stored_item_projected_by_names_and_marks_the_task()
    {
        await using var rig = await Start();
        var (reportId, _) = await ScanAndResearch(rig.C);
        var steam = (await FindingsByImage(rig.C, reportId))["steam.exe"].Str("id");
        var calls = rig.Probe.Calls.Count;

        // Names off: status and verdict only; the stored label answers (no new call); the task is tainted in that transaction.
        var plain = (await AgentSetup.StartTask(rig.C, reportId, grant: WebGrant())).Str("taskId");
        Assert.False(await Tainted(rig.C, plain));
        var r = await Lookup(rig.Host, plain, steam);
        SchemaTests.AssertValid("tool-result", r);
        Assert.Equal(["status", "untrusted", "verdict"], r.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal(("done", "considered_safe", AgentService.WebUntrustedNote), (r.Str("status"), r.Str("verdict"), r.Str("untrusted")));
        Assert.Equal(calls, rig.Probe.Calls.Count);
        Assert.True(await Tainted(rig.C, plain));
        var tool = rig.C.Events.Last(e => e.Str("type") == "agent.tool" && e["payload"].Str("tool") == "web_lookup")!;
        SchemaTests.AssertValid("event", tool);
        Assert.Equal("complete", tool["payload"].Str("status"));
        Assert.Equal("Steam · open port", tool["payload"].Str("label")); // UI-only: built from the subject
        Assert.DoesNotContain("Steam", r.ToJsonString());

        // Names on: summary and sources (title, domain; never an ID or URL).
        var named = (await AgentSetup.StartTask(rig.C, reportId, grant: WebGrant(names: true))).Str("taskId");
        var full = await Lookup(rig.Host, named, steam);
        SchemaTests.AssertValid("tool-result", full);
        Assert.Equal("steam.exe is a well-known program.", full.Str("summary"));
        Assert.Equal("""[{"title":"About steam.exe","domain":"docs.vendor-site.com"}]""", full["sources"]!.ToJsonString());
        Assert.DoesNotContain("src-", full.ToJsonString());
        Assert.DoesNotContain("https://", full.ToJsonString());
    }

    [Fact]
    public async Task Web_lookup_gates_grant_budget_scope_and_eligibility()
    {
        await using var rig = await Start(world: new World { ThreatFiles = ["Other.exe"] });
        var (reportId, _) = await ScanAndResearch(rig.C);
        var f = await FindingsByImage(rig.C, reportId);
        var steam = f["steam.exe"].Str("id");
        var report = await rig.C.Call("report.get", new JsonObject { ["reportId"] = reportId });
        var threat = report["findings"]!.AsArray().Single(x => x!["ruleId"]!.GetValue<string>() == "test.threat")!["id"]!.GetValue<string>();

        var noWeb = (await AgentSetup.StartTask(rig.C, reportId, grant: WebGrant(web: false))).Str("taskId");
        Assert.Equal("not_allowed", (await Lookup(rig.Host, noWeb, steam))["error"].Str("code"));
        Assert.False(await Tainted(rig.C, noWeb));
        Assert.DoesNotContain(Diagnostics.Tools(Grant.FromJson(WebGrant(web: false).Also(g => { g["limits"] = new Limits().ToJson(); g["idleExpirySeconds"] = 900; }))), t => t.Name == "web_lookup");
        Assert.Contains(Diagnostics.Tools(Grant.FromJson(WebGrant().Also(g => { g["limits"] = new Limits().ToJson(); g["idleExpirySeconds"] = 900; }))), t => t.Name == "web_lookup");

        var one = (await AgentSetup.StartTask(rig.C, reportId, grant: WebGrant(lookups: 1))).Str("taskId");
        Assert.Equal("not_allowed", (await Lookup(rig.Host, one, threat))["error"].Str("code"));     // not research-eligible (and not charged)
        Assert.Equal("invalid_arguments", (await rig.Host.Server.Engine.Agent.CallToolAsync(one, "web_lookup",
            new JsonObject { ["findingRef"] = steam, ["query"] = "anything" }, "in_app", null, default))["error"].Str("code")); // no free text
        Assert.Equal("done", (await Lookup(rig.Host, one, steam)).Str("status"));
        Assert.Equal("not_allowed", (await Lookup(rig.Host, one, steam))["error"].Str("code"));      // limits.webLookups

        var scoped = (await rig.C.Call("agent.start", new JsonObject
        {
            ["idempotencyKey"] = "s", ["goal"] = "x", ["recipientKind"] = "anthropic_api", ["grant"] = WebGrant(),
            ["scope"] = new JsonObject { ["reportId"] = reportId, ["findingIds"] = new JsonArray(f["RemoteHelper.exe"].Str("id")) },
        })).Str("taskId");
        Assert.Equal("not_allowed", (await Lookup(rig.Host, scoped, steam))["error"].Str("code"));   // out of scope
    }

    [Fact]
    public async Task Web_lookup_makes_one_fresh_call_when_nothing_is_stored_and_stores_the_same_label()
    {
        await using var rig = await Start();
        var observe = false;
        rig.Probe.Answer = (_, name, _) => Task.FromResult(observe ? ResearchProbe.Safe(name)
            : new ResearchAnswer("""{"verdict":"considered_safe","summary":"x","sources":[]}""", [], 0, new ProviderUsage(1, 1)));
        var (reportId, _) = await ScanAndResearch(rig.C);
        Assert.Empty((await Get(rig.C, reportId))["items"]!.AsArray());
        var remote = (await FindingsByImage(rig.C, reportId))["RemoteHelper.exe"].Str("id");
        var calls = rig.Probe.Calls.Count;

        observe = true;
        var task = (await AgentSetup.StartTask(rig.C, reportId, grant: WebGrant(names: true))).Str("taskId");
        var r = await Lookup(rig.Host, task, remote);
        Assert.Equal(calls + 1, rig.Probe.Calls.Count);
        Assert.Equal("worth_a_look", r.Str("verdict")); // unsigned: rule 2 capped it, as in a research run
        var item = ItemFor(await Get(rig.C, reportId), remote)!;
        Assert.Equal("worth_a_look", item.Str("verdict"));
        Assert.Equal("name_only", item.Str("basis"));
        Assert.Equal(calls + 1, rig.Probe.Calls.Count);
        Assert.Equal("done", (await Lookup(rig.Host, task, remote)).Str("status")); // now stored: no second call
        Assert.Equal(calls + 1, rig.Probe.Calls.Count);
    }

    [Fact]
    public async Task A_turn_with_a_lookup_lists_its_sources_on_the_message_only()
    {
        await using var rig = await Start();
        var (reportId, _) = await ScanAndResearch(rig.C);
        var steam = (await FindingsByImage(rig.C, reportId))["steam.exe"].Str("id");
        var n = 0;
        rig.Probe.Turn = _ => Interlocked.Increment(ref n) == 1 ? FakeProvider.Call(("web_lookup", new JsonObject { ["findingRef"] = steam })) : FakeProvider.Say("It is Steam.");
        var task = (await AgentSetup.StartTask(rig.C, reportId, grant: WebGrant(names: true))).Str("taskId");
        await AgentSetup.Ask(rig.C, task, "Is this safe?");
        var message = await AgentSetup.TaskEvent(rig.C, task, "agent.message");
        SchemaTests.AssertValid("event", message);
        var source = Assert.Single(message["payload"]!["sources"]!.AsArray())!;
        Assert.StartsWith("src-", source.Str("sourceId"));
        Assert.Equal("docs.vendor-site.com", source.Str("domain"));
        var transcript = rig.Host.Server.Engine.Store.Read(db => db.Scalar("SELECT body FROM agent_transcripts WHERE task_id = $t", ("$t", task)) as string)!;
        Assert.DoesNotContain(source.Str("sourceId"), transcript);
        Assert.True(await Tainted(rig.C, task));
    }

    [Fact]
    public async Task A_cancel_after_the_lookup_answered_but_before_it_is_stored_stores_and_releases_nothing()
    {
        await using var rig = await Start();
        var answerNow = false;
        string? task = null;
        rig.Probe.Answer = (_, name, _) =>
        {
            if (!answerNow) return Task.FromResult(new ResearchAnswer("""{"verdict":"considered_safe","summary":"x","sources":[]}""", [], 0, new ProviderUsage(1, 1)));
            // The provider has answered; agent.cancel lands now, before the lookup's store transaction (its flag is set
            // synchronously, before CancelAsync first awaits).
            _ = rig.Host.Server.Engine.Agent.CancelAsync(new JsonObject { ["taskId"] = task });
            return Task.FromResult(ResearchProbe.Safe(name));
        };
        var (reportId, _) = await ScanAndResearch(rig.C);
        var remote = (await FindingsByImage(rig.C, reportId))["RemoteHelper.exe"].Str("id");
        Assert.Null(ItemFor(await Get(rig.C, reportId), remote));
        var calls = rig.Probe.Calls.Count;

        answerNow = true;
        var n = 0;
        rig.Probe.Turn = _ => Interlocked.Increment(ref n) == 1 ? FakeProvider.Call(("web_lookup", new JsonObject { ["findingRef"] = remote })) : FakeProvider.Say("Done.");
        task = (await AgentSetup.StartTask(rig.C, reportId, grant: WebGrant(names: true))).Str("taskId");
        await AgentSetup.Ask(rig.C, task, "Is this safe?");
        var turn = await rig.C.WaitForEvent(e => e.Str("type") == "agent.turn" && e["payload"].Str("status") == "failed");
        Assert.Equal("cancelled", turn["payload"].Str("code"));

        Assert.Equal(calls + 1, rig.Probe.Calls.Count); // the provider was called and answered
        Assert.Null(ItemFor(await Get(rig.C, reportId), remote)); // nothing stored: no label, no cached product applied
        Assert.Equal(0L, rig.Host.Server.Engine.Store.Read(db => (long)db.Scalar("SELECT COUNT(*) FROM research_labels WHERE finding_id = $f", ("$f", remote))!));
        Assert.False(await Tainted(rig.C, task)); // nothing released
        Assert.DoesNotContain(rig.C.Events, e => e.Str("type") == "agent.tool" && e["payload"].Str("tool") == "web_lookup" && e["payload"].Str("status") == "complete");
        Assert.DoesNotContain(rig.C.Events, e => e.Str("type") == "agent.message" && e["payload"].Str("taskId") == task);
    }

    [Fact]
    public async Task Get_findings_carries_research_and_a_summary_only_with_names_and_taint_spreads_through_notes()
    {
        await using var rig = await Start();
        var (reportId, _) = await ScanAndResearch(rig.C);
        var steam = (await FindingsByImage(rig.C, reportId))["steam.exe"].Str("id");
        var agent = rig.Host.Server.Engine.Agent;
        JsonObject Research(JsonObject page) => page["findings"]!.AsArray().Single(x => x!["id"]!.GetValue<string>() == steam)!["research"]!.AsObject();

        var plain = (await AgentSetup.StartTask(rig.C, reportId, grant: WebGrant(web: false))).Str("taskId");
        var page = await agent.CallToolAsync(plain, "get_findings", new JsonObject { ["findingIds"] = new JsonArray(steam) }, "in_app", null, default);
        SchemaTests.AssertValid("tool-result", page);
        Assert.Equal("""{"verdict":"considered_safe"}""", Research(page).ToJsonString());
        Assert.False(await Tainted(rig.C, plain)); // a verdict alone is not web text

        var named = (await AgentSetup.StartTask(rig.C, reportId, grant: WebGrant(web: false, names: true))).Str("taskId");
        page = await agent.CallToolAsync(named, "get_findings", new JsonObject { ["findingIds"] = new JsonArray(steam) }, "in_app", null, default);
        SchemaTests.AssertValid("tool-result", page);
        Assert.Equal("steam.exe is a well-known program.", Research(page).Str("summary"));
        Assert.True(await Tainted(rig.C, named));
        Assert.True(rig.Host.Server.Engine.Store.Read(db => AgentService.IsWebTainted(db, named)));

        // The tainted task writes a note; a clean task that reads it (names on, so the note is shown) is tainted too.
        var cards = await agent.CallToolAsync(named, "get_cards", new JsonObject(), "in_app", null, default);
        var cardId = cards["cards"]![0]!.Str("cardId");
        Assert.Equal("noted", (await agent.CallToolAsync(named, "note_card", new JsonObject { ["cardId"] = cardId, ["text"] = "Looks fine." }, "in_app", null, default)).Str("status"));
        var reader = (await AgentSetup.StartTask(rig.C, reportId, grant: WebGrant(web: false, names: true))).Str("taskId");
        Assert.False(await Tainted(rig.C, reader));
        await agent.CallToolAsync(reader, "get_cards", new JsonObject(), "in_app", null, default);
        Assert.True(await Tainted(rig.C, reader));

        // A clean writer's note does not taint its readers.
        var clean = (await AgentSetup.StartTask(rig.C, reportId, grant: WebGrant(web: false, names: true))).Str("taskId");
        await agent.CallToolAsync(clean, "note_card", new JsonObject { ["cardId"] = cardId, ["text"] = "Clean note." }, "in_app", null, default);
        var reader2 = (await AgentSetup.StartTask(rig.C, reportId, grant: WebGrant(web: false, names: true))).Str("taskId");
        await agent.CallToolAsync(reader2, "get_cards", new JsonObject(), "in_app", null, default);
        Assert.False(await Tainted(rig.C, reader2));
    }
}

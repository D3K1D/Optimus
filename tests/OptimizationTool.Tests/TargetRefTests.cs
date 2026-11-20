using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Tests;

/// <summary>
/// contracts/m9-ai-powers.md §3.1: evidence-backed target refs and finding scope, for agent tasks and the desktop.
/// A fake handler records the local evidence the engine hands it; nothing touches this PC.
/// </summary>
public sealed class TargetRefTests
{
    /// <summary>Resolves only from engine-supplied evidence (or its own plan target in the lock); records what it got.</summary>
    private sealed class EvidenceHandler : IActionHandler
    {
        public readonly List<ActionRequest> Requests = [];
        public volatile bool Undoable = true;
        public int Writes;
        public string ActionId => "test.target";
        public string Version => "1.0.0";
        public string Label => "Test target";
        public string Privilege => "none";
        public bool SupportsUndo => true;
        public string Kind => "setting";
        public string Group => "test";
        public string What => "A test change on a target named by evidence.";
        public IReadOnlyList<string> Flags => [];
        public JsonObject? ParameterSchema => null;
        public bool TakesLease => false;
        public string? TargetKind => "test item";

        public Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request);
            string? id = request.Recheck || request.Undo ? request.Target?["id"]?.GetValue<string>() : request.Evidence?.EvidenceId;
            if (id is null) return Task.FromResult(ActionResolution.Ineligible(ActionService.UnknownTarget, "No evidence."));
            var on = Writes == 0;
            return Task.FromResult(new ActionResolution(true, new JsonObject { ["type"] = "test", ["id"] = id },
                new JsonObject { ["on"] = on }, new JsonObject { ["on"] = !on }, [new PlanRow("Item", on ? "On" : "Off", on ? "Off" : "On")], [], [], null,
                Resources: ["test:" + id], UndoSupported: Undoable));
        }

        public Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct) => Task.FromResult(new JsonObject { ["on"] = Writes % 2 == 0 });

        public Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct)
        {
            Interlocked.Increment(ref Writes);
            return Task.FromResult(new ActionOutcome("verified", new JsonObject { ["on"] = Writes % 2 == 0 }, "Done, read back."));
        }

        public ActionRequest? UndoRequest(JsonObject target, JsonObject before) => new(ActionId, target, null, null);
    }

    private static async Task<(TestHost Host, Client C, EvidenceHandler H, JsonObject Report)> Start()
    {
        var h = new EvidenceHandler();
        var host = TestHost.StartActions([new FakeActionModule(h, new FakeSchemeHandler(new FakePc()))],
            FakeAudit.Modules(new FakeWorld(), new CollectorOptions(), sampler: false), agent: AgentSetup.Options(null, new FakeProvider("anthropic_api")));
        var c = await host.ConnectAsync(minor: 3);
        var reportId = await AgentSetup.Report(c);
        await AgentSetup.SetKey(c);
        var report = await c.Call("report.get", new JsonObject { ["reportId"] = reportId });
        return (host, c, h, report);
    }

    [Fact]
    public async Task Catalog_ExposesEachHandlersTargetKind_NullWhenTheActionTakesNoTarget()
    {
        var (host, c, _, _) = await Start();
        await using var _h = host;
        var rows = (await c.Call("action.catalog"))["actions"]!.AsArray().Select(r => r!.AsObject()).ToList();
        Assert.Equal("test item", rows.Single(r => r.Str("actionId") == "test.target").Str("targetKind"));
        foreach (var id in new[] { "power.select_scheme", ActionService.ManualOpen })
        {
            var row = rows.Single(r => r.Str("actionId") == id);
            Assert.True(row.ContainsKey("targetKind")); // always present, so the UI need not guess
            Assert.Null(row["targetKind"]);
        }
    }

    private static JsonObject ChangesGrant()
    {
        var g = AgentSetup.Grant([]);
        g["canRequestChanges"] = true;
        return g;
    }

    private static JsonObject Ask(string? evidenceId, string? findingRef = null)
    {
        var a = new JsonObject { ["actionId"] = "test.target" };
        if (evidenceId is not null) a["targetRef"] = new JsonObject { ["evidenceId"] = evidenceId };
        if (findingRef is not null) a["findingRef"] = findingRef;
        return a;
    }

    private static (JsonObject Finding, string Cited, string Uncited) Pick(JsonObject report)
    {
        var findings = report["findings"]!.AsArray().Select(f => f!.AsObject()).ToList();
        var finding = findings.First(f => f["evidenceIds"]!.AsArray().Count > 0);
        var cited = findings.SelectMany(f => f["evidenceIds"]!.AsArray().Concat(f["counterevidence"]?.AsArray() ?? [])).Select(n => n!.GetValue<string>()).ToHashSet();
        var uncited = report["evidence"]!.AsArray().Select(e => e.Str("id")).First(id => !cited.Contains(id));
        return (finding, finding["evidenceIds"]![0]!.GetValue<string>(), uncited);
    }

    private static async Task<JsonObject> Request(AgentService agent, string taskId, JsonObject args) =>
        await agent.CallToolAsync(taskId, "request_action", args, "in_app", null, default);

    private static async Task Decline(Client c, JsonObject requested)
    {
        if (requested["planId"] is not null && requested.Str("status") == "awaiting_user")
            await c.Call("action.decline", new JsonObject { ["planId"] = requested.Str("planId") });
    }

    [Fact]
    public async Task Agent_TargetRef_NeedsReleasedOrCitedEvidence_AndTheHandlerGetsTheLocalItem()
    {
        var (host, c, h, report) = await Start();
        await using var _ = host;
        var agent = host.Server.Engine.Agent;
        var (finding, cited, uncited) = Pick(report);
        var taskId = (await AgentSetup.StartTask(c, report.Str("reportId"), grant: ChangesGrant())).Str("taskId");

        // Not released yet and no finding named: unknown_target, and no handler saw it.
        var refused = await Request(agent, taskId, Ask(cited));
        Assert.Equal("ineligible", refused.Str("status"));
        Assert.Equal(ActionService.UnknownTarget, refused["summary"]!["ineligible"].Str("code"));
        Assert.Empty(h.Requests);

        // Cited by the in-scope finding the call names: resolved to the stored, unprojected item.
        var byFinding = await Request(agent, taskId, Ask(cited, finding.Str("id")));
        Assert.Equal("awaiting_user", byFinding.Str("status"));
        var got = Assert.Single(h.Requests).Evidence!;
        Assert.Equal(cited, got.EvidenceId);
        var stored = report["evidence"]!.AsArray().Single(e => e.Str("id") == cited)!;
        Assert.Equal(stored.Str("collectorId"), got.CollectorId);
        Assert.True(JsonNode.DeepEquals(stored["value"], got.Value)); // the local value, not the projected copy
        var plan = await c.Call("action.get", new JsonObject { ["planId"] = byFinding.Str("planId") });
        Assert.Equal(cited, plan["targetRef"].Str("evidenceId"));
        Assert.Null(plan["target"]!["evidence"]);
        Assert.Null(byFinding["target"]); // the AI's summary carries no target details
        await Decline(c, byFinding);

        // Uncited evidence stays unknown even with a finding named (that finding doesn't cite it).
        Assert.Equal(ActionService.UnknownTarget, (await Request(agent, taskId, Ask(uncited, finding.Str("id"))))["summary"]!["ineligible"].Str("code"));

        // Aliases and made-up IDs are not targets.
        foreach (var alias in new[] { "App A", "ev-0000000000000000", "Process B" })
            Assert.Equal(ActionService.UnknownTarget, (await Request(agent, taskId, Ask(alias)))["summary"]!["ineligible"].Str("code"));
        Assert.Single(h.Requests);

        // Released by get_findings: then it resolves without a finding named.
        var page = await agent.CallToolAsync(taskId, "get_findings", new JsonObject(), "in_app", null, default);
        Assert.Contains(cited, page.ToJsonString());
        var released = await Request(agent, taskId, Ask(cited));
        Assert.Equal("awaiting_user", released.Str("status"));
        Assert.Equal(cited, h.Requests[^1].Evidence!.EvidenceId);
        Assert.Equal(0, h.Writes);
    }

    [Fact]
    public async Task Agent_TargetRef_WrongShapes_AreInvalid_AndAFindingOutsideTheScope_IsNotAllowed()
    {
        var (host, c, h, report) = await Start();
        await using var _ = host;
        var agent = host.Server.Engine.Agent;
        var (finding, cited, _) = Pick(report);
        var taskId = (await AgentSetup.StartTask(c, report.Str("reportId"), grant: ChangesGrant())).Str("taskId");

        foreach (var bad in new JsonNode[]
        {
            new JsonObject { ["alias"] = "App A" }, new JsonObject { ["evidenceId"] = cited, ["path"] = @"C:\x.exe" },
            new JsonObject { ["evidenceId"] = 7 }, new JsonObject { ["pid"] = 1234 }, "ev-1", new JsonObject { ["evidenceId"] = new string('a', 65) },
            new JsonObject { ["evidenceId"] = "ev-\u0000" },
        })
        {
            var r = await Request(agent, taskId, new JsonObject { ["actionId"] = "test.target", ["targetRef"] = bad.DeepClone() });
            Assert.Equal("invalid_arguments", r["error"].Str("code"));
        }

        Assert.Equal("not_allowed", (await Request(agent, taskId, Ask(cited, "fnd-not-in-this-report")))["error"].Str("code"));

        // A task scoped to other findings may not name this one.
        var other = report["findings"]!.AsArray().Select(f => f.Str("id")).FirstOrDefault(id => id != finding.Str("id"));
        var scoped = await c.Call("agent.start", new JsonObject
        {
            ["idempotencyKey"] = Guid.NewGuid().ToString(), ["goal"] = "Scoped", ["recipientKind"] = "anthropic_api",
            ["scope"] = new JsonObject { ["reportId"] = report.Str("reportId"), ["findingIds"] = other is null ? new JsonArray() : new JsonArray(other) },
            ["grant"] = ChangesGrant(),
        });
        Assert.Equal("not_allowed", (await Request(agent, scoped.Str("taskId"), Ask(cited, finding.Str("id"))))["error"].Str("code"));
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Agent_AmbiguousEvidence_IsUnknownTarget()
    {
        var (host, c, h, report) = await Start();
        await using var _ = host;
        var agent = host.Server.Engine.Agent;
        var (finding, cited, _) = Pick(report);
        var taskId = (await AgentSetup.StartTask(c, report.Str("reportId"), grant: ChangesGrant())).Str("taskId");
        // The same ID stored twice for this task (its own diagnostic evidence and its report): never guessed.
        var body = report["evidence"]!.AsArray().Single(e => e.Str("id") == cited)!.ToJsonString();
        host.Server.Engine.Store.Write(db => db.Exec("INSERT INTO agent_evidence (evidence_id, task_id, collector_id, subject, body) VALUES ($i, $t, 'x', 'y', $b)",
            ("$i", cited), ("$t", taskId), ("$b", body)));
        Assert.Equal(ActionService.UnknownTarget, (await Request(agent, taskId, Ask(cited, finding.Str("id"))))["summary"]!["ineligible"].Str("code"));
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Desktop_TargetRef_MustBelongToTheNamedFindingOrReport()
    {
        var (host, c, h, report) = await Start();
        await using var _ = host;
        var (finding, cited, uncited) = Pick(report);
        Task<JsonObject> Preview(string evidenceId, string? findingId = null, string? reportId = null)
        {
            var p = new JsonObject
            {
                ["idempotencyKey"] = Guid.NewGuid().ToString(), ["actionId"] = "test.target", ["targetRef"] = new JsonObject { ["evidenceId"] = evidenceId },
            };
            if (findingId is not null) p["findingId"] = findingId;
            if (reportId is not null) p["reportId"] = reportId;
            return c.Call("action.preview", p);
        }

        var ok = await Preview(cited, finding.Str("id"));
        Assert.Equal("awaiting_user", ok.Str("status"));
        Assert.Equal(cited, h.Requests[^1].Evidence!.EvidenceId);
        Assert.Equal("awaiting_user", (await Preview(uncited, reportId: report.Str("reportId"))).Str("status"));
        Assert.Equal(uncited, h.Requests[^1].Evidence!.EvidenceId);
        var count = h.Requests.Count;

        Assert.Equal(ActionService.UnknownTarget, (await Preview(uncited, finding.Str("id")))["ineligible"].Str("code")); // not cited by it
        Assert.Equal(ActionService.UnknownTarget, (await Preview(cited))["ineligible"].Str("code"));                     // nowhere named
        Assert.Equal(ActionService.UnknownTarget, (await Preview(cited, reportId: "report-0000000000000000"))["ineligible"].Str("code"));
        Assert.Equal(ActionService.UnknownTarget, (await Preview("App A", reportId: report.Str("reportId")))["ineligible"].Str("code"));
        Assert.Equal(count, h.Requests.Count);

        // A targetRef is only ever {evidenceId}, and never together with a raw target.
        await c.Fails("action.preview", new JsonObject
        {
            ["idempotencyKey"] = Guid.NewGuid().ToString(), ["actionId"] = "test.target", ["targetRef"] = new JsonObject { ["name"] = "App A" },
        });
        await c.Fails("action.preview", new JsonObject
        {
            ["idempotencyKey"] = Guid.NewGuid().ToString(), ["actionId"] = "test.target", ["targetRef"] = new JsonObject { ["evidenceId"] = cited },
            ["target"] = new JsonObject { ["id"] = "x" }, ["findingId"] = finding.Str("id"),
        });
        // A raw target alone reaches the handler without evidence (a handler like startup.disable then refuses it).
        var raw = await c.Call("action.preview", new JsonObject
        {
            ["idempotencyKey"] = Guid.NewGuid().ToString(), ["actionId"] = "test.target", ["target"] = new JsonObject { ["id"] = cited },
        });
        Assert.Equal(ActionService.UnknownTarget, raw["ineligible"].Str("code"));
        Assert.Null(h.Requests[^1].Evidence);
        Assert.False(h.Requests[^1].Recheck);
    }

    [Fact]
    public async Task Recheck_IsEngineSet_AndAWithdrawnUndo_IsNeverOffered()
    {
        var (host, c, h, report) = await Start();
        await using var _ = host;
        var (finding, cited, _) = Pick(report);
        h.Undoable = false;
        var plan = await c.Call("action.preview", new JsonObject
        {
            ["idempotencyKey"] = Guid.NewGuid().ToString(), ["actionId"] = "test.target", ["targetRef"] = new JsonObject { ["evidenceId"] = cited },
            ["findingId"] = finding.Str("id"),
        });
        Assert.False((bool)plan["undo"]!["supported"]!);
        var op = await Act.Approve(c, plan);
        Assert.Equal("verified", op.Str("state"));
        Assert.True(h.Requests[^1].Recheck);       // the lock recheck got the plan's own target
        Assert.Null(h.Requests[^1].Evidence);
        Assert.False((bool)op["undo"]!["available"]!);
        var undo = await c.Call("action.undo", new JsonObject { ["idempotencyKey"] = Guid.NewGuid().ToString(), ["operationId"] = op.Str("operationId") });
        Assert.Equal("not_supported", undo["ineligible"].Str("code"));
        Assert.Equal(1, h.Writes);
    }
}

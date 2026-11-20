using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Host.Elevation;
using OptimizationTool.Transport;

namespace OptimizationTool.Tests;

/// <summary>
/// contracts/m9-ai-powers.md §17.2 / §17.6 administrator checks on the host: the admin tier, requests (one per turn, two per
/// task, revision and frozen membership, expiry, declined_before, a paired window joining the open request), checks.approve /
/// checks.decline, the rechecks before start and before release, the cooldown, intake to evidence (coverage kept, malformed
/// stores nothing, interrupted keeps finished checks), the one continuation and card.fix. The helper is a fake
/// (<see cref="FakeReads"/>): nothing here launches it, triggers UAC, reads anything elevated or calls an AI.
/// </summary>
public sealed class AdminChecksTests
{
    private const string Defender = AdminChecks.DefenderHistory, Disks = AdminChecks.StorageHealth, BitLocker = AdminChecks.Encryption;
    private static readonly CallContext Bridge = new("bridge-checks", "agent", 7373);

    /// <summary>The helper, faked: records each session; <see cref="Script"/> answers it (default: every check ok with <see cref="Items"/>).</summary>
    public sealed class FakeReads : IElevatedReads
    {
        public readonly List<ElevatedReadRequest> Requests = [];
        public readonly Dictionary<string, List<JsonObject>> Items = [];
        public Func<ElevatedReadRequest, CancellationToken, Task<ElevatedReadResult>>? Script;
        public readonly List<ElevatedReadRequest> Invalid = [];
        public CancellationToken LastStop;

        /// <summary>Stands for the one-helper exclusion held by a write or a script: a read waits here before its admission.</summary>
        public Task? Exclusion;
        public int Waiting;

        public async Task<ElevatedReadResult> RunReadAsync(ElevatedReadRequest request, CancellationToken stop)
        {
            if (!BrokerElevation.ValidRead(request)) // the production shape check: such a request never reaches the helper
            {
                lock (Invalid) Invalid.Add(request);
                lock (Requests) Requests.Add(request);
                return new ElevatedReadResult("not_started", "bad_request", [], false);
            }
            if (Exclusion is { } held)
            {
                Interlocked.Increment(ref Waiting);
                await held;
            }
            // As BrokerElevation: the engine's admission right before the launch, then a stop that came meanwhile.
            if (request.Admit is { } admit && !admit()) return new ElevatedReadResult("not_started", "not_admitted", [], false);
            if (stop.IsCancellationRequested) return new ElevatedReadResult("stopped", "stopped", [], false);
            lock (Requests) Requests.Add(request); // launched: it reached the helper
            LastStop = stop;
            return await (Script?.Invoke(request, stop) ?? Task.FromResult(AllOk(request)));
        }

        public ElevatedReadResult AllOk(ElevatedReadRequest request) => new("complete", null,
            [.. request.Checks.Select(c => Ok(c.N, Items.TryGetValue(c.Id, out var items) ? items : []))], true);
    }

    public static ElevatedCheckResult Ok(int n, IReadOnlyList<JsonObject> items, bool complete = true, int? total = null) =>
        new(n, "ok", null, new ElevatedReadCoverage(complete, total ?? items.Count, items.Count), items);

    public static JsonObject Detection(string name = "Trojan:Win32/Wacatac.B!ml") => new()
    {
        ["threatName"] = name, ["category"] = "trojan", ["severity"] = "severe", ["detectedAt"] = "2026-11-01T10:00Z", ["action"] = "quarantine",
        ["succeeded"] = true, ["foundBy"] = "real_time", ["affectedItems"] = 2,
    };

    public static JsonObject Volume() => new()
    {
        ["drive"] = "C:", ["protection"] = "on", ["conversion"] = "fully_encrypted", ["percent"] = 100, ["method"] = "xts_aes_128",
        ["protectorTypes"] = new JsonArray("tpm", "recovery_password"),
    };

    private sealed record Rig(TestHost Host, Client C, string ReportId, FakeReads Reads, FakeProvider Provider, SemaphoreSlim Release, FakeCli Cli) : IAsyncDisposable
    {
        public AgentService Agent => Host.Server.Engine.Agent;
        public Store Store => Host.Server.Engine.Store;
        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            Assert.Empty(Reads.Invalid); // every session sent passed the host's own request-shape rules
        }
    }

    private static FakeProvider Gated(SemaphoreSlim release) =>
        new("anthropic_api", [.. Enumerable.Range(0, 40).Select(_ => (Func<ProviderRequest, CancellationToken, Task<ProviderTurn>>)(async (_, ct) =>
        {
            await release.WaitAsync(ct);
            return FakeProvider.Say("Done.");
        }))]);

    private static async Task<Rig> Start(bool flag = true, bool reads = true, TimeProvider? time = null)
    {
        var pc = new FakeSettings();
        pc.Set("a", "off");
        var release = new SemaphoreSlim(0);
        var provider = Gated(release);
        var cli = new FakeCli();
        var fake = new FakeReads();
        var o = AgentSetup.Options(null, provider);
        var agent = new AgentOptions
        {
            Providers = o.Providers, Credentials = o.Credentials, RetryDelay = o.RetryDelay, McpExecutablePath = o.McpExecutablePath, CliRunner = cli,
            ElevatedReads = reads ? fake : null, LocalIdentities = () => [new LocalIdentity("SAMUEL")],
        };
        var host = TestHost.StartActions([new FakeActionModule(new FakeSettingHandler(pc))], FakeAudit.Modules(new FakeWorld(), new CollectorOptions(), sampler: false),
            elevation: new FakeElevation(), agent: agent, enableAdmin: flag, time: time);
        var c = await host.ConnectAsync(minor: 5);
        var reportId = await AgentSetup.Report(c);
        await AgentSetup.SetKey(c);
        return new Rig(host, c, reportId, fake, provider, release, cli);
    }

    private static JsonObject AdminGrant(bool admin = true, JsonObject? limits = null)
    {
        var g = AgentSetup.Grant([Defender, Disks, BitLocker, "power.inventory"], limits);
        g["admin"] = admin;
        g["changes"] = "fix";
        return g;
    }

    private static async Task<string> NewTask(Rig r, JsonObject? grant = null) => (await AgentSetup.StartTask(r.C, r.ReportId, grant: grant ?? AdminGrant())).Str("taskId");

    private static Task<JsonObject> Check(Rig r, string taskId, string id, string? turnId, int? window = null)
    {
        var args = new JsonObject { ["diagnosticId"] = id };
        if (window is not null) args["window"] = window;
        return r.Agent.CallToolAsync(taskId, "run_diagnostic", args, "in_app", turnId, default);
    }

    private static Task<JsonObject> GetOp(Rig r, string taskId, string operationId) =>
        r.Agent.CallToolAsync(taskId, "get_operation", new JsonObject { ["operationId"] = operationId }, "in_app", null, default);

    private static string RequestOf(Rig r, string operationId) =>
        (string)r.Store.Read(db => db.Scalar("SELECT request_id FROM agent_operations WHERE operation_id = $o", ("$o", operationId)))!;

    private static JsonObject LastChecks(Rig r, string requestId) =>
        r.C.Events.Last(e => e.Str("type") == AgentService.ChecksEvent && e["payload"].Str("requestId") == requestId)["payload"]!.AsObject();

    /// <summary>
    /// Waits for the request's agent.checks view (at <paramref name="revision"/> or in <paramref name="state"/> when given). A tool
    /// call here runs in process, but the event reaches the client over the socket, so it may arrive after the call returns.
    /// </summary>
    private static async Task<JsonObject> ChecksView(Rig r, string requestId, long? revision = null, string? state = null) =>
        (await r.C.WaitForEvent(e => e.Str("type") == AgentService.ChecksEvent && e["payload"].Str("requestId") == requestId
            && (revision is null || (long)e["payload"]!["revision"]! == revision) && (state is null || e["payload"].Str("state") == state)))["payload"]!.AsObject();

    private static Task<JsonObject> Approve(Rig r, string requestId, long revision) =>
        r.C.Call("checks.approve", new JsonObject { ["idempotencyKey"] = Guid.NewGuid().ToString(), ["requestId"] = requestId, ["revision"] = revision });

    private static Task<JsonObject> WaitChecks(Rig r, string requestId, string state) =>
        r.C.WaitForEvent(e => e.Str("type") == AgentService.ChecksEvent && e["payload"].Str("requestId") == requestId && e["payload"].Str("state") == state);

    private static async Task Until(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met.");
            await Task.Delay(20);
        }
    }

    /// <summary>Starts a turn (the gated provider holds it) and returns its id.</summary>
    private static async Task<string> Turn(Rig r, string taskId, string text = "Look deeper?") => (await AgentSetup.Ask(r.C, taskId, text)).Str("turnId");

    /// <summary>Lets the running turn answer and waits for its message.</summary>
    private static async Task EndTurn(Rig r, string taskId, string turnId)
    {
        r.Release.Release();
        await AgentSetup.TaskEvent(r.C, taskId, "agent.message", p => p.Str("turnId") == turnId);
        await Until(() => !r.Agent.TurnAlive(taskId, turnId));
    }

    private static async Task SetAdmin(Rig r, bool admin)
    {
        var record = (await r.C.Call("ai.getPermissions"))["permissions"]!.DeepClone().AsObject();
        record["do"]!["admin"] = admin;
        await r.C.Call("ai.setPermissions", new JsonObject { ["idempotencyKey"] = Guid.NewGuid().ToString(), ["revision"] = (long)record["revision"]!, ["permissions"] = record });
    }

    private static long Used(Rig r, string taskId, string key) => r.Store.Read(db => AgentService.LoadTask(db, taskId))["used"]![key]!.GetValue<long>();

    // ---------------------------------------------------------------- tier and availability

    [Fact]
    public async Task AdminTier_IsListedOnlyWithTheFlagAndAReadPath_AndNeedsAdminRights()
    {
        await using (var r = await Start())
        {
            var list = await r.C.Call("provider.list");
            SchemaTests.AssertValid("provider-list", list);
            var tiers = list["diagnostics"]!.AsArray().ToDictionary(d => d.Str("id"), d => d.Str("tier"));
            Assert.Equal(("admin", "admin", "admin"), (tiers[Defender], tiers[Disks], tiers[BitLocker]));
            var window = list["diagnostics"]!.AsArray().Single(d => d.Str("id") == Defender)!["arguments"]!["properties"]!["window"]!;
            Assert.Equal((1, 90, 30), ((int)window["minimum"]!, (int)window["maximum"]!, (int)window["default"]!));

            // Administrator rights off: the ids are not offered to the model and a call is refused admin_not_allowed.
            var off = await NewTask(r, AdminGrant(admin: false));
            var refused = await Check(r, off, Defender, null);
            SchemaTests.AssertValid("tool-result", refused);
            Assert.Equal(("admin_not_allowed", ActionService.AdminNotAllowedMessage), (refused["error"].Str("code"), refused["error"].Str("message")));
            var enumIds = Diagnostics.Tools(Grant.FromJson(r.Store.Read(db => AgentService.LoadTask(db, off))["grant"]!))
                .Single(t => t.Name == "run_diagnostic").InputSchema["properties"]!["diagnosticId"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>());
            Assert.DoesNotContain(Defender, enumIds);
            var caps = await r.Agent.CallToolAsync(off, "list_capabilities", null, "in_app", null, default);
            SchemaTests.AssertValid("tool-result", caps);
        }
        foreach (var (flag, reads) in new[] { (false, true), (true, false) })
        {
            await using var r = await Start(flag, reads);
            Assert.DoesNotContain((await r.C.Call("provider.list"))["diagnostics"]!.AsArray(), d => d.Str("tier") == "admin");
            var taskId = await NewTask(r);
            var refused = await Check(r, taskId, Defender, null);
            Assert.Equal("admin_unavailable", refused["error"].Str("code")); // flag off wins over permission on
            Assert.Equal(0, r.Store.Read(db => (long)db.Scalar("SELECT COUNT(*) FROM check_requests")!));
        }
    }

    [Fact]
    public async Task Window_IsValidatedBeforeAnythingIsStored_AndNamesTheLabel_OneEntryPerCheck()
    {
        await using var r = await Start();
        var taskId = await NewTask(r);
        foreach (var bad in new[] { 0, 91 })
            Assert.Equal("invalid_arguments", (await Check(r, taskId, Defender, null, bad))["error"].Str("code"));
        Assert.Equal("invalid_arguments", (await Check(r, taskId, Disks, null, 7))["error"].Str("code")); // no window
        Assert.Equal(0, r.Store.Read(db => (long)db.Scalar("SELECT COUNT(*) FROM check_requests")!));

        var one = await Check(r, taskId, Defender, null, 1);
        var requestId = RequestOf(r, one.Str("operationId"));
        var charged = Used(r, taskId, "diagnosticCalls");
        Assert.Equal("Defender detection history · last day", (await ChecksView(r, requestId, revision: 1))["checks"]![0].Str("label"));
        Assert.Equal(one.Str("operationId"), (await Check(r, taskId, Defender, null, 1)).Str("operationId")); // the same check and window: the same, uncharged
        var other = await Check(r, taskId, Defender, null); // the default, 30 days: the same check with another window
        SchemaTests.AssertValid("tool-result", other);
        Assert.Equal(("not_allowed", AgentService.OtherWindowMessage), (other["error"].Str("code"), other["error"].Str("message")));
        Assert.Equal((1L, charged), (r.Store.Read(db => (long)db.Scalar("SELECT COUNT(*) FROM agent_operations WHERE request_id IS NOT NULL")!), Used(r, taskId, "diagnosticCalls")));

        var later = await NewTask(r); // another conversation: the default window is labelled
        var dflt = await Check(r, later, Defender, null);
        Assert.Equal("Defender detection history · last 30 days", (await ChecksView(r, RequestOf(r, dflt.Str("operationId")), revision: 1))["checks"]![0].Str("label"));
    }

    // ---------------------------------------------------------------- requests

    [Fact]
    public async Task OneTurnsCalls_FormOneRequest_ALaterTurnANew_TwoPerTask_SameCheckUncharged()
    {
        await using var r = await Start();
        var taskId = await NewTask(r);
        var turn = await Turn(r, taskId);
        var a = await Check(r, taskId, Defender, turn);
        SchemaTests.AssertValid("tool-result", a);
        SchemaTests.AssertValidRef("checks.schema.json#/$defs/toolWaiting", a);
        Assert.Equal("awaiting_user", a.Str("status"));
        var b = await Check(r, taskId, BitLocker, turn);
        var requestId = RequestOf(r, a.Str("operationId"));
        Assert.Equal(requestId, RequestOf(r, b.Str("operationId")));
        var view = await ChecksView(r, requestId, revision: 2);
        SchemaTests.AssertValid("event", r.C.Events.Last(e => e.Str("type") == AgentService.ChecksEvent));
        SchemaTests.AssertValid("checks", view);
        Assert.Equal((2, "awaiting_user", 2), ((int)view["revision"]!, view.Str("state"), view["checks"]!.AsArray().Count));
        Assert.Equal(2, Used(r, taskId, "diagnosticCalls")); // they count towards the diagnostic cap
        var again = await Check(r, taskId, Defender, turn);
        Assert.Equal((a.Str("operationId"), 2L), (again.Str("operationId"), Used(r, taskId, "diagnosticCalls"))); // the same, uncharged
        var pending = await GetOp(r, taskId, a.Str("operationId"));
        SchemaTests.AssertValid("tool-result", pending);
        Assert.Equal("awaiting_user", pending.Str("status"));
        await EndTurn(r, taskId, turn);

        var turn2 = await Turn(r, taskId, "And the disks?");
        var c = await Check(r, taskId, Disks, turn2);
        var second = RequestOf(r, c.Str("operationId"));
        Assert.NotEqual(requestId, second);
        await EndTurn(r, taskId, turn2);
        var turn3 = await Turn(r, taskId, "More?");
        var third = await Check(r, taskId, Disks, turn3, null);
        Assert.Equal(c.Str("operationId"), third.Str("operationId")); // the same check already waiting
        // The first request ends (expired), so Defender with another window would need a third request.
        r.Store.Write(db => db.Exec("UPDATE check_requests SET expires_at = '2020-01-01T00:00:00.000Z' WHERE request_id = $r", ("$r", requestId)));
        var refused = await r.Agent.CallToolAsync(taskId, "run_diagnostic", new JsonObject { ["diagnosticId"] = Defender, ["window"] = 7 }, "in_app", turn3, default);
        Assert.Equal(("not_allowed", AgentService.CheckRequestsUsed), (refused["error"].Str("code"), refused["error"].Str("message")));
        await EndTurn(r, taskId, turn3);
    }

    [Fact]
    public async Task Approve_StaleRevisionIsRefused_MembershipFreezes_ResultsBecomeEvidence_WithCoverage()
    {
        await using var r = await Start();
        r.Reads.Items[Defender] = [Detection(), Detection("Worm:Win32/Mydoom.O@mm")];
        var taskId = await NewTask(r);
        var turn = await Turn(r, taskId);
        var a = await Check(r, taskId, Defender, turn);
        var requestId = RequestOf(r, a.Str("operationId"));
        var b = await Check(r, taskId, BitLocker, turn); // revision 2

        var stale = await r.C.Fails("checks.approve", new JsonObject { ["idempotencyKey"] = "k1", ["requestId"] = requestId, ["revision"] = 1 });
        Assert.Equal((ErrorCodes.StaleRevision, 2), (stale.Code, (int)stale.ErrorData!["current"]!["revision"]!));
        SchemaTests.AssertValidRef("checks.schema.json#/$defs/approveParams", new JsonObject { ["idempotencyKey"] = "k1", ["requestId"] = requestId, ["revision"] = 2 });

        r.Reads.Script = async (req, stop) => { await Task.Delay(200, CancellationToken.None); return r.Reads.AllOk(req); };
        var approved = await Approve(r, requestId, 2);
        SchemaTests.AssertValid("checks", approved);
        Assert.Equal("running", approved.Str("state"));
        // Frozen: a later call of the same turn does not join it.
        var late = await Check(r, taskId, Disks, turn);
        Assert.NotEqual(requestId, RequestOf(r, late.Str("operationId")));
        await WaitChecks(r, requestId, "complete");
        var session = r.Reads.Requests.Single();
        Assert.Equal([(1, Defender, "1", (int?)30), (2, BitLocker, "1", null)], session.Checks.Select(c => (c.N, c.Id, c.HandlerVersion, c.WindowDays)));

        var op = await GetOp(r, taskId, a.Str("operationId"));
        SchemaTests.AssertValid("tool-result", op);
        Assert.Equal("complete", op.Str("status"));
        var result = op["result"]!;
        Assert.Equal((true, 2, 2), ((bool)result["coverage"]!["complete"]!, (int)result["coverage"]!["total"]!, (int)result["coverage"]!["returned"]!));
        var evidence = result["evidence"]!.AsArray();
        Assert.Equal(3, evidence.Count); // the summary first, then each detection
        Assert.Equal($"{Defender}.summary", evidence[0].Str("subject"));
        Assert.Equal("Read in full: 2 found.", evidence[0].Str("display"));
        Assert.All(evidence, e => Assert.Matches("^ev-[0-9a-f]{16}$", e.Str("id")));
        Assert.Equal("Threat A", evidence[1]!["value"].Str("threatName")); // names off: an alias
        Assert.DoesNotContain("Wacatac", op.ToJsonString());
        var stored = r.Store.Read(db => db.Query("SELECT body FROM agent_evidence WHERE task_id = $t AND collector_id = $c", x => (JsonObject)Engine.Json.Parse(x.GetString(0))!, ("$t", taskId), ("$c", "admin." + Defender)));
        Assert.All(stored, s => Assert.Equal("admin", s.Str("source")));
        Assert.Equal("ev-" + Ids.Hash16(taskId, requestId, Defender, $"{Defender}.detection.1"), stored[1].Str("id"));

        // Nothing found (complete, total 0) differs from could not read all of it.
        var bit = await GetOp(r, taskId, b.Str("operationId"));
        Assert.Equal((true, 0), ((bool)bit["result"]!["coverage"]!["complete"]!, (int)bit["result"]!["coverage"]!["total"]!));
        Assert.Equal("Read in full: nothing found.", bit["result"]!["evidence"]![0].Str("display"));
        Assert.True(Used(r, taskId, "bytesShared") > 0);
        await EndTurn(r, taskId, turn);
    }

    [Fact]
    public async Task ACompletedSession_WithChecksLeftUnread_KeepsEachChecksOwnState()
    {
        await using var r = await Start();
        var taskId = await NewTask(r);
        var a = await Check(r, taskId, Defender, null);
        var b = await Check(r, taskId, Disks, null);
        var c = await Check(r, taskId, BitLocker, null);
        var requestId = RequestOf(r, a.Str("operationId"));
        var none = new ElevatedReadCoverage(false, 0, 0);
        r.Reads.Script = (req, _) => Task.FromResult(new ElevatedReadResult("complete", null,
            [Ok(1, [Detection()]), new ElevatedCheckResult(2, "unsupported", null, none, []), new ElevatedCheckResult(3, "failed", "access_denied", none, [])], true));
        await Approve(r, requestId, 3);
        var view = (await WaitChecks(r, requestId, "complete"))["payload"]!;
        Assert.Equal(["complete", "failed", "failed"], view["checks"]!.AsArray().Select(x => x.Str("state")));
        var ops = new List<JsonObject>();
        foreach (var x in new[] { a, b, c }) ops.Add(await GetOp(r, taskId, x.Str("operationId")));
        Assert.Equal([("complete", (string?)null), ("failed", "unsupported"), ("failed", "access_denied")],
            ops.Select(o => (o.Str("status"), o["code"]?.GetValue<string>())));
    }

    [Fact]
    public async Task IncompleteCoverage_IsKept_ForTheAiAndTheUser()
    {
        await using var r = await Start();
        var taskId = await NewTask(r);
        var a = await Check(r, taskId, Defender, null);
        var requestId = RequestOf(r, a.Str("operationId"));
        r.Reads.Script = (req, _) => Task.FromResult(new ElevatedReadResult("complete", null, [Ok(1, [Detection()], complete: false, total: 9)], true));
        await Approve(r, requestId, 1);
        await WaitChecks(r, requestId, "complete");
        var op = await GetOp(r, taskId, a.Str("operationId"));
        Assert.Equal(("partial", false, 9), (op["result"].Str("status"), (bool)op["result"]!["coverage"]!["complete"]!, (int)op["result"]!["coverage"]!["total"]!));
        Assert.StartsWith("Could not read all of it", op["result"]!["evidence"]![0].Str("display"));
    }

    [Fact]
    public async Task PairedWindow_JoinsTheTasksOpenRequest_UntilApproved_AndPollsWithoutContinuation()
    {
        await using var r = await Start();
        var engine = r.Host.Server.Engine;
        var pair = await engine.InvokeAsync("agent.pair", new JsonObject { ["client"] = "claude_code" }, Bridge);
        await r.C.Call("agent.approvePairing", new JsonObject { ["pairingId"] = pair.Str("pairingId"), ["approve"] = true });
        var taskId = (await AgentSetup.StartTask(r.C, r.ReportId, "claude_code_mcp", AdminGrant(), pair.Str("pairingId"))).Str("taskId");
        async Task<JsonObject> Paired(string tool, JsonObject args) =>
            (JsonObject)(await engine.InvokeAsync("tool.call", new JsonObject { ["name"] = tool, ["arguments"] = args }, Bridge))!;
        var a = await Paired("run_diagnostic", new JsonObject { ["diagnosticId"] = Defender });
        var b = await Paired("run_diagnostic", new JsonObject { ["diagnosticId"] = Disks });
        var requestId = RequestOf(r, a.Str("operationId"));
        Assert.Equal(requestId, RequestOf(r, b.Str("operationId")));
        Assert.Contains("Poll get_operation", a.Str("next"));
        await Approve(r, requestId, 2);
        await WaitChecks(r, requestId, "complete");
        var c = await Paired("run_diagnostic", new JsonObject { ["diagnosticId"] = BitLocker });
        Assert.NotEqual(requestId, RequestOf(r, c.Str("operationId"))); // approved: a new request
        var op = await Paired("get_operation", new JsonObject { ["operationId"] = a.Str("operationId") });
        Assert.Equal("complete", op.Str("status"));
        Assert.Equal(0, r.Store.Read(db => (long)db.Scalar("SELECT COUNT(*) FROM continuations")!)); // a paired window is not continued
    }

    [Fact]
    public async Task Expiry_After10Minutes_AndDecline_WithDeclinedBefore_AndTheCooldown()
    {
        await using var r = await Start();
        var taskId = await NewTask(r);
        var a = await Check(r, taskId, Defender, null);
        var first = RequestOf(r, a.Str("operationId"));
        r.Store.Write(db => db.Exec("UPDATE check_requests SET expires_at = '2020-01-01T00:00:00.000Z' WHERE request_id = $r", ("$r", first)));
        Assert.Equal("expired", (await GetOp(r, taskId, a.Str("operationId"))).Str("status"));
        Assert.Equal("expired", (await r.C.Fails("checks.approve", new JsonObject { ["idempotencyKey"] = "x", ["requestId"] = first, ["revision"] = 1 })).ErrorData.Str("reason"));

        var b = await Check(r, taskId, BitLocker, null);
        var second = RequestOf(r, b.Str("operationId"));
        var declined = await r.C.Call("checks.decline", new JsonObject { ["requestId"] = second });
        SchemaTests.AssertValid("checks", declined);
        Assert.Equal(("declined", "user_declined"), (declined.Str("state"), declined.Str("code")));
        Assert.Equal("declined", (await GetOp(r, taskId, b.Str("operationId"))).Str("status"));
        Assert.Equal("declined", (await r.C.Fails("checks.decline", new JsonObject { ["requestId"] = second })).ErrorData.Str("reason"));
        // The same check is refused for the rest of the task; the cooldown is app-wide.
        var refused = await Check(r, taskId, BitLocker, null);
        Assert.Equal(("declined_before", AgentService.DeclinedBeforeMessage), (refused["error"].Str("code"), refused["error"].Str("message")));
        var third0 = await Check(r, taskId, Disks, null);
        Assert.Equal(("not_allowed", AgentService.CheckRequestsUsed), (third0["error"].Str("code"), third0["error"].Str("message")));

        var other = await NewTask(r); // another task (starting it stopped the first): still inside the 60 s
        var c = await Check(r, other, Disks, null);
        var third = RequestOf(r, c.Str("operationId"));
        var view = await ChecksView(r, third);
        Assert.NotNull(view.Str("retryAt"));
        Assert.True(DateTimeOffset.Parse(view.Str("retryAt"), System.Globalization.CultureInfo.InvariantCulture) > DateTimeOffset.UtcNow.AddSeconds(30));
        var cooling = await r.C.Fails("checks.approve", new JsonObject { ["idempotencyKey"] = "y", ["requestId"] = third, ["revision"] = 1 });
        Assert.Equal(("cooldown", view.Str("retryAt")), (cooling.ErrorData.Str("reason"), cooling.ErrorData.Str("retryAt")));
        Assert.Empty(r.Reads.Requests); // nothing was dispatched
    }

    [Fact]
    public async Task TwoApprovedRequests_TheFirstDeclinedAtTheHelper_TheSecondMeetsTheCooldown_AndWaitsAgain()
    {
        await using var r = await Start();
        var taskId = await NewTask(r);
        var turn1 = await Turn(r, taskId);
        var first = RequestOf(r, (await Check(r, taskId, Defender, turn1)).Str("operationId"));
        await EndTurn(r, taskId, turn1);
        var turn2 = await Turn(r, taskId, "And the disks?");
        var b = await Check(r, taskId, Disks, turn2);
        var second = RequestOf(r, b.Str("operationId"));
        await EndTurn(r, taskId, turn2);

        var gate = new TaskCompletionSource();
        r.Reads.Script = async (req, _) =>
        {
            if (req.RequestId != first) return r.Reads.AllOk(req);
            await gate.Task; // UAC is showing for the first request
            return new ElevatedReadResult("declined", "uac_cancelled",
                [.. req.Checks.Select(c => new ElevatedCheckResult(c.N, "cancelled", "uac_cancelled", new ElevatedReadCoverage(false, 0, 0), []))], true);
        };
        await Approve(r, first, 1);
        await Until(() => r.Reads.Requests.Count == 1);
        Assert.Equal("running", (await Approve(r, second, 1)).Str("state")); // queued behind the helper's session
        gate.SetResult();
        await WaitChecks(r, first, "declined");
        var waiting = (await r.C.WaitForEvent(e => e.Str("type") == AgentService.ChecksEvent && e["payload"].Str("requestId") == second
            && e["payload"].Str("state") == "awaiting_user" && e["payload"]?["retryAt"] is JsonValue))["payload"]!;
        Assert.All(waiting["checks"]!.AsArray(), c => Assert.Equal("awaiting_user", c.Str("state")));
        Assert.Single(r.Reads.Requests); // the second never reached the helper
        Assert.Equal("awaiting_user", r.Store.Read(db => db.Scalar("SELECT state FROM check_requests WHERE request_id = $r", ("$r", second))));
        Assert.Equal("awaiting_user", (await GetOp(r, taskId, b.Str("operationId"))).Str("status"));
        Assert.Equal("cooldown", (await r.C.Fails("checks.approve", new JsonObject { ["idempotencyKey"] = "z", ["requestId"] = second, ["revision"] = 1 })).ErrorData.Str("reason"));
    }

    [Fact]
    public async Task AReadQueuedBehindAWriteOrScript_MeetsACooldownStartedMeanwhile_AndWaitsAgain()
    {
        await using var r = await Start();
        var taskId = await NewTask(r);
        var turn1 = await Turn(r, taskId);
        var a = await Check(r, taskId, Defender, turn1);
        var first = RequestOf(r, a.Str("operationId"));
        await EndTurn(r, taskId, turn1);
        var turn2 = await Turn(r, taskId, "And the disks?");
        var second = RequestOf(r, (await Check(r, taskId, Disks, turn2)).Str("operationId"));
        await EndTurn(r, taskId, turn2);

        var held = new TaskCompletionSource(); // a write (or a script) holds the helper exclusion
        r.Reads.Exclusion = held.Task;
        Assert.Equal("running", (await Approve(r, first, 1)).Str("state")); // passed the engine's checks, now waits for the helper
        await Until(() => Volatile.Read(ref r.Reads.Waiting) == 1);
        await r.C.Call("checks.decline", new JsonObject { ["requestId"] = second }); // the cooldown starts meanwhile
        held.SetResult(); // the write ends
        var back = (await r.C.WaitForEvent(e => e.Str("type") == AgentService.ChecksEvent && e["payload"].Str("requestId") == first
            && e["payload"].Str("state") == "awaiting_user" && e["payload"]?["retryAt"] is JsonValue))["payload"]!;
        Assert.All(back["checks"]!.AsArray(), c => Assert.Equal("awaiting_user", c.Str("state")));
        Assert.Empty(r.Reads.Requests); // never launched
        Assert.Equal("awaiting_user", (await GetOp(r, taskId, a.Str("operationId"))).Str("status"));
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("helper_stopped")]
    public async Task StoppingRunningChecks_ByTheUser_StartsTheCooldown(string how)
    {
        await using var r = await Start();
        var taskId = await NewTask(r);
        var first = RequestOf(r, (await Check(r, taskId, Disks, null)).Str("operationId"));
        var gate = new TaskCompletionSource();
        r.Reads.Script = async (req, _) =>
        {
            await gate.Task;
            return new ElevatedReadResult("stopped", "stopped",
                [.. req.Checks.Select(c => new ElevatedCheckResult(c.N, "cancelled", "stopped", new ElevatedReadCoverage(false, 0, 0), []))], true);
        };
        await Approve(r, first, 1);
        await Until(() => r.Reads.Requests.Count == 1);
        if (how == "stop")
        {
            await r.C.Call("agent.stop", new JsonObject { ["taskId"] = taskId });
            await WaitChecks(r, first, "cancelled");
            taskId = await NewTask(r);
        }
        gate.SetResult();
        if (how == "helper_stopped") Assert.Equal("stopped", (await WaitChecks(r, first, "cancelled"))["payload"].Str("code"));
        var next = RequestOf(r, (await Check(r, taskId, Defender, null)).Str("operationId"));
        Assert.NotNull((await ChecksView(r, next)).Str("retryAt"));
        Assert.Equal("cooldown", (await r.C.Fails("checks.approve", new JsonObject { ["idempotencyKey"] = "c", ["requestId"] = next, ["revision"] = 1 })).ErrorData.Str("reason"));
    }

    /// <summary>The system clocks, with a wall clock the test can move (a user changing the time); the monotonic clock is untouched.</summary>
    private sealed class MovableClock : TimeProvider
    {
        public TimeSpan Offset;
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Offset;
    }

    /// <summary>A new task asks for a check: its request's retryAt (null: no cooldown) and whether checks.approve is refused cooldown.</summary>
    private static async Task<(DateTimeOffset? RetryAt, bool Cooling)> Probe(Rig r, Client c, string id = Defender)
    {
        var taskId = (await AgentSetup.StartTask(c, r.ReportId, grant: AdminGrant())).Str("taskId");
        var op = await r.Agent.CallToolAsync(taskId, "run_diagnostic", new JsonObject { ["diagnosticId"] = id }, "in_app", null, default);
        var requestId = RequestOf(r, op.Str("operationId"));
        var view = (await c.WaitForEvent(e => e.Str("type") == AgentService.ChecksEvent && e["payload"].Str("requestId") == requestId))["payload"]!;
        var retryAt = view["retryAt"] is JsonValue v ? DateTimeOffset.Parse(v.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture) : (DateTimeOffset?)null;
        var cooling = false;
        try { await c.Call("checks.approve", new JsonObject { ["idempotencyKey"] = Guid.NewGuid().ToString(), ["requestId"] = requestId, ["revision"] = 1 }); }
        catch (JsonRpcException e) { cooling = e.ErrorData.Str("reason") == "cooldown"; }
        return (retryAt, cooling);
    }

    private static async Task Decline(Rig r)
    {
        var taskId = await NewTask(r);
        await r.C.Call("checks.decline", new JsonObject { ["requestId"] = RequestOf(r, (await Check(r, taskId, Disks, null)).Str("operationId")) });
    }

    [Theory]
    [InlineData(-1)] // the clock moved back a day: still 60 s, not a day and a minute
    [InlineData(1)]  // the clock moved forward a day: still 60 s, not over at once
    public async Task TheCooldown_RunsOnTheMonotonicClock_WhenTheWallClockMoves(int days)
    {
        var clock = new MovableClock();
        await using var r = await Start(time: clock);
        await Decline(r);
        clock.Offset = TimeSpan.FromDays(days);
        var (retryAt, cooling) = await Probe(r, r.C);
        Assert.True(cooling);
        var now = clock.GetUtcNow();
        Assert.InRange(retryAt!.Value, now.AddSeconds(30), now.AddSeconds(61)); // retryAt: the wall clock now plus the time left
    }

    [Fact]
    public async Task TheCooldown_AfterARestart_IsClampedTo60s_Normalised_AndAPassedOneIsCleared_EvenAfterARollback()
    {
        var clock = new MovableClock { Offset = TimeSpan.FromDays(1) }; // declined while the clock was a day ahead
        await using var r = await Start(time: clock);
        await Decline(r);
        clock.Offset = TimeSpan.Zero; // corrected
        await r.Host.RestartAsync();
        var c = await r.Host.ConnectAsync(minor: 5);
        string? Stored() => r.Store.Read(db => Settings.Get(db, AgentService.CooldownSetting)?.GetValue<string>());
        var normalised = DateTimeOffset.Parse(Stored()!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(normalised, DateTimeOffset.UtcNow.AddSeconds(30), DateTimeOffset.UtcNow.AddSeconds(61)); // not a day ahead
        var (retryAt, cooling) = await Probe(r, c);
        Assert.True(cooling);
        Assert.InRange(retryAt!.Value, DateTimeOffset.UtcNow.AddSeconds(30), DateTimeOffset.UtcNow.AddSeconds(61));

        // A stored end that has passed is cleared at startup, so moving the clock back can't bring it back.
        r.Store.Write(db => Settings.Set(db, AgentService.CooldownSetting, Time.Format(DateTimeOffset.UtcNow.AddSeconds(-30))));
        await r.Host.RestartAsync();
        c = await r.Host.ConnectAsync(minor: 5);
        Assert.Null(Stored());
        clock.Offset = TimeSpan.FromMinutes(-10);
        Assert.Equal(((DateTimeOffset?)null, false), await Probe(r, c, Disks));
    }

    [Fact]
    public async Task TheCooldown_SurvivesARestart()
    {
        await using var r = await Start();
        var taskId = await NewTask(r);
        var first = RequestOf(r, (await Check(r, taskId, Disks, null)).Str("operationId"));
        await r.C.Call("checks.decline", new JsonObject { ["requestId"] = first });
        await r.Host.RestartAsync();
        var c = await r.Host.ConnectAsync(minor: 5);
        var other = (await AgentSetup.StartTask(c, r.ReportId, grant: AdminGrant())).Str("taskId");
        var op = await r.Agent.CallToolAsync(other, "run_diagnostic", new JsonObject { ["diagnosticId"] = Defender }, "in_app", null, default);
        var next = RequestOf(r, op.Str("operationId"));
        var view = (await c.WaitForEvent(e => e.Str("type") == AgentService.ChecksEvent && e["payload"].Str("requestId") == next))["payload"]!;
        Assert.True(DateTimeOffset.Parse(view.Str("retryAt"), System.Globalization.CultureInfo.InvariantCulture) > DateTimeOffset.UtcNow.AddSeconds(30));
        Assert.Equal("cooldown", (await c.Fails("checks.approve", new JsonObject { ["idempotencyKey"] = "r", ["requestId"] = next, ["revision"] = 1 })).ErrorData.Str("reason"));
    }

    [Fact]
    public async Task ChecksList_ShowsEveryRequestNotEnded_WithItsCooldownWait_AsAgentChecksCarriesIt()
    {
        await using var r = await Start();
        Assert.Contains("checks.list", AuditEngine.DesktopMethods);
        SchemaTests.AssertValidRef("checks.schema.json#/$defs/listParams", new JsonObject());
        var empty = await r.C.Call("checks.list", new JsonObject());
        SchemaTests.AssertValidRef("checks.schema.json#/$defs/listResult", empty);
        Assert.Empty(empty["requests"]!.AsArray());

        var taskId = await NewTask(r);
        var turn1 = await Turn(r, taskId);
        var declined = RequestOf(r, (await Check(r, taskId, Defender, turn1)).Str("operationId"));
        await EndTurn(r, taskId, turn1);
        var turn2 = await Turn(r, taskId, "And the disks?");
        var waiting = RequestOf(r, (await Check(r, taskId, Disks, turn2)).Str("operationId"));
        await EndTurn(r, taskId, turn2);
        await r.C.Call("checks.decline", new JsonObject { ["requestId"] = declined }); // the cooldown starts

        var list = await r.C.Call("checks.list", new JsonObject());
        SchemaTests.AssertValidRef("checks.schema.json#/$defs/listResult", list);
        var only = Assert.Single(list["requests"]!.AsArray())!.AsObject(); // the declined request has ended
        Assert.Equal((waiting, "awaiting_user"), (only.Str("requestId"), only.Str("state")));
        Assert.NotNull(only.Str("retryAt")); // inside its cooldown wait
        var view = await r.C.WaitForEvent(e => e.Str("type") == AgentService.ChecksEvent && e["payload"].Str("requestId") == waiting && e["payload"]?["retryAt"] is JsonValue);
        Assert.Equal(view["payload"]!.ToJsonString(), only.ToJsonString()); // the same view as agent.checks

        r.Store.Write(db => db.Exec("UPDATE check_requests SET expires_at = '2020-01-01T00:00:00.000Z' WHERE request_id = $r", ("$r", waiting)));
        Assert.Empty((await r.C.Call("checks.list", new JsonObject()))["requests"]!.AsArray()); // expired on read: ended
    }

    // ---------------------------------------------------------------- rechecks and discards

    [Fact]
    public async Task RecheckBeforeStart_TheBudget_CancelsInsteadOfStartingTheHelper()
    {
        await using var r = await Start();
        var taskId = await NewTask(r, AdminGrant(limits: new JsonObject { ["totalBytes"] = 4096, ["resultBytes"] = 2048 }));
        var a = await Check(r, taskId, Disks, null);
        var requestId = RequestOf(r, a.Str("operationId"));
        r.Store.Write(db =>
        {
            var task = AgentService.LoadTask(db, taskId);
            task["used"]!["bytesShared"] = 4096;
            db.Exec("UPDATE agent_tasks SET body = $b WHERE task_id = $t", ("$b", Engine.Json.Write(task)), ("$t", taskId));
        });
        var result = await Approve(r, requestId, 1);
        Assert.Equal(("cancelled", "exhausted"), (result.Str("state"), result.Str("code")));
        Assert.Empty(r.Reads.Requests);
    }

    [Theory]
    [InlineData("task_ended")]
    [InlineData("permissions")]
    public async Task ACancelBetweenTheApprovalAndTheLaunch_NeverStartsTheHelper(string cause)
    {
        await using var r = await Start();
        if (cause == "permissions") await SetAdmin(r, true);
        var taskId = await NewTask(r);
        var a = await Check(r, taskId, Disks, null);
        var requestId = RequestOf(r, a.Str("operationId"));
        var revoked = new TaskCompletionSource();
        r.Agent.BeforeReadLaunch = async id =>
        {
            // The approval has committed (running, its stop registered); the helper has not been launched yet.
            Assert.Equal("running", r.Store.Read(db => db.Scalar("SELECT state FROM check_requests WHERE request_id = $r", ("$r", id))));
            if (cause == "permissions") await SetAdmin(r, false);
            else await r.C.Call("agent.stop", new JsonObject { ["taskId"] = taskId });
            revoked.SetResult();
        };
        Assert.Equal("running", (await Approve(r, requestId, 1)).Str("state"));
        await revoked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(cause, (await ChecksView(r, requestId, state: "cancelled")).Str("code"));
        await Until(() => !r.Agent.HasActiveWork);
        Assert.Empty(r.Reads.Requests); // the helper was never started
        Assert.Equal("cancelled", r.Store.Read(db => db.Scalar("SELECT state FROM check_requests WHERE request_id = $r", ("$r", requestId))));
    }

    [Theory]
    [InlineData("permissions")]
    [InlineData("ai")]
    [InlineData("task_ended")]
    public async Task DuringTheSession_ANarrowingAChosenAiChangeOrATaskEnd_SendsStop_AndDiscardsTheResults(string cause)
    {
        await using var r = await Start();
        r.Reads.Items[Defender] = [Detection()];
        if (cause == "permissions") await SetAdmin(r, true);
        var taskId = await NewTask(r);
        var turn = await Turn(r, taskId);
        var a = await Check(r, taskId, Defender, turn);
        await EndTurn(r, taskId, turn);
        var requestId = RequestOf(r, a.Str("operationId"));
        var gate = new TaskCompletionSource();
        var stopped = new TaskCompletionSource();
        r.Reads.Script = async (req, stop) =>
        {
            stop.Register(() => stopped.TrySetResult());
            await gate.Task;
            return r.Reads.AllOk(req); // the helper still answers: the host discards it
        };
        await Approve(r, requestId, 1);
        await Until(() => r.Reads.Requests.Count == 1);
        switch (cause)
        {
            case "permissions":
                await SetAdmin(r, false); // a narrowing
                break;
            case "ai":
                await AgentSetup.Choose(r.C, "openai_api");
                break;
            default:
                await r.C.Call("agent.stop", new JsonObject { ["taskId"] = taskId });
                break;
        }
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10)); // stop was sent to the helper
        var view = await ChecksView(r, requestId, state: "cancelled");
        Assert.Equal(("cancelled", cause), (view.Str("state"), view.Str("code")));
        gate.SetResult();
        await Until(() => !r.Agent.HasActiveWork);
        Assert.Equal(0, r.Store.Read(db => (long)db.Scalar("SELECT COUNT(*) FROM agent_evidence WHERE collector_id LIKE 'admin.%'")!)); // nothing stored
        Assert.Equal("cancelled", LastChecks(r, requestId).Str("state"));
        Assert.Equal("dropped", r.Store.Read(db => db.Scalar("SELECT state FROM continuations WHERE request_id = $r", ("$r", requestId))));
    }

    // ---------------------------------------------------------------- intake

    [Fact]
    public async Task Malformed_StoresNothing_Interrupted_KeepsTheFinishedChecks()
    {
        await using var r = await Start();
        var taskId = await NewTask(r);
        var a = await Check(r, taskId, Defender, null);
        var requestId = RequestOf(r, a.Str("operationId"));
        var bad = Detection();
        bad["path"] = @"C:\Users\x\evil.exe"; // a field that is not on the list
        r.Reads.Script = (req, _) => Task.FromResult(new ElevatedReadResult("complete", null, [Ok(1, [Detection(), bad])], true));
        await Approve(r, requestId, 1);
        var failed = await WaitChecks(r, requestId, "failed");
        Assert.Equal("malformed", failed["payload"].Str("code"));
        Assert.Equal(("failed", "malformed"), ((await GetOp(r, taskId, a.Str("operationId"))).Str("status"), (await GetOp(r, taskId, a.Str("operationId"))).Str("code")));
        Assert.Equal(0, r.Store.Read(db => (long)db.Scalar("SELECT COUNT(*) FROM agent_evidence WHERE collector_id LIKE 'admin.%'")!));

        // A host's not-started code passes through; a session lost before read_end keeps what finished.
        var task2 = await NewTask(r);
        var d = await Check(r, task2, Defender, null);
        var e = await Check(r, task2, BitLocker, null);
        var second = RequestOf(r, d.Str("operationId"));
        r.Reads.Script = (req, _) => Task.FromResult(new ElevatedReadResult("interrupted", null,
            [Ok(1, [Detection()]), new ElevatedCheckResult(2, "failed", "interrupted", new ElevatedReadCoverage(false, 0, 0), [])], true));
        await Approve(r, second, 2);
        var ended = await WaitChecks(r, second, "failed");
        Assert.Equal("interrupted", ended["payload"].Str("code"));
        Assert.Equal("complete", (await GetOp(r, task2, d.Str("operationId"))).Str("status"));
        Assert.Equal(("failed", "interrupted"), ((await GetOp(r, task2, e.Str("operationId"))).Str("status"), (await GetOp(r, task2, e.Str("operationId"))).Str("code")));
    }

    [Theory]
    [InlineData("declined", "uac_cancelled", "declined")]
    [InlineData("not_started", "broker_missing", "failed")]
    [InlineData("timeout", null, "failed")]
    public async Task HelperEndStates_MapToTheRequest(string session, string? code, string expected)
    {
        await using var r = await Start();
        var taskId = await NewTask(r);
        var a = await Check(r, taskId, Disks, null);
        var requestId = RequestOf(r, a.Str("operationId"));
        r.Reads.Script = (req, _) => Task.FromResult(new ElevatedReadResult(session, code,
            session == "timeout"
                ? [new ElevatedCheckResult(1, "failed", "timeout", new ElevatedReadCoverage(false, 0, 0), [])]
                : [.. req.Checks.Select(c => new ElevatedCheckResult(c.N, session == "declined" ? "cancelled" : "failed", code, new ElevatedReadCoverage(false, 0, 0), []))],
            session != "not_started"));
        await Approve(r, requestId, 1);
        var ended = (await WaitChecks(r, requestId, expected))["payload"]!;
        Assert.Equal(code ?? "timeout", ended.Str("code"));
        Assert.Equal(expected, (await GetOp(r, taskId, a.Str("operationId"))).Str("status"));
        if (session == "declined") Assert.Equal("declined_before", (await Check(r, taskId, Disks, null))["error"].Str("code"));
    }

    // ---------------------------------------------------------------- continuation and card.fix

    [Fact]
    public async Task Continuation_StartsExactlyOnce_AfterTheTurnEnds_WithTheHostsMessage_NeverResultText()
    {
        await using var r = await Start();
        r.Reads.Items[Defender] = [Detection()];
        var taskId = await NewTask(r);
        var turn = await Turn(r, taskId);
        var a = await Check(r, taskId, Defender, turn);
        var requestId = RequestOf(r, a.Str("operationId"));
        await Approve(r, requestId, 1);
        await WaitChecks(r, requestId, "complete");
        await Task.Delay(200);
        Assert.Single(r.Provider.Requests); // a turn runs: the continuation waits for it
        Assert.Equal("pending", r.Store.Read(db => db.Scalar("SELECT state FROM continuations WHERE request_id = $r", ("$r", requestId))));

        await EndTurn(r, taskId, turn);
        var question = await AgentSetup.TaskEvent(r.C, taskId, "agent.question", p => p.Str("checks") == requestId);
        SchemaTests.AssertValid("event", question);
        await Until(() => r.Provider.Requests.Count == 2);
        var prompt = ((UserText)r.Provider.Requests.Last().Messages.OfType<UserText>().Last()).Text;
        var evidence = r.Store.Read(db => db.Query("SELECT evidence_id FROM agent_evidence WHERE task_id = $t AND collector_id LIKE 'admin.%' ORDER BY rowid", x => x.GetString(0), ("$t", taskId)));
        Assert.Contains($"- {Defender}, operation {a.Str("operationId")}: complete; evidence {string.Join(", ", evidence)}.", prompt);
        Assert.Contains("The administrator checks you asked for ended (complete):", prompt);
        Assert.DoesNotContain("Wacatac", prompt);
        Assert.DoesNotContain("Trojan", prompt);
        await EndTurn(r, taskId, question["payload"].Str("turnId"));
        await Task.Delay(200);
        Assert.Equal(2, r.Provider.Requests.Count); // exactly once
        Assert.Equal("started", r.Store.Read(db => db.Scalar("SELECT state FROM continuations WHERE request_id = $r", ("$r", requestId))));
    }

    [Fact]
    public async Task FixAttempt_ShowsWaitingChecks_ThenChecking_ThenWorkingAgain_InItsContinuation_WithTheFixBudget()
    {
        await using var r = await Start();
        var taskId = await NewTask(r);
        var card = r.Store.Read(db => Followups.AgentCards(db, r.ReportId, true, null))!.OfType<JsonObject>().First(c => c.Str("status") == "open" && c["findingId"] is not null).Str("cardId");
        var turn = (await r.C.Call("agent.ask", new JsonObject { ["idempotencyKey"] = Guid.NewGuid().ToString(), ["taskId"] = taskId, ["intent"] = "fix", ["cardId"] = card })).Str("turnId");
        var a = await Check(r, taskId, Defender, turn);
        var requestId = RequestOf(r, a.Str("operationId"));
        Assert.Equal(card, (await ChecksView(r, requestId)).Str("cardId"));
        string? State() => r.Store.Read(db => FixAttempts.Derive(db, card))?.Str("state");
        Assert.Equal("working", State()); // working wins while the turn runs
        await EndTurn(r, taskId, turn);
        Assert.Equal("waiting_checks", State());
        var fix = r.Store.Read(db => FixAttempts.Derive(db, card))!;
        SchemaTests.AssertValidRef("fix.schema.json", fix);
        Assert.Equal((requestId, taskId), (fix.Str("requestId"), fix.Str("taskId"))); // the attempt's task: a card's Stop binds to it
        Assert.Contains(r.C.Events, e => e.Str("type") == "card.fix" && e["payload"]?["fix"]?["taskId"]?.GetValue<string>() == taskId);
        Assert.Equal("fix_active", (await r.C.Fails("fix.dismiss", new JsonObject { ["cardId"] = card })).ErrorData.Str("reason"));

        var gate = new TaskCompletionSource();
        r.Reads.Script = async (req, _) => { await gate.Task; return r.Reads.AllOk(req); };
        await Approve(r, requestId, 1);
        await r.C.WaitForEvent(e => e.Str("type") == "card.fix" && e["payload"].Str("cardId") == card && e["payload"]?["fix"]?.Str("state") == "checking");
        gate.SetResult();
        var next = await AgentSetup.TaskEvent(r.C, taskId, "agent.question", p => p.Str("checks") == requestId);
        Assert.Equal(("fix", fix.Str("attemptId")), (next["payload"].Str("intent"), next["payload"].Str("attemptId")));
        await Until(() => State() == "working");
        await Until(() => r.Provider.Requests.Count == 2);
        Assert.Contains($"Then finish the fix request on card {card}", ((UserText)r.Provider.Requests.Last().Messages.OfType<UserText>().Last()).Text);
        await EndTurn(r, taskId, next["payload"].Str("turnId"));
        Assert.Equal("no_fix", State());
    }

    [Fact]
    public async Task FixContinuation_WaitsForItsOwnRunningTurn_ThenStartsAsTheFixAsk()
    {
        await using var r = await Start();
        var taskId = await NewTask(r);
        var card = r.Store.Read(db => Followups.AgentCards(db, r.ReportId, true, null))!.OfType<JsonObject>().First(c => c.Str("status") == "open" && c["findingId"] is not null).Str("cardId");
        var turn = (await r.C.Call("agent.ask", new JsonObject { ["idempotencyKey"] = Guid.NewGuid().ToString(), ["taskId"] = taskId, ["intent"] = "fix", ["cardId"] = card })).Str("turnId");
        var a = await Check(r, taskId, Defender, turn);
        var requestId = RequestOf(r, a.Str("operationId"));
        await Approve(r, requestId, 1);
        await WaitChecks(r, requestId, "complete"); // the fix turn that asked still runs (its attempt is working)
        await Task.Delay(200);
        Assert.Equal("pending", r.Store.Read(db => db.Scalar("SELECT state FROM continuations WHERE request_id = $r", ("$r", requestId))));
        Assert.Single(r.Provider.Requests);
        var attemptId = r.Store.Read(db => FixAttempts.Current(db, card))!.Id;

        await EndTurn(r, taskId, turn);
        var next = await AgentSetup.TaskEvent(r.C, taskId, "agent.question", p => p.Str("checks") == requestId);
        Assert.Equal(("fix", attemptId), (next["payload"].Str("intent"), next["payload"].Str("attemptId")));
        await Until(() => r.Provider.Requests.Count == 2);
        Assert.Equal("started", r.Store.Read(db => db.Scalar("SELECT state FROM continuations WHERE request_id = $r", ("$r", requestId))));
        await EndTurn(r, taskId, next["payload"].Str("turnId"));
    }

    [Fact]
    public async Task Shutdown_WithAContinuationPendingBehindARunningAsk_StartsNoNewRun()
    {
        await using var r = await Start();
        var taskId = await NewTask(r);
        var turn = await Turn(r, taskId);
        var requestId = RequestOf(r, (await Check(r, taskId, Defender, turn)).Str("operationId"));
        await Approve(r, requestId, 1);
        await WaitChecks(r, requestId, "complete"); // the continuation waits behind the running ask
        string? Continuation() => (string?)r.Store.Read(db => db.Scalar("SELECT state FROM continuations WHERE request_id = $r", ("$r", requestId)));
        Assert.Equal("pending", Continuation());

        await r.Agent.StopAllAsync().WaitAsync(TimeSpan.FromSeconds(5)); // the ask is cancelled and ends: nothing replaces it
        Assert.False(r.Agent.HasActiveWork);
        Assert.Single(r.Provider.Requests);
        Assert.Equal("pending", Continuation()); // left for the next start, which drops it
    }

    [Fact]
    public async Task Shutdown_SendsStopToARunningHelperSession_AndTakesNothingIn()
    {
        await using var r = await Start();
        r.Reads.Items[Defender] = [Detection()];
        var taskId = await NewTask(r);
        var requestId = RequestOf(r, (await Check(r, taskId, Defender, null)).Str("operationId"));
        var stopped = new TaskCompletionSource();
        r.Reads.Script = async (req, stop) =>
        {
            stop.Register(() => stopped.TrySetResult());
            await stopped.Task;
            return r.Reads.AllOk(req); // the helper still answers: the host is stopping and takes nothing in
        };
        await Approve(r, requestId, 1);
        await Until(() => r.Reads.Requests.Count == 1);
        await r.Agent.StopAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(stopped.Task.IsCompleted);
        Assert.False(r.Agent.HasActiveWork);
        Assert.Equal(0, r.Store.Read(db => (long)db.Scalar("SELECT COUNT(*) FROM agent_evidence WHERE collector_id LIKE 'admin.%'")!));
        Assert.Equal("running", r.Store.Read(db => db.Scalar("SELECT state FROM check_requests WHERE request_id = $r", ("$r", requestId)))); // startup fails it interrupted
    }

    [Fact]
    public async Task Shutdown_WhileAFinishedReadWaitsForTheStore_TakesNothingIn()
    {
        await using var r = await Start();
        r.Reads.Items[Defender] = [Detection()];
        var taskId = await NewTask(r);
        var requestId = RequestOf(r, (await Check(r, taskId, Defender, null)).Str("operationId"));
        var storeHeld = new TaskCompletionSource();
        using var release = new ManualResetEventSlim();
        var atIntake = new TaskCompletionSource();
        r.Agent.BeforeIntakeStore = id =>
        {
            // The session finished and passed the outer shutdown check; someone else holds the store before intake gets it.
            _ = Task.Run(() => r.Store.Write(db => { storeHeld.TrySetResult(); release.Wait(); }));
            storeHeld.Task.Wait();
            atIntake.TrySetResult();
        };
        await Approve(r, requestId, 1);
        await atIntake.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stopping = Task.Run(() => r.Agent.StopAllAsync());
        await Until(() => r.Agent.ShuttingDown);
        release.Set(); // intake now gets the store, after shutdown began
        await stopping.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, r.Store.Read(db => (long)db.Scalar("SELECT COUNT(*) FROM agent_evidence WHERE collector_id LIKE 'admin.%'")!));
        Assert.Equal("running", r.Store.Read(db => db.Scalar("SELECT state FROM check_requests WHERE request_id = $r", ("$r", requestId)))); // startup fails it interrupted
        Assert.DoesNotContain(r.C.Events, e => e.Str("type") == AgentService.ChecksEvent && e["payload"].Str("requestId") == requestId && e["payload"].Str("state") == "complete");
    }

    [Fact]
    public async Task Continuation_IsDropped_WhenTheAttemptWasReplaced()
    {
        await using var r = await Start();
        var taskId = await NewTask(r);
        var card = r.Store.Read(db => Followups.AgentCards(db, r.ReportId, true, null))!.OfType<JsonObject>().First(c => c.Str("status") == "open" && c["findingId"] is not null).Str("cardId");
        var turn = (await r.C.Call("agent.ask", new JsonObject { ["idempotencyKey"] = Guid.NewGuid().ToString(), ["taskId"] = taskId, ["intent"] = "fix", ["cardId"] = card })).Str("turnId");
        var a = await Check(r, taskId, Defender, turn);
        var requestId = RequestOf(r, a.Str("operationId"));
        await Approve(r, requestId, 1);
        await WaitChecks(r, requestId, "complete"); // the turn still runs: the continuation waits
        r.Store.Write(db => FixAttempts.Start(db, taskId, r.ReportId, card, null, "Claude")); // the attempt is replaced meanwhile
        await EndTurn(r, taskId, turn);
        await Until(() => (string?)r.Store.Read(db => db.Scalar("SELECT state FROM continuations WHERE request_id = $r", ("$r", requestId))) == "dropped");
        Assert.Single(r.Provider.Requests);
    }

    [Fact]
    public async Task Headless_RunsThroughTheSamePath()
    {
        await using var r = await Start();
        var claude = Path.Combine(Path.GetTempPath(), "ot-tests", "cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(claude);
        var exe = Path.Combine(claude, "claude.cmd");
        File.WriteAllText(exe, "@echo off");
        try
        {
            await r.C.Call("provider.setClient", new JsonObject { ["kind"] = "claude_code_mcp", ["path"] = exe, ["signedIn"] = true, ["version"] = "2.1.318" });
            var taskId = (await AgentSetup.StartTask(r.C, r.ReportId, "claude_code_mcp", AdminGrant())).Str("taskId");
            var engine = r.Host.Server.Engine;
            var bridge = new CallContext("bridge-run", "agent", 6363);
            string? opId = null;
            r.Cli.Script = async (launch, line, ct) =>
            {
                await engine.InvokeAsync("agent.pair", new JsonObject { ["client"] = "claude_code", ["runToken"] = FakeCli.Token(launch) }, bridge);
                var result = (JsonObject)(await engine.InvokeAsync("tool.call", new JsonObject { ["name"] = "run_diagnostic", ["arguments"] = new JsonObject { ["diagnosticId"] = Disks } }, bridge))!;
                opId ??= result.Str("operationId");
                line(FakeCli.Claude("result", new JsonObject { ["subtype"] = "success", ["is_error"] = false, ["result"] = "Waiting.", ["session_id"] = launch.Arguments[^1], ["num_turns"] = 1, ["usage"] = new JsonObject { ["input_tokens"] = 1, ["output_tokens"] = 1 } }));
                return new CliExit(0, "");
            };
            var turn = (await AgentSetup.Ask(r.C, taskId)).Str("turnId");
            await AgentSetup.TaskEvent(r.C, taskId, "agent.message", p => p.Str("turnId") == turn);
            var requestId = RequestOf(r, opId!);
            await Approve(r, requestId, 1);
            await AgentSetup.TaskEvent(r.C, taskId, "agent.question", p => p.Str("checks") == requestId);
            await Until(() => r.Cli.Launches.Count == 2);
            Assert.Contains($"- {Disks}, operation {opId}: complete", r.Cli.Launches[1].StandardInput);
        }
        finally
        {
            try { Directory.Delete(claude, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void ContinuationMessage_IsFixedHostText()
    {
        var text = AgentService.ContinuationMessage("chk-0000000000000001", "failed",
            [("security.defender_history", "op-1", "complete", ["ev-0000000000000001"], null), ("storage.health", "op-2", "failed", [], "timeout")], "card-1");
        Assert.Equal(
            "The administrator checks you asked for ended (failed):\n" +
            "- security.defender_history, operation op-1: complete; evidence ev-0000000000000001.\n" +
            "- storage.health, operation op-2: failed (timeout).\n" +
            "Read the results with get_operation and each operationId, and cite evidence as [ev-…]. A declined check can't be asked for again in this conversation: go on without it.\n" +
            "Then finish the fix request on card card-1: end with exactly one plan, or note_card on it with an outcome (needs_user or nothing_to_fix).", text);
    }

    [Fact]
    public void HostAndHelper_ReadRegistry_ListTheSameChecks_AsTheAdminTier()
    {
        Assert.Equal(AdminChecks.All.Select(c => c.Id).Order(), Diagnostics.All.Where(d => d.Admin).Select(d => d.Id).Order());
        Assert.All(EvidenceProjection.CollectorIds.Where(id => id.StartsWith("admin.", StringComparison.Ordinal)), id => Assert.NotNull(AdminChecks.Find(id["admin.".Length..])));
        Assert.Equal(3, EvidenceProjection.CollectorIds.Count(id => id.StartsWith("admin.", StringComparison.Ordinal)));
    }
}

using System.Diagnostics;
using System.Text.Json.Nodes;
using OptimizationTool.Desktop.Bridge;

namespace OptimizationTool.Desktop.Tests;

public sealed class PlanApprovalTests
{
    private const string Doc = "https://app.optimizationtool.invalid/index.html";
    private const string PlanId = "plan-abc123";
    private const string Hash = "9f2c0a7d1e";

    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private DateTimeOffset _utc = T0;
    private long _ticks = 1_000_000;
    private readonly FakeHost _host = new();
    private readonly FakePrompt _prompt = new();
    private readonly BridgeDispatcher _dispatcher;

    public PlanApprovalTests()
    {
        _dispatcher = new BridgeDispatcher(new BridgeRouter(), _host, DesktopLog.Disabled,
            new SettingsLauncher(_ => { }), approvals: new PlanApproval(_prompt, () => _ticks, () => _utc));
    }

    private static JsonObject Plan(string hash = Hash, string status = "awaiting_user", DateTimeOffset? expires = null, string requester = "claude_code") => new()
    {
        ["planId"] = PlanId,
        ["actionId"] = "power.select_scheme",
        ["label"] = "Switch power plan",
        ["target"] = new JsonObject { ["type"] = "power_scheme", ["id"] = "381b4222-f694-41f0-9685-ff5bb260df2e", ["label"] = "Balanced" },
        ["rows"] = new JsonArray(new JsonObject { ["label"] = "Active power plan", ["before"] = "High performance", ["after"] = "Balanced" }),
        ["context"] = new JsonArray(new JsonArray("Power", "On battery · 64%")),
        ["tradeoffs"] = new JsonArray("Slightly lower peak speed"),
        ["privilege"] = "none",
        ["undo"] = new JsonObject { ["supported"] = true, ["conditions"] = "Only while Balanced is still active" },
        ["requestedBy"] = new JsonObject { ["kind"] = requester, ["taskId"] = "task-1" },
        ["planHash"] = hash,
        ["expiresAt"] = (expires ?? T0.AddMinutes(5)).ToString("O"),
        ["status"] = status,
    };

    /// <summary>Simulates the host's plan store and the challenge/approve checks.</summary>
    private sealed class FakeHost : IBackend
    {
        public JsonObject Current = Plan();
        public Func<int, JsonObject>? PlanOnGet; // nth action.get (1-based) → plan
        public readonly List<(string Method, JsonObject? Params)> Calls = [];
        public JsonObject? ApproveError;
        public string? IssuedNonce;
        public int Gets;

        private static JsonObject Ok(JsonNode r) => new() { ["jsonrpc"] = "2.0", ["id"] = 1, ["result"] = r };
        private static JsonObject Err(int code, JsonObject? data = null) => new()
        {
            ["jsonrpc"] = "2.0", ["id"] = 1,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = "x", ["data"] = data },
        };

        public Task<JsonObject> CallAsync(string method, JsonObject? p, CancellationToken ct)
        {
            Calls.Add((method, (JsonObject?)p?.DeepClone()));
            JsonObject r = method switch
            {
                "action.get" => Ok((PlanOnGet?.Invoke(++Gets) ?? Current).DeepClone()),
                "action.decline" => Ok(new JsonObject { ["planId"] = PlanId, ["status"] = "declined" }),
                "action.challenge" => Ok(new JsonObject { ["nonce"] = IssuedNonce = "n-" + Guid.NewGuid().ToString("N"), ["expiresAt"] = "x" }),
                "action.approve" when ApproveError is not null => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["error"] = ApproveError.DeepClone() },
                "action.approve" when p!["planHash"]!.GetValue<string>() != Current["planHash"]!.GetValue<string>()
                    => Err(-32002, new JsonObject { ["current"] = Current.DeepClone() }),
                "action.approve" when p!["nonce"]!.GetValue<string>() != IssuedNonce => Err(-32013, new JsonObject { ["reason"] = "bad_nonce" }),
                "action.approve" => Ok(new JsonObject { ["operationId"] = "op-77", ["planId"] = PlanId, ["state"] = "verified" }),
                _ => Err(-32601),
            };
            return Task.FromResult(r);
        }
    }

    private sealed class FakePrompt : IPlanApprovalPrompt
    {
        public PlanDecision Decision = PlanDecision.Approve;
        public readonly List<PlanView> Shown = [];
        public Action? During;
        public TaskCompletionSource? Gate;

        public async Task<PlanDecision> ConfirmAsync(PlanView view, CancellationToken ct)
        {
            Shown.Add(view);
            During?.Invoke();
            if (Gate is { } g) await g.Task;
            return Decision;
        }
    }

    private async Task<JsonNode> Approve(string paramsJson = $$"""{"planId":"{{PlanId}}"}""", int id = 1)
    {
        var reply = await _dispatcher.HandleAsync(
            $$$"""{"type":"rpc","id":{{{id}}},"method":"shell.approvePlan","params":{{{paramsJson}}}}""", Doc, true, default);
        return JsonNode.Parse(reply!)!;
    }

    private string[] Methods => _host.Calls.Select(c => c.Method).ToArray();

    [Fact]
    public async Task Approve_sends_challenge_then_approve_with_the_exact_hash_from_action_get()
    {
        var reply = await Approve(id: 4);
        Assert.Equal("rpc.result", reply["type"]!.GetValue<string>());
        Assert.Equal(4, reply["id"]!.GetValue<int>());
        Assert.Equal("""{"status":"approved","operationId":"op-77"}""", reply["result"]!.ToJsonString());

        Assert.Equal(["action.get", "action.get", "action.challenge", "action.approve"], Methods);
        var approve = _host.Calls[^1].Params!;
        Assert.Equal(PlanId, approve["planId"]!.GetValue<string>());
        Assert.Equal(Hash, approve["planHash"]!.GetValue<string>());
        Assert.Equal(_host.IssuedNonce, approve["nonce"]!.GetValue<string>());
        Assert.Equal(3, approve.Count);

        var view = Assert.Single(_prompt.Shown);
        Assert.Equal("Switch power plan", view.Title);
        Assert.Equal("Claude Code", view.RequestedBy);
        Assert.Equal(("Active power plan", "High performance", "Balanced"), view.Rows[0]);
        Assert.Equal(["Power: On battery · 64%"], view.Context);
        Assert.False(view.NeedsAdmin);
        Assert.Equal("Undo: yes · Only while Balanced is still active", view.UndoLine);
    }

    [Fact]
    public async Task Decline_calls_action_decline_and_never_approves()
    {
        _prompt.Decision = PlanDecision.Decline;
        var reply = await Approve();
        Assert.Equal("""{"status":"declined"}""", reply["result"]!.ToJsonString());
        Assert.Equal(["action.get", "action.decline"], Methods);
    }

    [Fact]
    public async Task Plan_changed_after_it_was_shown_is_stale_and_not_approved()
    {
        _host.PlanOnGet = n => n == 1 ? Plan(Hash) : Plan("different-hash");
        var reply = await Approve();
        Assert.Equal("stale", reply["result"]!["status"]!.GetValue<string>());
        Assert.DoesNotContain("action.approve", Methods);
        Assert.DoesNotContain("action.challenge", Methods);
    }

    [Fact]
    public async Task Host_rejecting_a_changed_hash_surfaces_stale()
    {
        // Both reads return the shown hash, but the host's plan changes just before approve.
        _host.PlanOnGet = _ => Plan(Hash);
        _prompt.During = () => _host.Current = Plan("rebuilt-hash");
        var reply = await Approve();
        Assert.Equal("""{"status":"stale"}""", reply["result"]!.ToJsonString());
        Assert.Equal(Hash, _host.Calls.Last(c => c.Method == "action.approve").Params!["planHash"]!.GetValue<string>());
    }

    [Fact]
    public async Task Already_expired_plan_is_never_shown()
    {
        _host.Current = Plan(expires: T0.AddSeconds(-1));
        var reply = await Approve();
        Assert.Equal("""{"status":"expired"}""", reply["result"]!.ToJsonString());
        Assert.Empty(_prompt.Shown);
        Assert.Equal(["action.get"], Methods);
    }

    [Fact]
    public async Task Expiry_while_the_dialog_is_open_is_not_approved()
    {
        _prompt.During = () => _utc = T0.AddMinutes(6);
        var reply = await Approve();
        Assert.Equal("""{"status":"expired"}""", reply["result"]!.ToJsonString());
        Assert.DoesNotContain("action.approve", Methods);
    }

    [Fact]
    public async Task Dialog_reporting_expiry_is_expired()
    {
        _prompt.Decision = PlanDecision.Expired;
        Assert.Equal("expired", (await Approve())["result"]!["status"]!.GetValue<string>());
        Assert.DoesNotContain("action.approve", Methods);
    }

    [Theory]
    [InlineData(-32013, "expired", """{"status":"expired"}""")]
    [InlineData(-32013, "bad_nonce", """{"status":"unavailable","error":"bad_nonce"}""")]
    [InlineData(-32002, null, """{"status":"stale"}""")]
    [InlineData(-32603, null, """{"status":"error","error":"backend_error"}""")]
    public async Task Host_errors_on_approve_map_to_short_statuses(int code, string? reason, string expected)
    {
        _host.ApproveError = new JsonObject
        {
            ["code"] = code, ["message"] = "free text \u202E from host",
            ["data"] = reason is null ? null : new JsonObject { ["reason"] = reason },
        };
        var reply = await Approve();
        Assert.Equal(expected, reply["result"]!.ToJsonString());
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("declined")]
    [InlineData("ineligible")]
    [InlineData("running")]
    public async Task Plans_not_awaiting_the_user_are_not_shown(string status)
    {
        _host.Current = Plan(status: status);
        var reply = await Approve();
        Assert.Equal("unavailable", reply["result"]!["status"]!.GetValue<string>());
        Assert.Empty(_prompt.Shown);
    }

    [Theory]
    [InlineData("""{"planId":"plan-1","planHash":"x"}""")]
    [InlineData("""{"planId":"plan-1","approved":true}""")]
    [InlineData("""{"planId":"op-1"}""")]
    [InlineData("""{"planId":"plan-"}""")]
    [InlineData("""{"planId":"plan-a b"}""")]
    [InlineData("""{"planId":"plan-1\u202E"}""")]
    [InlineData("""{"planId":7}""")]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task Rejects_bad_params_without_calling_the_host(string paramsJson)
    {
        var reply = await Approve(paramsJson);
        Assert.Equal(-32602, reply["error"]!["code"]!.GetValue<int>());
        Assert.Empty(_host.Calls);
    }

    [Theory]
    [InlineData("action.approve", """{"planId":"plan-abc123","planHash":"9f2c0a7d1e","nonce":"n"}""")]
    [InlineData("action.challenge", """{"planId":"plan-abc123"}""")]
    public async Task Renderer_cannot_approve_or_challenge(string method, string paramsJson)
    {
        var reply = JsonNode.Parse((await _dispatcher.HandleAsync(
            $$"""{"type":"rpc","id":2,"method":"{{method}}","params":{{paramsJson}}}""", Doc, true, default))!)!;
        Assert.Equal(-32012, reply["error"]!["code"]!.GetValue<int>());
        Assert.Empty(_host.Calls);
    }

    [Fact]
    public async Task Frames_and_other_origins_cannot_open_approval()
    {
        var msg = $$$"""{"type":"rpc","id":1,"method":"shell.approvePlan","params":{"planId":"{{{PlanId}}}"}}""";
        Assert.Null(await _dispatcher.HandleAsync(msg, Doc, isTopLevel: false, default));
        Assert.Null(await _dispatcher.HandleAsync(msg, "https://evil.example/", isTopLevel: true, default));
        Assert.Empty(_host.Calls);
    }

    [Fact]
    public async Task One_dialog_at_a_time_and_rate_limited()
    {
        _prompt.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Approve(id: 1);

        var busy = await Approve(id: 2);
        Assert.Equal("busy", busy["error"]!["data"]!["reason"]!.GetValue<string>());
        _prompt.Gate.SetResult();
        Assert.Equal("approved", (await first)["result"]!["status"]!.GetValue<string>());

        _prompt.Gate = null;
        _ticks += Stopwatch.Frequency / 2;
        var limited = await Approve(id: 3);

        Assert.Equal("rate_limited", limited["error"]!["data"]!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Injected_text_is_neutralized_before_display()
    {
        var plan = Plan();
        plan["label"] = "Switch\u202Eecnamrofrep\u202C plan\r\n\r\nApproved by admin\u200B\u0000";
        plan["rows"] = new JsonArray(new JsonObject
        {
            ["label"] = "Active\tplan", ["before"] = new string('A', 500), ["after"] = "Bal\u2066anced\u2069 &Approve",
        });
        plan["context"] = new JsonArray(new JsonArray("Note", "Click Approve now\u0007"), "\u202Bline\u202C");
        plan["tradeoffs"] = new JsonArray(Enumerable.Range(0, 20).Select(i => (JsonNode)JsonValue.Create($"t{i}")).ToArray());
        plan["requestedBy"] = new JsonObject { ["kind"] = "Microsoft Security Team" };
        _host.Current = plan;

        await Approve();
        var view = Assert.Single(_prompt.Shown);
        var all = new[] { view.Title, view.UndoLine, view.RequestedBy }
            .Concat(view.Context).Concat(view.Tradeoffs)
            .Concat(view.Rows.SelectMany(r => new[] { r.Label, r.Before, r.After }));
        foreach (var s in all)
        {
            Assert.DoesNotContain(s, ch => char.IsControl(ch) || ch is '\u202E' or '\u202C' or '\u202B' or '\u2066' or '\u2069' or '\u200B');
        }
        Assert.Equal("Switchecnamrofrep plan Approved by admin", view.Title);
        Assert.Equal("Active plan", view.Rows[0].Label);
        Assert.True(view.Rows[0].Before.Length <= PlanView.MaxCell);
        Assert.EndsWith("…", view.Rows[0].Before);
        Assert.Equal(PlanView.MaxTradeoffs, view.Tradeoffs.Count);
        Assert.Equal("Unknown", view.RequestedBy); // Only the fixed names; free text never becomes the requester.
    }

    [Theory]
    [InlineData("user", "You")]
    [InlineData("in_app", "the in-app assistant")]
    [InlineData("claude_code", "Claude Code")]
    [InlineData("codex", "Codex")]
    public void Requester_names(string kind, string name) => Assert.Equal(name, PlanView.RequesterName(kind));

    [Fact]
    public void Admin_and_no_undo_lines()
    {
        var plan = Plan();
        plan["privilege"] = "admin";
        plan["undo"] = new JsonObject { ["supported"] = false };
        var view = PlanView.From(plan)!;
        Assert.True(view.NeedsAdmin);
        Assert.Equal("Undo: no", view.UndoLine);
    }

    [Fact]
    public async Task Missing_expiry_or_hash_is_refused_before_showing()
    {
        var noExpiry = Plan();
        noExpiry.Remove("expiresAt");
        _host.Current = noExpiry;
        Assert.Equal("""{"status":"error","error":"bad_plan"}""", (await Approve())["result"]!.ToJsonString());

        _ticks += 5 * Stopwatch.Frequency;
        var noHash = Plan();
        noHash.Remove("planHash");
        _host.Current = noHash;
        Assert.Equal("""{"status":"error","error":"bad_plan"}""", (await Approve())["result"]!.ToJsonString());
        Assert.Empty(_prompt.Shown);
    }
}

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Tests;

/// <summary>A fake browser sign-in: opens the URL it built (or a hostile one), then stores a token in memory.</summary>
public sealed class FakeSignIn : IProviderSignIn
{
    public const string Token = "chatgpt-access-TOKEN-SECRET-0123456789";
    public string Kind => "chatgpt_plan";
    public Uri Issuer { get; } = new("https://auth.example.test");
    public bool IsSignedIn => _signedIn;
    public SignInResult Result { get; set; } = new(true);
    public string AuthorizationUrl { get; set; } = "https://auth.example.test/api/accounts/authorize?response_type=code&code_challenge=abc";
    public ConcurrentQueue<Uri> Opened { get; } = new();
    public int SignOuts;
    public TaskCompletionSource? Gate { get; set; }
    private volatile bool _signedIn;

    public async Task<SignInResult> SignInAsync(Func<Uri, bool> openBrowser, CancellationToken ct)
    {
        var url = new Uri(AuthorizationUrl);
        if (!openBrowser(url)) return new SignInResult(false, "browser");
        Opened.Enqueue(url);
        if (Gate is not null) await Gate.Task.WaitAsync(ct);
        if (Result.Ok) _signedIn = true;
        return Result;
    }

    public Task<ProviderCredential?> CredentialAsync(CancellationToken ct) =>
        Task.FromResult(_signedIn ? new ProviderCredential(Kind, Token) : null);

    public void SignOut()
    {
        Interlocked.Increment(ref SignOuts);
        _signedIn = false;
    }
}

/// <summary>A fake plan adapter that accepts only the fake token and reports whether tools work.</summary>
public sealed class FakePlanAdapter(bool tools, params Func<ProviderRequest, CancellationToken, Task<ProviderTurn>>[] steps) : IProviderAdapter, IToolCapability
{
    private readonly FakeProvider _inner = new("chatgpt_plan", steps);
    public string Kind => "chatgpt_plan";
    public string DisplayName => "ChatGPT";
    public string DefaultModel => "fake-plan-model";
    public ConcurrentQueue<ProviderRequest> Requests => _inner.Requests;
    public ConcurrentQueue<string> KeysSeen => _inner.KeysSeen;

    /// <summary>Set to make the check fail with this adapter code (e.g. not_eligible, network).</summary>
    public string? FailCode { get; set; }
    public ConcurrentQueue<string> ToolTestModels { get; } = new();

    public Task<ProviderCheck> CheckAsync(ProviderCredential credential, CancellationToken ct) =>
        Task.FromResult(FailCode is { } code ? new ProviderCheck(false, null, [], code + ": failed", code)
            : credential.ApiKey == FakeSignIn.Token ? new ProviderCheck(true, "acct", ["plan-model-a"], null, Model: "plan-model-a") : new ProviderCheck(false, null, [], "no"));
    public Task<ProviderTurn> RunTurnAsync(ProviderCredential credential, ProviderRequest request, CancellationToken ct) => _inner.RunTurnAsync(credential, request, ct);
    public Task<bool> SupportsToolsAsync(ProviderCredential credential, string model, CancellationToken ct)
    {
        ToolTestModels.Enqueue(model);
        return Task.FromResult(tools);
    }
}

/// <summary>Provider kind chatgpt_plan: browser sign-in in the host, §9 statuses, explain-only when tools fail, no token leaks.</summary>
public class ChatGptPlanTests
{
    private static (TestHost Host, ConcurrentQueue<Uri> Browser, ConcurrentQueue<string> Logs) Start(FakeSignIn signIn, FakePlanAdapter adapter, TimeSpan? signInTimeout = null)
    {
        var browser = new ConcurrentQueue<Uri>();
        var logs = new ConcurrentQueue<string>();
        var options = new AgentOptions
        {
            Providers = [adapter], SignIns = [signIn], Credentials = new MemoryCredentialStore(), Project = FakeProjection.Project,
            OpenBrowser = url => { browser.Enqueue(url); return true; },
            SignInTimeout = signInTimeout ?? new AgentOptions().SignInTimeout,
        };
        return (TestHost.StartAgent(options, FakeAudit.Modules(new FakeWorld(), new OptimizationTool.Engine.CollectorOptions(), sampler: false), log: logs.Enqueue), browser, logs);
    }

    private static async Task<JsonObject> Entry(Client c) =>
        (await c.Call("provider.list"))["providers"]!.AsArray().Single(p => p.Str("kind") == "chatgpt_plan")!.AsObject();

    private static List<string> Statuses(Client c) =>
        c.Events.Where(e => e.Str("type") == "provider.status" && e["payload"].Str("kind") == "chatgpt_plan").OrderBy(e => (long)e["seq"]!)
            .Select(e => e["payload"].Str("status") + (e["payload"]!["detail"] is null ? "" : "/" + e["payload"].Str("detail"))).ToList();

    private static async Task Until(Client c, int count)
    {
        for (var i = 0; i < 300 && Statuses(c).Count < count; i++) await Task.Delay(10);
        await Task.Delay(50);
    }

    [Fact]
    public async Task SignIn_ConnectsThroughTheBrowser_AndAnsweringUsesTheToken()
    {
        var signIn = new FakeSignIn();
        var adapter = new FakePlanAdapter(tools: true, FakeProvider.Step(FakeProvider.Say("Hello from the plan.")));
        var (host, browser, logs) = Start(signIn, adapter);
        var c = await host.ConnectAsync(minor: 4);
        var reportId = await AgentSetup.Report(c);

        var entry = await Entry(c);
        Assert.Equal(("ChatGPT", "browser_sign_in", "not_configured"), (entry.Str("label"), entry.Str("mode"), entry.Str("status")));

        var connecting = await c.Call("provider.connect", new JsonObject { ["kind"] = "chatgpt_plan" });
        Assert.Equal("checking", connecting.Str("status"));
        await Until(c, 2);
        Assert.Equal(["checking", "connected"], Statuses(c));
        Assert.Equal(signIn.AuthorizationUrl, Assert.Single(browser).AbsoluteUri);
        entry = await Entry(c);
        Assert.Equal(["plan-model-a"], entry["models"]!.AsArray().Select(x => (string)x!));
        // The account's own model (not the hardcoded default) is stored, feature-tested and used for tasks.
        Assert.Equal("plan-model-a", entry.Str("defaultModel"));
        Assert.Equal("plan-model-a", Assert.Single(adapter.ToolTestModels));
        Assert.True((bool)entry["capabilities"]!["tools"]!);
        SchemaTests.AssertValid("provider-list", await c.Call("provider.list"));

        var taskId = (await AgentSetup.StartTask(c, reportId, "chatgpt_plan")).Str("taskId");
        await AgentSetup.Ask(c, taskId);
        Assert.Equal("Hello from the plan.", (await AgentSetup.TaskEvent(c, taskId, "agent.message"))["payload"].Str("text"));
        Assert.Equal(FakeSignIn.Token, Assert.Single(adapter.KeysSeen));
        Assert.NotEmpty(adapter.Requests.Single().Tools);
        Assert.Equal("plan-model-a", adapter.Requests.Single().Model);

        await c.Call("provider.disconnect", new JsonObject { ["kind"] = "chatgpt_plan" });
        Assert.Equal(1, signIn.SignOuts);
        Assert.Equal("not_configured", (await Entry(c)).Str("status"));
        Assert.Equal("stopped", (await c.Call("agent.get", new JsonObject { ["taskId"] = taskId })).Str("status"));

        // The token never reaches events, logs or the database.
        var events = string.Join("\n", c.Events.Select(e => e.ToJsonString()));
        await host.StopAsync();
        var db = Directory.GetFiles(host.DataDir, "state.db*").SelectMany(File.ReadAllBytes).ToArray();
        Assert.DoesNotContain(FakeSignIn.Token, events);
        Assert.DoesNotContain(FakeSignIn.Token, string.Join("\n", logs));
        Assert.False(Contains(db, Encoding.UTF8.GetBytes(FakeSignIn.Token)));
        Assert.False(Contains(db, Encoding.Unicode.GetBytes(FakeSignIn.Token)));
        await host.DisposeAsync();
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return true;
        return false;
    }

    [Theory]
    [InlineData("not_eligible", "error/not_eligible")]
    [InlineData("access_denied", "error/cancelled")]
    [InlineData("network", "error/network")]
    [InlineData("timeout", "error/callback_timeout")]
    [InlineData("storage", "error/storage")]
    [InlineData("invalid", "error/unavailable")]
    public async Task FailedSignIns_ReportTheirDetail(string error, string expected)
    {
        var signIn = new FakeSignIn { Result = new SignInResult(false, error) };
        var (host, _, _) = Start(signIn, new FakePlanAdapter(tools: true));
        await using var _h = host;
        var c = await host.ConnectAsync(minor: 4);
        await c.Call("provider.connect", new JsonObject { ["kind"] = "chatgpt_plan" });
        await Until(c, 2);
        Assert.Equal(["checking", expected], Statuses(c));
        foreach (var e in c.Events) SchemaTests.AssertValid("event", e);
    }

    [Fact]
    public async Task Disconnect_CancelsAPendingSignIn_WhichCanNeverConnectAfterwards()
    {
        var gate = new TaskCompletionSource();
        var signIn = new FakeSignIn { Gate = gate }; // the browser is open; the callback has not arrived
        var (host, _, logs) = Start(signIn, new FakePlanAdapter(tools: true));
        await using var _h = host;
        var c = await host.ConnectAsync(minor: 4);
        await c.Call("provider.connect", new JsonObject { ["kind"] = "chatgpt_plan" });
        await BenchTests.WaitUntil(() => !signIn.Opened.IsEmpty);

        var after = await c.Call("provider.disconnect", new JsonObject { ["kind"] = "chatgpt_plan" });
        Assert.Equal("not_configured", after.Str("status")); // not left "checking" for the rest of the 3 minutes
        await BenchTests.WaitUntil(() => logs.Any(l => l.Contains("sign-in cancelled by disconnect")));
        gate.TrySetResult(); // the browser comes back late: nobody is listening any more
        Assert.False(signIn.IsSignedIn);
        Assert.Equal("not_configured", (await Entry(c)).Str("status"));
        Assert.DoesNotContain(Statuses(c), s => s.StartsWith("connected", StringComparison.Ordinal) || s.StartsWith("error", StringComparison.Ordinal));

        // The provider is not stuck: signing in again works.
        signIn.Gate = null;
        await c.Call("provider.connect", new JsonObject { ["kind"] = "chatgpt_plan" });
        await BenchTests.WaitUntil(() => Statuses(c).LastOrDefault() == "connected");
    }

    [Fact]
    public async Task Connected_IsPushedAsSoonAsTheBrowserComesBack_WithoutAPoll()
    {
        var gate = new TaskCompletionSource();
        var signIn = new FakeSignIn { Gate = gate }; // the user is still signing in on the website
        var (host, _, _) = Start(signIn, new FakePlanAdapter(tools: true));
        await using var _h = host;
        var c = await host.ConnectAsync(minor: 4);
        await c.Call("provider.connect", new JsonObject { ["kind"] = "chatgpt_plan" });
        await BenchTests.WaitUntil(() => !signIn.Opened.IsEmpty);
        await Task.Delay(300);
        Assert.Equal(["checking"], Statuses(c));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        gate.TrySetResult(); // the browser came back with the callback
        // Only events are watched here (no provider.list): the host pushes provider.status itself.
        await BenchTests.WaitUntil(() => Statuses(c).LastOrDefault() == "connected", timeoutMs: 5000);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
        Assert.Equal(["checking", "connected"], Statuses(c));
    }

    [Fact]
    public async Task ABrowserThatNeverComesBack_EndsAsCallbackTimeout_NotChecking()
    {
        var signIn = new FakeSignIn { Gate = new TaskCompletionSource() }; // the loopback callback never arrives
        var (host, _, _) = Start(signIn, new FakePlanAdapter(tools: true), signInTimeout: TimeSpan.FromMilliseconds(200));
        await using var _h = host;
        var c = await host.ConnectAsync(minor: 4);
        await c.Call("provider.connect", new JsonObject { ["kind"] = "chatgpt_plan" });
        await Until(c, 2);
        Assert.Equal(["checking", "error/callback_timeout"], Statuses(c));
    }

    [Theory]
    [InlineData("not_eligible", "error/not_eligible")]
    [InlineData("network", "error/network")]
    [InlineData("timeout", "error/network")]
    [InlineData("quota", "error/quota")]
    [InlineData("overloaded", "error/unavailable")]
    [InlineData("auth", "error/auth")]
    public async Task ACheckFailureAfterSignIn_KeepsItsCode(string code, string expected)
    {
        var (host, _, _) = Start(new FakeSignIn(), new FakePlanAdapter(tools: true) { FailCode = code });
        await using var _h = host;
        var c = await host.ConnectAsync(minor: 4);
        await c.Call("provider.connect", new JsonObject { ["kind"] = "chatgpt_plan" });
        await Until(c, 2);
        Assert.Equal(["checking", expected], Statuses(c));
    }

    [Fact]
    public async Task OnlyTheIssuersHttpsUrlIsOpened()
    {
        var signIn = new FakeSignIn { AuthorizationUrl = "https://evil.example.test/api/accounts/authorize" };
        var (host, browser, _) = Start(signIn, new FakePlanAdapter(tools: true));
        await using var _h = host;
        var c = await host.ConnectAsync(minor: 4);
        await c.Call("provider.connect", new JsonObject { ["kind"] = "chatgpt_plan" });
        await Until(c, 2);
        Assert.Empty(browser);
        Assert.Equal(["checking", "error/unavailable"], Statuses(c));

        signIn.AuthorizationUrl = "http://auth.example.test/api/accounts/authorize"; // not https
        await c.Call("provider.connect", new JsonObject { ["kind"] = "chatgpt_plan" });
        await Until(c, 4);
        Assert.Empty(browser);
    }

    [Fact]
    public async Task WithoutToolSupport_TasksAreExplainOnly()
    {
        var adapter = new FakePlanAdapter(tools: false, FakeProvider.Step(FakeProvider.Say("Here is what the report says.")));
        var (host, _, _) = Start(new FakeSignIn(), adapter);
        await using var _h = host;
        var c = await host.ConnectAsync(minor: 4);
        var reportId = await AgentSetup.Report(c);
        await c.Call("provider.connect", new JsonObject { ["kind"] = "chatgpt_plan" });
        await Until(c, 2);
        Assert.False((bool)(await Entry(c))["capabilities"]!["tools"]!);

        var refused = await c.Fails("agent.start", new JsonObject
        {
            ["idempotencyKey"] = "a", ["goal"] = "Check", ["recipientKind"] = "chatgpt_plan",
            ["scope"] = new JsonObject { ["reportId"] = reportId }, ["grant"] = AgentSetup.Grant(),
        });
        Assert.Equal("tools_unavailable", refused.ErrorData.Str("reason"));
        // m9 §17.1 review: an explicit grant asking only for administrator rights is refused the same way.
        var adminOnly = AgentSetup.Grant([]);
        adminOnly["admin"] = true;
        var adminRefused = await c.Fails("agent.start", new JsonObject
        {
            ["idempotencyKey"] = "a-admin", ["goal"] = "Check", ["recipientKind"] = "chatgpt_plan",
            ["scope"] = new JsonObject { ["reportId"] = reportId }, ["grant"] = adminOnly,
        });
        Assert.Equal("tools_unavailable", adminRefused.ErrorData.Str("reason"));

        var taskId = (await AgentSetup.StartTask(c, reportId, "chatgpt_plan", grant: AgentSetup.Grant([]))).Str("taskId");
        await AgentSetup.Ask(c, taskId);
        await AgentSetup.TaskEvent(c, taskId, "agent.message");
        Assert.Empty(adapter.Requests.Single().Tools); // explain-only: no tools offered
    }

    [Fact]
    public async Task ExplainOnly_DefaultsGrantNoTools_AndPermissionsSayToolsAreUnavailable()
    {
        var adapter = new FakePlanAdapter(tools: false);
        var (host, _, _) = Start(new FakeSignIn(), adapter);
        await using var _h = host;
        var c = await host.ConnectAsync(minor: 5);
        var reportId = await AgentSetup.Report(c);
        await c.Call("provider.connect", new JsonObject { ["kind"] = "chatgpt_plan" });
        await Until(c, 2);
        var onboarding = await c.Call("onboarding.get");
        await c.Call("onboarding.save", new JsonObject
        {
            ["expectedRevision"] = (long)onboarding["revision"]!, ["patch"] = new JsonObject { ["provider"] = new JsonObject { ["value"] = "chatgpt" } },
        });
        var saved = await c.Call("ai.setPermissions", new JsonObject
        {
            ["idempotencyKey"] = "p", ["revision"] = 0,
            ["permissions"] = new JsonObject
            {
                ["see"] = new JsonObject { ["specs"] = true, ["history"] = true, ["names"] = false },
                ["do"] = new JsonObject { ["checks"] = true, ["changes"] = "fix", ["scripts"] = false, ["web"] = true, ["admin"] = false },
            },
        });
        Assert.False((bool)saved["available"]!["tools"]!);
        Assert.Equal("fix", saved["permissions"]!["do"].Str("changes")); // the record keeps the user's answer

        // A task built from the record never exceeds the provider: no checks, changes or web, instead of tools_unavailable.
        var task = await c.Call("agent.start", new JsonObject
        {
            ["idempotencyKey"] = "t", ["goal"] = "Explain", ["recipientKind"] = "chatgpt_plan", ["scope"] = new JsonObject { ["reportId"] = reportId },
        });
        Assert.Empty(task["grant"]!["diagnostics"]!.AsArray());
        Assert.Equal("off", task["grant"].Str("changes"));
        Assert.False((bool)task["grant"]!["web"]!);
        Assert.False((bool)task["grant"]!["scripts"]!);
    }
}

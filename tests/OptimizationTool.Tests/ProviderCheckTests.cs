using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Tests;

/// <summary>A key store whose remembered (or every) save fails, as on a locked or roaming profile.</summary>
public sealed class FailingCredentialStore(bool failSessionToo) : ICredentialStore
{
    private readonly MemoryCredentialStore _inner = new();
    public void Save(string kind, string apiKey, bool remember)
    {
        if (remember || failSessionToo) throw new UnauthorizedAccessException("Access to the secrets folder is denied.");
        _inner.Save(kind, apiKey, remember);
    }
    public ProviderCredential? Load(string kind) => _inner.Load(kind);
    public void Delete(string kind) => _inner.Delete(kind);
    public bool Has(string kind) => _inner.Has(kind);
}

/// <summary>A model that accepts the fake key, fails with a typed code when told to, and lists its own models.</summary>
public sealed class CodedProvider(string kind, params Func<ProviderRequest, CancellationToken, Task<ProviderTurn>>[] steps) : IProviderAdapter
{
    private readonly FakeProvider _inner = new(kind, steps);
    public string Kind => kind;
    public string DisplayName => "Coded";
    public string DefaultModel => "default-model";
    public string? FailCode { get; set; }
    public bool Throw { get; set; }
    public string? Picked { get; set; } = "account-model";
    public int Checks;
    public ConcurrentQueue<ProviderRequest> Requests => _inner.Requests;

    public Task<ProviderCheck> CheckAsync(ProviderCredential credential, CancellationToken ct)
    {
        Interlocked.Increment(ref Checks);
        if (FailCode is { } code)
        {
            if (Throw) throw new ProviderException(code, "failed", retryable: false);
            return Task.FromResult(new ProviderCheck(false, null, [], code + ": failed", code));
        }
        return Task.FromResult(new ProviderCheck(true, null, ["account-model", "other-model"], null, Model: Picked));
    }

    public Task<ProviderTurn> RunTurnAsync(ProviderCredential credential, ProviderRequest request, CancellationToken ct) => _inner.RunTurnAsync(credential, request, ct);
}

/// <summary>m3 §9 checks: typed failure codes, storage failures, never stuck in checking, the account's own model.</summary>
public class ProviderCheckTests
{
    private static async Task<(TestHost Host, Client C)> Start(IProviderAdapter adapter, ICredentialStore? store = null)
    {
        var host = TestHost.StartAgent(AgentSetup.Options(store, adapter), FakeAudit.Modules(new FakeWorld(), new OptimizationTool.Engine.CollectorOptions(), sampler: false));
        return (host, await host.ConnectAsync());
    }

    private static async Task<JsonObject> Entry(Client c, string kind = "anthropic_api") =>
        (await c.Call("provider.list"))["providers"]!.AsArray().Single(p => p.Str("kind") == kind)!.AsObject();

    private static Task<JsonObject> SetKey(Client c, bool remember = true) =>
        c.Call("provider.setKey", new JsonObject { ["kind"] = "anthropic_api", ["apiKey"] = FakeProvider.GoodKey, ["remember"] = remember });

    [Theory]
    [InlineData("auth", "auth")]
    [InlineData("quota", "quota")]
    [InlineData("network", "network")]
    [InlineData("timeout", "network")]
    [InlineData("not_eligible", "not_eligible")]
    [InlineData("rate_limited", "unavailable")]
    [InlineData("overloaded", "unavailable")]
    [InlineData("incomplete", "unavailable")]
    [InlineData("bad_request", "unavailable")]
    public async Task CheckFailures_KeepTheirTypedCode(string code, string expected)
    {
        foreach (var thrown in new[] { false, true })
        {
            var adapter = new CodedProvider("anthropic_api") { FailCode = code, Throw = thrown };
            var (host, c) = await Start(adapter);
            await using var _h = host;
            var result = await SetKey(c);
            Assert.Equal(("error", expected, expected), (result.Str("status"), result.Str("detail"), result.Str("error")));
            var entry = await Entry(c);
            Assert.Equal(("error", expected), (entry.Str("status"), entry.Str("detail")));
        }
    }

    [Fact]
    public async Task ARememberFailure_KeepsTheKeyForThisSession()
    {
        var store = new FailingCredentialStore(failSessionToo: false);
        var (host, c) = await Start(new CodedProvider("anthropic_api"), store);
        await using var _h = host;
        Assert.Equal("connected", (await SetKey(c)).Str("status"));
        var entry = await Entry(c);
        Assert.Equal(("connected", "not_saved"), (entry.Str("status"), entry.Str("detail")));
        Assert.True(store.Has("anthropic_api"));
        foreach (var e in c.Events) SchemaTests.AssertValid("event", e);

        // A later Retry keeps saying so; a disconnect clears it.
        await c.Call("provider.connect", new JsonObject { ["kind"] = "anthropic_api" });
        Assert.Equal("not_saved", (await Entry(c)).Str("detail"));
        await c.Call("provider.disconnect", new JsonObject { ["kind"] = "anthropic_api" });
        Assert.Equal(("not_configured", null), ((await Entry(c)).Str("status"), (await Entry(c)).Str("detail")));
    }

    [Fact]
    public async Task ASaveFailure_EndsInStorage_NotChecking()
    {
        var (host, c) = await Start(new CodedProvider("anthropic_api"), new FailingCredentialStore(failSessionToo: true));
        await using var _h = host;
        var result = await SetKey(c);
        Assert.Equal(("error", "storage"), (result.Str("status"), result.Str("error")));
        var entry = await Entry(c);
        Assert.Equal(("error", "storage"), (entry.Str("status"), entry.Str("detail")));
        foreach (var e in c.Events) SchemaTests.AssertValid("event", e);
    }

    [Fact]
    public async Task TheAccountsListedModel_IsStoredAndUsedForTasks()
    {
        var adapter = new CodedProvider("anthropic_api", FakeProvider.Step(FakeProvider.Say("Hi.")));
        var (host, c) = await Start(adapter);
        await using var _h = host;
        var reportId = await AgentSetup.Report(c);
        await SetKey(c);
        Assert.Equal("account-model", (await Entry(c)).Str("defaultModel"));
        var task = await AgentSetup.StartTask(c, reportId);
        Assert.Equal("account-model", task["recipient"].Str("model"));
        await AgentSetup.Ask(c, task.Str("taskId"));
        await AgentSetup.TaskEvent(c, task.Str("taskId"), "agent.message");
        Assert.Equal("account-model", Assert.Single(adapter.Requests).Model);
    }

    [Fact]
    public async Task AfterARestart_TheModelIsLookedUpOnceBeforeTheFirstTurn()
    {
        var store = new MemoryCredentialStore();
        store.Save("anthropic_api", FakeProvider.GoodKey, remember: true); // remembered from an earlier run: no check yet
        var adapter = new CodedProvider("anthropic_api", FakeProvider.Step(FakeProvider.Say("One.")), FakeProvider.Step(FakeProvider.Say("Two.")));
        var (host, c) = await Start(adapter, store);
        await using var _h = host;
        var reportId = await AgentSetup.Report(c);
        var task = await AgentSetup.StartTask(c, reportId);
        Assert.Equal("default-model", task["recipient"].Str("model"));
        await AgentSetup.Ask(c, task.Str("taskId"));
        await AgentSetup.TaskEvent(c, task.Str("taskId"), "agent.message");
        Assert.Equal("account-model", Assert.Single(adapter.Requests).Model);
        Assert.Equal(1, adapter.Checks);
        Assert.Equal("account-model", (await Entry(c)).Str("defaultModel"));
    }
}

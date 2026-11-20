using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Tests;

/// <summary>
/// Research and the check-up follow the chosen AI (onboarding provider.kind): with both API keys saved and OpenAI
/// chosen, every research and check-up call goes to the OpenAI seam and none to Anthropic's. Fake seams only.
/// </summary>
public sealed partial class ResearchTests
{
    /// <summary>A fake in-app provider of any API kind that can research; counts its label and check-up calls.</summary>
    private sealed class KindProbe(string kind) : IProviderAdapter, IWebResearch
    {
        public string Kind => kind;
        public string DisplayName => "Fake " + kind;
        public string DefaultModel => "fake-model-1";
        public ConcurrentQueue<ResearchCall> Calls { get; } = new();
        public ConcurrentQueue<ResearchCall> CheckupCalls { get; } = new();

        public Task<ProviderCheck> CheckAsync(ProviderCredential credential, CancellationToken ct) =>
            Task.FromResult(new ProviderCheck(credential.ApiKey == FakeProvider.GoodKey, "acct", ["fake-model-1"], null));

        public Task<ProviderTurn> RunTurnAsync(ProviderCredential credential, ProviderRequest request, CancellationToken ct) =>
            Task.FromResult(FakeProvider.Say("No chat here."));

        public Task<ResearchAnswer> ResearchAsync(ProviderCredential credential, ResearchCall call, CancellationToken ct)
        {
            Assert.Equal(kind, credential.Kind);
            if (call.System == CheckupPolicy.Prompt)
            {
                CheckupCalls.Enqueue(call);
                return Task.FromResult(new ResearchAnswer("[]", [], 0, new ProviderUsage(1, 1)));
            }
            Calls.Enqueue(call);
            return Task.FromResult(ResearchProbe.Safe(ResearchProbe.SubjectOf(call)["name"]!.GetValue<string>()));
        }
    }

    [Fact]
    public async Task With_both_keys_and_OpenAI_chosen_research_and_the_checkup_use_OpenAI_only()
    {
        var anthropic = new KindProbe("anthropic_api");
        var openai = new KindProbe("openai_api");
        var o = AgentSetup.Options(null, anthropic, openai);
        await using var host = TestHost.StartAgent(new AgentOptions
        {
            Providers = o.Providers, Credentials = o.Credentials, Project = o.Project, RetryDelay = o.RetryDelay, FileIdentity = new FakeIdentity(),
            LocalIdentities = () => TestNames,
        }, [new Module(new World()), new PcModule(new Pc())]);
        var c = await host.ConnectAsync(minor: 6);
        await AgentSetup.SetKey(c, "anthropic_api");
        await AgentSetup.SetKey(c, "openai_api");
        var onboarding = await c.Call("onboarding.get");
        await c.Call("onboarding.save", new JsonObject
        {
            ["expectedRevision"] = (long)onboarding["revision"]!,
            ["patch"] = new JsonObject { ["provider"] = new JsonObject { ["value"] = "api", ["kind"] = "openai_api" } },
        });
        await SavePermissions(c, web: true);

        var (scan, _) = await c.ScanAsync("audit.full");
        var reportId = scan.Str("reportId");
        await CheckupState(c, reportId, p => CheckupEnds.Contains(p.Str("status")));

        var view = await Get(c, reportId);
        Assert.Equal("done", view.Str("status"));
        Assert.NotEmpty(openai.Calls);
        Assert.NotEmpty(openai.CheckupCalls);
        Assert.Empty(anthropic.Calls);
        Assert.Empty(anthropic.CheckupCalls);
        // ai.getPermissions.available.web is the chosen provider's: OpenAI's observed sources made it "yes".
        Assert.Equal("yes", (await c.Call("ai.getPermissions"))["available"].Str("web"));
    }

    [Fact]
    public async Task Changing_the_chosen_AI_cancels_a_running_run_at_once_and_its_late_result_stores_nothing()
    {
        await using var rig = await Start();
        var (reportId, release, _) = await Blocked(rig);
        var runId = (await Get(rig.C, reportId)).Str("runId");
        var onboarding = await rig.C.Call("onboarding.get");
        await rig.C.Call("onboarding.save", new JsonObject
        {
            ["expectedRevision"] = (long)onboarding["revision"]!,
            ["patch"] = new JsonObject { ["provider"] = new JsonObject { ["value"] = "claude-code", ["kind"] = "claude_code_mcp" } },
        });
        // Cancelled by the save itself, while the call is still blocked (not later by the fence).
        await State(rig.C, reportId, p => p.Str("runId") == runId && p.Str("status") == "cancelled");
        release.SetResult();
        await Task.Delay(300);
        Assert.Empty((await Get(rig.C, reportId))["items"]!.AsArray());
    }
}

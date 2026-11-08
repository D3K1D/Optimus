using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Host.Elevation;
using OptimizationTool.Windows.Actions.Firewall;

namespace OptimizationTool.Broker.Tests;

/// <summary>
/// Host side of the boundary over a real (unelevated, local) named pipe, with a fake launcher standing in for UAC
/// and a scripted fake broker. Nothing is elevated and the firewall is never touched.
/// </summary>
public sealed class BrokerElevationTests
{
    private static readonly string Self = Environment.ProcessPath!; // the "broker" image the fake client really runs as

    private static ActionExecution Execution(string mode = "enable") => new(
        "plan-1", "hash-1", FirewallPlan.Target(),
        mode == "enable" ? FirewallPlan.State(false, false) : FirewallPlan.State(true, true),
        mode == "enable" ? FirewallPlan.State(true, true) : FirewallPlan.State(false, false),
        mode == "enable" ? null : new JsonObject { ["mode"] = "disable" });

    /// <summary>Connects to the host's pipe like the broker would and runs <paramref name="script"/>.</summary>
    private sealed class FakeLauncher(Func<NamedPipeClientStream, string, Task>? script, bool cancel = false) : IBrokerLauncher
    {
        public string? Arguments { get; private set; }
        public Task? Running { get; private set; }

        public BrokerLaunch Launch(string brokerPath, string arguments)
        {
            Arguments = arguments;
            if (cancel) return new BrokerLaunch(false, true, 0);
            var parts = arguments.Split(' ');
            var parsed = BrokerArgs.Parse(parts) ?? throw new InvalidOperationException("bad args");
            if (script is not null)
                Running = Task.Run(async () =>
                {
                    using var pipe = new NamedPipeClientStream(".", parsed.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
                    await pipe.ConnectAsync(5000);
                    await script(pipe, parsed.Nonce);
                });
            return new BrokerLaunch(true, false, (uint)Environment.ProcessId);
        }
    }

    private static async Task Answer(NamedPipeClientStream pipe, string nonce, string state, bool echoNonce = true)
    {
        var req = BrokerRequest.Parse(await BrokerProtocol.ReadAsync(pipe, CancellationToken.None))!;
        Assert.Equal(nonce, req.Nonce);
        Assert.Equal(FirewallPlan.ContentHash(req.ActionId, req.Mode, req.Target, req.Before, req.After), req.ContentHash);
        var response = new BrokerResponse(echoNonce ? nonce : new string('f', 64), state, FirewallPlan.State(true, true), "done", null, null, "rcpt-0000000000000001");
        await BrokerProtocol.WriteAsync(pipe, response.ToJson(), CancellationToken.None);
    }

    [Fact]
    public async Task Verified_answer_maps_to_outcome_with_receipt()
    {
        var launcher = new FakeLauncher((p, n) => Answer(p, n, "verified"));
        var e = new BrokerElevation(Self, launcher, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var outcome = await e.RunAsync(FirewallPlan.ActionId, Execution(), CancellationToken.None);
        Assert.Equal("verified", outcome.State);
        Assert.Equal("rcpt-0000000000000001", (string?)outcome.Observed["receiptId"]);
        Assert.Matches("^--pipe OptimizationTool\\.Broker\\.[0-9a-f]{32} --nonce [0-9a-f]{64}$", launcher.Arguments);
        await launcher.Running!;
    }

    [Fact]
    public async Task Uac_cancelled_is_declined()
    {
        var e = new BrokerElevation(Self, new FakeLauncher(null, cancel: true));
        var outcome = await e.RunAsync(FirewallPlan.ActionId, Execution(), CancellationToken.None);
        Assert.Equal("declined", outcome.State);
        Assert.Equal("uac_cancelled", outcome.Code);
    }

    [Fact]
    public async Task No_broker_connection_is_indeterminate()
    {
        var e = new BrokerElevation(Self, new FakeLauncher(null), TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1));
        var outcome = await e.RunAsync(FirewallPlan.ActionId, Execution(), CancellationToken.None);
        Assert.Equal("indeterminate", outcome.State);
        Assert.Equal("broker_timeout", outcome.Code);
    }

    [Fact]
    public async Task Broker_that_never_answers_is_indeterminate()
    {
        var launcher = new FakeLauncher(async (p, _) => { await BrokerProtocol.ReadAsync(p, CancellationToken.None); await Task.Delay(1500); });
        var e = new BrokerElevation(Self, launcher, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(300));
        var outcome = await e.RunAsync(FirewallPlan.ActionId, Execution(), CancellationToken.None);
        Assert.Equal("indeterminate", outcome.State);
    }

    [Fact]
    public async Task Answer_with_wrong_nonce_is_indeterminate()
    {
        var launcher = new FakeLauncher((p, n) => Answer(p, n, "verified", echoNonce: false));
        var e = new BrokerElevation(Self, launcher, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var outcome = await e.RunAsync(FirewallPlan.ActionId, Execution(), CancellationToken.None);
        Assert.Equal("indeterminate", outcome.State);
        Assert.Equal("bad_response", outcome.Code);
    }

    [Fact]
    public async Task Client_that_is_not_the_broker_image_is_refused()
    {
        var launcher = new FakeLauncher(async (p, _) => { try { await BrokerProtocol.ReadAsync(p, CancellationToken.None); } catch (InvalidDataException) { } });
        var otherImage = Path.Combine(Path.GetTempPath(), "not-the-broker.exe");
        File.WriteAllBytes(otherImage, []);
        try
        {
            var e = new BrokerElevation(otherImage, launcher, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2));
            var outcome = await e.RunAsync(FirewallPlan.ActionId, Execution(), CancellationToken.None);
            Assert.Equal("failed", outcome.State);
            Assert.Equal("broker_mismatch", outcome.Code);
        }
        finally { File.Delete(otherImage); }
    }

    [Fact]
    public async Task Only_the_firewall_action_is_elevated()
    {
        var launcher = new FakeLauncher(null);
        var e = new BrokerElevation(Self, launcher);
        var outcome = await e.RunAsync("power.select_scheme", Execution(), CancellationToken.None);
        Assert.Equal("action_not_allowed", outcome.Code);
        Assert.Null(launcher.Arguments);
    }

    [Fact]
    public void Pipe_acl_denies_network_and_allows_only_user_and_admins()
    {
        var user = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var rules = BrokerElevation.Security(user).GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        Assert.Contains(rules, r => r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.NetworkSid, null)) && r.AccessControlType == System.Security.AccessControl.AccessControlType.Deny);
        Assert.Equal(2, rules.Count(r => r.AccessControlType == System.Security.AccessControl.AccessControlType.Allow));
    }
}

using System.Buffers.Binary;
using System.Text.Json.Nodes;
using OptimizationTool.Windows.Actions.Firewall;

namespace OptimizationTool.Broker.Tests;

/// <summary>The broker's checks, with fakes behind every Windows seam (no real firewall, receipts or elevation).</summary>
public sealed class BrokerSessionTests
{
    private readonly FakeFirewall _fw = new();
    private readonly FakeReceipts _receipts = new();
    private readonly FakeLedger _ledger = new();

    private async Task<(BrokerResult Result, BrokerResponse? Sent)> Run(JsonObject frame, PeerInfo? server = null, string? nonce = null)
    {
        var pipe = await Duplex.WithFrames(frame);
        var result = await Kit.Session(_fw, _receipts, _ledger, nonce).RunAsync(pipe, server ?? Kit.Host, CancellationToken.None);
        return (result, await pipe.Response());
    }

    [Fact]
    public async Task Enable_writes_once_reads_back_and_writes_protected_receipt()
    {
        var (r, sent) = await Run(Kit.Request().ToJson());
        Assert.True(r.Wrote);
        Assert.Equal("verified", r.Code);
        Assert.Equal(1, _fw.Writes);
        Assert.Equal("verified", sent!.State);
        Assert.Equal(Kit.Nonce(), sent.Nonce);
        Assert.True(FirewallPlan.Bool(sent.Observed["publicProfile"]!["localEnabled"]));
        var receipt = Assert.Single(_receipts.All);
        Assert.Equal(FirewallPlan.Field, (string?)receipt["field"]);
        Assert.Equal("planhash-1", (string?)receipt["planHash"]);
        Assert.Equal(sent.ReceiptId, (string?)receipt["receiptId"]);
        Assert.Contains(Kit.Nonce(), _ledger.Used);
    }

    [Fact]
    public async Task Write_that_does_not_take_is_failed_not_verified()
    {
        _fw.Ignore = true;
        var (r, sent) = await Run(Kit.Request().ToJson());
        Assert.Equal("failed", sent!.State);
        Assert.True(r.Wrote);
    }

    [Fact]
    public async Task Wrong_nonce_is_refused()
    {
        var (r, sent) = await Run(Kit.Request(nonce: Kit.Nonce('b')).ToJson());
        Assert.Equal("bad_nonce", r.Code);
        Assert.Equal("failed", sent!.State);
        Assert.Equal(0, _fw.Writes);
    }

    [Fact]
    public async Task Replayed_nonce_is_refused()
    {
        _ledger.Used.Add(Kit.Nonce());
        var (r, _) = await Run(Kit.Request().ToJson());
        Assert.Equal("replayed", r.Code);
        Assert.Equal(0, _fw.Writes);
    }

    [Fact]
    public async Task Swapped_plan_hash_is_refused()
    {
        var json = Kit.Request().ToJson();
        json["contentHash"] = new string('0', 64);
        var (r, _) = await Run(json);
        Assert.Equal("plan_mismatch", r.Code);
        Assert.Equal(0, _fw.Writes);
    }

    [Fact]
    public async Task Swapped_plan_fields_with_matching_hash_still_fail_the_live_recompute()
    {
        // A request that claims a different Before (and hashes it consistently) cannot match what the broker recomputes.
        var target = FirewallPlan.Target();
        var before = FirewallPlan.State(false, true);
        var after = FirewallPlan.State(true, true);
        var req = new BrokerRequest(Kit.Nonce(), FirewallPlan.ActionId, "enable", "plan-1", "h",
            FirewallPlan.ContentHash(FirewallPlan.ActionId, "enable", target, before, after), target, before, after);
        var (r, _) = await Run(req.ToJson());
        Assert.Equal("plan_mismatch", r.Code);
        Assert.Equal(0, _fw.Writes);
    }

    [Fact]
    public async Task Drift_since_approval_is_refused()
    {
        _fw.State = FakeFirewall.Snap(true, true); // someone already turned it on
        var (r, _) = await Run(Kit.Request().ToJson());
        Assert.Equal("already_on", r.Code);
        Assert.Equal(0, _fw.Writes);
    }

    [Fact]
    public async Task Ineligible_live_context_is_refused()
    {
        _fw.State = _fw.State with { RemoteSession = true };
        var (r, _) = await Run(Kit.Request().ToJson());
        Assert.Equal("remote_session", r.Code);
        Assert.Equal(0, _fw.Writes);
    }

    [Theory]
    [InlineData("power.select_scheme")]
    [InlineData("defender.quick_scan")]
    [InlineData("registry.set")]
    public async Task Wrong_action_is_refused(string actionId)
    {
        var (r, _) = await Run(Kit.Request(actionId: actionId).ToJson());
        Assert.Equal("action_not_allowed", r.Code);
        Assert.Equal(0, _fw.Writes);
    }

    [Fact]
    public async Task Other_profile_target_is_refused()
    {
        var json = Kit.Request().ToJson();
        json["target"] = new JsonObject { ["type"] = "firewall_profile", ["id"] = "private" };
        var (r, _) = await Run(json);
        Assert.Equal("bad_request", r.Code);
        Assert.Equal(0, _fw.Writes);
    }

    [Fact]
    public async Task Oversize_frame_is_refused_before_reading_it()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 10 * 1024 * 1024);
        var pipe = new Duplex(new MemoryStream(header));
        var r = await Kit.Session(_fw, _receipts, _ledger).RunAsync(pipe, Kit.Host, CancellationToken.None);
        Assert.Equal("bad_request", r.Code);
        Assert.Equal(0, _fw.Writes);
        Assert.Empty(_ledger.Used);
    }

    [Fact]
    public async Task Malformed_request_is_refused()
    {
        var (r, _) = await Run(new JsonObject { ["type"] = "request", ["nonce"] = Kit.Nonce() });
        Assert.Equal("bad_request", r.Code);
        Assert.Equal(0, _fw.Writes);
    }

    [Fact]
    public async Task Server_image_mismatch_gets_no_answer()
    {
        var fake = Kit.Host with { ImagePath = @"C:\Users\someone\Downloads\OptimizationTool.Host.exe" };
        var (r, sent) = await Run(Kit.Request().ToJson(), fake);
        Assert.Equal("server_mismatch", r.Code);
        Assert.Null(sent);
        Assert.Equal(0, _fw.Writes);
        Assert.Empty(_ledger.Used);
    }

    [Fact]
    public async Task Server_in_other_session_gets_no_answer()
    {
        var (r, sent) = await Run(Kit.Request().ToJson(), Kit.Host with { SessionId = 2 });
        Assert.Equal("session_mismatch", r.Code);
        Assert.Null(sent);
    }

    [Fact]
    public async Task Different_admin_account_is_refused()
    {
        var (r, sent) = await Run(Kit.Request().ToJson(), Kit.Host with { User = Kit.OtherAdmin });
        Assert.Equal("different_admin", r.Code);
        Assert.Equal("different_admin", sent!.Code);
        Assert.Equal(0, _fw.Writes);
    }

    [Fact]
    public async Task Undo_requires_our_protected_enable_receipt()
    {
        _fw.State = FakeFirewall.Snap(true, true);
        var (r, _) = await Run(Kit.Request("disable").ToJson());
        Assert.Equal("no_receipt", r.Code);
        Assert.Equal(0, _fw.Writes);
    }

    [Fact]
    public async Task Undo_with_receipt_restores_disabled()
    {
        _receipts.Write(new JsonObject { ["actionId"] = FirewallPlan.ActionId, ["mode"] = "enable", ["field"] = FirewallPlan.Field, ["state"] = "verified" });
        _fw.State = FakeFirewall.Snap(true, true);
        var (r, sent) = await Run(Kit.Request("disable").ToJson());
        Assert.Equal("verified", sent!.State);
        Assert.False(_fw.State.LocalEnabled);
        Assert.True(r.Wrote);
    }

    [Theory]
    [InlineData(new[] { "--pipe", "OptimizationTool.Broker.0123456789abcdef0123456789abcdef", "--nonce", "x" }, false)]
    [InlineData(new[] { "--pipe", "OtherPipe", "--nonce", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }, false)]
    [InlineData(new[] { "--pipe", "OptimizationTool.Broker.0123456789abcdef0123456789abcdef", "--nonce", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "--extra" }, false)]
    [InlineData(new[] { "--pipe", "OptimizationTool.Broker.0123456789abcdef0123456789abcdef", "--nonce", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }, true)]
    public void Args_are_strict(string[] args, bool ok) => Assert.Equal(ok, BrokerArgs.Parse(args) is not null);
}

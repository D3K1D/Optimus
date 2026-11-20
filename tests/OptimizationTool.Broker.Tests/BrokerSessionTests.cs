using System.Buffers.Binary;
using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Broker;
using OptimizationTool.Windows.Actions.Firewall;

namespace OptimizationTool.Broker.Tests;

/// <summary>The broker's checks (protocol v2, m9 §3.5), with fakes behind every Windows seam and the window.</summary>
public sealed class BrokerSessionTests
{
    private readonly Rig _rig = new("test.a", "test.b", "test.c");

    // ---------------------------------------------------------------- the firewall on the registry (m4 behaviour kept)

    [Fact]
    public async Task Enable_writes_once_reads_back_and_journals_before_the_write()
    {
        var (r, sent, _) = await _rig.Run(Kit.Request().ToJson());
        Assert.True(r.Wrote);
        Assert.Equal("verified", r.Code);
        Assert.Equal(1, _rig.Fw.Writes);
        var step = Assert.Single(sent!.Steps);
        Assert.Equal("verified", step.State);
        Assert.Equal(Kit.Nonce(), sent.Nonce);
        Assert.True(FirewallPlan.Bool(step.Observed["publicProfile"]!["localEnabled"]));
        Assert.Matches("^rcpt-[0-9a-f]{16}$", sent.ReceiptId);
        Assert.Contains(Kit.Nonce(), _rig.Ledger.Used);
        Assert.Equal(["confirm", "begin", "prepared:1", "write:firewall.enable_public", "outcome:1:verified"], _rig.Log.Events);
    }

    [Fact]
    public async Task Write_that_does_not_take_is_failed_not_verified()
    {
        _rig.Fw.Ignore = true;
        var (r, sent, _) = await _rig.Run(Kit.Request().ToJson());
        Assert.Equal("failed", sent!.Steps[0].State);
        Assert.True(r.Wrote);
    }

    [Fact]
    public async Task Wrong_nonce_is_refused()
    {
        var (r, sent, _) = await _rig.Run(Kit.Request(Kit.Nonce('b')).ToJson());
        Assert.Equal("bad_nonce", r.Code);
        Assert.Equal("failed", sent!.Steps[0].State);
        Assert.Equal(0, _rig.Fw.Writes);
        Assert.Empty(_rig.Confirm.Shown);
    }

    [Fact]
    public async Task Replayed_nonce_is_refused()
    {
        _rig.Ledger.Used.Add(Kit.Nonce());
        var (r, _, _) = await _rig.Run(Kit.Request().ToJson());
        Assert.Equal("replayed", r.Code);
        Assert.Equal(0, _rig.Fw.Writes);
        Assert.Empty(_rig.Confirm.Shown);
    }

    [Fact]
    public async Task A_nonce_ledger_that_cannot_be_locked_or_written_refuses_fail_closed()
    {
        _rig.Ledger.Fail = true;
        var (r, sent, _) = await _rig.Run(Kit.Request().ToJson());
        Assert.Equal("journal_failed", r.Code);
        Assert.Equal("failed", sent!.Steps[0].State);
        Assert.Equal(0, _rig.Fw.Writes);
        Assert.Empty(_rig.Confirm.Shown);
    }

    [Fact]
    public async Task Swapped_content_hash_is_refused()
    {
        var json = Kit.Request().ToJson();
        json["steps"]![0]!["contentHash"] = new string('0', 64);
        var (r, _, _) = await _rig.Run(json);
        Assert.Equal("plan_mismatch", r.Code);
        Assert.Equal(0, _rig.Fw.Writes);
    }

    [Fact]
    public async Task Claimed_fields_with_a_consistent_hash_still_fail_the_live_recompute()
    {
        // A request that claims a different Before (and hashes it consistently) can't match what the broker resolves.
        var step = Kit.Step(1, FirewallPlan.ActionId, FirewallPlan.HandlerVersion, FirewallPlan.Target(), FirewallPlan.State(false, true), FirewallPlan.State(true, true), null);
        var (r, _, _) = await _rig.Run(Kit.Request(null, step).ToJson());
        Assert.Equal("plan_mismatch", r.Code);
        Assert.Equal(0, _rig.Fw.Writes);
        Assert.Empty(_rig.Confirm.Shown);
    }

    [Fact]
    public async Task Other_handler_version_is_refused()
    {
        var step = Kit.Step(1, FirewallPlan.ActionId, "0.9.0", FirewallPlan.Target(), FirewallPlan.State(false, false), FirewallPlan.State(true, true), null);
        var (r, _, _) = await _rig.Run(Kit.Request(null, step).ToJson());
        Assert.Equal("version_mismatch", r.Code);
    }

    [Fact]
    public async Task Drift_since_approval_is_refused()
    {
        _rig.Fw.State = FakeFirewall.Snap(true, true); // someone already turned it on
        var (r, _, _) = await _rig.Run(Kit.Request().ToJson());
        Assert.Equal("already_on", r.Code);
        Assert.Equal(0, _rig.Fw.Writes);
    }

    [Fact]
    public async Task Ineligible_live_context_is_refused()
    {
        _rig.Fw.State = _rig.Fw.State with { RemoteSession = true };
        var (r, _, _) = await _rig.Run(Kit.Request().ToJson());
        Assert.Equal("remote_session", r.Code);
        Assert.Equal(0, _rig.Fw.Writes);
    }

    [Theory]
    [InlineData("power.select_scheme")]
    [InlineData("defender.quick_scan")]
    [InlineData("registry.set")]
    public async Task Action_without_a_broker_handler_is_refused(string actionId)
    {
        var step = Kit.Step(1, actionId, "1.0.0", FirewallPlan.Target(), FirewallPlan.State(false, false), FirewallPlan.State(true, true), null);
        var (r, _, _) = await _rig.Run(Kit.Request(null, step).ToJson());
        Assert.Equal("action_not_allowed", r.Code);
        Assert.Equal(0, _rig.Fw.Writes);
    }

    [Fact]
    public async Task Other_profile_target_is_refused()
    {
        var step = Kit.Step(1, FirewallPlan.ActionId, FirewallPlan.HandlerVersion, new JsonObject { ["type"] = "firewall_profile", ["id"] = "private" },
            FirewallPlan.State(false, false), FirewallPlan.State(true, true), null);
        var (r, _, _) = await _rig.Run(Kit.Request(null, step).ToJson());
        Assert.Equal("bad_request", r.Code);
        Assert.Equal(0, _rig.Fw.Writes);
    }

    [Fact]
    public async Task Oversize_frame_is_refused_before_reading_it()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 10 * 1024 * 1024);
        var pipe = new Duplex();
        pipe.PushRaw(header);
        var r = await _rig.Session().RunAsync(pipe, Kit.Host, CancellationToken.None);
        Assert.Equal("bad_request", r.Code);
        Assert.Equal(0, _rig.Fw.Writes);
        Assert.Empty(_rig.Ledger.Used);
    }

    [Fact]
    public async Task Malformed_or_v1_request_is_refused()
    {
        var (r, _, _) = await _rig.Run(new JsonObject { ["type"] = "request", ["nonce"] = Kit.Nonce() });
        Assert.Equal("bad_request", r.Code);
        var v1 = new JsonObject
        {
            ["type"] = "request", ["nonce"] = Kit.Nonce(), ["actionId"] = FirewallPlan.ActionId, ["mode"] = "enable", ["planId"] = "plan-1", ["planHash"] = "h",
            ["contentHash"] = new string('a', 64), ["target"] = FirewallPlan.Target(), ["before"] = FirewallPlan.State(false, false), ["after"] = FirewallPlan.State(true, true),
        };
        var (r1, _, _) = await new Rig().Run(v1);
        Assert.Equal("bad_request", r1.Code);
        Assert.Equal(0, _rig.Fw.Writes);
    }

    [Fact]
    public async Task Server_image_mismatch_gets_no_answer()
    {
        var fake = Kit.Host with { ImagePath = @"C:\Users\someone\Downloads\OptimizationTool.Host.exe" };
        var (r, sent, _) = await _rig.Run(Kit.Request().ToJson(), fake);
        Assert.Equal("server_mismatch", r.Code);
        Assert.Null(sent);
        Assert.Equal(0, _rig.Fw.Writes);
        Assert.Empty(_rig.Ledger.Used);
    }

    [Fact]
    public async Task Server_in_other_session_gets_no_answer()
    {
        var (r, sent, _) = await _rig.Run(Kit.Request().ToJson(), Kit.Host with { SessionId = 2 });
        Assert.Equal("session_mismatch", r.Code);
        Assert.Null(sent);
    }

    [Fact]
    public async Task Different_admin_account_is_refused()
    {
        var (r, sent, _) = await _rig.Run(Kit.Request().ToJson(), Kit.Host with { User = Kit.OtherAdmin });
        Assert.Equal("different_admin", r.Code);
        Assert.NotNull(sent);
        Assert.Equal(0, _rig.Fw.Writes);
    }

    // ---------------------------------------------------------------- undo against the broker's own record

    [Fact]
    public async Task Undo_without_the_brokers_own_record_is_refused()
    {
        _rig.Fw.State = FakeFirewall.Snap(true, true);
        var (r, _, _) = await _rig.Run(Kit.Request(null, Kit.Firewall(1, "disable")).ToJson());
        Assert.Equal("no_record", r.Code); // no undoOf: a preimage offered by the host alone
        var (r2, _, _) = await _rig.Run(Kit.Request(Kit.Nonce('c'), Kit.Firewall(1, "disable", new ElevatedUndoOf("rcpt-00000000000000ff", 1))).ToJson(), nonce: Kit.Nonce('c'));
        Assert.Equal("no_record", r2.Code); // names a record the broker doesn't have
        Assert.Equal(0, _rig.Fw.Writes);
    }

    [Fact]
    public async Task Undo_with_its_own_record_restores_disabled()
    {
        var (_, enabled, _) = await _rig.Run(Kit.Request().ToJson());
        var undoOf = new ElevatedUndoOf(enabled!.ReceiptId!, 1);
        var (r, sent, _) = await _rig.Run(Kit.Request(Kit.Nonce('d'), Kit.Firewall(1, "disable", undoOf)).ToJson(), nonce: Kit.Nonce('d'));
        Assert.Equal("verified", sent!.Steps[0].State);
        Assert.False(_rig.Fw.State.LocalEnabled);
        Assert.True(r.Wrote);
    }

    [Fact]
    public async Task Undo_naming_a_record_of_another_action_is_refused()
    {
        var (_, first, _) = await _rig.Run(Kit.Request(null, Kit.Test(1, "test.a", 0, 5)).ToJson());
        _rig.Fw.State = FakeFirewall.Snap(true, true);
        var (r, _, _) = await _rig.Run(Kit.Request(Kit.Nonce('e'), Kit.Firewall(1, "disable", new ElevatedUndoOf(first!.ReceiptId!, 1))).ToJson(), nonce: Kit.Nonce('e'));
        Assert.Equal("no_record", r.Code);
    }

    [Fact]
    public async Task Undo_of_a_value_changed_since_its_record_is_refused()
    {
        var (_, first, _) = await _rig.Run(Kit.Request(null, Kit.Test(1, "test.a", 0, 5)).ToJson());
        _rig.Test("test.a").Value = 7; // someone else changed it after Optimus wrote 5
        var undo = Kit.Step(1, "test.a", "1.0.0", Kit.TestTarget(), new JsonObject { ["value"] = 7 }, new JsonObject { ["value"] = 0 },
            new JsonObject { ["restore"] = 0 }, new ElevatedUndoOf(first!.ReceiptId!, 1));
        var (r, _, _) = await _rig.Run(Kit.Request(Kit.Nonce('f'), undo).ToJson(), nonce: Kit.Nonce('f'));
        Assert.Equal("no_record", r.Code);
        Assert.Equal(7, _rig.Test("test.a").Value);
    }

    // ---------------------------------------------------------------- several steps, one session

    [Fact]
    public async Task Several_steps_run_in_order_in_one_session_after_one_confirmation()
    {
        var req = Kit.Request(null, Kit.Test(1, "test.a", 0, 1), Kit.Test(2, "test.b", 0, 2), Kit.Firewall(3));
        var (r, sent, _) = await _rig.Run(req.ToJson());
        Assert.Equal(["verified", "verified", "verified"], sent!.Steps.Select(s => s.State));
        Assert.Single(_rig.Confirm.Shown);
        Assert.Equal(["confirm", "begin", "prepared:1", "write:test.a:1", "outcome:1:verified", "prepared:2", "write:test.b:2", "outcome:2:verified",
            "prepared:3", "write:firewall.enable_public", "outcome:3:verified"], _rig.Log.Events);
        Assert.Equal("verified", r.Code);
    }

    [Fact]
    public async Task Stops_at_the_first_step_that_is_not_verified()
    {
        _rig.Test("test.b").Force = "failed";
        var req = Kit.Request(null, Kit.Test(1, "test.a", 0, 1), Kit.Test(2, "test.b", 0, 2), Kit.Test(3, "test.c", 0, 3));
        var (_, sent, _) = await _rig.Run(req.ToJson());
        Assert.Equal(["verified", "failed", "skipped"], sent!.Steps.Select(s => s.State));
        Assert.Equal(0, _rig.Test("test.c").Writes);
        Assert.DoesNotContain("prepared:3", _rig.Log.Events);
    }

    [Fact]
    public async Task A_write_that_throws_is_indeterminate_and_stops()
    {
        _rig.Test("test.a").Force = "throw";
        var (_, sent, _) = await _rig.Run(Kit.Request(null, Kit.Test(1, "test.a", 0, 1), Kit.Test(2, "test.b", 0, 2)).ToJson());
        Assert.Equal(["indeterminate", "skipped"], sent!.Steps.Select(s => s.State));
    }

    [Fact]
    public async Task A_step_that_fails_its_checks_refuses_the_whole_request_before_anything_is_shown()
    {
        _rig.Test("test.b").Value = 2; // already set: ineligible
        var (r, sent, _) = await _rig.Run(Kit.Request(null, Kit.Test(1, "test.a", 0, 1), Kit.Test(2, "test.b", 0, 2)).ToJson());
        Assert.Equal("already_set", r.Code);
        Assert.All(sent!.Steps, s => Assert.Equal("failed", s.State));
        Assert.Empty(_rig.Confirm.Shown);
        Assert.Equal(0, _rig.Test("test.a").Writes);
    }

    [Fact]
    public async Task Protected_target_is_refused_by_the_broker_even_if_the_host_let_it_through()
    {
        _rig.Test("test.a").Protected = new OptimizationTool.Windows.Actions.Protection.Protection("hardware_limit", "A tuning tool.");
        var (r, _, _) = await _rig.Run(Kit.Request(null, Kit.Test(1, "test.a", 0, 1)).ToJson());
        Assert.Equal("hardware_limit", r.Code);
        Assert.Equal(0, _rig.Test("test.a").Writes);
    }

    [Fact]
    public async Task Journal_failure_writes_nothing()
    {
        _rig.Journal.FailPrepared = true;
        var (r, sent, _) = await _rig.Run(Kit.Request(null, Kit.Test(1, "test.a", 0, 1), Kit.Test(2, "test.b", 0, 2)).ToJson());
        Assert.Equal(["failed", "skipped"], sent!.Steps.Select(s => s.State));
        Assert.Equal("journal_failed", sent.Steps[0].Code);
        Assert.Equal(0, _rig.Test("test.a").Writes);
        Assert.False(r.Wrote);
    }

    [Fact]
    public async Task Drift_between_the_window_and_the_write_fails_that_step()
    {
        _rig.Test("test.a").OnWrite = () => _rig.Test("test.b").Value = 9; // step 2 changes while step 1 runs
        var (_, sent, _) = await _rig.Run(Kit.Request(null, Kit.Test(1, "test.a", 0, 1), Kit.Test(2, "test.b", 0, 2), Kit.Test(3, "test.c", 0, 3)).ToJson());
        Assert.Equal(["verified", "failed", "skipped"], sent!.Steps.Select(s => s.State));
        Assert.Equal("plan_mismatch", sent.Steps[1].Code);
        Assert.Equal(0, _rig.Test("test.b").Writes);
    }

    [Fact]
    public async Task Stop_frame_finishes_the_step_in_flight_and_starts_nothing_further()
    {
        Duplex? pipe = null;
        _rig.Test("test.a").OnWrite = () =>
        {
            pipe!.Push(BrokerStop.ToJson(Kit.Nonce())).GetAwaiter().GetResult();
            Thread.Sleep(200); // the watcher reads it while step 1 is in flight
        };
        var (r, sent, _) = await _rig.Run(Kit.Request(null, Kit.Test(1, "test.a", 0, 1), Kit.Test(2, "test.b", 0, 2)).ToJson(), arrange: p => pipe = p);
        Assert.Equal(["verified", "skipped"], sent!.Steps.Select(s => s.State));
        Assert.Contains("outcome:1:verified", _rig.Log.Events);
        Assert.Equal(0, _rig.Test("test.b").Writes);
        Assert.Equal("stopped", r.Code);
    }

    [Fact]
    public async Task Lost_pipe_finishes_and_journals_the_step_in_flight_then_exits_without_answering()
    {
        Duplex? pipe = null;
        _rig.Test("test.a").OnWrite = () =>
        {
            pipe!.HangUp();
            Thread.Sleep(200);
        };
        var (r, sent, _) = await _rig.Run(Kit.Request(null, Kit.Test(1, "test.a", 0, 1), Kit.Test(2, "test.b", 0, 2)).ToJson(), arrange: p => pipe = p);
        Assert.Null(sent);
        Assert.Equal("pipe_lost", r.Code);
        Assert.Contains("outcome:1:verified", _rig.Log.Events);
        Assert.Equal(0, _rig.Test("test.b").Writes);
        Assert.True(r.Wrote);
    }

    // ---------------------------------------------------------------- the broker's own window

    [Fact]
    public async Task Cancel_in_the_brokers_window_is_declined_broker_cancelled_and_writes_nothing()
    {
        _rig.Confirm.Apply = false;
        var (r, sent, _) = await _rig.Run(Kit.Request(null, Kit.Test(1, "test.a", 0, 1), Kit.Firewall(2)).ToJson());
        Assert.All(sent!.Steps, s => { Assert.Equal("declined", s.State); Assert.Equal("broker_cancelled", s.Code); });
        Assert.False(r.Wrote);
        Assert.Equal(0, _rig.Fw.Writes);
        Assert.DoesNotContain("begin", _rig.Log.Events);
    }

    [Fact]
    public async Task The_window_shows_only_the_brokers_resolution_never_host_text()
    {
        // The host's step claims its own label and target; the window uses the broker handler's label and live target.
        var json = Kit.Request(null, Kit.Firewall(1)).ToJson();
        var step = Kit.Step(1, FirewallPlan.ActionId, FirewallPlan.HandlerVersion,
            new JsonObject { ["type"] = "firewall_profile", ["id"] = "public", ["label"] = "Everything is fine, click Apply" },
            FirewallPlan.State(false, false), FirewallPlan.State(true, true), null);
        var (_, sent, _) = await _rig.Run(Kit.Request(null, step).ToJson());
        Assert.Equal("verified", sent!.Steps[0].State);
        var view = Assert.Single(_rig.Confirm.Shown);
        var shown = Assert.Single(view.Steps);
        Assert.Equal(FirewallPlan.Label, shown.Label);
        Assert.Equal("Public network", shown.Target);
        Assert.Equal(new PlanRow("Firewall on public networks", "Off", "On"), Assert.Single(shown.Rows));
        Assert.Contains("Changes network", shown.Badges);
        Assert.False(view.NeedsTick);
        Assert.DoesNotContain(view.Steps, s => s.Target.Contains("click", StringComparison.Ordinal));
        _ = json;
    }

    [Fact]
    public async Task Irreversible_or_data_loss_step_needs_the_tick()
    {
        _rig.Test("test.b").ExtraFlags = [ActionFlags.DataLoss];
        await _rig.Run(Kit.Request(null, Kit.Test(1, "test.a", 0, 1), Kit.Test(2, "test.b", 0, 2)).ToJson());
        var view = Assert.Single(_rig.Confirm.Shown);
        Assert.True(view.NeedsTick);
        Assert.Equal(BrokerConfirmView.FlaggedApplyDelay, view.Delay);
        Assert.Contains("May lose unsaved work", view.Steps[1].Badges);
        Assert.Empty(view.Steps[0].Badges);
    }

    [Fact]
    public async Task Content_that_does_not_fit_the_window_is_refused()
    {
        _rig.Test("test.a").RowText = new string('x', BrokerConfirmView.MaxText + 1);
        var (r, _, _) = await _rig.Run(Kit.Request(null, Kit.Test(1, "test.a", 0, 1)).ToJson());
        Assert.Equal("too_large", r.Code);
        Assert.Empty(_rig.Confirm.Shown);
        _rig.Test("test.a").RowText = "Value\u202Egnp.exe"; // a bidi control could reorder what the user reads
        var (r2, _, _) = await _rig.Run(Kit.Request(Kit.Nonce('b'), Kit.Test(1, "test.a", 0, 1)).ToJson(), nonce: Kit.Nonce('b'));
        Assert.Equal("too_large", r2.Code);
        Assert.Equal(0, _rig.Test("test.a").Writes);
    }

    [Theory]
    [InlineData(new[] { "--pipe", "OptimizationTool.Broker.0123456789abcdef0123456789abcdef", "--nonce", "x" }, false)]
    [InlineData(new[] { "--pipe", "OtherPipe", "--nonce", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }, false)]
    [InlineData(new[] { "--pipe", "OptimizationTool.Broker.0123456789abcdef0123456789abcdef", "--nonce", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "--extra" }, false)]
    [InlineData(new[] { "--pipe", "OptimizationTool.Broker.0123456789abcdef0123456789abcdef", "--nonce", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }, true)]
    public void Args_are_strict(string[] args, bool ok) => Assert.Equal(ok, BrokerArgs.Parse(args) is not null);
}

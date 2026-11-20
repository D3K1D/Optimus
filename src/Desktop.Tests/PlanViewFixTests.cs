using System.Text.Json.Nodes;
using System.Windows.Forms;
using OptimizationTool.Desktop.Bridge;

namespace OptimizationTool.Desktop.Tests;

/// <summary>
/// m9-ai-powers.md §4: the native dialog's view of fix plans (numbered steps, badges, the AI's quoted reason, caps that
/// refuse rather than cut), the tick rule for flagged plans, and the shell's not_ticked answer. No host, no PC.
/// </summary>
public sealed class PlanViewFixTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static JsonObject Step(int n, string label, string[]? flags = null, string privilege = "none", int rows = 1, string? why = null) => new()
    {
        ["n"] = n, ["actionId"] = "setting.set", ["handlerVersion"] = "1.0.0", ["label"] = label, ["what"] = "Sets one listed Windows setting.",
        ["target"] = new JsonObject { ["type"] = "setting", ["id"] = "x" }, ["before"] = new JsonObject(), ["after"] = new JsonObject(), ["parameters"] = null,
        ["rows"] = new JsonArray([.. Enumerable.Range(0, rows).Select(i => (JsonNode?)new JsonObject { ["label"] = $"Row {i}", ["before"] = "On", ["after"] = "Off" })]),
        ["context"] = new JsonArray(), ["tradeoffs"] = new JsonArray(), ["privilege"] = privilege,
        ["flags"] = new JsonArray([.. (flags ?? []).Select(f => (JsonNode?)f)]),
        ["undo"] = new JsonObject { ["supported"] = true, ["conditions"] = null }, ["resources"] = new JsonArray(), ["contentHash"] = new string('a', 64), ["why"] = why,
    };

    private static JsonObject Fix(params JsonObject[] steps)
    {
        var flags = steps.SelectMany(s => s["flags"]!.AsArray().Select(f => f!.GetValue<string>())).Distinct();
        return new JsonObject
        {
            ["planId"] = "plan-0123456789abcdef", ["kind"] = "fix", ["actionId"] = null, ["label"] = null, ["handlerVersion"] = null,
            ["title"] = "Quieter startup", ["why"] = new JsonObject { ["text"] = "Two apps‮ open with Windows.", ["by"] = "Claude" },
            ["target"] = null, ["before"] = null, ["after"] = null, ["rows"] = new JsonArray(), ["context"] = new JsonArray(), ["tradeoffs"] = new JsonArray(),
            ["privilege"] = steps.Any(s => s["privilege"]!.GetValue<string>() == "admin") ? "admin" : "none",
            ["flags"] = new JsonArray([.. flags.Select(f => (JsonNode?)f)]), ["steps"] = new JsonArray([.. steps.Select(s => (JsonNode?)s)]),
            ["undo"] = new JsonObject { ["supported"] = true, ["conditions"] = null }, ["resources"] = new JsonArray(),
            ["requestedBy"] = new JsonObject { ["kind"] = "in_app" }, ["undoOf"] = null, ["planHash"] = "h", ["expiresAt"] = T0.AddMinutes(5).ToString("O"),
            ["status"] = "awaiting_user", ["ineligible"] = null,
        };
    }

    [Fact]
    public void A_fix_plan_shows_its_title_the_quoted_reason_and_numbered_steps()
    {
        var view = PlanView.From(Fix(Step(1, "Turn off Spotify at startup", why: "Opens at every sign-in."), Step(2, "Turn off Windows tips", rows: 4)))!;
        Assert.True(view.IsFix);
        Assert.Equal("Quieter startup", view.Title);
        Assert.Equal("From Claude: Two apps open with Windows.", view.Why); // the bidi override is gone
        Assert.Equal("Claude", view.RequestedBy); // the host's AI name on the reason, as the cards
        Assert.Equal([1, 2], view.Steps.Select(s => s.N));
        Assert.Equal("Turn off Spotify at startup", view.Steps[0].Label);
        Assert.Equal("Sets one listed Windows setting.", view.Steps[0].What);
        Assert.Equal("Opens at every sign-in.", view.Steps[0].Why);
        Assert.Null(view.Steps[1].Why);
        Assert.Equal(4, view.Steps[1].Rows.Count);
        Assert.Equal(("Row 0", "On", "Off"), view.Steps[0].Rows[0]);
        Assert.False(view.NeedsTick);
        Assert.Equal(PlanView.ApproveDelay, view.Delay);
        Assert.Equal(TimeSpan.FromMilliseconds(800), view.Delay);
    }

    [Fact]
    public void Badges_come_from_flags_and_privilege_in_a_fixed_order()
    {
        Assert.Empty(PlanView.BadgesOf(new JsonArray(), admin: false));
        Assert.Equal(["Admin"], PlanView.BadgesOf(null, admin: true));
        Assert.Equal(["Admin", "Can’t be undone", "May lose unsaved work", "Runs a program", "Restart needed", "Sign-out needed", "Changes network", "Takes a while"],
            PlanView.BadgesOf(new JsonArray("long", "network", "sign_out", "restart", "runs_code", "data_loss", "irreversible"), admin: true));
        Assert.Equal(["May lose unsaved work"], PlanView.BadgesOf(new JsonArray("data_loss", "made_up"), admin: false));
        var view = PlanView.From(Fix(Step(1, "Empty the Recycle Bin", ["irreversible", "data_loss"]), Step(2, "Slow down SysMain", privilege: "admin")))!;
        Assert.Equal(["Can’t be undone", "May lose unsaved work"], view.Steps[0].Badges);
        Assert.Equal(["Admin"], view.Steps[1].Badges);
    }

    [Theory]
    [InlineData("irreversible", true)]
    [InlineData("data_loss", true)]
    [InlineData("restart", false)]
    [InlineData("network", false)]
    public void The_tick_rule_follows_irreversible_and_data_loss(string flag, bool tick)
    {
        var fix = PlanView.From(Fix(Step(1, "One", [flag])))!;
        Assert.Equal(tick, fix.NeedsTick);
        Assert.Equal(tick ? TimeSpan.FromSeconds(3) : TimeSpan.FromMilliseconds(800), fix.Delay);
        // A single plan follows the same rule, and shows what and its badges.
        var single = PlanView.From(new JsonObject
        {
            ["label"] = "End a process", ["what"] = "Ends one of your own processes.", ["privilege"] = "none", ["flags"] = new JsonArray(flag),
            ["requestedBy"] = new JsonObject { ["kind"] = "user" }, ["expiresAt"] = T0.AddMinutes(5).ToString("O"),
            ["why"] = new JsonObject { ["text"] = "It is stuck.", ["by"] = "Claude" },
        })!;
        Assert.Equal(tick, single.NeedsTick);
        Assert.Equal("Ends one of your own processes.", single.What);
        Assert.Equal("From Claude: It is stuck.", single.Why);
        Assert.Single(single.Badges);
    }

    [Fact]
    public void Over_a_cap_the_view_is_refused_never_cut()
    {
        Assert.NotNull(PlanView.From(Fix([.. Enumerable.Range(1, 6).Select(n => Step(n, "S" + n))])));
        Assert.Null(PlanView.From(Fix([.. Enumerable.Range(1, 7).Select(n => Step(n, "S" + n))])));
        Assert.Null(PlanView.From(Fix(Step(1, "Too many rows", rows: 5))));
        Assert.Null(PlanView.From(Fix(Step(1, "Long why", why: new string('w', 401)))));
        Assert.NotNull(PlanView.From(Fix(Step(1, "Why at the cap", why: new string('w', 400)))));
        var longWhy = Fix(Step(1, "One"));
        longWhy["why"] = new JsonObject { ["text"] = new string('w', 401), ["by"] = "Claude" };
        Assert.Null(PlanView.From(longWhy));
        var noSteps = Fix(Step(1, "One"));
        noSteps["steps"] = new JsonArray();
        Assert.Null(PlanView.From(noSteps));
        var undo = Fix(Step(1, "Undo: Turn off Windows tips"));
        undo["title"] = null;
        undo["why"] = null;
        undo["undoOf"] = "op-0123456789abcdef";
        var undoView = PlanView.From(undo)!;
        Assert.Equal("Undo changes", undoView.Title);
        Assert.Null(undoView.Why);
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var t = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void The_dialog_shows_every_step_and_requires_the_tick_on_a_flagged_plan() => RunSta(() =>
    {
        var now = DateTimeOffset.UtcNow;
        var plan = Fix(Step(1, "Turn off Windows tips", why: "They pop up."), Step(2, "Empty the Recycle Bin", ["irreversible", "data_loss"]), Step(3, "Slow down SysMain", privilege: "admin"));
        plan["expiresAt"] = now.AddMinutes(5).ToString("O");
        using var d = new PlanApprovalDialog(PlanView.From(plan)!, () => now);
        var labels = d.LabelTexts.ToList();
        Assert.Contains("Quieter startup", labels);
        Assert.Contains("From Claude: Two apps open with Windows.", labels);
        Assert.Contains("1. Turn off Windows tips", labels);
        Assert.Contains("2. Empty the Recycle Bin", labels);
        Assert.Contains("3. Slow down SysMain", labels);
        Assert.Contains("Can’t be undone · May lose unsaved work", labels);
        Assert.Contains("Admin", labels);
        Assert.Contains("Why: They pop up.", labels);
        Assert.Contains("Step 2 can’t be undone. Step 2 may lose unsaved work.", labels);
        Assert.True(d.Scroller.AutoScroll);
        var shot = Environment.GetEnvironmentVariable("OT_DIALOG_SNAPSHOT_DIR");
        if (!string.IsNullOrEmpty(shot))
        {
            d.StartPosition = FormStartPosition.Manual;
            d.Location = new System.Drawing.Point(-3000, -3000);
            d.Show();
            Application.DoEvents();
            using var bmp = new System.Drawing.Bitmap(d.Width, d.Height);
            d.DrawToBitmap(bmp, new System.Drawing.Rectangle(System.Drawing.Point.Empty, d.Size));
            bmp.Save(Path.Combine(shot, "approve-fix.png"));
            d.Hide();
        }

        Assert.NotNull(d.TickBox);
        Assert.False(d.TickBox!.Checked);           // never pre-ticked
        Assert.Equal(PlanView.TickText, d.TickBox.Text);
        Assert.False(d.ApproveButton.Enabled);
        Assert.Equal(PlanDecision.NotTicked, d.Decision);
        d.ApproveButton.PerformClick();               // disabled and unticked: nothing happens
        Assert.Equal(PlanDecision.NotTicked, d.Decision);
        d.TickBox.Checked = true;
        d.Tick();
        Assert.Equal(PlanDecision.Decline, d.Decision); // ticked, then declined: a plain decline
        d.TickBox.Checked = false;
        d.DeclineButton.PerformClick();
        Assert.Equal(PlanDecision.NotTicked, d.Decision);
    });

    [Fact]
    public void An_unflagged_plan_has_no_tick_and_no_banner() => RunSta(() =>
    {
        var now = DateTimeOffset.UtcNow;
        var plan = Fix(Step(1, "Turn off Windows tips"));
        plan["expiresAt"] = now.AddMinutes(5).ToString("O");
        using var d = new PlanApprovalDialog(PlanView.From(plan)!, () => now);
        Assert.Null(d.TickBox);
        Assert.DoesNotContain(PlanView.CantUndo, d.LabelTexts);    // no red block, and the step can be undone
        Assert.Contains(PlanView.UndoAfterwards, d.LabelTexts);
        Assert.Equal(PlanDecision.Decline, d.Decision);
    });

    // ---------------------------------------------------------------- the shell's answer

    private sealed class Host(JsonObject plan) : IBackend
    {
        public readonly List<string> Calls = [];
        public Task<JsonObject> CallAsync(string method, JsonObject? p, CancellationToken ct)
        {
            Calls.Add(method);
            JsonNode result = method switch
            {
                "action.get" => plan.DeepClone(),
                "action.decline" => new JsonObject { ["status"] = "declined" },
                "action.challenge" => new JsonObject { ["nonce"] = "n-1" },
                _ => new JsonObject { ["operationId"] = "op-1" },
            };
            return Task.FromResult(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["result"] = result });
        }
    }

    private sealed class Prompt(PlanDecision decision) : IPlanApprovalPrompt
    {
        public Task<PlanDecision> ConfirmAsync(PlanView view, CancellationToken ct) => Task.FromResult(decision);
    }

    [Theory]
    [InlineData(true, PlanDecision.NotTicked, """{"status":"declined","error":"not_ticked"}""")]
    [InlineData(true, PlanDecision.Decline, """{"status":"declined"}""")]
    [InlineData(false, PlanDecision.NotTicked, """{"status":"declined"}""")]
    [InlineData(true, PlanDecision.Approve, """{"status":"approved","operationId":"op-1"}""")]
    public async Task A_flagged_plan_closed_without_the_tick_is_declined_not_ticked(bool flagged, PlanDecision decision, string expected)
    {
        var plan = Fix(Step(1, "Empty the Recycle Bin", flagged ? ["irreversible"] : []));
        plan["expiresAt"] = DateTimeOffset.UtcNow.AddMinutes(5).ToString("O");
        plan["planId"] = "plan-abc";
        var host = new Host(plan);
        var approvals = new PlanApproval(new Prompt(decision));
        var reply = JsonNode.Parse(await approvals.HandleAsync(1, new JsonObject { ["planId"] = "plan-abc" }, host, AppLog.Disabled, default))!;
        Assert.Equal(expected, reply["result"]!.ToJsonString());
        if (decision != PlanDecision.Approve) Assert.Equal(["action.get", "action.decline"], host.Calls);
    }
}

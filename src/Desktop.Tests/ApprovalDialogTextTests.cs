using System.Text.Json.Nodes;
using System.Windows.Forms;
using OptimizationTool.Desktop.Bridge;

namespace OptimizationTool.Desktop.Tests;

/// <summary>
/// The two shell approval dialogs' words and order (design README "Windows dialogs", v57, v62), checked on the pure builder
/// <see cref="ApprovalDialogText"/> over <see cref="PlanView"/>. No window is shown.
/// </summary>
public sealed class ApprovalDialogTextTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private static JsonObject Step(int n, string label, string[]? flags = null, string privilege = "none", JsonArray? rows = null, string? why = null,
        bool undo = true, string? conditions = null, string actionId = "setting.set", bool nothingToUndo = false, string[]? tradeoffs = null) => new()
    {
        ["n"] = n, ["actionId"] = actionId, ["handlerVersion"] = "1.0.0", ["label"] = label, ["what"] = "Sets one listed Windows setting.",
        ["target"] = new JsonObject { ["type"] = "setting", ["id"] = "x" }, ["before"] = new JsonObject(), ["after"] = new JsonObject(), ["parameters"] = null,
        ["rows"] = rows ?? new JsonArray(new JsonObject { ["label"] = "Tips", ["before"] = "On", ["after"] = "Off" }),
        ["context"] = new JsonArray(), ["tradeoffs"] = new JsonArray([.. (tradeoffs ?? []).Select(t => (JsonNode?)t)]), ["privilege"] = privilege,
        ["flags"] = new JsonArray([.. (flags ?? []).Select(f => (JsonNode?)f)]),
        ["undo"] = Undo(undo, conditions, nothingToUndo), ["resources"] = new JsonArray(), ["contentHash"] = new string('a', 64), ["why"] = why,
    };

    private static JsonObject Undo(bool supported, string? conditions, bool nothingToUndo)
    {
        var u = new JsonObject { ["supported"] = supported, ["conditions"] = conditions };
        if (nothingToUndo) u["nothingToUndo"] = true;
        return u;
    }

    private static PlanView Fix(params JsonObject[] steps) => PlanView.From(new JsonObject
    {
        ["planId"] = "plan-0123456789abcdef", ["kind"] = "fix", ["title"] = "Quieter startup",
        ["why"] = new JsonObject { ["text"] = "Two apps open with Windows.", ["by"] = "Claude" },
        ["privilege"] = "none", ["flags"] = new JsonArray(), ["steps"] = new JsonArray([.. steps.Select(s => (JsonNode?)s)]),
        ["requestedBy"] = new JsonObject { ["kind"] = "in_app" }, ["planHash"] = "h", ["expiresAt"] = T0.AddMinutes(5).ToString("O"), ["status"] = "awaiting_user",
    })!;

    private static PlanView Single(bool undo = true, string? conditions = null, string[]? flags = null, string privilege = "none", bool nothingToUndo = false,
        string kind = "claude_code", string? by = "Claude", JsonArray? tradeoffs = null) => PlanView.From(new JsonObject
    {
        ["label"] = "Switch power plan", ["what"] = "Switches Windows to another installed power plan, such as Balanced.",
        ["rows"] = new JsonArray(new JsonObject { ["label"] = "Active power plan", ["before"] = "High performance", ["after"] = "Balanced" }),
        ["context"] = new JsonArray(new JsonArray("Power", "On battery · 64%")),
        ["tradeoffs"] = tradeoffs ?? new JsonArray("Slightly lower peak speed", "Fans quieter"),
        ["privilege"] = privilege, ["flags"] = new JsonArray([.. (flags ?? []).Select(f => (JsonNode?)f)]),
        ["undo"] = Undo(undo, conditions, nothingToUndo),
        ["requestedBy"] = new JsonObject { ["kind"] = kind }, ["expiresAt"] = T0.AddMinutes(5).ToString("O"),
        ["why"] = by is null ? null : new JsonObject { ["text"] = "It runs on battery.", ["by"] = by },
    })!;

    private static IEnumerable<string> Texts(IEnumerable<DialogLine> lines) => lines.Select(l => l.Text);

    // ---------------------------------------------------------------- Approve a change

    [Fact]
    public void Title_bars_are_the_designed_ones()
    {
        Assert.Equal("Optimus · Approve a change", ApprovalDialogText.PlanTitleBar);
        Assert.Equal("Optimus · Approve a script", ApprovalDialogText.ScriptTitleBar);
    }

    [Fact]
    public void A_fix_reads_heading_asker_reason_then_numbered_steps_in_the_designed_order()
    {
        var view = Fix(Step(1, "Turn off tips", why: "They pop up."), Step(2, "Slow down SysMain", privilege: "admin", conditions: "You can undo this while SysMain is still set to Manual."));
        var lines = ApprovalDialogText.PlanBody(view);
        Assert.Equal(
        [
            (DialogLineKind.Heading, "Quieter startup"),
            (DialogLineKind.AskedBy, "Asked by Claude"),
            (DialogLineKind.Quote, "From Claude: Two apps open with Windows."),
            (DialogLineKind.StepName, "1. Turn off tips"),
            (DialogLineKind.What, "Sets one listed Windows setting."),
            (DialogLineKind.Row, "Tips: On → Off"),
            (DialogLineKind.Why, "Why: They pop up."),
            (DialogLineKind.Undo, "You can undo this afterwards."),
            (DialogLineKind.StepName, "2. Slow down SysMain"),
            (DialogLineKind.What, "Sets one listed Windows setting."),
            (DialogLineKind.Row, "Tips: On → Off"),
            (DialogLineKind.Badges, "Admin"),
            (DialogLineKind.Undo, "You can undo this while SysMain is still set to Manual."),
        ], lines.Select(l => (l.Kind, l.Text)));
        Assert.DoesNotContain(Texts(lines), t => t.StartsWith("Undo:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_reduced_fix_lists_the_steps_that_do_not_run_after_the_steps_and_before_the_restore_point_line_plainly()
    {
        // m9 §17.3, Design System v80: one unnumbered, plain line each, after the numbered steps and before "A restore point is
        // made first."; the steps the user switched off first, then those left out for administrator rights.
        var plan = new JsonObject
        {
            ["planId"] = "plan-0123456789abcdef", ["kind"] = "fix", ["title"] = "Quieter startup",
            ["why"] = null, ["privilege"] = "none", ["flags"] = new JsonArray(),
            ["steps"] = new JsonArray(Step(1, "Turn off tips"), Step(2, "Make a restore point", actionId: PlanView.RestorePointActionId)),
            ["removed"] = new JsonArray(new JsonObject { ["label"] = "Slow down SysMain" }),
            ["leftOut"] = new JsonArray(
                new JsonObject { ["actionId"] = "service.disable", ["label"] = "Turn off a service", ["what"] = "x", ["code"] = "admin_not_allowed" },
                new JsonObject { ["actionId"] = "firewall.enable_public", ["label"] = "Turn on Public firewall", ["what"] = "y", ["code"] = "admin_unavailable" }),
            ["requestedBy"] = new JsonObject { ["kind"] = "in_app" }, ["planHash"] = "h", ["expiresAt"] = T0.AddMinutes(5).ToString("O"), ["status"] = "awaiting_user",
        };
        var lines = ApprovalDialogText.PlanBody(PlanView.From(plan)!);
        var tail = lines.SkipWhile(l => l.Kind != DialogLineKind.LeftOut).Select(l => (l.Kind, l.Text)).ToList();
        Assert.Equal(
        [
            (DialogLineKind.LeftOut, "Slow down SysMain · Left out by you"),
            (DialogLineKind.LeftOut, "Turn off a service · Left out: needs administrator rights, which are off."),
            (DialogLineKind.LeftOut, "Turn on Public firewall · Left out: needs administrator rights, not available in this version yet."),
            (DialogLineKind.RestorePoint, "A restore point is made first."),
        ], tail);
        // After every numbered step; never a step name (bold) itself.
        var firstLeft = lines.ToList().FindIndex(l => l.Kind == DialogLineKind.LeftOut);
        Assert.True(lines.ToList().FindLastIndex(l => l.Kind == DialogLineKind.StepName) < firstLeft);
        Assert.DoesNotContain(lines, l => l.Kind == DialogLineKind.StepName && l.Text.Contains("Left out", StringComparison.Ordinal));
        // A plan with nothing left out has no such line.
        Assert.DoesNotContain(ApprovalDialogText.PlanBody(Fix(Step(1, "Turn off tips"), Step(2, "Two"))), l => l.Kind == DialogLineKind.LeftOut);
    }

    [Fact]
    public void A_plan_narrowed_twice_lists_every_step_switched_off_in_order()
    {
        // P2 review item 3: the host carries a narrowed parent's removed steps forward; the dialog shows each of them.
        var plan = new JsonObject
        {
            ["planId"] = "plan-0123456789abcdef", ["kind"] = "fix", ["title"] = "Quieter startup",
            ["why"] = null, ["privilege"] = "none", ["flags"] = new JsonArray(),
            ["steps"] = new JsonArray(Step(1, "Turn off tips")),
            ["removed"] = new JsonArray(new JsonObject { ["label"] = "Slow down SysMain" }, new JsonObject { ["label"] = "Hide widgets" }),
            ["requestedBy"] = new JsonObject { ["kind"] = "in_app" }, ["planHash"] = "h", ["expiresAt"] = T0.AddMinutes(5).ToString("O"), ["status"] = "awaiting_user",
        };
        var left = ApprovalDialogText.PlanBody(PlanView.From(plan)!).Where(l => l.Kind == DialogLineKind.LeftOut).Select(l => l.Text);
        Assert.Equal(["Slow down SysMain · Left out by you", "Hide widgets · Left out by you"], left);
    }

    [Fact]
    public void A_single_change_is_one_step_without_a_number_or_a_name_with_name_rows_for_context_then_the_tradeoff()
    {
        var lines = ApprovalDialogText.PlanBody(Single(privilege: "admin", flags: ["restart", "network"]));
        Assert.Equal(
        [
            "Switch power plan", "Asked by Claude", "From Claude: It runs on battery.",
            // design v62: one step has no number and no name (the heading says it)
            "Switches Windows to another installed power plan, such as Balanced.",
            "Active power plan: High performance → Balanced",
            "Power: On battery · 64%",                                         // the same before and after: label and value
            "Trade-offs:",                                                     // design v66: two or more → heading, one bullet each
            "• Slightly lower peak speed",
            "• Fans quieter",
            "Admin · Restart needed · Changes network",                       // badge words joined by " · "
            "You can undo this afterwards.",
        ], Texts(lines));
        Assert.Equal(DialogLineKind.What, lines[3].Kind);
        Assert.DoesNotContain(lines, l => l.Kind == DialogLineKind.StepName);
        Assert.Equal(3, lines.Count(l => l.Kind == DialogLineKind.Tradeoff));
    }

    [Theory]
    [InlineData("Plan", "A", "B", "Plan: A → B")]
    [InlineData("Plan", "A", "A", "Plan: A")]
    [InlineData("Plan", "A", "", "Plan: A")]
    public void Rows_show_before_and_after_or_one_value_when_they_are_the_same(string label, string before, string after, string expected) =>
        Assert.Equal(expected, ApprovalDialogText.Row(label, before, after));

    [Fact]
    public void Each_step_ends_with_its_undo_sentence_and_a_step_that_cannot_be_undone_is_red()
    {
        Assert.Equal(("You can undo this afterwards.", false), (Single().Steps[0].Undo, Single().Steps[0].UndoRed));
        var cond = Single(conditions: "You can undo this unless something else changes the power plan first.").Steps[0];
        Assert.Equal(("You can undo this unless something else changes the power plan first.", false), (cond.Undo, cond.UndoRed));
        var no = Single(undo: false);
        Assert.Equal(("This can’t be undone.", true), (no.Steps[0].Undo, no.Steps[0].UndoRed));
        var last = ApprovalDialogText.PlanBody(no)[^1];
        Assert.Equal((DialogLineKind.UndoRed, "This can’t be undone."), (last.Kind, last.Text));
    }

    [Fact]
    public void A_restore_point_step_brings_its_line_after_the_steps()
    {
        var with = ApprovalDialogText.PlanBody(Fix(Step(1, "Make a restore point", actionId: "restore_point.create", undo: false), Step(2, "Turn off tips")));
        Assert.Equal((DialogLineKind.RestorePoint, "A restore point is made first."), (with[^1].Kind, with[^1].Text));
        Assert.DoesNotContain("A restore point is made first.", Texts(ApprovalDialogText.PlanBody(Fix(Step(1, "Turn off tips")))));
    }

    [Fact]
    public void The_red_block_names_the_flagged_steps()
    {
        var four = Fix(Step(1, "A"), Step(2, "B"), Step(3, "C"), Step(4, "Empty the Recycle Bin", ["irreversible"], undo: false));
        Assert.Equal("Step 4 can’t be undone.", ApprovalDialogText.RedBlock(four));
        Assert.True(four.NeedsTick);
        Assert.Equal("This can’t be undone.", ApprovalDialogText.RedBlock(Fix(Step(1, "Empty the Recycle Bin", ["irreversible"], undo: false))));
        Assert.Equal("This can’t be undone.", ApprovalDialogText.RedBlock(Single(undo: false, flags: ["irreversible"])));
        Assert.Equal("Step 3 may lose unsaved work.", ApprovalDialogText.RedBlock(Fix(Step(1, "A"), Step(2, "B"), Step(3, "End a program", ["data_loss"]))));
        Assert.Equal("This may lose unsaved work.", ApprovalDialogText.RedBlock(Fix(Step(1, "End a program", ["data_loss"]))));
        Assert.Equal("Steps 2 and 4 can’t be undone. Step 3 may lose unsaved work.", ApprovalDialogText.RedBlock(
            Fix(Step(1, "A"), Step(2, "B", ["irreversible"]), Step(3, "C", ["data_loss"]), Step(4, "D", ["irreversible"]))));
        Assert.Equal("Steps 1, 2, 3 can’t be undone.", ApprovalDialogText.RedBlock(Fix(Step(1, "A", ["irreversible"]), Step(2, "B", ["irreversible"]), Step(3, "C", ["irreversible"]))));
        Assert.Null(ApprovalDialogText.RedBlock(Fix(Step(1, "A"), Step(2, "B"))));
        Assert.Equal("I understand this can’t be undone", PlanView.TickText);
    }

    [Theory]
    [InlineData(292, "This request ends in 4:52. If it ends, nothing changes.")]
    [InlineData(59.9, "This request ends in 0:59. If it ends, nothing changes.")]
    [InlineData(600, "This request ends in 10:00. If it ends, nothing changes.")]
    [InlineData(0, "This request ended. Nothing was changed.")]
    [InlineData(-5, "This request ended. Nothing was changed.")]
    public void The_expiry_line_counts_down_then_says_it_ended(double seconds, string expected) =>
        Assert.Equal(expected, ApprovalDialogText.Expiry(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(Keys.Enter, true)]
    [InlineData(Keys.Escape, true)]
    [InlineData(Keys.Enter | Keys.Shift, true)]
    [InlineData(Keys.Space, false)]
    [InlineData(Keys.Tab, false)]
    public void Esc_and_Enter_decline(Keys key, bool declines) => Assert.Equal(declines, ApprovalKeys.Declines(key));

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
    public void Both_dialogs_map_Enter_and_Esc_to_Decline_and_use_the_designed_titles_and_buttons() => RunSta(() =>
    {
        // Built, never shown.
        using var plan = new PlanApprovalDialog(Single(), () => T0);
        Assert.Equal(ApprovalDialogText.PlanTitleBar, plan.Text);
        Assert.Same(plan.DeclineButton, plan.AcceptButton);
        Assert.Same(plan.DeclineButton, plan.CancelButton);
        Assert.Equal(("Decline", "Approve"), (plan.DeclineButton.Text, plan.ApproveButton.Text));

        using var script = new ScriptApprovalDialog(PlanView.From(ScriptApprovalTests.Plan(expires: T0.AddMinutes(5)))!, () => T0, () => DateTime.UtcNow);
        Assert.Equal(ApprovalDialogText.ScriptTitleBar, script.Text);
        Assert.Same(script.DeclineButton, script.AcceptButton);
        Assert.Same(script.DeclineButton, script.CancelButton);
        Assert.Equal(("Decline", "Approve"), (script.DeclineButton.Text, script.ApproveButton.Text));
        Assert.Equal("This request ends in 5:00. If it ends, nothing changes.", script.CountdownText);
    });

    // ---------------------------------------------------------------- Approve a script

    [Fact]
    public void The_script_header_follows_the_designed_order_with_its_conditional_lines()
    {
        var view = PlanView.From(ScriptApprovalTests.Plan(elevated: true, bypass: true))!;
        Assert.Equal(
        [
            "Clear the DNS cache",
            "Asked by Claude",
            "From Claude: Some sites still open at their old address.",
            "Optimus can’t check what a script does and can’t undo it. No warning here doesn’t mean it is safe.",
            "Runs as administrator.",
            "Runs even though scripts are normally off on this PC.",
            "Found by Optimus:",
            "Line 2: deletes files or folders",
        ], Texts(ApprovalDialogText.ScriptHeader(view)));

        var plain = ScriptApprovalTests.Plan(elevated: false, bypass: false);
        plain["script"]!["guard"]!["warnings"] = new JsonArray();
        var lines = Texts(ApprovalDialogText.ScriptHeader(PlanView.From(plain)!)).ToList();
        Assert.DoesNotContain("Runs as administrator.", lines);
        Assert.DoesNotContain("Runs even though scripts are normally off on this PC.", lines);
        Assert.DoesNotContain("Found by Optimus:", lines);
        Assert.Equal("Optimus can’t check what a script does and can’t undo it. No warning here doesn’t mean it is safe.", lines[^1]);
    }

    [Fact]
    public void Each_part_has_the_designed_header_and_the_options_their_sub_lines()
    {
        var parts = new JsonArray(new JsonObject { ["fromLine"] = 1, ["toLine"] = 3, ["explains"] = "Clears and says so." });
        var s = PlanView.From(ScriptApprovalTests.Plan(parts: parts))!.Script!;
        Assert.Equal("Lines 4–6 · Claude’s explanation, not checked by Optimus", s.PartHeader(4, 6));
        Assert.Equal("Line 4 · Claude’s explanation, not checked by Optimus", s.PartHeader(4, 4));
        Assert.Contains("Lines 1–3 · Claude’s explanation, not checked by Optimus\r\n", s.BoxText());
        Assert.Equal("Read to the end to approve", ApprovalDialogText.ReadToEnd);
        Assert.Equal("Can’t be undone", ApprovalDialogText.ScriptRed);

        Assert.Equal("Make a restore point first", PlanView.RestorePointText);
        Assert.Equal("Covers Windows files and settings, not your files.", ApprovalDialogText.RestorePointSubLine(s));
        Assert.Equal("Restore points are off on this PC", ApprovalDialogText.RestorePointSubLine(PlanView.From(ScriptApprovalTests.Plan(rp: false, reason: "protection_off"))!.Script!));
        Assert.Equal("Not available in this version", ApprovalDialogText.RestorePointSubLine(PlanView.From(ScriptApprovalTests.Plan(rp: false, reason: "flag_off"))!.Script!));
        Assert.Equal("Let Claude see the last lines of output", PlanView.ShareText(s.AiName));
        Assert.Equal("Paths and names are removed where recognised.", PlanView.ShareNote);
    }

    [Fact]
    public void The_script_dialog_draws_the_lines_and_options_in_order() => RunSta(() =>
    {
        using var d = new ScriptApprovalDialog(PlanView.From(ScriptApprovalTests.Plan(expires: T0.AddMinutes(5)))!, () => T0, () => DateTime.UtcNow);
        var labels = d.LabelTexts.ToList();
        int At(string s) => labels.IndexOf(s);
        Assert.True(At("Clear the DNS cache") < At("Asked by Claude"));
        Assert.True(At("Asked by Claude") < At(PlanView.ScriptNotice));
        Assert.True(At(PlanView.ScriptNotice) < At("Found by Optimus:"));
        Assert.True(At("Found by Optimus:") < At("Line 2: deletes files or folders"));
        Assert.True(At("Line 2: deletes files or folders") < At("Read to the end to approve"));
        Assert.True(At("Read to the end to approve") < At(PlanView.RestorePointNote));
        Assert.True(At(PlanView.RestorePointNote) < At(PlanView.ShareNote));
        Assert.True(At(PlanView.ShareNote) < At("Can’t be undone"));
        Assert.Equal(("Make a restore point first", "Let Claude see the last lines of output", "I understand this can’t be undone"),
            (d.RestorePointBox.Text, d.ShareBox.Text, d.TickBox.Text));
        Assert.DoesNotContain(labels, l => l.StartsWith("Stop ends", StringComparison.Ordinal));
    });

    [Fact]
    public void The_script_dialog_ends_with_close_at_the_expiry() => RunSta(() =>
    {
        var utc = T0;
        using var d = new ScriptApprovalDialog(PlanView.From(ScriptApprovalTests.Plan(expires: T0.AddSeconds(2)))!, () => utc);
        utc = T0.AddSeconds(3);
        d.Tick();
        Assert.Equal(("This request ended. Nothing was changed.", "Close", PlanDecision.Expired), (d.CountdownText, d.DeclineButton.Text, d.Decision));
        Assert.False(d.ApproveButton.Enabled);
    });

    // ---------------------------------------------------------------- who asked

    [Theory]
    [InlineData("user", "Claude", null)]                       // design v62: started from a card, the line is left out
    [InlineData("in_app", "Claude", "Claude")]
    [InlineData("in_app", "ChatGPT", "ChatGPT")]
    [InlineData("in_app", null, "the assistant")]
    [InlineData("claude_code", null, "Claude")]
    [InlineData("in_app", "Microsoft Support", "the assistant")]   // free text never becomes the asker
    [InlineData("someone", null, "the assistant")]
    public void The_asker_is_named_as_the_cards_do(string kind, string? by, string? expected)
    {
        var plan = new JsonObject { ["requestedBy"] = new JsonObject { ["kind"] = kind } };
        if (by is not null) plan["why"] = new JsonObject { ["text"] = "x", ["by"] = by };
        Assert.Equal(expected, PlanView.AskerName(plan));
    }

    // ---------------------------------------------------------------- design v62

    [Fact]
    public void A_step_with_nothing_to_undo_has_no_undo_line_no_red_block_and_no_tick()
    {
        var single = Single(undo: false, nothingToUndo: true, flags: ["long"]);
        Assert.Equal(("", false), (single.Steps[0].Undo, single.Steps[0].UndoRed));
        var lines = ApprovalDialogText.PlanBody(single);
        Assert.DoesNotContain(lines, l => l.Kind is DialogLineKind.Undo or DialogLineKind.UndoRed);
        Assert.DoesNotContain(Texts(lines), t => t.Contains("undo", StringComparison.OrdinalIgnoreCase));
        Assert.False(single.NeedsTick);
        Assert.Null(ApprovalDialogText.RedBlock(single));
        Assert.Equal((DialogLineKind.Badges, "Takes a while"), (lines[^1].Kind, lines[^1].Text));

        var fix = Fix(Step(1, "Make a restore point", actionId: "restore_point.create", undo: false, nothingToUndo: true), Step(2, "Turn off tips"));
        Assert.Equal("", fix.Steps[0].Undo);
        Assert.Equal("You can undo this afterwards.", fix.Steps[1].Undo);
        Assert.Equal(["1. Make a restore point", "Sets one listed Windows setting.", "Tips: On → Off", "2. Turn off tips"],
            Texts(ApprovalDialogText.PlanBody(fix)).Skip(3).Take(4));
        Assert.False(fix.NeedsTick);
    }

    [Fact]
    public void Nothing_to_undo_never_hides_a_flagged_or_undoable_step()
    {
        // irreversible or data_loss changes something: the red line and the tick stay, whatever the host says.
        var flagged = Single(undo: false, nothingToUndo: true, flags: ["data_loss"]);
        Assert.Equal(("This can’t be undone.", true), (flagged.Steps[0].Undo, flagged.Steps[0].UndoRed));
        Assert.True(flagged.NeedsTick);
        Assert.Equal("This may lose unsaved work.", ApprovalDialogText.RedBlock(flagged));
        var step = Fix(Step(1, "Empty the Recycle Bin", ["irreversible"], undo: false, nothingToUndo: true), Step(2, "B"));
        Assert.Equal(("This can’t be undone.", true), (step.Steps[0].Undo, step.Steps[0].UndoRed));
        Assert.True(step.NeedsTick);
        // only with supported false
        Assert.Equal("You can undo this afterwards.", Single(undo: true, nothingToUndo: true).Steps[0].Undo);
        // not sent: the red sentence as before
        Assert.Equal(("This can’t be undone.", true), (Single(undo: false).Steps[0].Undo, Single(undo: false).Steps[0].UndoRed));
    }

    [Fact]
    public void Asked_by_the_assistant_when_no_AI_is_named_and_left_out_when_the_user_started_it()
    {
        var unnamed = ApprovalDialogText.PlanBody(Single(kind: "in_app", by: null));
        Assert.Equal((DialogLineKind.AskedBy, "Asked by the assistant"), (unnamed[1].Kind, unnamed[1].Text));
        Assert.DoesNotContain(Texts(unnamed), t => t.Contains("Assistant", StringComparison.Ordinal));

        var user = ApprovalDialogText.PlanBody(Single(kind: "user", by: null));
        Assert.DoesNotContain(user, l => l.Kind == DialogLineKind.AskedBy);
        Assert.Equal(["Switch power plan", "Switches Windows to another installed power plan, such as Balanced."], Texts(user).Take(2));
        Assert.Null(Single(kind: "user", by: null).RequestedBy);

        var script = ScriptApprovalTests.Plan();
        script["requestedBy"] = new JsonObject { ["kind"] = "in_app" };
        script["why"] = null;
        var view = PlanView.From(script)!;
        Assert.Equal("Asked by the assistant", ApprovalDialogText.ScriptHeader(view)[1].Text);
        Assert.Equal("Let the assistant see the last lines of output", PlanView.ShareText(view.Script!.AiName));
    }

    [Fact]
    public void The_tradeoff_comes_after_the_rows_and_before_the_badges_and_only_when_there_is_one()
    {
        var lines = ApprovalDialogText.PlanBody(Single(flags: ["restart"])).Select(l => l.Kind).ToList();
        var tradeoff = lines.IndexOf(DialogLineKind.Tradeoff);
        Assert.True(lines.LastIndexOf(DialogLineKind.Row) < tradeoff && tradeoff < lines.IndexOf(DialogLineKind.Badges));
        Assert.Equal("Trade-off: A", ApprovalDialogText.Tradeoff("A"));
        Assert.DoesNotContain(DialogLineKind.Tradeoff, ApprovalDialogText.PlanBody(Single(tradeoffs: new JsonArray())).Select(l => l.Kind));
    }

    [Fact]
    public void A_single_change_with_one_tradeoff_shows_one_trade_off_line()
    {
        var lines = ApprovalDialogText.PlanBody(Single(tradeoffs: new JsonArray("Slightly lower peak speed")));
        Assert.Equal(["Trade-off: Slightly lower peak speed"], lines.Where(l => l.Kind == DialogLineKind.Tradeoff).Select(l => l.Text));
    }

    [Fact]
    public void Every_step_of_a_fix_shows_its_own_tradeoffs_after_its_rows_and_before_its_badges()
    {
        var view = Fix(
            Step(1, "Turn off tips", ["restart"], tradeoffs: ["You won’t see tips"]),
            Step(2, "Slow down SysMain", ["restart"]),
            Step(3, "Stop a startup app", ["restart"], tradeoffs: ["It won’t open by itself when you sign in", "Features that rely on it running in the background may stop"]),
            Step(4, "Turn off a service", ["restart"], tradeoffs: ["A", "B", "C"]));
        var lines = ApprovalDialogText.PlanBody(view);
        Assert.Equal(
        [
            "1. Turn off tips", "Sets one listed Windows setting.", "Tips: On → Off", "Trade-off: You won’t see tips", "Restart needed", "You can undo this afterwards.",
            "2. Slow down SysMain", "Sets one listed Windows setting.", "Tips: On → Off", "Restart needed", "You can undo this afterwards.",
            "3. Stop a startup app", "Sets one listed Windows setting.", "Tips: On → Off",
            "Trade-offs:", "• It won’t open by itself when you sign in", "• Features that rely on it running in the background may stop",
            "Restart needed", "You can undo this afterwards.",
            "4. Turn off a service", "Sets one listed Windows setting.", "Tips: On → Off", "Trade-offs:", "• A", "• B", "• C", "Restart needed", "You can undo this afterwards.",
        ], Texts(lines.SkipWhile(l => l.Kind != DialogLineKind.StepName)));
        Assert.All(lines.Where(l => l.Text.StartsWith("Trade-off", StringComparison.Ordinal) || l.Text.StartsWith("• ", StringComparison.Ordinal)),
            l => Assert.Equal(DialogLineKind.Tradeoff, l.Kind));
        Assert.Equal(["You won’t see tips"], view.Steps[0].Tradeoffs!);
        Assert.Empty(view.Steps[1].Tradeoffs!);
    }

    [Fact]
    public void Tradeoff_lines_are_none_one_or_a_heading_with_one_bullet_each()
    {
        Assert.Empty(ApprovalDialogText.TradeoffLines(null));
        Assert.Empty(ApprovalDialogText.TradeoffLines([]));
        Assert.Equal(["Trade-off: A"], ApprovalDialogText.TradeoffLines(["A"]).Select(l => l.Text));
        Assert.Equal(["Trade-offs:", "• A", "• B"], ApprovalDialogText.TradeoffLines(["A", "B"]).Select(l => l.Text));
        Assert.Equal(["Trade-offs:", "• A", "• B", "• C"], ApprovalDialogText.TradeoffLines(["A", "B", "C"]).Select(l => l.Text));
    }

    [Fact]
    public void Tradeoff_bullets_are_one_level_in_under_their_label_and_every_other_line_is_not()
    {
        // design v67: "• …" indented under "Trade-offs:"; the label and a single "Trade-off: …" stay with the step's lines.
        Assert.Equal([0, 1, 1], ApprovalDialogText.TradeoffLines(["A", "B"]).Select(l => l.Indent));
        Assert.Equal([0], ApprovalDialogText.TradeoffLines(["A"]).Select(l => l.Indent));
        var lines = ApprovalDialogText.PlanBody(Single());
        Assert.All(lines, l => Assert.Equal(l.Text.StartsWith("• ", StringComparison.Ordinal) ? 1 : 0, l.Indent));
        Assert.Equal(2, lines.Count(l => l.Indent == 1));
    }

    [Fact]
    public void The_plan_dialog_draws_tradeoff_bullets_12px_in_under_the_label_with_no_extra_space_above() => RunSta(() =>
    {
        // Built, never shown.
        using var d = new PlanApprovalDialog(Single(), () => T0);
        var stack = (FlowLayoutPanel)d.Scroller.Controls[0];
        var labels = stack.Controls.OfType<Label>().ToList();
        var heading = labels.Single(l => l.Text == ApprovalDialogText.TradeoffsHeading);
        var bullets = labels.Where(l => l.Text.StartsWith("• ", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, bullets.Count);
        var row = labels.Single(l => l.Text.StartsWith("Active power plan:", StringComparison.Ordinal));
        var what = labels[labels.IndexOf(row) - 1];

        // The label stays aligned with the step's other lines.
        Assert.Equal(row.Left, heading.Left);
        Assert.Equal(what.Left, heading.Left);

        // The dialog's metrics scale together: the ordinary 3 px line margin gives the factor for the 12 px indent.
        var scale = heading.Margin.Top / 3.0;
        var indent = (int)Math.Round(PlanApprovalDialog.IndentStep * scale);
        Assert.Equal(12, PlanApprovalDialog.IndentStep);
        var previous = heading;
        foreach (var b in bullets)
        {
            Assert.Equal(heading.Left + indent, b.Left);
            Assert.Equal(heading.Margin.Top, b.Margin.Top);   // no extra margin above a bullet
            Assert.Equal(heading.Margin.Bottom, b.Margin.Bottom);
            Assert.Equal(previous.Bottom + previous.Margin.Bottom + b.Margin.Top, b.Top);
            Assert.True(b.Right <= heading.Left + heading.MaximumSize.Width, "the bullet wraps inside the column");
            previous = b;
        }
    });

    [Fact]
    public void The_plan_dialog_keeps_a_single_tradeoff_line_unindented() => RunSta(() =>
    {
        using var d = new PlanApprovalDialog(Single(tradeoffs: new JsonArray("Fans quieter")), () => T0);
        var labels = ((FlowLayoutPanel)d.Scroller.Controls[0]).Controls.OfType<Label>().ToList();
        var single = labels.Single(l => l.Text == "Trade-off: Fans quieter");
        var row = labels.Single(l => l.Text.StartsWith("Active power plan:", StringComparison.Ordinal));
        Assert.Equal(row.Left, single.Left);
        Assert.Equal(row.Margin, single.Margin);
    });

    [Fact]
    public void The_plan_dialog_draws_no_tick_for_a_step_with_nothing_to_undo() => RunSta(() =>
    {
        // Built, never shown.
        using var d = new PlanApprovalDialog(Single(undo: false, nothingToUndo: true), () => T0);
        Assert.Equal(PlanDecision.Decline, d.Decision);
    });
}

using System.Windows.Forms;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Engine.Actions.Scripts.Guard;

namespace OptimizationTool.Broker.Tests;

/// <summary>
/// The helper's "Administrator changes" window and its script variant (design README "Windows dialogs", v57, v62): words and order
/// from the views, Esc/Enter as Cancel. The windows are built but never shown.
/// </summary>
public sealed class BrokerDialogTextTests
{
    private static BrokerConfirmStep S(int n, string label, string target = "Public network", bool irr = false, bool loss = false, bool repair = false, params string[] badges) =>
        new(n, label, target, [new PlanRow("Firewall on public networks", "Off", "On"), new PlanRow("Profile", "Public", "Public")], badges, irr, loss, repair);

    [Fact]
    public void The_window_reads_heading_explanation_then_each_step()
    {
        var view = new BrokerConfirmView([S(1, "Turn on the public firewall", badges: "Changes network"), S(2, "Check Windows files", "Windows", repair: true, badges: "Takes a while")], false);
        Assert.Equal(
        [
            (BrokerLineKind.Heading, "Make these changes as administrator?"),
            (BrokerLineKind.Explanation, "You approved these in Optimus. Windows asked for permission because they need administrator rights."),
            (BrokerLineKind.StepName, "1. Turn on the public firewall"),
            (BrokerLineKind.Target, "Changes: Public network"),
            (BrokerLineKind.Row, "Firewall on public networks: Off → On"),
            (BrokerLineKind.Row, "Profile: Public"),
            (BrokerLineKind.Badges, "Admin · Changes network"),
            (BrokerLineKind.StepName, "2. Check Windows files"),
            (BrokerLineKind.Target, "Changes: Windows"),
            (BrokerLineKind.Row, "Firewall on public networks: Off → On"),
            (BrokerLineKind.Row, "Profile: Public"),
            (BrokerLineKind.Badges, "Admin · Takes a while"),
            (BrokerLineKind.Note, "This can take a long time. You can keep using your PC, and stop it from Optimus."),
        ], view.Lines().Select(l => (l.Kind, l.Text)));
        Assert.Equal("Optimus · Administrator changes", BrokerConfirmView.TitleBar);
        Assert.Equal(("Cancel", "Apply"), (BrokerConfirmView.CancelText, BrokerConfirmView.ApplyText));
    }

    [Fact]
    public void One_step_has_no_number_and_a_flagged_step_brings_the_red_line_and_the_tick()
    {
        var one = new BrokerConfirmView([S(1, "Empty the Recycle Bin", "Recycle Bin", irr: true, loss: true, badges: ["Can’t be undone", "May lose unsaved work"])], true);
        var lines = one.Lines();
        // design v65: one change has its own heading, then the explanation, then the step's name without a number
        Assert.Equal(
        [
            (BrokerLineKind.Heading, "Make this change as administrator?"),
            (BrokerLineKind.Explanation, "You approved these in Optimus. Windows asked for permission because they need administrator rights."),
            (BrokerLineKind.StepName, "Empty the Recycle Bin"),
        ], lines.Take(3).Select(l => (l.Kind, l.Text)));
        Assert.Equal("Make these changes as administrator?", new BrokerConfirmView([S(1, "A"), S(2, "B")], false).Lines()[0].Text);
        Assert.Equal("Admin · Can’t be undone · May lose unsaved work", lines.Single(l => l.Kind == BrokerLineKind.Badges).Text);
        Assert.Equal((BrokerLineKind.Red, "This can’t be undone. This may lose unsaved work."), (lines[^1].Kind, lines[^1].Text));
        Assert.Equal("I understand this can’t be undone", BrokerConfirmView.TickText);
        Assert.Equal(BrokerConfirmView.FlaggedApplyDelay, one.Delay);

        Assert.Equal("Step 4 can’t be undone.", BrokerConfirmView.RedLine([(1, false, false), (2, false, false), (3, false, false), (4, true, false)]));
        Assert.Equal("Steps 1 and 3 may lose unsaved work.", BrokerConfirmView.RedLine([(1, false, true), (2, false, false), (3, false, true)]));
        Assert.Null(BrokerConfirmView.RedLine([(1, false, false), (2, false, false)]));
        Assert.DoesNotContain(new BrokerConfirmView([S(1, "A"), S(2, "B")], false).Lines(), l => l.Kind == BrokerLineKind.Red);
    }

    [Fact]
    public void The_script_variant_reads_its_heading_fixed_line_and_found_by_optimus()
    {
        var view = new BrokerScriptView("Stop-Service Spooler\r\n", 1, [new GuardFinding("stops_service", 1, "Stops a Windows service")], BypassesPolicy: true);
        Assert.Equal(
        [
            "Run this script as administrator?",
            "Optimus can’t check what a script does and can’t undo it. No warning here doesn’t mean it is safe.",
            "Runs even though scripts are normally off on this PC.",
            "Found by Optimus:",
            "Line 1: stops a Windows service",
        ], view.HeaderLines().Select(l => l.Text));
        var plain = new BrokerScriptView("Get-Date\r\n", 1, [], BypassesPolicy: false);
        Assert.Equal(["Run this script as administrator?", BrokerScriptView.FixedLine], plain.HeaderLines().Select(l => l.Text));
        Assert.Equal("Optimus · Administrator changes", BrokerScriptView.TitleBar);
    }

    [Fact]
    public void The_script_variant_ends_with_the_red_line_before_the_tick_and_runs_with_Run()
    {
        var view = new BrokerScriptView("Get-Date\r\n", 1, [], BypassesPolicy: true);
        Assert.Equal([(BrokerLineKind.Red, "Can’t be undone")], view.FooterLines().Select(l => (l.Kind, l.Text)));
        Assert.Contains("Runs even though scripts are normally off on this PC.", view.HeaderLines().Select(l => l.Text));
        Assert.Equal(("Cancel", "Run", "I understand this can’t be undone"), (BrokerScriptView.CancelText, BrokerScriptView.RunText, BrokerScriptView.TickText));
        Assert.Equal("Apply", BrokerConfirmView.ApplyText); // administrator changes keep Apply
        Assert.True(view.NeedsTick);
    }

    private sealed class NoTool : OptimizationTool.Windows.Actions.Repair.IRepairTool
    {
        public (string? Path, string? Problem) Locate(string fileName) => (@"Q:\Windows\System32\" + fileName, null);
        public Task<int> RunAsync(string path, IReadOnlyList<string> arguments, Action<string> onLine, CancellationToken ct) => Task.FromResult(0);
    }

    [Fact]
    public void A_repair_has_nothing_to_undo_so_no_red_line_or_tick_and_its_note_says_it_can_be_stopped()
    {
        var kind = OptimizationTool.Windows.Actions.Repair.RepairKind.Sfc;
        var h = new OptimizationTool.Windows.Actions.Repair.RepairBrokerHandler(kind, new NoTool());
        var r = new ActionResolution(true, new System.Text.Json.Nodes.JsonObject { ["type"] = "repair", ["id"] = "sfc", ["label"] = "Windows files" },
            [], [], [new PlanRow("Windows files", "Not checked now", "Checked")], [], [], null);
        var view = BrokerConfirmView.Build([(1, h, r)]);
        Assert.False(view.NeedsTick);
        var lines = view.Lines();
        Assert.DoesNotContain(lines, l => l.Kind == BrokerLineKind.Red);
        Assert.DoesNotContain(lines, l => l.Text.Contains("undone", StringComparison.Ordinal));
        Assert.Equal((BrokerLineKind.Note, "This can take a long time. You can keep using your PC, and stop it from Optimus."), (lines[^1].Kind, lines[^1].Text));
    }

    [Theory]
    [InlineData(Keys.Enter, true)]
    [InlineData(Keys.Escape, true)]
    [InlineData(Keys.Space, false)]
    public void Esc_and_Enter_cancel(Keys key, bool cancels) => Assert.Equal(cancels, BrokerConfirmWindow.Cancels(key));

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
    public void The_windows_default_to_cancel_for_both_keys_and_are_titled_as_designed() => RunSta(() =>
    {
        // Built, never shown.
        using var confirm = new BrokerConfirmWindow.ConfirmForm(new BrokerConfirmView([S(1, "Empty the Recycle Bin", irr: true)], true));
        Assert.Equal(BrokerConfirmView.TitleBar, confirm.Text);
        Assert.Same(confirm.CancelButton_, confirm.AcceptButton);
        Assert.Same(confirm.CancelButton_, confirm.CancelButton);
        Assert.False(confirm.ApplyButton.Enabled);
        Assert.False(confirm.TickBox!.Checked);
        Assert.Equal(BrokerConfirmView.TickText, confirm.TickBox.Text);

        using var script = new BrokerScriptWindow.ScriptForm(new BrokerScriptView("Get-Date\r\n", 1, [], false));
        Assert.Equal(BrokerScriptView.TitleBar, script.Text);
        Assert.Same(script.CancelButton_, script.AcceptButton);
        Assert.Same(script.CancelButton_, script.CancelButton);
        Assert.Equal(("Cancel", "Run"), (script.CancelButton_.Text, script.ApplyButton.Text)); // design v62: Run for a script
        Assert.False(script.TickBox.Checked);
        // the red "Can’t be undone" after the script box and before the tick
        var order = script.Controls[0].Controls.Cast<Control>().ToList();
        var box = order.FindIndex(c => c is TextBox);
        var red = order.FindIndex(c => c is Label && c.Text == "Can’t be undone");
        Assert.True(box >= 0 && box < red && red < order.IndexOf(script.TickBox), string.Join(" | ", order.Select(c => c.GetType().Name + ":" + c.Text)));
    });
}

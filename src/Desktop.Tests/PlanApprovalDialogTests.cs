using System.Drawing;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using OptimizationTool.Desktop.Bridge;

namespace OptimizationTool.Desktop.Tests;

public class PlanApprovalDialogTests
{
    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var t = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static PlanView View(DateTimeOffset expires, bool admin = true) => PlanView.From(new JsonObject
    {
        ["label"] = "Turn on Public firewall &Approve",
        ["rows"] = new JsonArray(new JsonObject { ["label"] = "Public profile", ["before"] = "Off", ["after"] = "On" }),
        ["context"] = new JsonArray(new JsonArray("Network", "Public · Wi-Fi")),
        ["tradeoffs"] = new JsonArray("Some local sharing may stop working"),
        ["privilege"] = admin ? "admin" : "none",
        ["undo"] = new JsonObject { ["supported"] = true, ["conditions"] = "Re-disable with a fresh warning" },
        ["requestedBy"] = new JsonObject { ["kind"] = "claude_code" },
        ["expiresAt"] = expires.ToString("O"),
    })!;

    [Fact]
    public void Decline_is_the_default_and_approve_starts_disabled() => RunSta(() =>
    {
        var now = DateTimeOffset.UtcNow;
        using var d = new PlanApprovalDialog(View(now.AddMinutes(4).AddSeconds(30)), () => now);
        Assert.Same(d.DeclineButton, d.AcceptButton);
        Assert.Same(d.DeclineButton, d.CancelButton);
        Assert.Same(d.DeclineButton, d.ActiveControl);
        Assert.False(d.ApproveButton.Enabled);
        Assert.Equal(PlanDecision.Decline, d.Decision);
        Assert.Equal("This request ends in 4:30. If it ends, nothing changes.", d.CountdownText);
        Assert.Equal("Optimus · Approve a change", d.Text);

        var labels = d.LabelTexts.ToList();
        Assert.Contains("Turn on Public firewall &Approve", labels); // shown literally (UseMnemonic=false)
        Assert.Contains("Asked by Claude", labels);
        Assert.Contains("Admin", labels);
        Assert.Contains("Re-disable with a fresh warning", labels);
        Assert.DoesNotContain(labels, l => l.StartsWith("Undo:", StringComparison.Ordinal));
        Assert.Contains("Public profile: Off → On", labels);
        Assert.Contains("Network: Public · Wi-Fi", labels);
        Assert.Contains("Trade-off: Some local sharing may stop working", labels);
        Assert.All(d.Controls.OfType<Control>(), _ => { });

        var shot = Environment.GetEnvironmentVariable("OT_DIALOG_SNAPSHOT_DIR");
        if (!string.IsNullOrEmpty(shot))
        {
            d.StartPosition = FormStartPosition.Manual;
            d.Location = new Point(-3000, -3000);
            d.Show();
            Application.DoEvents();
            using var bmp = new Bitmap(d.Width, d.Height);
            d.DrawToBitmap(bmp, new Rectangle(Point.Empty, d.Size));
            bmp.Save(Path.Combine(shot, "approve-plan.png"));
        }
    });

    [Fact]
    public void No_needs_admin_line_for_user_scope() => RunSta(() =>
    {
        var now = DateTimeOffset.UtcNow;
        using var d = new PlanApprovalDialog(View(now.AddMinutes(5), admin: false), () => now);
        Assert.DoesNotContain("Admin", d.LabelTexts);
    });

    [Fact]
    public void Expiry_turns_the_decision_into_expired_and_the_buttons_into_close() => RunSta(() =>
    {
        var now = DateTimeOffset.UtcNow;
        using var d = new PlanApprovalDialog(View(now.AddSeconds(2)), () => now);
        now = now.AddSeconds(3);
        d.Tick();
        Assert.Equal("This request ended. Nothing was changed.", d.CountdownText);
        Assert.Equal(PlanDecision.Expired, d.Decision);
        Assert.Equal("Close", d.DeclineButton.Text);
        Assert.False(d.ApproveButton.Enabled);
        d.ApproveButton.PerformClick();                     // nothing can be approved once it ended
        Assert.Equal(PlanDecision.Expired, d.Decision);
        d.DeclineButton.PerformClick();                     // Close keeps it expired
        Assert.Equal(PlanDecision.Expired, d.Decision);
    });

    [Fact]
    public void Escape_declines() => RunSta(() =>
    {
        var now = DateTimeOffset.UtcNow;
        using var d = new PlanApprovalDialog(View(now.AddMinutes(5)), () => now);
        d.DeclineButton.PerformClick();
        Assert.Equal(PlanDecision.Decline, d.Decision);
    });
}

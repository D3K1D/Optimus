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
        ["requestedBy"] = new JsonObject { ["kind"] = "codex" },
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
        Assert.Equal("Expires in 4:30", d.CountdownText);

        var labels = d.LabelTexts.ToList();
        Assert.Contains("Turn on Public firewall &Approve", labels); // shown literally (UseMnemonic=false)
        Assert.Contains("Asked by Codex", labels);
        Assert.Contains("Needs admin", labels);
        Assert.Contains("Undo: yes · Re-disable with a fresh warning", labels);
        Assert.Contains("Network: Public · Wi-Fi", labels);
        Assert.Contains("• Some local sharing may stop working", labels);
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
        Assert.DoesNotContain("Needs admin", d.LabelTexts);
    });

    [Fact]
    public void Expiry_turns_the_decision_into_expired() => RunSta(() =>
    {
        var now = DateTimeOffset.UtcNow;
        using var d = new PlanApprovalDialog(View(now.AddSeconds(2)), () => now);
        now = now.AddSeconds(3);
        d.Tick();
        Assert.Equal("Expired", d.CountdownText);
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

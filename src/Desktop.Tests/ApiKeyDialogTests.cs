using System.Drawing;
using System.Windows.Forms;

namespace OptimizationTool.Desktop.Tests;

public class ApiKeyDialogTests
{
    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static T Find<T>(Control root) where T : Control =>
        root.Controls.OfType<T>().Single();

    [Theory]
    [InlineData("anthropic_api", "Anthropic API key")]
    [InlineData("openai_api", "OpenAI API key")]
    public void Dialog_has_password_box_remember_off_and_connect_disabled_until_typed(string kind, string title) => RunSta(() =>
    {
        using var dialog = new ApiKeyDialog(kind);
        Assert.Equal(title, dialog.Text);
        var box = Find<TextBox>(dialog);
        Assert.True(box.UseSystemPasswordChar);
        Assert.False(Find<CheckBox>(dialog).Checked);
        Assert.Equal("Remember on this PC", Find<CheckBox>(dialog).Text);

        var connect = (Button)dialog.AcceptButton!;
        Assert.Equal("Connect", connect.Text);
        Assert.Equal("Cancel", ((Button)dialog.CancelButton!).Text);
        Assert.False(connect.Enabled);
        box.Text = "x";
        Assert.True(connect.Enabled);

        var shot = Environment.GetEnvironmentVariable("OT_DIALOG_SNAPSHOT_DIR");
        if (!string.IsNullOrEmpty(shot))
        {
            dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new Point(-3000, -3000);
            dialog.Show();
            Application.DoEvents();
            using var bmp = new Bitmap(dialog.Width, dialog.Height);
            dialog.DrawToBitmap(bmp, new Rectangle(Point.Empty, dialog.Size));
            bmp.Save(Path.Combine(shot, kind + ".png"));
        }
    });

    [Fact]
    public void Disposing_clears_the_text_box() => RunSta(() =>
    {
        var dialog = new ApiKeyDialog("anthropic_api");
        var box = Find<TextBox>(dialog);
        box.Text = "sk-secret";
        dialog.Dispose();
        Assert.Equal("", box.Text);
    });
}

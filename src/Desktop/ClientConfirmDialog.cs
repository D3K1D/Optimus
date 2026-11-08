using OptimizationTool.Desktop.Bridge;

namespace OptimizationTool.Desktop;

/// <summary>
/// Native confirm for adding/removing the MCP registration (contracts/onboarding.md §2). Shows the exact command and
/// the bridge path (selectable, for copying). Cancel is the default button; Enter and Esc cancel.
/// </summary>
internal sealed class ClientConfirmDialog : Form
{
    private static readonly Color Surface = Color.FromArgb(0x1a, 0x1d, 0x23);
    private static readonly Color Field = Color.FromArgb(0x26, 0x2a, 0x33);
    private static readonly Color Ink = Color.FromArgb(0xee, 0xf1, 0xf6);
    private static readonly Color Muted = Color.FromArgb(0xa2, 0xaa, 0xb8);

    internal Button ConfirmButton { get; }
    internal Button CancelButton_ { get; }
    internal string CommandText { get; }

    public ClientConfirmDialog(ClientConfirmView view)
    {
        Text = view.Title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Surface;
        ForeColor = Ink;
        Font = new Font("Segoe UI", 10f);
        Padding = new Padding(20, 16, 20, 16);
        CommandText = view.Command;

        const int width = 520;
        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Surface,
        };
        stack.Controls.Add(Label_(view.Title, width, Ink, 12.5f, bold: true));
        stack.Controls.Add(Label_("Command", width, Muted));
        stack.Controls.Add(ReadOnlyBox(view.Command, width));
        stack.Controls.Add(Label_("Path", width, Muted));
        stack.Controls.Add(ReadOnlyBox(view.BridgePath, width));

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Width = width,
            Margin = new Padding(0, 14, 0, 0),
            BackColor = Surface,
        };
        CancelButton_ = MakeButton("Cancel", DialogResult.Cancel, primary: false);
        ConfirmButton = MakeButton(view.Verb, DialogResult.OK, primary: true);
        buttons.Controls.Add(CancelButton_);
        buttons.Controls.Add(ConfirmButton);
        stack.Controls.Add(buttons);
        Controls.Add(stack);

        AcceptButton = CancelButton_; // Enter cancels.
        CancelButton = CancelButton_; // Esc cancels.
        ActiveControl = CancelButton_;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.UseDarkTitleBar(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        CancelButton_.Focus();
    }

    private static Label Label_(string text, int width, Color color, float size = 10f, bool bold = false) => new()
    {
        Text = text,
        UseMnemonic = false,
        AutoSize = true,
        MaximumSize = new Size(width, 0),
        ForeColor = color,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
        Margin = new Padding(0, 6, 0, 2),
    };

    private static TextBox ReadOnlyBox(string text, int width) => new()
    {
        Text = text,
        ReadOnly = true,
        Multiline = true,
        WordWrap = true,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Field,
        ForeColor = Ink,
        Font = new Font("Consolas", 9.5f),
        Width = width,
        Height = 44,
        TabStop = false,
    };

    private static Button MakeButton(string text, DialogResult result, bool primary)
    {
        var b = new Button
        {
            Text = text,
            UseMnemonic = false,
            DialogResult = result,
            Size = new Size(96, 34),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Ink : Field,
            ForeColor = primary ? Surface : Ink,
            UseVisualStyleBackColor = false,
            Margin = new Padding(8, 0, 0, 0),
        };
        b.FlatAppearance.BorderColor = primary ? Ink : Color.FromArgb(0x6b, 0x73, 0x83);
        return b;
    }
}

/// <summary>Shows <see cref="ClientConfirmDialog"/> owned by the main window, outside the WebView2 callback.</summary>
internal sealed class WinFormsClientConfirmPrompt(Form owner) : IClientConfirmPrompt
{
    public Task<bool> ConfirmAsync(ClientConfirmView view, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (owner.IsDisposed || ct.IsCancellationRequested) { tcs.SetResult(false); return tcs.Task; }
        try
        {
            owner.BeginInvoke(() =>
            {
                try
                {
                    if (owner.IsDisposed || ct.IsCancellationRequested) { tcs.TrySetResult(false); return; }
                    using var dialog = new ClientConfirmDialog(view);
                    tcs.TrySetResult(dialog.ShowDialog(owner) == DialogResult.OK);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                {
                    tcs.TrySetResult(false);
                }
            });
        }
        catch (InvalidOperationException)
        {
            tcs.TrySetResult(false);
        }
        return tcs.Task;
    }
}

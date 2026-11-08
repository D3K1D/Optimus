using OptimizationTool.Desktop.Bridge;

namespace OptimizationTool.Desktop;

/// <summary>
/// Native, modal API-key entry (contracts/m3-agent.md §7). The key is typed here, never in the renderer.
/// The text box is cleared as soon as the value is read.
/// </summary>
internal sealed class ApiKeyDialog : Form
{
    private static readonly Color Surface = Color.FromArgb(0x1a, 0x1d, 0x23);
    private static readonly Color Field = Color.FromArgb(0x26, 0x2a, 0x33);
    private static readonly Color Ink = Color.FromArgb(0xee, 0xf1, 0xf6);
    private static readonly Color Muted = Color.FromArgb(0xa2, 0xaa, 0xb8);

    private readonly TextBox _key;
    private readonly CheckBox _remember;
    private readonly Button _connect;

    public ApiKeyDialog(string kind)
    {
        Text = kind == "openai_api" ? "OpenAI API key" : "Anthropic API key";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Surface;
        ForeColor = Ink;
        Font = new Font("Segoe UI", 10f);
        ClientSize = new Size(420, 168);

        var label = new Label { Text = "API key", AutoSize = true, ForeColor = Muted, Location = new Point(20, 18) };
        _key = new TextBox
        {
            UseSystemPasswordChar = true,
            MaxLength = 512,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Field,
            ForeColor = Ink,
            Location = new Point(20, 42),
            Width = 380,
        };
        _remember = new CheckBox
        {
            Text = "Remember on this PC",
            Checked = false,
            AutoSize = true,
            ForeColor = Ink,
            Location = new Point(20, 80),
        };
        _connect = MakeButton("Connect", DialogResult.OK, new Point(208, 120), primary: true);
        var cancel = MakeButton("Cancel", DialogResult.Cancel, new Point(308, 120), primary: false);
        _connect.Enabled = false;
        _key.TextChanged += (_, _) => _connect.Enabled = _key.TextLength > 0;

        Controls.AddRange([label, _key, _remember, _connect, cancel]);
        AcceptButton = _connect;
        CancelButton = cancel;
    }

    private static Button MakeButton(string text, DialogResult result, Point location, bool primary)
    {
        var b = new Button
        {
            Text = text,
            DialogResult = result,
            Location = location,
            Size = new Size(92, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Ink : Field,
            ForeColor = primary ? Surface : Ink,
            UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderColor = primary ? Ink : Color.FromArgb(0x6b, 0x73, 0x83);
        return b;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.UseDarkTitleBar(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _key.Focus();
    }

    /// <summary>Shows the dialog; returns the entry on Connect, null otherwise. The text box is always cleared.</summary>
    public ApiKeyEntry? Ask(IWin32Window owner)
    {
        try
        {
            var ok = ShowDialog(owner) == DialogResult.OK && _key.TextLength > 0;
            return ok ? new ApiKeyEntry(_key.Text.Trim(), _remember.Checked) : null;
        }
        finally
        {
            _key.Clear();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_key.IsDisposed) _key.Clear();
        base.Dispose(disposing);
    }
}

/// <summary>Shows <see cref="ApiKeyDialog"/> owned by the main window, posted outside the WebView2 event handler.</summary>
internal sealed class WinFormsApiKeyPrompt(Form owner) : IApiKeyPrompt
{
    public Task<ApiKeyEntry?> PromptAsync(string kind, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ApiKeyEntry?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (owner.IsDisposed || ct.IsCancellationRequested)
        {
            tcs.SetResult(null);
            return tcs.Task;
        }
        try
        {
            // BeginInvoke: never run a nested modal loop inside a WebView2 callback.
            owner.BeginInvoke(() =>
            {
                try
                {
                    if (owner.IsDisposed || ct.IsCancellationRequested) { tcs.TrySetResult(null); return; }
                    using var dialog = new ApiKeyDialog(kind);
                    tcs.TrySetResult(dialog.Ask(owner));
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                {
                    tcs.TrySetResult(null);
                }
            });
        }
        catch (InvalidOperationException)
        {
            tcs.TrySetResult(null);
        }
        return tcs.Task;
    }
}

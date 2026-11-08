using OptimizationTool.Desktop.Bridge;

namespace OptimizationTool.Desktop;

/// <summary>
/// Native approval for one exact plan (m4-actions.md §1.2). Shows only <see cref="PlanView"/> fields, already sanitized,
/// as plain labels (no mnemonics). Decline has default focus; Enter and Esc decline; Approve needs a deliberate click
/// and is enabled only after a short delay. Closes as Expired when the plan's expiry passes.
/// </summary>
internal sealed class PlanApprovalDialog : Form
{
    private static readonly Color Surface = Color.FromArgb(0x1a, 0x1d, 0x23);
    private static readonly Color Field = Color.FromArgb(0x26, 0x2a, 0x33);
    private static readonly Color Ink = Color.FromArgb(0xee, 0xf1, 0xf6);
    private static readonly Color Muted = Color.FromArgb(0xa2, 0xaa, 0xb8);
    private static readonly Color Exposure = Color.FromArgb(0xff, 0xad, 0x5c);
    public static readonly TimeSpan ApproveDelay = TimeSpan.FromMilliseconds(800);

    private readonly PlanView _view;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Label _countdown;
    private readonly Button _approve;
    private readonly Button _decline;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private readonly DateTime _shownAt = DateTime.UtcNow;

    public PlanDecision Decision { get; private set; } = PlanDecision.Decline;

    internal Button ApproveButton => _approve;
    internal Button DeclineButton => _decline;
    internal string CountdownText => _countdown.Text;
    internal IEnumerable<string> LabelTexts => AllLabels(this).Select(l => l.Text);

    public PlanApprovalDialog(PlanView view, Func<DateTimeOffset>? utcNow = null)
    {
        _view = view;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

        Text = "Approve change";
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

        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            BackColor = Surface,
        };
        const int width = 440;

        stack.Controls.Add(Text_(view.Title, width, Ink, 12.5f, bold: true));
        stack.Controls.Add(Text_($"Asked by {view.RequestedBy}", width, Muted));

        if (view.Rows.Count > 0)
        {
            var table = new TableLayoutPanel
            {
                ColumnCount = 4,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 10, 0, 6),
                BackColor = Field,
                Padding = new Padding(8, 6, 8, 6),
            };
            foreach (var (label, before, after) in view.Rows)
            {
                table.Controls.Add(Text_(label, 150, Muted));
                table.Controls.Add(Text_(before, 120, Ink));
                table.Controls.Add(Text_("→", 24, Muted));
                table.Controls.Add(Text_(after, 120, Ink, bold: true));
            }
            stack.Controls.Add(table);
        }

        foreach (var c in view.Context) stack.Controls.Add(Text_(c, width, Muted));
        foreach (var t in view.Tradeoffs) stack.Controls.Add(Text_("• " + t, width, Ink));
        if (view.NeedsAdmin) stack.Controls.Add(Text_("Needs admin", width, Exposure, bold: true));
        stack.Controls.Add(Text_(view.UndoLine, width, Muted));

        _countdown = Text_("", width, Muted);
        stack.Controls.Add(_countdown);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Width = width,
            Margin = new Padding(0, 12, 0, 0),
            BackColor = Surface,
        };
        _decline = MakeButton("Decline", primary: false);
        _approve = MakeButton("Approve", primary: true);
        _approve.Enabled = false;
        _decline.Click += (_, _) => Finish(PlanDecision.Decline);
        _approve.Click += (_, _) =>
        {
            if (DateTime.UtcNow - _shownAt >= ApproveDelay) Finish(PlanDecision.Approve);
        };
        buttons.Controls.Add(_decline);
        buttons.Controls.Add(_approve);
        stack.Controls.Add(buttons);

        Controls.Add(stack);
        AcceptButton = _decline; // Enter declines.
        CancelButton = _decline; // Esc declines.
        ActiveControl = _decline;

        _timer.Tick += (_, _) => Tick();
        Tick();
    }

    private void Finish(PlanDecision decision)
    {
        Decision = decision;
        _timer.Stop();
        DialogResult = decision == PlanDecision.Approve ? DialogResult.OK : DialogResult.Cancel;
        Close();
    }

    internal void Tick()
    {
        var left = _view.ExpiresAt - _utcNow();
        if (left <= TimeSpan.Zero)
        {
            _countdown.Text = "Expired";
            if (Visible) Finish(PlanDecision.Expired);
            else Decision = PlanDecision.Expired;
            return;
        }
        _countdown.Text = $"Expires in {(int)left.TotalMinutes}:{left.Seconds:00}";
        if (!_approve.Enabled && DateTime.UtcNow - _shownAt >= ApproveDelay && Visible) _approve.Enabled = true;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _decline.Focus();
        _timer.Start();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing by any other means (title bar X, Alt+F4) is a decline unless already decided.
        if (DialogResult != DialogResult.OK && Decision == PlanDecision.Approve) Decision = PlanDecision.Decline;
        _timer.Stop();
        base.OnFormClosing(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.UseDarkTitleBar(Handle);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>Plain label: no mnemonic processing, wraps at <paramref name="width"/>.</summary>
    private static Label Text_(string text, int width, Color color, float size = 10f, bool bold = false) => new()
    {
        Text = text,
        UseMnemonic = false,
        AutoSize = true,
        MaximumSize = new Size(width, 0),
        ForeColor = color,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
        Margin = new Padding(0, 3, 0, 3),
    };

    private static Button MakeButton(string text, bool primary)
    {
        var b = new Button
        {
            Text = text,
            UseMnemonic = false,
            Size = new Size(104, 34),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Ink : Field,
            ForeColor = primary ? Surface : Ink,
            UseVisualStyleBackColor = false,
            Margin = new Padding(8, 0, 0, 0),
        };
        b.FlatAppearance.BorderColor = primary ? Ink : Color.FromArgb(0x6b, 0x73, 0x83);
        return b;
    }

    private static IEnumerable<Label> AllLabels(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is Label l) yield return l;
            foreach (var inner in AllLabels(c)) yield return inner;
        }
    }
}

/// <summary>Shows <see cref="PlanApprovalDialog"/> owned by the main window, posted outside the WebView2 event handler.</summary>
internal sealed class WinFormsPlanPrompt(Form owner) : IPlanApprovalPrompt
{
    public Task<PlanDecision> ConfirmAsync(PlanView view, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<PlanDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (owner.IsDisposed || ct.IsCancellationRequested)
        {
            tcs.SetResult(PlanDecision.Decline);
            return tcs.Task;
        }
        try
        {
            owner.BeginInvoke(() =>
            {
                try
                {
                    if (owner.IsDisposed || ct.IsCancellationRequested) { tcs.TrySetResult(PlanDecision.Decline); return; }
                    using var dialog = new PlanApprovalDialog(view);
                    var result = dialog.ShowDialog(owner);
                    tcs.TrySetResult(result == DialogResult.OK && dialog.Decision == PlanDecision.Approve
                        ? PlanDecision.Approve
                        : dialog.Decision == PlanDecision.Expired ? PlanDecision.Expired : PlanDecision.Decline);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                {
                    tcs.TrySetResult(PlanDecision.Decline);
                }
            });
        }
        catch (InvalidOperationException)
        {
            tcs.TrySetResult(PlanDecision.Decline);
        }
        return tcs.Task;
    }
}

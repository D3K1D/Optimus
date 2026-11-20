using OptimizationTool.Desktop.Bridge;

namespace OptimizationTool.Desktop;

/// <summary>
/// Native approval for one exact plan (m4-actions.md §1.2; m9-ai-powers.md §4 adds fix plans), worded and ordered as the
/// design's "Approve a change" (README "Windows dialogs", v57): every line comes from <see cref="ApprovalDialogText"/> over
/// <see cref="PlanView"/> fields, already sanitized, drawn as plain labels (no mnemonics). Decline has default focus; Enter
/// and Esc decline; Approve needs a deliberate click and is enabled only after a short delay (800 ms; 3 s when flagged). A
/// plan flagged irreversible or data_loss shows the red block and a required, never pre-ticked "I understand this can’t be
/// undone"; Approve stays off until it is ticked. The content scrolls; nothing is cut. At the expiry the decision is Expired,
/// the line says the request ended, Approve goes and Decline becomes Close.
/// </summary>
internal sealed class PlanApprovalDialog : Form
{
    private static readonly Color Surface = Color.FromArgb(0x1a, 0x1d, 0x23);
    private static readonly Color Field = Color.FromArgb(0x26, 0x2a, 0x33);
    private static readonly Color Ink = Color.FromArgb(0xee, 0xf1, 0xf6);
    private static readonly Color Muted = Color.FromArgb(0xa2, 0xaa, 0xb8);
    private static readonly Color Exposure = Color.FromArgb(0xff, 0xad, 0x5c);
    private static readonly Color Danger = Color.FromArgb(0x8f, 0x1d, 0x1d);
    private static readonly Color DangerInk = Color.FromArgb(0xff, 0x8a, 0x80);
    public static readonly TimeSpan ApproveDelay = PlanView.ApproveDelay;
    private const int Width_ = 440;
    private const int MaxContentHeight = 480;
    /// <summary>How far one list level (the "• …" trade-off bullets, design v67) sits in from its label, in logical px.</summary>
    internal const int IndentStep = 12;

    private readonly PlanView _view;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Label _countdown;
    private readonly Button _approve;
    private readonly Button _decline;
    private readonly CheckBox? _tick;
    private readonly Panel _scroller;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private readonly DateTime _shownAt = DateTime.UtcNow;
    private bool _ended;

    public PlanDecision Decision { get; private set; }

    internal Button ApproveButton => _approve;
    internal Button DeclineButton => _decline;
    internal CheckBox? TickBox => _tick;
    internal Panel Scroller => _scroller;
    internal string CountdownText => _countdown.Text;
    internal IEnumerable<string> LabelTexts => AllLabels(this).Select(l => l.Text);

    public PlanApprovalDialog(PlanView view, Func<DateTimeOffset>? utcNow = null)
    {
        _view = view;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        Decision = view.NeedsTick ? PlanDecision.NotTicked : PlanDecision.Decline;

        Text = ApprovalDialogText.PlanTitleBar;
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

        var root = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            BackColor = Surface,
        };

        // Items 1–4 scroll inside one panel; the red block, the tick, the expiry line and the buttons stay in view.
        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Surface,
            Margin = Padding.Empty,
        };
        foreach (var line in ApprovalDialogText.PlanBody(view)) stack.Controls.Add(Draw(line));

        _scroller = new Panel { AutoScroll = true, BackColor = Surface, Margin = Padding.Empty };
        _scroller.Controls.Add(stack);
        var preferred = stack.GetPreferredSize(new Size(Width_, 0));
        _scroller.Size = new Size(Width_ + SystemInformation.VerticalScrollBarWidth + 4, Math.Min(preferred.Height, MaxContentHeight));
        root.Controls.Add(_scroller);

        if (view.NeedsTick)
        {
            if (ApprovalDialogText.RedBlock(view) is { } red)
            {
                var block = Text_(red, Width_, Ink, bold: true);
                block.BackColor = Danger;
                block.Padding = new Padding(8, 6, 8, 6);
                block.Margin = new Padding(0, 10, 0, 0);
                root.Controls.Add(block);
            }
            _tick = new CheckBox
            {
                Text = PlanView.TickText,
                Checked = false,
                UseMnemonic = false,
                AutoSize = true,
                ForeColor = Ink,
                Margin = new Padding(0, 8, 0, 0),
            };
            _tick.CheckedChanged += (_, _) => Tick();
            root.Controls.Add(_tick);
        }

        _countdown = Text_("", Width_, Muted);
        _countdown.Margin = new Padding(0, 10, 0, 0);
        root.Controls.Add(_countdown);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Width = Width_,
            Margin = new Padding(0, 12, 0, 0),
            BackColor = Surface,
        };
        _decline = MakeButton(ApprovalDialogText.Decline, primary: false);
        _approve = MakeButton(ApprovalDialogText.Approve, primary: true);
        _approve.Enabled = false;
        _decline.Click += (_, _) => Finish(_ended ? PlanDecision.Expired : Unapproved());
        _approve.Click += (_, _) =>
        {
            if (!_ended && DateTime.UtcNow - _shownAt >= _view.Delay && Ticked) Finish(PlanDecision.Approve);
        };
        buttons.Controls.Add(_decline);
        buttons.Controls.Add(_approve);
        root.Controls.Add(buttons);

        Controls.Add(root);
        AcceptButton = _decline; // Enter declines.
        CancelButton = _decline; // Esc declines.
        ActiveControl = _decline;

        _timer.Tick += (_, _) => Tick();
        Tick();
    }

    /// <summary>The required tick is given (always true for a plan that needs none).</summary>
    private bool Ticked => _tick is null || _tick.Checked;

    /// <summary>Any way out but Approve: a flagged plan closed without its tick is "not ticked", else a decline.</summary>
    private PlanDecision Unapproved() => _view.NeedsTick && !Ticked ? PlanDecision.NotTicked : PlanDecision.Decline;

    private void Finish(PlanDecision decision)
    {
        Decision = decision;
        _timer.Stop();
        DialogResult = decision == PlanDecision.Approve ? DialogResult.OK : DialogResult.Cancel;
        Close();
    }

    internal void Tick()
    {
        if (_ended) return;
        var left = _view.ExpiresAt - _utcNow();
        _countdown.Text = ApprovalDialogText.Expiry(left);
        if (left <= TimeSpan.Zero)
        {
            // Ended: nothing can be approved any more; the window stays to say so, and its one button is Close.
            _ended = true;
            Decision = PlanDecision.Expired;
            _approve.Enabled = false;
            _approve.Visible = false;
            if (_tick is not null) _tick.Enabled = false;
            _decline.Text = ApprovalDialogText.Close;
            _timer.Stop();
            return;
        }
        if (!Ticked) _approve.Enabled = false;
        else if (!_approve.Enabled && DateTime.UtcNow - _shownAt >= _view.Delay && Visible) _approve.Enabled = true;
        if (Decision != PlanDecision.Approve && Decision != PlanDecision.Expired) Decision = Unapproved();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _decline.Focus();
        if (!_ended) _timer.Start();
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (ApprovalKeys.Declines(keyData)) { _decline.PerformClick(); return true; }
        return base.ProcessDialogKey(keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing by any other means (title bar X, Alt+F4) is a decline unless already decided.
        if (DialogResult != DialogResult.OK && Decision is not PlanDecision.Expired) Decision = Unapproved();
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

    private static Label Draw(DialogLine line)
    {
        var indent = Math.Max(0, line.Indent) * IndentStep;
        var width = Width_ - indent;
        var l = line.Kind switch
        {
            DialogLineKind.Heading => Text_(line.Text, width, Ink, 12.5f, bold: true),
            DialogLineKind.Quote => Text_(line.Text, width, Ink, italic: true),
            DialogLineKind.StepName => Text_(line.Text, width, Ink, 10.5f, bold: true),
            DialogLineKind.Row or DialogLineKind.Tradeoff => Text_(line.Text, width, Ink),
            DialogLineKind.LeftOut => Text_(line.Text, width, Ink), // m9 §17.3: the label plain, not bold (Design System v80)
            DialogLineKind.Badges => Text_(line.Text, width, Exposure, bold: true),
            DialogLineKind.Why => Text_(line.Text, width, Muted, italic: true),
            DialogLineKind.UndoRed => Text_(line.Text, width, DangerInk, bold: true),
            _ => Text_(line.Text, width, Muted),
        };
        if (line.Kind == DialogLineKind.StepName) l.Margin = new Padding(0, 12, 0, 2);
        // A list item sits in under its label; its top and bottom margins stay the ordinary line's (no extra space above).
        if (indent > 0) l.Margin = l.Margin with { Left = indent };
        if (line.Kind is DialogLineKind.Row or DialogLineKind.Tradeoff) { l.BackColor = Field; l.Padding = new Padding(6, 2, 6, 2); }
        return l;
    }

    /// <summary>Plain label: no mnemonic processing, wraps at <paramref name="width"/>.</summary>
    private static Label Text_(string text, int width, Color color, float size = 10f, bool bold = false, bool italic = false) => new()
    {
        Text = text,
        UseMnemonic = false,
        AutoSize = true,
        MaximumSize = new Size(width, 0),
        ForeColor = color,
        Font = new Font("Segoe UI", size, (bold ? FontStyle.Bold : FontStyle.Regular) | (italic ? FontStyle.Italic : FontStyle.Regular)),
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

/// <summary>
/// The approval dialogs' keys (design v57): Esc and Enter mean Decline (or Close once the request ended), whatever has focus,
/// so nothing is approved by accident; Approve must be clicked.
/// </summary>
internal static class ApprovalKeys
{
    public static bool Declines(Keys keyData) => (keyData & ~Keys.Modifiers) is Keys.Enter or Keys.Escape;
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
                        : dialog.Decision is PlanDecision.Expired or PlanDecision.NotTicked ? dialog.Decision : PlanDecision.Decline);
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

    /// <summary>m9 §5.2: a script's own dialog; its two choices come back with the decision (only an Approve carries them on).</summary>
    public Task<ScriptDecision> ConfirmScriptAsync(PlanView view, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ScriptDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var declined = new ScriptDecision(PlanDecision.Decline, false, false);
        if (owner.IsDisposed || ct.IsCancellationRequested)
        {
            tcs.SetResult(declined);
            return tcs.Task;
        }
        try
        {
            owner.BeginInvoke(() =>
            {
                try
                {
                    if (owner.IsDisposed || ct.IsCancellationRequested) { tcs.TrySetResult(declined); return; }
                    using var dialog = new ScriptApprovalDialog(view);
                    var result = dialog.ShowDialog(owner);
                    var decision = result == DialogResult.OK && dialog.Decision == PlanDecision.Approve ? PlanDecision.Approve
                        : dialog.Decision is PlanDecision.Expired or PlanDecision.NotTicked ? dialog.Decision : PlanDecision.Decline;
                    tcs.TrySetResult(new ScriptDecision(decision, dialog.RestorePoint, dialog.ShareOutput));
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                {
                    tcs.TrySetResult(declined);
                }
            });
        }
        catch (InvalidOperationException)
        {
            tcs.TrySetResult(declined);
        }
        return tcs.Task;
    }
}

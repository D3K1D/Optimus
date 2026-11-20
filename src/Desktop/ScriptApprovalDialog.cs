using OptimizationTool.Desktop.Bridge;

namespace OptimizationTool.Desktop;

/// <summary>
/// The native approval of one approved script (contracts/m9-ai-powers.md §5.2; design "Approve a script", README "Windows
/// dialogs", v57). It is the authority, in the design's order: the title, who asked and the AI's reason as a quote, the fixed
/// notice that Optimus can't check or undo a script, the administrator and policy lines, "Found by Optimus:" and the guard's
/// warnings (all from <see cref="ApprovalDialogText.ScriptHeader"/>), the full script read-only in a monospace box with line
/// numbers (tabs as four spaces, long lines wrapped, nothing cut) and each part's explanation under its lines with the fixed
/// header, "Read to the end to approve" until it is read, the two choices (restore point; let the AI see the last lines of
/// output, pre-ticked), the red "Can’t be undone" and the never-pre-ticked tick, the expiry line. Approve is enabled only after
/// 3 s, with the tick, and once the script box has been scrolled to its end (a script that fits counts as read). Decline has
/// default focus; Enter and Esc decline; closing without the tick or before reading to the end is "not ticked". At the expiry
/// the decision is Expired, Approve goes and Decline becomes Close.
/// </summary>
internal sealed class ScriptApprovalDialog : Form
{
    private static readonly Color Surface = Color.FromArgb(0x1a, 0x1d, 0x23);
    private static readonly Color Field = Color.FromArgb(0x26, 0x2a, 0x33);
    private static readonly Color Ink = Color.FromArgb(0xee, 0xf1, 0xf6);
    private static readonly Color Muted = Color.FromArgb(0xa2, 0xaa, 0xb8);
    private static readonly Color Exposure = Color.FromArgb(0xff, 0xad, 0x5c);
    private static readonly Color Danger = Color.FromArgb(0x8f, 0x1d, 0x1d);
    private const int Width_ = 640;
    private const int HeaderHeight = 230;
    private const int BoxHeight = 300;

    private readonly PlanView _view;
    private readonly ScriptView _script;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly RichTextBox _box;
    private readonly CheckBox _restorePoint;
    private readonly CheckBox _share;
    private readonly CheckBox _tick;
    private readonly Label _countdown;
    private readonly Label _readHint;
    private readonly Button _approve;
    private readonly Button _decline;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private readonly DateTime _shownAt;
    private bool _readToEnd;
    private bool _ended;

    public PlanDecision Decision { get; private set; } = PlanDecision.NotTicked;
    public bool RestorePoint => _restorePoint.Checked && _restorePoint.Enabled;
    public bool ShareOutput => _share.Checked;

    internal Button ApproveButton => _approve;
    internal Button DeclineButton => _decline;
    internal CheckBox TickBox => _tick;
    internal CheckBox RestorePointBox => _restorePoint;
    internal CheckBox ShareBox => _share;
    internal RichTextBox ScriptBox => _box;
    internal bool ReadToEnd => _readToEnd;
    internal Label ReadHint => _readHint;
    internal string CountdownText => _countdown.Text;
    internal IEnumerable<string> LabelTexts => AllControls(this).OfType<Label>().Select(l => l.Text);
    private readonly Func<DateTime> _clock;

    public ScriptApprovalDialog(PlanView view, Func<DateTimeOffset>? utcNow = null, Func<DateTime>? clock = null)
    {
        _view = view;
        _script = view.Script ?? throw new ArgumentException("Not a script plan.", nameof(view));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _clock = clock ?? (() => DateTime.UtcNow);
        _shownAt = _clock();

        Text = ApprovalDialogText.ScriptTitleBar;
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
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill, BackColor = Surface,
        };

        // Items 1–3 scroll on their own (a long reason never pushes the script or the buttons off screen).
        var header = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Surface, Margin = Padding.Empty,
        };
        foreach (var line in ApprovalDialogText.ScriptHeader(view))
            header.Controls.Add(line.Kind switch
            {
                DialogLineKind.Heading => Text_(line.Text, Width_, Ink, 12.5f, bold: true),
                DialogLineKind.Quote => Text_(line.Text, Width_, Ink, italic: true),
                DialogLineKind.Notice => Text_(line.Text, Width_, Ink),
                DialogLineKind.FoundBy => Text_(line.Text, Width_, Ink, bold: true),
                DialogLineKind.Warning => Text_(line.Text, Width_, Exposure, bold: true),
                _ => Text_(line.Text, Width_, Muted),
            });
        var headerScroll = new Panel { AutoScroll = true, BackColor = Surface, Margin = Padding.Empty };
        headerScroll.Controls.Add(header);
        var preferred = header.GetPreferredSize(new Size(Width_, 0));
        headerScroll.Size = new Size(Width_ + SystemInformation.VerticalScrollBarWidth + 4, Math.Min(preferred.Height, HeaderHeight));
        root.Controls.Add(headerScroll);

        // The full script, exactly as approved: read-only, monospace, numbered, wrapped, nothing folded or cut.
        _box = new RichTextBox
        {
            ReadOnly = true, DetectUrls = false, WordWrap = true, ScrollBars = RichTextBoxScrollBars.Vertical, ShortcutsEnabled = true,
            BorderStyle = BorderStyle.None, BackColor = Field, ForeColor = Ink, Font = new Font("Consolas", 9.5f),
            Size = new Size(Width_ + SystemInformation.VerticalScrollBarWidth + 4, BoxHeight), Margin = new Padding(0, 8, 0, 8),
            Text = _script.BoxText(), TabStop = true,
        };
        _box.VScroll += (_, _) => Tick();
        _box.Resize += (_, _) => Tick();
        _box.KeyUp += (_, _) => Tick();
        _box.MouseWheel += (_, _) => Tick();
        root.Controls.Add(_box);
        _readHint = Text_(ApprovalDialogText.ReadToEnd, Width_, Muted);
        root.Controls.Add(_readHint);

        _restorePoint = Check(PlanView.RestorePointText, _script.RestorePointAvailable);
        _restorePoint.Enabled = _script.RestorePointAvailable;
        root.Controls.Add(_restorePoint);
        root.Controls.Add(Text_(ApprovalDialogText.RestorePointSubLine(_script), Width_, Muted, indent: true));
        _share = Check(PlanView.ShareText(_script.AiName), true);
        root.Controls.Add(_share);
        root.Controls.Add(Text_(PlanView.ShareNote, Width_, Muted, indent: true));

        var red = Text_(ApprovalDialogText.ScriptRed, Width_, Ink, bold: true);
        red.BackColor = Danger;
        red.Padding = new Padding(8, 6, 8, 6);
        red.Margin = new Padding(0, 10, 0, 0);
        root.Controls.Add(red);

        _tick = Check(PlanView.TickText, false);
        _tick.Margin = new Padding(0, 8, 0, 0);
        _tick.CheckedChanged += (_, _) => Tick();
        root.Controls.Add(_tick);

        _countdown = Text_("", Width_, Muted);
        root.Controls.Add(_countdown);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Width = Width_, Margin = new Padding(0, 12, 0, 0), BackColor = Surface,
        };
        _decline = MakeButton(ApprovalDialogText.Decline, primary: false);
        _approve = MakeButton(ApprovalDialogText.Approve, primary: true);
        _approve.Enabled = false;
        _decline.Click += (_, _) => Finish(_ended ? PlanDecision.Expired : Unapproved());
        _approve.Click += (_, _) =>
        {
            Tick();
            if (!_ended && CanApprove) Finish(PlanDecision.Approve);
        };
        buttons.Controls.Add(_decline);
        buttons.Controls.Add(_approve);
        root.Controls.Add(buttons);

        Controls.Add(root);
        AcceptButton = _decline;
        CancelButton = _decline;
        ActiveControl = _decline;
        _timer.Tick += (_, _) => Tick();
        Tick();
    }

    private bool DelayOver => _clock() - _shownAt >= _view.Delay;

    /// <summary>Approve needs the delay, the tick and the script read to its end.</summary>
    private bool CanApprove => DelayOver && _tick.Checked && _readToEnd;

    private PlanDecision Unapproved() => !_tick.Checked || !_readToEnd ? PlanDecision.NotTicked : PlanDecision.Decline;

    /// <summary>The last line of the box is visible (always true when the script fits without scrolling).</summary>
    internal bool AtEnd()
    {
        if (!_box.IsHandleCreated) return false;
        var lastLine = _box.GetLineFromCharIndex(Math.Max(0, _box.TextLength - 1));
        var bottom = _box.GetCharIndexFromPosition(new Point(1, Math.Max(0, _box.ClientSize.Height - 2)));
        return _box.GetLineFromCharIndex(bottom) >= lastLine;
    }

    /// <summary>Moves the box to its last line (the user's scroll, for the tests).</summary>
    internal void ScrollToEnd()
    {
        _box.SelectionStart = _box.TextLength;
        _box.ScrollToCaret();
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
            _tick.Enabled = false;
            _decline.Text = ApprovalDialogText.Close;
            _timer.Stop();
            return;
        }
        if (!_readToEnd && AtEnd()) _readToEnd = true; // once read to the end, it stays read (the content never changes)
        _readHint.Visible = !_readToEnd;
        _approve.Enabled = CanApprove;
        if (Decision is not (PlanDecision.Approve or PlanDecision.Expired)) Decision = Unapproved();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _decline.Focus();
        if (!_ended) _timer.Start();
        Tick();
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (ApprovalKeys.Declines(keyData)) { _decline.PerformClick(); return true; }
        return base.ProcessDialogKey(keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
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

    private CheckBox Check(string text, bool on) => new()
    {
        Text = text, Checked = on, UseMnemonic = false, AutoSize = true, ForeColor = Ink, Margin = new Padding(0, 6, 0, 0),
    };

    private static Label Text_(string text, int width, Color color, float size = 10f, bool bold = false, bool italic = false, bool indent = false) => new()
    {
        Text = text, UseMnemonic = false, AutoSize = true, MaximumSize = new Size(width, 0), ForeColor = color,
        Font = new Font("Segoe UI", size, (bold ? FontStyle.Bold : FontStyle.Regular) | (italic ? FontStyle.Italic : FontStyle.Regular)),
        Margin = new Padding(indent ? 20 : 0, 2, 0, 2),
    };

    private static Button MakeButton(string text, bool primary)
    {
        var b = new Button
        {
            Text = text, UseMnemonic = false, Size = new Size(104, 34), FlatStyle = FlatStyle.Flat, BackColor = primary ? Ink : Field,
            ForeColor = primary ? Surface : Ink, UseVisualStyleBackColor = false, Margin = new Padding(8, 0, 0, 0),
        };
        b.FlatAppearance.BorderColor = primary ? Ink : Color.FromArgb(0x6b, 0x73, 0x83);
        return b;
    }

    private static IEnumerable<Control> AllControls(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var inner in AllControls(c)) yield return inner;
        }
    }
}

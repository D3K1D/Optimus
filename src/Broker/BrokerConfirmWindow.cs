namespace OptimizationTool.Broker;

/// <summary>
/// The real confirmation window (m9 §3.5; design README "Windows dialogs", "Administrator changes", v57). It runs in the
/// elevated broker, so unelevated processes can't click it. Shows only <see cref="BrokerConfirmView.Lines"/>; Cancel is the
/// default and Esc and Enter cancel; Apply needs a deliberate click, is enabled after a short delay (800 ms; 3 s when flagged)
/// and, when flagged, only with the never-pre-ticked tick. Closing it or 3 minutes without an answer cancels. Never shown by
/// tests.
/// </summary>
internal sealed class BrokerConfirmWindow : IBrokerConfirm
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    public Task<bool> AskAsync(BrokerConfirmView view, CancellationToken ct)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                using var form = new ConfirmForm(view);
                using var reg = ct.Register(() => { try { form.BeginInvoke(form.Close); } catch (InvalidOperationException) { } });
                Application.Run(form);
                done.TrySetResult(form.Applied);
            }
            catch (Exception) { done.TrySetResult(false); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return done.Task;
    }

    /// <summary>Esc and Enter mean Cancel on the broker's windows (design v57): Apply must be clicked, even when it has focus.</summary>
    internal static bool Cancels(Keys keyData) => (keyData & ~Keys.Modifiers) is Keys.Enter or Keys.Escape;

    internal sealed class ConfirmForm : Form
    {
        private static readonly Color DangerInk = Color.FromArgb(0xb4, 0x23, 0x18);
        private readonly Button _apply = new() { Text = BrokerConfirmView.ApplyText, AutoSize = true, Enabled = false, UseMnemonic = false };
        private readonly Button _cancel = new() { Text = BrokerConfirmView.CancelText, AutoSize = true, DialogResult = DialogResult.Cancel, UseMnemonic = false };
        private readonly CheckBox? _tick;
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 200 };
        private readonly DateTime _shown = DateTime.UtcNow;
        private readonly TimeSpan _delay;

        public bool Applied { get; private set; }
        internal Button ApplyButton => _apply;
        internal Button CancelButton_ => _cancel;
        internal CheckBox? TickBox => _tick;

        public ConfirmForm(BrokerConfirmView view)
        {
            _delay = view.Delay;
            Text = BrokerConfirmView.TitleBar;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Font = new Font("Segoe UI", 10f);
            Padding = new Padding(16);
            AcceptButton = _cancel; // Enter cancels.
            CancelButton = _cancel; // Esc cancels.

            var root = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Fill };
            var list = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoScroll = true, MaximumSize = new Size(480, 420) };
            var red = new List<Label>();
            foreach (var line in view.Lines())
            {
                switch (line.Kind)
                {
                    case BrokerLineKind.Heading: root.Controls.Add(Line(line.Text, bold: true, size: 12f)); break;
                    case BrokerLineKind.Explanation: root.Controls.Add(Line(line.Text)); break;
                    case BrokerLineKind.Red: red.Add(Line(line.Text, bold: true, color: DangerInk)); break;
                    case BrokerLineKind.StepName: list.Controls.Add(Line(line.Text, bold: true, top: 8)); break;
                    default: list.Controls.Add(Line(line.Text)); break;
                }
            }
            root.Controls.Add(list);
            foreach (var l in red) root.Controls.Add(l);
            if (view.NeedsTick)
            {
                _tick = new CheckBox { Text = BrokerConfirmView.TickText, AutoSize = true, Checked = false, UseMnemonic = false };
                _tick.CheckedChanged += (_, _) => Update_();
                root.Controls.Add(_tick);
            }
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
            buttons.Controls.Add(_cancel);
            buttons.Controls.Add(_apply);
            root.Controls.Add(buttons);
            Controls.Add(root);
            ActiveControl = _cancel;

            _apply.Click += (_, _) => { if (_apply.Enabled) { Applied = true; Close(); } };
            _cancel.Click += (_, _) => Close(); // the form runs modeless (Application.Run): a DialogResult alone doesn't close it
            _timer.Tick += (_, _) => { if (DateTime.UtcNow - _shown > Timeout) Close(); Update_(); };
            Shown += (_, _) => { _cancel.Focus(); _timer.Start(); };
            FormClosed += (_, _) => _timer.Stop();
        }

        private void Update_() => _apply.Enabled = DateTime.UtcNow - _shown >= _delay && (_tick?.Checked ?? true);

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (Cancels(keyData)) { _cancel.PerformClick(); return true; }
            return base.ProcessDialogKey(keyData);
        }

        private static Label Line(string text, bool bold = false, float size = 10f, Color? color = null, int top = 2)
        {
            var l = new Label
            {
                Text = text, AutoSize = true, MaximumSize = new Size(460, 0), UseMnemonic = false,
                Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), Margin = new Padding(0, top, 0, 2),
            };
            if (color is { } c) l.ForeColor = c;
            return l;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }
    }
}

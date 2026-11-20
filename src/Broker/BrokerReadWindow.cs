namespace OptimizationTool.Broker;

/// <summary>
/// The helper's real read window (m9 §17.2; Design System v77 "Administrator checks"). Elevated, so unelevated processes
/// can't click it. Shows only <see cref="BrokerReadView.Lines"/>; Cancel is the default and Esc and Enter cancel; Read
/// needs a click, with no tick and no delay. Closing it, a stop from the host, or 3 minutes without an answer cancels.
/// Never shown by tests.
/// </summary>
internal sealed class BrokerReadWindow : IBrokerReadConfirm
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    public Task<bool> AskAsync(BrokerReadView view, CancellationToken ct)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                using var form = new ReadForm(view);
                using var reg = ct.Register(() => { try { form.BeginInvoke(form.Close); } catch (InvalidOperationException) { } });
                Application.Run(form);
                done.TrySetResult(form.Accepted && !ct.IsCancellationRequested);
            }
            catch (Exception) { done.TrySetResult(false); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return done.Task;
    }

    internal sealed class ReadForm : Form
    {
        private readonly Button _read = new() { Text = BrokerReadView.ReadText, AutoSize = true, UseMnemonic = false };
        private readonly Button _cancel = new() { Text = BrokerReadView.CancelText, AutoSize = true, DialogResult = DialogResult.Cancel, UseMnemonic = false };
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
        private readonly DateTime _shown = DateTime.UtcNow;

        public bool Accepted { get; private set; }

        public ReadForm(BrokerReadView view)
        {
            Text = BrokerReadView.TitleBar;
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
            foreach (var line in view.Lines())
                root.Controls.Add(line.Kind switch
                {
                    BrokerLineKind.Heading => Line(line.Text, bold: true, size: 12f),
                    BrokerLineKind.StepName => Line(line.Text, bold: true, top: 8),
                    BrokerLineKind.Notice => Line(line.Text, top: 10),
                    _ => Line(line.Text),
                });
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
            buttons.Controls.Add(_cancel);
            buttons.Controls.Add(_read);
            root.Controls.Add(buttons);
            Controls.Add(root);
            ActiveControl = _cancel;

            _read.Click += (_, _) => { Accepted = true; Close(); };
            _cancel.Click += (_, _) => Close();
            _timer.Tick += (_, _) => { if (DateTime.UtcNow - _shown > Timeout) Close(); };
            Shown += (_, _) => { _cancel.Focus(); _timer.Start(); };
            FormClosed += (_, _) => _timer.Stop();
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (BrokerConfirmWindow.Cancels(keyData)) { _cancel.PerformClick(); return true; }
            return base.ProcessDialogKey(keyData);
        }

        private static Label Line(string text, bool bold = false, float size = 10f, int top = 2) => new()
        {
            Text = text, AutoSize = true, MaximumSize = new Size(460, 0), UseMnemonic = false,
            Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), Margin = new Padding(0, top, 0, 2),
        };

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }
    }
}

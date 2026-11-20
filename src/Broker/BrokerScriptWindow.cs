using System.Globalization;
using System.Text;

namespace OptimizationTool.Broker;

/// <summary>
/// The broker's own script window (m9 §5.4; design README "Windows dialogs", "Administrator changes" item 5, v57), elevated
/// so unelevated processes can't click it. Shows only <see cref="BrokerScriptView"/>: the heading, the fixed line, the
/// policy line, "Found by Optimus:" and the warnings, the full script with line numbers (read-only, monospace, tabs as four
/// spaces, nothing cut), the red "Can’t be undone" and the never-pre-ticked tick (design v62). Cancel is the default and Esc
/// and Enter cancel; Run needs the tick and is enabled 3 s after the window opens. Closing it or 3 minutes without an answer
/// cancels. Never shown by tests.
/// </summary>
internal sealed class BrokerScriptWindow : IBrokerScriptConfirm
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    public Task<bool> AskAsync(BrokerScriptView view, CancellationToken ct)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                using var form = new ScriptForm(view);
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

    /// <summary>The script as the window shows it: "  12  text", tabs as four spaces.</summary>
    internal static string Numbered(string text)
    {
        var lines = text.Split("\r\n");
        var width = lines.Length.ToString(CultureInfo.InvariantCulture).Length;
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(width)).Append("  ").Append(lines[i].Replace("\t", "    ", StringComparison.Ordinal)).Append("\r\n");
        return sb.ToString();
    }

    internal sealed class ScriptForm : Form
    {
        private static readonly Color DangerInk = Color.FromArgb(0xb4, 0x23, 0x18);
        private readonly Button _run = new() { Text = BrokerScriptView.RunText, AutoSize = true, Enabled = false, UseMnemonic = false };
        private readonly Button _cancel = new() { Text = BrokerScriptView.CancelText, AutoSize = true, DialogResult = DialogResult.Cancel, UseMnemonic = false };
        private readonly CheckBox _tick = new() { Text = BrokerScriptView.TickText, AutoSize = true, Checked = false, UseMnemonic = false };
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 200 };
        private readonly DateTime _shown = DateTime.UtcNow;

        public bool Applied { get; private set; }
        internal Button ApplyButton => _run;
        internal Button CancelButton_ => _cancel;
        internal CheckBox TickBox => _tick;

        public ScriptForm(BrokerScriptView view)
        {
            Text = BrokerScriptView.TitleBar;
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
            foreach (var line in view.HeaderLines())
                root.Controls.Add(line.Kind switch
                {
                    BrokerLineKind.Heading => Line(line.Text, bold: true, size: 12f),
                    BrokerLineKind.FoundBy or BrokerLineKind.Warning => Line(line.Text, bold: true),
                    _ => Line(line.Text),
                });
            root.Controls.Add(new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = true, Width = 640, Height = 360,
                Font = new Font(FontFamily.GenericMonospace, 9.5f), Text = Numbered(view.Text), TabStop = true,
            });
            foreach (var line in view.FooterLines()) root.Controls.Add(Line(line.Text, bold: true, color: line.Kind == BrokerLineKind.Red ? DangerInk : null));
            _tick.CheckedChanged += (_, _) => Update_();
            root.Controls.Add(_tick);
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
            buttons.Controls.Add(_cancel);
            buttons.Controls.Add(_run);
            root.Controls.Add(buttons);
            Controls.Add(root);
            ActiveControl = _cancel;

            _run.Click += (_, _) => { if (_run.Enabled) { Applied = true; Close(); } };
            _cancel.Click += (_, _) => Close(); // the form runs modeless (Application.Run): a DialogResult alone doesn't close it
            _timer.Tick += (_, _) => { if (DateTime.UtcNow - _shown > Timeout) Close(); Update_(); };
            Shown += (_, _) => { _cancel.Focus(); _timer.Start(); };
            FormClosed += (_, _) => _timer.Stop();
        }

        private void Update_() => _run.Enabled = DateTime.UtcNow - _shown >= BrokerScriptView.ApplyDelay && _tick.Checked;

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (BrokerConfirmWindow.Cancels(keyData)) { _cancel.PerformClick(); return true; }
            return base.ProcessDialogKey(keyData);
        }

        private static Label Line(string text, bool bold = false, float size = 10f, Color? color = null)
        {
            var l = new Label
            {
                Text = text, AutoSize = true, MaximumSize = new Size(640, 0), UseMnemonic = false,
                Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), Margin = new Padding(0, 2, 0, 2),
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

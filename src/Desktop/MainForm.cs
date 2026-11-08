using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using OptimizationTool.Desktop.Backend;
using OptimizationTool.Desktop.Bridge;

namespace OptimizationTool.Desktop;

/// <summary>The single app window: one hardened WebView2 serving ui/ from the virtual app origin.</summary>
internal sealed class MainForm : Form
{
    /// <summary>tokens.css dark --ground.</summary>
    public static readonly Color Ground = Color.FromArgb(0x14, 0x16, 0x1b);

    private readonly DesktopOptions _options;
    private readonly DesktopLog _log;
    private readonly BackendConnection _backend;
    private readonly BridgeDispatcher _dispatcher;
    private readonly ClientConnector _clients;
    private readonly WebView2 _webView;
    private readonly CancellationTokenSource _closing = new();
    private int _documentGeneration;
    private bool _documentReady;

    public MainForm(DesktopOptions options, DesktopLog log, BackendConnection backend)
    {
        _options = options;
        _log = log;
        _backend = backend;
        _clients = new ClientConnector(new ProcessRunner(), CliLocator.System, new WinFormsClientConfirmPrompt(this),
            ClientConnector.DefaultBridgePath, installer: new ConsoleInstallLauncher(), opener: new ConsoleInstallLauncher());
        _dispatcher = new BridgeDispatcher(new BridgeRouter(), backend, log,
            apiKeys: new ApiKeyRequest(new WinFormsApiKeyPrompt(this)),
            approvals: new PlanApproval(new WinFormsPlanPrompt(this)),
            clients: _clients);

        Text = "Optimization Tool";
        BackColor = Ground;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1280, 800);
        MinimumSize = new Size(960, 640);

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Ground,
            AllowExternalDrop = false,
        };
        Controls.Add(_webView);

        _backend.StateChanged += (state, detail) => OnUi(() => PostBackendState(state, detail));
        // The host keeps the CLI reports in memory: a restarted host gets the shell's last ones again (m5-chat §2).
        _backend.StateChanged += (state, detail) =>
        {
            if (state == BackendState.Ready) _ = ResendClientReportsAsync();
        };
        _backend.EventReceived += evt => OnUi(() => Post(BridgeMessages.Event(evt)));
    }

    private async Task ResendClientReportsAsync()
    {
        try { await _clients.ResendReportsAsync(_backend, _log, _closing.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.UseDarkTitleBar(Handle);
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            await InitializeWebViewAsync();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            _log.Error("webview: WebView2 Runtime not found");
            MessageBox.Show(this, "Microsoft Edge WebView2 Runtime is required.", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }
        catch (Exception ex)
        {
            _log.Error($"webview: initialization failed ({ex.GetType().Name}: {ex.Message})");
            MessageBox.Show(this, "The window could not start.", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }
        _backend.Start();
    }

    private async Task InitializeWebViewAsync()
    {
        var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OptimizationTool", "WebView2");
        var envOptions = new CoreWebView2EnvironmentOptions
        {
            AreBrowserExtensionsEnabled = false,
            AllowSingleSignOnUsingOSPrimaryAccount = false,
        };
        var env = await CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: userData, options: envOptions);
        await _webView.EnsureCoreWebView2Async(env);
        var core = _webView.CoreWebView2;

        var s = core.Settings;
        s.AreHostObjectsAllowed = false;
        s.IsWebMessageEnabled = true;
        s.IsStatusBarEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.IsBuiltInErrorPageEnabled = true;
        s.AreDefaultScriptDialogsEnabled = true;
#if DEBUG
        s.AreDevToolsEnabled = true;
        s.AreDefaultContextMenusEnabled = true;
#else
        s.AreDevToolsEnabled = false;
        s.AreDefaultContextMenusEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
#endif

        var uiDir = _options.UiDir ?? Path.Combine(AppContext.BaseDirectory, "ui");
        if (!File.Exists(Path.Combine(uiDir, "index.html"))) _log.Warn($"webview: no index.html in {uiDir}");
        core.SetVirtualHostNameToFolderMapping(BridgeRouter.AppHost, uiDir, CoreWebView2HostResourceAccessKind.Deny);

        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.FrameNavigationStarting += OnFrameNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.WebMessageReceived += OnWebMessageReceived;
        core.FrameCreated += OnFrameCreated;
        core.PermissionRequested += (_, e) => { e.State = CoreWebView2PermissionState.Deny; e.Handled = true; _log.Warn($"webview: denied permission {e.PermissionKind}"); };
        core.DownloadStarting += (_, e) => { e.Cancel = true; e.Handled = true; _log.Warn("webview: blocked download"); };
        core.LaunchingExternalUriScheme += (_, e) => { e.Cancel = true; _log.Warn("webview: blocked external URI scheme"); };
        core.BasicAuthenticationRequested += (_, e) => { e.Cancel = true; };
        core.ServerCertificateErrorDetected += (_, e) => { e.Action = CoreWebView2ServerCertificateErrorAction.Cancel; };
        core.ProcessFailed += OnProcessFailed;

        core.Navigate(BridgeRouter.StartUrl);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!BridgeRouter.IsAppOrigin(e.Uri))
        {
            e.Cancel = true;
            _log.Warn($"webview: blocked top-level navigation to {SafeOrigin(e.Uri)}");
            return;
        }
        // A new document: replies to the previous one's calls are dropped.
        _documentGeneration++;
        _documentReady = false;
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _log.Info($"webview: navigation completed success={e.IsSuccess} status={e.WebErrorStatus}");
        if (!e.IsSuccess) return;
        _documentReady = true;
        var (state, detail) = _backend.Current;
        PostBackendState(state, detail);
    }

    private void OnFrameCreated(object? sender, CoreWebView2FrameCreatedEventArgs e)
    {
        // No host objects are ever added to frames. Frame messages never reach the backend.
        var frame = e.Frame;
        frame.NavigationStarting += (_, args) =>
        {
            if (BridgeRouter.IsAppOrigin(args.Uri) || args.Uri is "about:blank" or "about:srcdoc") return;
            args.Cancel = true;
            _log.Warn($"webview: blocked frame navigation to {SafeOrigin(args.Uri)}");
        };
        frame.WebMessageReceived += (_, _) => _log.Warn("bridge: ignored message (frame)");
        frame.PermissionRequested += (_, args) => { args.State = CoreWebView2PermissionState.Deny; args.Handled = true; };
    }

    private void OnFrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (BridgeRouter.IsAppOrigin(e.Uri) || e.Uri is "about:blank" or "about:srcdoc") return;
        e.Cancel = true;
        _log.Warn($"webview: blocked frame navigation to {SafeOrigin(e.Uri)}");
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true; // Never open a window inside the app.
        if (!e.IsUserInitiated || !Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) || BridgeRouter.IsAppOrigin(e.Uri))
        {
            _log.Warn($"webview: blocked new window to {SafeOrigin(e.Uri)}");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            _log.Info($"webview: opened {SafeOrigin(e.Uri)} in the system browser");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log.Warn($"webview: could not open system browser ({ex.Message})");
        }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // This handler is on the top-level CoreWebView2 only; frame messages arrive on CoreWebView2Frame.
        try
        {
            var generation = _documentGeneration;
            string? json;
            try { json = e.WebMessageAsJson; } catch (ArgumentException) { json = null; }
            var reply = await _dispatcher.HandleAsync(json, e.Source, isTopLevel: true, _closing.Token);
            if (reply is not null && generation == _documentGeneration) Post(reply);
        }
        catch (Exception ex)
        {
            _log.Error($"bridge: handler failed ({ex.GetType().Name})");
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _log.Error($"webview: process failed {e.ProcessFailedKind}");
        if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
        {
            try { _webView.CoreWebView2?.Navigate(BridgeRouter.StartUrl); } catch (InvalidOperationException) { }
        }
    }

    private void PostBackendState(BackendState state, JsonNode? detail)
    {
        if (!_documentReady) return; // Sent on NavigationCompleted instead.
        Post(BridgeMessages.Backend(BackendConnection.StateName(state), detail));
    }

    private void Post(string json)
    {
        var core = _webView.CoreWebView2;
        if (core is null || _closing.IsCancellationRequested) return;
        if (!BridgeRouter.IsAppOrigin(core.Source)) return; // Defense in depth: only the app document receives messages.
        try { core.PostWebMessageAsJson(json); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _log.Warn($"bridge: post failed ({ex.GetType().Name})");
        }
    }

    private void OnUi(Action action)
    {
        if (IsDisposed || _closing.IsCancellationRequested) return;
        try
        {
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }
        catch (InvalidOperationException) { } // Handle gone during shutdown.
    }

    private static string SafeOrigin(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Authority}" : "<invalid>";

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closing.Cancel();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _webView.Dispose();
            _closing.Dispose();
        }
        base.Dispose(disposing);
    }
}

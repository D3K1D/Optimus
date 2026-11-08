using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json.Nodes;
using OptimizationTool.Desktop.Bridge;
using OptimizationTool.Transport;

namespace OptimizationTool.Desktop.Backend;

public enum BackendState { Connecting, Ready, Reconnecting, Incompatible }

public sealed record BackendOptions
{
    public required string PipeName { get; init; }
    /// <summary>Absolute path of the expected server image. The pipe server's image must match it unless <see cref="AllowAnyHost"/>.</summary>
    public required string HostPath { get; init; }
    /// <summary>Launch <see cref="HostPath"/> when the pipe is not available.</summary>
    public bool LaunchHost { get; init; } = true;
    public IReadOnlyList<string> HostArguments { get; init; } = [];
    public bool AllowAnyHost { get; init; }
    public string ClientVersion { get; init; } = typeof(BackendConnection).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    /// <summary>Retry budget before the state detail reports "unavailable"; retries continue after it with a slower cadence.</summary>
    public TimeSpan ConnectBudget { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Owns the pipe connection to the per-user host: connect (launching the host if needed), verify the server image,
/// session.hello, and reconnect after a drop. contracts/protocol-v1.md §1, §2, §7.
/// </summary>
public sealed class BackendConnection(BackendOptions options, DesktopLog log) : IBackend, IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _stateLock = new();
    private volatile JsonRpcClient? _client;
    private Task? _loop;
    private Process? _hostProcess;
    private BackendState _state = BackendState.Connecting;
    private JsonNode? _detail;

    /// <summary>Raised on a background thread when state or detail changes.</summary>
    public event Action<BackendState, JsonNode?>? StateChanged;

    /// <summary>Raised on the pipe read loop with the params of each events.notify.</summary>
    public event Action<JsonNode?>? EventReceived;

    public (BackendState State, JsonNode? Detail) Current
    {
        get { lock (_stateLock) return (_state, _detail?.DeepClone()); }
    }

    public static string StateName(BackendState s) => s switch
    {
        BackendState.Connecting => "connecting",
        BackendState.Ready => "ready",
        BackendState.Reconnecting => "reconnecting",
        BackendState.Incompatible => "incompatible",
        _ => "connecting",
    };

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    public async Task<JsonObject> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
    {
        var client = _client;
        if (client is null) throw new BackendUnavailableException("Not connected.");
        try
        {
            return await client.CallRawAsync(method, parameters, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            throw new BackendUnavailableException("Connection dropped.", ex);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var first = true;
        while (!ct.IsCancellationRequested)
        {
            SetState(first ? BackendState.Connecting : BackendState.Reconnecting, null);
            first = false;

            JsonRpcClient client;
            try { client = await ConnectWithRetryAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Disconnected += cause =>
            {
                log.Warn($"backend: disconnected{(cause is null ? "" : " (" + cause.GetType().Name + ")")}");
                dropped.TrySetResult();
            };
            client.Notification += OnNotification;

            HelloOutcome outcome;
            JsonNode? detail;
            try { (outcome, detail) = await HelloAsync(client, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                return;
            }

            if (outcome == HelloOutcome.Incompatible)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                SetState(BackendState.Incompatible, detail);
                return; // An update is needed; retrying cannot help.
            }
            if (outcome == HelloOutcome.Failed)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                try { await Task.Delay(500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                continue;
            }

            _client = client;
            SetState(BackendState.Ready, null);
            try { await dropped.Task.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _client = null;
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private enum HelloOutcome { Ok, Incompatible, Failed }

    private async Task<(HelloOutcome, JsonNode?)> HelloAsync(JsonRpcClient client, CancellationToken ct)
    {
        var hello = new JsonObject
        {
            ["protocol"] = new JsonObject { ["major"] = 1, ["minor"] = 3 },
            ["client"] = new JsonObject { ["kind"] = "desktop", ["version"] = options.ClientVersion },
            ["features"] = new JsonArray(),
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.HelloTimeout);
        JsonObject response;
        try
        {
            response = await client.CallRawAsync("session.hello", hello, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.Warn("backend: session.hello timed out");
            return (HelloOutcome.Failed, null);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            log.Warn($"backend: session.hello failed ({ex.GetType().Name})");
            return (HelloOutcome.Failed, null);
        }

        if (response["error"] is JsonObject error)
        {
            var code = error["code"] is JsonValue c && c.TryGetValue<int>(out var ci) ? ci : 0;
            log.Warn($"backend: session.hello error {code}");
            return code == -32010 ? (HelloOutcome.Incompatible, error["data"]?.DeepClone()) : (HelloOutcome.Failed, null);
        }

        var major = response["result"]?["protocol"]?["major"] is JsonValue m && m.TryGetValue<int>(out var mi) ? mi : -1;
        if (major != 1)
        {
            log.Warn($"backend: server protocol major {major}, expected 1");
            return (HelloOutcome.Incompatible, new JsonObject
            {
                ["supported"] = new JsonObject { ["major"] = 1, ["minor"] = 0 },
                ["action"] = "Update Optimus",
            });
        }
        log.Info("backend: session.hello ok");
        return (HelloOutcome.Ok, null);
    }

    private void OnNotification(string method, JsonNode? parameters)
    {
        if (method != "events.notify") return;
        try { EventReceived?.Invoke(parameters?.DeepClone()); }
        catch (Exception ex) { log.Error($"backend: event handler failed ({ex.GetType().Name})"); }
    }

    private async Task<JsonRpcClient> ConnectWithRetryAsync(CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var delay = TimeSpan.FromMilliseconds(100);
        var reportedUnavailable = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var (client, failure) = await TryConnectOnceAsync(ct).ConfigureAwait(false);
            if (client is not null)
            {
                log.Info($"backend: connected after {started.ElapsedMilliseconds} ms");
                return client;
            }

            if (failure == ConnectFailure.NotListening && options.LaunchHost
                && (_hostProcess is null || HasExited(_hostProcess)) && !PipeExists(options.PipeName))
                LaunchHost();

            var overBudget = started.Elapsed > options.ConnectBudget;
            if (overBudget && !reportedUnavailable)
            {
                reportedUnavailable = true;
                log.Warn($"backend: not available after {options.ConnectBudget.TotalSeconds:0} s ({failure}), still retrying");
                var current = Current.State;
                SetState(current == BackendState.Ready ? BackendState.Reconnecting : current,
                    JsonValue.Create(failure == ConnectFailure.Untrusted ? "untrusted_host" : "unavailable"));
            }

            await Task.Delay(delay, ct).ConfigureAwait(false);
            var cap = overBudget ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(1);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, cap.Ticks));
        }
    }

    private enum ConnectFailure { None, NotListening, Untrusted, Error }

    private async Task<(JsonRpcClient?, ConnectFailure)> TryConnectOnceAsync(CancellationToken ct)
    {
        // Identification: the server may identify this client but not impersonate it.
        var pipe = new NamedPipeClientStream(".", options.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(TimeSpan.FromMilliseconds(300), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return (null, ConnectFailure.NotListening);
        }
        catch (UnauthorizedAccessException)
        {
            // CurrentUserOnly: the pipe exists but is owned by someone else.
            await pipe.DisposeAsync().ConfigureAwait(false);
            log.Error("backend: pipe owner is not the current user; refusing");
            return (null, ConnectFailure.Untrusted);
        }
        catch (IOException ex)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            log.Warn($"backend: connect failed ({ex.GetType().Name})");
            return (null, ConnectFailure.Error);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        if (!VerifyServer(pipe))
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return (null, ConnectFailure.Untrusted);
        }
        return (new JsonRpcClient(pipe), ConnectFailure.None);
    }

    private bool VerifyServer(NamedPipeClientStream pipe)
    {
        string? image;
        uint pid;
        try
        {
            pid = NativeMethods.ServerProcessId(pipe);
            image = NativeMethods.ProcessImagePath(pid);
        }
        catch (IOException ex)
        {
            log.Error($"backend: cannot identify pipe server ({ex.Message})");
            return options.AllowAnyHost;
        }

        var expected = Path.GetFullPath(options.HostPath);
        var matches = image is not null && string.Equals(Path.GetFullPath(image), expected, StringComparison.OrdinalIgnoreCase);
        if (matches) return true;
        if (options.AllowAnyHost)
        {
            log.Warn($"backend: pipe server pid {pid} image {image ?? "<unknown>"} is not the expected host; allowed by --dev-allow-any-host");
            return true;
        }
        log.Error($"backend: pipe server pid {pid} image {image ?? "<unknown>"} is not {expected}; refusing");
        return false;
    }

    private void LaunchHost()
    {
        var path = Path.GetFullPath(options.HostPath);
        if (!File.Exists(path))
        {
            log.Error($"backend: host not found at {path}");
            SetDetail(JsonValue.Create("host_missing"));
            return;
        }
        try
        {
            var psi = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(path)!,
            };
            foreach (var a in options.HostArguments) psi.ArgumentList.Add(a);
            _hostProcess?.Dispose();
            _hostProcess = Process.Start(psi);
            log.Info($"backend: launched host pid {_hostProcess?.Id}");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            log.Error($"backend: host launch failed ({ex.Message})");
            SetDetail(JsonValue.Create("host_launch_failed"));
        }
    }

    /// <summary>Lists \\.\pipe\ without opening the pipe (opening would consume a server instance). A busy pipe still exists.</summary>
    private static bool PipeExists(string name)
    {
        try { return Directory.EnumerateFiles(@"\\.\pipe\", name).Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static bool HasExited(Process p)
    {
        try { return p.HasExited; } catch (InvalidOperationException) { return true; }
    }

    private void SetDetail(JsonNode? detail)
    {
        BackendState state;
        lock (_stateLock) state = _state;
        SetState(state, detail);
    }

    private void SetState(BackendState state, JsonNode? detail)
    {
        lock (_stateLock)
        {
            if (_state == state && JsonNode.DeepEquals(_detail, detail)) return;
            _state = state;
            _detail = detail?.DeepClone();
        }
        log.Info($"backend: state {StateName(state)}{(detail is null ? "" : " " + detail.ToJsonString())}");
        try { StateChanged?.Invoke(state, detail?.DeepClone()); }
        catch (Exception ex) { log.Error($"backend: state handler failed ({ex.GetType().Name})"); }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        if (_client is { } c) await c.DisposeAsync().ConfigureAwait(false);
        _hostProcess?.Dispose(); // The host is a per-user backend; it is not stopped with the window.
        _cts.Dispose();
    }
}

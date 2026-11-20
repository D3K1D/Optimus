using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Engine.Actions.Scripts;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Windows.Actions.Broker;
using OptimizationTool.Transport.Logging;

namespace OptimizationTool.Host.Elevation;

/// <summary>
/// Result of asking Windows to start the broker elevated. <see cref="Process"/>: the launched process, owned by whoever
/// receives this (disposing it closes the handle); null when Windows gave no handle (or nothing started).
/// </summary>
public sealed record BrokerLaunch(bool Started, bool Cancelled, uint ProcessId, string? Error = null, IBrokerProcess? Process = null);

/// <summary>
/// The launched broker process, as a waiter (m9 §13 R2): the host keeps it so locks can follow the process, not the pipe.
/// Production wraps the ShellExecuteEx process handle; tests use a controllable fake.
/// </summary>
public interface IBrokerProcess : IDisposable
{
    /// <summary>Completes when the process has exited (never because a pipe closed).</summary>
    Task WaitForExitAsync();

    bool HasExited { get; }

    /// <summary>The exit code once it has exited and before it is disposed (the developer log); null when unknown.</summary>
    int? ExitCode => null;
}

/// <summary>Starts the broker (ShellExecute "runas" in production; a fake in tests).</summary>
public interface IBrokerLauncher
{
    BrokerLaunch Launch(string brokerPath, string arguments);
}

/// <summary>
/// Host side of the elevation boundary (m4 §5, m9 §3.5). For each broker session (all administrator steps of one plan, in
/// the plan's order): create a fresh first-instance pipe (ACL: this user + Administrators, NETWORK denied), launch the
/// broker through UAC with only <c>--pipe NAME --nonce HEX</c>, accept only the launched process in this session and
/// account, send the v2 request (each step with the content hash the broker must recompute from live state), forward one
/// stop frame when asked, and map its answer per step. UAC cancelled → declined/uac_cancelled (nothing sent). No answer
/// after the request was sent → indeterminate with Dispatched (the engine reads the protected journal and live state).
/// </summary>
public sealed class BrokerElevation : IElevation, IElevatedReads
{
    private readonly string _brokerPath;
    private readonly IBrokerLauncher _launcher;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _responseTimeout;
    private readonly string _journalRoot;
    private readonly HashSet<string> _usedNonces = [];
    private readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>The broker's allowlist: the administrator handlers compiled into both host and broker (m9 §3.5).</summary>
    public static IReadOnlySet<string> AllowedActions { get; } = BrokerCatalog.ActionIds.ToHashSet(StringComparer.Ordinal);

    /// <param name="responseTimeout">The wait for the answer after the request is sent. Null: the broker's own window (3
    /// minutes) + 45 s per step, + 10 minutes when a step is long (a restore point).</param>
    public BrokerElevation(string? brokerPath = null, IBrokerLauncher? launcher = null, TimeSpan? connectTimeout = null, TimeSpan? responseTimeout = null,
        string? journalRoot = null)
    {
        _brokerPath = brokerPath ?? Path.Combine(AppContext.BaseDirectory, "OptimizationTool.Broker.exe");
        _launcher = launcher ?? new ShellExecuteLauncher();
        _connectTimeout = connectTimeout ?? TimeSpan.FromMinutes(3); // includes the time the user spends on the UAC prompt
        _responseTimeout = responseTimeout ?? TimeSpan.Zero;
        _journalRoot = journalRoot ?? BrokerJournalFiles.DefaultRoot;
    }

    public ElevatedJournal? Journal(string planId) => BrokerJournalFiles.Read(_journalRoot, planId);

    /// <summary>m9 §17.1/§17.2: administrator checks run only with --enable-admin-actions; off → every read is not_started/admin_unavailable.</summary>
    public bool AdminChecksEnabled { get; init; }

    /// <summary>Tests only: replaces every helper lifetime bound below (the fake helper's budget).</summary>
    internal TimeSpan? LifetimeBoundOverride { get; init; }

    // ---- the one-helper exclusion (m9 §17.2, §17.6; review A6)
    //
    // One helper at a time, for every connection type (request, script, read). Once a helper may have started, the
    // exclusion is held until its PROCESS has exited (SessionEnd), not until the pipe answers, bounded by the helper's own
    // lifetime budget so a hung helper, or one launched without a process handle, can't hold it forever. The helper's
    // budget (src/Broker/Program.cs) is 15 minutes from its start; once a session knows its work it sets it ONCE to that
    // work plus a minute (CancelAfter from that moment). That moment is at most the initial 15 minutes in, so the helper's
    // lifetime is at most 15 minutes + its extension + 1 minute; the bound adds a minute for the exit itself. The exclusion
    // is OWNED from acquisition, but the bound's clock starts only when Windows reports the launch (after the UAC prompt):
    // the helper's own budget starts with its process, so a long UAC wait must not shorten the bound. Every path after a
    // successful launch keeps tracking the process (a connection timeout or a rejected client included): no request was
    // sent there, so no plan locks follow it, but the exclusion does.
    //   request: 15 + (2 + Σ per step: 45 s, a long tool's deadline + 1, or 10 minutes for another long step) + 1 + 1 minutes
    //   script:  15 + (max(the run's time limit, 10) + 2) + 1 + 1 minutes
    //   read:    15 + (checks × 60 s + 1) + 1 + 1 minutes   (window ≤ 3 minutes, then the 60 s watchdog per check)
    // The locks of an administrator plan still follow ElevatedResult.Ended (held until the process exits, or the host restarts
    // without a handle); only the exclusion is bounded.

    public static readonly TimeSpan HelperBudget = TimeSpan.FromMinutes(15), ExitGrace = TimeSpan.FromMinutes(1);

    private static TimeSpan Bound(TimeSpan extension) => HelperBudget + extension + TimeSpan.FromMinutes(1) + ExitGrace;

    public static TimeSpan RequestLifetime(IEnumerable<ElevatedStep> steps) =>
        Bound(steps.Aggregate(TimeSpan.FromMinutes(2), (sum, s) => sum + (s.Deadline is { } d ? d + TimeSpan.FromMinutes(1) : s.Long ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(45))));

    public static TimeSpan ScriptLifetime(TimeSpan timeout) => Bound((timeout > TimeSpan.FromMinutes(10) ? timeout : TimeSpan.FromMinutes(10)) + TimeSpan.FromMinutes(2));

    public static TimeSpan ReadLifetime(int checks) => Bound(AdminChecks.CheckTimeout * checks + TimeSpan.FromMinutes(1));

    /// <summary>One helper launch as the exclusion sees it: set once Windows reports the launch started.</summary>
    private sealed class HelperLife
    {
        /// <summary>Completes when the helper process has exited; never without a process handle.</summary>
        public Task? Ended;
        /// <summary>When the launch was reported (the bound's clock).</summary>
        public long Since;
    }

    /// <summary>
    /// Releases the exclusion: at once when no helper was launched or it has already exited, otherwise when the helper
    /// process exits or its lifetime bound (counted from the launch) has passed, whichever is first.
    /// </summary>
    private void Release(HelperLife life, TimeSpan bound)
    {
        if (life.Ended is not { } ended || ended.IsCompleted)
        {
            _one.Release();
            return;
        }
        var left = (LifetimeBoundOverride ?? bound) - System.Diagnostics.Stopwatch.GetElapsedTime(life.Since);
        _ = ReleaseLaterAsync(ended, left > TimeSpan.Zero ? left : TimeSpan.Zero);
    }

    private async Task ReleaseLaterAsync(Task ended, TimeSpan left)
    {
        try
        {
            var first = await Task.WhenAny(ended, Task.Delay(left)).ConfigureAwait(false);
            if (first != ended) AppLog.Current.Warn("broker: helper not seen to exit within its lifetime budget; the next helper may start", ErrorSites.BrokerExitError);
        }
        finally { _one.Release(); }
    }

    /// <summary>
    /// The wait for the broker's answer once the request is sent: its window (3 minutes), then per step 45 s, or a long tool's
    /// own deadline plus a minute (the broker's watchdog), or 10 minutes for another long step (a restore point), plus a minute.
    /// </summary>
    public static TimeSpan ResponseWait(IEnumerable<ElevatedStep> steps) =>
        steps.Aggregate(TimeSpan.FromMinutes(4), (sum, s) => sum + (s.Deadline is { } d ? d + TimeSpan.FromMinutes(1) : s.Long ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(45)));

    /// <summary>Logs a broker refusal or an unknown result (with the caller's line) and returns it.</summary>
    private static ElevatedResult Logged(ElevatedResult result, string what, string site,
        [System.Runtime.CompilerServices.CallerFilePath] string file = "", [System.Runtime.CompilerServices.CallerLineNumber] int line = 0,
        [System.Runtime.CompilerServices.CallerMemberName] string member = "")
    {
        AppLog.Current.Write(LogLevel.Warn, "broker: " + what, site, null, file, line, member);
        return result;
    }

    private static ElevatedResult All(ElevatedRequest request, string state, string text, string code, string? message = null, bool dispatched = false) =>
        new([.. request.Steps.Select(s => (s.N, new ActionOutcome(state, new JsonObject(), text, code, message)))], null, dispatched);

    public async Task<ElevatedResult> RunAsync(ElevatedRequest request, CancellationToken stop, CancellationToken ct)
    {
        if (request.Steps.Count is 0 or > BrokerRequest.MaxSteps || request.Steps.Zip(request.Steps.Skip(1)).Any(p => p.First.N >= p.Second.N)
            || request.Steps.Any(s => s.N is < 1 or > BrokerRequest.MaxSteps))
            return Logged(All(request, "failed", "Nothing changed.", "bad_request", "This change isn't supported."), "refused reason=bad_request", ErrorSites.BrokerRefused);
        if (request.Steps.Any(s => !AllowedActions.Contains(s.ActionId)))
            return Logged(All(request, "failed", "Nothing changed.", "action_not_allowed", "This change can't run with administrator rights."), "refused reason=action_not_allowed", ErrorSites.BrokerRefused);
        if (!File.Exists(_brokerPath))
            return Logged(All(request, "failed", "Nothing changed.", "broker_missing", "The helper that makes this change isn't installed."), "refused reason=broker_missing", ErrorSites.BrokerRefused);

        await _one.WaitAsync(ct).ConfigureAwait(false);
        var life = new HelperLife();
        try
        {
            var pipeName = BrokerProtocol.NewPipeName();
            var nonce = BrokerProtocol.NewNonce();
            if (!_usedNonces.Add(nonce)) return Logged(All(request, "failed", "Nothing changed.", "nonce_reuse"), "refused reason=nonce_reuse", ErrorSites.BrokerRefused);
            var wire = new BrokerRequest(nonce, request.PlanId, request.PlanHash, [.. request.Steps.Select(BrokerStepRequest.From)]);
            var timeout = _responseTimeout > TimeSpan.Zero ? _responseTimeout : ResponseWait(request.Steps);
            return await RunOnceAsync(pipeName, nonce, wire, request, timeout, life, stop, ct).ConfigureAwait(false);
        }
        finally { Release(life, RequestLifetime(request.Steps)); }
    }

    /// <summary>
    /// Creates the first-instance pipe, launches the broker through UAC and accepts only the launched process in this
    /// session and account. Returns the connected pipe, or the refusal code (uac_cancelled, broker_not_started,
    /// broker_timeout, broker_mismatch, different_admin) with nothing sent. Once Windows reports the launch, the process is
    /// tracked in <paramref name="life"/> (its end, and the bound's clock) on EVERY path, refusals included; its handle is
    /// closed after it exits.
    /// </summary>
    private async Task<(NamedPipeServerStream? Pipe, string? Code)> OpenAsync(string pipeName, string nonce, HelperLife life, CancellationToken ct, Func<string?>? beforeLaunch = null)
    {
        // The broker itself writes no log (it is elevated; the data folder is writable by the user): the host logs its side.
        AppLog.Current.Info("broker: launch requested (UAC asked)");
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var opened = await OpenCoreAsync(pipeName, nonce, life, ct, beforeLaunch).ConfigureAwait(false);
        var ms = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        switch (opened.Code)
        {
            case null: AppLog.Current.Debug($"broker: connected ms={ms}"); break;
            case "uac_cancelled": AppLog.Current.Info($"broker: not started reason=uac_cancelled ms={ms}", ErrorSites.BrokerUacCancelled); break;
            case ReadStopped or ReadNotAdmitted: AppLog.Current.Info($"broker: not launched reason={opened.Code}"); break;
            default: AppLog.Current.Warn($"broker: not started reason={opened.Code} ms={ms}", ErrorSites.BrokerNotConnected); break;
        }
        return opened;
    }

    private async Task<(NamedPipeServerStream? Pipe, string? Code)> OpenCoreAsync(string pipeName, string nonce, HelperLife life, CancellationToken ct, Func<string?>? beforeLaunch)
    {
        var (selfUser, selfSession) = ClientIdentity.Current();
        // Created before launch, as the first instance: a squatter on the name makes this throw instead of us connecting to it.
        var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, Security(selfUser));
        var keep = false;
        try
        {
            string? notLaunched = null;
            var launch = await Task.Run(() =>
            {
                // m9 §17.2 (reads): the last word before UAC, with the exclusion held; a code here launches nothing.
                if (beforeLaunch?.Invoke() is { } code) { notLaunched = code; return new BrokerLaunch(false, false, 0); }
                return _launcher.Launch(_brokerPath, $"--pipe {pipeName} --nonce {nonce}");
            }, ct).ConfigureAwait(false);
            if (notLaunched is not null) return (null, notLaunched);
            if (launch.Cancelled || !launch.Started)
            {
                launch.Process?.Dispose();
                return (null, launch.Cancelled ? "uac_cancelled" : "broker_not_started");
            }
            // From here a helper process exists: the exclusion follows it, whatever happens next (review item 4), and the
            // bound's clock starts now, after the UAC prompt (review item 3).
            life.Since = System.Diagnostics.Stopwatch.GetTimestamp();
            life.Ended = SessionEnd(launch.Process);

            using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connect.CancelAfter(_connectTimeout);
            // Nothing was sent yet, so the broker can't have written anything.
            try { await pipe.WaitForConnectionAsync(connect.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return (null, "broker_timeout"); }

            // Only the process we launched, in our session. A different account is refused here and by the broker.
            ClientInfo client;
            try { client = ClientIdentity.Inspect(pipe); }
            catch (Win32Exception) { return (null, "broker_mismatch"); }
            var wrongImage = client.ImagePath is { } image
                && !string.Equals(Path.GetFullPath(image), Path.GetFullPath(_brokerPath), StringComparison.OrdinalIgnoreCase);
            if ((launch.ProcessId != 0 && client.ProcessId != launch.ProcessId) || client.SessionId != selfSession || wrongImage) return (null, "broker_mismatch");
            // A non-elevated host may not be able to read the elevated token; the broker (which can read ours) is the
            // authoritative check. When we can see a different account, refuse here too.
            if (client.User is not null && !client.User.Equals(selfUser)) return (null, "different_admin");
            keep = true;
            return (pipe, null);
        }
        finally
        {
            if (!keep) await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Never completes: a broker whose end can't be observed (see <see cref="SessionEnd"/>).</summary>
    private static readonly Task Unobservable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;

    /// <summary>
    /// m9 §13 R2: once the request may have reached the broker, the session ends when the broker PROCESS exits, never when
    /// the pipe closes or answers. The handle is closed after the exit. Without a process handle (Windows reported a start
    /// but gave none, or the wait itself failed) termination can't be established, so the returned task never completes and
    /// the locks and lease stay held until the host restarts. The broker's budget is not a release point for the LOCKS: it
    /// is a cooperative cancellation the session itself extends, not a hard kill, so a write stuck past it would still be
    /// running. Only the one-helper exclusion is bounded by that budget (m9 §17.6; see Release).
    /// </summary>
    private static Task SessionEnd(IBrokerProcess? process) => process is null ? Unobservable : WaitAndCloseAsync(process);

    private static async Task WaitAndCloseAsync(IBrokerProcess process)
    {
        var exited = false;
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            exited = true;
            // The broker's codes: 0 wrote, 1 nothing written, 2 bad arguments, 3 exception.
            switch (process.ExitCode)
            {
                case 0: AppLog.Current.Debug("broker: exit=0 (wrote)"); break;
                case 1: AppLog.Current.Debug("broker: exit=1 (nothing written)"); break;
                case 2: AppLog.Current.Warn("broker: exit=2 (bad arguments)", ErrorSites.BrokerExitError); break;
                case 3: AppLog.Current.Warn("broker: exit=3 (exception)", ErrorSites.BrokerExitError); break;
                case { } other: AppLog.Current.Warn($"broker: exit={other}", ErrorSites.BrokerExitError); break;
                case null: AppLog.Current.Debug("broker: exited (code unknown)"); break;
            }
        }
        catch (Exception) { AppLog.Current.Warn("broker: waiting for its exit failed; locks stay held", ErrorSites.BrokerExitError); }
        finally { process.Dispose(); }
        if (!exited) await Unobservable.ConfigureAwait(false);
    }

    private async Task<ElevatedResult> RunOnceAsync(string pipeName, string nonce, BrokerRequest wire, ElevatedRequest request, TimeSpan timeout,
        HelperLife life, CancellationToken stop, CancellationToken ct)
    {
        var (opened, refusal) = await OpenAsync(pipeName, nonce, life, ct).ConfigureAwait(false);
        switch (refusal)
        {
            case "uac_cancelled": return All(request, "declined", "You cancelled the Windows permission prompt. Nothing changed.", "uac_cancelled");
            case "broker_not_started": return All(request, "failed", "Nothing changed.", "broker_not_started", "Windows couldn't start the helper.");
            case "broker_timeout": return All(request, "failed", "The helper didn't start in time. Nothing changed.", "broker_timeout");
            case "broker_mismatch": return All(request, "failed", "Nothing changed.", "broker_mismatch", "The helper couldn't be verified.");
            case "different_admin": return All(request, "failed", "Nothing changed.", "different_admin", "Windows permission came from a different account. Use Windows Security instead.");
        }
        // From here the broker may write: every result carries the process's end, whatever happens on the pipe.
        var ended = life.Ended!;
        try { return await ExchangeAsync(opened!, nonce, wire, request, timeout, stop, ct).ConfigureAwait(false) with { Ended = ended }; }
        catch (Exception)
        {
            return Logged(All(request, "indeterminate", "The result is unknown. Checking again.", "bad_response", dispatched: true), "result unknown reason=bad_response", ErrorSites.BrokerNoAnswer) with { Ended = ended };
        }
    }

    /// <summary>Sends the request on the verified pipe and maps the broker's answer (or its absence) per step.</summary>
    private static async Task<ElevatedResult> ExchangeAsync(NamedPipeServerStream opened, string nonce, BrokerRequest wire, ElevatedRequest request, TimeSpan timeout,
        CancellationToken stop, CancellationToken ct)
    {
        await using var pipe = opened;

        using var answer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        answer.CancelAfter(timeout);
        try { await BrokerProtocol.WriteAsync(pipe, wire.ToJson(), answer.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException)
        {
            return Logged(All(request, "indeterminate", "The result is unknown. Checking again.", "broker_timeout", dispatched: true), "result unknown reason=broker_timeout", ErrorSites.BrokerNoAnswer);
        }

        // Stop after this step: one stop frame; the broker checks it between steps.
        using var onStop = stop.Register(() => _ = Task.Run(async () =>
        {
            try { await BrokerProtocol.WriteAsync(pipe, BrokerStop.ToJson(nonce), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { /* the broker is gone or done */ }
        }));

        BrokerResponse? response = null;
        try
        {
            while (true)
            {
                var frame = await BrokerProtocol.ReadAsync(pipe, answer.Token).ConfigureAwait(false);
                if (BrokerProgress.Is(frame))
                {
                    // A long tool's progress line (m9 §3.5), for one of the steps sent; anything else is ignored.
                    if (BrokerProgress.Parse(frame, nonce) is { } p && request.Steps.Any(s => s.N == p.N))
                        try { request.Progress?.Report(p); } catch (Exception) { /* progress is best effort */ }
                    continue;
                }
                response = BrokerResponse.Parse(frame);
                break;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException)
        {
            return Logged(All(request, "indeterminate", "The result is unknown. Checking again.", "broker_timeout", dispatched: true), "result unknown reason=broker_timeout", ErrorSites.BrokerNoAnswer);
        }
        if (response is null || !BrokerProtocol.NonceEquals(response.Nonce, nonce))
            return Logged(All(request, "indeterminate", "The result is unknown. Checking again.", "bad_response", dispatched: true), "result unknown reason=bad_response", ErrorSites.BrokerNoAnswer);

        // Per step; a step the broker didn't answer is unknown (it may have run).
        var steps = request.Steps.Select(s => (s.N, response.Steps.FirstOrDefault(o => o.N == s.N)?.ToOutcome()
            ?? new ActionOutcome("indeterminate", new JsonObject(), "The result is unknown.", "bad_response"))).ToList();
        AppLog.Current.Debug($"broker: result plan={request.PlanId} steps={string.Join(",", steps.Select(s => $"{s.N}:{s.Item2.State}{(s.Item2.Code is { } c ? "/" + c : "")}"))}");
        return new ElevatedResult(steps, response.ReceiptId, Dispatched: true);
    }

    /// <summary>
    /// An elevated script (m9 §5.4) as one broker session: UAC, the broker's own checks and window, then its run. The
    /// transcript frames must arrive in sequence (1, 2, …) and end with the terminal frame; anything else ends the run
    /// (stop sent) and counts as killed, so the engine records it indeterminate. The transcript is kept in a run folder of
    /// this host under the runs root (the engine writes it there), the same bounds as a user-scope run.
    /// </summary>
    public async Task<ScriptRunResult> RunScriptAsync(ScriptRunRequest request, IScriptRunObserver observer, CancellationToken stop)
    {
        if (!File.Exists(_brokerPath)) return ScriptRunResult.NotStarted("broker_missing");
        await _one.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        var life = new HelperLife();
        try
        {
            var nonce = BrokerProtocol.NewNonce();
            if (!_usedNonces.Add(nonce)) return ScriptRunResult.NotStarted("nonce_reuse");
            var (opened, refusal) = await OpenAsync(BrokerProtocol.NewPipeName(), nonce, life, CancellationToken.None).ConfigureAwait(false);
            if (refusal is not null) return ScriptRunResult.NotStarted(refusal);
            // From here the script may run: the result carries the broker process's end, whatever happens on the pipe.
            var ended = life.Ended!;
            try { return await ScriptExchangeAsync(opened!, nonce, request, observer, stop).ConfigureAwait(false) with { Ended = ended }; }
            catch (Exception) { return ScriptRunResult.KilledRun(null, TimeSpan.Zero) with { Ended = ended }; }
        }
        finally { Release(life, ScriptLifetime(request.Timeout)); }
    }

    // ---- administrator checks (m9 §17.2)

    /// <summary>A read stopped before its helper was launched: nothing launched, nothing sent.</summary>
    private const string ReadStopped = "stopped", ReadNotAdmitted = "not_admitted";

    private static ElevatedReadResult ReadAll(ElevatedReadRequest request, string state, string? code, string checkState, string checkCode, bool dispatched = false) =>
        new(state, code, [.. request.Checks.Select(c => new ElevatedCheckResult(c.N, checkState, checkCode, new ElevatedReadCoverage(false, 0, 0), []))], dispatched);

    /// <summary>The host's own shape check before anything is launched: 1–4 checks, n = 1, 2, …, known ids at the catalog's version, windows within their rules.</summary>
    internal static bool ValidRead(ElevatedReadRequest request)
    {
        if (!BrokerReadRequest.IsValidRequestId(request.RequestId) || request.Checks.Count is 0 or > AdminChecks.MaxChecksPerRequest) return false;
        for (var i = 0; i < request.Checks.Count; i++)
        {
            var c = request.Checks[i];
            if (c.N != i + 1 || AdminChecks.Find(c.Id) is not { } def || def.Version != c.HandlerVersion || !AdminChecks.ResolveWindow(def, c.WindowDays).Ok) return false;
            if (request.Checks.Take(i).Any(p => p.Id == c.Id)) return false;
        }
        return true;
    }

    /// <summary>The wait for the helper's frames once the request is sent: its window (3 minutes), 60 s per check, a minute's margin.</summary>
    public static TimeSpan ReadWait(int checks) => TimeSpan.FromMinutes(4) + AdminChecks.CheckTimeout * checks;

    /// <summary>
    /// Administrator checks as one helper session (m9 §17.2): the flag, the request's shape, UAC, the helper's own checks
    /// and window, then its frames, validated here in full (sequence without gaps, read_started at most once and before any
    /// ok result, exactly one result per check in order, every item against the check's exact field list and caps,
    /// read_end last). Anything else is malformed (every check failed, nothing kept); a connection lost or a wait run out
    /// before read_end is interrupted (finished checks kept, the rest failed/interrupted); either way a stop is sent.
    /// </summary>
    public async Task<ElevatedReadResult> RunReadAsync(ElevatedReadRequest request, CancellationToken stop)
    {
        if (!AdminChecksEnabled) return ReadAll(request, "not_started", "admin_unavailable", "failed", "admin_unavailable");
        if (!ValidRead(request)) return ReadAll(request, "not_started", "bad_request", "failed", "bad_request");
        if (!File.Exists(_brokerPath)) return ReadAll(request, "not_started", "broker_missing", "failed", "broker_missing");
        try { await _one.WaitAsync(stop).ConfigureAwait(false); }
        catch (OperationCanceledException) { return ReadAll(request, "stopped", "stopped", "cancelled", "stopped"); }
        var life = new HelperLife();
        try
        {
            var nonce = BrokerProtocol.NewNonce();
            if (!_usedNonces.Add(nonce)) return ReadAll(request, "not_started", "nonce_reuse", "failed", "nonce_reuse");
            // Right before the launcher runs, with the exclusion held (a write or script may have held it until now): the
            // engine's admission (request.Admit), then a stop that came meanwhile (a revocation). Either launches nothing.
            var (opened, refusal) = await OpenAsync(BrokerProtocol.NewPipeName(), nonce, life, CancellationToken.None, () =>
            {
                bool admitted;
                try { admitted = request.Admit?.Invoke() ?? true; }
                catch (Exception) { admitted = false; }
                return stop.IsCancellationRequested ? ReadStopped : admitted ? null : ReadNotAdmitted;
            }).ConfigureAwait(false);
            switch (refusal)
            {
                case null: break;
                case ReadStopped: return ReadAll(request, "stopped", "stopped", "cancelled", "stopped");
                case ReadNotAdmitted: return ReadAll(request, "not_started", ReadNotAdmitted, "failed", ReadNotAdmitted);
                case "uac_cancelled": return ReadAll(request, "declined", "uac_cancelled", "cancelled", "uac_cancelled");
                default: return ReadAll(request, "not_started", refusal, "failed", refusal);
            }
            var ended = life.Ended!;
            try { return await ReadExchangeAsync(opened!, nonce, request, stop).ConfigureAwait(false) with { Ended = ended }; }
            catch (Exception) { return ReadAll(request, "interrupted", "interrupted", "failed", "interrupted", dispatched: true) with { Ended = ended }; }
        }
        finally { Release(life, ReadLifetime(request.Checks.Count)); }
    }

    private async Task<ElevatedReadResult> ReadExchangeAsync(NamedPipeServerStream opened, string nonce, ElevatedReadRequest request, CancellationToken stop)
    {
        await using var pipe = opened;
        var wire = new BrokerReadRequest(nonce, request.RequestId, [.. request.Checks.Select(c => new BrokerReadCheck(c.N, c.Id, c.HandlerVersion, c.WindowDays))]);
        using var deadline = new CancellationTokenSource(_responseTimeout > TimeSpan.Zero ? _responseTimeout : ReadWait(request.Checks.Count));
        var results = new List<ElevatedCheckResult>();

        ElevatedReadResult Interrupted()
        {
            var done = results.ToList();
            done.AddRange(request.Checks.Skip(results.Count).Select(c => new ElevatedCheckResult(c.N, "failed", "interrupted", new ElevatedReadCoverage(false, 0, 0), [])));
            AppLog.Current.Warn("broker: read interrupted", ErrorSites.BrokerNoAnswer);
            return new ElevatedReadResult("interrupted", "interrupted", done, Dispatched: true);
        }

        async Task SendStop()
        {
            try { await BrokerProtocol.WriteAsync(pipe, BrokerStop.ToJson(nonce), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { /* the helper is gone or done */ }
        }

        try { await BrokerProtocol.WriteAsync(pipe, wire.ToJson(), deadline.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException) { return Interrupted(); }
        using var onStop = stop.Register(() => _ = Task.Run(SendStop));

        var expected = 1;
        var started = false;
        try
        {
            while (true)
            {
                var f = BrokerReadFrames.Parse(await BrokerProtocol.ReadAsync(pipe, deadline.Token).ConfigureAwait(false), nonce);
                if (f is null || f.Seq != expected++ || (f.RequestId is not null && f.RequestId != request.RequestId)) return await Malformed().ConfigureAwait(false);
                switch (f.Kind)
                {
                    case "started":
                        if (started || results.Count > 0) return await Malformed().ConfigureAwait(false);
                        started = true;
                        try { request.Started?.Invoke(); } catch (Exception) { /* best effort */ }
                        break;
                    case "result":
                        if (results.Count >= request.Checks.Count || f.N != request.Checks[results.Count].N || f.RequestId is null) return await Malformed().ConfigureAwait(false);
                        var def = AdminChecks.Find(request.Checks[results.Count].Id)!;
                        if (f.State == "ok" && (!started || f.Items.Count > def.MaxItems || f.Items.Any(i => !AdminChecks.Conforms(def, i)))) return await Malformed().ConfigureAwait(false);
                        results.Add(new ElevatedCheckResult(f.N, f.State!, f.Code, new ElevatedReadCoverage(f.Coverage!.Complete, f.Coverage.Total, f.Coverage.Returned), f.Items));
                        break;
                    default: // end, always last
                        // The exact request id, except for the one case where the helper couldn't read it: a refusal of an
                        // unparsed request, with no read_started and no result before it (review item 7).
                        var refusedUnparsed = f.State == "refused" && results.Count == 0 && !started;
                        if (f.RequestId is null && !refusedUnparsed) return await Malformed().ConfigureAwait(false);
                        if (results.Count != request.Checks.Count && !refusedUnparsed) return await Malformed().ConfigureAwait(false);
                        if (refusedUnparsed && results.Count == 0)
                            return ReadAll(request, "refused", f.Code, "failed", f.Code ?? "refused", dispatched: true);
                        AppLog.Current.Debug($"broker: read end={f.State} checks={string.Join(",", results.Select(r => $"{r.N}:{r.State}{(r.Code is { } c ? "/" + c : "")}"))}");
                        return new ElevatedReadResult(f.State!, f.Code, results, Dispatched: true);
                }
            }
        }
        // Malformed input (oversized frame, not JSON, too deep) keeps nothing (review item 5); only a real transport loss —
        // the stream ended mid-frame, an I/O error, or the wait ran out — is interrupted and keeps the finished checks.
        catch (InvalidDataException ex) when (!BrokerProtocol.StreamEnded(ex))
        {
            return await Malformed().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException)
        {
            await SendStop().ConfigureAwait(false);
            return Interrupted();
        }

        async Task<ElevatedReadResult> Malformed()
        {
            await SendStop().ConfigureAwait(false);
            AppLog.Current.Warn("broker: read malformed", ErrorSites.BrokerNoAnswer);
            return ReadAll(request, "malformed", "malformed", "failed", "malformed", dispatched: true);
        }
    }

    /// <summary>Sends the script request on the verified pipe and follows its frames to the end (or the pipe's loss).</summary>
    private static async Task<ScriptRunResult> ScriptExchangeAsync(NamedPipeServerStream opened, string nonce, ScriptRunRequest request, IScriptRunObserver observer,
        CancellationToken stop)
    {
        await using var pipe = opened;
        var wire = new BrokerScriptRequest(nonce, request.PlanId ?? "plan-script", request.OperationId, request.Sha256, request.Bytes, request.BypassPolicy);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3) + request.Timeout + TimeSpan.FromMinutes(2));
        string? folder = null;
        var started = DateTimeOffset.UtcNow;
        try { await BrokerProtocol.WriteAsync(pipe, wire.ToJson(), deadline.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException) { return ScriptRunResult.KilledRun(null, TimeSpan.Zero); }

        async Task SendStop()
        {
            try { await BrokerProtocol.WriteAsync(pipe, BrokerStop.ToJson(nonce), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { /* the broker is gone or done */ }
        }
        using var onStop = stop.Register(() => _ = Task.Run(SendStop));

        var expected = 1;
        try
        {
            while (true)
            {
                var f = BrokerScriptFrames.Parse(await BrokerProtocol.ReadAsync(pipe, deadline.Token).ConfigureAwait(false), nonce);
                if (f is null || f.Seq != expected++ || (f.Kind == "transcript" && folder is null) || (f.Kind == "started" && folder is not null))
                {
                    await SendStop().ConfigureAwait(false);
                    return ScriptRunResult.KilledRun(folder, DateTimeOffset.UtcNow - started);
                }
                switch (f.Kind)
                {
                    case "started":
                        folder = Path.Combine(request.RunsRoot, Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)));
                        Directory.CreateDirectory(request.RunsRoot);
                        Directory.CreateDirectory(folder);
                        OptimizationTool.Engine.Acl.RestrictDirectory(folder);
                        started = DateTimeOffset.UtcNow;
                        observer.Started(started, folder);
                        break;
                    case "transcript":
                        foreach (var (err, text) in f.Lines) observer.Line(err, text);
                        break;
                    default:
                        var took = DateTimeOffset.UtcNow - started;
                        return f.State switch
                        {
                            "exited" => ScriptRunResult.Exit(f.ExitCode ?? -1, folder, took),
                            "not_started" when folder is null => ScriptRunResult.NotStarted(f.Code ?? "not_started"),
                            _ => ScriptRunResult.KilledRun(folder, took),
                        };
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException)
        {
            await SendStop().ConfigureAwait(false);
            return ScriptRunResult.KilledRun(folder, DateTimeOffset.UtcNow - started); // the broker or the pipe died mid-run
        }
    }

    public static PipeSecurity Security(SecurityIdentifier user)
    {
        var s = new PipeSecurity();
        s.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return s;
    }
}

/// <summary>The elevated script runner seam for the engine (m9 §5.4): every run is one broker session.</summary>
public sealed class BrokerScriptRunner(BrokerElevation elevation) : IScriptRunner
{
    public Task<ScriptRunResult> RunAsync(ScriptRunRequest request, IScriptRunObserver observer, CancellationToken stop) =>
        elevation.RunScriptAsync(request, observer, stop);
}

/// <summary>ShellExecuteEx "runas": the UAC prompt. ERROR_CANCELLED (1223) means the user said no.</summary>
public sealed class ShellExecuteLauncher : IBrokerLauncher
{
    private const int ERROR_CANCELLED = 1223;

    public BrokerLaunch Launch(string brokerPath, string arguments)
    {
        var info = new SHELLEXECUTEINFOW
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFOW>(),
            fMask = 0x00000040 | 0x00000100 | 0x00000400, // NOCLOSEPROCESS | NOASYNC | FLAG_NO_UI
            lpVerb = "runas",
            lpFile = brokerPath,
            lpParameters = arguments,
            lpDirectory = Path.GetDirectoryName(brokerPath),
            nShow = 0, // SW_HIDE
        };
        if (!ShellExecuteExW(ref info))
        {
            var error = Marshal.GetLastWin32Error();
            return error == ERROR_CANCELLED ? new BrokerLaunch(false, true, 0) : new BrokerLaunch(false, false, 0, error.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        // m9 §13 R2: the process handle is kept (SEE_MASK_NOCLOSEPROCESS) and owned by the returned waiter, which closes it
        // when disposed; the host waits on it so locks follow the broker process, not the pipe. No handle: Process is null.
        if (info.hProcess == IntPtr.Zero) return new BrokerLaunch(true, false, 0);
        return Launched(new SafeProcessHandle(info.hProcess, ownsHandle: true), h => new BrokerProcessHandle(h), GetProcessId);
    }

    /// <summary>
    /// After Windows reported the launch: the result is ALWAYS Started (re-check B). When the process waiter can't be built
    /// (registering the wait fails, the id can't be read), the handle is closed and the launch is reported without a
    /// process, so the host treats the helper as unobservable: the exclusion is held until its lifetime bound and the
    /// plan's locks until restart, never released at once.
    /// </summary>
    internal static BrokerLaunch Launched(SafeProcessHandle handle, Func<SafeProcessHandle, IBrokerProcess> waiter, Func<SafeProcessHandle, uint> processId)
    {
        uint pid = 0;
        try
        {
            pid = processId(handle);
            return new BrokerLaunch(true, false, pid, Process: waiter(handle));
        }
        catch (Exception)
        {
            handle.Dispose();
            AppLog.Current.Warn("broker: launched, but its process can't be watched; held until its lifetime bound", ErrorSites.BrokerExitError);
            return new BrokerLaunch(true, false, pid);
        }
    }

    /// <summary>
    /// The launched broker's process handle as a waiter: a thread-pool wait on the handle (signalled when the process exits),
    /// unregistered and the handle closed on dispose. Written and reviewed, never run on the development PC.
    /// </summary>
    private sealed class BrokerProcessHandle : IBrokerProcess
    {
        private readonly SafeProcessHandle _handle;
        private readonly ProcessWaitHandle _wait;
        private readonly RegisteredWaitHandle _registration;
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        public BrokerProcessHandle(SafeProcessHandle handle)
        {
            _handle = handle;
            _wait = new ProcessWaitHandle(handle);
            _registration = ThreadPool.RegisterWaitForSingleObject(_wait, static (state, _) => ((TaskCompletionSource)state!).TrySetResult(), _exited,
                Timeout.Infinite, executeOnlyOnce: true);
        }

        public Task WaitForExitAsync() => _exited.Task;

        public bool HasExited => _exited.Task.IsCompleted || (Volatile.Read(ref _disposed) == 0 && _wait.WaitOne(0));

        public int? ExitCode => Volatile.Read(ref _disposed) == 0 && _exited.Task.IsCompleted && GetExitCodeProcess(_handle, out var code) ? (int)code : null;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _registration.Unregister(null);
            _wait.Dispose();   // does not own the handle
            _handle.Dispose(); // closes the process handle
        }

        /// <summary>A wait handle over the process handle (signalled at exit) that doesn't own it.</summary>
        private sealed class ProcessWaitHandle : WaitHandle
        {
            public ProcessWaitHandle(SafeProcessHandle process) => SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFOW
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIconOrMonitor;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteExW(ref SHELLEXECUTEINFOW info);

    [DllImport("kernel32.dll")]
    private static extern uint GetProcessId(SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
}

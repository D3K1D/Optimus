using System.Security.Principal;
using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Broker;

namespace OptimizationTool.Broker;

/// <summary>Who is on one end of the broker pipe.</summary>
public sealed record PeerInfo(uint ProcessId, string? ImagePath, uint SessionId, SecurityIdentifier? User);

/// <summary>
/// The protected journal (m9 §3.5; files in <see cref="BrokerJournalFiles"/>): the session record, a prepared record with
/// the preimage BEFORE each write, and each step's outcome. A write whose prepared record could not be stored never happens.
/// </summary>
public interface IBrokerJournal
{
    /// <summary>Writes the session record and returns its receipt ID.</summary>
    string Begin(BrokerRequest request, DateTimeOffset at);
    void Prepared(string receiptId, BrokerStepRequest step, ActionResolution resolution, DateTimeOffset at);
    void Outcome(string receiptId, int n, ActionOutcome outcome, DateTimeOffset at);
    /// <summary>The broker's own record of step <paramref name="n"/> of an earlier session, or null.</summary>
    BrokerRecordStep? Find(string receiptId, int n);
}

/// <summary>What one atomic consume of a nonce found.</summary>
public enum NonceConsume
{
    /// <summary>Never used before; now recorded as used. The request may go on.</summary>
    Consumed,
    /// <summary>Already used: the request is refused.</summary>
    Replayed,
    /// <summary>The ledger could not be locked, trusted, read or written: the request is refused (fail closed).</summary>
    Failed,
}

/// <summary>Single-use nonces across broker launches (and across brokers running at the same time).</summary>
public interface INonceLedger
{
    /// <summary>Checks and records the nonce in one step; a request runs only on <see cref="NonceConsume.Consumed"/>.</summary>
    NonceConsume TryConsume(string nonce);
}

/// <summary>The broker's own confirmation window (m9 §3.5): true = Apply, false = Cancel. Fakes in tests.</summary>
public interface IBrokerConfirm
{
    Task<bool> AskAsync(BrokerConfirmView view, CancellationToken ct);
}

public sealed record BrokerSessionOptions(
    string Nonce,
    string ExpectedHostImage,
    PeerInfo Self,
    IReadOnlyList<IBrokerHandler> Handlers,
    IBrokerJournal Journal,
    INonceLedger Ledger,
    IBrokerConfirm Confirm,
    TimeProvider? Time = null)
{
    /// <summary>The per-step write deadline of a setting step (m9 §3.5: 45 s); a long step uses its handler's deadline plus a minute.</summary>
    public TimeSpan StepDeadline { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Called once the steps are known, with how long the rest of the session may need (the process's budget grows to fit a long tool).</summary>
    public Action<TimeSpan>? ExtendBudget { get; init; }

    /// <summary>Elevated scripts (m9 §5.4): the broker's own guard, policy, run folder, window and runner. Null refuses script requests.</summary>
    public BrokerScriptOptions? Scripts { get; init; }

    /// <summary>Administrator checks (m9 §17.2): the helper's read adapters, window and journal. Null refuses read requests.</summary>
    public BrokerReadOptions? Reads { get; init; }
}

/// <summary>Result for the caller and tests: the refusal/outcome code, the response sent (if any), and whether a write happened.</summary>
public sealed record BrokerResult(string Code, BrokerResponse? Sent, bool Wrote);

/// <summary>
/// One broker run (m4 §5, m9 §3.5). Verify the pipe server is the installed host in this session and account; read one
/// bounded v2 request; check the nonce (the launch's, never used before); re-resolve EVERY step from live state with the
/// broker's own compiled handler (same version, same content hash, its own protected lists, undo only against its own
/// record); show the broker's own window built only from those resolutions; then per step in order: prepared record
/// (preimage) → write → read back → outcome record, stopping at the first step that is not verified, at a stop frame or
/// when the pipe is gone (the step in flight finishes and is journalled). Answer once.
/// </summary>
public sealed class BrokerSession(BrokerSessionOptions o)
{
    private readonly TimeProvider _time = o.Time ?? TimeProvider.System;
    private readonly Dictionary<string, IBrokerHandler> _handlers = o.Handlers.ToDictionary(h => h.ActionId, StringComparer.Ordinal);
    private volatile bool _stop;
    private volatile bool _lost;

    private sealed record Resolved(BrokerStepRequest Step, IBrokerHandler Handler, ActionResolution Resolution);

    public async Task<BrokerResult> RunAsync(Stream pipe, PeerInfo server, CancellationToken ct)
    {
        // 1. Server identity. An unverified server gets no answer at all.
        if (server.ImagePath is null || !SamePath(server.ImagePath, o.ExpectedHostImage))
            return new BrokerResult("server_mismatch", null, false);
        if (server.SessionId != o.Self.SessionId)
            return new BrokerResult("session_mismatch", null, false);
        if (server.User is null || o.Self.User is null || !server.User.Equals(o.Self.User))
        {
            // m9 §17.2: refused for reads exactly as for writes; the decision is made here, before anything is parsed or
            // consumed. Only the answer's shape follows the connection's type, so the host's read path gets its own frames.
            JsonNode? first = null;
            try
            {
                using var peek = CancellationTokenSource.CreateLinkedTokenSource(ct);
                peek.CancelAfter(TimeSpan.FromSeconds(10));
                first = await BrokerProtocol.ReadAsync(pipe, peek.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidDataException or OperationCanceledException or IOException) { first = null; }
            if (BrokerReadRequest.IsRead(first)) return await BrokerReadSession.RefuseAsync(pipe, o.Nonce, first, "different_admin").ConfigureAwait(false);
            return await Refuse(pipe, null, "different_admin", "Windows permission came from a different account. Use Windows Security instead.", ct).ConfigureAwait(false);
        }

        // 2. One bounded, well-formed request whose type is fixed by this first frame: a v2 change request, an elevated
        // script (m9 §5.4) or administrator checks (m9 §17.2, a read connection that accepts nothing else afterwards).
        BrokerRequest? request;
        JsonNode? frame = null;
        try { frame = await BrokerProtocol.ReadAsync(pipe, ct).ConfigureAwait(false); }
        catch (InvalidDataException) { frame = null; }
        if (BrokerReadRequest.IsRead(frame))
            return o.Reads is { } reads
                ? await new BrokerReadSession(o, reads, _time).RunAsync(pipe, frame!, ct).ConfigureAwait(false)
                : await BrokerReadSession.RefuseAsync(pipe, o.Nonce, frame, "check_not_allowed").ConfigureAwait(false);
        if (BrokerScriptRequest.IsScript(frame))
            return o.Scripts is { } scripts
                ? await new BrokerScriptSession(o, scripts, _time).RunAsync(pipe, frame!, ct).ConfigureAwait(false)
                : await BrokerScriptSession.RefuseAsync(pipe, o.Nonce, "action_not_allowed").ConfigureAwait(false);
        request = BrokerRequest.Parse(frame);
        if (request is null) return await Refuse(pipe, null, "bad_request", "The request was not valid.", ct).ConfigureAwait(false);

        // 3. Nonce: the one this broker was launched with, never seen before. Consumed now, whatever happens next.
        if (!BrokerProtocol.IsValidNonce(request.Nonce) || !BrokerProtocol.NonceEquals(request.Nonce, o.Nonce))
            return await Refuse(pipe, request, "bad_nonce", "The request did not match this approval.", ct).ConfigureAwait(false);
        switch (o.Ledger.TryConsume(o.Nonce))
        {
            case NonceConsume.Consumed: break;
            case NonceConsume.Replayed: return await Refuse(pipe, request, "replayed", "This approval was already used.", ct).ConfigureAwait(false);
            default: return await Refuse(pipe, request, "journal_failed", "The administrator journal couldn't be written. Nothing changed.", ct).ConfigureAwait(false);
        }

        // 4. Every step re-resolved by the broker itself before anything is shown or written.
        var resolved = new List<Resolved>();
        foreach (var step in request.Steps)
        {
            var (r, refusal, message) = Check(step);
            if (r is null) return await Refuse(pipe, request, refusal!, message!, ct).ConfigureAwait(false);
            resolved.Add(r);
        }

        // 5. The broker's own window: only its own resolutions; a request that doesn't fit it is refused.
        var view = BrokerConfirmView.Build([.. resolved.Select(r => (r.Step.N, r.Handler, r.Resolution))]);
        if (!view.Fits) return await Refuse(pipe, request, "too_large", "This change has more to show than the confirmation window can.", ct).ConfigureAwait(false);
        bool apply;
        try { apply = await o.Confirm.AskAsync(view, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { apply = false; }
        if (!apply)
        {
            var declined = request.Steps.Select(s => new BrokerStepOutcome(s.N, "declined", [], "You cancelled in the administrator window. Nothing changed.",
                "broker_cancelled", "Cancelled in the administrator window.")).ToList();
            return await Answer(pipe, request, declined, null, "broker_cancelled", wrote: false, ct).ConfigureAwait(false);
        }

        // 6. The session record, then the steps in order, watching for a stop frame or a lost pipe between steps.
        string receiptId;
        try { receiptId = o.Journal.Begin(request, _time.GetUtcNow()); }
        catch (Exception) { return await Refuse(pipe, request, "journal_failed", "The administrator journal couldn't be written. Nothing changed.", ct).ConfigureAwait(false); }
        o.ExtendBudget?.Invoke(resolved.Aggregate(TimeSpan.FromMinutes(2), (sum, r) => sum + WatchdogOf(r.Handler)));
        using var watching = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watcher = Task.Run(() => WatchAsync(pipe, request.Nonce, watching.Token), CancellationToken.None);

        var outcomes = new List<BrokerStepOutcome>();
        var wrote = false;
        var halted = false;
        var hung = false;
        foreach (var r in resolved)
        {
            var n = r.Step.N;
            if (halted || _stop || _lost)
            {
                outcomes.Add(Skipped(n));
                continue;
            }
            // Re-read right before the write: the window may have been open a while.
            var live = r.Handler.Resolve(r.Step);
            if (!live.Eligible || ContentHash(r.Handler, live, r.Step) != r.Step.ContentHash || !JsonNode.DeepEquals(live.After, r.Resolution.After))
            {
                outcomes.Add(new BrokerStepOutcome(n, "failed", (JsonObject)live.Before.DeepClone(), "This changed since you approved. Nothing was written.", "plan_mismatch",
                    "The setting changed since you approved. Preview it again."));
                halted = true;
                continue;
            }
            try { o.Journal.Prepared(receiptId, r.Step, live, _time.GetUtcNow()); }
            catch (Exception)
            {
                outcomes.Add(new BrokerStepOutcome(n, "failed", [], "The administrator journal couldn't be written. Nothing was written.", "journal_failed"));
                halted = true;
                continue;
            }
            // The write runs under a watchdog (m9 §3.5: 45 s per setting step; a long tool its own deadline plus a minute). A
            // write that doesn't come back is journalled indeterminate, nothing further starts, and the broker answers and exits.
            var run = new BrokerRun(request.PlanId, new StepProgress(this, pipe, request.Nonce, n), _stopSignal.Token);
            var write = Task.Run(() => r.Handler.Run(live, r.Step, run), CancellationToken.None);
            ActionOutcome outcome;
            try
            {
                outcome = await write.WaitAsync(WatchdogOf(r.Handler), _time, CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                outcome = new ActionOutcome("indeterminate", [], "The change didn't finish in time. Check it again.", "write_timeout", "The write did not finish in time.");
                hung = true;
            }
            catch (Exception ex) { outcome = new ActionOutcome("indeterminate", [], "The result could not be confirmed.", "write_error", ex.GetType().Name); }
            wrote = true;
            try { o.Journal.Outcome(receiptId, n, outcome, _time.GetUtcNow()); }
            catch (Exception) { /* the prepared record stands; the host reads live state */ }
            outcomes.Add(new BrokerStepOutcome(n, outcome.State, outcome.Observed, outcome.ReceiptText, outcome.Code, outcome.Message));
            if (outcome.State is not ("verified" or "completed")) halted = true;
        }
        await watching.CancelAsync().ConfigureAwait(false);
        try { await watcher.ConfigureAwait(false); } catch (OperationCanceledException) { }

        var code = hung ? "write_timeout" : _lost ? "pipe_lost" : _stop && outcomes.Any(x => x.State == "skipped") ? "stopped"
            : outcomes.FirstOrDefault(x => x.State is not ("verified" or "completed"))?.State ?? "verified";
        return await Answer(pipe, request, outcomes, receiptId, code, wrote, ct).ConfigureAwait(false);
    }

    /// <summary>How long the session waits for one step's write: 45 s for a setting step; a long tool's deadline plus a minute.</summary>
    private TimeSpan WatchdogOf(IBrokerHandler h) => h.Deadline is { } d ? d + TimeSpan.FromMinutes(1) : o.StepDeadline;

    private readonly CancellationTokenSource _stopSignal = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// A long step's progress lines become {type:"progress", nonce, n, text} frames: at most one per second, never a repeat,
    /// written under the same lock as the answer so frames never interleave. Nothing is sent once the host is gone.
    /// </summary>
    private sealed class StepProgress(BrokerSession owner, Stream pipe, string nonce, int n) : IProgress<string>
    {
        private string? _last;
        private DateTimeOffset _at = DateTimeOffset.MinValue;

        public void Report(string value)
        {
            var now = owner._time.GetUtcNow();
            if (owner._lost || value == _last || now - _at < TimeSpan.FromSeconds(1)) return;
            _last = value;
            _at = now;
            owner._writeLock.Wait();
            try { BrokerProtocol.WriteAsync(pipe, BrokerProgress.ToJson(nonce, n, value), CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException) { owner._lost = true; }
            finally { owner._writeLock.Release(); }
        }
    }

    private static BrokerStepOutcome Skipped(int n) => new(n, "skipped", [], "Not started.", null, null);

    /// <summary>One step's checks: a compiled handler of the same version, undo only against the broker's own record, an eligible live resolution, the protected lists, the same content hash.</summary>
    private (Resolved? R, string? Code, string? Message) Check(BrokerStepRequest step)
    {
        if (!_handlers.TryGetValue(step.ActionId, out var h)) return (null, "action_not_allowed", "This change can't run with administrator rights.");
        if (h.Version != step.HandlerVersion) return (null, "version_mismatch", "This app and its administrator helper don't match. Reinstall Optimus.");
        // The request must be internally consistent: its own fields hash to the content hash it carries.
        if (BrokerContent.Hash(step.ActionId, step.HandlerVersion, step.Target, step.Before, step.After, step.Parameters) != step.ContentHash)
            return (null, "plan_mismatch", "The plan didn't match what was approved.");

        BrokerRecordStep? original = null;
        var undo = h.IsUndo(step);
        if (undo != step.UndoOf is not null) return (null, undo ? "no_record" : "bad_request", undo ? "There's no record of Optimus making this change." : "This change isn't supported.");
        if (step.UndoOf is { } u)
        {
            try { original = o.Journal.Find(u.ReceiptId, u.N); }
            catch (Exception) { original = null; }
            if (original is null || original.State is not ("verified" or "completed") || original.ActionId != step.ActionId
                || !Equals(original.Target["id"]?.ToJsonString(), step.Target["id"]?.ToJsonString()))
                return (null, "no_record", "There's no record of Optimus making this change.");
        }

        ActionResolution r;
        try { r = h.Resolve(step); }
        catch (Exception) { return (null, "unreadable", "Windows didn't report the current state."); }
        if (!r.Eligible) return (null, r.IneligibleCode ?? "ineligible", r.IneligibleMessage ?? "This change isn't possible right now.");
        if (h.Protect(r) is { } p) return (null, p.Code, p.Reason);
        if (ContentHash(h, r, step) != step.ContentHash) return (null, "plan_mismatch", "The setting changed since you approved. Preview it again.");
        // An undo restores exactly the broker's recorded preimage, from exactly what it recorded writing.
        if (original is not null && (!JsonNode.DeepEquals(r.After, original.Before) || !JsonNode.DeepEquals(r.Before, original.Observed ?? original.After)))
            return (null, "no_record", "This doesn't match Optimus's record of the change.");
        return (new Resolved(step, h, r), null, null);
    }

    private static string ContentHash(IBrokerHandler h, ActionResolution r, BrokerStepRequest step) =>
        BrokerContent.Hash(h.ActionId, h.Version, r.Target, r.Before, r.After, step.Parameters);

    /// <summary>Reads host frames while the steps run: a stop frame stops after the step in flight; EOF or an error means the host is gone.</summary>
    private async Task WatchAsync(Stream pipe, string nonce, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await BrokerProtocol.ReadAsync(pipe, ct).ConfigureAwait(false);
                if (BrokerStop.Is(frame, nonce))
                {
                    _stop = true;
                    await _stopSignal.CancelAsync().ConfigureAwait(false); // a long tool in flight is ended
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { _lost = true; }
    }

    /// <summary>A refusal before anything was written: every step failed with the same code.</summary>
    private async Task<BrokerResult> Refuse(Stream pipe, BrokerRequest? request, string code, string message, CancellationToken ct)
    {
        var steps = (request?.Steps ?? []).Select(s => new BrokerStepOutcome(s.N, "failed", [], "Nothing changed.", code, message)).ToList();
        return await Answer(pipe, request, steps, null, code, wrote: false, ct).ConfigureAwait(false);
    }

    private async Task<BrokerResult> Answer(Stream pipe, BrokerRequest? request, IReadOnlyList<BrokerStepOutcome> steps, string? receiptId, string code, bool wrote, CancellationToken ct)
    {
        var response = new BrokerResponse(o.Nonce, steps, receiptId);
        if (_lost) return new BrokerResult(code, null, wrote); // nobody to answer; the journal holds the result
        await _writeLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try { await BrokerProtocol.WriteAsync(pipe, response.ToJson(), ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { return new BrokerResult(code, null, wrote); }
        finally { _writeLock.Release(); }
        _ = request;
        return new BrokerResult(code, response, wrote);
    }

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}

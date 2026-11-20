using System.Text;
using System.Text.Json.Nodes;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Windows.Actions.Broker;
using OptimizationTool.Windows.Actions.Broker.Reads;

namespace OptimizationTool.Broker;

/// <summary>
/// What the helper's own read window shows (m9 §17.2; Design System v77, "Windows dialogs", "Administrator checks"): the
/// heading, the line under it, per check the catalog's label and its one line on what it reads, the closing line, Cancel /
/// Read. Every word is compiled into the helper; nothing comes from the host (the AI is never named).
/// </summary>
public sealed record BrokerReadView(IReadOnlyList<(string Label, string Reads)> Checks)
{
    public const string TitleBar = "Optimus · Administrator checks";
    public const string Heading = "Read these as administrator?";
    public const string HeadingOne = "Read this as administrator?";
    public const string Explanation = "You allowed this in Optimus. Windows asked for permission because these need administrator rights.";
    public const string Closing = "Nothing on your PC is changed. The results go to Optimus and may be shown to the AI you use.";
    public const string CancelText = BrokerConfirmView.CancelText;
    public const string ReadText = "Read";

    /// <summary>From the helper's catalog and each check's RESOLVED window (design v79: the Defender line names its days).</summary>
    public static BrokerReadView Build(IEnumerable<(AdminCheckDef Def, int? Days)> checks) => new([.. checks.Select(c => (c.Def.Label, AdminChecks.WindowLine(c.Def, c.Days)))]);

    /// <summary>Each check at its default window.</summary>
    public static BrokerReadView Build(IEnumerable<AdminCheckDef> defs) => Build(defs.Select(d => (d, AdminChecks.ResolveWindow(d, null).Days)));

    /// <summary>The lines in order: heading (one or several checks), the explanation, per check its label then its line, the closing line.</summary>
    public IReadOnlyList<BrokerLine> Lines()
    {
        var lines = new List<BrokerLine> { new(BrokerLineKind.Heading, Checks.Count == 1 ? HeadingOne : Heading), new(BrokerLineKind.Explanation, Explanation) };
        foreach (var (label, reads) in Checks)
        {
            lines.Add(new(BrokerLineKind.StepName, label));
            lines.Add(new(BrokerLineKind.Row, reads));
        }
        lines.Add(new(BrokerLineKind.Notice, Closing));
        return lines;
    }
}

/// <summary>The helper's read window: true = Read, false = Cancel. Fakes in tests; the real one is never shown by tests.</summary>
public interface IBrokerReadConfirm
{
    Task<bool> AskAsync(BrokerReadView view, CancellationToken ct);
}

/// <summary>The helper's record of read sessions in its protected journal: <c>rcpt-*.read.json</c> before reading, <c>rcpt-*.read-end.json</c> after.</summary>
public interface IBrokerReadJournal
{
    /// <summary>Written before the first check starts (which checks, when); returns the record's ID. A failure reads nothing.</summary>
    string BeginRead(BrokerReadRequest request, DateTimeOffset at);
    /// <summary>The outcome: the session's end state and each check's state and code.</summary>
    void EndRead(string receiptId, string state, IReadOnlyList<(int N, string State, string? Code)> results, DateTimeOffset at);
}

/// <summary>The helper's read wiring: its compiled adapters, its window and journal, and the per-check watchdog.</summary>
public sealed record BrokerReadOptions(IReadOnlyList<IAdminReadAdapter> Adapters, IBrokerReadConfirm Confirm, IBrokerReadJournal Journal)
{
    /// <summary>The watchdog per check (m9 §17.2: 60 s).</summary>
    public TimeSpan CheckTimeout { get; init; } = AdminChecks.CheckTimeout;

    /// <summary>Each frame's write is bounded by this (10 s); past it the pipe is abandoned and the session still ends.</summary>
    public TimeSpan WriteTimeout { get; init; } = BrokerReadSession.DefaultWriteTimeout;
}

/// <summary>
/// One read session in the helper (m9 §17.2). The type of the connection is fixed by its first frame (a read): from then
/// on only a <c>stop</c> frame is accepted; any other frame (a write request, a script, anything) is refused: no further
/// check starts, the rest are cancelled (bad_frame) and read_end is refused. Order: the request strictly parsed; the nonce
/// (the launch's) consumed through the ledger, so a replay reads nothing; every check looked up in the helper's OWN
/// catalog and adapters (same version, window within its rule; anything else refuses the whole request before the
/// window); the helper's own window from its compiled text only; a journal line before reading; then each check in order
/// under a watchdog. A check that does not return in time is failed/timeout, no further check starts (the rest skipped),
/// read_end is sent and the helper exits (Program returns; the stuck read is on a background thread, which ends with the
/// process). Each adapter's output is SHAPED to its field list here, before anything is sent: unknown or extra fields are
/// dropped, an item with a bad listed field is rejected whole, items over the check's cap or the 200/64 KB frame caps are
/// cut whole; every cut or rejection makes coverage incomplete. Only what was on the window is read.
/// </summary>
internal sealed class BrokerReadSession(BrokerSessionOptions o, BrokerReadOptions r, TimeProvider time)
{
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly CancellationTokenSource _stopSignal = new();
    private int _seq;
    private volatile bool _lost;
    private volatile bool _stop;
    private volatile bool _badFrame;

    private string Nonce => o.Nonce;

    public async Task<BrokerResult> RunAsync(Stream pipe, JsonNode frame, CancellationToken ct)
    {
        var req = BrokerReadRequest.Parse(frame);
        if (req is null) return await RefuseCoreAsync(pipe, BrokerReadRequest.RequestIdOf(frame), [], "bad_request").ConfigureAwait(false);
        if (!BrokerProtocol.IsValidNonce(req.Nonce) || !BrokerProtocol.NonceEquals(req.Nonce, Nonce)) return await Refuse(pipe, req, "bad_nonce").ConfigureAwait(false);
        switch (o.Ledger.TryConsume(Nonce))
        {
            case NonceConsume.Consumed: break;
            case NonceConsume.Replayed: return await Refuse(pipe, req, "replayed").ConfigureAwait(false);
            default: return await Refuse(pipe, req, "journal_failed").ConfigureAwait(false);
        }

        // Every check from the helper's own catalog and adapters, or the whole request is refused.
        var plan = new List<(BrokerReadCheck Check, AdminCheckDef Def, IAdminReadAdapter Adapter, int? Days)>();
        foreach (var c in req.Checks)
        {
            var def = AdminChecks.Find(c.Id);
            var adapter = r.Adapters.FirstOrDefault(a => a.Id == c.Id);
            if (def is null || adapter is null) return await Refuse(pipe, req, "check_not_allowed").ConfigureAwait(false);
            if (adapter.Version != def.Version || c.HandlerVersion != def.Version) return await Refuse(pipe, req, "version_mismatch").ConfigureAwait(false);
            if (plan.Any(p => p.Def.Id == def.Id)) return await Refuse(pipe, req, "bad_request").ConfigureAwait(false);
            var (ok, days) = AdminChecks.ResolveWindow(def, c.Window);
            if (!ok) return await Refuse(pipe, req, "bad_request").ConfigureAwait(false);
            plan.Add((c, def, adapter, days));
        }

        // From here the host may only stop us: watch the pipe while the window is open too.
        using var watching = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watcher = Task.Run(() => WatchAsync(pipe, watching.Token), CancellationToken.None);
        try
        {
            bool read;
            using (var window = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopSignal.Token))
            {
                try { read = await r.Confirm.AskAsync(BrokerReadView.Build(plan.Select(p => (p.Def, p.Days))), window.Token).ConfigureAwait(false); }
                catch (Exception) { read = false; }
            }
            if (_badFrame) return await EndAll(pipe, req, "cancelled", "bad_frame", "refused", "bad_frame").ConfigureAwait(false);
            if (_stop) return await EndAll(pipe, req, "cancelled", "stopped", "stopped", "stopped").ConfigureAwait(false);
            if (!read) return await EndAll(pipe, req, "cancelled", "broker_cancelled", "declined", "broker_cancelled").ConfigureAwait(false);

            string receiptId;
            try { receiptId = r.Journal.BeginRead(req, time.GetUtcNow()); }
            catch (Exception) { return await Refuse(pipe, req, "journal_failed").ConfigureAwait(false); }
            o.ExtendBudget?.Invoke(r.CheckTimeout * plan.Count + TimeSpan.FromMinutes(1));
            await Send(pipe, seq => BrokerReadFrames.Started(Nonce, req.RequestId, seq)).ConfigureAwait(false);

            var results = new List<(int N, string State, string? Code)>();
            var hung = false;
            foreach (var (check, def, adapter, days) in plan)
            {
                if (hung) { await Result(pipe, req, results, check.N, "skipped", "timeout").ConfigureAwait(false); continue; }
                if (_badFrame) { await Result(pipe, req, results, check.N, "cancelled", "bad_frame").ConfigureAwait(false); continue; }
                if (_stop || _lost) { await Result(pipe, req, results, check.N, "cancelled", _lost ? "pipe_lost" : "stopped").ConfigureAwait(false); continue; }

                AdminReadOutput output;
                var work = Task.Run(() => adapter.Read(new AdminReadInput(days, time.GetUtcNow()), CancellationToken.None), CancellationToken.None);
                try { output = await work.WaitAsync(r.CheckTimeout, time, CancellationToken.None).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    hung = true;
                    await Result(pipe, req, results, check.N, "failed", "timeout").ConfigureAwait(false);
                    continue;
                }
                catch (Exception) { output = AdminReadOutput.Failed("read_error"); }
                var (state, code, coverage, items) = Pack(def, output, Nonce, req.RequestId, _seq + 1, check.N);
                results.Add((check.N, state, code));
                await Send(pipe, seq => BrokerReadFrames.Result(Nonce, req.RequestId, seq, check.N, state, code, coverage, items)).ConfigureAwait(false);
            }

            var end = hung ? "timeout" : _badFrame ? "refused" : (_stop || _lost) && results.Any(x => x.State == "cancelled") ? "stopped" : "complete";
            var endCode = hung ? "timeout" : _badFrame ? "bad_frame" : _lost && end == "stopped" ? "pipe_lost" : null;
            try { r.Journal.EndRead(receiptId, end, results, time.GetUtcNow()); }
            catch (Exception) { /* the begin record stands */ }
            await Send(pipe, seq => BrokerReadFrames.End(Nonce, req.RequestId, seq, end, endCode)).ConfigureAwait(false);
            return new BrokerResult(endCode ?? end, null, Wrote: false);
        }
        finally
        {
            await watching.CancelAsync().ConfigureAwait(false);
            try { await watcher.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// The adapter's output shaped for the wire: each item rebuilt from the field list (extra fields dropped, a bad item
    /// rejected), at most the check's cap and 200 items, then whole items cut from the end until the frame is ≤ 64 KB.
    /// Coverage: total = what the adapter says matched; returned = items sent; complete only when nothing was cut,
    /// rejected or left out.
    /// </summary>
    internal static (string State, string? Code, BrokerReadCoverage Coverage, List<JsonObject> Items) Pack(AdminCheckDef def, AdminReadOutput output,
        string nonce, string requestId, int seq, int n)
    {
        if (output.State is not "ok")
        {
            var state = output.State is "unsupported" ? "unsupported" : "failed";
            return (state, output.Code ?? "read_error", new BrokerReadCoverage(false, 0, 0), []);
        }
        var shaped = new List<JsonObject>();
        var rejected = 0;
        foreach (var item in output.Items)
        {
            if (AdminChecks.Shape(def, item, out var redacted) is { } s) shaped.Add(s);
            else rejected++;
            if (redacted) rejected++; // a free-text value nulled: the item is sent, but coverage is not complete
        }
        var cap = Math.Min(def.MaxItems, AdminChecks.MaxItems);
        var items = shaped.Take(cap).ToList();
        var total = Math.Max(output.Total, output.Items.Count);
        var code = output.Code is { } c && BrokerWire.IsCode(c) ? c : null;
        // Whole items only: the frame with its coverage must fit the 64 KB frame cap.
        while (true)
        {
            var complete = output.Complete && rejected == 0 && items.Count == total;
            var coverage = new BrokerReadCoverage(complete, total, items.Count);
            var frame = BrokerReadFrames.Result(nonce, requestId, seq, n, "ok", code, coverage, items);
            if (Encoding.UTF8.GetByteCount(frame.ToJsonString()) <= Math.Min(AdminChecks.MaxResultBytes, BrokerProtocol.MaxFrameBytes) || items.Count == 0)
                return ("ok", code, coverage, items);
            items.RemoveAt(items.Count - 1);
        }
    }

    private async Task Result(Stream pipe, BrokerReadRequest req, List<(int, string, string?)> results, int n, string state, string code)
    {
        results.Add((n, state, code));
        await Send(pipe, seq => BrokerReadFrames.Result(Nonce, req.RequestId, seq, n, state, code, new BrokerReadCoverage(false, 0, 0), [])).ConfigureAwait(false);
    }

    /// <summary>Every check ends the same way without reading (cancelled in the window, stopped before, or refused), then read_end.</summary>
    private async Task<BrokerResult> EndAll(Stream pipe, BrokerReadRequest req, string state, string code, string end, string endCode)
    {
        foreach (var c in req.Checks)
            await Send(pipe, seq => BrokerReadFrames.Result(Nonce, req.RequestId, seq, c.N, state, code, new BrokerReadCoverage(false, 0, 0), [])).ConfigureAwait(false);
        await Send(pipe, seq => BrokerReadFrames.End(Nonce, req.RequestId, seq, end, endCode)).ConfigureAwait(false);
        return new BrokerResult(endCode, null, Wrote: false);
    }

    private Task<BrokerResult> Refuse(Stream pipe, BrokerReadRequest req, string code) => EndAll(pipe, req, "failed", code, "refused", code);

    private async Task<BrokerResult> RefuseCoreAsync(Stream pipe, string? requestId, IReadOnlyList<BrokerReadCheck> checks, string code)
    {
        foreach (var c in checks)
            await Send(pipe, seq => BrokerReadFrames.Result(Nonce, requestId!, seq, c.N, "failed", code, new BrokerReadCoverage(false, 0, 0), [])).ConfigureAwait(false);
        await Send(pipe, seq => BrokerReadFrames.End(Nonce, requestId, seq, "refused", code)).ConfigureAwait(false);
        return new BrokerResult(code, null, Wrote: false);
    }

    /// <summary>
    /// A read connection the helper refuses before its own checks (no read adapters wired, a different administrator):
    /// a failed result per check when the request parses, then read_end refused. Nothing is read, the nonce is not consumed.
    /// </summary>
    internal static async Task<BrokerResult> RefuseAsync(Stream pipe, string nonce, JsonNode? frame, string code)
    {
        var req = BrokerReadRequest.Parse(frame);
        var requestId = req?.RequestId ?? BrokerReadRequest.RequestIdOf(frame);
        var seq = 0;
        if (req is not null && requestId is not null)
            foreach (var c in req.Checks)
                if (!await WriteBoundedAsync(pipe, BrokerReadFrames.Result(nonce, requestId, ++seq, c.N, "failed", code, new BrokerReadCoverage(false, 0, 0), []), DefaultWriteTimeout).ConfigureAwait(false))
                    return new BrokerResult(code, null, false);
        await WriteBoundedAsync(pipe, BrokerReadFrames.End(nonce, requestId, ++seq, "refused", code), DefaultWriteTimeout).ConfigureAwait(false);
        return new BrokerResult(code, null, false);
    }

    /// <summary>One frame with the next sequence number, under the write lock; nothing once the host is gone.</summary>
    private async Task Send(Stream pipe, Func<int, JsonObject> make)
    {
        await _write.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_lost) return;
            // Abandoned when the host stops draining or is gone: nothing more is sent, the session still ends (journal, exit).
            if (!await WriteBoundedAsync(pipe, make(++_seq), r.WriteTimeout).ConfigureAwait(false)) _lost = true;
        }
        finally { _write.Release(); }
    }

    /// <summary>A frame's write may take at most this long (m9 review item 2); a write that doesn't finish is abandoned.</summary>
    public static readonly TimeSpan DefaultWriteTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// One frame written within <paramref name="timeout"/>: the write is cancelled at the timeout and, should the stream
    /// ignore cancellation, abandoned (the caller never waits longer). False when it timed out or failed.
    /// </summary>
    internal static async Task<bool> WriteBoundedAsync(Stream pipe, JsonObject frame, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await BrokerProtocol.WriteAsync(pipe, frame, cts.Token).WaitAsync(timeout + TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException or OperationCanceledException or TimeoutException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// The read connection accepts only <c>stop</c> for this nonce: it cancels what has not started. Any other frame (a
    /// write request, a script, anything malformed) is refused: nothing further starts and the session ends refused. The
    /// host going away also starts nothing further.
    /// </summary>
    private async Task WatchAsync(Stream pipe, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                JsonNode? f;
                try { f = await BrokerProtocol.ReadAsync(pipe, ct).ConfigureAwait(false); }
                catch (InvalidDataException ex) when (!BrokerProtocol.StreamEnded(ex)) { f = null; } // malformed: refused; a lost host: below
                if (BrokerStop.Is(f, Nonce)) _stop = true;
                else _badFrame = true;
                await _stopSignal.CancelAsync().ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            _lost = true;
            try { await _stopSignal.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) { }
        }
    }
}

using System.Text;
using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions.Scripts;
using OptimizationTool.Engine.Actions.Scripts.Guard;
using OptimizationTool.Windows.Actions.Broker;

namespace OptimizationTool.Broker;

/// <summary>
/// What the broker's own script window shows (m9 §5.4; design README "Windows dialogs", "Administrator changes" item 5, v57):
/// the heading, the fixed line, the policy line when it adds -ExecutionPolicy Bypass, "Found by Optimus:" with the guard's
/// warnings (its own run of the guard), the full text decoded from the exact bytes it will run, and the required tick.
/// Nothing in it comes from the host.
/// </summary>
public sealed record BrokerScriptView(string Text, int Lines, IReadOnlyList<GuardFinding> Warnings, bool BypassesPolicy)
{
    public const string TitleBar = BrokerConfirmView.TitleBar;
    public const string Heading = "Run this script as administrator?";
    public const string TickText = BrokerConfirmView.TickText;
    public const string FixedLine = "Optimus can’t check what a script does and can’t undo it. No warning here doesn’t mean it is safe.";
    public const string PolicyLine = "Runs even though scripts are normally off on this PC.";
    public const string FoundBy = "Found by Optimus:";
    /// <summary>The red line after the script box, before the tick (design v62).</summary>
    public const string RedText = "Can’t be undone";
    /// <summary>The buttons (design v62): Cancel and Run (Apply stays for administrator changes).</summary>
    public const string CancelText = BrokerConfirmView.CancelText;
    public const string RunText = "Run";
    public bool NeedsTick => true;
    public static readonly TimeSpan ApplyDelay = TimeSpan.FromSeconds(3);

    /// <summary>"Line 12: turns off a Windows security feature", one per warning (lower-cased first letter, as the card).</summary>
    public IReadOnlyList<string> WarningLines => [.. Warnings.Select(w => (w.Line is { } l ? $"Line {l}: " : "") + LowerFirst(w.Text ?? w.Rule))];

    /// <summary>The lines above the script box, in order: heading, fixed line, policy line, then "Found by Optimus:" and the warnings.</summary>
    public IReadOnlyList<BrokerLine> HeaderLines()
    {
        var lines = new List<BrokerLine> { new(BrokerLineKind.Heading, Heading), new(BrokerLineKind.Notice, FixedLine) };
        if (BypassesPolicy) lines.Add(new(BrokerLineKind.Notice, PolicyLine));
        if (Warnings.Count > 0)
        {
            lines.Add(new(BrokerLineKind.FoundBy, FoundBy));
            lines.AddRange(WarningLines.Select(w => new BrokerLine(BrokerLineKind.Warning, w)));
        }
        return lines;
    }

    /// <summary>The lines after the script box, before the tick: the red "Can’t be undone" (a script always gets it).</summary>
    public IReadOnlyList<BrokerLine> FooterLines() => [new(BrokerLineKind.Red, RedText)];

    private static string LowerFirst(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];
}

/// <summary>The broker's own script window: true = Run, false = Cancel. Fakes in tests; the real one is never shown by tests.</summary>
public interface IBrokerScriptConfirm
{
    Task<bool> AskAsync(BrokerScriptView view, CancellationToken ct);
}

/// <summary>The broker's own record of elevated script runs (in the protected journal): an operation runs at most once.</summary>
public interface IBrokerScriptJournal
{
    bool Seen(string operationId);
    /// <summary>Written before the interpreter starts; returns the record's ID.</summary>
    string Begin(BrokerScriptRequest request, DateTimeOffset at);
    void End(string receiptId, string state, int? exitCode, DateTimeOffset at);
}

/// <summary>The broker's own script wiring: runner (its own job), policy reader, PowerShell's parser, window, journal, run folders.</summary>
public sealed record BrokerScriptOptions(IScriptRunner Runner, IScriptPolicy Policy, IScriptParser? Parser, IBrokerScriptConfirm Confirm, IBrokerScriptJournal Journal,
    string RunsRoot)
{
    /// <summary>Same option as the host's: accept the C# layer alone when the parser is unavailable. Off: fail closed.</summary>
    public bool AllowGuardWithoutParser { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan ParseTimeout { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>How often queued transcript lines are sent when fewer than a frame's worth are waiting.</summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMilliseconds(200);
}

/// <summary>
/// One elevated script run in the broker (m9 §5.4). The broker trusts none of the request: size, the one serialisation
/// (UTF-8 with BOM, CRLF, printable ASCII), the SHA-256 of the bytes, its own run of ScriptGuard over the text (with
/// PowerShell's parser, fail closed), its own policy read, then its own window with the text, the warnings and the tick.
/// The nonce is consumed and the operation recorded before anything starts, so a replay runs nothing. The runner stages
/// the bytes in its own run folder (administrators-only write), runs the fixed interpreter line in a job it owns, and the
/// transcript goes back in bounded, sequence-numbered frames; the terminal frame is always sent last. Stop, the time
/// limit and a lost host end every process in the job.
/// </summary>
internal sealed class BrokerScriptSession(BrokerSessionOptions o, BrokerScriptOptions s, TimeProvider time)
{
    private readonly SemaphoreSlim _write = new(1, 1);
    private string Nonce => o.Nonce;
    private readonly object _gate = new();
    private readonly List<(bool, string)> _pending = [];
    private int _seq;
    private int _lines;
    private long _chars;
    private bool _truncated;
    private volatile bool _lost;

    public async Task<BrokerResult> RunAsync(Stream pipe, JsonNode frame, CancellationToken ct)
    {
        var req = BrokerScriptRequest.Parse(frame);
        if (req is null) return await NotStarted(pipe, "bad_request").ConfigureAwait(false);
        if (!BrokerProtocol.IsValidNonce(req.Nonce) || !BrokerProtocol.NonceEquals(req.Nonce, o.Nonce)) return await NotStarted(pipe, "bad_nonce").ConfigureAwait(false);
        switch (o.Ledger.TryConsume(o.Nonce))
        {
            case NonceConsume.Consumed: break;
            case NonceConsume.Replayed: return await NotStarted(pipe, "replayed").ConfigureAwait(false);
            default: return await NotStarted(pipe, "journal_failed").ConfigureAwait(false);
        }
        bool seen;
        try { seen = s.Journal.Seen(req.OperationId); }
        catch (Exception) { return await NotStarted(pipe, "journal_failed").ConfigureAwait(false); }
        if (seen) return await NotStarted(pipe, "replayed").ConfigureAwait(false);

        // The one serialisation, re-made from the bytes' text: anything else is refused, never repaired.
        if (Decode(req.Bytes) is not { } ser) return await NotStarted(pipe, "serialisation").ConfigureAwait(false);
        if (ser.Sha256 != req.Sha256) return await NotStarted(pipe, "hash_mismatch").ConfigureAwait(false);

        ScriptPolicyState policy;
        try { policy = s.Policy.Read(); }
        catch (Exception) { policy = new ScriptPolicyState(true, false); }
        if (policy.Managed) return await NotStarted(pipe, "managed").ConfigureAwait(false);
        if (policy.NeedsBypass && !req.BypassPolicy) return await NotStarted(pipe, "policy_changed").ConfigureAwait(false);

        // The broker's own guard (the same ScriptGuard code the host ran, with its own parse).
        ScriptParse? parse = null;
        if (s.Parser is { } parser)
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(s.ParseTimeout);
                parse = await parser.ParseAsync(ser.Bytes, limit.Token).WaitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (Exception) { parse = null; }
        var guard = ScriptGuard.Check(ser.Text, parse, requireParser: !s.AllowGuardWithoutParser);
        if (guard.Refused.Count > 0) return await NotStarted(pipe, "guard_" + guard.Refused[0].Rule).ConfigureAwait(false);

        var view = new BrokerScriptView(ser.Text, ser.Lines.Count, guard.Warnings, policy.NeedsBypass);
        bool run;
        try { run = await s.Confirm.AskAsync(view, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { run = false; }
        if (!run) return await NotStarted(pipe, "broker_cancelled").ConfigureAwait(false);

        string receiptId;
        try { receiptId = s.Journal.Begin(req, time.GetUtcNow()); }
        catch (Exception) { return await NotStarted(pipe, "journal_failed").ConfigureAwait(false); }
        o.ExtendBudget?.Invoke(s.Timeout + TimeSpan.FromMinutes(2));

        using var stop = new CancellationTokenSource();
        using var watching = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watcher = Task.Run(() => WatchAsync(pipe, req.Nonce, stop, watching.Token), CancellationToken.None);
        using var flushing = new CancellationTokenSource();
        var flusher = Task.Run(() => FlushLoopAsync(pipe, flushing.Token), CancellationToken.None);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        deadline.CancelAfter(s.Timeout);

        ScriptRunResult result;
        try
        {
            result = await s.Runner.RunAsync(new ScriptRunRequest(req.OperationId, ser.Bytes, ser.Sha256, s.Timeout, policy.NeedsBypass, s.RunsRoot),
                new Observer(this, pipe), deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { result = ScriptRunResult.KilledRun(null, TimeSpan.Zero); }
        catch (Exception) { result = ScriptRunResult.KilledRun(null, TimeSpan.Zero); }
        await flushing.CancelAsync().ConfigureAwait(false);
        try { await flusher.ConfigureAwait(false); } catch (OperationCanceledException) { }
        await watching.CancelAsync().ConfigureAwait(false);
        try { await watcher.ConfigureAwait(false); } catch (OperationCanceledException) { }

        var (state, exit, code) = result.Exited ? ("exited", result.ExitCode, (string?)null)
            : result.NotStartedCode is { } nc ? ("not_started", (int?)null, nc)
            : ("killed", (int?)null, stop.IsCancellationRequested ? "stopped" : deadline.IsCancellationRequested ? "timeout" : null);
        try { s.Journal.End(receiptId, state, exit, time.GetUtcNow()); }
        catch (Exception) { /* the begin record stands */ }
        await Flush(pipe).ConfigureAwait(false);
        await Send(pipe, seq => BrokerScriptFrames.End(o.Nonce, seq, state, exit, code, _lines, _truncated)).ConfigureAwait(false);
        return new BrokerResult(state == "exited" ? "completed" : state, null, Wrote: state != "not_started");
    }

    /// <summary>The bytes as the one serialisation: BOM, printable ASCII, CRLF; re-serialised from their text, identical.</summary>
    internal static ScriptSerialisation? Decode(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes.Length > BrokerScriptRequest.MaxBytes || bytes[0] != 0xEF || bytes[1] != 0xBB || bytes[2] != 0xBF) return null;
        if (bytes.Skip(3).Any(b => b >= 0x80)) return null;
        var ser = ScriptText.Serialise(Encoding.ASCII.GetString(bytes, 3, bytes.Length - 3));
        return ser.Ok && ser.Bytes.AsSpan().SequenceEqual(bytes) ? ser : null;
    }

    private Task<BrokerResult> NotStarted(Stream pipe, string code) => NotStartedAsync(pipe, code);

    /// <summary>A script request this broker can't take at all: the terminal frame alone, nothing run.</summary>
    internal static async Task<BrokerResult> RefuseAsync(Stream pipe, string nonce, string code)
    {
        try { await BrokerProtocol.WriteAsync(pipe, BrokerScriptFrames.End(nonce, 1, "not_started", null, code, 0, false), CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        return new BrokerResult(code, null, false);
    }

    private async Task<BrokerResult> NotStartedAsync(Stream pipe, string code)
    {
        await Send(pipe, seq => BrokerScriptFrames.End(o.Nonce, seq, "not_started", null, code, 0, false)).ConfigureAwait(false);
        return new BrokerResult(code, null, false);
    }

    private sealed class Observer(BrokerScriptSession owner, Stream pipe) : IScriptRunObserver
    {
        public void Started(DateTimeOffset at, string runFolder) =>
            owner.Send(pipe, seq => BrokerScriptFrames.Started(owner.Nonce, seq)).GetAwaiter().GetResult();

        public void Line(bool stderr, string text)
        {
            bool full;
            lock (owner._gate)
            {
                owner._lines++;
                var clipped = text.Length > BrokerScriptFrames.MaxLineChars ? text[..BrokerScriptFrames.MaxLineChars] : text;
                if (owner._truncated || owner._chars + clipped.Length > BrokerScriptFrames.MaxTranscriptChars)
                {
                    owner._truncated = true; // the rest is counted, never sent
                    return;
                }
                owner._chars += clipped.Length;
                owner._pending.Add((stderr, clipped));
                full = owner._pending.Count >= BrokerScriptFrames.MaxLinesPerFrame;
            }
            if (full) owner.Flush(pipe).GetAwaiter().GetResult();
        }
    }

    private async Task FlushLoopAsync(Stream pipe, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(s.FlushInterval, time, ct).ConfigureAwait(false);
                await Flush(pipe).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private readonly SemaphoreSlim _flush = new(1, 1);

    /// <summary>Sends the queued lines, in order, in frames of at most 64 lines and about 12 K characters (one flusher at a time).</summary>
    private async Task Flush(Stream pipe)
    {
        await _flush.WaitAsync().ConfigureAwait(false);
        try { await FlushQueued(pipe).ConfigureAwait(false); }
        finally { _flush.Release(); }
    }

    private async Task FlushQueued(Stream pipe)
    {
        while (true)
        {
            List<(bool, string)> batch;
            lock (_gate)
            {
                if (_pending.Count == 0) return;
                var take = 0;
                var size = 0;
                while (take < _pending.Count && take < BrokerScriptFrames.MaxLinesPerFrame && (take == 0 || size + _pending[take].Item2.Length < 12 * 1024))
                    size += _pending[take++].Item2.Length;
                batch = _pending.GetRange(0, take);
                _pending.RemoveRange(0, take);
            }
            await Send(pipe, seq => BrokerScriptFrames.Transcript(o.Nonce, seq, batch)).ConfigureAwait(false);
        }
    }

    /// <summary>One frame with the next sequence number, under the write lock; nothing once the host is gone.</summary>
    private async Task Send(Stream pipe, Func<int, JsonObject> make)
    {
        await _write.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_lost) return;
            var frame = make(++_seq);
            try { await BrokerProtocol.WriteAsync(pipe, frame, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException) { _lost = true; }
        }
        finally { _write.Release(); }
    }

    /// <summary>A stop frame, or the host going away, ends every process in the job.</summary>
    private async Task WatchAsync(Stream pipe, string nonce, CancellationTokenSource stop, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var f = await BrokerProtocol.ReadAsync(pipe, ct).ConfigureAwait(false);
                if (BrokerStop.Is(f, nonce)) { await stop.CancelAsync().ConfigureAwait(false); return; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            _lost = true;
            try { await stop.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) { }
        }
    }
}

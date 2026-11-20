using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions.Scripts;
using OptimizationTool.Windows.Actions.Broker;

namespace OptimizationTool.Broker.Tests;

/// <summary>
/// An elevated script inside the broker (m9 §5.4), with fakes for the runner, parser, policy, window and journal. The
/// broker trusts nothing from the host: size, serialisation, hash, its own guard and policy, its own window; a replay runs
/// nothing; the transcript comes back in bounded, sequence-numbered frames ending with the terminal frame. Nothing runs.
/// </summary>
public sealed class ScriptSessionTests
{
    internal sealed class Runner(Log log) : IScriptRunner
    {
        public readonly List<ScriptRunRequest> Requests = [];
        public List<(bool, string)> Lines = [(false, "hello"), (true, "careful")];
        public int Exit { get; set; }
        public TaskCompletionSource? Gate;
        public volatile bool Cancelled;

        public async Task<ScriptRunResult> RunAsync(ScriptRunRequest request, IScriptRunObserver observer, CancellationToken stop)
        {
            log.Add("run");
            Requests.Add(request);
            observer.Started(DateTimeOffset.UtcNow, @"Q:\ProgramData\OptimizationTool\script-runs\x");
            foreach (var (err, text) in Lines) observer.Line(err, text);
            if (Gate is { } g)
                try { await g.Task.WaitAsync(stop); }
                catch (OperationCanceledException) { Cancelled = true; return ScriptRunResult.KilledRun(null, TimeSpan.Zero); }
            return ScriptRunResult.Exit(Exit, null, TimeSpan.FromMilliseconds(3));
        }
    }

    private sealed class Policy : IScriptPolicy
    {
        public ScriptPolicyState State = new(false, true);
        public ScriptPolicyState Read() => State;
    }

    private sealed class Parser : IScriptParser
    {
        public ScriptParse? Result = new([], [], [], []);
        public Task<ScriptParse?> ParseAsync(byte[] bytes, CancellationToken ct) => Task.FromResult(Result);
    }

    private sealed class Window(Log log) : IBrokerScriptConfirm
    {
        public bool Run = true;
        public readonly List<BrokerScriptView> Shown = [];
        public Task<bool> AskAsync(BrokerScriptView view, CancellationToken ct)
        {
            Shown.Add(view);
            log.Add("window");
            return Task.FromResult(Run);
        }
    }

    private sealed class ScriptJournal(Log log) : IBrokerScriptJournal
    {
        public readonly HashSet<string> Ops = [];
        public readonly List<(string, string, int?)> Ends = [];
        public bool Seen(string operationId) => Ops.Contains(operationId);
        public string Begin(BrokerScriptRequest request, DateTimeOffset at)
        {
            Ops.Add(request.OperationId);
            log.Add("journal");
            return "rcpt-0000000000000a01";
        }
        public void End(string receiptId, string state, int? exitCode, DateTimeOffset at) => Ends.Add((receiptId, state, exitCode));
    }

    private sealed class Setup
    {
        public readonly Rig Rig = new();
        public readonly Runner Runner;
        public readonly Policy Policy = new();
        public readonly Parser Parser = new();
        public readonly Window Window;
        public readonly ScriptJournal Journal;
        public BrokerScriptOptions Options;

        public Setup()
        {
            Runner = new Runner(Rig.Log);
            Window = new Window(Rig.Log);
            Journal = new ScriptJournal(Rig.Log);
            Options = new BrokerScriptOptions(Runner, Policy, Parser, Window, Journal, @"Q:\ProgramData\OptimizationTool\script-runs") { FlushInterval = TimeSpan.FromMilliseconds(20) };
        }

        public BrokerSession Session(string? nonce = null, bool scripts = true) =>
            new(new BrokerSessionOptions(nonce ?? Kit.Nonce(), Kit.HostImage, Kit.Self, Rig.Handlers, Rig.Journal, Rig.Ledger, Rig.Confirm) { Scripts = scripts ? Options : null });

        public async Task<(BrokerResult Result, List<BrokerScriptFrames.Frame> Frames, Duplex Pipe)> Run(JsonObject request, string? nonce = null, bool scripts = true,
            Func<Duplex, Task>? during = null)
        {
            var pipe = await Duplex.WithFrames(request);
            var running = Session(nonce, scripts).RunAsync(pipe, Kit.Host, CancellationToken.None);
            if (during is not null) await during(pipe);
            var result = await running.WaitAsync(TimeSpan.FromSeconds(20));
            return (result, await Frames(pipe, nonce ?? Kit.Nonce()), pipe);
        }
    }

    internal static async Task<List<BrokerScriptFrames.Frame>> Frames(Duplex pipe, string nonce)
    {
        var list = new List<BrokerScriptFrames.Frame>();
        pipe.Output.Position = 0;
        while (pipe.Output.Position < pipe.Output.Length)
            list.Add(BrokerScriptFrames.Parse(await BrokerProtocol.ReadAsync(pipe.Output, CancellationToken.None), nonce) ?? throw new InvalidDataException("bad frame"));
        return list;
    }

    internal const string Text = "# Reset the print spooler\nRemove-Item C:\\Windows\\Temp\\spool\\* -Recurse\nWrite-Output \"done\"";

    internal static JsonObject Request(string text = Text, string? nonce = null, string op = "op-0000000000000001", Func<byte[], byte[]>? tamper = null,
        string? sha = null, bool bypass = true)
    {
        var ser = ScriptText.Serialise(text);
        var bytes = tamper?.Invoke(ser.Bytes) ?? ser.Bytes;
        return new BrokerScriptRequest(nonce ?? Kit.Nonce(), "plan-0000000000000001", op, sha ?? ScriptText.Hash(bytes), bytes, bypass).ToJson();
    }

    private static string End(List<BrokerScriptFrames.Frame> frames) => frames[^1].Kind == "end" ? frames[^1].State + ":" + frames[^1].Code : "no end";

    [Fact]
    public async Task Runs_after_its_own_checks_and_window_and_streams_the_transcript_in_sequence()
    {
        var s = new Setup();
        var (result, frames, _) = await s.Run(Request());
        Assert.Equal("completed", result.Code);
        Assert.True(result.Wrote);
        // Its own window: the exact bytes' text (CRLF), its own guard's warning, the policy line, the required tick.
        var view = Assert.Single(s.Window.Shown);
        Assert.Equal(ScriptText.Serialise(Text).Text, view.Text);
        Assert.Equal(3, view.Lines);
        Assert.Contains(view.Warnings, w => w.Rule == "deletes_files" && w.Line == 2);
        Assert.StartsWith("Line 2: ", view.WarningLines.Single(l => l.StartsWith("Line 2", StringComparison.Ordinal)));
        Assert.True(view.BypassesPolicy);
        Assert.True(view.NeedsTick);
        // Nonce consumed and the operation journalled before the runner starts.
        Assert.Equal(["window", "journal", "run"], s.Rig.Log.Events);
        Assert.Contains(Kit.Nonce(), s.Rig.Ledger.Used);
        var run = Assert.Single(s.Runner.Requests);
        Assert.Equal((ScriptText.Serialise(Text).Sha256, true, "op-0000000000000001"), (run.Sha256, run.BypassPolicy, run.OperationId));
        // Frames: started, transcript, end; seq 1, 2, 3.
        Assert.Equal(["started", "transcript", "end"], frames.Select(f => f.Kind));
        Assert.Equal([1, 2, 3], frames.Select(f => f.Seq));
        Assert.Equal([(false, "hello"), (true, "careful")], frames[1].Lines);
        Assert.Equal(("exited", 0), (frames[2].State!, frames[2].ExitCode!.Value));
        Assert.Equal(("rcpt-0000000000000a01", "exited", (int?)0), Assert.Single(s.Journal.Ends));
    }

    [Theory]
    [InlineData("# Faster boot\nbcdedit /set {current} bootmenupolicy legacy", "guard_boot_or_disk")]
    [InlineData("$c = \"Get-Process\"\nInvoke-Expression $c", "guard_dynamic_code")]
    public async Task The_brokers_own_guard_refuses_what_the_host_let_through(string text, string code)
    {
        var s = new Setup();
        var (result, frames, _) = await s.Run(Request(text));
        Assert.Equal("not_started:" + code, End(frames));
        Assert.Single(frames);
        Assert.Empty(s.Window.Shown);
        Assert.Empty(s.Runner.Requests);
        Assert.False(result.Wrote);
    }

    [Fact]
    public async Task Hash_serialisation_size_policy_and_parser_refusals_run_nothing()
    {
        async Task<string> Refused(JsonObject req, Action<Setup>? arrange = null)
        {
            var s = new Setup();
            arrange?.Invoke(s);
            var (_, frames, _) = await s.Run(req);
            Assert.Empty(s.Runner.Requests);
            Assert.Empty(s.Window.Shown);
            return End(frames);
        }
        Assert.Equal("not_started:hash_mismatch", await Refused(Request(sha: new string('0', 64))));
        // Not the one serialisation: LF line ends, no BOM, a non-ASCII byte.
        Assert.Equal("not_started:serialisation", await Refused(Request(tamper: b => [.. b.Where(x => x != (byte)'\r')])));
        Assert.Equal("not_started:serialisation", await Refused(Request(tamper: b => b[3..])));
        Assert.Equal("not_started:serialisation", await Refused(Request(tamper: b => { var c = (byte[])b.Clone(); c[5] = 0xC3; return c; })));
        // Over the size cap: not even a request.
        var big = Request();
        big["bytes"] = Convert.ToBase64String(new byte[ScriptText.MaxBytes + 1]);
        Assert.Equal("not_started:bad_request", await Refused(big));
        // The broker reads the policy itself.
        Assert.Equal("not_started:managed", await Refused(Request(), s => s.Policy.State = new ScriptPolicyState(true, false)));
        Assert.Equal("not_started:policy_changed", await Refused(Request(bypass: false)));
        // PowerShell's parser unavailable in the broker: fail closed.
        Assert.Equal("not_started:guard_unchecked", await Refused(Request(), s => s.Parser.Result = null));
        // Cancelled in its window.
        var c = new Setup();
        c.Window.Run = false;
        var (_, cf, _) = await c.Run(Request());
        Assert.Equal("not_started:broker_cancelled", End(cf));
        Assert.Empty(c.Runner.Requests);
    }

    [Fact]
    public async Task A_nonce_ledger_that_cannot_be_locked_or_written_runs_nothing()
    {
        var s = new Setup();
        s.Rig.Ledger.Fail = true;
        var (_, frames, _) = await s.Run(Request());
        Assert.Equal("not_started:journal_failed", End(frames));
        Assert.Empty(s.Runner.Requests);
    }

    [Fact]
    public async Task A_replay_runs_nothing()
    {
        var s = new Setup();
        await s.Run(Request());
        Assert.Single(s.Runner.Requests);
        // The same launch nonce again.
        var (_, again, _) = await s.Run(Request());
        Assert.Equal("not_started:replayed", End(again));
        // A new launch nonce for the same operation.
        var (_, sameOp, _) = await s.Run(Request(nonce: Kit.Nonce('e')), nonce: Kit.Nonce('e'));
        Assert.Equal("not_started:replayed", End(sameOp));
        // Another nonce than this broker was launched with.
        var (_, wrong, _) = await s.Run(Request(nonce: Kit.Nonce('f'), op: "op-0000000000000002"), nonce: Kit.Nonce('a'));
        Assert.Equal("not_started:bad_nonce", End(wrong));
        Assert.Single(s.Runner.Requests);
        // A broker without the script wiring refuses the type outright.
        var (_, none, _) = await new Setup().Run(Request(), scripts: false);
        Assert.Equal("not_started:action_not_allowed", End(none));
    }

    [Fact]
    public async Task Stop_and_a_lost_host_end_the_run()
    {
        var s = new Setup();
        s.Runner.Gate = new TaskCompletionSource();
        var (result, frames, _) = await s.Run(Request(), during: async p =>
        {
            while (s.Runner.Requests.Count == 0) await Task.Delay(5);
            await p.Push(BrokerStop.ToJson(Kit.Nonce()));
        });
        Assert.True(s.Runner.Cancelled);
        Assert.Equal("killed:stopped", End(frames));
        Assert.Equal("killed", result.Code);

        var lost = new Setup();
        lost.Runner.Gate = new TaskCompletionSource();
        await lost.Run(Request(), during: async p =>
        {
            while (lost.Runner.Requests.Count == 0) await Task.Delay(5);
            p.HangUp();
        });
        Assert.True(lost.Runner.Cancelled); // the job ends when the host is gone
        Assert.Equal("killed", Assert.Single(lost.Journal.Ends).Item2);
    }

    [Fact]
    public async Task Transcript_frames_are_bounded_and_the_terminal_frame_is_always_last()
    {
        var s = new Setup();
        s.Runner.Lines = [.. Enumerable.Range(0, 300).Select(i => (i % 2 == 0, "line " + i))];
        var (_, frames, _) = await s.Run(Request());
        Assert.Equal(Enumerable.Range(1, frames.Count), frames.Select(f => f.Seq));
        Assert.All(frames.Where(f => f.Kind == "transcript"), f => Assert.InRange(f.Lines.Count, 1, BrokerScriptFrames.MaxLinesPerFrame));
        Assert.Equal(300, frames.Where(f => f.Kind == "transcript").Sum(f => f.Lines.Count));
        Assert.Equal("end", frames[^1].Kind);

        // Over 1 MiB of text: the rest is counted, not sent, and the end frame still arrives.
        var big = new Setup();
        big.Runner.Lines = [.. Enumerable.Range(0, 400).Select(i => (false, new string('x', 4000)))];
        var (_, bf, pipe) = await big.Run(Request());
        var sent = bf.Where(f => f.Kind == "transcript").Sum(f => f.Lines.Sum(l => l.Text.Length));
        Assert.True(sent <= BrokerScriptFrames.MaxTranscriptChars);
        pipe.Output.Position = 0;
        JsonNode? last = null;
        while (pipe.Output.Position < pipe.Output.Length) last = await BrokerProtocol.ReadAsync(pipe.Output, CancellationToken.None);
        Assert.True(last!["truncated"]!.GetValue<bool>());
        Assert.Equal(400, last["lines"]!.GetValue<int>());
        // A line over 4096 characters is clipped.
        var wide = new Setup();
        wide.Runner.Lines = [(false, new string('y', 9000))];
        var (_, wf, _) = await wide.Run(Request());
        Assert.Equal(BrokerScriptFrames.MaxLineChars, wf.Single(f => f.Kind == "transcript").Lines.Single().Text.Length);
    }

    [Fact]
    public void Script_request_and_frames_parse_strictly()
    {
        var ok = Request();
        Assert.NotNull(BrokerScriptRequest.Parse(ok));
        var extra = (JsonObject)ok.DeepClone();
        extra["command"] = "powershell -c x";
        Assert.Null(BrokerScriptRequest.Parse(extra));
        var noOptions = (JsonObject)ok.DeepClone();
        noOptions.Remove("options");
        Assert.Null(BrokerScriptRequest.Parse(noOptions));
        var v2 = (JsonObject)ok.DeepClone();
        v2["version"] = 2;
        Assert.Null(BrokerScriptRequest.Parse(v2));
        Assert.Null(BrokerScriptFrames.Parse(BrokerScriptFrames.Started(Kit.Nonce('b'), 1), Kit.Nonce()));      // another nonce
        Assert.Null(BrokerScriptFrames.Parse(BrokerScriptFrames.Started(Kit.Nonce(), 0), Kit.Nonce()));         // seq starts at 1
        var bad = BrokerScriptFrames.End(Kit.Nonce(), 2, "verified", 0, null, 0, false);
        Assert.Null(BrokerScriptFrames.Parse(bad, Kit.Nonce()));                                                 // never verified
        Assert.Equal("exited", BrokerScriptFrames.Parse(BrokerScriptFrames.End(Kit.Nonce(), 2, "exited", 3, null, 1, false), Kit.Nonce())!.State);
    }
}

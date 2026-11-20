using System.Security.Principal;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using OptimizationTool.Broker;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Broker;
using OptimizationTool.Windows.Actions.Firewall;
using ProtectionResult = OptimizationTool.Windows.Actions.Protection.Protection;

namespace OptimizationTool.Broker.Tests;

// Fakes only: no test touches this PC's firewall, registry, restore points, the journal folder, a window or elevation.

/// <summary>What happened, in order, across the fakes (journal before write is asserted on it).</summary>
internal sealed class Log
{
    public List<string> Events { get; } = [];
    public void Add(string e) { lock (Events) Events.Add(e); }
}

internal sealed class FakeFirewall(Log? log = null) : IPublicFirewallReader, IPublicFirewallWriter
{
    public PublicFirewallSnapshot State { get; set; } = Snap(local: false, effective: false);
    public bool Ignore { get; set; }
    public int Writes { get; private set; }
    public Action? OnWrite { get; set; }

    public static PublicFirewallSnapshot Snap(bool? local, bool? effective) =>
        new(local, effective, PublicActive: true, PolicyConfigured: false, MdmConfigured: false, OtherFirewallOn: false,
            ConsoleSession: true, RemoteSession: false, Managed: false, Listening: [], InboundRules: []);

    public PublicFirewallSnapshot Read(CancellationToken ct) => State;

    public void SetPublicEnabled(bool enabled)
    {
        Writes++;
        log?.Add("write:" + FirewallPlan.ActionId);
        OnWrite?.Invoke();
        if (!Ignore) State = State with { LocalEnabled = enabled, EffectiveEnabled = enabled };
    }
}

/// <summary>
/// A generic administrator step for session tests: a single in-memory value {value}. Target {type:"test", id}, parameters
/// {to} (or {restore} for an undo). Its result can be forced; Protected makes the broker's own lists refuse it.
/// </summary>
internal sealed class FakeBrokerHandler(string actionId, Log log) : IBrokerHandler
{
    public int Value;
    public int Writes;
    public string? Force; // "failed" | "indeterminate" | "throw"
    public ProtectionResult? Protected;
    public IReadOnlyList<string> ExtraFlags = [];
    public Action? OnWrite;
    public string RowText = "Value";

    public string ActionId => actionId;
    public string Version => "1.0.0";
    public string Label => "Test " + actionId;
    public IReadOnlyList<string> Flags => [];

    public bool IsUndo(BrokerStepRequest step) => step.Parameters?["restore"] is not null;

    public ActionResolution Resolve(BrokerStepRequest step)
    {
        var to = (step.Parameters?["to"] ?? step.Parameters?["restore"])?.GetValue<int>();
        if (to is null) return ActionResolution.Ineligible("invalid_arguments", "No value.");
        if (to == Value) return ActionResolution.Ineligible("already_set", "Already set.");
        return new ActionResolution(true, Kit.TestTarget(), new JsonObject { ["value"] = Value }, new JsonObject { ["value"] = to },
            [new PlanRow(RowText, Value.ToString(System.Globalization.CultureInfo.InvariantCulture), to.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))],
            [], [], null, Flags: ExtraFlags, Privilege: "admin");
    }

    public ProtectionResult? Protect(ActionResolution resolution) => Protected;

    public ActionOutcome WriteAndReadBack(ActionResolution r, BrokerStepRequest step, CancellationToken ct)
    {
        log.Add($"write:{actionId}:{step.N}");
        Writes++;
        OnWrite?.Invoke();
        if (Force == "throw") throw new IOException("boom");
        if (Force is null) Value = r.After["value"]!.GetValue<int>();
        if (Force == "indeterminate") Value = 999;
        var observed = new JsonObject { ["value"] = Value };
        return JsonNode.DeepEquals(observed, r.After) ? new ActionOutcome("verified", observed, "Set, read back.")
            : JsonNode.DeepEquals(observed, r.Before) ? new ActionOutcome("failed", observed, "Still the old value.", "not_applied")
            : new ActionOutcome("indeterminate", observed, "Unexpected.", "unexpected_state");
    }
}

/// <summary>The protected journal in memory. Fail makes the next prepared record throw.</summary>
internal sealed class FakeJournal(Log log) : IBrokerJournal
{
    public readonly Dictionary<string, (BrokerRequest Request, Dictionary<int, (BrokerStepRequest Step, ActionResolution R)> Prepared, Dictionary<int, ActionOutcome> Outcomes)> Sessions = [];
    public bool FailPrepared;
    private int _next;

    public string Begin(BrokerRequest request, DateTimeOffset at)
    {
        var id = $"rcpt-{++_next:x16}";
        Sessions[id] = (request, [], []);
        log.Add("begin");
        return id;
    }

    public void Prepared(string receiptId, BrokerStepRequest step, ActionResolution resolution, DateTimeOffset at)
    {
        if (FailPrepared) throw new IOException("disk full");
        Sessions[receiptId].Prepared[step.N] = (step, resolution);
        log.Add($"prepared:{step.N}");
    }

    public void Outcome(string receiptId, int n, ActionOutcome outcome, DateTimeOffset at)
    {
        Sessions[receiptId].Outcomes[n] = outcome;
        log.Add($"outcome:{n}:{outcome.State}");
    }

    public BrokerRecordStep? Find(string receiptId, int n)
    {
        if (!Sessions.TryGetValue(receiptId, out var s) || !s.Prepared.TryGetValue(n, out var p)) return null;
        s.Outcomes.TryGetValue(n, out var o);
        return new BrokerRecordStep(receiptId, s.Request.PlanId, n, p.Step.ActionId, p.R.Target, p.R.Before, p.R.After, o?.State, o?.Observed);
    }
}

internal sealed class FakeLedger : INonceLedger
{
    public HashSet<string> Used { get; } = [];
    /// <summary>Models a ledger that can't be locked, trusted or written.</summary>
    public bool Fail { get; set; }
    public NonceConsume TryConsume(string nonce) => Fail ? NonceConsume.Failed : Used.Add(nonce) ? NonceConsume.Consumed : NonceConsume.Replayed;
}

/// <summary>The broker's window: records what it was shown; answers Apply unless told to cancel.</summary>
internal sealed class FakeConfirm(Log log) : IBrokerConfirm
{
    public bool Apply = true;
    public List<BrokerConfirmView> Shown { get; } = [];
    public Task<bool> AskAsync(BrokerConfirmView view, CancellationToken ct)
    {
        Shown.Add(view);
        log.Add("confirm");
        return Task.FromResult(Apply);
    }
}

/// <summary>
/// Stands in for the pipe: reads frames pushed by the test (blocking while none are queued, like a live pipe) and collects
/// what the broker writes. <see cref="HangUp"/> ends the input (the host is gone).
/// </summary>
internal sealed class Duplex : Stream
{
    private readonly Channel<byte[]> _in = Channel.CreateUnbounded<byte[]>();
    private byte[] _current = [];
    private int _offset;
    public MemoryStream Output { get; } = new();

    public static async Task<Duplex> WithFrames(params JsonObject[] frames)
    {
        var d = new Duplex();
        foreach (var f in frames) await d.Push(f);
        return d;
    }

    public async Task Push(JsonObject frame)
    {
        var ms = new MemoryStream();
        await BrokerProtocol.WriteAsync(ms, frame, CancellationToken.None);
        _in.Writer.TryWrite(ms.ToArray());
    }

    public void PushRaw(byte[] bytes) => _in.Writer.TryWrite(bytes);
    public void HangUp() => _in.Writer.TryComplete();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (_offset >= _current.Length)
        {
            if (!await _in.Reader.WaitToReadAsync(ct)) return 0;
            if (_in.Reader.TryRead(out var next)) { _current = next; _offset = 0; }
        }
        var n = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsMemory(_offset, n).CopyTo(buffer);
        _offset += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) { lock (Output) Output.Write(buffer, offset, count); }

    public async Task<BrokerResponse?> Response()
    {
        if (Output.Length == 0) return null;
        Output.Position = 0;
        return BrokerResponse.Parse(await BrokerProtocol.ReadAsync(Output, CancellationToken.None));
    }
}

internal static class Kit
{
    public const string HostImage = @"C:\Program Files\OptimizationTool\OptimizationTool.Host.exe";
    public static readonly SecurityIdentifier User = new("S-1-5-21-1000-2000-3000-1001");
    public static readonly SecurityIdentifier OtherAdmin = new("S-1-5-21-1000-2000-3000-1002");
    public static readonly PeerInfo Self = new(200, @"C:\Program Files\OptimizationTool\OptimizationTool.Broker.exe", 1, User);
    public static readonly PeerInfo Host = new(100, HostImage, 1, User);

    public static string Nonce(char c = 'a') => new(c, 64);

    public static JsonObject TestTarget() => new() { ["type"] = "test", ["id"] = "value", ["label"] = "Test value" };

    public static BrokerStepRequest Step(int n, string actionId, string version, JsonObject target, JsonObject before, JsonObject after, JsonObject? parameters,
        ElevatedUndoOf? undoOf = null) =>
        BrokerStepRequest.From(new ElevatedStep(n, actionId, version, target, before, after, parameters, undoOf));

    public static BrokerStepRequest Firewall(int n = 1, string mode = "enable", ElevatedUndoOf? undoOf = null)
    {
        var before = mode == "enable" ? FirewallPlan.State(false, false) : FirewallPlan.State(true, true);
        var after = mode == "enable" ? FirewallPlan.State(true, true) : FirewallPlan.State(false, false);
        return Step(n, FirewallPlan.ActionId, FirewallPlan.HandlerVersion, FirewallPlan.Target(), before, after,
            mode == "enable" ? null : new JsonObject { ["mode"] = "disable" }, undoOf);
    }

    public static BrokerStepRequest Test(int n, string actionId, int from, int to) =>
        Step(n, actionId, "1.0.0", TestTarget(), new JsonObject { ["value"] = from }, new JsonObject { ["value"] = to }, new JsonObject { ["to"] = to });

    public static BrokerRequest Request(string? nonce = null, params BrokerStepRequest[] steps) =>
        new(nonce ?? Nonce(), "plan-0000000000000001", "planhash-1", steps.Length == 0 ? [Firewall()] : steps);
}

/// <summary>One broker with every fake wired; the registry is the firewall handler plus any test handlers.</summary>
internal sealed class Rig
{
    public readonly Log Log = new();
    public readonly FakeFirewall Fw;
    public readonly FakeJournal Journal;
    public readonly FakeLedger Ledger = new();
    public readonly FakeConfirm Confirm;
    public readonly List<IBrokerHandler> Handlers;

    public Rig(params string[] testActions)
    {
        Fw = new FakeFirewall(Log);
        Journal = new FakeJournal(Log);
        Confirm = new FakeConfirm(Log);
        Handlers = [new FirewallBrokerHandler(Fw, Fw), .. testActions.Select(a => new FakeBrokerHandler(a, Log))];
    }

    public FakeBrokerHandler Test(string actionId) => (FakeBrokerHandler)Handlers.Single(h => h.ActionId == actionId);

    public BrokerSession Session(string? nonce = null) =>
        new(new BrokerSessionOptions(nonce ?? Kit.Nonce(), Kit.HostImage, Kit.Self, Handlers, Journal, Ledger, Confirm));

    public async Task<(BrokerResult Result, BrokerResponse? Sent, Duplex Pipe)> Run(JsonObject frame, PeerInfo? server = null, string? nonce = null, Action<Duplex>? arrange = null)
    {
        var pipe = await Duplex.WithFrames(frame);
        arrange?.Invoke(pipe);
        var result = await Session(nonce).RunAsync(pipe, server ?? Kit.Host, CancellationToken.None);
        return (result, await pipe.Response(), pipe);
    }
}

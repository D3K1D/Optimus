using System.Security.Principal;
using System.Text.Json.Nodes;
using OptimizationTool.Broker;
using OptimizationTool.Windows.Actions.Firewall;

namespace OptimizationTool.Broker.Tests;

// Fakes only: no test touches this PC's firewall, the receipts folder, or elevation.

internal sealed class FakeFirewall : IPublicFirewallReader, IPublicFirewallWriter
{
    public PublicFirewallSnapshot State { get; set; } = Snap(local: false, effective: false);
    public bool Ignore { get; set; }
    public int Writes { get; private set; }

    public static PublicFirewallSnapshot Snap(bool? local, bool? effective) =>
        new(local, effective, PublicActive: true, PolicyConfigured: false, MdmConfigured: false, OtherFirewallOn: false,
            ConsoleSession: true, RemoteSession: false, Managed: false, Listening: [], InboundRules: []);

    public PublicFirewallSnapshot Read(CancellationToken ct) => State;

    public void SetPublicEnabled(bool enabled)
    {
        Writes++;
        if (!Ignore) State = State with { LocalEnabled = enabled, EffectiveEnabled = enabled };
    }
}

internal sealed class FakeReceipts : IReceiptStore
{
    public List<JsonObject> All { get; } = [];
    public string Write(JsonObject receipt)
    {
        var id = "rcpt-" + All.Count.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
        receipt["receiptId"] = id;
        All.Add(receipt);
        return id;
    }
    public JsonObject? LatestVerified(string field) =>
        All.LastOrDefault(r => (string?)r["field"] == field && (string?)r["state"] == "verified");
}

internal sealed class FakeLedger : INonceLedger
{
    public HashSet<string> Used { get; } = [];
    public bool WasUsed(string nonce) => Used.Contains(nonce);
    public void MarkUsed(string nonce) => Used.Add(nonce);
}

/// <summary>Reads from one stream and writes to another, standing in for the pipe.</summary>
internal sealed class Duplex(Stream input) : Stream
{
    public MemoryStream Output { get; } = new();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => Output.Write(buffer, offset, count);

    public static async Task<Duplex> WithFrames(params JsonObject[] frames)
    {
        var ms = new MemoryStream();
        foreach (var f in frames) await BrokerProtocol.WriteAsync(ms, f, CancellationToken.None);
        ms.Position = 0;
        return new Duplex(ms);
    }

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

    public static BrokerSession Session(FakeFirewall fw, FakeReceipts receipts, FakeLedger ledger, string? nonce = null) =>
        new(new BrokerSessionOptions(nonce ?? Nonce(), HostImage, Self, fw, fw, receipts, ledger));

    public static BrokerRequest Request(string mode = "enable", string? nonce = null, string actionId = FirewallPlan.ActionId)
    {
        var before = mode == "enable" ? FirewallPlan.State(false, false) : FirewallPlan.State(true, true);
        var after = mode == "enable" ? FirewallPlan.State(true, true) : FirewallPlan.State(false, false);
        var target = FirewallPlan.Target();
        return new BrokerRequest(nonce ?? Nonce(), actionId, mode, "plan-0000000000000001", "planhash-1",
            FirewallPlan.ContentHash(actionId, mode, target, before, after), target, before, after);
    }
}

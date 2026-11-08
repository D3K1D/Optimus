// One-shot elevated helper for machine-scope actions (contracts/m4-actions.md §5).
// Launched only by the host (BrokerElevation) through UAC with exactly: --pipe <name> --nonce <hex>.
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using OptimizationTool.Broker;
using OptimizationTool.Windows.Actions.Firewall;

// Load DLLs only from System32 (plus the app's own managed assemblies, which the runtime binds itself).
BrokerNative.SetDefaultDllDirectories(0x00000800 /* LOAD_LIBRARY_SEARCH_SYSTEM32 */);
BrokerNative.SetDllDirectoryW("");

if (BrokerArgs.Parse(args) is not { } parsed) return 2;

using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60));
try
{
    // Identification only: the host can see who we are but can't impersonate the elevated token.
    using var pipe = new NamedPipeClientStream(".", parsed.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
    await pipe.ConnectAsync(10_000, budget.Token);
    var self = PeerIdentity.Self();
    var server = PeerIdentity.Server(pipe);
    var store = new ProtectedReceiptStore(ProtectedReceiptStore.DefaultRoot, self.User!);
    var session = new BrokerSession(new BrokerSessionOptions(
        parsed.Nonce,
        Path.Combine(AppContext.BaseDirectory, "OptimizationTool.Host.exe"),
        self,
        new WindowsPublicFirewallReader(),
        new WindowsPublicFirewallWriter(),
        store,
        new FileNonceLedger(store, ProtectedReceiptStore.DefaultRoot)));
    var result = await session.RunAsync(pipe, server, budget.Token);
    return result.Wrote ? 0 : 1;
}
catch (Exception)
{
    return 3;
}

namespace OptimizationTool.Broker
{
    /// <summary>Strict argument parsing: exactly "--pipe NAME --nonce HEX", both well-formed.</summary>
    public sealed record BrokerArgs(string Pipe, string Nonce)
    {
        public static BrokerArgs? Parse(string[] args)
        {
            if (args.Length != 4 || args[0] != "--pipe" || args[2] != "--nonce") return null;
            if (!BrokerProtocol.IsValidPipeName(args[1]) || !BrokerProtocol.IsValidNonce(args[3])) return null;
            return new BrokerArgs(args[1], args[3]);
        }
    }

    internal static class BrokerNative
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetDefaultDllDirectories(uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetDllDirectoryW(string path);
    }
}

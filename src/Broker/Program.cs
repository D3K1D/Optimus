// One-shot elevated helper for administrator actions (contracts/m4-actions.md §5, m9-ai-powers.md §3.5).
// Launched only by the host (BrokerElevation) through UAC with exactly: --pipe <name> --nonce <hex>.
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using OptimizationTool.Broker;
using OptimizationTool.Windows.Actions.Broker;

// Load DLLs only from System32 (plus the app's own managed assemblies, which the runtime binds itself).
BrokerNative.SetDefaultDllDirectories(0x00000800 /* LOAD_LIBRARY_SEARCH_SYSTEM32 */);
BrokerNative.SetDllDirectoryW("");

if (BrokerArgs.Parse(args) is not { } parsed) return 2;

// UAC + the broker's own window (3 min) + up to 6 steps; a restore point can take several minutes. Once the steps are
// known the session sets the rest of the budget (a repair tool's own deadline, up to 90 minutes, plus margins).
using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(15));
try
{
    // Identification only: the host can see who we are but can't impersonate the elevated token.
    using var pipe = new NamedPipeClientStream(".", parsed.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
    await pipe.ConnectAsync(10_000, budget.Token);
    var self = PeerIdentity.Self();
    var server = PeerIdentity.Server(pipe);
    // m9 §13 R1: the journal and the script-runs root are trusted chains from %ProgramData%, held while the broker runs.
    var files = new WindowsTrustedFileSystem(self.User!);
    using var journal = new ProtectedJournal(TrustedChain.ProgramData, BrokerJournalFiles.DefaultRoot, self.User!, files);
    // m9 §17.2: administrator checks read through WMI only (no write, no method outside the allowlist).
    using var wmi = new OptimizationTool.Windows.Actions.Broker.Reads.WindowsReadWmi();
    var session = new BrokerSession(new BrokerSessionOptions(
        parsed.Nonce,
        Path.Combine(AppContext.BaseDirectory, "OptimizationTool.Host.exe"),
        self,
        BrokerRegistry.Create(),
        journal,
        new FileNonceLedger(journal),
        new BrokerConfirmWindow())
    {
        ExtendBudget = rest => { try { budget.CancelAfter(rest + TimeSpan.FromMinutes(1)); } catch (ObjectDisposedException) { } },
        // m9 §5.4: an elevated script, checked, shown and run by the broker itself (its own guard, policy, folder, job).
        Scripts = new BrokerScriptOptions(
            new OptimizationTool.Windows.Actions.Scripts.WindowsScriptRunner(BrokerRunFolders.Trusted(files, TrustedChain.ProgramData)),
            new OptimizationTool.Windows.Actions.Scripts.ScriptPolicyReader(),
            new OptimizationTool.Windows.Actions.Scripts.PowerShellParser(BrokerRunFolders.Root, BrokerRunFolders.Trusted(files, TrustedChain.ProgramData)),
            new BrokerScriptWindow(),
            journal,
            BrokerRunFolders.Root),
        // m9 §17.2: administrator checks, shown in the helper's own window and read by its compiled adapters.
        Reads = new BrokerReadOptions(OptimizationTool.Windows.Actions.Broker.Reads.BrokerReads.Create(wmi), new BrokerReadWindow(), journal),
    });
    var result = await session.RunAsync(pipe, server, budget.Token);
    // m9 §17.2: a check that didn't return is still running on its own thread; leave at once (no disposal of the WMI
    // connection it uses), which ends the stuck work with the process.
    if (result.Code == "timeout") Environment.Exit(1);
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

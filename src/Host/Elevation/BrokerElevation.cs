using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Firewall;

namespace OptimizationTool.Host.Elevation;

/// <summary>Result of asking Windows to start the broker elevated.</summary>
public sealed record BrokerLaunch(bool Started, bool Cancelled, uint ProcessId, string? Error = null);

/// <summary>Starts the broker (ShellExecute "runas" in production; a fake in tests).</summary>
public interface IBrokerLauncher
{
    BrokerLaunch Launch(string brokerPath, string arguments);
}

/// <summary>
/// Host side of the elevation boundary (contracts/m4-actions.md §5). For each approved machine-scope plan:
/// create a fresh first-instance pipe (ACL: this user + Administrators, NETWORK denied), launch the broker through
/// UAC with only <c>--pipe NAME --nonce HEX</c>, accept only the launched process in this session and account,
/// send the plan (with the content hash the broker must recompute from live state), and map its single answer.
/// UAC cancelled → declined/uac_cancelled. No answer in time → indeterminate (the engine reconciles).
/// </summary>
public sealed class BrokerElevation : IElevation
{
    private readonly string _brokerPath;
    private readonly IBrokerLauncher _launcher;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _responseTimeout;
    private readonly HashSet<string> _usedNonces = [];
    private readonly SemaphoreSlim _one = new(1, 1);

    public BrokerElevation(string? brokerPath = null, IBrokerLauncher? launcher = null, TimeSpan? connectTimeout = null, TimeSpan? responseTimeout = null)
    {
        _brokerPath = brokerPath ?? Path.Combine(AppContext.BaseDirectory, "OptimizationTool.Broker.exe");
        _launcher = launcher ?? new ShellExecuteLauncher();
        _connectTimeout = connectTimeout ?? TimeSpan.FromMinutes(3); // includes the time the user spends on the UAC prompt
        _responseTimeout = responseTimeout ?? TimeSpan.FromSeconds(45);
    }

    public async Task<ActionOutcome> RunAsync(string actionId, ActionExecution execution, CancellationToken ct)
    {
        var before = (JsonObject)execution.Before.DeepClone();
        if (actionId != FirewallPlan.ActionId)
            return new ActionOutcome("failed", before, "Nothing changed.", "action_not_allowed", "This change can't run with administrator rights.");
        var mode = FirewallPlan.Mode(execution.Parameters);
        if (mode is null || !FirewallPlan.ValidTarget(execution.Target))
            return new ActionOutcome("failed", before, "Nothing changed.", "bad_request", "This change isn't supported.");
        if (!File.Exists(_brokerPath))
            return new ActionOutcome("failed", before, "Nothing changed.", "broker_missing", "The helper that makes this change isn't installed.");

        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var pipeName = BrokerProtocol.NewPipeName();
            var nonce = BrokerProtocol.NewNonce();
            if (!_usedNonces.Add(nonce)) return Indeterminate(before, "nonce_reuse");
            var request = new BrokerRequest(nonce, actionId, mode, execution.PlanId, execution.PlanHash,
                FirewallPlan.ContentHash(actionId, mode, execution.Target, execution.Before, execution.After),
                (JsonObject)execution.Target.DeepClone(), (JsonObject)execution.Before.DeepClone(), (JsonObject)execution.After.DeepClone());
            return await RunOnceAsync(pipeName, nonce, request, before, ct).ConfigureAwait(false);
        }
        finally { _one.Release(); }
    }

    private async Task<ActionOutcome> RunOnceAsync(string pipeName, string nonce, BrokerRequest request, JsonObject before, CancellationToken ct)
    {
        var (selfUser, selfSession) = ClientIdentity.Current();
        // Created before launch, as the first instance: a squatter on the name makes this throw instead of us connecting to it.
        await using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, Security(selfUser));

        var launch = await Task.Run(() => _launcher.Launch(_brokerPath, $"--pipe {pipeName} --nonce {nonce}"), ct).ConfigureAwait(false);
        if (launch.Cancelled)
            return new ActionOutcome("declined", before, "You cancelled the Windows permission prompt. Nothing changed.", "uac_cancelled");
        if (!launch.Started)
            return new ActionOutcome("failed", before, "Nothing changed.", "broker_not_started", "Windows couldn't start the helper.");

        using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connect.CancelAfter(_connectTimeout);
        try { await pipe.WaitForConnectionAsync(connect.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Indeterminate(before, "broker_timeout"); }

        // Only the process we launched, in our session. A different account is refused here and by the broker.
        ClientInfo client;
        try { client = ClientIdentity.Inspect(pipe); }
        catch (Win32Exception) { return new ActionOutcome("failed", before, "Nothing changed.", "broker_mismatch", "The helper couldn't be verified."); }
        var wrongImage = client.ImagePath is { } image
            && !string.Equals(Path.GetFullPath(image), Path.GetFullPath(_brokerPath), StringComparison.OrdinalIgnoreCase);
        if ((launch.ProcessId != 0 && client.ProcessId != launch.ProcessId) || client.SessionId != selfSession || wrongImage)
            return new ActionOutcome("failed", before, "Nothing changed.", "broker_mismatch", "The helper couldn't be verified.");
        // A non-elevated host may not be able to read the elevated token; the broker (which can read ours) is the
        // authoritative check. When we can see a different account, refuse here too.
        if (client.User is not null && !client.User.Equals(selfUser))
            return new ActionOutcome("failed", before, "Nothing changed.", "different_admin",
                "Windows permission came from a different account. Use Windows Security instead.");

        using var answer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        answer.CancelAfter(_responseTimeout);
        BrokerResponse? response;
        try
        {
            await BrokerProtocol.WriteAsync(pipe, request.ToJson(), answer.Token).ConfigureAwait(false);
            response = BrokerResponse.Parse(await BrokerProtocol.ReadAsync(pipe, answer.Token).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException)
        {
            return Indeterminate(before, "broker_timeout");
        }
        if (response is null || !BrokerProtocol.NonceEquals(response.Nonce, nonce)) return Indeterminate(before, "bad_response");

        var observed = response.Observed;
        if (response.ReceiptId is { } id) observed["receiptId"] = id;
        return new ActionOutcome(response.State, observed, response.ReceiptText, response.Code, response.Message);
    }

    private static ActionOutcome Indeterminate(JsonObject before, string code) =>
        new("indeterminate", new JsonObject(), "The result is unknown. Checking the setting again.", code);

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
        try { return new BrokerLaunch(true, false, info.hProcess != IntPtr.Zero ? GetProcessId(info.hProcess) : 0); }
        finally { if (info.hProcess != IntPtr.Zero) CloseHandle(info.hProcess); }
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
    private static extern uint GetProcessId(IntPtr process);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

namespace OptimizationTool.Transport.Logging;

/// <summary>One named place in the code worth pointing to: id <c>E-&lt;AREA&gt;-&lt;nnn&gt;</c>, area and a one-line summary.</summary>
public sealed record ErrorSite(string Id, string Area, string Summary);

/// <summary>
/// The table of error-site ids. Every <c>"E-XXX-nnn"</c> literal under <c>src/</c> must be listed here (a test checks).
/// To add one: a constant and one line in <see cref="All"/>, in its area's group.
/// Areas: RES research, CHK check-up, PRV providers, ACT actions, FIX fix plans, SCR scripts, BEN bench, SCN scan,
/// HST host, APP desktop shell, MCP bridge, BRK broker, UNH unhandled exceptions.
/// </summary>
public static class ErrorSites
{
    // UNH: unhandled exceptions
    public const string HostUnhandled = "E-UNH-001";
    public const string DesktopUnhandled = "E-UNH-002";
    public const string McpUnhandled = "E-UNH-003";
    public const string UnobservedTask = "E-UNH-004";
    public const string WinFormsThread = "E-UNH-005";

    // HST: host
    public const string HostRpcInternal = "E-HST-001";
    public const string HostClientRefused = "E-HST-002";
    public const string HostPipeAccept = "E-HST-003";

    // APP: desktop shell
    public const string HostExitedUnexpectedly = "E-APP-001";
    public const string BackendLost = "E-APP-002";
    public const string BackendHelloFailed = "E-APP-003";
    public const string BackendUnavailable = "E-APP-004";
    public const string BackendNeedsUpdate = "E-APP-005";
    public const string BackendHostExitedEarly = "E-APP-006";
    public const string BackendUntrustedHost = "E-APP-007";
    public const string BackendLaunchFailed = "E-APP-008";

    // ACT: actions
    public const string ActionOpFailed = "E-ACT-001";
    public const string ActionExecuteFailed = "E-ACT-002";
    public const string ActionStraggler = "E-ACT-003";
    public const string ActionResolveFailed = "E-ACT-004";
    public const string ActionReconcile = "E-ACT-005";
    public const string ActionRecheck = "E-ACT-006";
    public const string ActionShutdown = "E-ACT-007";

    // FIX: fix plans
    public const string FixOpFailed = "E-FIX-001";
    public const string FixStepFailed = "E-FIX-002";
    public const string FixStepExecuteFailed = "E-FIX-003";
    public const string FixRunFailed = "E-FIX-004";
    public const string FixBrokerFailed = "E-FIX-005";
    public const string FixReconcile = "E-FIX-006";
    public const string FixUndoStepFailed = "E-FIX-007";

    // SCR: scripts
    public const string ScriptOpFailed = "E-SCR-001";
    public const string ScriptPolicyRead = "E-SCR-002";
    public const string ScriptParser = "E-SCR-003";
    public const string ScriptRunFailed = "E-SCR-004";
    public const string ScriptRestorePoint = "E-SCR-005";
    public const string ScriptTranscript = "E-SCR-006";

    // BEN: bench
    public const string BenchRunFailed = "E-BEN-001";
    public const string BenchPartFailed = "E-BEN-002";
    public const string BenchWorkerLaunch = "E-BEN-003";
    public const string BenchWorkerStderr = "E-BEN-004";
    public const string BenchWorkerExit = "E-BEN-005";
    public const string BenchSensors = "E-BEN-006";
    public const string BenchStop = "E-BEN-007";
    public const string BenchFolder = "E-BEN-008";

    // BRK: broker (logged by the host; the broker writes no log)
    public const string BrokerUacCancelled = "E-BRK-001";
    public const string BrokerNotConnected = "E-BRK-002";
    public const string BrokerRefused = "E-BRK-003";
    public const string BrokerNoAnswer = "E-BRK-004";
    public const string BrokerExitError = "E-BRK-005";

    // SCN: scan
    public const string ScanFailed = "E-SCN-001";
    public const string ScanCollectorFailed = "E-SCN-002";
    public const string ScanCollectorGap = "E-SCN-003";
    public const string ScanStop = "E-SCN-004";

    // MCP: bridge
    public const string McpToolFailed = "E-MCP-001";
    public const string McpHelloFailed = "E-MCP-002";
    public const string McpConnectFailed = "E-MCP-003";
    public const string McpPairingFailed = "E-MCP-004";

    // PRV: providers
    public const string ProviderTimeout = "E-PRV-001";
    public const string ProviderNetwork = "E-PRV-002";
    public const string ProviderHttpStatus = "E-PRV-003";
    public const string ProviderIncomplete = "E-PRV-004";
    public const string ProviderStreamError = "E-PRV-005";
    public const string CliLaunchFailed = "E-PRV-006";
    public const string CliExitNonZero = "E-PRV-007";
    public const string CliRunFailed = "E-PRV-008";

    // RES: research (labels phase and chat web_lookup)
    public const string LookupRefused = "E-RES-001";
    public const string LookupProviderUnsupported = "E-RES-002";
    public const string LookupWebOff = "E-RES-003";
    public const string LookupNotSignedIn = "E-RES-004";
    public const string LookupUnsupported = "E-RES-005";
    public const string LookupTimeout = "E-RES-006";
    public const string LookupProviderError = "E-RES-007";
    public const string LookupCancelled = "E-RES-008";
    public const string LookupFenceTaskInactive = "E-RES-009";
    public const string LookupFenceWebOff = "E-RES-010";
    public const string LookupFenceChanged = "E-RES-011";
    public const string LookupNothingStored = "E-RES-012";
    public const string ResearchNotStarted = "E-RES-013";
    public const string ResearchNotSignedIn = "E-RES-014";
    public const string ResearchUnsupported = "E-RES-015";
    public const string ResearchCallFailed = "E-RES-016";
    public const string ResearchFenced = "E-RES-017";
    public const string ResearchRunFailed = "E-RES-018";
    public const string ResearchNoSeam = "E-RES-019";
    public const string ResearchAnswerDropped = "E-RES-020";
    public const string ResearchNoObservedSource = "E-RES-021";
    public const string ResearchSubjectsSkipped = "E-RES-022";
    public const string UrlRefused = "E-RES-023";
    public const string SourceOpenBlocked = "E-RES-024";

    // CHK: check-up
    public const string CheckupNotStarted = "E-CHK-001";
    public const string CheckupNotSignedIn = "E-CHK-002";
    public const string CheckupNoSeam = "E-CHK-003";
    public const string CheckupUnsupported = "E-CHK-004";
    public const string CheckupCallFailed = "E-CHK-005";
    public const string CheckupFenced = "E-CHK-006";
    public const string CheckupRunFailed = "E-CHK-007";
    public const string CheckupKillAboveSearches = "E-CHK-008";
    public const string CheckupAnswerUnusable = "E-CHK-009";
    public const string CheckupResultsDropped = "E-CHK-010";
    public const string CheckupIdentityFiltered = "E-CHK-011";
    public const string CheckupBudget = "E-CHK-012";

    public static IReadOnlyDictionary<string, ErrorSite> All { get; } = new[]
    {
        // UNH
        new ErrorSite(HostUnhandled, "UNH", "Host: unhandled exception"),
        new ErrorSite(DesktopUnhandled, "UNH", "Desktop shell: unhandled exception"),
        new ErrorSite(McpUnhandled, "UNH", "MCP bridge: unhandled exception"),
        new ErrorSite(UnobservedTask, "UNH", "Unobserved task exception"),
        new ErrorSite(WinFormsThread, "UNH", "Desktop shell: exception on the UI thread"),

        // HST
        new ErrorSite(HostRpcInternal, "HST", "Host: a JSON-RPC method failed with an internal error"),
        new ErrorSite(HostClientRefused, "HST", "Host: a client was refused (other user or session, untrusted image, dev option off)"),
        new ErrorSite(HostPipeAccept, "HST", "Host: accepting a pipe client or creating the next pipe instance failed"),

        // APP
        new ErrorSite(HostExitedUnexpectedly, "APP", "Desktop shell: the host process exited unexpectedly"),
        new ErrorSite(BackendLost, "APP", "Desktop shell: the connection to the host was lost"),
        new ErrorSite(BackendHelloFailed, "APP", "Desktop shell: session.hello failed, timed out or was refused"),
        new ErrorSite(BackendUnavailable, "APP", "Desktop shell: the host pipe is not available"),
        new ErrorSite(BackendNeedsUpdate, "APP", "Desktop shell: the host needs a newer Optimus"),
        new ErrorSite(BackendHostExitedEarly, "APP", "Desktop shell: a launched host exited before it connected"),
        new ErrorSite(BackendUntrustedHost, "APP", "Desktop shell: the pipe server is not the installed host"),
        new ErrorSite(BackendLaunchFailed, "APP", "Desktop shell: the host could not be launched"),

        // ACT
        new ErrorSite(ActionOpFailed, "ACT", "Action: the operation ended failed or indeterminate"),
        new ErrorSite(ActionExecuteFailed, "ACT", "Action: the handler threw while executing"),
        new ErrorSite(ActionStraggler, "ACT", "Action: still running after its deadline (locks held)"),
        new ErrorSite(ActionResolveFailed, "ACT", "Action: resolving the target failed or gave an invalid resolution"),
        new ErrorSite(ActionReconcile, "ACT", "Action: restart reconcile failed"),
        new ErrorSite(ActionRecheck, "ACT", "Action: a manual recheck failed"),
        new ErrorSite(ActionShutdown, "ACT", "Action: stopping actions at shutdown failed"),

        // FIX
        new ErrorSite(FixOpFailed, "FIX", "Fix plan: the operation ended failed, partial or indeterminate"),
        new ErrorSite(FixStepFailed, "FIX", "Fix plan: a step ended failed or indeterminate"),
        new ErrorSite(FixStepExecuteFailed, "FIX", "Fix plan: a step's handler threw"),
        new ErrorSite(FixRunFailed, "FIX", "Fix plan: the run or its finish failed"),
        new ErrorSite(FixBrokerFailed, "FIX", "Fix plan: the broker session failed"),
        new ErrorSite(FixReconcile, "FIX", "Fix plan: restart reconcile of a step failed"),
        new ErrorSite(FixUndoStepFailed, "FIX", "Fix plan: preparing the undo of a step failed"),

        // SCR
        new ErrorSite(ScriptOpFailed, "SCR", "Script: the run ended failed or indeterminate"),
        new ErrorSite(ScriptPolicyRead, "SCR", "Script: the execution policy could not be read"),
        new ErrorSite(ScriptParser, "SCR", "Script: the PowerShell parser is unavailable"),
        new ErrorSite(ScriptRunFailed, "SCR", "Script: the runner, a transition or the finish failed"),
        new ErrorSite(ScriptRestorePoint, "SCR", "Script: the restore point failed"),
        new ErrorSite(ScriptTranscript, "SCR", "Script: the transcript could not be stored or read"),

        // BEN
        new ErrorSite(BenchRunFailed, "BEN", "Bench: the run failed or could not be finished"),
        new ErrorSite(BenchPartFailed, "BEN", "Bench: a part failed (worker error, deadline, heartbeat, invalid result)"),
        new ErrorSite(BenchWorkerLaunch, "BEN", "Bench: the worker could not be launched or sent no hello"),
        new ErrorSite(BenchWorkerStderr, "BEN", "Bench: the worker wrote a diagnostic code to stderr"),
        new ErrorSite(BenchWorkerExit, "BEN", "Bench: the worker exited with a non-zero code"),
        new ErrorSite(BenchSensors, "BEN", "Bench: sensors, power or hardware could not be read"),
        new ErrorSite(BenchStop, "BEN", "Bench: stopping a run failed"),
        new ErrorSite(BenchFolder, "BEN", "Bench: the disk test folder was refused"),

        // BRK
        new ErrorSite(BrokerUacCancelled, "BRK", "Broker: the user cancelled the UAC prompt"),
        new ErrorSite(BrokerNotConnected, "BRK", "Broker: not started, timed out, or failed verification (mismatch, different admin)"),
        new ErrorSite(BrokerRefused, "BRK", "Broker: the request was refused before launch (bad request, action not allowed, missing)"),
        new ErrorSite(BrokerNoAnswer, "BRK", "Broker: no or a bad answer after the request was sent (result unknown)"),
        new ErrorSite(BrokerExitError, "BRK", "Broker: exited with 2 (bad arguments), 3 (exception) or an unknown code"),

        // SCN
        new ErrorSite(ScanFailed, "SCN", "Scan: the run failed"),
        new ErrorSite(ScanCollectorFailed, "SCN", "Scan: a collector threw"),
        new ErrorSite(ScanCollectorGap, "SCN", "Scan: a collector timed out or was denied"),
        new ErrorSite(ScanStop, "SCN", "Scan: stopping scans at shutdown failed"),

        // MCP
        new ErrorSite(McpToolFailed, "MCP", "MCP bridge: a tool call returned an error"),
        new ErrorSite(McpHelloFailed, "MCP", "MCP bridge: session.hello failed"),
        new ErrorSite(McpConnectFailed, "MCP", "MCP bridge: the host pipe could not be reached"),
        new ErrorSite(McpPairingFailed, "MCP", "MCP bridge: pairing could not be started"),

        // PRV
        new ErrorSite(ProviderTimeout, "PRV", "Provider: the call timed out"),
        new ErrorSite(ProviderNetwork, "PRV", "Provider: the provider could not be reached"),
        new ErrorSite(ProviderHttpStatus, "PRV", "Provider: an HTTP error status"),
        new ErrorSite(ProviderIncomplete, "PRV", "Provider: the answer was cut off"),
        new ErrorSite(ProviderStreamError, "PRV", "Provider: the streamed answer failed (error event, budget, no terminal event)"),
        new ErrorSite(CliLaunchFailed, "PRV", "Headless CLI: could not be started"),
        new ErrorSite(CliExitNonZero, "PRV", "Headless CLI: exited with a non-zero code"),
        new ErrorSite(CliRunFailed, "PRV", "Headless CLI: the run failed (read loop, broken pipe)"),

        // RES
        new ErrorSite(LookupRefused, "RES", "web_lookup: refused before the call (limit, out of scope, not researchable, no subject name, release)"),
        new ErrorSite(LookupProviderUnsupported, "RES", "web_lookup: this AI has no research seam"),
        new ErrorSite(LookupWebOff, "RES", "web_lookup: web search is \"no\" for this AI and model (or the CLI is not ready)"),
        new ErrorSite(LookupNotSignedIn, "RES", "web_lookup: the AI is not signed in"),
        new ErrorSite(LookupUnsupported, "RES", "web_lookup: the provider refused web search"),
        new ErrorSite(LookupTimeout, "RES", "web_lookup: the call timed out"),
        new ErrorSite(LookupProviderError, "RES", "web_lookup: the provider returned an error"),
        new ErrorSite(LookupCancelled, "RES", "web_lookup: the asking run was cancelled"),
        new ErrorSite(LookupFenceTaskInactive, "RES", "web_lookup fence: the task is no longer active"),
        new ErrorSite(LookupFenceWebOff, "RES", "web_lookup fence: online lookups were turned off"),
        new ErrorSite(LookupFenceChanged, "RES", "web_lookup fence: the model or account changed during the call"),
        new ErrorSite(LookupNothingStored, "RES", "web_lookup: the answer gave no usable label"),
        new ErrorSite(ResearchNotStarted, "RES", "Research: a run did not start (web off, no provider, web \"no\")"),
        new ErrorSite(ResearchNotSignedIn, "RES", "Research: the AI is not signed in"),
        new ErrorSite(ResearchUnsupported, "RES", "Research: the provider refused web search"),
        new ErrorSite(ResearchCallFailed, "RES", "Research: a call failed (provider error, timeout, run budget)"),
        new ErrorSite(ResearchFenced, "RES", "Research fence: the run was cancelled (permissions, provider, model, account or report changed)"),
        new ErrorSite(ResearchRunFailed, "RES", "Research: the run failed with an exception"),
        new ErrorSite(ResearchNoSeam, "RES", "Research: no research seam for this AI"),
        new ErrorSite(ResearchAnswerDropped, "RES", "Research: the answer could not be parsed"),
        new ErrorSite(ResearchNoObservedSource, "RES", "Research: no listed source was observed by the provider"),
        new ErrorSite(ResearchSubjectsSkipped, "RES", "Research: findings skipped (no name that may leave this PC)"),
        new ErrorSite(UrlRefused, "RES", "URL hygiene refused a source URL"),
        new ErrorSite(SourceOpenBlocked, "RES", "research.source: the stored URL failed hygiene"),

        // CHK
        new ErrorSite(CheckupNotStarted, "CHK", "Check-up: not started (refused or web \"no\")"),
        new ErrorSite(CheckupNotSignedIn, "CHK", "Check-up: the AI is not signed in"),
        new ErrorSite(CheckupNoSeam, "CHK", "Check-up: no research seam for this AI"),
        new ErrorSite(CheckupUnsupported, "CHK", "Check-up: the provider refused web search"),
        new ErrorSite(CheckupCallFailed, "CHK", "Check-up: a call failed (provider error, timeout)"),
        new ErrorSite(CheckupFenced, "CHK", "Check-up fence: cancelled (permissions, provider, model, account or report changed)"),
        new ErrorSite(CheckupRunFailed, "CHK", "Check-up: the run failed with an exception"),
        new ErrorSite(CheckupKillAboveSearches, "CHK", "Check-up: the answer ran more searches than allowed"),
        new ErrorSite(CheckupAnswerUnusable, "CHK", "Check-up: the answer could not be parsed or was too big"),
        new ErrorSite(CheckupResultsDropped, "CHK", "Check-up: results dropped by the host rules (not sent, URL not observed, shape, cap)"),
        new ErrorSite(CheckupIdentityFiltered, "CHK", "Check-up: summary names left out by the identity filter"),
        new ErrorSite(CheckupBudget, "CHK", "Check-up: stopped by the run budget or the call cap"),
    }.ToDictionary(s => s.Id, StringComparer.Ordinal);
}

using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Processes;

namespace OptimizationTool.Windows.Actions.Shell;

/// <summary>
/// explorer.restart's shell seam (the process reads and the end go through <see cref="IProcessControl"/>). Tests use a
/// fake; no test may call the real <see cref="StartExplorer"/> or end a real process.
/// </summary>
internal interface IShellControl
{
    /// <summary>This is a remote (Remote Desktop) session.</summary>
    bool RemoteSession { get; }
    /// <summary>The PID of the process that owns this session's shell window (GetShellWindow), or null when there is none.</summary>
    uint? ShellPid();
    /// <summary>Starts explorer.exe from the Windows directory as the current user. True when it started.</summary>
    bool StartExplorer();
}

/// <summary>Real implementation (user32, the Windows directory). Written, never run by the tests.</summary>
internal sealed partial class WindowsShellControl : IShellControl
{
    private const int SmRemoteSession = 0x1000;

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    private static partial nint GetShellWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    public bool RemoteSession => GetSystemMetrics(SmRemoteSession) != 0;

    public uint? ShellPid()
    {
        var hwnd = GetShellWindow();
        if (hwnd == 0) return null;
        return GetWindowThreadProcessId(hwnd, out var pid) != 0 && pid != 0 ? pid : null;
    }

    public bool StartExplorer()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var path = Path.Combine(windows, "explorer.exe");
        if (windows.Length == 0 || !File.Exists(path)) return false;
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = windows });
        return p is not null;
    }
}

/// <summary>
/// explorer.restart (contracts/m9-ai-powers.md §3.2): restarts the Windows shell of this user and session. It ends the
/// session's shell process (explorer.exe, rechecked on the handle that ends it), waits a few seconds for Windows to start
/// it again by itself, and otherwise starts explorer.exe from the Windows directory. Open folder windows close
/// (data_loss). Nothing to restore: kind "provider", no undo. Report-only like the repairs and network.flush_dns (design
/// v59): the operation and plan end "completed", never "verified", with result "restarted" (Windows brought it back),
/// "started" (Optimus started it) or "not_running" (no new shell came back; the receipt says how to start it), and the
/// finding's card is not closed. A shell that could not be ended ends "failed" with result "failed". Refused in a
/// remote session.
/// </summary>
internal sealed class ExplorerRestartHandler(IShellControl shell, IProcessControl processes, TimeSpan? wait = null, TimeSpan? poll = null) : IActionHandler
{
    public const string Id = "explorer.restart";
    internal const string Resource = "shell:explorer";
    private readonly TimeSpan _wait = wait ?? TimeSpan.FromSeconds(5);
    private readonly TimeSpan _poll = poll ?? TimeSpan.FromMilliseconds(250);

    public string ActionId => Id;
    public string Version => "1.1.0";
    public string Label => "Restart Windows Explorer";
    public string Privilege => "none";
    public bool SupportsUndo => false;
    public string Kind => "provider";
    public string Group => "apps";
    public string What => "Restarts the Windows shell (taskbar, Start and folder windows) for you; open folder windows close.";
    public IReadOnlyList<string> Flags => [ActionFlags.DataLoss];
    public JsonObject? ParameterSchema => null;
    public bool TakesLease => false;
    /// <summary>What Windows reported after the restart (a new shell, or none); not a state Optimus verifies.</summary>
    public bool ReportsOnly => true;

    internal static JsonObject Target() => new() { ["type"] = "shell", ["id"] = "explorer", ["label"] = "Windows Explorer" };

    public Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct) => Task.Run(() => Resolve(request), ct);

    public Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct) => Task.Run(() => new JsonObject { ["running"] = Shell() is not null }, ct);

    public Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct) => ExecuteCoreAsync(progress, ct);

    public ActionRequest? UndoRequest(JsonObject target, JsonObject before) => null;

    /// <summary>The session's shell if it is this user's own explorer.exe in this session; null when there is none.</summary>
    private ProcessInfo? Shell()
    {
        if (shell.ShellPid() is not { } pid || processes.Query(pid) is not { } p) return null;
        return p;
    }

    private static bool IsExplorer(ProcessInfo p) => string.Equals(FileName(p.ImagePath), "explorer.exe", StringComparison.OrdinalIgnoreCase);

    internal ActionResolution Resolve(ActionRequest request)
    {
        if (request.ActionId != Id) return ActionResolution.Ineligible("wrong_action", "This request is for a different action.");
        if (request.Parameters is { Count: > 0 }) return ActionResolution.Ineligible("invalid_arguments", "This change takes no parameters.");
        if (request.Target is { Count: > 0 } t && !JsonNode.DeepEquals(t, Target())) return ActionResolution.Ineligible("invalid_arguments", "This change takes no target.");
        if (request.Undo) return ActionResolution.Ineligible("not_supported", "A restart can't be undone.");

        ProcessInfo? live;
        bool remote;
        try
        {
            remote = shell.RemoteSession;
            live = Shell();
        }
        catch (Exception) { return ActionResolution.Ineligible("read_failed", "Windows didn't report its shell."); }
        if (remote) return ActionResolution.Ineligible("remote_session", "This needs someone at the PC, not a remote session.");
        if (live is null) return ActionResolution.Ineligible("not_found", "Windows Explorer isn't running as the shell. Start it from Task Manager › Run new task.");
        if (!IsExplorer(live)) return ActionResolution.Ineligible("unsupported", "This PC uses another program as its shell; Optimus leaves it alone.");
        if (live.SessionId != processes.CurrentSession) return ActionResolution.Ineligible("other_session", "The shell runs in another session.");
        if (live.UserSid is null || processes.CurrentUserSid is not { } me || !string.Equals(live.UserSid, me, StringComparison.OrdinalIgnoreCase))
            return ActionResolution.Ineligible("other_user", "The shell runs under another account.");
        if (live.Elevated != false) return ActionResolution.Ineligible("elevated", "The shell runs as administrator; restart it from Task Manager.");

        return new ActionResolution(
            Eligible: true, Target: Target(),
            Before: new JsonObject { ["running"] = true }, After: new JsonObject { ["running"] = true, ["restarted"] = true },
            Rows: [new PlanRow("Windows Explorer", "Running", "Restarted")],
            Context: [new("Applies to", "You, this session")],
            Tradeoffs: ["Open folder windows close; files copying in them may stop.", "The taskbar and desktop disappear for a few seconds."],
            UndoConditions: null,
            Resources: [Resource]);
    }

    internal async Task<ActionOutcome> ExecuteCoreAsync(IProgress<string>? progress, CancellationToken ct)
    {
        ProcessInfo? old;
        try { old = Shell(); }
        catch (Exception) { old = null; }
        if (old is null || !IsExplorer(old))
            return new ActionOutcome("failed", new JsonObject { ["result"] = "failed" }, "Nothing was restarted: Windows Explorer isn't running as the shell.", "not_found");

        progress?.Report("Restarting Windows Explorer");
        TerminateResult ended;
        try { ended = processes.Terminate(old.Pid, old.CreateTime, old.ImagePath, _wait); }
        catch (Exception e) { ended = new TerminateResult("error", e.HResult); }
        if (ended.Status is not ("ended" or "gone"))
            return new ActionOutcome("failed", new JsonObject { ["result"] = "failed" }, "Windows Explorer wasn't restarted; nothing changed.",
                ended.Status == "denied" ? "access_denied" : "not_applied", ended.Win32Error == 0 ? null : $"Windows error {ended.Win32Error}");

        // Windows normally starts the shell again by itself; give it a few seconds before starting it ourselves.
        if (await WaitForNewShellAsync(old, ct).ConfigureAwait(false))
            return new ActionOutcome("completed", new JsonObject { ["result"] = "restarted" }, "Windows Explorer restarted and is running again. Nothing else was changed.");
        bool started;
        try { started = shell.StartExplorer(); }
        catch (Exception) { started = false; }
        if (started && await WaitForNewShellAsync(old, ct).ConfigureAwait(false))
            return new ActionOutcome("completed", new JsonObject { ["result"] = "started" }, "Windows Explorer restarted and is running again. Nothing else was changed.");
        // Design v59: a completed result (the old shell ended and none came back), never indeterminate.
        return new ActionOutcome("completed", new JsonObject { ["result"] = "not_running" },
            "Windows Explorer didn't come back. Press Ctrl+Shift+Esc, choose Run new task, and type explorer.");
    }

    private async Task<bool> WaitForNewShellAsync(ProcessInfo old, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + _wait;
        while (true)
        {
            ProcessInfo? now;
            try { now = Shell(); }
            catch (Exception) { now = null; }
            if (now is not null && IsExplorer(now) && (now.Pid != old.Pid || now.CreateTime != old.CreateTime)) return true;
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(_poll, ct).ConfigureAwait(false);
        }
    }

    private static string FileName(string path)
    {
        var i = path.LastIndexOfAny(['\\', '/']);
        return i >= 0 ? path[(i + 1)..] : path;
    }
}

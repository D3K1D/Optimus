using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Windows.Actions.Broker;
using OptimizationTool.Windows.Actions.Protection;
using OptimizationTool.Windows.Actions.Scripts;
using ProtectionResult = OptimizationTool.Windows.Actions.Protection.Protection;

namespace OptimizationTool.Windows.Actions.Repair;

/// <summary>
/// The repair tools' seam (m9 §3.2 repair.sfc, repair.dism_restorehealth): find a Microsoft tool in the system directory
/// (taken from the Windows API, never PATH or an environment variable; no reparse point on its path; Microsoft-signed as
/// MpCmdRun is), and run it with fixed arguments, no shell and no window, line by line. Cancelling kills the tool and its
/// children, then throws. Tests use a fake; the real one (<see cref="WindowsRepairTool"/>) has never been run.
/// </summary>
public interface IRepairTool
{
    /// <summary>The verified path of <paramref name="fileName"/> in the system directory, or (null, not_found | untrusted).</summary>
    (string? Path, string? Problem) Locate(string fileName);
    Task<int> RunAsync(string path, IReadOnlyList<string> arguments, Action<string> onLine, CancellationToken ct);
}

/// <summary>The two repairs: tool, fixed arguments, own deadline, flags and the words the plan and result use.</summary>
public sealed record RepairKind(string ActionId, string Tool, IReadOnlyList<string> Arguments, TimeSpan Deadline, string Label, string What, IReadOnlyList<string> Flags,
    string Verb, string TargetId)
{
    public static readonly RepairKind Sfc = new("repair.sfc", "sfc.exe", ["/scannow"], TimeSpan.FromMinutes(60), "Check and repair Windows files",
        "Runs Windows' System File Checker (sfc /scannow), which checks protected Windows files and repairs the ones it can; it takes a while and may need a restart.",
        [ActionFlags.Restart, ActionFlags.Long], "Checking Windows files", "sfc");

    public static readonly RepairKind Dism = new("repair.dism_restorehealth", "dism.exe", ["/Online", "/Cleanup-Image", "/RestoreHealth"], TimeSpan.FromMinutes(90),
        "Repair the Windows component store",
        "Runs DISM /RestoreHealth, which repairs Windows' own store of system components, downloading clean copies from Windows Update when needed; it takes a while and may need a restart.",
        [ActionFlags.Restart, ActionFlags.Network, ActionFlags.Long], "Repairing the Windows image", "dism");

    public static readonly IReadOnlyList<RepairKind> All = [Sfc, Dism];
}

/// <summary>Plan and result code shared by the host handler (preview) and the broker handler (the run).</summary>
public static class RepairPlan
{
    public const string HandlerVersion = "1.1.0";

    public static JsonObject Target(RepairKind k) => new() { ["type"] = "repair", ["id"] = k.TargetId, ["label"] = "Windows" };

    public static bool ValidTarget(RepairKind k, JsonObject? t) =>
        t is null || t.Count == 0 || t["id"] is JsonValue v && v.TryGetValue<string>(out var id) && id == k.TargetId && t.All(kv => kv.Key is "type" or "id" or "label");

    public static ActionResolution Resolve(RepairKind k, IRepairTool tool, JsonObject? target, JsonObject? parameters)
    {
        if (parameters is { Count: > 0 } || !ValidTarget(k, target)) return ActionResolution.Ineligible("invalid_arguments", "This change takes no parameters.");
        var (path, problem) = tool.Locate(k.Tool);
        if (path is null)
            return ActionResolution.Ineligible(problem == "untrusted" ? "tool_untrusted" : "tool_missing", "Windows' repair tool could not be verified, so Optimus won't run it.");
        var minutes = (int)k.Deadline.TotalMinutes;
        var context = new List<KeyValuePair<string, string>>
        {
            new("Time", $"Often 10 to 30 minutes; stopped after {minutes} minutes"), new("Undo", "Not possible: Windows replaces damaged files itself"),
        };
        if (k.Flags.Contains(ActionFlags.Network)) context.Add(new("Download", "May fetch clean files from Windows Update"));
        return new ActionResolution(true, Target(k), new JsonObject(), new JsonObject { ["operation"] = k.TargetId },
            [new PlanRow(k == RepairKind.Sfc ? "Windows files" : "Component store", "Not checked now", "Checked, repaired where possible")], context,
            ["The PC may feel slower while it runs.", "A restart may be needed to finish repairs.", "Stop ends the tool; repairs it already made stay."],
            null, Resources: ["repair:" + k.TargetId]);
    }

    private static readonly Regex Percent = new(@"(?<!\d)(\d{1,3})(?:[.,]\d+)?\s*%", RegexOptions.CultureInvariant);

    /// <summary>A whole percentage in one output line (sfc "Verification 45% complete.", DISM "[==== 45.0% ====]"), or null.</summary>
    public static int? PercentOf(string line)
    {
        var m = Percent.Match(line.Replace("\0", "", StringComparison.Ordinal));
        return m.Success && int.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var p) && p is >= 0 and <= 100 ? p : null;
    }

    /// <summary>
    /// The tool's exit code (and the English result lines when present) → completed with a plain result, or failed. Never
    /// verified: the tool's own report is what it says, not something Optimus read back.
    /// sfc: no_violations | repaired | could_not_repair | finished; DISM: healthy | repaired | finished; else failed.
    /// </summary>
    public static ActionOutcome Outcome(RepairKind k, int exitCode, IReadOnlyList<string> tail)
    {
        var text = string.Join('\n', tail).Replace("\0", "", StringComparison.Ordinal).ToLowerInvariant();
        string result;
        if (k == RepairKind.Sfc)
            result = text.Contains("could not perform the requested operation") ? "failed"
                : text.Contains("did not find any integrity violations") ? "no_violations"
                : text.Contains("successfully repaired") ? "repaired"
                : text.Contains("unable to fix") ? "could_not_repair"
                : exitCode == 0 ? "finished" : "failed";
        else
            result = exitCode != 0 ? "failed"
                : text.Contains("no component store corruption detected") ? "healthy"
                : text.Contains("restore operation completed successfully") ? "repaired"
                : "finished";
        var observed = new JsonObject { ["result"] = result, ["exitCode"] = exitCode };
        return result switch
        {
            "no_violations" => new ActionOutcome("completed", observed, "System File Checker found no damaged Windows files."),
            "repaired" when k == RepairKind.Sfc => new ActionOutcome("completed", observed, "System File Checker found damaged Windows files and repaired them. Restart to finish."),
            "could_not_repair" => new ActionOutcome("completed", observed, "System File Checker found damaged Windows files it could not repair. Try the component store repair, then run it again."),
            "healthy" => new ActionOutcome("completed", observed, "DISM found no damage in the component store."),
            "repaired" => new ActionOutcome("completed", observed, "DISM repaired the component store. Restart, then run the Windows file check."),
            "finished" => new ActionOutcome("completed", observed, $"{(k == RepairKind.Sfc ? "System File Checker" : "DISM")} finished (exit code {exitCode}). Windows' log has the details."),
            _ => new ActionOutcome("failed", observed, $"{(k == RepairKind.Sfc ? "System File Checker" : "DISM")} could not finish (exit code {exitCode}).", "tool_failed",
                "The repair tool reported a failure."),
        };
    }
}

/// <summary>
/// repair.sfc · repair.dism_restorehealth in the host (m9 §3.2): administrator, provider kind (long, with progress), single
/// action only (never a fix step: fix steps are setting kind). The host only previews; the run is in the broker.
/// </summary>
public sealed class RepairHandler(RepairKind kind, IRepairTool tool) : IActionHandler
{
    public string ActionId => kind.ActionId;
    public string Version => RepairPlan.HandlerVersion;
    public string Label => kind.Label;
    public string Privilege => "admin";
    public bool SupportsUndo => false;
    public bool NothingToUndo => true;
    public string Kind => "provider";
    public string Group => "repair";
    public string What => kind.What;
    public IReadOnlyList<string> Flags => kind.Flags;
    public JsonObject? ParameterSchema => null;
    public bool TakesLease => true;
    public TimeSpan? LongDeadline => kind.Deadline;
    /// <summary>m9 §3.8: a repair tool reports what it did; Optimus never verifies it, and the finding stays open.</summary>
    public bool ReportsOnly => true;

    public Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct) =>
        Task.Run(() => RepairPlan.Resolve(kind, tool, request.Target, request.Parameters), ct);

    public Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct) => Task.FromResult(new JsonObject());

    public Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct) =>
        Task.FromResult(new ActionOutcome("failed", new JsonObject(), "Nothing ran.", "admin_only", "This runs through the administrator helper."));

    public ActionRequest? UndoRequest(JsonObject target, JsonObject before) => null;
}

/// <summary>
/// A repair in the broker: the tool located and verified again by the broker, the fixed arguments only, progress lines
/// (whole percentages, at most one per change) sent to the host, killed on Stop and at its own deadline (sfc 60 min, DISM
/// 90 min). Outcome completed or failed, never verified, never undone.
/// </summary>
public sealed class RepairBrokerHandler(RepairKind kind, IRepairTool tool, TimeProvider? time = null) : IBrokerHandler
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string ActionId => kind.ActionId;
    public string Version => RepairPlan.HandlerVersion;
    public string Label => kind.Label;
    public IReadOnlyList<string> Flags => kind.Flags;
    public TimeSpan? Deadline => kind.Deadline;

    /// <summary>The deadline actually applied (tests shorten it).</summary>
    internal TimeSpan RunDeadline { get; init; } = kind.Deadline;

    public bool IsUndo(BrokerStepRequest step) => false;

    public ActionResolution Resolve(BrokerStepRequest step) => RepairPlan.Resolve(kind, tool, step.Target, step.Parameters);

    public ProtectionResult? Protect(ActionResolution resolution) => null;

    public ActionOutcome WriteAndReadBack(ActionResolution r, BrokerStepRequest step, CancellationToken ct) =>
        Run(r, step, new BrokerRun("broker", new Progress<string>(_ => { }), ct));

    public ActionOutcome Run(ActionResolution r, BrokerStepRequest step, BrokerRun run)
    {
        var (path, problem) = tool.Locate(kind.Tool);
        if (path is null)
            return new ActionOutcome("failed", new JsonObject(), "Nothing ran.", problem == "untrusted" ? "tool_untrusted" : "tool_missing", "Windows' repair tool could not be verified.");
        using var deadline = new CancellationTokenSource(RunDeadline, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, run.Cancel);
        var tail = new Queue<string>();
        int? last = null;
        run.Progress.Report(kind.Verb);
        void OnLine(string line)
        {
            var clean = line.Replace("\0", "", StringComparison.Ordinal);
            lock (tail)
            {
                tail.Enqueue(clean.Length > 300 ? clean[..300] : clean);
                while (tail.Count > 60) tail.Dequeue();
            }
            if (RepairPlan.PercentOf(clean) is { } p && p != last)
            {
                last = p;
                run.Progress.Report($"{kind.Verb} · {p}%");
            }
        }
        int exit;
        try { exit = tool.RunAsync(path, kind.Arguments, OnLine, linked.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException)
        {
            var timedOut = deadline.IsCancellationRequested && !run.Cancel.IsCancellationRequested;
            return timedOut
                ? new ActionOutcome("failed", new JsonObject { ["result"] = "failed" }, $"Stopped after {(int)kind.Deadline.TotalMinutes} minutes. Repairs it already made stay.", "timeout", "The repair tool took too long.")
                : new ActionOutcome("failed", new JsonObject { ["result"] = "stopped" }, "Stopped. Repairs it already made stay.", "stopped", "Stopped.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ActionOutcome("failed", new JsonObject { ["result"] = "failed" }, "The repair tool could not start.", "start_failed", ex.GetType().Name);
        }
        List<string> lines;
        lock (tail) lines = [.. tail];
        return RepairPlan.Outcome(kind, exit, lines);
    }
}

/// <summary>
/// The real tool runner (NEVER RUN on the development PC): &lt;system directory&gt;\sfc.exe or dism.exe, with the directory
/// from GetSystemDirectoryW, no reparse point on the path and a Microsoft signature; started with an argument list (no
/// shell, no window, stdin closed), both streams drained line by line; cancel kills the process tree.
/// </summary>
public sealed class WindowsRepairTool : IRepairTool
{
    public (string? Path, string? Problem) Locate(string fileName)
    {
        if (fileName is not ("sfc.exe" or "dism.exe")) return (null, "not_found");
        var path = Path.Combine(PowerShellInterpreter.SystemDirectory, fileName);
        try
        {
            if (!File.Exists(path)) return (null, "not_found");
            if (PowerShellInterpreter.HasReparsePoint(path)) return (null, "untrusted");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, "not_found"); }
        return WindowsFileFacts.IsMicrosoft(path) ? (path, null) : (null, "untrusted");
    }

    public async Task<int> RunAsync(string path, IReadOnlyList<string> arguments, Action<string> onLine, CancellationToken ct)
    {
        var sfc = Path.GetFileName(path).Equals("sfc.exe", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            WorkingDirectory = Path.GetDirectoryName(path)!,
            StandardOutputEncoding = sfc ? Encoding.Unicode : null, // sfc writes UTF-16 to a pipe
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("start_failed");
        p.StandardInput.Close();
        p.OutputDataReceived += (_, e) => { if (e.Data is { } d) onLine(d); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is { } d) onLine(d); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try { await p.WaitForExitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (Exception) { }
            throw;
        }
        return p.ExitCode;
    }
}

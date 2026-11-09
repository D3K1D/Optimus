using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;

namespace OptimizationTool.Windows.Actions.Defender;

/// <summary>defender.update_signatures, defender.quick_scan, defender.full_scan (contracts/m4-actions.md §2).</summary>
public sealed class DefenderActionModule : IActionModule
{
    public IEnumerable<IActionHandler> Handlers(ActionContext context)
    {
        var system = new WindowsDefenderSystem();
        return
        [
            new DefenderHandler(DefenderOp.UpdateSignatures, system),
            new DefenderHandler(DefenderOp.QuickScan, system),
            new DefenderHandler(DefenderOp.FullScan, system),
        ];
    }
}

internal enum DefenderOp { UpdateSignatures, QuickScan, FullScan }

/// <summary>
/// A Windows-owned Defender operation run through MpCmdRun.exe with fixed arguments. Defender decides what to do
/// with anything it finds under its own policy; this handler never remediates, and a successful start is not
/// treated as completion: it reads back WMI (signature version, scan end time) and Defender/Operational records.
/// </summary>
internal sealed class DefenderHandler(DefenderOp op, IDefenderSystem system, TimeProvider? clock = null, TimeSpan? tick = null) : IActionHandler
{
    public const string ManualUri = "windowsdefender://threat";
    private const int AccessDenied = unchecked((int)0x80070005);
    private const int ThreatsFoundExit = 2;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly TimeSpan _tick = tick ?? TimeSpan.FromMinutes(1);

    public string ActionId => op switch
    {
        DefenderOp.UpdateSignatures => "defender.update_signatures",
        DefenderOp.QuickScan => "defender.quick_scan",
        _ => "defender.full_scan",
    };
    public string Version => "1.0.0";
    public string Label => op switch
    {
        DefenderOp.UpdateSignatures => "Update Defender intelligence",
        DefenderOp.QuickScan => "Run a quick scan",
        _ => "Run a full scan",
    };
    public string Privilege => "none";
    public bool SupportsUndo => false;
    public string Kind => "provider";

    private string TargetId => op switch { DefenderOp.UpdateSignatures => "signatures", DefenderOp.QuickScan => "quick_scan", _ => "full_scan" };
    private string ScanKind => op == DefenderOp.QuickScan ? "quick" : "full";

    internal IReadOnlyList<string> Arguments => op switch
    {
        DefenderOp.UpdateSignatures => ["-SignatureUpdate"],
        DefenderOp.QuickScan => ["-Scan", "-ScanType", "1"],
        _ => ["-Scan", "-ScanType", "2"],
    };

    public ActionRequest? UndoRequest(JsonObject target, JsonObject before) => null;

    public async Task<ActionResolution> ResolveAsync(ActionRequest request, CancellationToken ct)
    {
        if (request.ActionId != ActionId) return ActionResolution.Ineligible("bad_request", "Wrong action.");
        if (request.Target is { } t && (t["id"]?.GetValue<string>() is { } id && id != TargetId || t["type"]?.GetValue<string>() is { } ty && ty != "defender"))
            return ActionResolution.Ineligible("bad_target", "Unknown target.");
        if (op == DefenderOp.FullScan && !Confirmed(request.Parameters))
            return ActionResolution.Ineligible("confirmation_required", "A full scan takes a long time. Choose it explicitly to continue.");

        var (state, problem) = await EligibleStateAsync(ct).ConfigureAwait(false);
        if (problem is not null) return problem;

        var before = Shape(state!);
        var after = new JsonObject { ["operation"] = TargetId };
        var rows = new List<PlanRow>();
        var context = new List<KeyValuePair<string, string>>();
        List<string> tradeoffs;
        switch (op)
        {
            case DefenderOp.UpdateSignatures:
                rows.Add(new("Security intelligence", SignatureText(state!), "Latest from Microsoft"));
                context.Add(new("Download", "Small, from Microsoft"));
                context.Add(new("Time", "Usually under a minute"));
                tradeoffs = ["Uses the internet for a short download.", "Defender may apply its own settings after updating."];
                break;
            case DefenderOp.QuickScan:
                rows.Add(new("Last quick scan", When(state!.QuickScanEndUtc), "Now"));
                context.Add(new("Time", "A few minutes"));
                context.Add(new("Resources", "Light disk and CPU use"));
                tradeoffs = ["The PC may feel slower while it runs.", "Defender acts on anything it finds under its own settings."];
                break;
            default:
                rows.Add(new("Last full scan", When(state!.FullScanEndUtc), "Now"));
                context.Add(new("Time", "Often an hour or more"));
                context.Add(new("Resources", "Heavy disk use"));
                tradeoffs = ["Takes a long time and uses the disk heavily.", "The PC may feel slower until it ends.", "Defender acts on anything it finds under its own settings."];
                break;
        }
        context.Add(new("Admin", "Not usually needed"));
        var target = new JsonObject { ["type"] = "defender", ["id"] = TargetId, ["label"] = "Microsoft Defender" };
        return new ActionResolution(true, target, before, after, rows, context, tradeoffs, null);
    }

    public async Task<JsonObject> ReadTargetAsync(JsonObject target, CancellationToken ct)
    {
        var read = await system.ReadStatusAsync(ct).ConfigureAwait(false);
        return read.State is { } s ? Shape(s) : new JsonObject { ["error"] = read.Error ?? "failed" };
    }

    public async Task<ActionOutcome> ExecuteAsync(ActionExecution execution, IProgress<string>? progress, CancellationToken ct)
    {
        if (op == DefenderOp.FullScan && !Confirmed(execution.Parameters))
            return Fail("confirmation_required", "A full scan needs an explicit choice.", new JsonObject());

        // Re-check eligibility right before running (Defender may have changed since the preview).
        var (state, problem) = await EligibleStateAsync(ct).ConfigureAwait(false);
        if (problem is not null) return Fail(problem.IneligibleCode!, problem.IneligibleMessage!, new JsonObject(), problem.ManualUri);
        var tool = system.ResolveTool();
        if (tool.Path is null) return Fail(tool.Problem == "untrusted" ? "tool_untrusted" : "tool_missing", "Defender's command tool could not be verified.", new JsonObject(), ManualUri);

        var started = _clock.GetUtcNow();
        progress?.Report(op == DefenderOp.UpdateSignatures ? "Updating definitions" : $"Scanning · {ScanKind}");
        int exit;
        try
        {
            var run = system.RunAsync(tool.Path, Arguments, ct);
            var t0 = _clock.GetTimestamp();
            while (true)
            {
                var done = await Task.WhenAny(run, Task.Delay(_tick, _clock, ct)).ConfigureAwait(false);
                if (done == run) break;
                ct.ThrowIfCancellationRequested();
                var min = (int)_clock.GetElapsedTime(t0).TotalMinutes;
                progress?.Report(op == DefenderOp.UpdateSignatures ? $"Updating definitions · {min} min" : $"Scanning · {ScanKind} · {min} min");
            }
            exit = await run.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ActionOutcome("indeterminate", new JsonObject { ["result"] = "cancelled" },
                "Stopped before Defender reported a result. Defender may still finish on its own.", "cancelled", "Cancelled.");
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return Fail("start_failed", "Defender's command tool did not start.", new JsonObject(), ManualUri);
        }

        if (exit == AccessDenied || exit == 5)
            return Fail("needs_admin", "Defender needs administrator rights for this. Use Windows Security instead.", new JsonObject { ["exitCode"] = Hex(exit) }, ManualUri);

        // Read back with the audit's reader; never take the exit code alone as proof.
        var afterRead = await system.ReadStatusAsync(CancellationToken.None).ConfigureAwait(false);
        var events = SafeEvents(started.AddMinutes(-1));
        var observed = new JsonObject { ["exitCode"] = Hex(exit), ["eventsReadable"] = events.Readable };
        if (afterRead.State is not { } after)
        {
            observed["result"] = "unknown";
            return new ActionOutcome("indeterminate", observed, "Defender's result could not be read back.", "readback_failed", null);
        }

        return op == DefenderOp.UpdateSignatures ? SignatureOutcome(state!, after, exit, events, observed) : ScanOutcome(state!, after, exit, events, started, observed);
    }

    private ActionOutcome SignatureOutcome(DefenderState before, DefenderState after, int exit, DefenderEvents events, JsonObject observed)
    {
        observed["signatureVersionBefore"] = before.SignatureVersion;
        observed["signatureVersion"] = after.SignatureVersion;
        observed["signatureUpdatedAt"] = Iso(after.SignatureUpdatedUtc);
        var changed = after.SignatureVersion is not null && after.SignatureVersion != before.SignatureVersion
            || after.SignatureUpdatedUtc > before.SignatureUpdatedUtc;
        if (changed)
        {
            observed["result"] = "updated";
            return new ActionOutcome("completed", observed, $"Security intelligence updated to {after.SignatureVersion ?? "a newer version"}.");
        }
        if (exit == 0 && events.SignatureFailures == 0)
        {
            observed["result"] = "already_current";
            return new ActionOutcome("completed", observed, $"Security intelligence is already current ({after.SignatureVersion ?? "version unknown"}).");
        }
        observed["result"] = "not_updated";
        return new ActionOutcome("failed", observed, "Defender could not update its security intelligence. Try again later or use Windows Update.",
            exit != 0 ? "exit_" + Hex(exit) : "update_failed", null);
    }

    private ActionOutcome ScanOutcome(DefenderState before, DefenderState after, int exit, DefenderEvents events, DateTimeOffset started, JsonObject observed)
    {
        var endBefore = op == DefenderOp.QuickScan ? before.QuickScanEndUtc : before.FullScanEndUtc;
        var endAfter = op == DefenderOp.QuickScan ? after.QuickScanEndUtc : after.FullScanEndUtc;
        var advanced = endAfter is { } ea && (endBefore is not { } eb || ea > eb);
        observed["scanType"] = ScanKind;
        observed["scanEndAt"] = Iso(endAfter);
        observed["scanEndAdvanced"] = advanced;

        // Correlate by Defender's own scan ID: the first start record of this kind after we began, then its end record.
        var start = events.Scans.FirstOrDefault(s => s.EventId == 1000 && s.Kind == ScanKind && s.Time >= started.AddMinutes(-1));
        var end = start is null ? null : events.Scans.FirstOrDefault(s => s.EventId != 1000 && s.Time >= start.Time
            && (start.ScanId is null || string.Equals(s.ScanId, start.ScanId, StringComparison.OrdinalIgnoreCase)));
        observed["scanId"] = start?.ScanId;
        observed["scanRecord"] = end?.EventId switch { 1001 => "completed", 1002 => "stopped", 1005 => "failed", _ => start is null ? null : "started" };

        var threatsKnown = events.Readable;
        var threatCount = events.Detections;
        var found = threatCount > 0 || exit == ThreatsFoundExit;
        observed["threats"] = new JsonObject
        {
            ["readable"] = threatsKnown,
            ["count"] = threatsKnown ? threatCount : null,
            ["malware"] = threatsKnown ? threatCount - events.PuaDetections : null,
            ["pua"] = threatsKnown ? events.PuaDetections : null,
            ["found"] = found,
        };

        var label = op == DefenderOp.QuickScan ? "Quick scan" : "Full scan";
        if (end?.EventId is 1002 or 1005 && !advanced)
        {
            observed["result"] = end.EventId == 1002 ? "stopped" : "failed";
            return new ActionOutcome("failed", observed, end.EventId == 1002 ? $"{label} was stopped before it finished." : $"{label} failed.",
                end.EventId == 1002 ? "scan_stopped" : "scan_failed", null);
        }
        if (advanced || end?.EventId == 1001)
        {
            // "No threats" needs the complete event history; without it the scan still finished, but its result is unknown.
            var unknown = !found && !threatsKnown;
            observed["result"] = found ? "threats_found" : unknown ? "threats_unknown" : "no_threats";
            observed["manualUri"] = found || unknown ? ManualUri : null;
            var text = unknown ? $"{label} finished. Threat result unknown; check Windows Security."
                : !found ? $"{label} finished. No threats found."
                : threatsKnown && threatCount > 0
                    ? $"{label} finished. Defender found {(threatCount == 1 ? "1 threat" : $"{threatCount} threats")}; review in Windows Security."
                    : $"{label} finished. Defender found threats; review in Windows Security.";
            return new ActionOutcome("completed", observed, text);
        }
        observed["result"] = "unconfirmed";
        if (exit is not (0 or ThreatsFoundExit))
            return new ActionOutcome("failed", observed, $"{label} did not run.", "exit_" + Hex(exit), null);
        return new ActionOutcome("indeterminate", observed, $"{label} result could not be confirmed. Check Windows Security.", "unconfirmed", null);
    }

    private async Task<(DefenderState? State, ActionResolution? Problem)> EligibleStateAsync(CancellationToken ct)
    {
        var read = await system.ReadStatusAsync(ct).ConfigureAwait(false);
        if (read.State is not { } s)
        {
            return (null, read.Error switch
            {
                "unsupported" => ActionResolution.Ineligible("defender_unavailable", "Microsoft Defender isn't available on this PC."),
                "denied" => ActionResolution.Ineligible("needs_admin", "Windows needs administrator rights to read Defender.", ManualUri),
                _ => ActionResolution.Ineligible("defender_unreadable", "Microsoft Defender did not answer.", ManualUri),
            });
        }
        if (s.Mode != "primary")
            return (s, ActionResolution.Ineligible("not_primary", s.Mode == "disabled"
                ? "Microsoft Defender is off or not running." : "Another antivirus is in charge. Use its app instead.", ManualUri));
        if (s.ServiceEnabled == false)
            return (s, ActionResolution.Ineligible("service_stopped", "Microsoft Defender's service is not running.", ManualUri));
        var tool = system.ResolveTool();
        if (tool.Path is null)
            return (s, ActionResolution.Ineligible(tool.Problem == "untrusted" ? "tool_untrusted" : "tool_missing", "Defender's command tool could not be verified.", ManualUri));
        return (s, null);
    }

    private DefenderEvents SafeEvents(DateTimeOffset since)
    {
        try { return system.ReadEventsSince(since, CancellationToken.None); }
        catch (Exception) { return DefenderEvents.Unreadable; }
    }

    private JsonObject Shape(DefenderState s) => op switch
    {
        DefenderOp.UpdateSignatures => new JsonObject { ["signatureVersion"] = s.SignatureVersion, ["signatureUpdatedAt"] = Iso(s.SignatureUpdatedUtc) },
        DefenderOp.QuickScan => new JsonObject { ["quickScanEndAt"] = Iso(s.QuickScanEndUtc) },
        _ => new JsonObject { ["fullScanEndAt"] = Iso(s.FullScanEndUtc) },
    };

    private static bool Confirmed(JsonObject? p) =>
        p?["confirmLong"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static ActionOutcome Fail(string code, string message, JsonObject observed, string? manualUri = null)
    {
        observed["result"] ??= "not_run";
        if (manualUri is not null) observed["manualUri"] = manualUri;
        return new ActionOutcome("failed", observed, message, code, message);
    }

    private static string SignatureText(DefenderState s)
    {
        var v = s.SignatureVersion ?? "Unknown version";
        return s.SignatureAgeDays switch
        {
            null or < 0 or > 65000 => v,
            0 => $"{v} · today",
            1 => $"{v} · 1 day old",
            var d => $"{v} · {d} days old",
        };
    }

    private static string When(DateTime? utc) => utc is { } t ? t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "Not recorded";
    private static string? Iso(DateTime? utc) => utc?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    private static string Hex(int code) => "0x" + code.ToString("x8", CultureInfo.InvariantCulture);
}

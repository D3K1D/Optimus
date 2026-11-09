using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;
using OptimizationTool.Engine;
using OptimizationTool.Windows.Reliability;
using OptimizationTool.Windows.Security.Collectors;
using OptimizationTool.Windows.Security.Common;

namespace OptimizationTool.Windows.Actions.Defender;

/// <summary>Defender state as the handlers use it. Mode: primary | passive | side_by_side | disabled | unknown.</summary>
internal sealed record DefenderState(string Mode, bool? ServiceEnabled, string? SignatureVersion, DateTime? SignatureUpdatedUtc,
    long? SignatureAgeDays, DateTime? QuickScanEndUtc, DateTime? FullScanEndUtc);

/// <summary>State, or Error: unsupported | denied | failed.</summary>
internal sealed record DefenderStatusRead(DefenderState? State, string? Error);

/// <summary>Path to a verified Microsoft-signed MpCmdRun.exe, or Problem: not_found | untrusted.</summary>
internal sealed record ToolPath(string? Path, string? Problem);

/// <summary>Scan lifecycle record from Defender/Operational: 1000 started, 1001 completed, 1002 stopped, 1005 failed. Kind: quick | full | other.</summary>
internal sealed record ScanEvent(int EventId, string? ScanId, string Kind, DateTimeOffset Time);

/// <summary>Defender/Operational since the operation started: scan records, detections (1116, count + classification), signature results (2000 ok / 2001 failed).</summary>
internal sealed record DefenderEvents(bool Readable, IReadOnlyList<ScanEvent> Scans, int Detections, int PuaDetections, int SignatureSuccesses, int SignatureFailures)
{
    public static readonly DefenderEvents Unreadable = new(false, [], 0, 0, 0, 0);
}

/// <summary>Every Windows touch the Defender handlers make. Tests replace it; no test may start a real update or scan.</summary>
internal interface IDefenderSystem
{
    Task<DefenderStatusRead> ReadStatusAsync(CancellationToken ct);
    ToolPath ResolveTool();
    /// <summary>Runs the tool with fixed arguments, no shell, no window; kills it (and its tree) when <paramref name="ct"/> fires, then throws.</summary>
    Task<int> RunAsync(string path, IReadOnlyList<string> args, CancellationToken ct);
    DefenderEvents ReadEventsSince(DateTimeOffset since, CancellationToken ct);
}

internal sealed class WindowsDefenderSystem : IDefenderSystem
{
    private const int WBEM_E_INVALID_NAMESPACE = unchecked((int)0x8004100E);
    private const int WBEM_E_INVALID_CLASS = unchecked((int)0x80041010);
    private const int WBEM_E_ACCESS_DENIED = unchecked((int)0x80041003);
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);
    private const string Channel = "Microsoft-Windows-Windows Defender/Operational";

    /// <summary>Same WMI read as the audit (win.security.defender): MSFT_MpComputerStatus via WbemScripting.</summary>
    public async Task<DefenderStatusRead> ReadStatusAsync(CancellationToken ct)
    {
        DefenderRaw raw;
        try { raw = await StaWorker.Run(() => new DefenderCollector(new CollectorOptions()).Read(ct), "defender action", ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (UnauthorizedAccessException) { return new(null, "denied"); }
        catch (Exception) { return new(null, "failed"); }

        if (raw.Status is not { } s)
        {
            var hr = raw.StatusError;
            if (hr == Automation.Hex(WBEM_E_INVALID_NAMESPACE) || hr == Automation.Hex(WBEM_E_INVALID_CLASS) || hr == "no_rows") return new(null, "unsupported");
            if (hr == Automation.Hex(WBEM_E_ACCESS_DENIED) || hr == Automation.Hex(E_ACCESSDENIED)) return new(null, "denied");
            return new(null, "failed");
        }
        return new(new DefenderState(DefenderCollector.Mode(s), s.AmServiceEnabled, s.SignatureVersion, s.SignatureUpdatedUtc,
            s.SignatureAgeDays, s.QuickScanEndUtc, s.FullScanEndUtc), null);
    }

    /// <summary>
    /// MpCmdRun.exe from the Defender platform directory: registry InstallLocation first, then the newest
    /// %ProgramData%\Microsoft\Windows Defender\Platform\&lt;version&gt;, then %ProgramFiles%\Windows Defender.
    /// The path must sit under one of those roots, must not be a reparse point, and must be Microsoft-signed (embedded or catalog).
    /// </summary>
    public ToolPath ResolveTool()
    {
        var platformRoot = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows Defender", "Platform");
        var programRoot = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender");
        var candidates = new List<string>();
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows Defender", writable: false);
            if (key?.GetValue("InstallLocation") is string loc && loc.Length is > 0 and < 260) candidates.Add(System.IO.Path.Combine(loc, "MpCmdRun.exe"));
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        try
        {
            if (Directory.Exists(platformRoot))
                candidates.AddRange(Directory.GetDirectories(platformRoot)
                    .Select(d => (d, v: Version.TryParse(System.IO.Path.GetFileName(d).Split('-')[0], out var v) ? v : null))
                    .Where(x => x.v is not null).OrderByDescending(x => x.v).Select(x => System.IO.Path.Combine(x.d, "MpCmdRun.exe")));
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { }
        candidates.Add(System.IO.Path.Combine(programRoot, "MpCmdRun.exe"));

        var sawUntrusted = false;
        using var checker = new SignatureChecker();
        foreach (var c in candidates)
        {
            string full;
            try { full = System.IO.Path.GetFullPath(c); } catch (Exception) { continue; }
            if (!Under(full, platformRoot) && !Under(full, programRoot)) { sawUntrusted = true; continue; }
            try
            {
                var fi = new FileInfo(full);
                if (!fi.Exists) continue;
                if (fi.Attributes.HasFlag(FileAttributes.ReparsePoint)) { sawUntrusted = true; continue; }
            }
            catch (Exception) { continue; }
            if (checker.Check(full).IsMicrosoft) return new ToolPath(full, null);
            sawUntrusted = true;
        }
        return new ToolPath(null, sawUntrusted ? "untrusted" : "not_found");
    }

    private static bool Under(string path, string root) =>
        path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    public async Task<int> RunAsync(string path, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            WorkingDirectory = System.IO.Path.GetDirectoryName(path)!,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("start_failed");
        // Drain output so the tool never blocks on a full pipe; its text is not kept (it can contain paths).
        p.OutputDataReceived += static (_, _) => { };
        p.ErrorDataReceived += static (_, _) => { };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (Exception) { }
            throw;
        }
        return p.ExitCode;
    }

    public DefenderEvents ReadEventsSince(DateTimeOffset since, CancellationToken ct)
    {
        var at = since.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var xpath = $"*[System[(EventID=1000 or EventID=1001 or EventID=1002 or EventID=1005 or EventID=1116 or EventID=2000 or EventID=2001) and TimeCreated[@SystemTime>='{at}']]]";
        var raw = EventReader.Read(Channel, xpath, 500, ct);
        return DefenderEventParser.FromChannel(raw);
    }
}

/// <summary>Pure: Defender/Operational rows → scan records, detection counts and signature results. Keeps no names or paths.</summary>
internal static class DefenderEventParser
{
    /// <summary>Scan records are kept from whatever was read, but the history counts as readable (so "no threats" can be
    /// claimed) only when the whole query was read: no row cap, no interruption and no row that failed to render.</summary>
    public static DefenderEvents FromChannel(RawChannel raw) =>
        raw.Status != "ok" ? DefenderEvents.Unreadable
        : Parse(raw.Events) with { Readable = !raw.RowCapHit && !raw.Interrupted && raw.Unreadable == 0 };

    public static DefenderEvents Parse(IEnumerable<RawEvent> events)
    {
        var scans = new List<ScanEvent>();
        var detections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pua = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int ok = 0, failed = 0, anon = 0;
        foreach (var e in events)
        {
            switch (e.EventId)
            {
                case 1000 or 1001 or 1002 or 1005:
                    var p = e.Field("Scan Parameters", -1) ?? "";
                    var kind = p.Contains("Quick", StringComparison.OrdinalIgnoreCase) ? "quick" : p.Contains("Full", StringComparison.OrdinalIgnoreCase) ? "full" : "other";
                    scans.Add(new ScanEvent(e.EventId, e.Field("Scan ID", -1), kind, e.Time));
                    break;
                case 1116:
                    var id = e.Field("Detection ID", -1) is { Length: > 0 } d ? d : $"anon-{anon++}";
                    detections.Add(id);
                    if ((e.Field("Category Name", -1) ?? "").Contains("Potentially Unwanted", StringComparison.OrdinalIgnoreCase)) pua.Add(id);
                    break;
                case 2000: ok++; break;
                case 2001: failed++; break;
            }
        }
        return new DefenderEvents(true, scans.OrderBy(s => s.Time).ToList(), detections.Count, pua.Count, ok, failed);
    }
}

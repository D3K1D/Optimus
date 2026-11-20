using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Engine.Diagnostics;

namespace OptimizationTool.Desktop.Bridge;

/// <summary>
/// "Report a problem" in the shell (docs/error-logging.md §2, protocol-v1.md §9). Shell-handled, never forwarded, so it
/// works while the host is down:
/// <list type="bullet">
/// <item><c>shell.problemReport {operation?}</c> → <c>{reportId, text, title, trimmed}</c>: built by <see cref="ProblemReport"/>
/// from <c>&lt;data&gt;\logs</c>; the last few reports are kept in memory by id.</item>
/// <item><c>shell.openIssue {reportId}</c> → <c>{status:"opened"|"failed"|"unknown"}</c>: the shell builds the GitHub URL
/// (<see cref="IssueUrl"/>) from the report it holds (the page never sends text or a URL) and opens it through the system
/// handler, rate-limited as <see cref="SettingsLauncher"/>.</item>
/// <item><c>shell.crashStatus {}</c> → <c>{crashed, version, process?, at?}</c> from the crash markers; <c>shell.crashDismiss {}</c>
/// → <c>{status:"cleared"}</c> deletes them.</item>
/// </list>
/// </summary>
public sealed class ProblemReporter
{
    public const string ReportMethod = "shell.problemReport";
    public const string OpenIssueMethod = "shell.openIssue";
    public const string CrashStatusMethod = "shell.crashStatus";
    public const string CrashDismissMethod = "shell.crashDismiss";
    public static readonly IReadOnlySet<string> Methods = new HashSet<string>(StringComparer.Ordinal) { ReportMethod, OpenIssueMethod, CrashStatusMethod, CrashDismissMethod };

    public const int KeepReports = 5;
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);
    private static readonly HashSet<string> CrashProcesses = new(StringComparer.Ordinal) { "host", "desktop", "mcp" };

    private readonly string? _dataDir;
    private readonly Action<string> _open;
    private readonly Func<long> _nowTicks;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<IReadOnlyList<LocalIdentity>> _identities;
    private readonly string _appVersion;
    private readonly string _windowsVersion;
    private readonly string _architecture;
    private readonly Lock _lock = new();
    private readonly LinkedList<(string Id, ProblemReportResult Report)> _reports = new();
    private long _lastOpenTicks = long.MinValue;

    /// <param name="dataDir">The data folder (logs in <c>&lt;data&gt;\logs</c>); defaults to %LOCALAPPDATA%\OptimizationTool.</param>
    /// <param name="open">Opens the issue URL; defaults to the system handler.</param>
    /// <param name="nowTicks">Monotonic clock in <see cref="Stopwatch"/> ticks for the rate limit; for tests.</param>
    public ProblemReporter(string? dataDir = null, Action<string>? open = null, Func<long>? nowTicks = null, Func<DateTimeOffset>? clock = null,
        Func<IReadOnlyList<LocalIdentity>>? identities = null, string? appVersion = null, string? windowsVersion = null, string? architecture = null)
    {
        _dataDir = dataDir ?? DefaultDataDir();
        _open = open ?? SettingsLauncher.OpenWithSystem;
        _nowTicks = nowTicks ?? Stopwatch.GetTimestamp;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _identities = identities ?? IdentityFilter.Local;
        _appVersion = appVersion ?? (typeof(ProblemReporter).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
        _windowsVersion = windowsVersion ?? Environment.OSVersion.Version.ToString(3);
        _architecture = architecture ?? RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
    }

    private static string? DefaultDataDir()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(local) ? null : Path.Combine(local, "OptimizationTool");
    }

    public string Handle(string method, JsonNode id, JsonObject? parameters, AppLog log) => method switch
    {
        ReportMethod => Report(id, parameters, log),
        OpenIssueMethod => OpenIssue(id, parameters, log),
        CrashStatusMethod => CrashStatus(id, parameters, log),
        CrashDismissMethod => CrashDismiss(id, parameters, log),
        _ => BridgeMessages.Error(id, -32601, "Method not found."),
    };

    private string Report(JsonNode id, JsonObject? parameters, AppLog log)
    {
        string? operation = null;
        if (parameters is { Count: > 0 })
        {
            if (parameters.Count != 1 || !parameters.ContainsKey("operation")) return Invalid(id, ReportMethod, "params", log);
            var node = parameters["operation"];
            if (node is not null)
            {
                if (node is not JsonValue v || !v.TryGetValue<string>(out var s)) return Invalid(id, ReportMethod, "operation", log);
                operation = s;
            }
        }

        ProblemReportResult report;
        try
        {
            report = ProblemReport.Build(_dataDir, _appVersion, _windowsVersion, _architecture, operation, _identities(), _clock());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            log.Warn($"shell: {ReportMethod} failed ({ex.GetType().Name})");
            return BridgeMessages.Error(id, -32013, "Could not build the report.", new JsonObject { ["reason"] = "failed" });
        }
        var (_, trimmed) = IssueUrl.Build(report.Title, report.Text);
        var reportId = "rpt-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        lock (_lock)
        {
            _reports.AddLast((reportId, report));
            while (_reports.Count > KeepReports) _reports.RemoveFirst();
        }
        log.Info($"shell: {ReportMethod} {reportId} ({ProblemReport.Operation(operation)}, {report.Text.Length} chars)");
        return BridgeMessages.Result(id, new JsonObject
        {
            ["reportId"] = reportId,
            ["text"] = report.Text,
            ["title"] = report.Title,
            ["trimmed"] = trimmed,
        });
    }

    private string OpenIssue(JsonNode id, JsonObject? parameters, AppLog log)
    {
        if (parameters is null || parameters.Count != 1 || parameters["reportId"] is not JsonValue v || !v.TryGetValue<string>(out var reportId)
            || reportId.Length is 0 or > 64)
            return Invalid(id, OpenIssueMethod, "reportId", log);

        ProblemReportResult? report = null;
        lock (_lock)
            foreach (var (rid, r) in _reports)
                if (rid == reportId) report = r;
        if (report is null)
        {
            log.Info($"shell: {OpenIssueMethod} unknown report");
            return BridgeMessages.Result(id, new JsonObject { ["status"] = "unknown" });
        }

        var (url, _) = IssueUrl.Build(report.Title, report.Text);
        // Defence in depth: only the project's own new-issue page is ever opened.
        if (!IsIssueUrl(url))
        {
            log.Warn($"shell: {OpenIssueMethod} refused (not the issue page)");
            return BridgeMessages.Result(id, new JsonObject { ["status"] = "failed" });
        }

        lock (_lock)
        {
            var now = _nowTicks();
            if (_lastOpenTicks != long.MinValue && now - _lastOpenTicks < MinInterval.TotalSeconds * Stopwatch.Frequency)
            {
                log.Warn($"shell: {OpenIssueMethod} rate limited");
                return BridgeMessages.Error(id, -32013, "Too many requests.", new JsonObject { ["reason"] = "rate_limited" });
            }
            _lastOpenTicks = now;
        }

        try { _open(url.AbsoluteUri); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.Warn($"shell: could not open the issue page ({ex.GetType().Name})");
            return BridgeMessages.Result(id, new JsonObject { ["status"] = "failed" });
        }
        log.Info($"shell: {OpenIssueMethod} {reportId} opened"); // the id only: the URL holds the report
        return BridgeMessages.Result(id, new JsonObject { ["status"] = "opened" });
    }

    /// <summary>True for exactly https://github.com/D3K1D/Optimus/issues/new with a query, default port, no user info.</summary>
    internal static bool IsIssueUrl(Uri url) =>
        url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort && url.UserInfo.Length == 0
        && string.Equals(url.IdnHost, IssueUrl.Host, StringComparison.Ordinal)
        && string.Equals(url.AbsolutePath, IssueUrl.PathPrefix, StringComparison.Ordinal)
        && url.AbsoluteUri.StartsWith(IssueUrl.Base + "&", StringComparison.Ordinal);

    private string CrashStatus(JsonNode id, JsonObject? parameters, AppLog log)
    {
        if (parameters is { Count: > 0 }) return Invalid(id, CrashStatusMethod, "params", log);
        var crashes = _dataDir is null ? [] : CrashMarker.ReadAll(_dataDir);
        var last = crashes.LastOrDefault(c => CrashProcesses.Contains(c.Process));
        // version: the app's own, for the logo menu (the page has no other source for it).
        if (last is null) return BridgeMessages.Result(id, new JsonObject { ["crashed"] = false, ["version"] = _appVersion });
        return BridgeMessages.Result(id, new JsonObject
        {
            ["crashed"] = true,
            ["version"] = _appVersion,
            ["process"] = last.Process,
            ["at"] = last.At.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        });
    }

    private string CrashDismiss(JsonNode id, JsonObject? parameters, AppLog log)
    {
        if (parameters is { Count: > 0 }) return Invalid(id, CrashDismissMethod, "params", log);
        if (_dataDir is not null) CrashMarker.ClearAll(_dataDir);
        log.Info($"shell: {CrashDismissMethod} cleared");
        return BridgeMessages.Result(id, new JsonObject { ["status"] = "cleared" });
    }

    private static string Invalid(JsonNode id, string method, string field, AppLog log)
    {
        log.Warn($"shell: refused {method} (invalid params)");
        return BridgeMessages.Error(id, -32602, "Invalid params.", new JsonObject { ["field"] = field });
    }
}

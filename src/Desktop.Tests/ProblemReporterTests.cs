using System.Text.Json.Nodes;
using OptimizationTool.Desktop.Bridge;
using OptimizationTool.Engine.Agent;

namespace OptimizationTool.Desktop.Tests;

/// <summary>
/// docs/error-logging.md §2: shell.problemReport, shell.openIssue, shell.crashStatus, shell.crashDismiss. Shell-handled,
/// never forwarded. Nothing is really opened: the opener is a fake; logs and markers live in a temp folder.
/// </summary>
public sealed class ProblemReporterTests : IDisposable
{
    private const string Doc = "https://app.optimizationtool.invalid/index.html";
    private readonly string _data = Path.Combine(Path.GetTempPath(), "ot-reporter-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _opened = [];
    private readonly NoBackend _backend = new();
    private long _now = 1_000_000;
    private Action<string>? _open;
    private readonly BridgeDispatcher _dispatcher;

    private sealed class NoBackend : IBackend
    {
        public int Calls;
        public Task<JsonObject> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            Calls++;
            throw new BackendUnavailableException("none");
        }
    }

    public ProblemReporterTests()
    {
        Directory.CreateDirectory(Path.Combine(_data, "logs"));
        var reporter = new ProblemReporter(_data, url => { if (_open is not null) _open(url); else _opened.Add(url); }, () => _now,
            () => DateTimeOffset.UtcNow, () => IdentityFilter.Local("jamie", "DESKTOP-K7Q2", null), "0.9.0", "10.0.26200", "x64");
        _dispatcher = new BridgeDispatcher(new BridgeRouter(), _backend, AppLog.Disabled, problems: reporter);
    }

    public void Dispose()
    {
        try { Directory.Delete(_data, true); } catch (IOException) { }
    }

    private async Task<JsonNode> Call(string method, string paramsJson = "{}")
    {
        var reply = await _dispatcher.HandleAsync($$$"""{"type":"rpc","id":7,"method":"{{{method}}}","params":{{{paramsJson}}}}""", Doc, true, default);
        return JsonNode.Parse(reply!)!;
    }

    [Fact]
    public void All_four_are_shell_methods()
    {
        foreach (var m in new[] { "shell.problemReport", "shell.openIssue", "shell.crashStatus", "shell.crashDismiss" })
        {
            Assert.Contains(m, (IReadOnlySet<string>)BridgeRouter.ShellMethods);
            Assert.IsType<BridgeDecision.Shell>(new BridgeRouter().Evaluate($$$"""{"type":"rpc","id":1,"method":"{{{m}}}","params":{}}""", Doc, true));
        }
    }

    [Fact]
    public async Task ProblemReport_returns_id_text_title_trimmed()
    {
        File.WriteAllText(Path.Combine(_data, "logs", "host.log"),
            DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture)
            + " host 1:1 WARN rpc: scan.start failed for jamie -32603 [E-HST-002] @src/Host/HostServer.cs:184\n");
        var r = (await Call("shell.problemReport", """{"operation":"scan"}"""))["result"]!;
        Assert.Matches("^rpt-[0-9a-f]{16}$", r["reportId"]!.GetValue<string>());
        Assert.Equal("Problem: scan", r["title"]!.GetValue<string>());
        var text = r["text"]!.GetValue<string>();
        Assert.StartsWith("Optimus 0.9.0 · Windows 10.0.26200 · x64\nOperation: scan\n", text);
        Assert.Contains("host WARN rpc: scan.start failed for <removed> -32603 [E-HST-002] @src/Host/HostServer.cs:184", text);
        Assert.False(r["trimmed"]!.GetValue<bool>());
        Assert.Equal(0, _backend.Calls);

        Assert.Contains("Operation: other", (await Call("shell.problemReport"))["result"]!["text"]!.GetValue<string>());
        Assert.Contains("Operation: other", (await Call("shell.problemReport", """{"operation":"my secret doc"}"""))["result"]!["text"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"operation":5}""")]
    [InlineData("""{"operation":"scan","text":"x"}""")]
    [InlineData("""{"url":"https://evil.example/"}""")]
    public async Task ProblemReport_rejects_bad_params(string p) =>
        Assert.Equal(-32602, (await Call("shell.problemReport", p))["error"]!["code"]!.GetValue<int>());

    [Fact]
    public async Task OpenIssue_opens_only_the_project_issue_page_from_the_held_report()
    {
        var id = (await Call("shell.problemReport", """{"operation":"crash"}"""))["result"]!["reportId"]!.GetValue<string>();
        var r = await Call("shell.openIssue", $$"""{"reportId":"{{id}}"}""");
        Assert.Equal("opened", r["result"]!["status"]!.GetValue<string>());
        var url = new Uri(Assert.Single(_opened));
        Assert.Equal("https://github.com/D3K1D/Optimus/issues/new", url.GetLeftPart(UriPartial.Path));
        Assert.StartsWith("https://github.com/D3K1D/Optimus/issues/new?template=bug_report.yml&title=Problem%3A%20crash&report=Optimus%200.9.0", _opened[0]);
        Assert.True(ProblemReporter.IsIssueUrl(url));
        Assert.False(ProblemReporter.IsIssueUrl(new Uri("https://github.com.evil.example/D3K1D/Optimus/issues/new?template=x")));
        Assert.False(ProblemReporter.IsIssueUrl(new Uri("https://github.com/someone/else/issues/new?template=x")));
        Assert.False(ProblemReporter.IsIssueUrl(new Uri("http://github.com/D3K1D/Optimus/issues/new?template=bug_report.yml&title=x")));
    }

    [Fact]
    public async Task OpenIssue_unknown_and_invalid()
    {
        Assert.Equal("unknown", (await Call("shell.openIssue", """{"reportId":"rpt-0000000000000000"}"""))["result"]!["status"]!.GetValue<string>());
        Assert.Equal(-32602, (await Call("shell.openIssue", """{"url":"https://github.com/"}"""))["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32602, (await Call("shell.openIssue", """{"reportId":"rpt-1","text":"x"}"""))["error"]!["code"]!.GetValue<int>());
        Assert.Empty(_opened);
    }

    [Fact]
    public async Task OpenIssue_is_rate_limited_and_reports_failure()
    {
        var id = (await Call("shell.problemReport"))["result"]!["reportId"]!.GetValue<string>();
        var p = $$"""{"reportId":"{{id}}"}""";
        Assert.Equal("opened", (await Call("shell.openIssue", p))["result"]!["status"]!.GetValue<string>());
        var limited = await Call("shell.openIssue", p);
        Assert.Equal(-32013, limited["error"]!["code"]!.GetValue<int>());
        Assert.Equal("rate_limited", limited["error"]!["data"]!["reason"]!.GetValue<string>());
        _now += 2 * System.Diagnostics.Stopwatch.Frequency;
        _open = _ => throw new System.ComponentModel.Win32Exception(2);
        Assert.Equal("failed", (await Call("shell.openIssue", p))["result"]!["status"]!.GetValue<string>());
        Assert.Single(_opened);
    }

    [Fact]
    public async Task Only_the_last_reports_are_kept()
    {
        var first = (await Call("shell.problemReport"))["result"]!["reportId"]!.GetValue<string>();
        for (var i = 0; i < ProblemReporter.KeepReports; i++) await Call("shell.problemReport");
        Assert.Equal("unknown", (await Call("shell.openIssue", $$"""{"reportId":"{{first}}"}"""))["result"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task CrashStatus_and_dismiss()
    {
        var none = (await Call("shell.crashStatus"))["result"]!;
        Assert.False(none["crashed"]!.GetValue<bool>());
        Assert.False(string.IsNullOrEmpty(none["version"]!.GetValue<string>()));
        File.WriteAllText(Path.Combine(_data, "logs", "crash-host.json"),
            """{"process":"host","at":"2026-11-18T10:00:00.000Z","type":"InvalidOperationException","site":"E-UNH-001","version":"0.9.0"}""");
        File.WriteAllText(Path.Combine(_data, "logs", "crash-mcp.json"),
            """{"process":"mcp","at":"2026-11-18T10:05:00.000Z","type":"IOException","site":"E-UNH-003","version":"0.9.0"}""");
        var s = (await Call("shell.crashStatus"))["result"]!;
        Assert.True(s["crashed"]!.GetValue<bool>());
        Assert.Equal("mcp", s["process"]!.GetValue<string>());
        Assert.Equal("2026-11-18T10:05:00.000Z", s["at"]!.GetValue<string>());

        Assert.Equal(-32602, (await Call("shell.crashDismiss", """{"x":1}"""))["error"]!["code"]!.GetValue<int>());
        Assert.Equal("cleared", (await Call("shell.crashDismiss"))["result"]!["status"]!.GetValue<string>());
        Assert.Empty(Directory.GetFiles(Path.Combine(_data, "logs"), "crash-*.json"));
        Assert.False((await Call("shell.crashStatus"))["result"]!["crashed"]!.GetValue<bool>());
        Assert.Equal(-32602, (await Call("shell.crashStatus", """{"x":1}"""))["error"]!["code"]!.GetValue<int>());
    }
}

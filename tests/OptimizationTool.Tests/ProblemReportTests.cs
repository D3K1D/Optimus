using System.Text;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Engine.Diagnostics;

namespace OptimizationTool.Tests;

/// <summary>docs/error-logging.md §2: the "Report a problem" text, built from fake logs in a temp folder.</summary>
public sealed class ProblemReportTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyList<LocalIdentity> Ids = IdentityFilter.Local("jamie", "DESKTOP-K7Q2", "CONTOSO");
    private readonly string _data = Path.Combine(Path.GetTempPath(), "ot-report-" + Guid.NewGuid().ToString("N"));
    private string Logs => Path.Combine(_data, "logs");

    public ProblemReportTests() => Directory.CreateDirectory(Logs);

    public void Dispose()
    {
        try { Directory.Delete(_data, true); } catch (IOException) { }
    }

    private static string At(TimeSpan ago) => (Now - ago).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);

    private void Write(string file, params string[] lines) =>
        File.AppendAllText(Path.Combine(Logs, file), string.Join("\r\n", lines) + "\r\n", Encoding.UTF8);

    private ProblemReportResult Build(string? op = "scan") => ProblemReport.Build(_data, "0.9.0", "10.0.26200", "x64", op, Ids, Now);

    private const string Stack =
        "  System.InvalidOperationException: Collector stopped early\n" +
        "     at OptimizationTool.Engine.Scans.RunAsync(String intent, CancellationToken ct) in src/Engine/Scans.cs:line 142\n" +
        "     at System.Threading.Tasks.Task.Run()\n" +
        "     at OptimizationTool.Host.HostServer.<HandleAsync>d__12.MoveNext() in C:\\build\\repo\\src\\Host\\HostServer.cs:line 184";

    [Fact]
    public void Picks_the_last_exception_with_site_frames_and_title()
    {
        Write("host.log",
            $"{At(TimeSpan.FromHours(2))} host 10:1 ERROR scan: old failure [E-SCN-001] @src/Engine/Scans.cs:90",
            "  System.ArgumentException: old",
            "     at OptimizationTool.Engine.Old.Run() in src/Engine/Old.cs:line 1",
            $"{At(TimeSpan.FromMinutes(5))} host 10:7 ERROR scan: collector failed [E-SCN-003] @src/Engine/Scans.cs:150",
            Stack);
        var r = Build();
        Assert.Equal("Problem: scan E-SCN-003", r.Title);
        Assert.StartsWith("Optimus 0.9.0 · Windows 10.0.26200 · x64\nOperation: scan\nError: E-SCN-003 · InvalidOperationException · src/Engine/Scans.cs:142", r.Text);
        Assert.Contains("\n  InvalidOperationException: Collector stopped early\n", r.Text);
        Assert.Contains("    at OptimizationTool.Engine.Scans.RunAsync in src/Engine/Scans.cs:142", r.Text);
        Assert.Contains("    at OptimizationTool.Host.HostServer.<HandleAsync>d__12.MoveNext in src/Host/HostServer.cs:184", r.Text);
        Assert.DoesNotContain("System.Threading", r.Text);   // only Optimus frames
        Assert.DoesNotContain("String intent", r.Text);      // parameter lists dropped
        Assert.DoesNotContain("Old.cs", r.Text);             // not the last exception
        Assert.DoesNotContain("C:", r.Text);
        Assert.Contains("ERROR scan: collector failed [E-SCN-003] @src/Engine/Scans.cs:150", r.Text);
    }

    [Fact]
    public void Scrubs_personal_data_token_by_token()
    {
        string[] secrets =
        [
            @"C:\Users\jamie\Documents\taxes.xlsx", "%USERPROFILE%\\AppData", "jamie", "DESKTOP-K7Q2", "CONTOSO",
            "S-1-5-21-1004336348-1177238915-682003330-512", "jamie.k@example.com", "192.168.1.20", "10.0.0.5:8080",
            "fe80::1c2b:3d4e:5f60:7a8b", "00-1A-2B-3C-4D-5E", "0011.2233.4455", "VK7JG-NPHTM-C97JM-9MPGT-3V66T", "WD-WCC4E1234567",
            "3f2504e0-4f89-11d3-9a0c-0305e82c3301", "https://evil.example.com/x?token=abc", "Program/Data",
        ];
        var lines = secrets.Select((s, i) => $"{At(TimeSpan.FromMinutes(30 - i))} host 1:1 WARN scan: value {s} seen @src/Engine/Scans.cs:{i + 1}").ToArray();
        Write("host.log", lines);
        Write("desktop.log", $"{At(TimeSpan.FromMinutes(1))} desktop 2:1 WARN finding: \"Spotify starts with Windows for jamie\" kept 'Chrome password vault' x");
        var r = Build();
        foreach (var s in secrets) Assert.DoesNotContain(s, r.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Spotify", r.Text);
        Assert.DoesNotContain("password vault", r.Text);
        Assert.DoesNotContain("example", r.Text);
        Assert.Contains("WARN scan: value <removed> seen @src/Engine/Scans.cs:1", r.Text);
        Assert.Contains("WARN finding: <removed> kept <removed> x", r.Text);
        for (var i = 1; i <= secrets.Length; i++) Assert.Contains($"@src/Engine/Scans.cs:{i}", r.Text);
    }

    [Fact]
    public void Identity_split_over_two_tokens_is_removed()
    {
        Write("host.log", $"{At(TimeSpan.FromMinutes(1))} host 1:1 WARN rpc: ja mie failed");
        Assert.DoesNotContain("mie", Build().Text);
    }

    [Fact]
    public void Keeps_code_locations_site_ids_methods_versions_and_type_names()
    {
        Write("host.log",
            $"{At(TimeSpan.FromMinutes(3))} host 1:1 WARN rpc: scan.start failed -32603 [E-HST-002] @src/Host/HostServer.cs:184",
            $"{At(TimeSpan.FromMinutes(2))} host 1:1 WARN rpc: provider.setKey shell.openSettings version 10.0.26100.2 at src/Engine/X.cs:line 7 System.IO.IOException",
            $"{At(TimeSpan.FromMinutes(1))} host 1:1 WARN rpc: OptimizationTool.Engine.Scans.RunAsync Microsoft.Data.Sqlite.SqliteException");
        var t = Build().Text;
        Assert.Contains("scan.start failed -32603 [E-HST-002] @src/Host/HostServer.cs:184", t);
        Assert.Contains("provider.setKey shell.openSettings version 10.0.26100.2 at src/Engine/X.cs:line 7 System.IO.IOException", t);
        Assert.Contains("OptimizationTool.Engine.Scans.RunAsync Microsoft.Data.Sqlite.SqliteException", t);
    }

    [Fact]
    public void Message_with_a_rejected_token_is_left_out()
    {
        Write("host.log", $"{At(TimeSpan.FromMinutes(1))} host 1:1 ERROR scan: failed [E-SCN-003]",
            "  System.IO.IOException: Could not open C:\\Users\\jamie\\file.txt",
            "     at OptimizationTool.Engine.Scans.RunAsync() in src/Engine/Scans.cs:line 142");
        var t = Build().Text;
        Assert.Contains("\n  IOException\n", t);
        Assert.DoesNotContain("Could not open", t);
    }

    [Fact]
    public void Recent_lines_are_last_24_hours_newest_30_without_date()
    {
        Write("host.log", $"{At(TimeSpan.FromHours(25))} host 1:1 ERROR rpc: too old marker");
        Write("host.log", Enumerable.Range(0, 40).Select(i => $"{At(TimeSpan.FromMinutes(100 - i))} host 1:1 WARN rpc: line{i:D2} x").ToArray());
        Write("mcp.log", $"{At(TimeSpan.FromSeconds(10))} mcp 1:1 INFO bridge: info is not listed");
        Write("desktop.log.1", $"{At(TimeSpan.FromSeconds(5))} desktop 1:1 ERROR webview: newest");
        var t = Build().Text;
        var recent = t[(t.IndexOf(ProblemReport.RecentHeader, StringComparison.Ordinal) + ProblemReport.RecentHeader.Length + 1)..].Split('\n');
        Assert.Equal(30, recent.Length);
        Assert.DoesNotContain("too old", t);
        Assert.DoesNotContain("info is not listed", t);
        Assert.DoesNotContain("line10", t);
        Assert.StartsWith("  ", recent[0]);
        Assert.Contains("line11", recent[0]);
        Assert.EndsWith("desktop ERROR webview: newest", recent[^1]);
        Assert.Matches(@"^  \d{2}:\d{2}:\d{2} desktop ERROR", recent[^1]);
        Assert.DoesNotContain("2026-", t);
    }

    [Theory]
    [InlineData("scan", "scan")]
    [InlineData("crash", "crash")]
    [InlineData("benchmark", "benchmark")]
    [InlineData("ai", "ai")]
    [InlineData("ai_task", "ai_task")]
    [InlineData("my photos", "other")]
    [InlineData("SCAN", "other")]
    [InlineData(null, "other")]
    public void Operation_is_allowlisted(string? op, string expected)
    {
        var r = Build(op);
        Assert.Contains($"\nOperation: {expected}\n", r.Text);
        Assert.Equal("Problem: " + expected, r.Title);
    }

    [Fact]
    public void Caps_line_length_and_report_size()
    {
        var longMsg = string.Concat(Enumerable.Repeat("word ", 200));
        Write("host.log", Enumerable.Range(0, 30).Select(i => $"{At(TimeSpan.FromMinutes(60 - i))} host 1:1 WARN rpc: {longMsg}").ToArray());
        var frames = Enumerable.Range(0, 60).Select(i => $"     at OptimizationTool.Engine.M{i}() in src/Engine/{new string('A', 200)}.cs:line {i}");
        Write("desktop.log", new[] { $"{At(TimeSpan.FromMinutes(1))} desktop 1:1 ERROR x: y", "  System.Exception: boom" }.Concat(frames).ToArray());
        var t = Build().Text;
        Assert.True(Encoding.UTF8.GetByteCount(t) <= ProblemReport.MaxBytes);
        Assert.All(t.Split('\n'), l => Assert.True(l.Length <= ProblemReport.MaxLine, l));
        Assert.True(t.Split('\n').Count(l => l.StartsWith("    at ", StringComparison.Ordinal)) <= ProblemReport.MaxFrames);
    }

    [Fact]
    public void Works_without_logs()
    {
        Directory.Delete(_data, true);
        var r = Build();
        Assert.StartsWith("Optimus 0.9.0 · Windows 10.0.26200 · x64\nOperation: scan\n", r.Text);
        Assert.EndsWith(ProblemReport.RecentHeader + "\n  none", r.Text);
        Assert.Equal("Problem: scan", r.Title);
        Assert.Contains("Operation: other", ProblemReport.Build(null, "0.9.0", "10.0.26200", "x64", null, Ids, Now).Text);
    }

    [Fact]
    public void Crash_marker_is_named()
    {
        File.WriteAllText(Path.Combine(Logs, "crash-desktop.json"),
            """{"process":"desktop","at":"2026-11-18T11:58:00.000Z","type":"NullReferenceException","site":"E-UNH-002","version":"0.9.0"}""");
        var r = Build("crash");
        Assert.Contains("\nCrash: desktop at 11:58:00 · E-UNH-002\n", r.Text);
        Assert.Equal("Problem: crash E-UNH-002", r.Title);
    }

    [Fact]
    public void Reading_does_not_block_a_writer()
    {
        var path = Path.Combine(Logs, "host.log");
        using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var bytes = Encoding.UTF8.GetBytes($"{At(TimeSpan.FromMinutes(1))} host 1:1 WARN rpc: held open\n");
        writer.Write(bytes);
        writer.Flush();
        Assert.Contains("held open", Build().Text);
        writer.Write(bytes); // still writable
    }

    [Fact]
    public void Reads_only_the_tail_of_a_big_log()
    {
        var filler = string.Concat(Enumerable.Repeat($"{At(TimeSpan.FromHours(30))} host 1:1 INFO x: filler\n", 12000));
        File.WriteAllText(Path.Combine(Logs, "host.log"), $"{At(TimeSpan.FromMinutes(9))} host 1:1 WARN rpc: at the head\n" + filler
            + $"{At(TimeSpan.FromMinutes(1))} host 1:1 WARN rpc: at the tail\n");
        var t = Build().Text;
        Assert.Contains("at the tail", t);
        Assert.DoesNotContain("at the head", t);
    }
}

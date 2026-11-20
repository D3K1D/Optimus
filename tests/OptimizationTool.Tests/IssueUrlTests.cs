using OptimizationTool.Transport.Logging;

namespace OptimizationTool.Tests;

/// <summary>docs/error-logging.md §2 "The GitHub URL".</summary>
public sealed class IssueUrlTests
{
    private static Dictionary<string, string> Query(Uri url) =>
        url.AbsoluteUri[(url.AbsoluteUri.IndexOf('?') + 1)..].Split('&')
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

    private static string Report(int recent, int frames, int lineLength = 60)
    {
        var lines = new List<string> { "Optimus 0.9.0 · Windows 10.0.26200 · x64", "Operation: scan", "", "Last error", "  InvalidOperationException" };
        for (var i = 0; i < frames; i++) lines.Add($"    at OptimizationTool.Engine.F{i:D2} in src/Engine/F.cs:{i}");
        lines.Add("");
        lines.Add("Recent warnings and errors");
        for (var i = 0; i < recent; i++) lines.Add($"  10:00:{i % 60:D2} host WARN rpc: line{i:D3} " + new string('x', lineLength));
        return string.Join('\n', lines);
    }

    [Fact]
    public void Host_path_and_template_are_exact()
    {
        var (url, trimmed) = IssueUrl.Build("Problem: scan", "r");
        Assert.False(trimmed);
        Assert.Equal("https", url.Scheme);
        Assert.Equal("github.com", url.Host);
        Assert.Equal("/D3K1D/Optimus/issues/new", url.AbsolutePath);
        Assert.StartsWith("https://github.com/D3K1D/Optimus/issues/new?template=bug_report.yml&title=", url.AbsoluteUri);
        Assert.Equal("bug_report.yml", Query(url)["template"]);
    }

    [Fact]
    public void Special_characters_round_trip()
    {
        const string title = "Problem: a&b #1 + c=d";
        const string report = "line 1 & 2\n#hash + plus = eq ? q\r\nÜnïcode · 日本 😀 %20";
        var (url, trimmed) = IssueUrl.Build(title, report);
        Assert.False(trimmed);
        var q = Query(url);
        Assert.Equal(title, q["title"]);
        Assert.Equal(report, q["report"]);
        Assert.Equal(3, q.Count);
        Assert.DoesNotContain(" ", url.AbsoluteUri);
        Assert.DoesNotContain("\n", url.AbsoluteUri);
    }

    [Fact]
    public void Under_the_limit_is_unchanged()
    {
        var report = Report(10, 5);
        var (url, trimmed) = IssueUrl.Build("Problem: scan", report);
        Assert.False(trimmed);
        Assert.Equal(report, Query(url)["report"]);
        Assert.DoesNotContain(IssueUrl.TrimNote, Query(url)["report"]);
    }

    [Fact]
    public void Over_the_limit_drops_oldest_log_lines_first()
    {
        var report = Report(30, 20, 120);
        var (url, trimmed) = IssueUrl.Build("Problem: scan", report, 4000);
        Assert.True(trimmed);
        Assert.True(url.AbsoluteUri.Length <= 4000);
        var text = Query(url)["report"];
        Assert.EndsWith("\n" + IssueUrl.TrimNote, text);
        Assert.Contains("line029", text);           // newest kept
        Assert.DoesNotContain("line000", text);     // oldest gone
        Assert.Contains("F19 in", text);            // frames untouched while log lines remain
    }

    [Fact]
    public void Then_frames_from_the_bottom_then_a_hard_cut()
    {
        var report = Report(30, 20, 120);
        var (url, _) = IssueUrl.Build("Problem: scan", report, 1500);
        var text = Query(url)["report"];
        Assert.DoesNotContain("rpc: line", text);
        Assert.Contains("F00 in", text);
        Assert.DoesNotContain("F19 in", text);
        Assert.True(url.AbsoluteUri.Length <= 1500);

        var (cut, trimmed) = IssueUrl.Build("Problem: scan", new string('é', 5000), 600);
        Assert.True(trimmed);
        Assert.True(cut.AbsoluteUri.Length <= 600);
        Assert.EndsWith(IssueUrl.TrimNote, Query(cut)["report"]);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(777)]
    [InlineData(2000)]
    [InlineData(8000)]
    public void Never_over_max_length(int max)
    {
        var (url, _) = IssueUrl.Build("Problem: scan " + new string('日', 40), Report(30, 20, 200) + "😀😀😀", max);
        Assert.True(url.AbsoluteUri.Length <= max, $"{url.AbsoluteUri.Length} > {max}");
    }
}

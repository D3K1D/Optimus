using System.Security.Cryptography;
using System.Text;
using OptimizationTool.Engine.Actions.Scripts;

namespace OptimizationTool.Tests.Scripts;

/// <summary>m9 §5.1 rules 3–4 (one serialisation, plain text only, refused never trimmed), the transcript bounds and the output scrub.</summary>
public class ScriptTextTests
{
    private static (string Rule, int? Line) Refusal(string s)
    {
        var r = ScriptText.Serialise(s);
        Assert.False(r.Ok);
        return (r.Rule!, r.Line);
    }

    [Fact]
    public void One_exact_form_BOM_and_CRLF_and_the_hash_is_over_those_bytes()
    {
        var lf = ScriptText.Serialise("a\n\tb\n");
        var crlf = ScriptText.Serialise("a\r\n\tb\r\n");
        var cr = ScriptText.Serialise("a\r\tb\r");
        Assert.True(lf.Ok);
        Assert.Equal("a\r\n\tb\r\n", lf.Text);
        Assert.Equal(["a", "\tb"], lf.Lines);
        Assert.Equal([0xEF, 0xBB, 0xBF], lf.Bytes[..3]);
        Assert.Equal(lf.Bytes, crlf.Bytes);
        Assert.Equal(lf.Bytes, cr.Bytes);
        Assert.Equal(3 + lf.Text.Length, lf.Bytes.Length);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData([0xEF, 0xBB, 0xBF, .. Encoding.ASCII.GetBytes("a\r\n\tb\r\n")])), lf.Sha256);
        Assert.Matches("^[0-9a-f]{64}$", lf.Sha256);
        // One changed byte: another hash.
        Assert.NotEqual(lf.Sha256, ScriptText.Serialise("a\n\tc\n").Sha256);
    }

    [Fact]
    public void Anything_but_plain_ASCII_and_the_limits_is_refused_with_its_rule_and_line()
    {
        Assert.Equal(("non_ascii", 2), Refusal("ok\ncaf\u00e9"));
        Assert.Equal(("non_ascii", 3), Refusal("a\nb\n\u202eevil"));   // bidi override
        Assert.Equal(("non_ascii", 1), Refusal("a\u200bb"));          // zero-width
        Assert.Equal(("non_ascii", 1), Refusal("x\u0007"));           // control
        Assert.Equal(("non_ascii", 1), Refusal("Write-Output \u2018x\u2019")); // look-alike quotes
        Assert.Equal(("non_ascii", 1), Refusal("Get-Pr\u043Ecess"));  // Cyrillic o
        Assert.Equal(("non_ascii", 1), Refusal("\uFEFFGet-Process"));  // a BOM in the text
        Assert.True(ScriptText.Serialise(new string('x', 200)).Ok);
        Assert.Equal(("line_too_long", 2), Refusal("ok\n" + new string('x', 201)));
        Assert.True(ScriptText.Serialise(string.Join('\n', Enumerable.Repeat("x", 200))).Ok);
        Assert.Equal(("too_many_lines", 201), Refusal(string.Join('\n', Enumerable.Repeat("x", 201))));
        var big = ScriptText.Serialise(string.Join('\n', Enumerable.Repeat(new string('y', 150), 60)));
        Assert.Equal(("too_large", (int?)null), (big.Rule!, big.Line));
        Assert.Equal(("empty", (int?)null), Refusal("   \n\t"));
        Assert.Equal(("empty", (int?)null), Refusal(""));
    }

    [Fact]
    public void The_transcript_keeps_the_first_MiB_tagged_in_arrival_order_and_counts_the_rest()
    {
        var t = new ScriptTranscript();
        t.Add(false, "one");
        t.Add(true, "two");
        t.Add(false, "three\0x");
        Assert.Equal("[out] one\n[err] two\n[out] three?x\n", t.Text);
        Assert.Equal(3, t.Lines);
        Assert.False(t.Truncated);

        var big = new ScriptTranscript();
        var line = new string('z', 1000);
        for (var i = 0; i < 1100; i++) big.Add(i % 2 == 1, line);
        Assert.True(big.Truncated);
        Assert.Equal(1100, big.Lines);
        Assert.True(Encoding.UTF8.GetByteCount(big.Text) <= ScriptTranscript.MaxBytes);
        Assert.EndsWith("\n", big.Text);
        Assert.Equal(40, big.Tail.Count); // the AI's excerpt still sees the last lines
        Assert.All(big.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries), l => Assert.Matches(@"^\[(out|err)\] ", l));
    }

    private static readonly ScrubIdentity Sam = new(["Sam"], ["DESKTOP-SAM7", "WORKGROUP"], [@"C:\Users\Sam"]);
    private const string ScriptPath = @"C:\Users\Sam\AppData\Local\OptimizationTool\script-runs\r7k2xq\script.ps1";

    [Theory]
    [InlineData(@"[out] at C:\Users\Sam\AppData\Local\OptimizationTool\script-runs\r7k2xq\script.ps1:4 char:1", "[out] at script.ps1:4 char:1")]
    [InlineData(@"[out] Checked C:\Windows\SoftwareDistribution\Download", "[out] Checked <path>")]
    [InlineData(@"[out] HKLM:\SOFTWARE\X Sam", "[out] <path> <user>")]
    [InlineData(@"[out] share \\server\share\x.txt ok", "[out] share <path> ok")]
    [InlineData("[out] see https://example.com/a?b=c done", "[out] see <path> done")]
    [InlineData("[out] on DESKTOP-SAM7 as Sam in WORKGROUP", "[out] on <pc> as <user> in <pc>")]
    [InlineData("[out] samples: 3", "[out] samples: 3")] // not a whole-word match
    [InlineData("[err] Access is denied.", "[err] Access is denied.")]
    public void The_scrub_replaces_paths_and_names(string line, string expected) => Assert.Equal(expected, ScriptOutputScrub.Line(line, ScriptPath, Sam));

    [Theory]
    [InlineData("[out] password=hunter2")]
    [InlineData("[out] token: abcdef")]
    [InlineData("[out] connected to 192.168.1.20")]
    [InlineData("[out] mail sam@example.com")]
    [InlineData("[out] {6c5d7f2a-1111-2222-3333-444455556666}")]
    [InlineData("[out] S-1-5-21-1111-2222-3333-1001")]
    [InlineData("[out] bell\u0007")]
    public void Lines_that_still_look_sensitive_are_dropped(string line) => Assert.Null(ScriptOutputScrub.Line(line, ScriptPath, Sam));

    [Fact]
    public void The_excerpt_is_the_last_40_lines_or_4KB_and_counts_what_it_leaves_out()
    {
        var x = ScriptOutputScrub.Excerpt([$"[out] at {ScriptPath} on DESKTOP-SAM7", @"[out] HKLM:\SOFTWARE\X Sam", "[out] " + new string('z', 400)], 3, ScriptPath, Sam);
        Assert.Equal(("[out] at script.ps1 on <pc>\n[out] <path> <user>", 1), x);

        var many = Enumerable.Range(1, 100).Select(i => $"[out] line {i}").ToList();
        var tail = many.Skip(60).ToList();
        var e = ScriptOutputScrub.Excerpt(tail, 100, null, Sam);
        Assert.Equal(40, e.Excerpt.Split('\n').Length);
        Assert.StartsWith("[out] line 61", e.Excerpt);
        Assert.Equal(60, e.OmittedLines);

        var wide = Enumerable.Range(1, 40).Select(i => "[out] " + new string((char)('a' + i % 26), 250)).ToList();
        var w = ScriptOutputScrub.Excerpt(wide, 40, null, Sam);
        Assert.True(Encoding.UTF8.GetByteCount(w.Excerpt) <= ScriptOutputScrub.MaxBytes);
        Assert.Equal(40 - w.Excerpt.Split('\n').Length, w.OmittedLines);
    }
}

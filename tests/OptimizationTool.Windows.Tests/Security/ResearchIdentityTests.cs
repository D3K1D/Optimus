using OptimizationTool.Engine;
using OptimizationTool.Windows.Security;
using OptimizationTool.Windows.Security.Collectors;
using OptimizationTool.Windows.Security.Common;

namespace OptimizationTool.Windows.Tests.Security;

/// <summary>
/// m9 §6.1–§6.2: the local-only inputs of research. Threat resources keep only file names; the identity reader only
/// rebuilds paths under the whitelisted roots. Synthetic values; no file on this PC is read.
/// </summary>
public sealed class ResearchIdentityTests
{
    [Fact]
    public void Threat_resources_keep_only_file_names_of_file_resources()
    {
        object[] raw = [@"file:_C:\Users\bob\Downloads\bad.exe", "process:_pid:4242,ProcessStart:1", @"regkeyvalue:_HKCU\Software\X", @"file:_C:\Temp\BAD.EXE", 7];
        Assert.Equal(["bad.exe"], DefenderCollector.ResourceFiles(raw));
        Assert.Null(DefenderCollector.ResourceFiles(@"file:_C:\x.exe")); // a single string is not a list
        Assert.Null(DefenderCollector.ResourceFiles(null));
    }

    [Fact]
    public void Threat_evidence_carries_the_names_but_no_path()
    {
        ThreatDetectionRaw[] det = [new(10, 1, 0, new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc), null, ["bad.exe"])];
        ThreatRaw[] thr = [new(10, "Trojan:Win32/Example.A", 8, 5, false)];
        var status = new DefenderStatusRaw("Normal", true, true, true, "1.0", new DateTime(2026, 6, 14, 0, 0, 0, DateTimeKind.Utc), 1, null, null, true);
        var r = new DefenderCollector(new CollectorOptions()).Normalize(new DefenderRaw(status, null, det, thr, null, Elevated: true, Truncated: false));
        var threat = r.Facts.Single(f => f.Subject.StartsWith("security.defender.threat.", StringComparison.Ordinal));
        Assert.Equal("""["bad.exe"]""", threat.Value["resourceFiles"]!.ToJsonString());
        Assert.DoesNotContain("Users", threat.Value.ToJsonString());
    }

    [Theory]
    [InlineData(@"%ProgramFiles%\App\app.exe", @"C:\Program Files\App\app.exe")]
    [InlineData(@"%LOCALAPPDATA%\Steam\steam.exe", @"C:\Users\someone\AppData\Local\Steam\steam.exe")]
    [InlineData("\u2026\\app.exe", null)]
    [InlineData("app.exe", null)]
    [InlineData(@"%UNKNOWN%\app.exe", null)]
    [InlineData(@"%ProgramFiles%\..\Windows\x.exe", null)]
    [InlineData(@"\\server\share\x.exe", null)]
    public void Identity_paths_are_rebuilt_only_under_known_roots(string evidencePath, string? expected)
    {
        var roots = new PathRoots([("ProgramFiles", @"C:\Program Files"), ("LOCALAPPDATA", @"C:\Users\someone\AppData\Local"), ("SystemRoot", @"C:\Windows")]);
        Assert.Equal(expected, new WindowsFileIdentity(roots, _ => null).Resolve(evidencePath));
    }
}

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using OptimizationTool.Transport;
using OptimizationTool.Transport.Logging;

namespace OptimizationTool.Tests;

/// <summary>The shared logger (docs/error-logging.md §1): scrubbing, line format, dev.log switch, rotation, code locations.</summary>
public class AppLogTests
{
    private static readonly LogScrubber Pat = new(@"C:\Users\Pat", "pat", "BOX-7", "CONTOSO");

    private static string TempDir() => Directory.CreateTempSubdirectory("otlog-").FullName;

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static AppLog Open(string dir, bool dev, string process = "host", TextWriter? echo = null) =>
        AppLog.Open(process, dir, dev, echo, makeCurrent: false, scrubber: Pat);

    private static int Line([CallerLineNumber] int line = 0) => line;

    // ---- scrubbing ----

    [Fact]
    public void Scrubber_hides_profile_user_pc_and_domain()
    {
        Assert.Equal(@"client rejected: user <pc>\<user> image %USERPROFILE%\app\x.exe",
            Pat.Scrub(@"client rejected: user BOX-7\pat image C:\users\pat\app\x.exe"));
        Assert.Equal(@"logon <domain>\<user>", Pat.Scrub(@"logon contoso\Pat"));
    }

    [Fact]
    public void Scrubber_matches_whole_words_only()
    {
        var shortName = new LogScrubber(@"C:\Users\an", "an", "PC1", "CORP");
        Assert.Equal("scan finished; <user> left", shortName.Scrub("scan finished; an left"));
        // The domain is a whole token only.
        Assert.Equal("CORPORATE <domain>", shortName.Scrub("CORPORATE corp"));
    }

    [Fact]
    public void Scrubber_hides_sids_inside_pipe_names_and_emails()
    {
        Assert.Equal("listening on OptimizationTool.v1.<sid>",
            Pat.Scrub("listening on OptimizationTool.v1.S-1-5-21-3623811015-3361044348-30300820-1013"));
        Assert.Equal("owner <sid> mail <email>.", LogScrubber.None.Scrub("owner S-1-5-18 mail first.last+x@mail.example.co.uk."));
    }

    [Fact]
    public void Scrubbing_applies_to_messages_and_exceptions_in_both_files()
    {
        var dir = TempDir();
        try
        {
            var log = Open(dir, dev: true);
            log.Error(@"open C:\Users\Pat\x failed for pat@example.com", ex: new InvalidOperationException("user BOX-7\\pat"));
            foreach (var path in new[] { AppLog.PathFor(dir, "host"), AppLog.DevPathFor(dir) })
            {
                var text = File.ReadAllText(path);
                Assert.Contains(@"open %USERPROFILE%\x failed for <email>", text);
                Assert.Contains(@"user <pc>\<user>", text);
                Assert.DoesNotContain("Pat", text, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally { Cleanup(dir); }
    }

    // ---- line format ----

    [Fact]
    public void Normal_file_lines_have_time_process_pid_tid_level_site_and_location_on_warn()
    {
        var dir = TempDir();
        try
        {
            var log = Open(dir, dev: false);
            log.Info("rpc: scan.start ok 12ms");
            var warnLine = Line(); log.Warn("rpc: scan.start failed -32603", "E-HST-001");
            var lines = File.ReadAllLines(AppLog.PathFor(dir, "host"));
            Assert.Equal(2, lines.Length);
            Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z host \d+:\d+ INFO rpc: scan\.start ok 12ms$", lines[0]);
            Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z host \d+:\d+ WARN rpc: scan\.start failed -32603 \[E-HST-001\] "
                + $@"@tests/OptimizationTool\.Tests/AppLogTests\.cs:{warnLine}$", lines[1]);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Dev_log_lines_carry_location_and_member_on_every_level()
    {
        var dir = TempDir();
        try
        {
            var log = Open(dir, dev: true);
            var infoLine = Line(); log.Info("lifecycle: start");
            var line = File.ReadAllLines(AppLog.DevPathFor(dir)).Single();
            Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z host \d+:\d+ INFO lifecycle: start "
                + $@"@tests/OptimizationTool\.Tests/AppLogTests\.cs:{infoLine} {nameof(Dev_log_lines_carry_location_and_member_on_every_level)}$", line);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Exceptions_follow_indented_two_spaces_with_mapped_paths()
    {
        var dir = TempDir();
        try
        {
            var log = Open(dir, dev: false);
            log.Error("rpc: boom", ex: Thrown());
            var lines = File.ReadAllLines(AppLog.PathFor(dir, "host"));
            Assert.Contains(" ERROR rpc: boom @", lines[0]);
            Assert.StartsWith("  System.InvalidOperationException: inner", lines[1]);
            Assert.All(lines.Skip(1), l => Assert.StartsWith("  ", l));
            var text = string.Join('\n', lines);
            Assert.Contains("tests/OptimizationTool.Tests/AppLogTests.cs:line", text);
            Assert.DoesNotContain("/_/", text);
        }
        finally { Cleanup(dir); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static InvalidOperationException Thrown()
    {
        try { throw new InvalidOperationException("inner"); }
        catch (InvalidOperationException ex) { return ex; }
    }

    [Fact]
    public async Task A_src_exception_from_this_build_points_to_src_and_no_build_machine_path()
    {
        // Embedded PDBs + PathMap (Directory.Build.props): a Transport frame reads src/Transport/Frame.cs:line N.
        var ex = await Assert.ThrowsAsync<FrameException>(() => Frame.ReadAsync(new MemoryStream([1, 2])));
        var text = AppLog.Disabled.ExceptionText(ex);
        Assert.Matches(@"in src/Transport/Frame\.cs:line \d+", text);
        Assert.DoesNotContain(@":\", text);
        Assert.DoesNotContain("Users", text);
    }

    [Fact]
    public void Echo_gets_the_normal_lines()
    {
        var echo = new StringWriter();
        var log = AppLog.Open("host", null, devLog: false, echo, makeCurrent: false, scrubber: Pat);
        log.Info("start 0.9.0");
        log.Debug("rpc: hidden");
        Assert.Contains(" host ", echo.ToString());
        Assert.Contains(" INFO start 0.9.0", echo.ToString());
        Assert.DoesNotContain("hidden", echo.ToString());
    }

    // ---- dev.log switch ----

    [Fact]
    public void Debug_goes_only_to_dev_log()
    {
        var dir = TempDir();
        try
        {
            var log = Open(dir, dev: true);
            Assert.True(log.DevEnabled);
            log.Debug("rpc: scan.start ok 3ms");
            log.Info("rpc: visible");
            Assert.DoesNotContain("scan.start", File.ReadAllText(AppLog.PathFor(dir, "host")));
            Assert.Contains(" DEBUG rpc: scan.start ok 3ms @", File.ReadAllText(AppLog.DevPathFor(dir)));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Nothing_is_written_to_dev_log_when_off()
    {
        var dir = TempDir();
        try
        {
            var log = Open(dir, dev: false);
            Assert.False(log.DevEnabled);
            log.Debug("rpc: x");
            log.Error("rpc: y");
            Assert.False(File.Exists(AppLog.DevPathFor(dir)));
            Assert.Single(File.ReadAllLines(AppLog.PathFor(dir, "host")));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Dev_log_is_shared_by_processes()
    {
        var dir = TempDir();
        try
        {
            var host = Open(dir, dev: true, "host");
            var desktop = Open(dir, dev: true, "desktop");
            Parallel.For(0, 200, i => (i % 2 == 0 ? host : desktop).Info($"rpc: n{i}"));
            var lines = File.ReadAllLines(AppLog.DevPathFor(dir));
            Assert.Equal(200, lines.Length);
            Assert.Equal(100, lines.Count(l => l.Contains(" desktop ", StringComparison.Ordinal)));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Switch_is_on_by_flag_environment_or_marker()
    {
        var dir = TempDir();
        try
        {
            Assert.False(DevLogSwitch.IsOn(["--source", "fixture"], dir, null));
            Assert.True(DevLogSwitch.IsOn(["--dev-log"], dir, null));
            Assert.True(DevLogSwitch.IsOn([], dir, "1"));
            Assert.False(DevLogSwitch.IsOn([], dir, "0"));
            Assert.False(DevLogSwitch.IsOn(null, null, null));
            File.WriteAllText(DevLogSwitch.MarkerPath(dir), "");
            Assert.True(DevLogSwitch.IsOn([], dir, null));
            Assert.EndsWith(@"\dev-log.on", DevLogSwitch.MarkerPath(dir));
        }
        finally { Cleanup(dir); }
    }

    // 0.9.1 on the user's PC: the marker was made by a shell inside a packaged (MSIX) app, so Windows put it in that package's
    // redirected AppData and the installed host logged only "dev-log=False". The start line now says which source switched
    // the log on, or why it is off, and names a package whose redirected copy holds a marker.
    [Fact]
    public void Switch_explains_its_source_or_why_it_is_off()
    {
        var local = TempDir();
        try
        {
            var data = Path.Combine(local, "OptimizationTool");
            Directory.CreateDirectory(data);
            Assert.Equal("off (no --dev-log, OPTIMUS_DEV_LOG unset, no dev-log.on in the data folder)", DevLogSwitch.Explain([], data, null, local));
            Assert.Equal("off (no --dev-log, OPTIMUS_DEV_LOG not 1, no dev-log.on in the data folder)", DevLogSwitch.Explain([], data, "0", local));
            Assert.Equal("off (no --dev-log, OPTIMUS_DEV_LOG unset, no data folder)", DevLogSwitch.Explain(null, null, null, local));
            Assert.Equal("on (flag)", DevLogSwitch.Explain(["--dev-log"], data, null, local));
            Assert.Equal("on (env)", DevLogSwitch.Explain([], data, " 1 ", local));

            // A marker only in a package's redirected copy: still off (the switch stays strict), and the line names the package.
            var redirected = Path.Combine(local, "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Local", "OptimizationTool");
            Directory.CreateDirectory(Path.Combine(local, "Packages", "Other_123", "LocalCache", "Local"));
            Directory.CreateDirectory(redirected);
            File.WriteAllText(Path.Combine(redirected, "dev-log.on"), "");
            Assert.False(DevLogSwitch.IsOn([], data, null));
            Assert.Equal("off (no --dev-log, OPTIMUS_DEV_LOG unset, no dev-log.on in the data folder); a dev-log.on is in the redirected AppData "
                + "of package Claude_pzs8sxrjxfjjc, which Optimus doesn't read", DevLogSwitch.Explain([], data, null, local));
            // Only the default data folder (directly under local AppData) is looked up; a --data-dir elsewhere is not.
            Assert.Null(DevLogSwitch.RedirectedMarkerPackage(Path.Combine(local, "x", "OptimizationTool"), local));
            Assert.Null(DevLogSwitch.RedirectedMarkerPackage(data, Path.Combine(local, "missing")));

            File.WriteAllText(DevLogSwitch.MarkerPath(data), "");
            Assert.Equal("on (marker)", DevLogSwitch.Explain([], data, null, local));
        }
        finally { Cleanup(local); }
    }

    // ---- rotation ----

    [Fact]
    public void Process_file_rotates_at_open()
    {
        var dir = TempDir();
        try
        {
            var path = AppLog.PathFor(dir, "host");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[AppLog.ProcessRotateBytes + 1]);
            Open(dir, dev: false).Info("fresh");
            Assert.Equal(AppLog.ProcessRotateBytes + 1, new FileInfo(path + ".1").Length);
            Assert.Single(File.ReadAllLines(path));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Process_file_rotates_while_running()
    {
        var dir = TempDir();
        try
        {
            var log = Open(dir, dev: false);
            var path = AppLog.PathFor(dir, "host");
            log.Info("first");
            using (var fs = new FileStream(path, FileMode.Append)) fs.Write(new byte[AppLog.ProcessRotateBytes]);
            var chunk = new string('x', 1000);
            for (var i = 0; i < 300; i++) log.Info(chunk); // past 256 KiB written: checked, rotated
            Assert.True(File.Exists(path + ".1"));
            Assert.True(new FileInfo(path).Length < AppLog.ProcessRotateBytes);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Dev_log_rotates_keeping_three()
    {
        var dir = TempDir();
        try
        {
            var path = AppLog.DevPathFor(dir);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[AppLog.DevRotateBytes + 1]);
            File.WriteAllText(path + ".1", "one");
            File.WriteAllText(path + ".2", "two");
            File.WriteAllText(path + ".3", "three");
            Open(dir, dev: true).Info("fresh");
            Assert.Single(File.ReadAllLines(path));
            Assert.Equal(AppLog.DevRotateBytes + 1, new FileInfo(path + ".1").Length);
            Assert.Equal("one", File.ReadAllText(path + ".2"));
            Assert.Equal("two", File.ReadAllText(path + ".3"));
            Assert.False(File.Exists(path + ".4"));
        }
        finally { Cleanup(dir); }
    }

    // ---- never throws, Disabled, Current ----

    [Fact]
    public void An_unwritable_folder_never_throws()
    {
        var log = AppLog.Open("host", @"Z:\no\such\drive\:bad", devLog: true, new StringWriter(), makeCurrent: false, scrubber: Pat);
        log.Error("still fine", ex: new InvalidOperationException());
        AppLog.Disabled.Error("nothing");
        Assert.False(AppLog.Disabled.DevEnabled);
    }

    // ---- code locations ----

    [Theory]
    [InlineData("/_/src/Engine/Agent/AgentService.cs", "src/Engine/Agent/AgentService.cs")]
    [InlineData(@"/_/src\Host\HostServer.cs", "src/Host/HostServer.cs")]
    [InlineData(@"C:\work\repo\src\Host\HostServer.cs", "src/Host/HostServer.cs")]
    [InlineData("/home/x/src/a/src/Mcp/Program.cs", "src/Mcp/Program.cs")]
    [InlineData(@"C:\elsewhere\Thing.cs", "Thing.cs")]
    [InlineData("", "?")]
    public void CodeLocation_maps_compiler_paths(string input, string expected) =>
        Assert.Equal(expected, CodeLocation.From(input));

    // ---- crash markers ----

    [Fact]
    public void CrashMarker_round_trip_and_clear()
    {
        var dir = TempDir();
        try
        {
            Assert.Empty(CrashMarker.ReadAll(dir));
            CrashMarker.Write(dir, "host", new InvalidOperationException("secret message"), ErrorSites.HostUnhandled);
            CrashMarker.Write(dir, "desktop", "exit 0xE0434352", null);
            var json = File.ReadAllText(CrashMarker.PathFor(dir, "host"));
            Assert.DoesNotContain("secret", json);
            var all = CrashMarker.ReadAll(dir);
            Assert.Equal(2, all.Count);
            var host = all.Single(c => c.Process == "host");
            Assert.Equal("InvalidOperationException", host.Type);
            Assert.Equal("E-UNH-001", host.Site);
            Assert.Matches(@"^\d+\.\d+\.\d+$", host.Version);
            Assert.True((DateTimeOffset.UtcNow - host.At).Duration() < TimeSpan.FromMinutes(1));
            Assert.Null(all.Single(c => c.Process == "desktop").Site);
            CrashMarker.ClearAll(dir);
            Assert.Empty(CrashMarker.ReadAll(dir));
            CrashMarker.Write(@"Z:\no\such\drive\:bad", "host", "x", null); // never throws
        }
        finally { Cleanup(dir); }
    }

    // ---- error sites ----

    private static readonly string[] Areas = ["RES", "CHK", "PRV", "ACT", "FIX", "SCR", "BEN", "SCN", "HST", "APP", "MCP", "BRK", "UNH"];

    [Fact]
    public void ErrorSites_are_unique_and_well_formed()
    {
        var constants = typeof(ErrorSites).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.Equal(constants.Count, constants.Distinct().Count());
        Assert.Equal(constants.Order(), ErrorSites.All.Keys.Order());
        foreach (var (id, site) in ErrorSites.All)
        {
            Assert.Matches(@"^E-[A-Z]{3}-\d{3}$", id);
            Assert.Equal(id, site.Id);
            Assert.Equal(id.Substring(2, 3), site.Area);
            Assert.Contains(site.Area, Areas);
            Assert.False(string.IsNullOrWhiteSpace(site.Summary));
        }
    }

    [Fact]
    public void Every_error_site_id_in_src_is_in_the_table()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "OptimizationTool.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var missing = new List<string>();
        var found = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root.FullName, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(file), "\"(E-[A-Z]{3}-\\d{3})\""))
            {
                found++;
                if (!ErrorSites.All.ContainsKey(m.Groups[1].Value))
                    missing.Add($"{m.Groups[1].Value} in {Path.GetRelativePath(root.FullName, file)}");
            }
        }
        Assert.True(found >= ErrorSites.All.Count);
        Assert.Empty(missing);
    }
}

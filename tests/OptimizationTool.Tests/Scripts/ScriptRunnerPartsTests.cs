using System.Text;
using Microsoft.Win32;
using OptimizationTool.Engine.Actions.Scripts;
using OptimizationTool.Windows.Actions.Scripts;

namespace OptimizationTool.Tests.Scripts;

/// <summary>
/// The real runner's parts that can be checked without starting anything (m9 §5.4): arguments, the from-scratch
/// environment, the policy read (fake registry), the staged file (held, re-hashed, swap caught), stream draining and the
/// parse output reader. The runner and parser themselves start PowerShell and are only covered by the Live test below,
/// which this suite never runs.
/// </summary>
public class ScriptRunnerPartsTests
{
    [Fact]
    public void Arguments_are_structured_and_Bypass_only_when_asked()
    {
        Assert.Equal(["-NoProfile", "-NonInteractive", "-InputFormat", "None", "-File", @"C:\d\script-runs\ab\script.ps1"],
            PowerShellInterpreter.Arguments(@"C:\d\script-runs\ab\script.ps1", bypassPolicy: false));
        Assert.Equal(["-NoProfile", "-NonInteractive", "-InputFormat", "None", "-ExecutionPolicy", "Bypass", "-File", @"C:\x y\script.ps1"],
            PowerShellInterpreter.Arguments(@"C:\x y\script.ps1", bypassPolicy: true));
        Assert.Equal(@"""C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"" -NoProfile -File ""C:\x y\script.ps1""",
            PowerShellInterpreter.CommandLine(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", ["-NoProfile", "-File", @"C:\x y\script.ps1"]));
        Assert.Equal(@"""a"" ""b\"" c"" d\", PowerShellInterpreter.CommandLine("a", ["b\" c", @"d\"]));
        Assert.Equal(@"C:\Windows\System32\WindowsPowerShell\v1.0", PowerShellInterpreter.FolderOf(@"C:\Windows\System32"));
    }

    [Fact]
    public void The_environment_is_built_from_scratch_nothing_inherited()
    {
        string Folder(Environment.SpecialFolder f) => f switch
        {
            Environment.SpecialFolder.Windows => @"C:\Windows",
            Environment.SpecialFolder.ProgramFiles => @"C:\Program Files",
            Environment.SpecialFolder.UserProfile => @"C:\Users\Sam",
            Environment.SpecialFolder.CommonDesktopDirectory => @"C:\Users\Public\Desktop",
            _ => @"C:\Other",
        };
        var env = PowerShellInterpreter.Environment(@"C:\d\script-runs\ab", @"C:\Windows\System32", Folder, "Sam", "DESKTOP-SAM7", "DESKTOP-SAM7", 8, true);
        Assert.Equal(@"C:\Windows\System32;C:\Windows\System32\Wbem;C:\Windows\System32\WindowsPowerShell\v1.0", env["PATH"]);
        Assert.Equal(@"C:\Program Files\WindowsPowerShell\Modules;C:\Windows\System32\WindowsPowerShell\v1.0\Modules", env["PSModulePath"]);
        Assert.Equal(@"C:\d\script-runs\ab", env["TEMP"]);
        Assert.Equal(@"C:\d\script-runs\ab", env["TMP"]);
        Assert.Equal(@"C:\Windows", env["SystemRoot"]);
        Assert.Equal("C:", env["SystemDrive"]);
        Assert.Equal(@"\Users\Sam", env["HOMEPATH"]);
        // Nothing from this process: no profiler, startup hook, module path or any other variable it happens to have.
        Environment.SetEnvironmentVariable("OT_SCRIPT_TEST_LEAK", "leak");
        try
        {
            var real = PowerShellInterpreter.Environment(@"C:\d\x");
            Assert.DoesNotContain("OT_SCRIPT_TEST_LEAK", real.Keys);
            foreach (var bad in new[] { "COR_ENABLE_PROFILING", "COR_PROFILER", "CORECLR_ENABLE_PROFILING", "DOTNET_STARTUP_HOOKS", "PSExecutionPolicyPreference", "__PSLockdownPolicy" })
                Assert.DoesNotContain(bad, real.Keys);
            Assert.Equal(@"C:\d\x", real["TEMP"]);
        }
        finally { Environment.SetEnvironmentVariable("OT_SCRIPT_TEST_LEAK", null); }
        Assert.Equal("A=1\0b=2\0\0", ScriptProcess.Block(new Dictionary<string, string> { ["b"] = "2", ["A"] = "1", ["bad=name"] = "x" }));
    }

    private static ScriptPolicyState Policy(Dictionary<(RegistryHive, string, string), object> values) =>
        new ScriptPolicyReader((h, k, n) => values.TryGetValue((h, k, n), out var v) ? v : null).Read();

    [Fact]
    public void The_policy_read_is_managed_by_a_Group_Policy_and_bypass_only_when_the_local_policy_refuses()
    {
        const string P = ScriptPolicyReader.PolicyKey, S = ScriptPolicyReader.ShellKey;
        Assert.Equal(new ScriptPolicyState(true, false), Policy(new() { [(RegistryHive.LocalMachine, P, "EnableScripts")] = 0 }));
        Assert.Equal(new ScriptPolicyState(true, false), Policy(new() { [(RegistryHive.CurrentUser, P, "ExecutionPolicy")] = "RemoteSigned" }));
        Assert.Equal(new ScriptPolicyState(false, true), Policy(new()));                                   // none set: Restricted
        Assert.Equal(new ScriptPolicyState(false, false), Policy(new() { [(RegistryHive.CurrentUser, S, "ExecutionPolicy")] = "RemoteSigned" }));
        Assert.Equal(new ScriptPolicyState(false, true), Policy(new() { [(RegistryHive.LocalMachine, S, "ExecutionPolicy")] = "AllSigned" }));
        Assert.Equal(new ScriptPolicyState(false, false), Policy(new()
        {
            [(RegistryHive.CurrentUser, S, "ExecutionPolicy")] = "Undefined", [(RegistryHive.LocalMachine, S, "ExecutionPolicy")] = "Unrestricted",
        }));
        Assert.Equal(new ScriptPolicyState(false, true), Policy(new()
        {
            [(RegistryHive.CurrentUser, S, "ExecutionPolicy")] = "Restricted", [(RegistryHive.LocalMachine, S, "ExecutionPolicy")] = "Bypass",
        }));
    }

    private static string TempRoot() => Path.Combine(Path.GetTempPath(), "ot-tests", Guid.NewGuid().ToString("N"), "script-runs");

    [Fact]
    public void The_staged_file_is_held_and_rehashed_and_a_changed_or_swapped_file_is_not_run()
    {
        var root = TempRoot();
        try
        {
            var ser = ScriptText.Serialise("Get-Date\n");
            var (staged, problem) = ScriptStage.Prepare(root, ser.Bytes, ser.Sha256);
            Assert.Null(problem);
            using (staged)
            {
                Assert.StartsWith(Path.GetFullPath(root), staged!.Folder, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(ser.Bytes, File.ReadAllBytes(staged.ScriptPath)); // readable by others
                Assert.ThrowsAny<IOException>(() => new FileStream(staged.ScriptPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose()); // never writable
                Assert.ThrowsAny<IOException>(() => File.Delete(staged.ScriptPath));                                                                 // never deletable
            }
            // Changed between writing and running: the re-hash refuses it.
            var changed = ScriptStage.Prepare(root, ser.Bytes, ser.Sha256, afterWrite: p => File.AppendAllText(p, "Start-Process x\r\n"));
            Assert.Equal((null, "hash_mismatch"), (changed.Staged, changed.Problem));
            // Swapped for another file with the same bytes: its identity differs.
            var swapped = ScriptStage.Prepare(root, ser.Bytes, ser.Sha256, afterWrite: p => { File.Delete(p); File.WriteAllBytes(p, ser.Bytes); });
            Assert.Equal((null, "hash_mismatch"), (swapped.Staged, swapped.Problem));
            // Bytes that are not the approved ones.
            Assert.Equal("hash_mismatch", ScriptStage.Prepare(root, ser.Bytes, new string('0', 64)).Problem);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(root)!, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task Streams_are_drained_into_bounded_lines()
    {
        var lines = new List<string>();
        var text = "one\r\ntwo\n" + new string('x', 10) + "\nlast";
        await ScriptProcess.DrainAsync(new MemoryStream(Encoding.ASCII.GetBytes(text)), Encoding.ASCII, lines.Add, maxLine: 4);
        Assert.Equal(["one", "two", "xxxx", "xxxx", "xx", "last"], lines);
    }

    [Fact]
    public void The_parse_command_only_parses_and_its_output_is_read_strictly()
    {
        Assert.Contains("[System.Management.Automation.Language.Parser]::ParseFile({0}", PowerShellParser.Command);
        Assert.DoesNotContain("Invoke-Expression", PowerShellParser.Command);
        Assert.DoesNotContain("& $", PowerShellParser.Command);
        var parse = PowerShellParser.Read("""
            {"commands":[{"name":"bcdedit","op":"","line":2,"args":["/set",null,"@p"],"pipeline":0},{"name":null,"op":"&","line":3,"args":[],"pipeline":1}],
             "members":[{"member":"Invoke","static":false,"type":null,"invoked":true,"line":4}],
             "types":[{"name":"System.Reflection.Assembly","line":5}],
             "errors":[{"line":6,"code":"MissingEndCurlyBrace"}]}
            """);
        Assert.NotNull(parse);
        Assert.Equal(["/set", null, "@p"], parse.Commands[0].Arguments);
        Assert.Null(parse.Commands[1].Name);
        Assert.Equal("Invoke", parse.Members[0].Member);
        Assert.Equal(("System.Reflection.Assembly", 5), parse.Types[0]);
        Assert.Equal((6, "MissingEndCurlyBrace"), parse.Errors[0]);
        Assert.Null(PowerShellParser.Read("not json"));
        Assert.Null(PowerShellParser.Read("""{"commands":[{"name":1}]}"""));
        Assert.Empty(PowerShellParser.Read("{}")!.Commands);
    }

    /// <summary>
    /// The real runner and parser on a harmless script. Category Live: excluded from the suite (it starts PowerShell). For a
    /// VM or the lead's live pass only.
    /// </summary>
    [Fact]
    [Trait("Category", "Live")]
    public async Task Live_the_real_runner_and_parser_run_a_harmless_script()
    {
        var root = TempRoot();
        var ser = ScriptText.Serialise("Write-Output 'hello'\nWrite-Error 'warn'\nexit 3\n");
        var parse = await new PowerShellParser(root).ParseAsync(ser.Bytes, default);
        Assert.NotNull(parse);
        Assert.Contains(parse!.Commands, c => c.Name == "Write-Output");
        var lines = new List<(bool, string)>();
        var observer = new ListObserver(lines);
        var result = await new WindowsScriptRunner().RunAsync(new ScriptRunRequest("op-live", ser.Bytes, ser.Sha256, TimeSpan.FromMinutes(1), true, root), observer, default);
        Assert.True(result.Exited);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains((false, "hello"), lines);
    }

    private sealed class ListObserver(List<(bool, string)> lines) : IScriptRunObserver
    {
        public void Started(DateTimeOffset at, string runFolder) { }
        public void Line(bool stderr, string text) { lock (lines) lines.Add((stderr, text)); }
    }
}

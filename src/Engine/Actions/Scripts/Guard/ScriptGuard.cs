using static OptimizationTool.Engine.Actions.Scripts.Guard.GuardRules;

namespace OptimizationTool.Engine.Actions.Scripts.Guard;

/// <summary>One guard result: a rule code (§5.7), the line it was found on (null for the whole script) and, for a warning, its card text.</summary>
public sealed record GuardFinding(string Rule, int? Line, string? Text = null);

public sealed record GuardResult(IReadOnlyList<GuardFinding> Refused, IReadOnlyList<GuardFinding> Warnings)
{
    public string Version => ScriptGuard.Version;
}

/// <summary>
/// ScriptGuard (contracts/m9-ai-powers.md §5.3), version sg-1. It screens the exact approved text for recognised patterns:
/// it refuses the hard-limit families (tuning and fan tools, boot and disk destruction, download-and-run, code built or
/// loaded at run time, other interpreters) and turns the rest of what it recognises into visible warnings. Two layers feed
/// the same rule tables: a small C# tokenizer over the text (always), and PowerShell's own parse when the host has it
/// (<see cref="ScriptParse"/>; a parse error is a refusal). Without the parse the host refuses the script
/// (<see cref="Unchecked"/>) unless it was told to accept the first layer alone.
/// <para><b>It refuses recognised patterns. It does not prove a script safe</b>, and a determined script can get past it.</para>
/// </summary>
public static class ScriptGuard
{
    public const string Version = "sg-1";

    public const string TuningTool = "tuning_tool", BootOrDisk = "boot_or_disk", DownloadRun = "download_run", DynamicCode = "dynamic_code",
        OtherInterpreter = "other_interpreter";

    /// <summary>PowerShell's parser was not available and the host requires it: the script could not be screened fully.</summary>
    public const string Unchecked = "unchecked";

    public const string SecurityOff = "security_off", DeletesFiles = "deletes_files", StartsProgram = "starts_program", ChangesService = "changes_service",
        SendsData = "sends_data", SystemRegistry = "system_registry", Permissions = "permissions", ExecutionPolicy = "execution_policy";

    public static readonly string[] RefuseRules = [TuningTool, BootOrDisk, DownloadRun, DynamicCode, OtherInterpreter, Unchecked];
    public static readonly string[] WarnRules = [SecurityOff, DeletesFiles, StartsProgram, ChangesService, SendsData, SystemRegistry, Permissions, ExecutionPolicy];

    /// <summary>Screens <paramref name="text"/> (the approved text, CRLF). <paramref name="parse"/> is PowerShell's parse of the same bytes, or null.</summary>
    public static GuardResult Check(string text, ScriptParse? parse, bool requireParser)
    {
        var acc = new Acc();
        var lexer = PsLexer.Run(text);
        foreach (var (line, _) in lexer.Refusals) acc.Refuse(DynamicCode, line);
        Evaluate(acc, PsAnalyzer.Run(lexer.Tokens));
        if (parse is not null)
        {
            Evaluate(acc, PsAnalyzer.FromParse(parse));
            foreach (var (line, _) in parse.Errors) acc.Refuse(DynamicCode, line > 0 ? line : null);
        }
        else if (requireParser) acc.Refuse(Unchecked, null);
        return acc.Result();
    }

    private sealed class Acc
    {
        private readonly List<GuardFinding> _refused = [];
        private readonly List<GuardFinding> _warnings = [];

        public void Refuse(string rule, int? line)
        {
            if (!_refused.Any(r => r.Rule == rule && r.Line == line)) _refused.Add(new GuardFinding(rule, line));
        }

        public void Warn(string rule, int line, string text)
        {
            if (!_warnings.Any(w => w.Rule == rule && w.Line == line)) _warnings.Add(new GuardFinding(rule, line, text));
        }

        public GuardResult Result() => new(
            [.. _refused.OrderBy(r => r.Line ?? int.MaxValue).ThenBy(r => Array.IndexOf(RefuseRules, r.Rule))],
            [.. _warnings.OrderBy(w => w.Line).ThenBy(w => Array.IndexOf(WarnRules, w.Rule))]);
    }

    private static void Evaluate(Acc acc, GuardInput input)
    {
        // Constant text anywhere (not comments): tuning and fan tools, processor power settings, protection bypasses.
        foreach (var (text, line) in input.Literals)
        {
            if (text.Length == 0) continue;
            var squashed = Squash(text);
            if (TuningWords.Any(w => squashed.Contains(w, StringComparison.Ordinal)) || TuningWholeWords.IsMatch(text)) acc.Refuse(TuningTool, line);
            var lower = text.ToLowerInvariant();
            if (BypassWords.Any(w => lower.Contains(w, StringComparison.Ordinal))) acc.Refuse(DynamicCode, line);
        }

        foreach (var (name, line) in input.Variables)
        {
            var n = name.ToLowerInvariant();
            if (n == "executioncontext" || n.StartsWith("function:", StringComparison.Ordinal) || n.StartsWith("alias:", StringComparison.Ordinal)
                || n.Contains("__pslockdownpolicy", StringComparison.Ordinal))
                acc.Refuse(DynamicCode, line);
        }

        foreach (var (name, line) in input.Types) CheckType(acc, TypeName(name), line);

        foreach (var m in input.Members)
        {
            if (m.Name is null) { acc.Refuse(DynamicCode, m.Line); continue; } // a member whose name is computed
            var name = m.Name.ToLowerInvariant();
            if (DynamicMembers.Contains(name)) acc.Refuse(DynamicCode, m.Line);
            if (DownloadMembers.Contains(name)) acc.Refuse(DownloadRun, m.Line);
            if (UploadMembers.Contains(name)) acc.Warn(SendsData, m.Line, "Sends or fetches data over the network");
            if (m.Static && m.Invoked)
            {
                var type = m.Type is null ? null : TypeName(m.Type);
                if (type is null || !StaticAllowlist.Contains(type)) acc.Refuse(DynamicCode, m.Line);
                else if (type is "io.file" or "io.directory" && FileWriteMembers.Contains(name)) acc.Warn(DeletesFiles, m.Line, "Deletes or overwrites files");
                else if (type == "environment" && name == "setenvironmentvariable") acc.Warn(SystemRegistry, m.Line, "Changes environment variables");
            }
            else if (m.Invoked && name == "delete") acc.Warn(DeletesFiles, m.Line, "Deletes something");
        }

        var registryAnywhere = input.Literals.Any(l => ProtectedRegistry.Any(p => Norm(l.Text).Contains(p, StringComparison.Ordinal)));
        var policyAnywhere = input.Literals.Any(l => PolicyRegistry.Any(p => Norm(l.Text).Contains(p, StringComparison.Ordinal)));
        var securityValueAnywhere = input.Literals.Any(l => SecurityValueNames.Contains(l.Text.Trim()));
        foreach (var c in input.Commands) CheckCommand(acc, c, registryAnywhere, policyAnywhere, securityValueAnywhere);

        // A pipeline that feeds what it fetched into something that runs it.
        foreach (var group in input.Commands.GroupBy(c => c.Pipeline))
        {
            var list = group.ToList();
            var source = list.FindIndex(IsWebSource);
            if (source >= 0 && list.Skip(source + 1).Any(IsExecutor)) acc.Refuse(DownloadRun, list[source].Line);
        }

        // Deleting restore points through WMI: a shadow-copy query anywhere and a removal (Remove-CimInstance, .Delete()).
        if (input.Literals.Any(l => l.Text.Contains("shadowcopy", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var c in input.Commands.Where(c => c.Name is not null && Canonical(c.Name) is "remove-ciminstance" or "remove-wmiobject"))
                acc.Refuse(BootOrDisk, c.Line);
            foreach (var m in input.Members.Where(m => m.Invoked && m.Name is { } n && n.Equals("delete", StringComparison.OrdinalIgnoreCase)))
                acc.Refuse(BootOrDisk, m.Line);
        }

        foreach (var target in input.Redirects) CheckWriteTarget(acc, target, target.Line);
    }

    private static void CheckType(Acc acc, string type, int line)
    {
        if (TypeIn(type, DynamicTypes)) acc.Refuse(DynamicCode, line);
        if (TypeIn(type, DownloadTypes)) acc.Refuse(DownloadRun, line);
        if (TypeIn(type, SendTypes)) acc.Warn(SendsData, line, "Sends or fetches data over the network");
    }

    private static bool IsWebSource(GCmd c) =>
        c.Name is not null && (WebCommands.Contains(Canonical(c.Name)) || (Canonical(c.Name) is "curl" or "wget"));

    private static bool IsExecutor(GCmd c)
    {
        if (c.Name is null) return c.Op != "";
        var canon = Canonical(c.Name);
        return DynamicCommands.Contains(canon) || Interpreters.Contains(Base(c.Name)) || c.Op == ".";
    }

    private static List<string> Lits(GCmd c) => [.. c.Args.Where(a => a.Literal is not null).Select(a => a.Literal!.ToLowerInvariant())];

    /// <summary>The value after a parameter (by a prefix of its name of at least <paramref name="min"/> characters), or null.</summary>
    private static GArg? ParamValue(GCmd c, string full, int min = 3)
    {
        for (var i = 0; i < c.Args.Count - 1; i++)
            if (c.Args[i] is { Param: true } p && p.Literal!.Length >= min && full.StartsWith(p.Literal, StringComparison.OrdinalIgnoreCase))
                return c.Args[i + 1];
        return null;
    }

    /// <summary>
    /// A native tool's switch value (schtasks /TR …): "/name value", "-name value", "/name:value" or "-name:value", the name
    /// matched without case among the arguments. Native switches are words, not PowerShell parameters, so
    /// <see cref="ParamValue"/> never sees them. Returns (true, value) when the switch is present; a value that isn't a
    /// literal (or a switch with nothing after it) comes back as an argument with a null Literal.
    /// </summary>
    private static (bool Found, GArg? Value) SwitchValue(GCmd c, string name)
    {
        for (var i = 0; i < c.Args.Count; i++)
        {
            var a = c.Args[i];
            var text = a.Literal ?? a.Raw.Trim().Trim('"', '\'');
            if (text.Length < name.Length + 1 || text[0] is not ('/' or '-')) continue;
            var body = text[1..];
            if (body.Equals(name, StringComparison.OrdinalIgnoreCase))
                return (true, a.Literal is null ? new GArg(null, a.Raw, a.Line) : i + 1 < c.Args.Count ? c.Args[i + 1] : new GArg(null, "", a.Line));
            if (body.Length > name.Length && body[name.Length] == ':' && body.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                return (true, new GArg(a.Literal is null ? null : body[(name.Length + 1)..].Trim('"', '\''), a.Raw, a.Line));
        }
        return (false, null);
    }

    private static bool HasParam(GCmd c, string full, int min = 3) =>
        c.Args.Any(a => a.Param && a.Literal!.Length >= min && full.StartsWith(a.Literal, StringComparison.OrdinalIgnoreCase));

    /// <summary>Parameters of a launcher that take a value (so the word after them is not the program).</summary>
    private static readonly string[] LauncherValueParams =
        ["-argumentlist", "-args", "-workingdirectory", "-windowstyle", "-verb", "-credential", "-redirectstandardinput", "-redirectstandardoutput",
         "-redirectstandarderror", "-name", "-scriptblock", "-initializationscript", "-inputobject", "-psversion", "-argument", "-id", "-trigger"];

    /// <summary>What a launcher (Start-Process, Invoke-Item, Start-Job -FilePath, a task action) starts: its program argument.</summary>
    private static GArg? LaunchTarget(GCmd c, string canon)
    {
        foreach (var name in canon switch
                 {
                     "start-job" or "start-threadjob" => new[] { "-filepath" },
                     "new-scheduledtaskaction" or "register-scheduledtask" => ["-execute"],
                     _ => ["-filepath", "-path", "-literalpath", "-pspath"],
                 })
            if (ParamValue(c, name, 3) is { } v) return v;
        if (canon is "start-job" or "start-threadjob" or "new-scheduledtaskaction" or "register-scheduledtask") return null;
        for (var i = 0; i < c.Args.Count; i++)
        {
            var a = c.Args[i];
            if (a.Param) continue;
            if (i > 0 && c.Args[i - 1] is { Param: true } p && LauncherValueParams.Any(v => v.StartsWith(p.Literal!, StringComparison.OrdinalIgnoreCase) && p.Literal!.Length >= 3)) continue;
            return a;
        }
        return null;
    }

    /// <summary>A program name a launcher or a task starts: refused when it names an interpreter, a script file or a boot/disk tool.</summary>
    private static void CheckLaunched(Acc acc, string text, int line)
    {
        foreach (var part in text.Split([' ', '\t', ',', '"', '\''], StringSplitOptions.RemoveEmptyEntries))
        {
            var lower = part.ToLowerInvariant();
            if (Interpreters.Contains(Base(part))) acc.Refuse(OtherInterpreter, line);
            if (PsScriptExtensions.Any(e => lower.EndsWith(e, StringComparison.Ordinal))) acc.Refuse(DynamicCode, line);
            if (OtherScriptExtensions.Any(e => lower.EndsWith(e, StringComparison.Ordinal))) acc.Refuse(OtherInterpreter, line);
            if (DiskCommands.Contains(Base(part)) && Base(part) != "format") acc.Refuse(BootOrDisk, line);
        }
    }

    private static void CheckWriteTarget(Acc acc, GArg target, int line)
    {
        var raw = target.Raw.ToLowerInvariant();
        if (raw is "$null" or "null" || raw.StartsWith("$env:temp", StringComparison.Ordinal) || raw.StartsWith("$env:tmp", StringComparison.Ordinal)) return;
        if (target.Literal is null || UserFolders.Any(u => Norm(raw).Contains(u, StringComparison.Ordinal)))
            acc.Warn(DeletesFiles, line, "Overwrites or empties a file");
    }

    private static void CheckCommand(Acc acc, GCmd c, bool registryAnywhere, bool policyAnywhere, bool securityValueAnywhere)
    {
        var line = c.Line;
        if (c.Block) return; // & { … }: the block's own statements are analysed
        if (c.Name is null)
        {
            if (c.Op != "") acc.Refuse(DynamicCode, line); // & $x, & ("a"+"b"), . $path: the card can't show what runs
            return;
        }
        if (c.Op == ".") acc.Refuse(DynamicCode, line); // dot-sourcing runs a file (or a command) in this scope
        var raw = c.Name;
        var lowerRaw = raw.Trim('"', '\'').ToLowerInvariant();
        var canon = Canonical(raw);
        var b = Base(raw);
        var lits = Lits(c);

        if (PsScriptExtensions.Any(e => lowerRaw.EndsWith(e, StringComparison.Ordinal))) acc.Refuse(DynamicCode, line);
        if (OtherScriptExtensions.Any(e => lowerRaw.EndsWith(e, StringComparison.Ordinal))) acc.Refuse(OtherInterpreter, line);
        if (Interpreters.Contains(b)) acc.Refuse(OtherInterpreter, line);
        if (DynamicCommands.Contains(canon)) acc.Refuse(DynamicCode, line);
        if (DiskCommands.Contains(canon) && !(canon == "format" && raw.Contains('-'))) acc.Refuse(BootOrDisk, line);
        if (c.Args.Any(a => a.Splat)) acc.Refuse(DynamicCode, line);
        if (c.Args.Any(a => a.Param && a.Literal!.Length >= 4 && "-encodedcommand".StartsWith(a.Literal, StringComparison.OrdinalIgnoreCase))) acc.Refuse(DynamicCode, line);

        switch (canon)
        {
            case "vssadmin" when lits.Any(l => l is "delete" or "resize"):
            case "wbadmin" when lits.Contains("delete"):
            case "reagentc" when lits.Contains("/disable"):
            case "cipher" when lits.Any(l => l.StartsWith("/w", StringComparison.Ordinal)):
            case "fsutil" when lits.Any(l => l is "setzerodata" or "repair") || (lits.Contains("dirty") && lits.Contains("set")):
            case "mountvol" when lits.Any(l => l is "/d" or "/p"):
            case "manage-bde" when lits.Any(l => l is "-off" or "-forcerecovery" or "-fr" or "-lock"):
                acc.Refuse(BootOrDisk, line);
                break;
            case "wmic":
                if (lits.Contains("process") && lits.Any(l => l is "call" or "create")) acc.Refuse(OtherInterpreter, line);
                if (lits.Any(l => l.Contains("shadowcopy", StringComparison.Ordinal)) && lits.Contains("delete")) acc.Refuse(BootOrDisk, line);
                break;
            case "nvidia-smi" when c.Args.Any(a => a.Literal is { } l && NvidiaTuningSwitches.Contains(l.Split('=')[0])):
                acc.Refuse(TuningTool, line);
                break;
            case "certutil" when lits.Any(l => l is "-urlcache" or "/urlcache" or "-verifyctl" or "-decode" or "-decodehex" or "/decode" or "/decodehex"):
            case "bitsadmin":
            case "start-bitstransfer":
                acc.Refuse(DownloadRun, line);
                break;
            case "import-module":
                if (c.Args.Any(a => !a.Param && (a.Literal is null || IsPathLike(a.Literal) || a.Literal.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    || a.Literal.EndsWith(".psm1", StringComparison.OrdinalIgnoreCase) || a.Literal.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))))
                    acc.Refuse(DynamicCode, line);
                break;
            case "using" when lits.Any(l => l is "module" or "assembly"):
                acc.Refuse(DynamicCode, line);
                break;
            case "new-object":
                if (HasParam(c, "-comobject", 3)) acc.Refuse(DynamicCode, line);
                var typeArg = ParamValue(c, "-typename", 3) ?? c.Args.FirstOrDefault(a => !a.Param);
                if (typeArg is not null)
                {
                    if (typeArg.Literal is null) acc.Refuse(DynamicCode, line);
                    else CheckType(acc, TypeName(typeArg.Literal), line);
                }
                break;
            case "get-variable" or "set-variable" or "new-variable" when lits.Any(l => l.Contains("executioncontext", StringComparison.Ordinal) || l.StartsWith("function:", StringComparison.Ordinal)):
            case "set-item" or "new-item" when lits.Any(l => l.StartsWith("function:", StringComparison.Ordinal) || l.StartsWith("alias:", StringComparison.Ordinal)):
                acc.Refuse(DynamicCode, line);
                break;
            case "invoke-wmimethod" or "invoke-cimmethod" or "set-wmiinstance" or "set-ciminstance" or "new-ciminstance" or "remove-wmiobject" or "remove-ciminstance":
                if (lits.Any(l => l.Contains("win32_process", StringComparison.Ordinal))) acc.Refuse(OtherInterpreter, line);
                if (lits.Any(l => l.Contains("shadowcopy", StringComparison.Ordinal))) acc.Refuse(BootOrDisk, line);
                if (c.Args.Any(a => a.Literal is null && !a.Splat) && canon.StartsWith("invoke", StringComparison.Ordinal)) acc.Refuse(DynamicCode, line);
                break;
        }

        // Launchers: what they start must be a literal, and not an interpreter, a script file or a disk tool.
        if (canon is "start-process" or "invoke-item" or "start-job" or "start-threadjob" or "new-scheduledtaskaction" or "register-scheduledtask")
        {
            var target = LaunchTarget(c, canon);
            if (target is not null && target.Literal is null) acc.Refuse(DynamicCode, line);
            foreach (var a in c.Args.Where(a => !a.Param && a.Literal is not null)) CheckLaunched(acc, a.Literal!, line);
        }
        // A native tool that stores a command line to run later: the program it names is checked as a launcher's is
        // (schtasks /TR; its slash switches are read here, never as PowerShell parameters).
        if (canon == "schtasks" && SwitchValue(c, "tr") is (true, var tr))
        {
            if (tr?.Literal is null) acc.Refuse(DynamicCode, line);
            else CheckLaunched(acc, tr.Literal, line);
        }

        // Web requests: a download to a file is refused (what it fetched is not on the card); anything else is a warning.
        if (WebCommands.Contains(canon))
        {
            if (HasParam(c, "-outfile", 4)) acc.Refuse(DownloadRun, line);
            else acc.Warn(SendsData, line, "Sends or fetches data over the network");
        }
        if (canon is "curl" or "wget")
        {
            if (lits.Any(l => l is "-o" or "--output" or "--remote-name" or "-p" or "--directory-prefix" or "--output-document") || c.Args.Any(a => a.Literal == "-O"))
                acc.Refuse(DownloadRun, line);
            else acc.Warn(SendsData, line, "Sends or fetches data over the network");
        }
        if (SendCommands.Contains(canon)) acc.Warn(SendsData, line, "Sends or fetches data over the network");

        // ---------------- warnings
        var args = c.Args.Select(a => (a.Literal ?? a.Raw).ToLowerInvariant()).ToList();
        string[] securityNames = [.. args.Where(a => SecurityServices.Contains(a.Trim(',')))];

        if (canon == "set-mppreference" && c.Args.Any(a => a.Param && (a.Literal!.StartsWith("-disable", StringComparison.OrdinalIgnoreCase) || a.Literal.StartsWith("-excl", StringComparison.OrdinalIgnoreCase))))
            acc.Warn(SecurityOff, line, "Turns off a Windows security feature");
        if (canon == "add-mppreference" && c.Args.Any(a => a.Param && a.Literal!.StartsWith("-excl", StringComparison.OrdinalIgnoreCase)))
            acc.Warn(SecurityOff, line, "Excludes files from antivirus scanning");
        if (canon == "netsh" && ((lits.Contains("advfirewall") && lits.Any(l => l is "off" or "state=off")) || (lits.Contains("firewall") && lits.Any(l => l.Contains("disable", StringComparison.Ordinal)))))
            acc.Warn(SecurityOff, line, "Turns off a Windows security feature");
        if (canon == "set-netfirewallprofile" && HasParam(c, "-enabled", 3) && args.Any(a => a is "false" or "$false" or "0"))
            acc.Warn(SecurityOff, line, "Turns off a Windows security feature");
        if (canon is "disable-netfirewallrule" or "remove-netfirewallrule" or "set-netfirewallrule" or "new-netfirewallrule")
            acc.Warn(SecurityOff, line, "Changes the firewall");
        if (canon is "disable-bitlocker" or "suspend-bitlocker" or "clear-bitlockerautounlock" || (canon == "set-processmitigation" && HasParam(c, "-disable", 3)))
            acc.Warn(SecurityOff, line, "Turns off a Windows security feature");
        if ((ServiceCommands.Contains(canon) || canon is "sc.exe" or "net") && securityNames.Length > 0)
            acc.Warn(SecurityOff, line, "Turns off a Windows security feature");
        if (canon is "uninstall-windowsfeature" or "disable-windowsoptionalfeature" && args.Any(a => a.Contains("defender", StringComparison.Ordinal)))
            acc.Warn(SecurityOff, line, "Turns off a Windows security feature");
        if (RegistryWriteCommands.Contains(canon) && (args.Any(a => SecurityValueNames.Contains(a)) || (securityValueAnywhere && args.Any(a => a.StartsWith('$')))))
            acc.Warn(SecurityOff, line, "Turns off a Windows security feature");

        if (canon == "set-executionpolicy") acc.Warn(ExecutionPolicy, line, "Changes the PowerShell script policy");
        if (RegistryWriteCommands.Contains(canon) && (args.Any(a => PolicyRegistry.Any(p => Norm(a).Contains(p, StringComparison.Ordinal)))
                                                       || (policyAnywhere && c.Args.Any(a => a.Literal is null))))
            acc.Warn(ExecutionPolicy, line, "Changes the PowerShell script policy");

        if (ServiceCommands.Contains(canon)) acc.Warn(ChangesService, line, "Stops or changes a Windows service");
        if (canon == "sc.exe" && lits.Any(l => l is "config" or "create" or "delete" or "stop" or "failure" or "pause" or "sdset" or "privs" or "start"))
            acc.Warn(ChangesService, line, "Stops or changes a Windows service");
        if (canon == "net" && lits.Any(l => l is "stop" or "start" or "pause" or "continue"))
            acc.Warn(ChangesService, line, "Stops or changes a Windows service");
        if (TaskCommands.Contains(canon) || (canon == "schtasks" && lits.Any(l => l is "/create" or "/change" or "/delete" or "/end")))
            acc.Warn(ChangesService, line, "Creates or changes a scheduled task");
        if (DriverCommands.Contains(canon) || (canon == "dism" && lits.Any(l => l.Contains("driver", StringComparison.Ordinal))))
            acc.Warn(ChangesService, line, "Installs or removes a driver");
        if (NetworkCommands.Contains(canon)
            || (canon == "netsh" && lits.Contains("reset"))
            || (canon == "ipconfig" && lits.Any(l => l is "/release" or "/renew" or "/release6" or "/renew6"))
            || (canon == "route" && lits.Any(l => l is "add" or "delete" or "change" or "-f")))
            acc.Warn(ChangesService, line, "Resets or changes network settings");
        if (PowerCommands.Contains(canon)) acc.Warn(ChangesService, line, "Restarts or shuts down Windows");
        if (FileCommands.Contains(canon) && args.Any(a => StartupFolders.Any(s => Norm(a).Contains(s, StringComparison.Ordinal))))
            acc.Warn(ChangesService, line, "Adds or changes a startup entry");

        if (RemoveCommands.Contains(canon)) acc.Warn(DeletesFiles, line, "Deletes files or folders");
        if (WriteCommands.Contains(canon))
        {
            var target = ParamValue(c, canon == "out-file" ? "-filepath" : "-path", 2) ?? ParamValue(c, "-literalpath", 3)
                         ?? c.Args.FirstOrDefault(a => !a.Param);
            if (target is not null) CheckWriteTarget(acc, target, line);
        }
        foreach (var a in c.Args.Where(a => a.Param && a.Literal!.Length >= 4 && "-recurse".StartsWith(a.Literal, StringComparison.OrdinalIgnoreCase)))
            acc.Warn(DeletesFiles, a.Line, "Works on many files at once (-Recurse or a wildcard)");
        if (FileCommands.Contains(canon) && c.Args.Any(a => !a.Param && a.Literal is { } l && (l.Contains('*') || l.Contains('?'))))
            acc.Warn(DeletesFiles, line, "Works on many files at once (-Recurse or a wildcard)");

        if (StartCommands.Contains(canon)) acc.Warn(StartsProgram, line, "Starts something that isn't in this script");
        if (InstallCommands.Contains(canon) || (PackageManagers.Contains(canon) && lits.Any(l => l is "install" or "uninstall" or "upgrade" or "update" or "remove")))
            acc.Warn(StartsProgram, line, "Installs or removes software");
        if ((canon == "net" && lits.Contains("start")) || (canon == "sc.exe" && lits.Contains("start")) || (canon == "schtasks" && lits.Contains("/run")))
            acc.Warn(StartsProgram, line, "Starts something that isn't in this script");
        if ((IsPathLike(lowerRaw) || lowerRaw.EndsWith(".exe", StringComparison.Ordinal) || lowerRaw.EndsWith(".msi", StringComparison.Ordinal)
             || lowerRaw.EndsWith(".msc", StringComparison.Ordinal) || lowerRaw.EndsWith(".cpl", StringComparison.Ordinal)) && !SystemTools.Contains(canon) && !SystemTools.Contains(b))
            acc.Warn(StartsProgram, line, "Starts something that isn't in this script");

        if (RegistryWriteCommands.Contains(canon))
        {
            var touchesProtected = args.Any(a => ProtectedRegistry.Any(p => Norm(a).Contains(p, StringComparison.Ordinal)));
            var unknownPath = registryAnywhere && c.Args.Any(a => a.Literal is null);
            var blindImport = (canon == "reg" && lits.Any(l => l is "import" or "restore" or "load")) || canon is "regini" or "regedit";
            if (touchesProtected || unknownPath || blindImport) acc.Warn(SystemRegistry, line, "Changes a protected part of the registry");
        }

        if (AclCommands.Contains(canon)) acc.Warn(Permissions, line, "Changes who can open files");
        if (AccountCommands.Contains(canon) || (canon == "net" && lits.Any(l => l is "user" or "localgroup" or "accounts" or "group")))
            acc.Warn(Permissions, line, "Changes accounts or passwords");
        if (CredentialCommands.Contains(canon)) acc.Warn(Permissions, line, "Works with saved passwords or sign-in");
        if (RemoteCommands.Contains(canon)) acc.Warn(Permissions, line, "Opens remote access to this PC");
    }
}

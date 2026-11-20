using System.Text.RegularExpressions;

namespace OptimizationTool.Engine.Actions.Scripts.Guard;

/// <summary>
/// The guard's compiled tables (contracts/m9-ai-powers.md §5.3, version sg-1). They name what the guard recognises; they are
/// not a list of everything dangerous, and a script can do the same things in ways not listed here.
/// </summary>
internal static class GuardRules
{
    // ---------------------------------------------------------------- names

    /// <summary>Common aliases → the command they run (Windows PowerShell 5.1; note sc = Set-Content, not sc.exe).</summary>
    public static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["iex"] = "invoke-expression", ["icm"] = "invoke-command", ["iwr"] = "invoke-webrequest", ["curl"] = "invoke-webrequest",
        ["wget"] = "invoke-webrequest", ["irm"] = "invoke-restmethod", ["saps"] = "start-process", ["start"] = "start-process",
        ["sasv"] = "start-service", ["spsv"] = "stop-service", ["ri"] = "remove-item", ["rm"] = "remove-item", ["rmdir"] = "remove-item",
        ["del"] = "remove-item", ["erase"] = "remove-item", ["rd"] = "remove-item", ["sc"] = "set-content", ["clc"] = "clear-content",
        ["ac"] = "add-content", ["ni"] = "new-item", ["sp"] = "set-itemproperty", ["si"] = "set-item", ["rp"] = "remove-itemproperty",
        ["cpi"] = "copy-item", ["copy"] = "copy-item", ["cp"] = "copy-item", ["mi"] = "move-item", ["move"] = "move-item", ["mv"] = "move-item",
        ["rni"] = "rename-item", ["ren"] = "rename-item", ["gwmi"] = "get-wmiobject", ["iwmi"] = "invoke-wmimethod", ["rwmi"] = "remove-wmiobject",
        ["swmi"] = "set-wmiinstance", ["ipmo"] = "import-module", ["sal"] = "set-alias", ["nal"] = "new-alias", ["ipal"] = "import-alias",
        ["ii"] = "invoke-item", ["sajb"] = "start-job", ["gv"] = "get-variable", ["sv"] = "set-variable", ["set"] = "set-variable",
        ["nv"] = "new-variable", ["nsn"] = "new-pssession", ["etsn"] = "enter-pssession", ["r"] = "invoke-history", ["ihy"] = "invoke-history",
        ["tee"] = "tee-object", ["foreach"] = "foreach-object", ["%"] = "foreach-object", ["where"] = "where-object", ["?"] = "where-object",
        ["gcm"] = "get-command", ["kill"] = "stop-process", ["spps"] = "stop-process",
    };

    /// <summary>Lower-case comparison form of a command name: backticks and carets dropped, the file name of a path, without .exe/.com.</summary>
    public static string Base(string name)
    {
        var n = name.Replace("`", "", StringComparison.Ordinal).Replace("^", "", StringComparison.Ordinal).Trim().Trim('"', '\'').ToLowerInvariant();
        var slash = n.LastIndexOfAny(['\\', '/']);
        if (slash >= 0) n = n[(slash + 1)..];
        if (n.EndsWith(".exe", StringComparison.Ordinal) || n.EndsWith(".com", StringComparison.Ordinal)) n = n[..^4];
        return n;
    }

    /// <summary>The canonical command a name runs: an alias resolved, else its base name. sc.exe stays "sc.exe".</summary>
    public static string Canonical(string name)
    {
        var raw = name.Replace("`", "", StringComparison.Ordinal).Replace("^", "", StringComparison.Ordinal).Trim().ToLowerInvariant();
        var b = Base(name);
        if (b == "sc" && raw.EndsWith(".exe", StringComparison.Ordinal)) return "sc.exe";
        if (raw.Contains('\\') || raw.Contains('/') || raw.EndsWith(".exe", StringComparison.Ordinal)) return b;
        return Aliases.TryGetValue(b, out var full) ? full : b;
    }

    public static bool IsPathLike(string name) => name.Contains('\\') || name.Contains('/');

    // ---------------------------------------------------------------- refuse families

    /// <summary>Starting another interpreter or a script host (other_interpreter).</summary>
    public static readonly HashSet<string> Interpreters = new(StringComparer.OrdinalIgnoreCase)
    {
        "powershell", "pwsh", "powershell_ise", "cmd", "wscript", "cscript", "mshta", "rundll32", "regsvr32", "bash", "wsl", "python", "python3",
        "pythonw", "py", "node", "perl", "ruby", "php", "java", "javaw", "msbuild", "installutil", "regasm", "regsvcs", "msxsl", "forfiles", "hh",
        "odbcconf", "pcalua", "syncappvpublishingserver", "ieexec", "msdt", "conhost", "cmstp", "scriptrunner", "runscripthelper", "mmc", "runas",
    };

    /// <summary>Script files a command may name (dynamic for PowerShell's own, another interpreter for the rest).</summary>
    public static readonly string[] PsScriptExtensions = [".ps1", ".psm1", ".psd1"];
    public static readonly string[] OtherScriptExtensions = [".bat", ".cmd", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".py", ".sh", ".scr"];

    /// <summary>Commands that build, load or run code the card does not show (dynamic_code).</summary>
    public static readonly HashSet<string> DynamicCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "invoke-expression", "invoke-command", "add-type", "set-alias", "new-alias", "import-alias", "new-module", "invoke-history",
        "new-pssession", "enter-pssession", "import-pssession", "register-pssessionconfiguration",
    };

    /// <summary>A type name matches a listed one exactly, or as its namespace (a listed "x." matches by prefix).</summary>
    public static bool TypeIn(string type, IEnumerable<string> list) =>
        list.Any(p => type == p.TrimEnd('.') || type.StartsWith(p.EndsWith('.') ? p : p + ".", StringComparison.Ordinal));

    /// <summary>Boot and disk tools (boot_or_disk), refused whatever their arguments.</summary>
    public static readonly HashSet<string> DiskCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "bcdedit", "bootrec", "diskpart", "bcdboot", "bootsect", "clear-disk", "initialize-disk", "remove-partition", "format-volume",
        "resize-partition", "set-partition", "set-disk", "remove-physicaldisk", "reset-physicaldisk", "remove-virtualdisk", "disable-computerrestore",
        "remove-computerrestorepoint", "format",
    };

    /// <summary>Tuning, fan and low-level hardware access, matched in any constant text (tuning_tool). Compared without spaces, dashes and underscores.</summary>
    public static readonly string[] TuningWords =
    [
        "ryzenmaster", "amdryzenmaster", "curveoptimizer", "precisionboostoverdrive", "throttlestop", "afterburner", "xtucli", "extremetuningutility",
        "ryzenadj", "ryzencontroller", "zenstates", "universalx86tuning", "precisionx", "gputweak", "winring0", "inpoutx64", "inpout32",
        "rtcore64", "rtcore32", "pawnio", "rwdrv", "nbfc", "notebookfancontrol", "speedfan", "fancontrol", "argusmonitor", "msacpi",
        "asusatkwmi", "atkwmi", "lenovogamezone", "softpowerplaytable", "powermizer", "overclock", "undervolt", "voltageoffset", "vcore",
        "fanspeed", "fancurve", "fanmode",
        // processor power settings (powercfg aliases and the subgroup GUID)
        "subprocessor", "procthrottlemax", "procthrottlemin", "perfboostmode", "perfboostpol", "perfepp", "cpmincores", "cpmaxcores", "procfreqmax",
        "idledisable", "perfincpol", "perfdecpol", "perfautonomous", "54533251",
    ];

    /// <summary>Whole words only (too short to match inside other names).</summary>
    public static readonly Regex TuningWholeWords = new(@"(?<![a-z0-9])(pbo|xtu|ec_?write|msr)(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>nvidia-smi switches that set clocks or power limits.</summary>
    public static readonly HashSet<string> NvidiaTuningSwitches = new(StringComparer.OrdinalIgnoreCase)
    {
        "-pl", "--power-limit", "-lgc", "--lock-gpu-clocks", "-lmc", "--lock-memory-clocks", "-ac", "--applications-clocks", "-rgc",
        "--reset-gpu-clocks", "-rmc", "--reset-memory-clocks", "-rac", "--reset-applications-clocks", "-cc", "--cuda-clocks",
    };

    /// <summary>Text that tries to switch off the script's own protections (AMSI, Constrained Language mode): dynamic_code.</summary>
    public static readonly string[] BypassWords = ["amsiutils", "amsiinitfailed", "amsiscanbuffer", "amsicontext", "__pslockdownpolicy", "system.management.automation.amsi"];

    /// <summary>Instance or static members that build, load or run code (dynamic_code).</summary>
    public static readonly HashSet<string> DynamicMembers = new(StringComparer.OrdinalIgnoreCase)
    {
        "invoke", "invokescript", "invokereturnasis", "begininvoke", "addscript", "addcommand", "newscriptblock", "expandstring", "getmethod",
        "getmethods", "getfield", "getfields", "invokemember", "createinstance", "getdelegateforfunctionpointer", "load", "loadfrom", "loadfile",
        "loadwithpartialname", "definedynamicassembly", "frombase64string", "frombase64chararray", "fromhexstring", "getnewclosure", "invokecommand",
        "executecommand", "shellexecute", "createobject", "getactiveobject",
    };

    /// <summary>Members that download (download_run).</summary>
    public static readonly HashSet<string> DownloadMembers = new(StringComparer.OrdinalIgnoreCase)
    {
        "downloadstring", "downloadfile", "downloaddata", "openread", "downloadstringasync", "downloadfileasync", "downloaddataasync",
        "downloadstringtaskasync", "downloadfiletaskasync", "downloaddatataskasync", "getstringasync", "getbytearrayasync", "getstreamasync",
    };

    /// <summary>Members that send data out (sends_data warning).</summary>
    public static readonly HashSet<string> UploadMembers = new(StringComparer.OrdinalIgnoreCase)
    {
        "uploadstring", "uploadfile", "uploaddata", "uploadvalues", "uploadstringasync", "uploadfileasync", "postasync", "putasync", "sendmailasync",
    };

    /// <summary>Types whose static methods may be called ([type]::Method). Anything else is refused (dynamic_code).</summary>
    public static readonly HashSet<string> StaticAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "math", "string", "char", "int", "int16", "int32", "int64", "long", "uint16", "uint32", "uint64", "double", "single", "float", "decimal",
        "bool", "boolean", "byte", "datetime", "datetimeoffset", "timespan", "guid", "version", "environment", "io.path", "io.directory", "io.file",
        "io.driveinfo", "regex", "text.regularexpressions.regex", "convert", "globalization.cultureinfo", "enum", "array", "console",
        "diagnostics.stopwatch", "threading.thread", "uri", "net.dns", "net.ipaddress", "security.principal.windowsidentity", "bitconverter",
    };

    /// <summary>Static methods of allowed types that still count: file changes warn; the rest of this list is refused elsewhere.</summary>
    public static readonly HashSet<string> FileWriteMembers = new(StringComparer.OrdinalIgnoreCase)
    {
        "delete", "writealltext", "writeallbytes", "writealllines", "appendalltext", "appendalllines", "move", "replace", "encrypt", "decrypt", "setattributes",
    };

    /// <summary>Type names (casts, New-Object, type literals) that build or run code (dynamic_code), by prefix after "system.".</summary>
    public static readonly string[] DynamicTypes =
    [
        "scriptblock", "powershell", "runspacefactory", "management.automation.runspaces", "management.automation.powershell",
        "management.automation.scriptblock", "management.automation.language", "management.automation.psmoduleinfo", "management.automation.commandinfo",
        "reflection.", "reflection", "appdomain", "activator", "delegate", "runtime.interopservices", "marshal", "wmiclass", "wmi", "type",
        "codedom", "microsoft.csharp", "linq.expressions", "dynamic", "componentmodel.typedescriptor",
    ];

    /// <summary>Types that download (download_run).</summary>
    public static readonly string[] DownloadTypes = ["net.webclient", "net.http.httpclient", "net.webrequest", "net.httpwebrequest", "net.filewebrequest", "net.ftpwebrequest"];

    /// <summary>Types that send data out (sends_data warning).</summary>
    public static readonly string[] SendTypes = ["net.mail", "net.sockets"];

    /// <summary>"system.x.y" → "x.y", lower, generic arguments and attribute arguments removed.</summary>
    public static string TypeName(string t)
    {
        var n = t.Trim().Replace("`", "", StringComparison.Ordinal).ToLowerInvariant();
        var cut = n.IndexOfAny(['[', '(', ',']);
        if (cut >= 0) n = n[..cut];
        n = n.Trim();
        if (n.StartsWith("system.", StringComparison.Ordinal)) n = n["system.".Length..];
        return n;
    }

    // ---------------------------------------------------------------- warning tables

    public static readonly HashSet<string> ServiceCommands = new(StringComparer.OrdinalIgnoreCase)
        { "stop-service", "set-service", "new-service", "restart-service", "remove-service", "suspend-service", "resume-service" };

    public static readonly HashSet<string> SecurityServices = new(StringComparer.OrdinalIgnoreCase)
        { "windefend", "mpssvc", "wscsvc", "securityhealthservice", "sense", "wdnissvc", "wdfilter", "wdboot", "bfe", "mpsdrv" };

    public static readonly HashSet<string> TaskCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "register-scheduledtask", "unregister-scheduledtask", "set-scheduledtask", "disable-scheduledtask", "enable-scheduledtask",
        "new-scheduledtask", "new-scheduledtaskaction", "new-scheduledtaskprincipal", "new-scheduledtasksettingsset",
    };

    public static readonly HashSet<string> DriverCommands = new(StringComparer.OrdinalIgnoreCase) { "pnputil", "add-windowsdriver", "remove-windowsdriver", "devcon" };

    public static readonly HashSet<string> NetworkCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "disable-netadapter", "enable-netadapter", "restart-netadapter", "remove-netipaddress", "new-netipaddress", "set-netipaddress",
        "set-dnsclientserveraddress", "set-netipinterface", "set-netadapter", "set-netadapteradvancedproperty", "remove-netroute", "new-netroute",
        "set-netconnectionprofile", "reset-netadapteradvancedproperty", "netcfg",
    };

    public static readonly HashSet<string> PowerCommands = new(StringComparer.OrdinalIgnoreCase) { "restart-computer", "stop-computer", "shutdown", "logoff" };

    public static readonly HashSet<string> InstallCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "msiexec", "wusa", "install-module", "install-package", "install-script", "update-module", "add-appxpackage", "add-appxprovisionedpackage",
        "remove-appxpackage", "install-windowsfeature", "uninstall-windowsfeature", "enable-windowsoptionalfeature", "disable-windowsoptionalfeature",
        "add-windowscapability", "remove-windowscapability", "add-windowspackage", "remove-windowspackage",
    };

    public static readonly HashSet<string> PackageManagers = new(StringComparer.OrdinalIgnoreCase) { "winget", "choco", "scoop" };

    public static readonly HashSet<string> StartCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "start-process", "start-service", "start-scheduledtask", "invoke-item", "start-job", "start-threadjob", "register-objectevent",
        "register-wmievent", "register-engineevent", "register-cimindicationevent",
    };

    public static readonly HashSet<string> WebCommands = new(StringComparer.OrdinalIgnoreCase) { "invoke-webrequest", "invoke-restmethod" };

    public static readonly HashSet<string> SendCommands = new(StringComparer.OrdinalIgnoreCase) { "send-mailmessage", "ftp", "tftp", "scp", "sftp" };

    public static readonly HashSet<string> RemoveCommands = new(StringComparer.OrdinalIgnoreCase) { "remove-item", "clear-content", "clear-recyclebin", "cleanmgr" };

    public static readonly HashSet<string> WriteCommands = new(StringComparer.OrdinalIgnoreCase) { "set-content", "out-file", "add-content", "tee-object" };

    public static readonly HashSet<string> FileCommands = new(StringComparer.OrdinalIgnoreCase)
        { "remove-item", "clear-content", "set-content", "out-file", "add-content", "copy-item", "move-item", "rename-item", "new-item", "tee-object" };

    public static readonly HashSet<string> RegistryWriteCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "set-itemproperty", "new-itemproperty", "new-item", "remove-item", "remove-itemproperty", "set-item", "rename-itemproperty",
        "copy-itemproperty", "move-itemproperty", "rename-item", "copy-item", "move-item", "reg", "regini", "regedit",
    };

    public static readonly HashSet<string> AclCommands = new(StringComparer.OrdinalIgnoreCase)
        { "icacls", "cacls", "xcacls", "takeown", "set-acl", "grant-smbshareaccess", "revoke-smbshareaccess", "block-smbshareaccess", "new-smbshare" };

    public static readonly HashSet<string> AccountCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "new-localuser", "set-localuser", "remove-localuser", "enable-localuser", "disable-localuser", "rename-localuser", "add-localgroupmember",
        "remove-localgroupmember", "new-localgroup", "remove-localgroup", "set-localgroup",
    };

    public static readonly HashSet<string> CredentialCommands = new(StringComparer.OrdinalIgnoreCase)
        { "cmdkey", "vaultcmd", "convertto-securestring", "convertfrom-securestring", "get-credential", "export-clixml" };

    public static readonly HashSet<string> RemoteCommands = new(StringComparer.OrdinalIgnoreCase) { "enable-psremoting", "set-wsmanquickconfig", "winrm", "set-item-wsman" };

    /// <summary>Windows tools that are part of the system: written as name.exe they are not "something this script doesn't contain".</summary>
    public static readonly HashSet<string> SystemTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "ipconfig", "netsh", "powercfg", "sc.exe", "reg", "schtasks", "net", "net1", "icacls", "takeown", "wevtutil", "dism", "sfc", "chkdsk",
        "cleanmgr", "pnputil", "fsutil", "whoami", "hostname", "tasklist", "taskkill", "systeminfo", "wmic", "gpupdate", "gpresult", "w32tm",
        "nslookup", "ping", "tracert", "pathping", "route", "arp", "netstat", "certutil", "cipher", "compact", "robocopy", "xcopy", "attrib",
        "find", "findstr", "sort", "more", "tree", "where", "timeout", "choice", "shutdown", "logoff", "query", "quser", "driverquery", "defrag",
        "label", "vol", "manage-bde", "auditpol", "secedit", "setx", "getmac", "nbtstat", "clip", "expand", "tar", "cmdkey", "openfiles",
        "typeperf", "logman", "relog", "vssadmin", "wbadmin", "reagentc", "mountvol", "bcdedit", "diskpart", "nvidia-smi", "msiexec", "wusa",
    };

    // ---------------------------------------------------------------- paths

    /// <summary>Registry places a script should not change unseen (system_registry), compared on a lower-case path with '\'.</summary>
    public static readonly string[] ProtectedRegistry =
    [
        @"\currentcontrolset\control", @"\currentcontrolset\services", @"\controlset001\", @"\controlset002\", @"\winlogon",
        @"\currentversion\run", @"\policies\", @"image file execution options", @"\currentversion\explorer\shell folders", @"\appinit_dlls",
    ];

    /// <summary>Registry places of the PowerShell execution policy (execution_policy).</summary>
    public static readonly string[] PolicyRegistry = [@"\shellids\microsoft.powershell", @"\policies\microsoft\windows\powershell"];

    /// <summary>Value names that switch off a security feature when written (security_off).</summary>
    public static readonly HashSet<string> SecurityValueNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "enablelua", "consentpromptbehavioradmin", "promptonsecuredesktop", "smartscreenenabled", "enablesmartscreen", "disableantispyware",
        "disablerealtimemonitoring", "disableantivirus", "enablefirewall", "disablebehaviormonitoring", "disableioavprotection", "tamperprotection",
    };

    /// <summary>User folders: writing over a file there may lose the user's data (deletes_files).</summary>
    public static readonly string[] UserFolders =
    [
        @"\users\", "$env:userprofile", "$home", "~\\", "~/", "$env:appdata", "$env:localappdata", "$env:onedrive", @"\desktop", @"\documents",
        @"\downloads", @"\pictures", @"\music", @"\videos", "onedrive", "getfolderpath",
    ];

    /// <summary>Startup folders (changes_service: adds a startup entry).</summary>
    public static readonly string[] StartupFolders = [@"\start menu\programs\startup", "shell:startup", "shell:common startup"];

    /// <summary>A registry path in a script's text, normalised: lower case, '/' → '\', drive and provider prefixes kept.</summary>
    public static string Norm(string s) => s.Replace('/', '\\').Replace("`", "", StringComparison.Ordinal).ToLowerInvariant();

    public static bool LooksRegistry(string s)
    {
        var n = Norm(s);
        return n.Contains("hklm", StringComparison.Ordinal) || n.Contains("hkcu", StringComparison.Ordinal) || n.Contains("hkey_", StringComparison.Ordinal)
            || n.Contains("registry::", StringComparison.Ordinal) || n.Contains("hku:", StringComparison.Ordinal);
    }

    /// <summary>Compact form for word matching: lower case, spaces, dashes, underscores, dots, backticks and carets removed.</summary>
    public static string Squash(string s)
    {
        var chars = new char[s.Length];
        var n = 0;
        foreach (var c in s)
            if (c is not (' ' or '\t' or '-' or '_' or '`' or '^' or '.' or '"' or '\''))
                chars[n++] = char.ToLowerInvariant(c);
        return new string(chars, 0, n);
    }
}

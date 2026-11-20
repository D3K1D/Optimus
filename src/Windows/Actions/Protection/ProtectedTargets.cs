namespace OptimizationTool.Windows.Actions.Protection;

/// <summary>
/// Why Optimus refuses a target (contracts/m9-ai-powers.md §3.2, §3.4). Code: "protected" (Windows, security, backup,
/// device management, Optimus itself) or "hardware_limit" (a vendor tuning or fan utility). Reason: one short plain line.
/// </summary>
public sealed record Protection(string Code, string Reason)
{
    public const string ProtectedCode = "protected", HardwareLimitCode = "hardware_limit";
}

/// <summary>
/// The compiled protected lists and the hardware-control list, one entry point per target kind. Host handlers use them
/// now; the broker (§3.5) reuses them. Matching folds case, spacing and punctuation, so "  MSI   afterburner " and
/// "MSIAfterburner.EXE" are the same name, and a renamed file still matches on its publisher. Best effort by design:
/// the hardware list is a second line behind §0.2 (no operation sets voltages, clocks or fans).
/// </summary>
public static class ProtectedTargets
{
    // ---------------------------------------------------------------- lists

    /// <summary>System, session-critical and shell processes (image file names, folded).</summary>
    internal static readonly string[] SystemProcesses =
    [
        "system", "registry", "secure system", "memory compression", "idle", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
        "svchost", "fontdrvhost", "dwm", "explorer", "sihost", "taskhostw", "ctfmon", "logonui", "userinit", "spoolsv", "audiodg", "conhost",
        "startmenuexperiencehost", "shellexperiencehost", "shellhost", "searchhost", "textinputhost", "lockapp", "wudfhost", "dllhost",
        "runtimebroker", "applicationframehost", "systemsettings", "wlanext", "dashost", "smartscreen", "consent", "trustedinstaller", "tiworker",
        "msiexec", "wuauclt", "musnotification", "usocoreworker", "mousocoreworker", "taskmgr",
    ];

    /// <summary>Security product processes (Microsoft Defender and common third-party products), folded image names.</summary>
    internal static readonly string[] SecurityProcesses =
    [
        "msmpeng", "nissrv", "mpdefendercoreservice", "mpcmdrun", "securityhealthservice", "securityhealthsystray", "securityhealthhost", "mssense", "sensendr",
        "avastsvc", "avastui", "avgsvc", "avgui", "bdagent", "vsserv", "bdservicehost", "bdredline", "avp", "avpui", "ksde", "nortonsecurity", "ns", "nllauncher",
        "mcshield", "mfemms", "mfevtps", "mcuicnt", "modulecoreservice", "ekrn", "egui", "mbamservice", "mbamtray", "mbam", "sophoshealth", "savservice",
        "sophosui", "pccntmon", "coreserviceshell", "ntrtscan", "wrsa", "csfalconservice", "csfalconcontainer", "sentinelagent", "sentinelui", "cb", "repmgr",
        "cylancesvc", "fsorsp64", "fshoster32", "fshoster64", "zatray", "vsmon", "psanhost", "pavsrvx86", "aswidsagent", "emsisoftsecurity", "a2service",
    ];

    /// <summary>Optimus's own processes (prefixes, folded).</summary>
    internal static readonly string[] OwnProcessPrefixes = ["optimizationtool", "optimus"];

    /// <summary>
    /// Windows services that keep the PC secure, updated, bootable and manageable: security, update, RPC, scheduler,
    /// event log, installer, crypto, profile, power, plug and play (service key names, folded).
    /// </summary>
    internal static readonly string[] WindowsServices =
    [
        // security
        "windefend", "wdnissvc", "wdboot", "wdfilter", "wdnisdrv", "sense", "securityhealthservice", "wscsvc", "mpssvc", "bfe", "mdcoresvc", "samss",
        "keyiso", "vaultsvc", "efs", "ngcsvc", "ngcctnrsvc", "appidsvc", "webthreatdefsvc", "webthreatdefusersvc", "sgrmbroker", "lsm", "netlogon",
        // update and installer
        "wuauserv", "usosvc", "waasmedicsvc", "bits", "dosvc", "trustedinstaller", "msiserver", "installservice", "appxsvc", "clipsvc",
        // rpc, com, scheduler, event log, wmi, crypto, profile, power, pnp, core
        "rpcss", "rpceptmapper", "dcomlaunch", "schedule", "eventlog", "eventsystem", "winmgmt", "cryptsvc", "profsvc", "power", "plugplay",
        "brokerinfrastructure", "systemeventsbroker", "timebrokersvc", "coremessagingregistrar", "gpsvc", "sens", "dhcp", "dnscache", "nsi",
        "audiosrv", "audioendpointbuilder", "themes", "usermanager", "staterepository", "camsvc", "devicesetupmanager",
    ];

    /// <summary>
    /// Third-party security, backup and device-management publishers (folded signer prefixes). A service, startup entry,
    /// task or process signed by one of them is protected whatever its file is called.
    /// </summary>
    internal static readonly string[] ProtectedPublishers =
    [
        // security
        "avast software", "avg technologies", "bitdefender", "kaspersky", "nortonlifelock", "gen digital", "symantec", "mcafee", "eset",
        "malwarebytes", "sophos", "trend micro", "webroot", "crowdstrike", "sentinelone", "sentinel labs", "carbon black", "vmware carbon black",
        "cylance", "blackberry cylance", "f-secure", "withsecure", "check point", "zonealarm", "panda security", "emsisoft", "g data", "avira",
        "comodo", "trellix", "fortinet", "palo alto networks", "cisco systems", "huntress", "bullguard", "totalav", "surfshark",
        // backup
        "veeam", "acronis", "macrium", "carbonite", "backblaze", "idrive", "paragon software", "aomei", "easeus", "code42",
        // device management
        "manageengine", "zoho", "kaseya", "connectwise", "ninjarmm", "ninjaone", "datto", "n-able", "solarwinds msp", "atera", "jamf", "ivanti",
        "citrix", "vmware airwatch", "microsoft intune",
    ];

    /// <summary>Device-management product names that Microsoft signs (folded name fragments).</summary>
    internal static readonly string[] ManagementNames = ["intunemanagementextension", "microsoftintune", "intune", "ccmexec", "configmgr", "mdmagent", "omadmclient"];

    /// <summary>
    /// Vendor tuning and fan utilities (folded name fragments matched in a name or an image file name). A match makes the
    /// target hardware_limit: Optimus neither stops nor disables nor removes them (§3.4).
    /// </summary>
    internal static readonly string[] HardwareProducts =
    [
        "afterburner", "rtss", "rivatuner", "precisionx", "precision x1", "evgaprecision", "gputweak", "armourycrate", "armoury crate", "aisuite",
        "fanxpert", "asusfancontrol", "ryzenmaster", "amdryzenmaster", "xtuservice", "xtucli", "intelxtu", "extremetuning", "intelperformancemaximizer", "throttlestop",
        "speedfan", "fancontrol", "argusmonitor", "nzxtcam", "icue", "corsairservice", "easytune", "gigabytecontrolcenter", "rgbfusion",
        "msicenter", "dragoncenter", "msi.centralserver", "mysticlight", "atuning", "asrocktimingconfigurator", "firestorm", "trixx",
        "lenovovantage", "legionzone", "alienwarecommandcenter", "awcc", "omencommandcenter", "omengaminghub", "nitrosense", "predatorsense",
        "dellpowermanager", "nvidiainspector", "nvidiaprofileinspector", "clocktuner", "undervolt", "overclock", "fancurve",
    ];

    /// <summary>Publishers whose startup and background software is tuning, fan or lighting control (folded signer prefixes).</summary>
    internal static readonly string[] HardwarePublishers =
    [
        "micro-star international", "msi", "evga", "asustek", "asus", "corsair", "nzxt", "gigabyte", "giga-byte", "asrock", "zotac", "sapphire",
        "argotronic", "unwinder", "techpowerup", "1usmus",
    ];

    // ---------------------------------------------------------------- entry points

    /// <summary>A process. <paramref name="imageName"/> may be a file name or a full path.</summary>
    public static Protection? Process(string? imageName, string? publisher = null, IReadOnlyList<string>? securityProducts = null)
    {
        var image = FoldedFile(imageName);
        if (image.Length == 0) return new(Protection.ProtectedCode, "Optimus can't tell what this process is.");
        if (OwnProcessPrefixes.Any(p => image.StartsWith(p, StringComparison.Ordinal))) return new(Protection.ProtectedCode, "This is part of Optimus.");
        if (SystemProcesses.Contains(image)) return new(Protection.ProtectedCode, "Windows needs this process to keep running.");
        if (SecurityProcesses.Contains(image)) return new(Protection.ProtectedCode, "This is part of your security software.");
        return Common(image, publisher, securityProducts);
    }

    /// <summary>A Windows service by its key name (and display name, publisher). Kernel and file-system drivers are always protected.</summary>
    public static Protection? Service(string? serviceName, string? displayName = null, string? publisher = null, string? imagePath = null, bool kernelDriver = false,
        IReadOnlyList<string>? securityProducts = null)
    {
        if (kernelDriver) return new(Protection.ProtectedCode, "This is a driver; Optimus never turns drivers off.");
        var name = Fold(serviceName);
        if (name.Length == 0) return new(Protection.ProtectedCode, "Optimus can't tell what this service is.");
        if (WindowsServices.Contains(name)) return new(Protection.ProtectedCode, "Windows needs this service.");
        if (OwnProcessPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal))) return new(Protection.ProtectedCode, "This is part of Optimus.");
        var image = FoldedFile(imagePath);
        if (image.Length > 0 && SecurityProcesses.Contains(image)) return new(Protection.ProtectedCode, "This is part of your security software.");
        return Common(name + " " + Fold(displayName) + " " + image, publisher, securityProducts);
    }

    /// <summary>A scheduled task by its full path (and what it runs). Everything under \Microsoft\ is protected.</summary>
    public static Protection? Task(string? taskPath, string? imagePath = null, string? publisher = null, IReadOnlyList<string>? securityProducts = null)
    {
        var path = NormalTaskPath(taskPath);
        if (path.Length == 0) return new(Protection.ProtectedCode, "Optimus can't tell what this task is.");
        if (path.StartsWith(@"\microsoft\", StringComparison.Ordinal) || path == @"\microsoft") return new(Protection.ProtectedCode, "This is one of Windows' own tasks.");
        var image = FoldedFile(imagePath);
        if (image.Length > 0 && (SecurityProcesses.Contains(image) || SystemProcesses.Contains(image) && image is not "explorer"))
            return new(Protection.ProtectedCode, "This task runs part of Windows or your security software.");
        return Common(Fold(path) + " " + image, publisher, securityProducts);
    }

    /// <summary>A startup entry (Run value or Startup-folder item) by its name and the file it launches.</summary>
    public static Protection? StartupEntry(string? name, string? imagePath = null, string? publisher = null, IReadOnlyList<string>? securityProducts = null)
    {
        var image = FoldedFile(imagePath);
        if (image.Length > 0)
        {
            if (OwnProcessPrefixes.Any(p => image.StartsWith(p, StringComparison.Ordinal))) return new(Protection.ProtectedCode, "This is part of Optimus.");
            if (SecurityProcesses.Contains(image)) return new(Protection.ProtectedCode, "This starts your security software.");
        }
        return Common(Fold(name) + " " + image, publisher, securityProducts);
    }

    /// <summary>
    /// Store (MSIX) packages Windows itself needs or that install and update other apps (folded package-name prefixes,
    /// the family name before '_'). Frameworks, resource packages and system-signed packages are refused by the handler.
    /// </summary>
    internal static readonly string[] WindowsPackages =
    [
        "microsoft.windowsstore", "microsoft.storepurchaseapp", "microsoft.desktopappinstaller", "microsoft.sechealthui", "microsoft.windows.",
        "microsoftwindows.", "microsoft.ui.xaml", "microsoft.vclibs", "microsoft.net.", "microsoft.windowsappruntime", "microsoft.services.store",
        "microsoft.aad.brokerplugin", "microsoft.accountscontrol", "microsoft.lockapp", "microsoft.win32webviewhost", "microsoft.webexperience",
        "microsoft.applicationcompatibilityenhancements", "microsoft.directxruntime", "microsoft.hevcvideoextension", "microsoft.vp9videoextensions",
        "microsoft.webmediaextensions", "microsoft.webpimageextension", "microsoft.heifimageextension", "microsoft.rawimageextension",
        "microsoft.av1videoextension", "microsoft.mpeg2videoextension", "microsoft.windowsterminal", "microsoft.gamingservices", "microsoft.xboxidentityprovider",
        "microsoft.xbox.tcui", "microsoft.xboxgameoverlay", "microsoft.xboxgamingoverlay", "microsoft.xboxspeechtotextoverlay",
    ];

    /// <summary>A Store (MSIX) app by its family name (and display name, publisher).</summary>
    public static Protection? StoreApp(string? familyName, string? displayName = null, string? publisher = null, IReadOnlyList<string>? securityProducts = null)
    {
        var family = Fold(familyName);
        var name = family.Split('_')[0];
        if (name.Length == 0) return new(Protection.ProtectedCode, "Optimus can't tell what this app is.");
        if (WindowsPackages.Any(p => name.StartsWith(p, StringComparison.Ordinal) || name == p.TrimEnd('.'))) return new(Protection.ProtectedCode, "Windows needs this app.");
        if (OwnProcessPrefixes.Any(p => Compact(name).Contains(p, StringComparison.Ordinal)) || OwnProcessPrefixes.Any(p => Compact(displayName ?? "").StartsWith(p, StringComparison.Ordinal)))
            return new(Protection.ProtectedCode, "This is part of Optimus.");
        return Common(name + " " + Fold(displayName), publisher, securityProducts);
    }

    /// <summary>Words that say what kind of product it is, never whose (left out when a Security Center name is matched).</summary>
    private static readonly HashSet<string> GenericWords = new(StringComparer.Ordinal)
    {
        "windows", "microsoft", "defender", "antivirus", "anti", "virus", "malware", "antimalware", "security", "internet", "total", "firewall", "endpoint",
        "protection", "free", "pro", "premium", "plus", "suite", "essentials", "the", "smart", "home", "edition", "cloud", "advanced", "personal",
        "business", "agent", "client", "and", "for", "by", "av", "network", "safe", "secure", "ultimate", "standard", "basic",
    };

    /// <summary>
    /// The vendor key of a Security Center product name: its first word (letters and digits) that is not a generic product
    /// word, at least three characters ("Avast Free Antivirus" → "avast", "Norton 360" → "norton"); null for Microsoft's own
    /// products (the compiled lists cover them) and names with no such word.
    /// </summary>
    internal static string? VendorKey(string? productName)
    {
        var folded = Fold(productName);
        if (folded.Length == 0 || folded.Contains("windows defender", StringComparison.Ordinal) || folded.Contains("microsoft defender", StringComparison.Ordinal)
            || folded == "windows firewall") return null;
        var words = Words(folded);
        return words.FirstOrDefault(w => w.Length >= 3 && !GenericWords.Contains(w));
    }

    private static string[] Words(string folded) =>
        new string(folded.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// The live Security Center list (m9 §3.2): a target whose publisher starts with a registered product's vendor key, or
    /// one of whose name words starts with it, belongs to that product. Errs towards protecting: a false match only means
    /// Optimus leaves something alone.
    /// </summary>
    internal static bool SecurityCenterProduct(string names, string publisher, IReadOnlyList<string>? products)
    {
        if (products is not { Count: > 0 }) return false;
        var nameWords = Words(names);
        var pub = Compact(publisher);
        foreach (var product in products)
        {
            if (VendorKey(product) is not { } key) continue;
            if (pub.Length > 0 && pub.StartsWith(key, StringComparison.Ordinal)) return true;
            if (nameWords.Any(w => w.StartsWith(key, StringComparison.Ordinal))) return true;
        }
        return false;
    }

    /// <summary>Security, backup, management and hardware checks shared by every kind (name text already folded).</summary>
    private static Protection? Common(string names, string? publisher, IReadOnlyList<string>? securityProducts = null)
    {
        var pub = Fold(publisher);
        var compact = Compact(names);
        if (pub.Length > 0 && ProtectedPublishers.Any(p => StartsWithWord(pub, p))) return new(Protection.ProtectedCode, "This belongs to security, backup or device-management software.");
        if (SecurityCenterProduct(names, pub, securityProducts)) return new(Protection.ProtectedCode, "This belongs to security software registered with Windows Security Center.");
        if (ManagementNames.Any(m => compact.Contains(Compact(m), StringComparison.Ordinal))) return new(Protection.ProtectedCode, "This belongs to device-management software.");
        if (HardwareProducts.Any(h => compact.Contains(Compact(h), StringComparison.Ordinal)))
            return new(Protection.HardwareLimitCode, "This is a hardware tuning or fan tool; Optimus leaves it alone.");
        if (pub.Length > 0 && HardwarePublishers.Any(p => StartsWithWord(pub, p)))
            return new(Protection.HardwareLimitCode, "This comes from a hardware vendor's tuning or fan software; Optimus leaves it alone.");
        return null;
    }

    // ---------------------------------------------------------------- folding

    /// <summary>Lower case, whitespace runs collapsed to one space, trimmed; control and format characters dropped.</summary>
    internal static string Fold(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        var space = false;
        foreach (var c in s.Normalize(System.Text.NormalizationForm.FormKC))
        {
            if (char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format) continue;
            if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>Letters and digits only (and '.', so "msi.centralserver" stays distinct), folded.</summary>
    private static string Compact(string s) => new(Fold(s).Where(c => char.IsLetterOrDigit(c) || c == '.').ToArray());

    /// <summary>The folded file name of a path or name, without a trailing ".exe" and surrounding quotes.</summary>
    internal static string FoldedFile(string? pathOrName)
    {
        var s = Fold(pathOrName).Trim('"', ' ');
        var slash = Math.Max(s.LastIndexOf('\\'), s.LastIndexOf('/'));
        if (slash >= 0) s = s[(slash + 1)..];
        s = s.Trim();
        return s.EndsWith(".exe", StringComparison.Ordinal) ? s[..^4].TrimEnd() : s;
    }

    /// <summary>"\Microsoft\Windows\X" folded: one backslash per separator, a leading one, no trailing one.</summary>
    internal static string NormalTaskPath(string? path)
    {
        var parts = Fold(path).Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? "" : "\\" + string.Join('\\', parts);
    }

    /// <summary>The folded publisher starts with the folded prefix as a whole word ("msi" matches "msi co., ltd." not "msix").</summary>
    private static bool StartsWithWord(string publisher, string prefix) =>
        publisher.StartsWith(prefix, StringComparison.Ordinal)
        && (publisher.Length == prefix.Length || !char.IsLetterOrDigit(publisher[prefix.Length]));
}

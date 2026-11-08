using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OptimizationTool.Engine.Agent;

// Allowlist projectors for src/Windows/Reliability collectors: installed apps, device problems, displays, event
// log groups, diagnostic-data and Start settings. Event message text, update titles, device instance IDs and
// registry key names never leave.

/// <summary>win.apps.installed: app alias, publisher alias, numeric version, source, scope, kind, reported size (MiB).</summary>
internal sealed class AppsProjector : Projector
{
    public override IReadOnlyCollection<string> CollectorIds { get; } = ["win.apps.installed"];
    public override string Source(string collectorId) => "App registrations";

    public override Mapped? Map(string collectorId, string subject, JsonObject v, Out o)
    {
        if (subject == "apps.installed.summary")
        {
            var reg = o.Int("registeredCount", v["registeredCount"]) ?? 0;
            var pkg = o.Int("packageCount", v["packageCount"]) ?? 0;
            var sized = o.Int("withReportedSizeCount", v["withReportedSizeCount"]) ?? 0;
            o.Int("runtimeCount", v["runtimeCount"]);
            o.Int("skippedSystemComponentCount", v["skippedSystemComponentCount"]);
            o.Int("skippedUpdateCount", v["skippedUpdateCount"]);
            o.Bool("otherUsersCovered", v["otherUsersCovered"]);
            o.Bool("portableAppsCovered", v["portableAppsCovered"]);
            return new(subject, $"{Say.Plural(reg + pkg, "app", "apps")} found; {Say.N(sized)} report a size.");
        }
        var package = subject.StartsWith("apps.package.", StringComparison.Ordinal);
        if (!package && !subject.StartsWith("apps.installed.", StringComparison.Ordinal)) return null;

        var rawName = Read.Str(v["name"]);
        var name = o.Name("app", "app", rawName);
        if (name is null) return null;
        o.Name("publisher", "publisher", Read.Str(v["publisher"]));
        o.Version("version", v["version"]);
        o.Enum("source", v["source"], "uninstall", "package");
        o.Const("scope", Read.Str(v["scope"]) switch { "hklm64" or "hklm32" => "machine", "hkcu" or "user" => "user", _ => "unknown" });
        o.Enum("kind", v["kind"], "app", "runtime");
        var mib = o.Mib("sizeMiB", v["sizeBytes"]);
        o.Enum("sizeQuality", v["sizeQuality"], "reported", "unknown");
        var display = mib is null ? package ? $"{name} is installed from the Store or a package; size unknown." : $"{name} is installed; size unknown."
            : mib >= 1024 ? $"{name} reports about {Say.N(Math.Round(mib.Value / 1024, 1))} GiB." : $"{name} reports about {Say.N(Math.Max(1, Math.Round(mib.Value)))} MiB.";
        return new($"apps.{(package ? "package" : "installed")}.{Slug(o.AliasOf("app", rawName))}", display, mib is null ? "unavailable" : "reported");
    }
}

/// <summary>win.devices.pnp: problem devices (alias, class, problem code, driver provider alias/version/date) and counts.</summary>
internal sealed class DevicesProjector : Projector
{
    internal static readonly Regex DeviceClass = new(@"^[A-Za-z][A-Za-z0-9]{0,31}$", RegexOptions.CultureInvariant);

    public override IReadOnlyCollection<string> CollectorIds { get; } = ["win.devices.pnp"];
    public override string Source(string collectorId) => "Device Manager";

    public override Mapped? Map(string collectorId, string subject, JsonObject v, Out o)
    {
        if (subject == "devices.pnp.summary")
        {
            var count = o.Int("deviceCount", v["deviceCount"]) ?? 0;
            o.Int("withDriverCount", v["withDriverCount"]);
            var problems = o.Int("problemCount", v["problemCount"]) ?? 0;
            o.Int("driversOlderThan2YearsCount", v["driversOlderThan2YearsCount"]);
            o.Int("thirdPartyDriversOlderThan2YearsCount", v["thirdPartyDriversOlderThan2YearsCount"]);
            if (v["byClass"] is JsonObject by)
            {
                var outBy = new JsonObject();
                long other = 0;
                foreach (var (k, n) in by)
                {
                    if (Read.Long(n) is not { } c || c < 0) { o.Dropped(n ?? JsonValue.Create(0)); continue; }
                    if (outBy.Count < 64 && DeviceClass.IsMatch(k) && !Sanitize.LooksSensitive(k)) outBy[k] = c;
                    else { other += c; o.Stats.DropField(); }
                }
                if (other > 0) outBy["other"] = (Read.Long(outBy["other"]) ?? 0) + other;
                o.V["byClass"] = outBy;
            }
            return new(subject, $"{Say.Plural(count, "device", "devices")} present, {Say.Plural(problems, "with a problem", "with problems")}.");
        }
        if (!subject.StartsWith("devices.pnp.", StringComparison.Ordinal)) return null;
        var rawName = Read.Str(v["name"]);
        var name = o.Name("device", "device", rawName) ?? "A device";
        var cls = Read.Str(v["class"]);
        if (cls is not null && DeviceClass.IsMatch(cls) && !Sanitize.LooksSensitive(cls)) o.V["class"] = cls; else o.Dropped(v["class"]);
        var code = o.Int("problemCode", v["problemCode"], 0, 1000);
        if (code is null) return null;
        o.Name("driverProvider", "publisher", Read.Str(v["provider"]));
        o.Version("driverVersion", v["driverVersion"]);
        o.Date("driverDate", v["driverDate"]);
        return new($"devices.pnp.{Slug(o.AliasOf("device", rawName ?? subject))}", $"{name} reports problem code {Say.N(code.Value)}.");
    }
}

/// <summary>win.custom.display: index, primary, resolution, current/max refresh and supported rates.</summary>
internal sealed class DisplayProjector : Projector
{
    public override IReadOnlyCollection<string> CollectorIds { get; } = ["win.custom.display"];
    public override string Source(string collectorId) => "Display settings";

    public override Mapped? Map(string collectorId, string subject, JsonObject v, Out o)
    {
        if (!subject.StartsWith("display.", StringComparison.Ordinal)) return null;
        var index = o.Int("index", v["index"], 1, 64);
        if (index is null) return null;
        var primary = o.Bool("primary", v["primary"]) == true;
        o.Int("widthPx", v["widthPx"], 0, 100000);
        o.Int("heightPx", v["heightPx"], 0, 100000);
        var current = o.Int("currentHz", v["currentHz"], 1, 2000);
        var max = o.Int("maxHzAtResolution", v["maxHzAtResolution"], 1, 2000);
        o.IntArray("ratesHz", v["ratesHz"], 1, 2000);
        var count = o.Int("activeDisplayCount", v["activeDisplayCount"], 1, 64) ?? 1;
        var label = count == 1 ? "Your display" : primary ? "Your main display" : $"Display {Say.N(index.Value)}";
        var display = current is null ? $"{label} refresh rate could not be read."
            : max > current ? $"{label} runs at {Say.N(current.Value)} Hz; it supports up to {Say.N(max.Value)} Hz at this resolution."
            : $"{label} runs at {Say.N(current.Value)} Hz, its highest rate at this resolution.";
        return new($"display.{Say.N(index.Value)}", display, current is null ? "unavailable" : "measured");
    }
}

/// <summary>
/// win.events.*: channel, provider (known-shape names only, else alias), event ID, level, count, first/last and
/// recent times, normalized codes (bugcheck, exception, update error), faulting app/module aliases, KB number.
/// Never message text or update titles.
/// </summary>
internal sealed class EventsProjector : Projector
{
    private static readonly IReadOnlyDictionary<string, (string Short, string Channel, string Label)> Channels = new Dictionary<string, (string, string, string)>
    {
        ["win.events.system"] = ("system", "System", "System"),
        ["win.events.application"] = ("application", "Application", "Application"),
        ["win.events.update"] = ("update", "Microsoft-Windows-WindowsUpdateClient/Operational", "Windows Update"),
        ["win.events.defender"] = ("defender", "Microsoft-Windows-Windows Defender/Operational", "Defender"),
    };

    private static readonly HashSet<string> KnownProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        // Providers the collectors and rules group on, plus common Windows sources. Only these are shared verbatim.
        "Microsoft-Windows-Kernel-Power", "Microsoft-Windows-WER-SystemErrorReporting", "BugCheck", "Microsoft-Windows-WHEA-Logger",
        "Application Error", "Application Hang", "Windows Error Reporting", "Microsoft-Windows-WindowsUpdateClient",
        "Microsoft-Windows-Windows Defender", "EventLog", "Service Control Manager", "Microsoft-Windows-Kernel-General",
        "Microsoft-Windows-Kernel-Boot", "Microsoft-Windows-Kernel-Processor-Power", "Microsoft-Windows-Kernel-PnP", "Microsoft-Windows-Ntfs",
        "Microsoft-Windows-Winlogon", "Microsoft-Windows-DistributedCOM", "Microsoft-Windows-Time-Service", "Microsoft-Windows-Security-SPP",
        "Microsoft-Windows-Resource-Exhaustion-Detector", "Microsoft-Windows-DriverFrameworks-UserMode", "Microsoft-Windows-Display",
        "User32", "Disk", "Ntfs", "volmgr", "Tcpip", "Wininit", "Winlogon", "MsiInstaller", "SecurityCenter", "ESENT", "VSS", "WMI", ".NET Runtime",
        "Application Popup", "DCOM", "Schannel", "nvlddmkm", "amdkmdag", "igfx", "storahci", "stornvme", "iaStorAC", "Display",
    };

    private static readonly HashSet<string> ReportTypes = new(StringComparer.Ordinal)
    {
        "APPCRASH", "AppHangB1", "AppHangXProcB1", "BEX", "BEX64", "MoAppCrash", "MoAppHang", "MoAppCrashB1", "LiveKernelEvent", "BlueScreen",
        "CLR20r3", "WindowsUpdateFailure", "WindowsUpdateFailure2", "WindowsUpdateFailure3", "StoreAgentInstallFailure1", "StoreAgentDownloadFailure1",
        "StoreAgentScanForUpdatesFailure0", "PnPDriverImportError", "PnPDriverInstallError", "RADAR_PRE_LEAK_64", "RADAR_PRE_LEAK_WOW64",
        "ProcessExceptionCode", "AppxDeploymentFailureBlue", "WHEA_UNCORRECTABLE_ERROR", "Crash32", "Crash64", "other",
    };

    private static readonly Regex Kb = new(@"\bKB(\d{6,8})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public override IReadOnlyCollection<string> CollectorIds { get; } = Channels.Keys.ToArray();
    public override string Source(string collectorId) => Channels.TryGetValue(collectorId, out var c) ? $"{c.Label} event log" : "Event log";

    public override Mapped? Map(string collectorId, string subject, JsonObject v, Out o)
    {
        if (!Channels.TryGetValue(collectorId, out var ch)) return null;
        if (Read.Str(v["channel"]) != ch.Channel) return null;
        o.Const("channel", ch.Channel);
        var lookbackDays = 0L;
        if (o.Int("lookbackSeconds", v["lookbackSeconds"], 0, 10L * 365 * 86400) is { } lb) lookbackDays = (long)Math.Round(lb / 86400.0);
        var window = lookbackDays <= 0 ? "recently" : lookbackDays <= 1 ? "in the last day" : $"in the last {Say.N(lookbackDays)} days";

        if (subject == $"events.{ch.Short}.coverage")
        {
            o.Enum("scope", v["scope"], "all levels", "critical, error, warning and selected events");
            var rows = o.Int("rowsRead", v["rowsRead"]) ?? 0;
            o.Int("groups", v["groups"]);
            o.Int("rowCap", v["rowCap"]);
            o.Bool("rowCapHit", v["rowCapHit"]);
            o.Time("oldestAt", v["oldestAt"]);
            o.Time("newestAt", v["newestAt"]);
            return new(subject, $"Read {Say.Plural(rows, "event", "events")} from the {ch.Label} log.");
        }
        if (!subject.StartsWith($"events.{ch.Short}.", StringComparison.Ordinal)) return null;

        var eventId = o.Int("eventId", v["eventId"], 0, 65535);
        if (eventId is null) return null;
        var rawProvider = Read.Str(v["provider"]);
        if (rawProvider is null) return null;
        string provider, providerSlug;
        if (KnownProviders.TryGetValue(rawProvider, out var known))
        {
            provider = known;
            o.V["provider"] = provider;
            var p = provider.StartsWith("Microsoft-Windows-", StringComparison.OrdinalIgnoreCase) ? provider["Microsoft-Windows-".Length..] : provider;
            providerSlug = AliasMap.Slug(p);
            if (providerSlug.Length == 0) providerSlug = "provider";
        }
        else
        {
            provider = o.Name("provider", "provider", rawProvider) ?? "An event source";
            providerSlug = Slug(o.AliasOf("provider", rawProvider));
        }
        o.Enum("level", v["level"], "critical", "error", "warning", "information", "verbose");
        var count = o.Int("count", v["count"], 0, 100000000) ?? 0;
        o.Time("firstAt", v["firstAt"]);
        o.Time("lastAt", v["lastAt"]);
        if (v["times"] is not null) o.TimeArray("times", v["times"]);

        var tail = "";
        string? app = null;
        if (v["appName"] is { } appNode)
        {
            var rawApp = FileName(Read.Str(appNode));
            app = o.Name("app", "process", rawApp);
            tail = "." + Slug(o.AliasOf("process", rawApp));
            if (app is null) o.Dropped(appNode);
        }
        else if (subject.EndsWith(".unknown", StringComparison.Ordinal) && (rawProvider is "Application Error" or "Application Hang")) tail = ".unknown";

        if (v["modules"] is JsonObject modules)
        {
            var outM = new JsonObject();
            foreach (var (k, n) in modules.Take(10))
            {
                var (label, untrusted) = o.Resolve("module", FileName(k));
                if (label is null || outM.ContainsKey(label) || Read.Long(n) is not { } c || c < 0) { o.Dropped(n ?? JsonValue.Create(0)); continue; }
                outM[label] = c;
                if (untrusted) o.MarkUntrusted("modules");
            }
            o.V["modules"] = outM;
        }
        HexTally(o, "exceptionCodes", v["exceptionCodes"]);
        HexTally(o, "bugcheckCodes", v["bugcheckCodes"]);
        if (v["powerButtonCount"] is not null) o.Int("powerButtonCount", v["powerButtonCount"]);
        if (v["distinctUpdates"] is not null) o.Int("distinctUpdates", v["distinctUpdates"]);

        string? bugcheck = null, error = null;
        if (v["bugcheckCode"] is { } bc) { bugcheck = o.Hex("bugcheckCode", bc); tail = "." + (bugcheck ?? "unknown"); }
        if (v["errorCode"] is { } ec) { error = o.Hex("errorCode", ec); tail = "." + (error ?? "unknown"); }
        if (v["reportType"] is { } rt)
        {
            var s = Read.Str(rt);
            var type = s is not null && ReportTypes.Contains(s) ? s : "other";
            if (type == "other" && s != "other") o.Dropped(rt);
            o.V["reportType"] = type;
            tail = "." + AliasMap.Slug(type);
        }
        if (v["sampleTitle"] is { } title)
        {
            try
            {
                if (Kb.Match(Sanitize.Fold(Read.Str(title))) is { Success: true } m) o.V["kb"] = "KB" + m.Groups[1].Value;
            }
            catch (RegexMatchTimeoutException) { o.Dropped(title); }
        }

        var times = Say.Times(count);
        var display = app is not null ? $"A {(eventId == 1002 ? "hang" : "crash")} of {app} was logged {times} {window}."
            : bugcheck is not null ? $"Stop code {bugcheck} was logged {times} {window}."
            : error is not null ? $"Update install failure {error} was logged {times} {window}."
            : $"{ShortProvider(provider)} event {Say.N(eventId.Value)} was logged {times} {window}.";
        return new($"events.{ch.Short}.{providerSlug}.{Say.N(eventId.Value)}{tail}", display);
    }

    private static string ShortProvider(string p) => p.StartsWith("Microsoft-Windows-", StringComparison.OrdinalIgnoreCase) ? p["Microsoft-Windows-".Length..] : p;

    private static void HexTally(Out o, string key, JsonNode? n)
    {
        if (n is null) return;
        if (n is not JsonObject obj) { o.Dropped(n); return; }
        var outT = new JsonObject();
        foreach (var (k, c) in obj.Take(10))
        {
            if (Out.IsHex(k) && Read.Long(c) is { } l && l >= 0) outT[k] = l;
            else o.Dropped(c ?? JsonValue.Create(0));
        }
        o.V[key] = outT;
    }
}

/// <summary>win.privacy.diagdata: enumerated policy/user levels, managed, state, effective level.</summary>
internal sealed class DiagDataProjector : Projector
{
    public override IReadOnlyCollection<string> CollectorIds { get; } = ["win.privacy.diagdata"];
    public override string Source(string collectorId) => "Privacy settings";

    public override Mapped? Map(string collectorId, string subject, JsonObject v, Out o)
    {
        if (subject != "privacy.diagdata") return null;
        o.Int("policyValue", v["policyValue"], 0, 3);
        o.Int("userValue", v["userValue"], 0, 3);
        o.Bool("managed", v["managed"]);
        var state = o.Enum("state", v["state"], "managed", "known", "unknown");
        var effective = o.Enum("effective", v["effective"], "required", "optional", "off", "unknown") ?? "unknown";
        var label = effective switch { "required" => "required only", _ => effective };
        return new(subject, state switch
        {
            "unknown" or null => "The diagnostic data setting could not be read.",
            "managed" => $"Diagnostic data is managed by policy ({label}).",
            _ => $"Diagnostic data is set to {label}.",
        }, state is "unknown" or null ? "unavailable" : "measured");
    }
}

/// <summary>win.custom.start: the enumerated Start recommendation switches and the effective state.</summary>
internal sealed class StartSuggestionsProjector : Projector
{
    public override IReadOnlyCollection<string> CollectorIds { get; } = ["win.custom.start"];
    public override string Source(string collectorId) => "Start settings";

    public override Mapped? Map(string collectorId, string subject, JsonObject v, Out o)
    {
        if (subject != "start.suggestions") return null;
        o.Int("irisRecommendations", v["irisRecommendations"], 0, 1);
        o.Int("subscribedContent338388", v["subscribedContent338388"], 0, 1);
        o.Int("systemPaneSuggestions", v["systemPaneSuggestions"], 0, 1);
        var on = o.Bool("suggestionsOn", v["suggestionsOn"]);
        var basis = Read.Str(v["basis"]) switch { "setting" => "setting", "assumed default (on)" => "assumed_default", _ => null };
        if (basis is null) o.Dropped(v["basis"]); else o.V["basis"] = basis;
        o.Enum("confidence", v["confidence"], "high", "medium", "low");
        var display = on == false ? "Start tips and recommendations are off."
            : basis == "setting" ? "Start shows tips and recommendations." : "Start likely shows tips and recommendations (Windows default).";
        return new(subject, display, basis == "setting" ? "measured" : "estimated");
    }
}

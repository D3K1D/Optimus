using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OptimizationTool.Engine.Agent;

// Allowlist projectors for src/Windows/System collectors: OS version, hardware sizes and counts, volumes, power,
// performance samples. Model names become device aliases; PIDs, process start times, GUIDs, PCI IDs and custom
// power-plan names never leave.

/// <summary>win.system.os: family, edition ID, version, build, architecture; uptime in days.</summary>
internal sealed class OsProjector : Projector
{
    private static readonly HashSet<string> Editions = new(StringComparer.Ordinal)
    {
        "Core", "CoreN", "CoreSingleLanguage", "CoreCountrySpecific", "Professional", "ProfessionalN", "ProfessionalWorkstation",
        "ProfessionalWorkstationN", "ProfessionalEducation", "ProfessionalEducationN", "Enterprise", "EnterpriseN", "EnterpriseS", "EnterpriseSN",
        "EnterpriseG", "Education", "EducationN", "IoTEnterprise", "IoTEnterpriseS", "ServerRdsh", "SE", "CloudEdition", "CloudEditionN",
    };
    private static readonly Regex DisplayVersion = new(@"^(?:\d{2}H\d|\d{4})$", RegexOptions.CultureInvariant);

    public override IReadOnlyCollection<string> CollectorIds { get; } = ["win.system.os"];
    public override string Source(string collectorId) => "Windows version and clock";

    public override Mapped? Map(string collectorId, string subject, JsonObject v, Out o)
    {
        if (subject == "system.uptime")
        {
            var days = o.Num("uptimeDays", v["uptimeDays"] ?? (Read.Num(v["uptimeSeconds"]) is { } s ? JsonValue.Create(s / 86400.0) : null), 1, 0, 100000) ?? 0;
            return new(subject, days >= 1 ? $"Windows has been running for {Say.N(Math.Floor(days))} days since the last restart." : "Windows was restarted within the last day.");
        }
        if (subject != "system.os") return null;
        var family = o.Enum("family", v["family"], "Windows 11", "Windows 10");
        var edition = Read.Str(v["editionId"]);
        if (edition is not null) { if (Editions.Contains(edition)) o.V["editionId"] = edition; else { o.V["editionId"] = "other"; o.Dropped(v["editionId"]); } }
        var dv = Read.Str(v["displayVersion"]);
        if (dv is not null && DisplayVersion.IsMatch(dv)) o.V["displayVersion"] = dv; else { o.Dropped(v["displayVersion"]); dv = null; }
        var build = o.Int("build", v["build"], 0, 999999);
        var ubr = o.Int("ubr", v["ubr"], 0, 999999);
        var it = Read.Str(v["installationType"]);
        if (it is "Client" or "Server" or "Server Core" or "Nano Server") o.V["installationType"] = it; else o.Dropped(v["installationType"]);
        var arch = o.Enum("architecture", v["architecture"], "x64", "arm64", "x86", "arm");
        var text = family ?? "Windows";
        if (dv is not null) text += " " + dv;
        if (build is not null) text += $", build {Say.N(build.Value)}{(ubr is not null ? "." + Say.N(ubr.Value) : "")}";
        if (arch is not null) text += $" ({arch})";
        return new(subject, text + ".", "reported");
    }
}

/// <summary>win.system.hardware: CPU vendor/topology, memory size, GPU vendor/memory/driver, firmware date. Models are device aliases.</summary>
internal sealed class HardwareProjector : Projector
{
    // Short version-like token: must contain a digit and no run of more than 8 letters (not a word or a sentence).
    private static readonly Regex BiosVersion = new(@"^(?=.*\d)(?!.*[A-Za-z]{9})[A-Za-z0-9][A-Za-z0-9._-]{0,23}$", RegexOptions.CultureInvariant);

    public override IReadOnlyCollection<string> CollectorIds { get; } = ["win.system.hardware"];
    public override string Source(string collectorId) => "Hardware registry and adapters";

    public override Mapped? Map(string collectorId, string subject, JsonObject v, Out o)
    {
        switch (subject)
        {
            case "system.cpu":
            {
                var model = o.Name("model", "device", Read.Str(v["name"])) ?? "Processor";
                var vendor = Sanitize.Fold(Read.Str(v["vendor"])).ToLowerInvariant();
                o.Const("vendor", vendor.Contains("intel") ? "intel" : vendor.Contains("amd") ? "amd" : vendor.Contains("qualcomm") ? "qualcomm"
                    : vendor.Contains("arm") ? "arm" : vendor.Length == 0 ? "unknown" : "other");
                o.Int("nominalMhz", v["nominalMhz"], 0, 100000);
                o.Int("packages", v["packages"], 0, 64);
                var cores = o.Int("cores", v["cores"], 0, 4096);
                var lp = o.Int("logicalProcessors", v["logicalProcessors"], 0, 8192);
                o.Int("performanceCores", v["performanceCores"], 0, 4096);
                o.Int("efficiencyCores", v["efficiencyCores"], 0, 4096);
                return new(subject, $"{model} has {(cores is not null ? Say.Plural(cores.Value, "core", "cores") + " and " : "")}{(lp is not null ? Say.Plural(lp.Value, "logical processor", "logical processors") : "an unknown number of logical processors")}.", "reported");
            }
            case "system.memory":
            {
                var installed = o.Gib("installedGiB", v["installedBytes"]);
                var usable = o.Gib("usableGiB", v["usableBytes"]);
                var total = installed ?? usable;
                return total is null ? null : new(subject, $"{Say.N(total.Value)} GiB of memory installed.");
            }
            case "system.firmware":
            {
                var maker = o.Name("manufacturer", "publisher", Read.Str(v["systemManufacturer"]));
                var model = o.Name("model", "device", Read.Str(v["systemProductName"]));
                o.Name("biosVendor", "publisher", Read.Str(v["biosVendor"]));
                var ver = Sanitize.Fold(Read.Str(v["biosVersion"]));
                if (ver.Length > 0 && BiosVersion.IsMatch(ver) && !Sanitize.LooksSensitiveVersion(ver)) o.V["biosVersion"] = ver; else { o.Dropped(v["biosVersion"]); ver = ""; }
                var date = o.Date("biosReleaseMonth", v["biosReleaseDate"], monthOnly: true);
                var who = model is not null ? maker is not null ? $"{model} by {maker}" : model : maker ?? "";
                var fw = $"firmware {(ver.Length > 0 ? ver : "unknown")}{(date is not null ? $" ({date})" : "")}";
                return new(subject, who.Length > 0 ? $"{who}, {fw}." : $"Firmware {(ver.Length > 0 ? ver : "unknown")}.", "reported");
            }
        }
        if (!subject.StartsWith("system.gpu.", StringComparison.Ordinal)) return null;
        var rawName = Read.Str(v["name"]);
        var gpuName = o.Name("model", "device", rawName);
        var gpu = gpuName is null ? "A graphics adapter" : "Graphics adapter " + gpuName;
        o.Const("vendor", Read.Str(v["vendorId"])?.ToLowerInvariant() switch
        {
            "10de" => "nvidia", "1002" or "1022" => "amd", "8086" => "intel", "1414" => "microsoft", "5143" or "4d4f" => "qualcomm", null => "unknown", _ => "other",
        });
        o.Gib("dedicatedMemoryGiB", v["dedicatedMemoryBytes"]);
        o.Gib("sharedMemoryGiB", v["sharedMemoryBytes"]);
        var drv = o.Version("driverVersion", v["driverVersion"]);
        var dd = o.Date("driverDate", v["driverDate"]);
        o.Name("driverProvider", "publisher", Read.Str(v["driverProvider"]));
        var text = drv is not null ? $"{gpu} uses driver {drv}{(dd is not null ? $" ({dd})" : "")}." : $"{gpu} was found.";
        return new($"system.gpu.{Slug(o.AliasOf("device", rawName))}", text, "reported");
    }
}

/// <summary>win.system.volumes: drive letter, system flag, capacity/free (GiB), free %, file system, media and bus type.</summary>
internal sealed class VolumesProjector : Projector
{
    public override IReadOnlyCollection<string> CollectorIds { get; } = ["win.system.volumes"];
    public override string Source(string collectorId) => "Windows volume capacity";

    public override Mapped? Map(string collectorId, string subject, JsonObject v, Out o)
    {
        if (!subject.StartsWith("volume.", StringComparison.Ordinal)) return null;
        var letter = Read.Str(v["letter"]);
        if (letter is not { Length: 1 } || !char.IsAsciiLetter(letter[0])) return null;
        letter = letter.ToUpperInvariant();
        o.V["letter"] = letter;
        var isSystem = o.Bool("isSystem", v["isSystem"]) == true;
        var cap = o.Gib("capacityGiB", v["capacityBytes"]);
        var free = o.Gib("freeGiB", v["freeBytes"]);
        o.Pct("freePercent", v["freePercent"]);
        var fs = Read.Str(v["fileSystem"]);
        o.V["fileSystem"] = fs is "NTFS" or "ReFS" or "FAT32" or "FAT" or "exFAT" or "UDF" or "CDFS" ? fs : fs is null ? null : "other";
        o.Enum("mediaType", v["mediaType"], "ssd", "hdd", "unknown");
        o.Enum("busType", v["busType"], "unknown", "scsi", "ata", "usb", "raid", "iscsi", "sas", "sata", "sd", "mmc", "virtual",
            "file_backed_virtual", "spaces", "nvme", "scm", "ufs", "other");
        var which = isSystem ? "The system drive" : $"Drive {letter}:";
        return new($"volume.{letter.ToLowerInvariant()}", free is not null && cap is not null
            ? $"{which} has {Say.N(free.Value)} GiB free out of {Say.N(cap.Value)} GiB." : $"{which} capacity could not be read.");
    }
}

/// <summary>win.system.power: plan personalities (no names/GUIDs), power source, battery %, power mode.</summary>
internal sealed class PowerProjector : Projector
{
    private static readonly string[] Personalities = ["high_performance", "balanced", "power_saver", "unknown"];

    public override IReadOnlyCollection<string> CollectorIds { get; } = ["win.system.power"];
    public override string Source(string collectorId) => "Windows power settings";

    public override Mapped? Map(string collectorId, string subject, JsonObject v, Out o)
    {
        switch (subject)
        {
            case "power.schemes":
            {
                var count = o.Int("count", v["count"], 0, 1000) ?? 0;
                o.Bool("hasBalanced", v["hasBalanced"]);
                if (v["schemes"] is JsonArray arr)
                {
                    var outArr = new JsonArray();
                    foreach (var s in arr.Take(32).OfType<JsonObject>())
                    {
                        var p = Read.Str(s["personality"]);
                        outArr.Add(new JsonObject
                        {
                            ["personality"] = p is not null && Array.IndexOf(Personalities, p) >= 0 ? p : "unknown",
                            ["builtIn"] = Read.Bool(s["builtIn"]),
                            ["active"] = Read.Bool(s["active"]),
                        });
                    }
                    o.V["schemes"] = outArr;
                }
                return new(subject, count == 1 ? "One power plan is available." : $"{Say.N(count)} power plans are available.");
            }
            case "power.active_scheme":
            {
                var p = o.Enum("personality", v["personality"], Personalities) ?? "unknown";
                var builtIn = o.Bool("builtIn", v["builtIn"]);
                var ultimate = o.Bool("ultimate", v["ultimate"]);
                var label = ultimate == true ? "Ultimate Performance" : p.Replace('_', ' ');
                return new(subject, builtIn == true ? $"The active power plan is {label}." : $"The active power plan is a custom plan ({p.Replace('_', ' ')}).");
            }
            case "power.source":
            {
                var source = o.Enum("source", v["source"], "battery", "ac", "unknown");
                var hasBattery = o.Bool("hasBattery", v["hasBattery"]);
                var pct = o.Int("batteryPercent", v["batteryPercent"], 0, 100);
                o.Bool("batterySaverOn", v["batterySaverOn"]);
                return new(subject, source switch
                {
                    "battery" => pct is not null ? $"Running on battery at {Say.N(pct.Value)}%." : "Running on battery.",
                    "ac" when hasBattery == true => pct is not null ? $"Plugged in, battery at {Say.N(pct.Value)}%." : "Plugged in.",
                    "ac" => "Plugged in; no battery reported.",
                    _ => "Power source not reported.",
                });
            }
            case "power.mode":
            {
                var supported = o.Bool("supported", v["supported"]);
                var mode = supported == true ? o.Enum("mode", v["mode"], "balanced", "best_power_efficiency", "better_battery", "best_performance", "other") : null;
                return new(subject, mode is not null ? $"Power mode is {mode.Replace('_', ' ')}." : "Power mode is not reported on this PC.",
                    supported == true ? "measured" : "unavailable");
            }
        }
        return null;
    }
}

/// <summary>win.perf.sample: rounded CPU/memory/disk measurements; top processes as aliases with CPU % and working set (MiB).</summary>
internal sealed class PerfProjector : Projector
{
    public override IReadOnlyCollection<string> CollectorIds { get; } = ["win.perf.sample"];
    public override string Source(string collectorId) => "Performance sampler";

    public override Mapped? Map(string collectorId, string subject, JsonObject v, Out o)
    {
        switch (subject)
        {
            case "perf.cpu":
            {
                var avg = o.Pct("avgPercent", v["avgPercent"]);
                o.Pct("maxPercent", v["maxPercent"]);
                var n = o.Int("sampleCount", v["sampleCount"], 0, 100000) ?? 0;
                o.Num("intervalSeconds", v["intervalSeconds"], 1, 0, 3600);
                o.Const("normalization", "percentOfAllLogicalProcessors");
                o.PctArray("percentSamples", v["percentSamples"]);
                return avg is null ? null : new(subject, $"CPU averaged {Say.N(avg.Value)}% over {Say.Plural(n, "sample", "samples")}.");
            }
            case "perf.memory":
            {
                o.Gib("totalGiB", v["totalBytes"]);
                o.Gib("availableMinGiB", v["availableMinBytes"]);
                var avail = o.Pct("availableAvgPercent", v["availableAvgPercent"]);
                o.PctArray("availablePercentSamples", v["availablePercentSamples"]);
                o.Gib("commitLimitGiB", v["commitLimitBytes"]);
                o.Pct("commitMaxPercent", v["commitMaxPercent"]);
                o.PctArray("commitPercentSamples", v["commitPercentSamples"]);
                var n = o.Int("sampleCount", v["sampleCount"], 0, 100000) ?? 0;
                o.Num("intervalSeconds", v["intervalSeconds"], 1, 0, 3600);
                return avail is null ? null : new(subject, $"Available memory averaged {Say.N(avail.Value)}% over {Say.Plural(n, "sample", "samples")}.");
            }
            case "perf.disk":
            {
                var busy = o.Pct("busyAvgPercent", v["busyAvgPercent"]);
                o.Pct("busyMaxPercent", v["busyMaxPercent"]);
                o.PctArray("busyPercentSamples", v["busyPercentSamples"]);
                o.Mib("mibPerSecAvg", v["bytesPerSecAvg"]);
                o.Mib("mibPerSecMax", v["bytesPerSecMax"]);
                o.Num("latencyAvgMs", v["latencyAvgMs"], 1, 0, 1e6);
                var n = o.Int("sampleCount", v["sampleCount"], 0, 100000) ?? 0;
                o.Num("intervalSeconds", v["intervalSeconds"], 1, 0, 3600);
                return busy is null ? null : new(subject, $"Disks were busy {Say.N(busy.Value)}% of the time on average over {Say.Plural(n, "sample", "samples")}.");
            }
            case "perf.sample":
            {
                o.Num("windowSeconds", v["windowSeconds"], 1, 0, 86400);
                var interval = o.Num("intervalSeconds", v["intervalSeconds"], 1, 0, 3600) ?? 0;
                var elapsed = o.Num("elapsedSeconds", v["elapsedSeconds"], 1, 0, 86400) ?? 0;
                o.Int("snapshots", v["snapshots"], 0, 100000);
                var intervals = o.Int("intervals", v["intervals"], 0, 100000) ?? 0;
                o.Int("logicalProcessors", v["logicalProcessors"], 0, 8192);
                var early = o.Bool("stoppedEarly", v["stoppedEarly"]);
                o.Int("processesTracked", v["processesTracked"], 0, 1000000);
                o.Int("processIntervalsInvalidated", v["processIntervalsInvalidated"], 0, 100000000);
                o.Bool("thisToolTopConsumer", v["thisToolTopConsumer"]);
                o.Bool("defenderTopConsumer", v["defenderTopConsumer"]);
                o.Num("overheadPercentOfOneCore", v["overheadPercentOfOneCore"], 1, 0, 100000);
                return new(subject, early == true
                    ? $"Sampling stopped early after {Say.Plural(intervals, "sample", "samples")}."
                    : $"Sampled for {Say.N(Math.Round(elapsed))} s at {Say.N(interval)} s intervals ({Say.Plural(intervals, "sample", "samples")}).");
            }
        }
        if (!subject.StartsWith("perf.process.", StringComparison.Ordinal)) return null;
        var image = FileName(Read.Str(v["image"]));
        var name = o.Name("process", "process", image) ?? "An unidentified process";
        var cpu = o.Pct("cpuAvgPercent", v["cpuAvgPercent"]) ?? 0;
        o.Pct("cpuMaxPercent", v["cpuMaxPercent"]);
        o.Num("cpuAvgLogicalProcessors", v["cpuAvgLogicalProcessors"], 2, 0, 8192);
        o.PctArray("cpuPercentSamples", v["cpuPercentSamples"]);
        var valid = o.Int("validIntervals", v["validIntervals"], 0, 100000) ?? 0;
        o.Int("totalIntervals", v["totalIntervals"], 0, 100000);
        o.Int("foregroundIntervals", v["foregroundIntervals"], 0, 100000);
        var ws = o.Mib("workingSetAvgMiB", v["workingSetAvgBytes"]);
        o.Mib("workingSetPeakMiB", v["workingSetPeakBytes"]);
        o.Int("cpuRank", v["cpuRank"], 1, 100);
        o.Int("memoryRank", v["memoryRank"], 1, 100);
        o.Bool("isThisTool", v["isThisTool"]);
        o.Bool("isDefender", v["isDefender"]);
        o.Const("normalization", "percentOfAllLogicalProcessors");
        // Second and later instances of the same image keep their ".N" suffix.
        var last = subject[(subject.LastIndexOf('.') + 1)..];
        var instance = last.Length is > 0 and <= 3 && last.All(char.IsAsciiDigit) ? "." + last : "";
        return new($"perf.process.{Slug(o.AliasOf("process", image))}{instance}",
            $"{name} averaged {Say.N(cpu)}% CPU over {Say.Plural(valid, "sample", "samples")}{(ws is not null ? $" and used about {Say.N(Math.Round(ws.Value))} MiB" : "")}.");
    }
}

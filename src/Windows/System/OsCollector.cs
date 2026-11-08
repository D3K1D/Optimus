using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using OptimizationTool.Engine;

namespace OptimizationTool.Windows.SystemDomain;

internal sealed record OsRaw(
    string? ProductName, string? EditionId, string? DisplayVersion, string? CurrentBuild, long? Ubr,
    string? InstallationType, string Architecture, long UptimeMs, bool RegistryReadable);

/// <summary>win.system.os: edition, build, architecture, uptime. Registry (HKLM, read) + runtime info.</summary>
internal sealed class OsCollector(CollectorOptions options) : SystemCollector<OsRaw>(options)
{
    public override string Id => "win.system.os";
    public override string Category => "performance";
    public override string GapTitle => "Windows version";
    public override string GapSource => "Windows registry";

    protected override OsRaw Read(CancellationToken ct)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return new OsRaw(
            Sys.RegString(key, "ProductName"), Sys.RegString(key, "EditionID"), Sys.RegString(key, "DisplayVersion"),
            Sys.RegString(key, "CurrentBuild") ?? Sys.RegString(key, "CurrentBuildNumber"), Sys.RegNumber(key, "UBR"),
            Sys.RegString(key, "InstallationType"),
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(), Environment.TickCount64, key is not null);
    }

    public override CollectorResult Normalize(OsRaw raw)
    {
        var facts = new List<Fact>();
        int? build = int.TryParse(raw.CurrentBuild, out var b) ? b : null;
        // ProductName still says "Windows 10" on Windows 11; the build number is authoritative.
        var family = build is >= 22000 ? "Windows 11" : build is >= 10240 ? "Windows 10" : raw.ProductName;
        var edition = EditionName(raw.EditionId);
        var name = string.Join(' ', new[] { family, edition }.Where(s => !string.IsNullOrEmpty(s)));

        if (raw.RegistryReadable)
        {
            var value = new JsonObject
            {
                ["family"] = family,
                ["editionId"] = raw.EditionId,
                ["displayVersion"] = raw.DisplayVersion,
                ["build"] = build,
                ["ubr"] = raw.Ubr,
                ["installationType"] = raw.InstallationType,
                ["architecture"] = raw.Architecture,
            };
            var ver = raw.DisplayVersion is { } dv ? $" {dv}" : "";
            var bld = build is { } bn ? $", build {bn}{(raw.Ubr is { } u ? $".{u}" : "")}" : "";
            facts.Add(new Fact("system.os", "Windows registry", "reported", value, $"{name}{ver}{bld} ({raw.Architecture}).", null));
        }

        var uptimeSeconds = raw.UptimeMs / 1000;
        var days = uptimeSeconds / 86400.0;
        facts.Add(new Fact("system.uptime", "System clock", "measured",
            new JsonObject { ["uptimeSeconds"] = uptimeSeconds, ["uptimeDays"] = Sys.Round(days) },
            days >= 1 ? $"Windows has been running for {Math.Floor(days):0} days since the last restart." : "Windows was restarted within the last day.", null));

        return raw.RegistryReadable
            ? Sys.Result(facts, "complete", null)
            : Sys.Result(facts, "partial", "version key unreadable");
    }

    private static string? EditionName(string? id) => id switch
    {
        null => null,
        "Core" => "Home",
        "CoreN" => "Home N",
        "CoreSingleLanguage" => "Home Single Language",
        "Professional" => "Pro",
        "ProfessionalN" => "Pro N",
        "ProfessionalWorkstation" => "Pro for Workstations",
        "ProfessionalEducation" => "Pro Education",
        "Enterprise" => "Enterprise",
        "EnterpriseS" => "Enterprise LTSC",
        "Education" => "Education",
        "IoTEnterprise" => "IoT Enterprise",
        "ServerRdsh" => "Enterprise multi-session",
        _ => id,
    };
}

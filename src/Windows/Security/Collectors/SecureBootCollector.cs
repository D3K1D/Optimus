using System.Text.Json.Nodes;
using Microsoft.Win32;
using OptimizationTool.Engine;
using OptimizationTool.Windows.Security.Common;

namespace OptimizationTool.Windows.Security.Collectors;

/// <summary>FirmwareType: 1 BIOS, 2 UEFI, else unknown. SecureBootEnabled from the registry state key.</summary>
internal sealed record SecureBootRaw(int FirmwareType, int? UefiSecureBootEnabled, bool StateKeyPresent);

/// <summary>
/// win.security.secureboot — firmware type via <c>GetFirmwareType</c> and Secure Boot state from
/// <c>HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State\UEFISecureBootEnabled</c>. Both work as a standard
/// user (reading the UEFI variable directly would need a privilege this tool does not request).
/// </summary>
internal sealed class SecureBootCollector(CollectorOptions options) : SecurityCollector<SecureBootRaw>(options)
{
    public const string CollectorId = "win.security.secureboot";

    public override string Id => CollectorId;
    public override string GapTitle => "Secure Boot";
    public override string GapSource => "Firmware";
    public override string GapDisplay => "Secure Boot state could not be read.";

    internal override SecureBootRaw Read(CancellationToken ct)
    {
        var type = Native.GetFirmwareType(out var t) ? t : 0;
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", writable: false);
        return new SecureBootRaw(type, key?.GetValue("UEFISecureBootEnabled") is int v ? v : null, key is not null);
    }

    internal override CollectorResult Normalize(SecureBootRaw raw)
    {
        var facts = new FactSet("Secure Boot state");
        var firmware = raw.FirmwareType switch { 1 => "bios", 2 => "uefi", _ => "unknown" };
        bool? enabled = firmware == "bios" ? false : raw.UefiSecureBootEnabled is { } v ? v != 0 : null;
        facts.Add("security.secureboot", new JsonObject
        {
            ["firmware"] = firmware,
            ["secureBootEnabled"] = enabled,
            ["capable"] = firmware == "uefi" ? true : firmware == "bios" ? false : null,
        }, firmware switch
        {
            "bios" => "This PC starts in legacy BIOS mode, so Secure Boot is not in use.",
            "uefi" when enabled == true => "Secure Boot is on.",
            "uefi" when enabled == false => "This PC uses UEFI, and Secure Boot is off.",
            _ => "Secure Boot state is not reported.",
        });
        if (firmware == "unknown") return facts.Result("unsupported", "Firmware type is not reported.");
        if (firmware == "uefi" && enabled is null) return facts.Result("partial", "Secure Boot state value is missing.");
        return facts.Result("complete", null);
    }
}

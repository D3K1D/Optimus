using System.Text.Json.Nodes;
using Microsoft.Win32;
using OptimizationTool.Engine;
using OptimizationTool.Windows.Security.Common;

namespace OptimizationTool.Windows.Security.Collectors;

internal sealed record RegDword(int? Value, string? Kind);

internal sealed record UacRaw(RegDword EnableLua, RegDword ConsentPromptBehaviorAdmin, RegDword PromptOnSecureDesktop, string? EditionId, string? Error);

/// <summary>
/// win.security.uac — <c>EnableLUA</c>, <c>ConsentPromptBehaviorAdmin</c> and <c>PromptOnSecureDesktop</c> from
/// <c>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System</c> (64-bit view, read-only), with value types.
/// An absent EnableLUA resolves to the documented default (enabled). A change only applies after a restart, which
/// this collector cannot observe, so <c>restartPending</c> is recorded as unknown.
/// </summary>
internal sealed class UacCollector(CollectorOptions options) : SecurityCollector<UacRaw>(options)
{
    public const string CollectorId = "win.security.uac";
    private const string KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    public override string Id => CollectorId;
    public override string GapTitle => "User Account Control";
    public override string GapSource => "Windows registry";
    public override string GapDisplay => "User Account Control settings could not be read.";

    internal override UacRaw Read(CancellationToken ct)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(KeyPath, writable: false);
        string? edition = null;
        using (var cv = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", writable: false))
            edition = cv?.GetValue("EditionID") as string;
        if (key is null) return new UacRaw(new(null, null), new(null, null), new(null, null), edition, "key_missing");
        return new UacRaw(Dword(key, "EnableLUA"), Dword(key, "ConsentPromptBehaviorAdmin"), Dword(key, "PromptOnSecureDesktop"), edition, null);
    }

    private static RegDword Dword(RegistryKey key, string name)
    {
        var kind = key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase) ? key.GetValueKind(name) : (RegistryValueKind?)null;
        if (kind is null) return new(null, null);
        var v = key.GetValue(name);
        return new(v is int i ? i : null, kind switch
        {
            RegistryValueKind.DWord => "REG_DWORD",
            RegistryValueKind.QWord => "REG_QWORD",
            RegistryValueKind.String => "REG_SZ",
            _ => "other",
        });
    }

    internal override CollectorResult Normalize(UacRaw raw)
    {
        var facts = new FactSet("UAC policy registry");
        // Documented default: EnableLUA absent → UAC enabled. A non-DWORD value is treated as unknown.
        bool? effective = raw.EnableLua.Kind switch
        {
            null => true,
            "REG_DWORD" => raw.EnableLua.Value != 0,
            _ => null,
        };
        var consent = raw.ConsentPromptBehaviorAdmin.Kind == "REG_DWORD" ? raw.ConsentPromptBehaviorAdmin.Value : null;
        facts.Add("security.uac", new JsonObject
        {
            ["enableLua"] = raw.EnableLua.Value,
            ["enableLuaKind"] = raw.EnableLua.Kind,
            ["enableLuaPresent"] = raw.EnableLua.Kind is not null,
            ["enabled"] = effective,
            ["consentPromptBehaviorAdmin"] = consent,
            ["promptOnSecureDesktop"] = raw.PromptOnSecureDesktop.Kind == "REG_DWORD" ? raw.PromptOnSecureDesktop.Value : null,
            ["view"] = "64",
            ["edition"] = Text.Clean(raw.EditionId, 40),
            ["restartPending"] = null,
        }, effective switch
        {
            false => "Windows reports User Account Control is turned off.",
            true when consent == 0 => "User Account Control is on, but admin requests are approved without asking.",
            true => "User Account Control is on.",
            _ => "User Account Control has an unexpected setting type.",
        });
        return facts.Result(raw.Error is null ? "complete" : "partial", raw.Error is null ? null : "UAC policy key is missing; using Windows defaults.");
    }
}

using System.Text.Json.Nodes;
using Microsoft.Win32;
using OptimizationTool.Engine;

namespace OptimizationTool.Windows.Reliability;

/// <summary>A registry DWORD read: present with a value, absent, or unreadable (denied).</summary>
internal sealed record RegDword(bool Present, int? Value, bool Denied = false)
{
    public static readonly RegDword Absent = new(false, null);

    public static RegDword Read(RegistryHive hive, RegistryView view, string path, string name)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(path, writable: false);
            var v = key?.GetValue(name);
            return v switch
            {
                int i => new RegDword(true, i),
                long l => new RegDword(true, (int)l),
                string s when int.TryParse(s, out var p) => new RegDword(true, p),
                null => Absent,
                _ => new RegDword(true, null),
            };
        }
        catch (System.Security.SecurityException) { return new RegDword(false, null, true); }
        catch (UnauthorizedAccessException) { return new RegDword(false, null, true); }
    }

    public JsonNode? Json() => Present ? Value : null;
}

// ---------------- Diagnostic data ----------------

internal sealed record RawDiagData(RegDword Policy, RegDword User);

internal sealed class DiagDataCollector(CollectorOptions options) : ReliabilityCollector(options)
{
    public const string PolicyPath = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection";
    public const string UserPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection";

    public override string Id => "win.privacy.diagdata";
    public override string Category => "privacy_telemetry";
    public override string GapTitle => "Diagnostic data";
    public override string GapSource => "Privacy settings";

    protected override CollectorResult Collect(CancellationToken ct) => DiagDataNormalizer.Normalize(new RawDiagData(
        RegDword.Read(RegistryHive.LocalMachine, RegistryView.Registry64, PolicyPath, "AllowTelemetry"),
        RegDword.Read(RegistryHive.LocalMachine, RegistryView.Registry64, UserPath, "AllowTelemetry")));
}

internal static class DiagDataNormalizer
{
    /// <summary>
    /// state: managed (policy set) | known (user choice read, no policy) | unknown (neither readable).
    /// effective: required | optional | off | unknown. Levels: 0 security/off, 1 required, 2 enhanced (legacy, counts as optional), 3 optional.
    /// With a policy, the policy sets the maximum; a lower user choice still applies.
    /// </summary>
    public static CollectorResult Normalize(RawDiagData raw)
    {
        if (raw.Policy.Denied && raw.User.Denied) return new([], new CoverageReceipt("denied", 0, "Access denied."));
        int? level;
        string state;
        if (raw.Policy.Present && raw.Policy.Value is { } p)
        {
            state = "managed";
            level = raw.User.Value is { } u ? Math.Min(p, u) : p;
        }
        else if (raw.User.Present && raw.User.Value is { } u)
        {
            state = "known";
            level = u;
        }
        else { state = "unknown"; level = null; }

        var effective = level switch { null => "unknown", <= 0 => "off", 1 => "required", _ => "optional" };
        var display = (state, effective) switch
        {
            ("unknown", _) => "The diagnostic data setting could not be read.",
            ("managed", _) => $"Diagnostic data is managed by policy ({Label(effective)}).",
            _ => $"Diagnostic data is set to {Label(effective)}.",
        };
        var fact = new Fact("privacy.diagdata", "Privacy settings", state == "unknown" ? "unavailable" : "measured", new JsonObject
        {
            ["policyValue"] = raw.Policy.Json(),
            ["userValue"] = raw.User.Json(),
            ["managed"] = state == "managed",
            ["state"] = state,
            ["effective"] = effective,
        }, display, null);
        var partial = raw.Policy.Denied || raw.User.Denied;
        return new([fact], new CoverageReceipt(partial ? "partial" : "complete", 1, partial ? "one setting unreadable" : null));
    }

    private static string Label(string effective) => effective switch
    {
        "optional" => "optional",
        "required" => "required only",
        "off" => "off",
        _ => "unknown",
    };
}

// ---------------- Start suggestions ----------------

internal sealed record RawStart(RegDword Iris, RegDword Subscribed338388, RegDword SystemPane);

internal sealed class StartCollector(CollectorOptions options) : ReliabilityCollector(options)
{
    public const string CdmPath = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    public const string AdvancedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    public override string Id => "win.custom.start";
    public override string Category => "customization";
    public override string GapTitle => "Start suggestions";
    public override string GapSource => "Start settings";

    protected override CollectorResult Collect(CancellationToken ct) => StartNormalizer.Normalize(new RawStart(
        RegDword.Read(RegistryHive.CurrentUser, RegistryView.Default, AdvancedPath, "Start_IrisRecommendations"),
        RegDword.Read(RegistryHive.CurrentUser, RegistryView.Default, CdmPath, "SubscribedContent-338388Enabled"),
        RegDword.Read(RegistryHive.CurrentUser, RegistryView.Default, CdmPath, "SystemPaneSuggestionsEnabled")));
}

internal static class StartNormalizer
{
    /// <summary>
    /// Windows 11 Start's "Show recommendations for tips, shortcuts, new apps, and more" is Start_IrisRecommendations.
    /// Absent = Windows default, which we assume is ON (medium confidence: the default can change by build or region).
    /// The two ContentDeliveryManager values are the older Start/"suggestions" switches; recorded as context.
    /// </summary>
    public static CollectorResult Normalize(RawStart raw)
    {
        if (raw.Iris.Denied && raw.Subscribed338388.Denied && raw.SystemPane.Denied) return new([], new CoverageReceipt("denied", 0, "Access denied."));
        bool on;
        string basis, confidence;
        if (raw.Iris.Present && raw.Iris.Value is { } v) { on = v != 0; basis = "setting"; confidence = "high"; }
        else { on = true; basis = "assumed default (on)"; confidence = "medium"; }

        var fact = new Fact("start.suggestions", "Start settings", basis == "setting" ? "measured" : "estimated", new JsonObject
        {
            ["irisRecommendations"] = raw.Iris.Json(),
            ["subscribedContent338388"] = raw.Subscribed338388.Json(),
            ["systemPaneSuggestions"] = raw.SystemPane.Json(),
            ["suggestionsOn"] = on,
            ["basis"] = basis,
            ["defaultAssumed"] = basis.StartsWith("assumed", StringComparison.Ordinal) ? "on" : null,
            ["confidence"] = confidence,
        }, on ? (basis == "setting" ? "Start shows tips and recommendations." : "Start likely shows tips and recommendations (Windows default).")
              : "Start tips and recommendations are off.", null);
        var partial = raw.Iris.Denied || raw.Subscribed338388.Denied || raw.SystemPane.Denied;
        return new([fact], new CoverageReceipt(partial ? "partial" : "complete", 1, partial ? "a setting was unreadable" : null));
    }
}

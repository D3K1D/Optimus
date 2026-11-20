using System.Text.Json.Nodes;

namespace OptimizationTool.Windows.Actions.Settings;

/// <summary>One allowed value of a setting. Data null means "remove the value" (Windows' own default).</summary>
internal sealed record SettingValue(string Value, string Label, JsonNode? Data);

/// <summary>
/// One reviewed Windows setting (contracts/m9-ai-powers.md §3.3). The AI names a settingId and one listed value; it
/// never supplies an address or data. absentMeans: the listed value Windows behaves as when the value is missing
/// (documentation for the reviewer and the guide; the preview still shows "Not set"), or null when unknown.
/// </summary>
internal sealed record SettingRow(
    string SettingId, string Label, string What, string Scope, string Hive, int View, string Key, string Name, string Type,
    IReadOnlyList<SettingValue> Values, string? AbsentMeans, IReadOnlyList<RegLocation> PolicyKeys, int MinBuild, string Notify,
    IReadOnlyList<string> Flags, IReadOnlyList<string> Tradeoffs, string Source)
{
    public RegLocation Location => new(Hive, View, Key, Name);

    /// <summary>The lock key reg:&lt;view&gt;:&lt;HIVE&gt;\&lt;KEY&gt;\&lt;name&gt;, the namespace lower-case and the rest upper-case (m9 §3.1).</summary>
    public string Resource => "reg:" + $"{View}:{Hive}\\{Key}\\{Name}".ToUpperInvariant();
}

/// <summary>
/// The reviewed settings table. A new row is a small reviewed change: exact key, value name, type and data, a source,
/// and its tests in SettingsCatalogTests. Rules: user scope (HKCU), or machine scope (HKLM, administrator, through the
/// broker, m9 §3.2); no row weakens a security
/// feature, changes update or installer policy, touches processor power settings, or enables something that deletes
/// data later.
/// </summary>
internal static class SettingsCatalog
{
    public const int Windows11 = 22000;
    private const string Cu = @"Software\Microsoft\Windows\CurrentVersion";
    private const string Advanced = Cu + @"\Explorer\Advanced";
    private const string PrivacyDoc = "https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services#181-general";

    private static JsonNode D(uint v) => JsonValue.Create((long)v);
    private static SettingValue[] OnOff(uint on = 1, uint off = 0) => [new("on", "On", D(on)), new("off", "Off", D(off))];
    private static RegLocation Lm(string key, string name) => new("HKLM", 64, key, name);
    private static RegLocation Cu_(string key, string name) => new("HKCU", 64, key, name);

    public static IReadOnlyList<SettingRow> Rows { get; } =
    [
        new("privacy.advertising_id", "Advertising ID", "Lets apps use an ID for ads tailored to you.",
            "user", "HKCU", 64, Cu + @"\AdvertisingInfo", "Enabled", "dword", OnOff(), null,
            [Lm(@"SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy")], Windows11, "none", [],
            ["Ads in apps become less relevant to you"], PrivacyDoc),

        new("privacy.tailored_experiences", "Tailored experiences", "Lets Microsoft use diagnostic data for personal tips, ads and recommendations.",
            "user", "HKCU", 64, Cu + @"\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", "dword", OnOff(), null,
            [Cu_(@"Software\Policies\Microsoft\Windows\CloudContent", "DisableTailoredExperiencesWithDiagnosticData")], Windows11, "none", [],
            ["Fewer personalised tips and offers"],
            "https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-experience#allowtailoredexperienceswithdiagnosticdata"),

        new("notifications.windows_tips", "Tips and suggestions", "Shows tips and suggestions as notifications while you use Windows.",
            "user", "HKCU", 64, Cu + @"\ContentDeliveryManager", "SubscribedContent-338389Enabled", "dword", OnOff(), null,
            [Cu_(@"Software\Policies\Microsoft\Windows\CloudContent", "DisableSoftLanding"), Lm(@"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableSoftLanding")],
            Windows11, "none", [], ["You miss Windows' own feature tips"],
            "https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-experience#allowwindowstips"),

        new("privacy.app_launch_tracking", "App launch tracking", "Lets Windows track which apps you start to improve Start and search.",
            "user", "HKCU", 64, Advanced, "Start_TrackProgs", "dword", OnOff(), "on",
            [Cu_(@"Software\Policies\Microsoft\Windows\EdgeUI", "DisableMFUTracking"), Lm(@"SOFTWARE\Policies\Microsoft\Windows\EdgeUI", "DisableMFUTracking")],
            Windows11, "explorer", [], ["Start and search no longer list your most-used apps"], PrivacyDoc),

        new("gaming.background_recording", "Background recording", "Keeps recording the last moments of games so you can save them afterwards.",
            "user", "HKCU", 64, Cu + @"\GameDVR", "HistoricalCaptureEnabled", "dword", OnOff(), null,
            [Lm(@"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR")], Windows11, "none", [],
            ["Uses some CPU, GPU and disk while you play", "Off: you can't save a clip of what just happened"],
            "https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-applicationmanagement#allowgamedvr"),

        new("appearance.transparency", "Transparency effects", "Makes windows, Start and the taskbar slightly see-through.",
            "user", "HKCU", 64, Cu + @"\Themes\Personalize", "EnableTransparency", "dword", OnOff(), null,
            [], Windows11, "setting_change", [], ["Off: a flatter look, slightly less work for the graphics chip"],
            "https://learn.microsoft.com/en-us/windows/apps/design/style/acrylic"),

        new("explorer.file_extensions", "File name extensions", "Shows the ending of file names, such as .exe or .pdf, in File Explorer.",
            "user", "HKCU", 64, Advanced, "HideFileExt", "dword", [new("shown", "Shown", D(0)), new("hidden", "Hidden", D(1))], "hidden",
            [], Windows11, "explorer", [], ["Shown: renaming a file can change its extension by accident"],
            "https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/ns-shlobj_core-shellstatea"),

        new("explorer.hidden_files", "Hidden files", "Shows files and folders marked hidden in File Explorer.",
            "user", "HKCU", 64, Advanced, "Hidden", "dword", [new("shown", "Shown", D(1)), new("hidden", "Hidden", D(2))], "hidden",
            [], Windows11, "explorer", [], ["Shown: folders look busier, and hidden app data is easier to delete by mistake"],
            "https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/ns-shlobj_core-shellstatea"),

        new("taskbar.search", "Search on the taskbar", "Chooses how search appears on the taskbar: hidden, an icon, or a search box.",
            "user", "HKCU", 64, Cu + @"\Search", "SearchboxTaskbarMode", "dword",
            [new("hidden", "Hidden", D(0)), new("icon", "Search icon only", D(1)), new("box", "Search box", D(2))], null,
            [Lm(@"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "SearchOnTaskbarMode")], 22621, "explorer", [],
            ["Hidden: open search with the Windows key and type instead"],
            "https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-search#configuresearchontaskbarmode"),

        new("startup.app_delay", "Startup delay for desktop apps", "Windows waits a few seconds after sign-in before starting desktop apps.",
            "user", "HKCU", 64, Cu + @"\Explorer\Serialize", "StartupDelayInMSec", "dword",
            [new("none", "No delay", D(0)), new("default", "Windows default", null)], "default",
            [], Windows11, "none", [], ["No delay: sign-in can feel busier while startup apps load"],
            // No Microsoft page documents this value; reviewed from the well-known Explorer behaviour (value 0 = no delay, absent = default).
            "reviewed: HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Serialize StartupDelayInMSec (no Microsoft documentation page)"),

        // ---- machine scope (m9 §3.2, P3b): administrator, through the broker, behind --enable-admin-actions.
        // Left out on purpose: diagnostic data level. Settings stores the user's choice as AllowTelemetry under
        // ...\CurrentVersion\Policies\DataCollection and touches more than one value; that exact write set isn't
        // confirmed on Windows 11, and a row is never guessed.

        new("startup.fast_startup", "Fast startup", "Keeps part of Windows ready after Shut down so the PC starts faster; Restart is not affected.",
            "machine", "HKLM", 64, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", "dword", OnOff(), null,
            [Lm(@"SOFTWARE\Policies\Microsoft\Windows\System", "HiberbootEnabled")], Windows11, "none", [],
            ["Off: starting the PC takes a little longer", "On: some drivers and updates only finish after a Restart, not a Shut down",
             "Has no effect while hibernation is off"],
            "reviewed: HiberbootEnabled under Session Manager Power (1 on, 0 off), the value Control Panel Power Options writes; policy HiberbootEnabled under Policies Windows System (Require use of fast startup)"),
    ];

    public static SettingRow? Find(string? settingId) => Rows.FirstOrDefault(r => r.SettingId == settingId);
}

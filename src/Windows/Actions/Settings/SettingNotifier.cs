using System.Runtime.InteropServices;

namespace OptimizationTool.Windows.Actions.Settings;

/// <summary>Tells Windows a setting changed, for rows whose notify is "setting_change" or "explorer". Tests use a fake.</summary>
internal interface ISettingNotifier
{
    void Notify(string kind);
}

/// <summary>
/// setting_change: broadcasts WM_SETTINGCHANGE ("ImmersiveColorSet", as Settings does for personalisation).
/// explorer: broadcasts WM_SETTINGCHANGE ("TraySettings") and SHChangeNotify(SHCNE_ASSOCCHANGED) so Explorer and the
/// taskbar re-read their options. Never blocks on a hung window (SMTO_ABORTIFHUNG, 500 ms).
/// </summary>
internal sealed partial class WindowsSettingNotifier : ISettingNotifier
{
    private const nint HwndBroadcast = 0xffff;
    private const uint WmSettingChange = 0x001A, SmtoAbortIfHung = 0x0002, TimeoutMs = 500;
    private const int ShcneAssocChanged = 0x08000000;

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SendMessageTimeout(nint hWnd, uint msg, nint wParam, string? lParam, uint flags, uint timeout, out nint result);

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);

    public void Notify(string kind)
    {
        switch (kind)
        {
            case "setting_change":
                SendMessageTimeout(HwndBroadcast, WmSettingChange, 0, "ImmersiveColorSet", SmtoAbortIfHung, TimeoutMs, out _);
                break;
            case "explorer":
                SendMessageTimeout(HwndBroadcast, WmSettingChange, 0, "TraySettings", SmtoAbortIfHung, TimeoutMs, out _);
                SHChangeNotify(ShcneAssocChanged, 0, 0, 0);
                break;
        }
    }
}

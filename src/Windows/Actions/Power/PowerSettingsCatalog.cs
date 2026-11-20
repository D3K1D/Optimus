namespace OptimizationTool.Windows.Actions.Power;

/// <summary>One allowed choice: the value the AI or UI names, its label, and the power value index written.</summary>
internal sealed record PowerChoice(string Value, string Label, uint Index);

/// <summary>
/// One reviewed power setting (contracts/m9-ai-powers.md §3.2): its subgroup and setting GUIDs, the choices that may be
/// written, and the largest index an undo may restore (the previous value need not be one of the choices).
/// </summary>
internal sealed record PowerSettingRow(
    string SettingId, string Label, Guid Subgroup, Guid Setting, IReadOnlyList<PowerChoice> Choices, uint MaxRestore,
    IReadOnlyList<string> Tradeoffs, bool Timeout)
{
    public string Resource(Guid scheme) => "power:" + scheme.ToString("D").ToUpperInvariant() + ":" + Setting.ToString("D").ToUpperInvariant();
}

/// <summary>
/// The allowlist for power.set_value. Only these settings: display, sleep and hibernate timeouts, lid and power button
/// actions, USB selective suspend and PCI Express link state power management. The processor subgroup
/// (54533251-82be-4824-96c1-47b60b740d00) and anything about frequency, boost, core parking, cooling policy or throttling
/// is not here and must never be added (§0.2, §3.4). GUIDs are Windows' documented power setting GUIDs (winnt.h / powercfg).
/// </summary>
internal static class PowerSettingsCatalog
{
    public static readonly Guid SubVideo = new("7516b95f-f776-4464-8c53-06167f40cc99");
    public static readonly Guid SubSleep = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    public static readonly Guid SubButtons = new("4f971e89-eebd-4455-a8de-9e59040e7347");
    public static readonly Guid SubUsb = new("2a737441-1930-4402-8d77-b2bebba308a3");
    public static readonly Guid SubPcie = new("501a4d13-42af-4429-9fd1-a8218c268e20");
    /// <summary>The processor power management subgroup: never listed (tests assert it).</summary>
    public static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");

    private static readonly int[] Minutes = [1, 2, 3, 5, 10, 15, 20, 25, 30, 45, 60, 120, 180, 240, 300];

    /// <summary>Never, then the minute steps Windows' own lists offer. Index = seconds.</summary>
    private static PowerChoice[] Timeouts() =>
    [
        new("never", "Never", 0),
        .. Minutes.Select(m => new PowerChoice(m.ToString(System.Globalization.CultureInfo.InvariantCulture), MinutesLabel(m), (uint)m * 60)),
    ];

    internal static string MinutesLabel(int m) => m switch
    {
        1 => "1 minute",
        < 60 => $"{m} minutes",
        60 => "1 hour",
        _ when m % 60 == 0 => $"{m / 60} hours",
        _ => $"{m} minutes",
    };

    private static PowerChoice[] Actions() =>
        [new("do_nothing", "Do nothing", 0), new("sleep", "Sleep", 1), new("hibernate", "Hibernate", 2), new("shut_down", "Shut down", 3)];

    public static readonly IReadOnlyList<PowerSettingRow> Rows =
    [
        new("power.display_off", "Turn off the screen after", SubVideo, new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e"), Timeouts(), uint.MaxValue,
            ["A shorter time saves power; the screen goes dark sooner when you pause."], Timeout: true),
        new("power.sleep", "Sleep after", SubSleep, new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da"), Timeouts(), uint.MaxValue,
            ["Sleeping saves power; downloads and remote access pause while it sleeps."], Timeout: true),
        new("power.hibernate", "Hibernate after", SubSleep, new("9d7815a6-7ee4-497e-8888-515a05f02364"), Timeouts(), uint.MaxValue,
            ["Hibernating uses no power but takes longer to resume."], Timeout: true),
        new("power.lid_close", "When the lid is closed", SubButtons, new("5ca83367-6e45-459f-a27b-476b1d01c936"), Actions(), 4,
            ["Do nothing keeps the PC running with the lid closed, which can trap heat in a bag."], Timeout: false),
        new("power.power_button", "When the power button is pressed", SubButtons, new("7648efa3-dd9c-4e3e-b566-50f929386280"), Actions(), 4,
            ["Changes what a short press of the power button does."], Timeout: false),
        new("power.usb_selective_suspend", "USB selective suspend", SubUsb, new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226"),
            [new("disabled", "Off", 0), new("enabled", "On", 1)], 1,
            ["On saves power; a few USB devices misbehave when they are suspended."], Timeout: false),
        new("power.pcie_link_state", "PCI Express power saving", SubPcie, new("ee12f906-d277-404b-b6da-e5fa1a576df5"),
            [new("off", "Off", 0), new("moderate", "Moderate power savings", 1), new("maximum", "Maximum power savings", 2)], 2,
            ["More savings can add a little delay when a device wakes; Off uses more power."], Timeout: false),
    ];

    /// <summary>What the user sees for an index: its choice label, a timeout in words, or "Other value (n)".</summary>
    public static string Text(PowerSettingRow row, uint index)
    {
        if (row.Choices.FirstOrDefault(c => c.Index == index) is { } c) return c.Label;
        if (row.Timeout && index % 60 == 0 && index / 60 <= int.MaxValue) return MinutesLabel((int)(index / 60));
        if (row.Timeout) return $"{index} seconds";
        return $"Other value ({index})";
    }
}

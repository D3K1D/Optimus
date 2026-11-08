using OptimizationTool.Engine;
using OptimizationTool.Windows.Reliability;
using OptimizationTool.Windows.Reliability.Rules;

namespace OptimizationTool.Windows.Tests.Reliability;

public class EventsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 9, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);

    internal static RawEvent Ev(string provider, int id, int level, DateTimeOffset t, params (string? Name, string Value)[] data) =>
        new(provider, id, level, t, data.Select(d => new KeyValuePair<string?, string>(d.Name, d.Value)).ToList());

    internal static RawEvent KernelPower(DateTimeOffset t, int bugcheck = 0, string powerButton = "0") =>
        Ev("Microsoft-Windows-Kernel-Power", 41, 1, t, ("BugcheckCode", bugcheck.ToString()), ("BugcheckParameter1", "0x0"), ("SleepInProgress", "0"), ("PowerButtonTimestamp", powerButton));

    internal static RawEvent Bugcheck(DateTimeOffset t, string param1) =>
        Ev("Microsoft-Windows-WER-SystemErrorReporting", 1001, 2, t, ("param1", param1), ("param2", @"C:\Windows\Minidump\fake.dmp"), ("param3", "fake-report-id"));

    internal static RawEvent AppError(DateTimeOffset t, string app) =>
        Ev("Application Error", 1000, 2, t, ("AppName", app), ("AppVersion", "1.0.0.0"), ("AppTimeStamp", "00000000"), ("ModuleName", "contoso.dll"),
            ("ModuleVersion", "1.0"), ("ModuleTimeStamp", "0"), ("ExceptionCode", "c0000005"), ("FaultingOffset", "0x1"), ("ProcessId", "0x1"),
            ("ProcessCreationTime", "0x1"), ("AppPath", @"C:\Users\someone\AppData\Local\Contoso\contoso.exe"));

    internal static RawEvent UpdateFail(DateTimeOffset t, string code, string guid) =>
        Ev("Microsoft-Windows-WindowsUpdateClient", 20, 2, t, ("errorCode", code), ("updateTitle", "Synthetic Update for Testing (KB0000001)"), ("updateGuid", guid));

    internal static RawEvent UpdateOk(DateTimeOffset t, string guid) =>
        Ev("Microsoft-Windows-WindowsUpdateClient", 19, 4, t, ("updateTitle", "Synthetic Update"), ("updateGuid", guid));

    internal static CollectorResult Normalize(EventChannelSpec spec, params RawEvent[] events) =>
        EventNormalizer.Normalize(spec, new RawChannel("ok", null, events, false, false, 0), Week, 2000);

    [Fact]
    public void Groups_kernel_power_with_codes_and_power_button()
    {
        var r = Normalize(EventChannelSpec.System, KernelPower(T0), KernelPower(T0.AddHours(1), 0x9f), KernelPower(T0.AddHours(2), 0, "133000000000000000"),
            Bugcheck(T0.AddHours(1).AddMinutes(2), "0x0000009f (0x0000000000000003, 0xffff000000000000)"));
        Assert.Equal("complete", r.Coverage.Status);
        Assert.Equal(4, r.Coverage.Observations);
        var kp = r.Facts.Single(f => f.Subject == "events.system.kernel-power.41");
        Assert.Equal(3, RuleKit.Int(kp.Value["count"]));
        Assert.Equal(1, RuleKit.Int(kp.Value["powerButtonCount"]));
        Assert.Equal(1, RuleKit.Int(kp.Value["bugcheckCodes"]!["0x9f"]));
        Assert.Equal(3, kp.Value["times"]!.AsArray().Count);
        Assert.Equal("P7D", kp.Interval);
        var bc = r.Facts.Single(f => f.Subject == "events.system.wer-systemerrorreporting.1001.0x9f");
        Assert.Equal("0x9f", RuleKit.Str(bc.Value["bugcheckCode"]));
        Assert.Contains(r.Facts, f => f.Subject == "events.system.coverage");
        Assert.Equal(r.Facts.Select(f => f.Subject).Order(StringComparer.Ordinal), r.Facts.Select(f => f.Subject));
        // No paths or report IDs from EventData leak into evidence.
        var all = string.Join("\n", r.Facts.Select(f => f.Value.ToJsonString() + f.Display));
        Assert.DoesNotContain("Minidump", all);
        Assert.DoesNotContain("fake-report-id", all);
    }

    [Fact]
    public void Groups_app_crashes_by_app_without_paths()
    {
        var r = Normalize(EventChannelSpec.Application, AppError(T0, "contoso.exe"), AppError(T0.AddHours(1), "contoso.exe"), AppError(T0.AddHours(2), "fabrikam.exe"));
        var crashes = r.Facts.Where(f => f.Subject.StartsWith(AppCrashesRule.Prefix, StringComparison.Ordinal)).ToList();
        Assert.Equal(2, crashes.Count);
        var c = crashes.Single(f => RuleKit.Str(f.Value["appName"]) == "contoso.exe");
        Assert.Equal(2, RuleKit.Int(c.Value["count"]));
        Assert.Equal(2, RuleKit.Int(c.Value["exceptionCodes"]!["0xc0000005"]));
        Assert.DoesNotContain("someone", string.Join("\n", r.Facts.Select(f => f.Value.ToJsonString() + f.Display)));
    }

    [Fact]
    public void Update_failures_group_by_error_code()
    {
        var r = Normalize(EventChannelSpec.Update, UpdateFail(T0, "0x80070643", "g1"), UpdateFail(T0.AddDays(1), "0x80070643", "g1"), UpdateOk(T0.AddDays(2), "g2"));
        var f = r.Facts.Single(x => x.Subject == "events.update.windowsupdateclient.20.0x80070643");
        Assert.Equal(2, RuleKit.Int(f.Value["count"]));
        Assert.Equal(1, RuleKit.Int(f.Value["distinctUpdates"]));
        Assert.Contains(r.Facts, x => x.Subject == "events.update.windowsupdateclient.19");
    }

    [Fact]
    public void Row_cap_is_partial_and_failures_map_to_coverage()
    {
        var capped = EventNormalizer.Normalize(EventChannelSpec.System, new RawChannel("ok", null, [KernelPower(T0)], true, false, 0), Week, 1);
        Assert.Equal("partial", capped.Coverage.Status);
        Assert.Contains("row cap", capped.Coverage.Reason);
        Assert.Equal("unsupported", EventNormalizer.Normalize(EventChannelSpec.Defender, new RawChannel("missing", "Channel not found.", [], false, false, 0), Week, 10).Coverage.Status);
        Assert.Equal("unsupported", EventNormalizer.Normalize(EventChannelSpec.Defender, new RawChannel("disabled", "Channel disabled.", [], false, false, 0), Week, 10).Coverage.Status);
        Assert.Equal("denied", EventNormalizer.Normalize(EventChannelSpec.Defender, new RawChannel("denied", "Access denied.", [], false, false, 0), Week, 10).Coverage.Status);
        var empty = Normalize(EventChannelSpec.Update);
        Assert.Equal("complete", empty.Coverage.Status);
        Assert.Single(empty.Facts);
    }

    [Fact]
    public void XPath_and_hex_and_xml_parse()
    {
        Assert.Contains("timediff(@SystemTime) <= 604800000", EventChannelSpec.System.XPath(Week));
        Assert.Contains("Level=2", EventChannelSpec.System.XPath(Week));
        Assert.DoesNotContain("Level", EventChannelSpec.Update.XPath(Week));
        Assert.Equal("0x9f", EventNormalizer.Hex("0x0000009f (0x1, 0x2)"));
        Assert.Equal("0xc0000005", EventNormalizer.Hex("c0000005"));
        Assert.Null(EventNormalizer.Hex("not a code"));

        const string xml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event"><System><Provider Name="Microsoft-Windows-Kernel-Power"/>
            <EventID>41</EventID><Level>1</Level><TimeCreated SystemTime="2026-01-09T08:00:00.0000000Z"/></System>
            <EventData><Data Name="BugcheckCode">0</Data><Data Name="PowerButtonTimestamp">0</Data></EventData></Event>
            """;
        var e = EventReader.Parse(xml)!;
        Assert.Equal(41, e.EventId);
        Assert.Equal("0", e.Field("BugcheckCode", 0));
        Assert.Null(EventReader.Parse("<broken"));
    }
}

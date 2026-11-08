using OptimizationTool.Engine;
using OptimizationTool.Windows.Reliability;
using OptimizationTool.Windows.Reliability.Rules;

namespace OptimizationTool.Windows.Tests.Reliability;

public class SettingsAndInventoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);

    // ---- devices ----

    private static RawDevice Dev(string id, int problem, string cls = "Net", DateTimeOffset? date = null) =>
        new(id, $"Contoso {cls} Device", cls, "Contoso", "1.2.3.4", date ?? Now.AddDays(-30), true, problem);

    [Fact]
    public void Pnp_emits_summary_and_problem_devices_only_without_instance_ids()
    {
        var r = PnpNormalizer.Normalize(new RawDevices([Dev(@"PCI\VEN_FAKE&DEV_0001\1", 0), Dev(@"USB\VID_FAKE\2", 28, "USB", Now.AddYears(-3)), Dev(@"PCI\VEN_FAKE\3", 22)], false, 0), Now);
        Assert.Equal("complete", r.Coverage.Status);
        Assert.Equal(3, r.Facts.Count);
        var summary = r.Facts.Single(f => f.Subject == "devices.pnp.summary");
        Assert.Equal(2, RuleKit.Int(summary.Value["problemCount"]));
        Assert.Equal(1, RuleKit.Int(summary.Value["driversOlderThan2YearsCount"]));
        Assert.Equal(1, RuleKit.Int(summary.Value["thirdPartyDriversOlderThan2YearsCount"]));
        Assert.DoesNotContain("VEN_FAKE", string.Join("\n", r.Facts.Select(f => f.Value.ToJsonString() + f.Display + f.Subject)));
    }

    [Fact]
    public void Pnp_truncated_is_partial()
    {
        Assert.Equal("partial", PnpNormalizer.Normalize(new RawDevices([Dev("X\\1", 0)], true, 0), Now).Coverage.Status);
    }

    [Fact]
    public void Device_problem_rule_severity_and_benign_disabled()
    {
        var ctx = TestKit.Ctx("win.devices.pnp", PnpNormalizer.Normalize(new RawDevices([Dev("A\\1", 10), Dev("A\\2", 28), Dev("A\\3", 22)], false, 0), Now));
        var r = new DeviceProblemRule().Evaluate(ctx);
        Assert.Equal(2, r.Findings.Count);
        Assert.Contains(r.Findings, f => f.Severity == "medium" && f.Card.Text.Contains("code 10", StringComparison.Ordinal));
        Assert.Contains(r.Findings, f => f.Severity == "low" && f.Card.Text.Contains("code 28", StringComparison.Ordinal));
        foreach (var f in r.Findings) TestKit.AssertCard(f);

        var benign = TestKit.Ctx("win.devices.pnp", PnpNormalizer.Normalize(new RawDevices([Dev("A\\1", 0), Dev("A\\3", 22)], false, 0), Now));
        Assert.Equal("clear", new DeviceProblemRule().Evaluate(benign).Status);
        Assert.Equal("insufficient", new DeviceProblemRule().Evaluate(TestKit.Missing("win.devices.pnp", "failed")).Status);
    }

    // ---- apps ----

    private static RawUninstallEntry U(string scope, string key, string name, long? kb = null, bool sys = false, bool parent = false, string? release = null, string version = "1.0") =>
        new(scope, key, name, "Contoso", version, kb, sys, parent, release);

    private static readonly Dictionary<string, string> AllOk = new() { ["hklm64"] = "ok", ["hklm32"] = "ok", ["hkcu"] = "ok", ["packages"] = "ok" };

    [Fact]
    public void Apps_skip_components_updates_and_dedupe()
    {
        var raw = new RawApps([
            U("hklm64", "{A}", "Contoso Studio", 6L * 1024 * 1024),
            U("hklm32", "{A2}", "Contoso Studio", 6L * 1024 * 1024),
            U("hklm64", "{B}", "Contoso Helper", sys: true),
            U("hklm64", "{C}", "Contoso Patch", parent: true),
            U("hklm64", "KB0000001", "Security Update for Contoso (KB0000001)"),
            U("hkcu", "{D}", "Contoso Notes", release: "Hotfix"),
            U("hkcu", "{E}", "Contoso Visual C++ Redistributable", 30L * 1024 * 1024),
            U("hkcu", "{F}", "Fabrikam Reader"),
        ], [new RawPackage("Fabrikam.Paint_8wekyb3d8bbwe", "Fabrikam Paint", "Fabrikam", "1.0.0.0")], AllOk);
        var r = AppsNormalizer.Normalize(raw);
        Assert.Equal("complete", r.Coverage.Status);
        var apps = r.Facts.Where(f => f.Subject.StartsWith("apps.installed.", StringComparison.Ordinal) && f.Subject != "apps.installed.summary").ToList();
        Assert.Equal(3, apps.Count);
        var studio = apps.Single(f => RuleKit.Str(f.Value["name"]) == "Contoso Studio");
        Assert.Equal("reported", studio.Quality);
        Assert.Equal(6L * 1024 * 1024 * 1024, RuleKit.Long(studio.Value["sizeBytes"]));
        Assert.Equal("runtime", RuleKit.Str(apps.Single(f => RuleKit.Str(f.Value["name"])!.Contains("Redistributable", StringComparison.Ordinal)).Value["kind"]));
        var pkg = Assert.Single(r.Facts, f => f.Subject.StartsWith("apps.package.", StringComparison.Ordinal));
        Assert.Null(RuleKit.Long(pkg.Value["sizeBytes"]));
        var summary = r.Facts.Single(f => f.Subject == "apps.installed.summary");
        Assert.Equal(1, RuleKit.Int(summary.Value["skippedSystemComponentCount"]));
        Assert.Equal(3, RuleKit.Int(summary.Value["skippedUpdateCount"]));
        Assert.DoesNotContain("UninstallString", string.Join("\n", r.Facts.Select(f => f.Value.ToJsonString())), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apps_package_failure_is_partial()
    {
        var r = AppsNormalizer.Normalize(new RawApps([U("hkcu", "{F}", "Fabrikam Reader")], [], new Dictionary<string, string>(AllOk) { ["packages"] = "COMException" }));
        Assert.Equal("partial", r.Coverage.Status);
        Assert.Contains("packages", r.Coverage.Reason);
    }

    [Fact]
    public void Large_installs_lists_top_apps_and_skips_runtimes()
    {
        var raw = new RawApps([
            U("hklm64", "{A}", "Contoso Studio", 6L * 1024 * 1024),
            U("hklm64", "{B}", "Contoso Game", 40L * 1024 * 1024),
            U("hklm64", "{C}", "Contoso SDK", 9L * 1024 * 1024),
            U("hklm64", "{D}", "Small Tool", 1024),
        ], [], AllOk);
        var f = Assert.Single(new LargeInstallsRule().Evaluate(TestKit.Ctx("win.apps.installed", AppsNormalizer.Normalize(raw))).Findings);
        Assert.Equal("info", f.Severity);
        Assert.Equal(3, f.EvidenceIds.Count); // two apps + summary
        Assert.Contains("led by Contoso Game (40 GB)", f.Card.Text);
        Assert.DoesNotContain("unused", f.Card.Text, StringComparison.OrdinalIgnoreCase);
        TestKit.AssertCard(f);

        var small = new RawApps([U("hklm64", "{D}", "Small Tool", 1024), U("hklm64", "{C}", "Contoso SDK", 9L * 1024 * 1024)], [], AllOk);
        Assert.Equal("clear", new LargeInstallsRule().Evaluate(TestKit.Ctx("win.apps.installed", AppsNormalizer.Normalize(small))).Status);
        Assert.Equal("insufficient", new LargeInstallsRule().Evaluate(TestKit.Missing("win.apps.installed")).Status);
    }

    // ---- diagnostic data ----

    private static CollectorResult Diag(RegDword policy, RegDword user) => DiagDataNormalizer.Normalize(new RawDiagData(policy, user));

    [Fact]
    public void Diag_states_managed_known_unknown()
    {
        var managed = Diag(new(true, 1), new(true, 3)).Facts.Single();
        Assert.Equal("managed", RuleKit.Str(managed.Value["state"]));
        Assert.Equal("required", RuleKit.Str(managed.Value["effective"]));
        Assert.Equal("optional", RuleKit.Str(Diag(RegDword.Absent, new(true, 3)).Facts.Single().Value["effective"]));
        Assert.Equal("unknown", RuleKit.Str(Diag(RegDword.Absent, RegDword.Absent).Facts.Single().Value["state"]));
        Assert.Equal("denied", Diag(new(false, null, true), new(false, null, true)).Coverage.Status);
    }

    [Fact]
    public void Optional_diag_rule()
    {
        var on = new OptionalDiagRule().Evaluate(TestKit.Ctx("win.privacy.diagdata", Diag(RegDword.Absent, new(true, 3))));
        var f = Assert.Single(on.Findings);
        Assert.Equal("low", f.Severity);
        Assert.Equal("ms-settings:privacy-feedback", f.Card.SettingsUri);
        TestKit.AssertCard(f);
        Assert.Equal("clear", new OptionalDiagRule().Evaluate(TestKit.Ctx("win.privacy.diagdata", Diag(RegDword.Absent, new(true, 1)))).Status);
        Assert.Equal("inapplicable", new OptionalDiagRule().Evaluate(TestKit.Ctx("win.privacy.diagdata", Diag(new(true, 3), new(true, 3)))).Status);
        Assert.Equal("insufficient", new OptionalDiagRule().Evaluate(TestKit.Ctx("win.privacy.diagdata", Diag(RegDword.Absent, RegDword.Absent))).Status);
        Assert.Equal("insufficient", new OptionalDiagRule().Evaluate(TestKit.Missing("win.privacy.diagdata")).Status);
    }

    // ---- start ----

    [Fact]
    public void Start_absent_assumes_default_on_with_medium_confidence()
    {
        var absent = StartNormalizer.Normalize(new RawStart(RegDword.Absent, RegDword.Absent, RegDword.Absent));
        Assert.Equal("estimated", absent.Facts.Single().Quality);
        var f = Assert.Single(new StartSuggestionsRule().Evaluate(TestKit.Ctx("win.custom.start", absent)).Findings);
        Assert.Equal("medium", f.Confidence);
        Assert.Equal("info", f.Severity);
        TestKit.AssertCard(f);

        var on = StartNormalizer.Normalize(new RawStart(new(true, 1), RegDword.Absent, RegDword.Absent));
        Assert.Equal("high", new StartSuggestionsRule().Evaluate(TestKit.Ctx("win.custom.start", on)).Findings.Single().Confidence);
        var off = StartNormalizer.Normalize(new RawStart(new(true, 0), new(true, 1), new(true, 1)));
        Assert.Equal("clear", new StartSuggestionsRule().Evaluate(TestKit.Ctx("win.custom.start", off)).Status);
        Assert.Equal("insufficient", new StartSuggestionsRule().Evaluate(TestKit.Missing("win.custom.start")).Status);
    }

    // ---- display ----

    [Fact]
    public void Display_normalizes_and_rule_flags_gap()
    {
        var r = DisplayNormalizer.Normalize([
            new RawDisplay(@"\\.\DISPLAY1", true, 2560, 1440, 165, [60, 120, 165]),
            new RawDisplay(@"\\.\DISPLAY2", false, 1920, 1080, 60, [60, 144]),
        ]);
        Assert.Equal(new[] { "display.display1", "display.display2" }, r.Facts.Select(f => f.Subject));
        var res = new DisplayRefreshRule().Evaluate(TestKit.Ctx("win.custom.display", r));
        var f = Assert.Single(res.Findings);
        Assert.Equal("low", f.Severity);
        Assert.Equal("Display 2 runs at 60 Hz but supports 144 Hz. Would you like to review it?", f.Card.Text);
        TestKit.AssertCard(f);
    }

    [Fact]
    public void Display_rounding_gap_is_clear_and_unknown_is_insufficient()
    {
        var rounding = DisplayNormalizer.Normalize([new RawDisplay(@"\\.\DISPLAY1", true, 1920, 1080, 59, [59, 60])]);
        Assert.Equal("clear", new DisplayRefreshRule().Evaluate(TestKit.Ctx("win.custom.display", rounding)).Status);
        var unknown = DisplayNormalizer.Normalize([new RawDisplay(@"\\.\DISPLAY1", true, 1920, 1080, 1, [])]);
        Assert.Equal("insufficient", new DisplayRefreshRule().Evaluate(TestKit.Ctx("win.custom.display", unknown)).Status);
        Assert.Equal("insufficient", new DisplayRefreshRule().Evaluate(TestKit.Missing("win.custom.display")).Status);
        Assert.Equal("inapplicable", new DisplayRefreshRule().Evaluate(TestKit.Ctx("win.custom.display", DisplayNormalizer.Normalize([]))).Status);
    }
}

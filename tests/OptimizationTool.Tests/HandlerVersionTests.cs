using System.Text.RegularExpressions;

namespace OptimizationTool.Tests;

/// <summary>
/// handlerVersion is bound by the plan hash, the fix step's content hash, the broker's version check and the
/// stale_plan drift check, so a handler's version is raised whenever its behaviour changes (wording alone does not).
/// 1.1.0: behaviour changed in the M9 review fixes — explorer.restart report-only; network.flush_dns and the repairs
/// report completed, never verified (repairs also always name a result); service/startup/task.disable, firewall.block_app,
/// app.set_gpu_preference and process.end protection and undo checks; manual.open page-less guided tasks (design v60/v63).
/// </summary>
public partial class HandlerVersionTests
{
    private static readonly Dictionary<string, string> Raised = new()
    {
        ["explorer.restart"] = "1.1.0", ["network.flush_dns"] = "1.1.0", ["repair.sfc"] = "1.1.0", ["repair.dism_restorehealth"] = "1.1.0",
        ["service.disable"] = "1.1.0", ["startup.disable"] = "1.1.0", ["task.disable"] = "1.1.0", ["firewall.block_app"] = "1.1.0",
        ["app.set_gpu_preference"] = "1.1.0", ["process.end"] = "1.1.0", ["manual.open"] = "1.1.0",
    };

    [Fact]
    public void Every_handler_version_is_pinned()
    {
        var actual = AppGuideTests.RealCatalog().ToDictionary(a => a.ActionId, a => a.Version);
        foreach (var (id, version) in Raised) Assert.True(actual.TryGetValue(id, out var v) && v == version, $"{id}: {v ?? "missing"}, pinned {version}");
        foreach (var (id, version) in actual.Where(a => !Raised.ContainsKey(a.Key)))
            Assert.True(version == "1.0.0", $"{id} is {version}: pin it here (and in the fixture's HANDLER_VERSIONS)");
    }

    [GeneratedRegex(@"var HANDLER_VERSIONS = \{(?<body>.*?)\};", RegexOptions.Singleline)]
    private static partial Regex FixtureTable();

    [GeneratedRegex(@"'(?<id>[^']+)':\s*'(?<v>[^']+)'")]
    private static partial Regex FixtureRow();

    /// <summary>The fixture's HANDLER_VERSIONS (ui/js/adapter-fixture-actions.js) lists exactly the raised versions.</summary>
    [Fact]
    public void The_fixture_mirrors_the_raised_versions()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OptimizationTool.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var js = File.ReadAllText(Path.Combine(dir!.FullName, "ui", "js", "adapter-fixture-actions.js"));
        var body = FixtureTable().Match(js);
        Assert.True(body.Success, "HANDLER_VERSIONS not found in adapter-fixture-actions.js");
        var rows = FixtureRow().Matches(body.Groups["body"].Value).ToDictionary(m => m.Groups["id"].Value, m => m.Groups["v"].Value);
        Assert.Equal(Raised.OrderBy(k => k.Key), rows.OrderBy(k => k.Key));
    }
}

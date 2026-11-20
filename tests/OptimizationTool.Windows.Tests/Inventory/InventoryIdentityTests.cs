using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Windows.Inventory;
using OptimizationTool.Windows.Reliability;

namespace OptimizationTool.Windows.Tests.Inventory;

/// <summary>
/// m9 review R3, R3b: the user, PC and domain names never reach a stored inventory item, through the whole collector path
/// (raw source → <see cref="InventoryKit.Clean"/>/<see cref="InventoryKit.Version"/> → stored item). One rule: an identity
/// of 3+ folded characters matches by containment anywhere (separators, case and full-width forms folded away); a 1–2
/// character identity matches as a whole token (split at separators, letter↔digit and lower→upper case), and so does the
/// domain name at every length (a domain equal to the PC name is the PC name).
/// </summary>
public sealed class InventoryIdentityTests
{
    private static RawUninstallEntry App(string name, string? publisher, string? version) => new("hklm64", name, name, publisher, version, null, false, false, null);

    private static async Task<JsonArray> Stored(IReadOnlyList<LocalIdentity> ids, params RawUninstallEntry[] entries)
    {
        var status = new Dictionary<string, string> { ["hklm64"] = "ok", ["hklm32"] = "ok", ["hkcu"] = "ok", ["packages"] = "ok" };
        var r = await new ProgramsInventoryCollector(_ => new RawApps(entries, [], status), ids).CollectAsync(default);
        return (JsonArray)Assert.Single(r.Facts).Value["items"]!;
    }

    private static string Row(JsonNode? i) => $"{i!["name"]}|{i["publisher"]}|{i["version"]}";

    [Fact]
    public async Task Stored_items_drop_long_identities_by_containment_in_name_publisher_and_version()
    {
        var items = await Stored(["jamie", "DESKTOP-AB12"],
            App("Realtek Audio", "Realtek", "6.0.9235.1"),
            App("jamieBackup", "Adatum", "1.0"),                  // concatenated
            App("jamie_backup2", "Adatum", "1.0"),                // underscore and digit
            App("7-Zip 24.08", "Igor Pavlov", "24.08"),
            App("DESKTOP-AB12Tools", "Contoso", "2.0"),           // the PC name glued to a word
            App("Contoso Tool", "Contoso", "1.0-jamie"),          // in the version: version dropped
            App("Litware VPN", "Litware", "3.0jamie"),            // glued into the version: version dropped
            App("Fabrikam Sync", "my-jamie", "4.1"),              // in the publisher: publisher dropped
            App("ｊａｍｉｅ Notes", "Litware", "1.0"),             // full-width
            App("Wingtip j.a.m.i.e", "Wingtip", "1.0"));          // dots between letters
        var rows = items.Select(Row).OrderBy(s => s, StringComparer.Ordinal).ToList();
        Assert.Equal(
        [
            "7-Zip 24.08|Igor Pavlov|24.08",
            "Contoso Tool|Contoso|",
            "Fabrikam Sync||4.1",
            "Litware VPN|Litware|",
            "Realtek Audio|Realtek|6.0.9235.1",
        ], rows);
    }

    [Fact]
    public async Task Stored_items_drop_an_identity_with_separators_in_the_text_because_separators_are_removed()
    {
        var items = await Stored(["jamie"], App("ja-mie sync", "Litware", "1.0"), App("Adatum PDF Reader", "Adatum", "11.0.2"));
        Assert.Equal(["Adatum PDF Reader|Adatum|11.0.2"], items.Select(Row));
    }

    [Fact]
    public async Task Stored_items_match_one_and_two_character_names_as_whole_tokens_only()
    {
        var items = await Stored(["Al", "JD", "Q"],
            App("AlBackup", "Adatum", "1.0"),                     // camelCase token
            App("Al Tools", "Adatum", "1.0"),
            App("backup-al", "Adatum", "1.0"),
            App("JD2 Suite", "Adatum", "1.0"),                     // letter→digit token
            App("Q Notes", "Adatum", "1.0"),                       // a 1-character name is not skipped
            App("Realtek Audio", "Realtek", "6.0.9235.1"),
            App("Calculator Plus", "Contoso", "2.0"),
            App("Quicken", "Contoso", "3.0"),
            App("Litware VPN", "Litware", "1.0-JD"));              // a 2-character name in the version
        var rows = items.Select(Row).OrderBy(s => s, StringComparer.Ordinal).ToList();
        Assert.Equal(["Calculator Plus|Contoso|2.0", "Litware VPN|Litware|", "Quicken|Contoso|3.0", "Realtek Audio|Realtek|6.0.9235.1"], rows);
    }

    [Fact]
    public async Task Stored_items_match_the_domain_name_only_as_a_whole_token()
    {
        var items = await Stored(IdentityFilter.Local("jamie", "DESKTOP-AB12", "CORP"),
            App("Microsoft Corporation Help", "Microsoft Corporation", "10.0"),
            App("CORP Tools", "Adatum", "1.0"),
            App("corp-backup", "Adatum", "1.0"),
            App("Corporate Suite", "Adatum", "1.0"),
            App("Contoso Tool", "Contoso", "2.0-corp"));          // in the version: version dropped
        var rows = items.Select(Row).OrderBy(s => s, StringComparer.Ordinal).ToList();
        Assert.Equal(["Contoso Tool|Contoso|", "Corporate Suite|Adatum|1.0", "Microsoft Corporation Help|Microsoft Corporation|10.0"], rows);
    }

    [Fact]
    public async Task Stored_items_treat_a_domain_equal_to_the_pc_name_as_the_pc_name()
    {
        var items = await Stored(IdentityFilter.Local("jamie", "DESKTOP-AB12", "DESKTOP-AB12"),
            App("DESKTOP-AB12Tools", "Contoso", "2.0"), App("Adatum PDF Reader", "Adatum", "11.0.2"));
        Assert.Equal(["Adatum PDF Reader|Adatum|11.0.2"], items.Select(Row));
    }

    [Fact]
    public async Task Stored_items_match_a_three_character_name_by_containment()
    {
        var items = await Stored(["pat"], App("Patcher", "Adatum", "1.0"), App("my_PAT_tool", "Adatum", "1.0"), App("Adatum PDF Reader", "Adatum", "11.0.2"));
        Assert.Equal(["Adatum PDF Reader|Adatum|11.0.2"], items.Select(Row));
    }
}

using System.Diagnostics;
using System.Text.Json.Nodes;
using OptimizationTool.Desktop.Bridge;

namespace OptimizationTool.Desktop.Tests;

public class SettingsLauncherTests
{
    private const string Doc = "https://app.optimizationtool.invalid/index.html";

    /// <summary>Records opens instead of launching anything; the clock advances only when told.</summary>
    private sealed class Harness
    {
        public readonly List<string> Opened = [];
        public long Now = 1_000_000;
        public readonly SettingsLauncher Launcher;
        public readonly BridgeDispatcher Dispatcher;
        public readonly ThrowingBackend Backend = new();

        public Harness()
        {
            Launcher = new SettingsLauncher(Opened.Add, () => Now);
            Dispatcher = new BridgeDispatcher(new BridgeRouter(), Backend, DesktopLog.Disabled, Launcher);
        }

        public void Advance(TimeSpan t) => Now += (long)(t.TotalSeconds * Stopwatch.Frequency);

        public async Task<JsonNode> Call(string paramsJson, int id = 1) =>
            JsonNode.Parse((await Dispatcher.HandleAsync(
                $$$"""{"type":"rpc","id":{{{id}}},"method":"shell.openSettings","params":{{{paramsJson}}}}""", Doc, true, default))!)!;
    }

    /// <summary>shell.openSettings must never reach the backend.</summary>
    private sealed class ThrowingBackend : IBackend
    {
        public int Calls;
        public Task<JsonObject> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("forwarded");
        }
    }

    public static TheoryData<string> CatalogUris => new(SettingsLauncher.Catalog);

    [Theory]
    [MemberData(nameof(CatalogUris))]
    public async Task Opens_every_catalog_uri_without_forwarding(string uri)
    {
        var h = new Harness();
        var reply = await h.Call(new JsonObject { ["uri"] = uri }.ToJsonString(), id: 7);
        Assert.Equal("rpc.result", reply["type"]!.GetValue<string>());
        Assert.Equal(7, reply["id"]!.GetValue<int>());
        Assert.True(reply["result"]!["opened"]!.GetValue<bool>());
        Assert.Equal([uri], h.Opened);
        Assert.Equal(0, h.Backend.Calls);
    }

    [Fact]
    public void Catalog_matches_m2_collectors_section_5()
    {
        Assert.Equal(12, SettingsLauncher.Catalog.Count);
        Assert.All(SettingsLauncher.Catalog, u => Assert.True(u.StartsWith("ms-settings:", StringComparison.Ordinal) || u.StartsWith("windowsdefender://", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("ms-settings:display-advanced&x")]
    [InlineData("ms-settings:display-advanced?x=1")]
    [InlineData("ms-settings:display-advanced#x")]
    [InlineData("ms-settings:display-advanced/")]
    [InlineData("ms-settings:display-advanced ")]
    [InlineData(" ms-settings:display-advanced")]
    [InlineData("ms-settings:display-advanced\n")]
    [InlineData("ms-settings:display-advanced\u0000")]
    [InlineData("ms-settings:display-advanced\" & calc")]
    [InlineData("MS-SETTINGS:display-advanced")]
    [InlineData("ms-settings:Display-Advanced")]
    [InlineData("WindowsDefender://threat")]
    [InlineData("ms-settings:")]
    [InlineData("ms-settings:network")]
    [InlineData("windowsdefender:threat")]
    [InlineData("http://example.com/")]
    [InlineData("https://app.optimizationtool.invalid/index.html")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("C:\\Windows\\System32\\calc.exe")]
    [InlineData("calc")]
    [InlineData("")]
    public async Task Rejects_anything_not_exactly_in_the_catalog(string uri)
    {
        var h = new Harness();
        var reply = await h.Call(new JsonObject { ["uri"] = uri }.ToJsonString());
        Assert.Equal(-32602, reply["error"]!["code"]!.GetValue<int>());
        Assert.Empty(h.Opened);
        Assert.Equal(0, h.Backend.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"uri":7}""")]
    [InlineData("""{"uri":null}""")]
    [InlineData("""{"uri":["ms-settings:troubleshoot"]}""")]
    [InlineData("""{"uri":"ms-settings:troubleshoot","args":"x"}""")]
    [InlineData("null")]
    public async Task Rejects_malformed_params(string paramsJson)
    {
        var h = new Harness();
        var reply = await h.Call(paramsJson);
        Assert.Equal(-32602, reply["error"]!["code"]!.GetValue<int>());
        Assert.Empty(h.Opened);
    }

    [Fact]
    public async Task Rate_limits_to_one_open_per_second()
    {
        var h = new Harness();
        Assert.True((await h.Call("""{"uri":"ms-settings:troubleshoot"}"""))["result"]!["opened"]!.GetValue<bool>());

        h.Advance(TimeSpan.FromMilliseconds(500));
        var limited = await h.Call("""{"uri":"ms-settings:startupapps"}""");
        Assert.Equal(-32013, limited["error"]!["code"]!.GetValue<int>());
        Assert.Equal("rate_limited", limited["error"]!["data"]!["reason"]!.GetValue<string>());

        h.Advance(TimeSpan.FromMilliseconds(600));
        Assert.True((await h.Call("""{"uri":"ms-settings:startupapps"}"""))["result"]!["opened"]!.GetValue<bool>());
        Assert.Equal(["ms-settings:troubleshoot", "ms-settings:startupapps"], h.Opened);
    }

    [Fact]
    public async Task Refused_calls_do_not_consume_the_rate_limit()
    {
        var h = new Harness();
        await h.Call("""{"uri":"http://example.com/"}""");
        Assert.True((await h.Call("""{"uri":"ms-settings:troubleshoot"}"""))["result"]!["opened"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Launch_failure_is_an_error_not_a_crash()
    {
        var launcher = new SettingsLauncher(_ => throw new System.ComponentModel.Win32Exception(2));
        var d = new BridgeDispatcher(new BridgeRouter(), new ThrowingBackend(), DesktopLog.Disabled, launcher);
        var reply = JsonNode.Parse((await d.HandleAsync("""{"type":"rpc","id":1,"method":"shell.openSettings","params":{"uri":"ms-settings:troubleshoot"}}""", Doc, true, default))!)!;
        Assert.Equal(-32013, reply["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Frames_and_other_origins_cannot_open_settings()
    {
        var h = new Harness();
        const string msg = """{"type":"rpc","id":1,"method":"shell.openSettings","params":{"uri":"ms-settings:troubleshoot"}}""";
        Assert.Null(await h.Dispatcher.HandleAsync(msg, Doc, isTopLevel: false, default));
        Assert.Null(await h.Dispatcher.HandleAsync(msg, "https://evil.example/", isTopLevel: true, default));
        Assert.Empty(h.Opened);
    }
}

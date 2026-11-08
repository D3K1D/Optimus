using System.Diagnostics;
using System.Text.Json.Nodes;
using OptimizationTool.Desktop.Bridge;

namespace OptimizationTool.Desktop.Tests;

public sealed class OpenUrlTests
{
    private const string Doc = "https://app.optimizationtool.invalid/index.html";
    private readonly List<string> _opened = [];
    private long _now = 1_000_000;
    private readonly BridgeDispatcher _dispatcher;

    public OpenUrlTests()
    {
        _dispatcher = new BridgeDispatcher(new BridgeRouter(), new NoBackend(), DesktopLog.Disabled,
            new SettingsLauncher(_ => throw new InvalidOperationException("settings opened")),
            links: SettingsLauncher.ForWebLinks(_opened.Add, () => _now));
    }

    private sealed class NoBackend : IBackend
    {
        public Task<JsonObject> CallAsync(string method, JsonObject? parameters, CancellationToken ct) =>
            throw new InvalidOperationException("forwarded");
    }

    private async Task<JsonNode> Open(string paramsJson) =>
        JsonNode.Parse((await _dispatcher.HandleAsync(
            $$$"""{"type":"rpc","id":3,"method":"shell.openUrl","params":{{{paramsJson}}}}""", Doc, true, default))!)!;

    [Theory]
    [InlineData("https://claude.com/claude-code")]
    [InlineData("https://developers.openai.com/codex")]
    [InlineData("https://chatgpt.com/")]
    [InlineData("https://claude.com/download")]
    [InlineData("https://chatgpt.com/codex")]
    public async Task Opens_the_five_allowed_links(string url)
    {
        var reply = await Open(new JsonObject { ["url"] = url }.ToJsonString());
        Assert.True(reply["result"]!["opened"]!.GetValue<bool>());
        Assert.Equal(3, reply["id"]!.GetValue<int>());
        Assert.Equal([url], _opened);
    }

    [Theory]
    [InlineData("https://claude.com/claude-code/")]
    [InlineData("https://claude.com/claude-code?x=1")]
    [InlineData("https://claude.com/claude-code#top")]
    [InlineData("https://Claude.com/claude-code")]
    [InlineData("HTTPS://claude.com/claude-code")]
    [InlineData("http://claude.com/claude-code")]
    [InlineData("https://claude.com.evil.example/claude-code")]
    [InlineData("https://claude.com@evil.example/claude-code")]
    [InlineData("https://claude.com:444/claude-code")]
    [InlineData(" https://chatgpt.com/")]
    [InlineData("https://chatgpt.com/ ")]
    [InlineData("https://chatgpt.com")]
    [InlineData("https://chatgpt.com/?q=1")]
    [InlineData("https://ch\u0430tgpt.com/")] // Cyrillic a (U+0430)
    [InlineData("https://developers.openai.com/codex\n")]
    [InlineData("https://claude.com/download/")]
    [InlineData("https://claude.com/download?os=win")]
    [InlineData("https://chatgpt.com/codex/")]
    [InlineData("https://chatgpt.com/codex/install.ps1")]
    [InlineData("https://chatgpt.com/Codex")]
    [InlineData("https://claude.ai/install.ps1")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ms-settings:troubleshoot")]
    [InlineData("")]
    public async Task Rejects_everything_else(string url)
    {
        var reply = await Open(new JsonObject { ["url"] = url }.ToJsonString());
        Assert.Equal(-32602, reply["error"]!["code"]!.GetValue<int>());
        Assert.Empty(_opened);
    }

    [Theory]
    [InlineData("""{"uri":"https://chatgpt.com/"}""")]
    [InlineData("""{"url":"https://chatgpt.com/","target":"_self"}""")]
    [InlineData("""{"url":["https://chatgpt.com/"]}""")]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task Rejects_malformed_params(string paramsJson)
    {
        Assert.Equal(-32602, (await Open(paramsJson))["error"]!["code"]!.GetValue<int>());
        Assert.Empty(_opened);
    }

    [Fact]
    public async Task Rate_limited_like_openSettings()
    {
        await Open("""{"url":"https://chatgpt.com/"}""");
        _now += Stopwatch.Frequency / 2;
        Assert.Equal("rate_limited", (await Open("""{"url":"https://chatgpt.com/"}"""))["error"]!["data"]!["reason"]!.GetValue<string>());
        _now += Stopwatch.Frequency;
        Assert.True((await Open("""{"url":"https://chatgpt.com/"}"""))["result"]!["opened"]!.GetValue<bool>());
        Assert.Equal(2, _opened.Count);
    }

    [Fact]
    public async Task Web_links_cannot_open_settings_uris_and_vice_versa()
    {
        // shell.openSettings does not accept the web links.
        var reply = JsonNode.Parse((await _dispatcher.HandleAsync(
            """{"type":"rpc","id":1,"method":"shell.openSettings","params":{"uri":"https://chatgpt.com/"}}""", Doc, true, default))!)!;
        Assert.Equal(-32602, reply["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Frames_and_other_origins_are_ignored()
    {
        const string msg = """{"type":"rpc","id":1,"method":"shell.openUrl","params":{"url":"https://chatgpt.com/"}}""";
        Assert.Null(await _dispatcher.HandleAsync(msg, Doc, isTopLevel: false, default));
        Assert.Null(await _dispatcher.HandleAsync(msg, "https://evil.example/", isTopLevel: true, default));
        Assert.Empty(_opened);
    }
}

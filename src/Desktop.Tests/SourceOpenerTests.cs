using System.Text.Json.Nodes;
using OptimizationTool.Desktop.Bridge;

namespace OptimizationTool.Desktop.Tests;

/// <summary>
/// m9 §6.5 shell.openSource: the page passes only a source ID; the shell asks the host (research.source, shell-only),
/// re-validates the URL and opens it. Nothing is really opened: the opener is a fake.
/// </summary>
public sealed class SourceOpenerTests
{
    private const string Doc = "https://app.optimizationtool.invalid/index.html";
    private readonly List<string> _opened = [];
    private long _now = 1_000_000;

    private sealed class Backend(Func<string, JsonObject?, JsonObject> answer) : IBackend
    {
        public List<(string Method, JsonObject? Params)> Calls { get; } = [];
        public Task<JsonObject> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            Calls.Add((method, parameters));
            return Task.FromResult(answer(method, parameters));
        }
    }

    private static JsonObject Url(string url) => new() { ["result"] = new JsonObject { ["url"] = url } };
    private static JsonObject Error(int code, string reason) => new() { ["error"] = new JsonObject { ["code"] = code, ["message"] = "x", ["data"] = new JsonObject { ["reason"] = reason } } };

    private async Task<(JsonNode Reply, Backend Backend)> Open(Func<string, JsonObject?, JsonObject> answer, string paramsJson = """{"sourceId":"src-0123456789abcdef"}""")
    {
        var backend = new Backend(answer);
        var dispatcher = new BridgeDispatcher(new BridgeRouter(), backend, AppLog.Disabled,
            new SettingsLauncher(_ => throw new InvalidOperationException("settings opened")),
            links: SettingsLauncher.ForWebLinks(_ => throw new InvalidOperationException("five-URL launcher used")),
            sources: new SourceOpener(_opened.Add, () => _now));
        var reply = await dispatcher.HandleAsync($$$"""{"type":"rpc","id":4,"method":"shell.openSource","params":{{{paramsJson}}}}""", Doc, true, default);
        return (JsonNode.Parse(reply!)!, backend);
    }

    [Fact]
    public async Task Opens_a_stored_source_after_checking_it_again()
    {
        var (reply, backend) = await Open((_, _) => Url("https://store.steampowered.com/remoteplay"));
        Assert.Equal("opened", reply["result"]!["status"]!.GetValue<string>());
        Assert.Equal(["https://store.steampowered.com/remoteplay"], _opened);
        var call = Assert.Single(backend.Calls);
        Assert.Equal("research.source", call.Method);
        Assert.Equal("""{"sourceId":"src-0123456789abcdef"}""", call.Params!.ToJsonString());
    }

    [Theory]
    [InlineData("http://store.steampowered.com/")]
    [InlineData("https://10.0.0.5/")]
    [InlineData("https://user:pw@example.com/")]
    [InlineData("https://example.com:8443/")]
    [InlineData("https://localhost/")]
    [InlineData("https://example.com/#frag")]   // not what the host would store: blocked rather than rewritten
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("https://bücher.de/")]            // the host stores punycode; anything else is blocked
    public async Task A_url_failing_the_shell_check_is_blocked(string url)
    {
        var (reply, _) = await Open((_, _) => Url(url));
        Assert.Equal("blocked", reply["result"]!["status"]!.GetValue<string>());
        Assert.Empty(_opened);
    }

    [Fact]
    public async Task Unknown_and_host_blocked_sources()
    {
        var (unknown, _) = await Open((_, _) => Error(-32001, "not_found"));
        Assert.Equal("unknown", unknown["result"]!["status"]!.GetValue<string>());
        var (blocked, _) = await Open((_, _) => Error(-32013, "blocked"));
        Assert.Equal("blocked", blocked["result"]!["status"]!.GetValue<string>());
        Assert.Empty(_opened);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"sourceId":12}""")]
    [InlineData("""{"sourceId":"https://evil.example.net/"}""")]
    [InlineData("""{"url":"https://store.steampowered.com/"}""")]
    [InlineData("""{"sourceId":"src-0123456789abcdef","url":"https://evil.example.net/"}""")]
    public async Task Missing_or_malformed_source_ids_are_invalid_params(string paramsJson)
    {
        var (reply, backend) = await Open((_, _) => Url("https://store.steampowered.com/"), paramsJson);
        Assert.Equal(-32602, reply["error"]!["code"]!.GetValue<int>());
        Assert.Empty(backend.Calls);
        Assert.Empty(_opened);
    }

    [Fact]
    public void The_renderer_can_call_research_get_start_cancel_but_never_research_source()
    {
        var router = new BridgeRouter();
        foreach (var m in new[] { "research.get", "research.start", "research.cancel" })
            Assert.IsType<BridgeDecision.Forward>(router.Evaluate($$$"""{"type":"rpc","id":1,"method":"{{{m}}}","params":{"reportId":"report-x"}}""", Doc, true));
        var refused = Assert.IsType<BridgeDecision.Reply>(router.Evaluate("""{"type":"rpc","id":2,"method":"research.source","params":{"sourceId":"src-0123456789abcdef"}}""", Doc, true));
        Assert.Equal(-32012, JsonNode.Parse(refused.Json)!["error"]!["code"]!.GetValue<int>());
        Assert.IsType<BridgeDecision.Shell>(router.Evaluate("""{"type":"rpc","id":3,"method":"shell.openSource","params":{"sourceId":"src-0123456789abcdef"}}""", Doc, true));
        Assert.Equal(3, SettingsLauncher.WebLinks.Count); // the launcher's links are unchanged (M9 dropped the two Codex pages)
    }

    [Theory]
    [InlineData("https://en.wikipedia.org/wiki/Steam_(service)#x", "https://en.wikipedia.org/wiki/Steam_(service)")]
    [InlineData("https://bücher.de/x", "https://xn--bcher-kva.de/x")]
    [InlineData("https://127.1/", null)]
    [InlineData("https://0x7f000001/", null)]
    [InlineData("https://[::1]/", null)]
    [InlineData("https://shop.example/", null)]
    public void Shell_hygiene_matches_the_host(string raw, string? expected) => Assert.Equal(expected, SourceUrl.Clean(raw));
}

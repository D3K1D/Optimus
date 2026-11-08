using System.Diagnostics;
using System.Text.Json.Nodes;
using OptimizationTool.Desktop.Bridge;

namespace OptimizationTool.Desktop.Tests;

public sealed class ApiKeyRequestTests : IDisposable
{
    private const string Doc = "https://app.optimizationtool.invalid/index.html";
    private const string Key = "sk-test-SECRET-7f3a9c1e5b2d4086";

    private readonly string _logPath = Path.Combine(Path.GetTempPath(), "ot-desktop-test-" + Guid.NewGuid().ToString("N") + ".log");
    private readonly DesktopLog _log;
    private readonly FakePrompt _prompt = new();
    private readonly RecordingBackend _backend = new();
    private long _now = 1_000_000;
    private readonly BridgeDispatcher _dispatcher;
    private readonly List<string> _posted = [];

    public ApiKeyRequestTests()
    {
        _log = new DesktopLog(_logPath);
        _dispatcher = new BridgeDispatcher(new BridgeRouter(), _backend, _log,
            new SettingsLauncher(_ => { }), new ApiKeyRequest(_prompt, () => _now));
    }

    public void Dispose()
    {
        try { File.Delete(_logPath); } catch (IOException) { }
    }

    private sealed class FakePrompt : IApiKeyPrompt
    {
        public Func<string, ApiKeyEntry?> Next = _ => new ApiKeyEntry(Key, true);
        public TaskCompletionSource? Gate;
        public List<string> Kinds = [];

        public async Task<ApiKeyEntry?> PromptAsync(string kind, CancellationToken ct)
        {
            Kinds.Add(kind);
            if (Gate is { } g) await g.Task;
            return Next(kind);
        }
    }

    private sealed class RecordingBackend : IBackend
    {
        public readonly List<(string Method, string Params)> Calls = [];
        public Func<JsonObject?, JsonObject> Respond = p => Ok(new JsonObject { ["status"] = "connected" });
        public Exception? Throw;

        public static JsonObject Ok(JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = 1, ["result"] = result };

        public Task<JsonObject> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            Calls.Add((method, parameters?.ToJsonString() ?? ""));
            if (Throw is not null) throw Throw;
            return Task.FromResult(Respond(parameters));
        }
    }

    private void Advance(double seconds) => _now += (long)(seconds * Stopwatch.Frequency);

    private async Task<JsonNode> Request(string paramsJson, int id = 1)
    {
        var reply = await _dispatcher.HandleAsync(
            $$$"""{"type":"rpc","id":{{{id}}},"method":"shell.requestApiKey","params":{{{paramsJson}}}}""", Doc, true, default);
        _posted.Add(reply!);
        return JsonNode.Parse(reply!)!;
    }

    private void AssertKeyNotLeaked()
    {
        Assert.All(_posted, p => Assert.DoesNotContain(Key, p, StringComparison.Ordinal));
        var log = File.Exists(_logPath) ? File.ReadAllText(_logPath) : "";
        Assert.DoesNotContain(Key, log, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", log, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("anthropic_api")]
    [InlineData("openai_api")]
    public async Task Key_goes_only_to_provider_setKey_and_renderer_gets_status(string kind)
    {
        var reply = await Request(new JsonObject { ["kind"] = kind }.ToJsonString(), id: 9);

        Assert.Equal("rpc.result", reply["type"]!.GetValue<string>());
        Assert.Equal(9, reply["id"]!.GetValue<int>());
        Assert.Equal("""{"status":"connected"}""", reply["result"]!.ToJsonString());
        Assert.Equal([kind], _prompt.Kinds);

        var call = Assert.Single(_backend.Calls);
        Assert.Equal("provider.setKey", call.Method);
        var sent = JsonNode.Parse(call.Params)!;
        Assert.Equal(kind, sent["kind"]!.GetValue<string>());
        Assert.Equal(Key, sent["apiKey"]!.GetValue<string>());
        Assert.True(sent["remember"]!.GetValue<bool>());

        Assert.Contains("provider.setKey", File.ReadAllText(_logPath));
        AssertKeyNotLeaked();
    }

    [Fact]
    public async Task Remember_defaults_are_passed_as_chosen()
    {
        _prompt.Next = _ => new ApiKeyEntry(Key, false);
        await Request("""{"kind":"openai_api"}""");
        Assert.False(JsonNode.Parse(_backend.Calls[0].Params)!["remember"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Cancel_returns_cancelled_and_calls_nothing()
    {
        _prompt.Next = _ => null;
        var reply = await Request("""{"kind":"anthropic_api"}""");
        Assert.Equal("""{"status":"cancelled"}""", reply["result"]!.ToJsonString());
        Assert.Empty(_backend.Calls);
    }

    [Fact]
    public async Task Empty_key_counts_as_cancel()
    {
        _prompt.Next = _ => new ApiKeyEntry("", true);
        var reply = await Request("""{"kind":"anthropic_api"}""");
        Assert.Equal("cancelled", reply["result"]!["status"]!.GetValue<string>());
        Assert.Empty(_backend.Calls);
    }

    [Fact]
    public async Task Host_rejection_returns_short_code_only()
    {
        // A misbehaving host echoing the key in free text must not leak it.
        _backend.Respond = p => RecordingBackend.Ok(new JsonObject { ["status"] = "error", ["error"] = "invalid_key", ["detail"] = "bad key " + p!["apiKey"] });
        var reply = await Request("""{"kind":"anthropic_api"}""");
        Assert.Equal("""{"status":"error","error":"invalid_key"}""", reply["result"]!.ToJsonString());
        AssertKeyNotLeaked();
    }

    [Fact]
    public async Task Rpc_error_from_host_is_mapped_without_message_or_data()
    {
        _backend.Respond = p => new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1,
            ["error"] = new JsonObject { ["code"] = -32602, ["message"] = "key " + p!["apiKey"], ["data"] = new JsonObject { ["echo"] = p["apiKey"]!.DeepClone() } },
        };
        var reply = await Request("""{"kind":"anthropic_api"}""");
        Assert.Equal("rpc.result", reply["type"]!.GetValue<string>());
        Assert.Equal("""{"status":"error","error":"backend_error"}""", reply["result"]!.ToJsonString());
        AssertKeyNotLeaked();
    }

    [Fact]
    public async Task Backend_unavailable_and_unexpected_exceptions_do_not_leak()
    {
        _backend.Throw = new BackendUnavailableException("down");
        Assert.Equal("""{"status":"error","error":"backend_unavailable"}""", (await Request("""{"kind":"anthropic_api"}"""))["result"]!.ToJsonString());

        Advance(2);
        _backend.Throw = new InvalidOperationException("contains " + Key);
        Assert.Equal("""{"status":"error","error":"failed"}""", (await Request("""{"kind":"anthropic_api"}"""))["result"]!.ToJsonString());
        AssertKeyNotLeaked();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("""{"kind":"local"}""")]
    [InlineData("""{"kind":"claude_code_mcp"}""")]
    [InlineData("""{"kind":"Anthropic_api"}""")]
    [InlineData("""{"kind":"anthropic_api "}""")]
    [InlineData("""{"kind":"anthropic"}""")]
    [InlineData("""{"kind":""}""")]
    [InlineData("""{"kind":7}""")]
    [InlineData("""{"kind":["anthropic_api"]}""")]
    [InlineData("""{"kind":"anthropic_api","apiKey":"sk-x"}""")]
    [InlineData("""{"kind":"anthropic_api","remember":true}""")]
    public async Task Rejects_bad_params_with_32602_without_prompting(string paramsJson)
    {
        var reply = await Request(paramsJson);
        Assert.Equal(-32602, reply["error"]!["code"]!.GetValue<int>());
        Assert.Empty(_prompt.Kinds);
        Assert.Empty(_backend.Calls);
    }

    [Fact]
    public async Task One_dialog_at_a_time()
    {
        _prompt.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = _dispatcher.HandleAsync("""{"type":"rpc","id":1,"method":"shell.requestApiKey","params":{"kind":"anthropic_api"}}""", Doc, true, default);

        Advance(5);
        var second = await Request("""{"kind":"openai_api"}""", id: 2);
        Assert.Equal(-32013, second["error"]!["code"]!.GetValue<int>());
        Assert.Equal("busy", second["error"]!["data"]!["reason"]!.GetValue<string>());

        _prompt.Gate.SetResult();
        var firstReply = JsonNode.Parse((await first)!)!;
        Assert.Equal("connected", firstReply["result"]!["status"]!.GetValue<string>());
        Assert.Equal(["anthropic_api"], _prompt.Kinds);
    }

    [Fact]
    public async Task Rate_limited_between_dialogs()
    {
        _prompt.Next = _ => null;
        await Request("""{"kind":"anthropic_api"}""");
        Advance(0.5);
        var limited = await Request("""{"kind":"anthropic_api"}""");
        Assert.Equal("rate_limited", limited["error"]!["data"]!["reason"]!.GetValue<string>());
        Advance(0.6);
        Assert.Equal("cancelled", (await Request("""{"kind":"anthropic_api"}"""))["result"]!["status"]!.GetValue<string>());
        Assert.Equal(2, _prompt.Kinds.Count);
    }

    [Fact]
    public async Task Renderer_cannot_call_provider_setKey()
    {
        var reply = JsonNode.Parse((await _dispatcher.HandleAsync(
            $$$"""{"type":"rpc","id":3,"method":"provider.setKey","params":{"kind":"anthropic_api","apiKey":"{{{Key}}}","remember":true}}""", Doc, true, default))!)!;
        Assert.Equal(-32012, reply["error"]!["code"]!.GetValue<int>());
        Assert.Empty(_backend.Calls);
        Assert.DoesNotContain(Key, File.Exists(_logPath) ? File.ReadAllText(_logPath) : "");
    }

    [Fact]
    public async Task Frames_and_other_origins_cannot_open_the_dialog()
    {
        const string msg = """{"type":"rpc","id":1,"method":"shell.requestApiKey","params":{"kind":"anthropic_api"}}""";
        Assert.Null(await _dispatcher.HandleAsync(msg, Doc, isTopLevel: false, default));
        Assert.Null(await _dispatcher.HandleAsync(msg, "https://evil.example/", isTopLevel: true, default));
        Assert.Empty(_prompt.Kinds);
    }

    [Fact]
    public void Entry_does_not_print_its_key()
    {
        var e = new ApiKeyEntry(Key, true);
        Assert.DoesNotContain(Key, e.ToString());
        e.Clear();
        Assert.Equal("", e.ApiKey);
    }
}

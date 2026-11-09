using System.Text.Json.Nodes;
using OptimizationTool.Desktop.Bridge;

namespace OptimizationTool.Desktop.Tests;

public class BridgeRouterTests
{
    private const string Doc = "https://app.optimizationtool.invalid/index.html";
    private readonly BridgeRouter _router = new();

    private static JsonObject ReplyJson(BridgeDecision d) =>
        JsonNode.Parse(Assert.IsType<BridgeDecision.Reply>(d).Json)!.AsObject();

    [Fact]
    public void Forwards_allowlisted_call_with_renderer_id_and_params()
    {
        var d = _router.Evaluate("""{"type":"rpc","id":17,"method":"scan.start","params":{"idempotencyKey":"k","intent":"audit.full","text":"Begin scan"}}""", Doc, true);
        var fwd = Assert.IsType<BridgeDecision.Forward>(d);
        Assert.Equal("scan.start", fwd.Method);
        Assert.Equal(17, fwd.Id.GetValue<int>());
        Assert.Equal("k", fwd.Params!["idempotencyKey"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("onboarding.get")] [InlineData("onboarding.save")] [InlineData("inventory.start")] [InlineData("inventory.get")]
    [InlineData("inventory.cancel")] [InlineData("starter.list")] [InlineData("scan.start")] [InlineData("scan.get")]
    [InlineData("scan.cancel")] [InlineData("report.get")] [InlineData("report.list")] [InlineData("evidence.get")]
    [InlineData("followup.get")] [InlineData("followup.respond")] [InlineData("events.read")]
    [InlineData("finding.get")] [InlineData("finding.setDisposition")] [InlineData("finding.clearDisposition")]
    [InlineData("scan.skipCollector")] [InlineData("report.compare")]
    [InlineData("provider.list")] [InlineData("provider.connect")] [InlineData("provider.disconnect")]
    [InlineData("agent.start")] [InlineData("agent.get")] [InlineData("agent.active")] [InlineData("agent.ask")] [InlineData("agent.stop")] [InlineData("agent.cancel")] [InlineData("agent.approvePairing")]
    [InlineData("review.preview")] [InlineData("review.submit")] [InlineData("review.export")] [InlineData("review.import")]
    [InlineData("action.catalog")] [InlineData("action.preview")] [InlineData("action.get")] [InlineData("action.decline")]
    [InlineData("action.undo")] [InlineData("operation.get")] [InlineData("operation.list")] [InlineData("manual.recheck")]
    [InlineData("bench.capabilities")] [InlineData("bench.start")] [InlineData("bench.get")] [InlineData("bench.cancel")]
    [InlineData("bench.list")] [InlineData("bench.evidence")]
    public void Every_desktop_method_is_allowed(string method) =>
        Assert.IsType<BridgeDecision.Forward>(_router.Evaluate($$"""{"type":"rpc","id":1,"method":"{{method}}"}""", Doc, true));

    [Theory]
    [InlineData("session.hello")]
    [InlineData("action.approve")]
    [InlineData("provider.setKey")]
    [InlineData("agent.pair")]
    [InlineData("agent.pairStatus")]
    [InlineData("tool.call")]
    [InlineData("action.challenge")]
    [InlineData("request_action")]
    [InlineData("Provider.List")]
    [InlineData("Scan.Start")]
    [InlineData("scan.start ")]
    public void Rejects_methods_off_the_allowlist_with_32012(string method)
    {
        var reply = ReplyJson(_router.Evaluate($$"""{"type":"rpc","id":5,"method":"{{method}}"}""", Doc, true));
        Assert.Equal("rpc.error", reply["type"]!.GetValue<string>());
        Assert.Equal(5, reply["id"]!.GetValue<int>());
        Assert.Equal(-32012, reply["error"]!["code"]!.GetValue<int>());
        Assert.Equal(method, reply["error"]!["data"]!["method"]!.GetValue<string>());
        Assert.Equal("desktop", reply["error"]!["data"]!["clientKind"]!.GetValue<string>());
    }

    [Fact]
    public void Rejects_oversize_message_without_parsing()
    {
        var big = new string('a', BridgeRouter.MaxMessageBytes);
        var reply = ReplyJson(_router.Evaluate($$$"""{"type":"rpc","id":1,"method":"scan.get","params":{"x":"{{{big}}}"}}""", Doc, true));
        Assert.Equal(-32600, reply["error"]!["code"]!.GetValue<int>());
        Assert.Equal("size", reply["error"]!["data"]!["field"]!.GetValue<string>());
        Assert.Null(reply["id"]);
    }

    [Fact]
    public void SaveImage_is_shell_handled_and_may_exceed_the_normal_size_limit()
    {
        var big = new string('A', BridgeRouter.MaxMessageBytes * 4);
        var d = _router.Evaluate($$$"""{"type":"rpc","id":3,"method":"shell.saveImage","params":{"pngBase64":"{{{big}}}"}}""", Doc, true);
        var shell = Assert.IsType<BridgeDecision.Shell>(d);
        Assert.Equal("shell.saveImage", shell.Method);
    }

    [Fact]
    public void Large_message_naming_saveImage_but_calling_another_method_is_rejected()
    {
        var big = new string('a', BridgeRouter.MaxMessageBytes);
        var reply = ReplyJson(_router.Evaluate($$$"""{"type":"rpc","id":1,"method":"scan.get","params":{"x":"shell.saveImage","z":"{{{big}}}"}}""", Doc, true));
        Assert.Equal("size", reply["error"]!["data"]!["field"]!.GetValue<string>());
        Assert.Null(reply["id"]);
    }

    [Fact]
    public void SaveImage_over_the_image_limit_is_rejected()
    {
        var big = new string('A', BridgeRouter.MaxImageMessageBytes);
        var reply = ReplyJson(_router.Evaluate($$$"""{"type":"rpc","id":1,"method":"shell.saveImage","params":{"pngBase64":"{{{big}}}"}}""", Doc, true));
        Assert.Equal("size", reply["error"]!["data"]!["field"]!.GetValue<string>());
    }

    [Fact]
    public void Counts_utf8_bytes_not_chars()
    {
        // 100k three-byte chars = 300 KB UTF-8, under the char limit.
        var wide = new string('\u20ac', 100_000);
        var reply = ReplyJson(_router.Evaluate($$$"""{"type":"rpc","id":1,"method":"scan.get","params":{"x":"{{{wide}}}"}}""", Doc, true));
        Assert.Equal("size", reply["error"]!["data"]!["field"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("")]
    [InlineData("[1,2]")]
    [InlineData("\"rpc\"")]
    [InlineData("null")]
    public void Rejects_bad_json_with_32600(string json)
    {
        var reply = ReplyJson(_router.Evaluate(json, Doc, true));
        Assert.Equal(-32600, reply["error"]!["code"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("""{"type":"call","id":1,"method":"scan.get"}""", "type")]
    [InlineData("""{"id":1,"method":"scan.get"}""", "type")]
    [InlineData("""{"type":"rpc","method":"scan.get"}""", "id")]
    [InlineData("""{"type":"rpc","id":"1","method":"scan.get"}""", "id")]
    [InlineData("""{"type":"rpc","id":1}""", "method")]
    [InlineData("""{"type":"rpc","id":1,"method":7}""", "method")]
    [InlineData("""{"type":"rpc","id":1,"method":"scan.get","params":[1]}""", "params")]
    [InlineData("""{"type":"rpc","id":1,"method":"scan.get","params":"x"}""", "params")]
    public void Rejects_malformed_rpc_with_field(string json, string field)
    {
        var reply = ReplyJson(_router.Evaluate(json, Doc, true));
        Assert.Equal(-32600, reply["error"]!["code"]!.GetValue<int>());
        Assert.Equal(field, reply["error"]!["data"]!["field"]!.GetValue<string>());
    }

    [Fact]
    public void Deep_nesting_is_rejected_not_thrown()
    {
        var json = """{"type":"rpc","id":1,"method":"scan.get","params":""" + new string('[', 200) + new string(']', 200) + "}";
        Assert.IsType<BridgeDecision.Reply>(_router.Evaluate(json, Doc, true));
    }

    [Theory]
    [InlineData("https://evil.example/index.html")]
    [InlineData("http://app.optimizationtool.invalid/index.html")]
    [InlineData("https://app.optimizationtool.invalid:444/index.html")]
    [InlineData("https://user@app.optimizationtool.invalid/index.html")]
    [InlineData("https://app.optimizationtool.invalid.evil.example/")]
    [InlineData("https://sub.app.optimizationtool.invalid/")]
    [InlineData("file:///C:/ui/index.html")]
    [InlineData("about:blank")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData(null)]
    public void Ignores_messages_from_other_origins(string? source)
    {
        var d = _router.Evaluate("""{"type":"rpc","id":1,"method":"scan.get"}""", source, true);
        Assert.Equal("origin", Assert.IsType<BridgeDecision.Ignore>(d).Reason);
    }

    [Theory]
    [InlineData("https://app.optimizationtool.invalid/")]
    [InlineData("https://app.optimizationtool.invalid/index.html")]
    [InlineData("https://APP.optimizationtool.invalid/index.html?x=1#y")]
    public void Accepts_app_origin_documents(string source) =>
        Assert.IsType<BridgeDecision.Forward>(_router.Evaluate("""{"type":"rpc","id":1,"method":"scan.get"}""", source, true));

    [Fact]
    public void Ignores_frame_messages_even_from_app_origin()
    {
        var d = _router.Evaluate("""{"type":"rpc","id":1,"method":"scan.get"}""", Doc, isTopLevel: false);
        Assert.Equal("frame", Assert.IsType<BridgeDecision.Ignore>(d).Reason);
    }

    [Fact]
    public void Response_mapping_uses_renderer_id_and_relays_only_code_message_data()
    {
        var id = JsonValue.Create(42);
        var ok = JsonNode.Parse(BridgeMessages.FromResponse(id, JsonNode.Parse("""{"jsonrpc":"2.0","id":9,"result":{"a":1}}""")!.AsObject()))!;
        Assert.Equal("rpc.result", ok["type"]!.GetValue<string>());
        Assert.Equal(42, ok["id"]!.GetValue<int>());
        Assert.Equal(1, ok["result"]!["a"]!.GetValue<int>());

        var err = JsonNode.Parse(BridgeMessages.FromResponse(id, JsonNode.Parse("""{"jsonrpc":"2.0","id":9,"error":{"code":-32004,"message":"busy","data":{"scanId":"scan-1"},"extra":1}}""")!.AsObject()))!;
        Assert.Equal("rpc.error", err["type"]!.GetValue<string>());
        Assert.Equal(42, err["id"]!.GetValue<int>());
        Assert.Equal(-32004, err["error"]!["code"]!.GetValue<int>());
        Assert.Equal("scan-1", err["error"]!["data"]!["scanId"]!.GetValue<string>());
        Assert.Null(err["error"]!["extra"]);
    }
}

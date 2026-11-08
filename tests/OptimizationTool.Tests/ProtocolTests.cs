using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using OptimizationTool.Engine;
using OptimizationTool.Host;
using OptimizationTool.Transport;

namespace OptimizationTool.Tests;

public class ProtocolTests
{
    [Fact]
    public async Task IncompatibleMajor_IsRejectedWithAction()
    {
        await using var host = TestHost.Start();
        var c = await host.ConnectAsync(hello: false);
        var error = await c.Fails("session.hello", Rpc.HelloParams(major: 2));
        Assert.Equal(ErrorCodes.IncompatibleProtocol, error.Code);
        Assert.Equal(1, (int)error.ErrorData!["supported"]!["major"]!);
        Assert.Equal("Update Optimization Tool", error.ErrorData.Str("action"));
    }

    [Fact]
    public async Task MethodBeforeHello_IsHandshakeRequired_ThenWorksAfterHello()
    {
        await using var host = TestHost.Start();
        var c = await host.ConnectAsync(hello: false);
        Assert.Equal(ErrorCodes.HandshakeRequired, (await c.Fails("onboarding.get")).Code);
        await c.Call("session.hello", Rpc.HelloParams());
        Assert.Equal(1, (int)(await c.Call("onboarding.get"))["revision"]!);
    }

    [Fact]
    public async Task UnknownMethod_AndBadKind_AreRejected()
    {
        await using var host = TestHost.Start();
        var c = await host.ConnectAsync(hello: false);
        var badKind = Rpc.HelloParams();
        badKind["client"]!["kind"] = "robot";
        Assert.Equal(ErrorCodes.InvalidParams, (await c.Fails("session.hello", badKind)).Code);
        await c.Call("session.hello", Rpc.HelloParams());
        Assert.Equal(ErrorCodes.MethodNotFound, (await c.Fails("action.format")).Code);
    }

    [Fact]
    public void DesktopAllowlist_IsTheEngineMethodSet()
    {
        Assert.Equal(AuditEngine.Methods.Order(), HostServer.Allowlists["desktop"].Order());
        Assert.DoesNotContain("session.hello", HostServer.Allowlists["desktop"]);
    }

    [Fact]
    public async Task OversizeFrame_DropsTheConnection()
    {
        await using var host = TestHost.Start();
        await using var pipe = await RawConnect(host);
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Frame.MaxPayloadBytes + 1);
        await pipe.WriteAsync(header);
        await pipe.FlushAsync();
        Assert.Equal(0, await ReadWithTimeout(pipe)); // server closed without replying

        var c = await host.ConnectAsync(); // the host is still serving
        Assert.NotNull(c.Hello);
    }

    [Fact]
    public async Task TruncatedFrame_IsDropped_AndHostKeepsServing()
    {
        await using var host = TestHost.Start();
        await using (var pipe = await RawConnect(host))
        {
            var header = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(header, 100);
            await pipe.WriteAsync(header);
            await pipe.WriteAsync(Encoding.UTF8.GetBytes("{\"jsonrpc\""));
            await pipe.FlushAsync();
        } // closed mid-frame
        await using (var pipe = await RawConnect(host))
        {
            await pipe.WriteAsync(new byte[] { 1, 0 }); // half a header
            await pipe.FlushAsync();
        }
        var c = await host.ConnectAsync();
        Assert.NotNull(await c.Call("onboarding.get"));
    }

    [Fact]
    public async Task InvalidJson_AndBatches_GetErrorsWithoutDroppingTheConnection()
    {
        await using var host = TestHost.Start();
        await using var pipe = await RawConnect(host);
        await Frame.WriteAsync(pipe, Encoding.UTF8.GetBytes("{not json"));
        var parse = JsonNode.Parse((await Frame.ReadAsync(pipe))!)!;
        Assert.Equal(ErrorCodes.ParseError, (int)parse["error"]!["code"]!);

        await Frame.WriteAsync(pipe, Encoding.UTF8.GetBytes("[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"onboarding.get\"}]"));
        var batch = JsonNode.Parse((await Frame.ReadAsync(pipe))!)!;
        Assert.Equal(ErrorCodes.InvalidRequest, (int)batch["error"]!["code"]!);

        await Frame.WriteAsync(pipe, Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"onboarding.get\"}"));
        var gated = JsonNode.Parse((await Frame.ReadAsync(pipe))!)!;
        Assert.Equal(2, (int)gated["id"]!);
        Assert.Equal(ErrorCodes.HandshakeRequired, (int)gated["error"]!["code"]!);
    }

    [Fact]
    public async Task MultipleClients_AllReceivePushedEvents()
    {
        await using var host = TestHost.Start();
        var a = await host.ConnectAsync();
        var b = await host.ConnectAsync();
        var (scan, _) = await a.ScanAsync("audit.storage");
        var onB = await b.WaitForScanFinished(scan.Str("scanId"));
        Assert.Equal("complete", onB["payload"].Str("status"));
        Assert.True(host.Server.ConnectionCount >= 2);
    }

    [Fact]
    public async Task SecondHost_OnTheSamePipeName_CannotStart()
    {
        await using var host = TestHost.Start();
        var dir = TestHost.NewDataDir();
        Assert.ThrowsAny<Exception>(() => HostServer.Start(new HostOptions { DataDir = dir, PipeName = host.PipeName, IdleExit = null }));
        var c = await host.ConnectAsync();
        Assert.NotNull(c.Hello);
    }

    [Fact]
    public async Task Host_ExitsWhenIdle()
    {
        await using var host = TestHost.Start(idle: TimeSpan.FromMilliseconds(300));
        var c = await host.ConnectAsync();
        await c.Rpc.DisposeAsync();
        var done = await Task.WhenAny(host.Server.Completion, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(host.Server.Completion, done);
    }

    [Fact]
    public async Task Hello_ReportsProtocolFeaturesAndCursor()
    {
        await using var host = TestHost.Start();
        var c = await host.ConnectAsync();
        Assert.Equal(4, (int)c.Hello!["protocol"]!["minor"]!); // server is 1.4; the test client says 1.0
        Assert.Contains("scan.fixture", c.Hello["features"]!.AsArray().Select(x => (string)x!));
        Assert.StartsWith("c-", c.Hello.Str("connectionId"));
        Assert.Equal("user", c.Hello["eventCursor"].Str("stream"));
    }

    private static async Task<NamedPipeClientStream> RawConnect(TestHost host)
    {
        var pipe = new NamedPipeClientStream(".", host.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000);
        return pipe;
    }

    private static async Task<int> ReadWithTimeout(Stream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { return await stream.ReadAsync(new byte[16], cts.Token); }
        catch (IOException) { return 0; }
    }
}

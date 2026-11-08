using System.Diagnostics;
using System.Text.Json.Nodes;

namespace OptimizationTool.Mcp.Tests;

/// <summary>Runs the built bridge executable over real stdio against the stub host.</summary>
public sealed class StdioProcessTests
{
    [Fact]
    public async Task Stdout_CarriesOnlyJsonRpc()
    {
        await using var host = new StubAgentHost();
        var exe = Path.Combine(AppContext.BaseDirectory, "OptimizationTool.Mcp.exe");
        Assert.True(File.Exists(exe), exe);

        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--pipe");
        psi.ArgumentList.Add(host.PipeName);
        psi.ArgumentList.Add("--no-log");
        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        async Task SendAsync(string json)
        {
            await process.StandardInput.WriteLineAsync(json);
            await process.StandardInput.FlushAsync();
        }

        var lines = new List<string>();
        async Task<JsonObject> ReadResponseAsync(int id)
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new EndOfStreamException("stdout closed");
                lines.Add(line);
                var msg = JsonNode.Parse(line)!.AsObject();
                if (msg["id"]?.GetValue<int>() == id) return msg;
            }
        }

        await SendAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"claude-code","version":"2.1.310"}}}""");
        var init = await ReadResponseAsync(1);
        Assert.Equal("optimization-tool", init["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        await SendAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        await SendAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var list = await ReadResponseAsync(2);
        Assert.Equal(10, list["result"]!["tools"]!.AsArray().Count);

        await SendAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_capabilities","arguments":{}}}""");
        var call = await ReadResponseAsync(3);
        Assert.Contains("(code 4821)", call["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("claude_code", host.Last("agent.pair")!["client"]!.GetValue<string>());
        Assert.Equal(process.Id, host.Last("agent.pair")!["companionPid"]!.GetValue<int>());

        process.StandardInput.Close();
        Assert.True(process.WaitForExit(10_000), "bridge did not exit when stdin closed");
        var rest = await process.StandardOutput.ReadToEndAsync();
        lines.AddRange(rest.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')));

        Assert.NotEmpty(lines);
        foreach (var line in lines)
            Assert.Equal("2.0", JsonNode.Parse(line)!["jsonrpc"]!.GetValue<string>());
        _ = await stderr;
    }
}

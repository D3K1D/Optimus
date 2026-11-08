using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace OptimizationTool.Providers.Tests;

/// <summary>Records each request (method, URI, headers, body) and answers from a queue. No network.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    public sealed record Captured(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, string? Body)
    {
        public JsonObject Json => (JsonObject)JsonNode.Parse(Body!)!;
    }

    readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses = new();
    public List<Captured> Requests { get; } = [];

    public FakeHandler Respond(HttpStatusCode status, string body, Action<HttpResponseMessage>? configure = null)
    {
        _responses.Enqueue((_, _) =>
        {
            var r = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            configure?.Invoke(r);
            return Task.FromResult(r);
        });
        return this;
    }

    public FakeHandler Respond(JsonNode json) => Respond(HttpStatusCode.OK, json.ToJsonString());

    public FakeHandler Respond(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responses.Enqueue(responder);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        if (request.Content is not null)
            foreach (var h in request.Content.Headers) headers[h.Key.ToLowerInvariant()] = string.Join(",", h.Value);
        Requests.Add(new Captured(request.Method, request.RequestUri!, headers, body));
        if (_responses.Count == 0) throw new InvalidOperationException("No fake response queued.");
        return await _responses.Dequeue()(request, cancellationToken);
    }
}

/// <summary>A response body that yields some bytes and then fails, like a dropped connection mid-answer.</summary>
internal sealed class BrokenStream(byte[] prefix) : Stream
{
    int _pos;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _pos; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_pos >= prefix.Length) throw new IOException("connection reset");
        var n = Math.Min(count, prefix.Length - _pos);
        Array.Copy(prefix, _pos, buffer, offset, n);
        _pos += n;
        return n;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal static class TestKeys
{
    // Fake, never valid. Distinctive so a leak is easy to detect.
    public const string Anthropic = "sk-ant-api03-TESTKEY-do-not-leak-0123456789abcdef";
    public const string OpenAi = "sk-proj-TESTKEY-do-not-leak-0123456789abcdef";
}

/// <summary>Runs only when the named environment variable is set; otherwise reported as skipped.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute(string envVar)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVar)))
            Skip = $"Set {envVar} to run this live test.";
    }
}

using System.Text;
using System.Text.Json;

namespace OptimizationTool.Bench.Tests;

/// <summary>Captures worker events in memory, one parsed JSON object per line, thread-safe.</summary>
internal sealed class EventSink : Stream
{
    private readonly Lock _lock = new();
    private readonly List<byte> _pending = [];
    private readonly List<(DateTime At, JsonElement Ev)> _events = [];

    public EventWriter Writer => new(this);

    public IReadOnlyList<JsonElement> Events { get { lock (_lock) return _events.Select(e => e.Ev).ToList(); } }

    public IReadOnlyList<string> Lines { get; private set; } = [];

    /// <summary>Waits until an event with ev == <paramref name="ev"/> arrives; returns it and the time it arrived.</summary>
    public (JsonElement Ev, DateTime At)? WaitFor(string ev, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            lock (_lock)
            {
                foreach (var (at, e) in _events)
                    if (e.GetProperty("ev").GetString() == ev) return (e, at);
            }
            Thread.Sleep(5);
        }
        return null;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        lock (_lock)
        {
            foreach (var b in buffer)
            {
                if (b != (byte)'\n') { _pending.Add(b); continue; }
                var line = Encoding.UTF8.GetString(_pending.ToArray());
                _pending.Clear();
                Lines = [.. Lines, line];
                _events.Add((DateTime.UtcNow, JsonDocument.Parse(line).RootElement.Clone()));
            }
        }
    }

    public override void WriteByte(byte value) => Write([value]);
    public override void Flush() { }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>A temp folder for one test, removed afterwards (the only place tests write).</summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ot-bench-tests-" + Guid.NewGuid().ToString("N"));
    public TempFolder() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal static class Ctx
{
    public static PartContext For(RunCommand cmd, EventSink? sink = null, CancellationToken ct = default, double scale = 1.0) =>
        new(cmd, ct, new BenchScale(scale), sink?.Writer);
}

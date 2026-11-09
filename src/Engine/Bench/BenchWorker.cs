using System.Text;

namespace OptimizationTool.Engine.Bench;

/// <summary>
/// Starts OptimizationTool.Bench.exe (contracts/m6-benchmark.md §4). The Windows implementation lives in src/Host: it
/// creates the process suspended with no window and redirected stdio, assigns it to a kill-on-close job object and only
/// then resumes it. The fixture source and the tests use <see cref="FakeBenchWorkerLauncher"/>; nothing in the tests
/// ever runs a real workload.
/// </summary>
public interface IBenchWorkerLauncher
{
    /// <summary>A started worker, or throws when it cannot be started (the part fails with worker_unavailable).</summary>
    IBenchWorker Launch();
}

/// <summary>One running worker: JSON lines on stdin/stdout (seam: BenchSeams.cs).</summary>
public interface IBenchWorker : IDisposable
{
    /// <summary>Writes one line (a JSON object, no newline) to the worker's stdin.</summary>
    Task SendAsync(string line, CancellationToken ct);

    /// <summary>
    /// The next line from the worker's stdout, or null at end of stream (the worker exited or was killed). Throws
    /// <see cref="InvalidDataException"/> for a line longer than <see cref="BoundedLineReader.MaxLineBytes"/>.
    /// </summary>
    Task<string?> ReadLineAsync(CancellationToken ct);

    /// <summary>Ends the worker at once (the whole job). Idempotent; never throws.</summary>
    void Kill();
}

/// <summary>
/// Reads UTF-8 lines of at most 64 KiB from a stream (the worker's stdout). A longer line throws
/// <see cref="InvalidDataException"/> without buffering more than the limit. "\r\n" and "\n" both end a line.
/// </summary>
public sealed class BoundedLineReader(Stream stream, int maxLineBytes = BoundedLineReader.MaxLineBytes)
{
    public const int MaxLineBytes = 64 * 1024;

    private readonly byte[] _buffer = new byte[8192];
    private int _start, _end;
    private readonly MemoryStream _line = new();

    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        _line.SetLength(0);
        while (true)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await stream.ReadAsync(_buffer, ct).ConfigureAwait(false);
                if (_end == 0)
                    return _line.Length == 0 ? null : Decode(); // last line without a newline
            }
            var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            var take = (newline < 0 ? _end : newline) - _start;
            if (_line.Length + take > maxLineBytes) throw new InvalidDataException("A worker line exceeded the size limit.");
            _line.Write(_buffer, _start, take);
            if (newline < 0) { _start = _end; continue; }
            _start = newline + 1;
            return Decode();
        }
    }

    private string Decode()
    {
        var bytes = _line.GetBuffer().AsSpan(0, (int)_line.Length);
        if (bytes.Length > 0 && bytes[^1] == (byte)'\r') bytes = bytes[..^1];
        return Encoding.UTF8.GetString(bytes);
    }
}

/// <summary>
/// The bench folder (%LOCALAPPDATA%\OptimizationTool\bench, i.e. &lt;data dir&gt;\bench): created by the host with the
/// data directory's user-only ACL, and refused when it (or the data directory) is a reparse point, so the disk test can
/// never be redirected elsewhere.
/// </summary>
public static class BenchFolder
{
    public const string Name = "bench";

    /// <summary>The prepared folder path. Throws <see cref="IOException"/> when a reparse point is found.</summary>
    public static string Prepare(string dataDir)
    {
        var root = Path.GetFullPath(dataDir);
        var path = Path.Combine(root, Name);
        if (IsReparsePoint(root)) throw new IOException("The data folder is a link; the disk test is refused.");
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
            Acl.RestrictDirectory(path);
        }
        if (IsReparsePoint(path)) throw new IOException("The bench folder is a link; the disk test is refused.");
        return path;
    }

    public static bool IsReparsePoint(string path)
    {
        var info = new DirectoryInfo(path);
        return info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }
}

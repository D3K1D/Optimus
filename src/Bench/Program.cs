namespace OptimizationTool.Bench;

/// <summary>
/// OptimizationTool.Bench.exe: the benchmark worker (contracts/m6-benchmark.md §4). The Host starts it suspended in a
/// kill-on-close job with redirected stdio and no window. stdout carries only wire events; stderr only short codes.
/// Exits 0 on "exit" or stdin EOF.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var scale = BenchScale.FromArgs(args);
        var events = new EventWriter(Console.OpenStandardOutput());
        using var worker = new Worker(events, scale);
        events.Hello(Environment.ProcessId, scale.IsTest ? scale.Factor : null);
        worker.StartHeartbeat();

        var reader = new BoundedLineReader(Console.OpenStandardInput());
        // stdin is read on this (main) thread; parts run on their own thread, the heartbeat on another.
        while (true)
        {
            string? line;
            try { line = reader.ReadLine(); }
            catch (IOException) { line = null; }
            if (line is null || !worker.Handle(line)) break;
        }
        // EOF or exit: stop the current part and give it a moment to drain I/O and free memory. Anything still open is
        // closed by process exit (the disk file is FILE_FLAG_DELETE_ON_CLOSE, so Windows deletes it then).
        worker.Shutdown(TimeSpan.FromSeconds(2));
        return 0;
    }
}

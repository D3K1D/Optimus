using OptimizationTool.Engine.Bench;

namespace OptimizationTool.Host.Bench;

/// <summary>
/// --source fixture: synthetic hardware, sensors and an in-process worker, so the UI and CI can run the whole benchmark
/// flow without loading the PC. <paramref name="speed"/> scales the timings (1 = about 45 s for all four parts, 0 = fast).
/// </summary>
public static class FixtureBench
{
    public static BenchOptions Options(double speed)
    {
        var factor = Math.Max(0.02, speed);
        TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds * factor);
        var timings = new BenchTimings
        {
            CoolDown = S(5),
            SampleInterval = TimeSpan.FromSeconds(Math.Clamp(factor, 0.05, 1)),
            Planned = new Dictionary<string, TimeSpan>
            {
                [BenchParts.Ram] = S(15), [BenchParts.Cpu] = S(45), [BenchParts.Gpu] = S(20), [BenchParts.Disk] = S(22),
            },
            Repeat = S(10),
        };
        var script = new FakeWorkerScript { PartDuration = S(8) };
        return new BenchOptions
        {
            Sensors = new FakeSensorHubFactory(), Hardware = new FakeBenchHardware(), Launcher = new FakeBenchWorkerLauncher(script),
            Timings = timings,
        };
    }
}

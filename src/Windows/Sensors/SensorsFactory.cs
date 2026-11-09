using OptimizationTool.Engine.Bench;

namespace OptimizationTool.Windows.Sensors;

/// <summary>
/// Host entry point for M6a sensors (contracts/m6-benchmark.md §3): one driver-free <see cref="ISensorHub"/> per run.
/// Slot ids are per part (clock|temp|power|fan, read|write|copy|latency, read|write|random4k|temp).
/// </summary>
public sealed class SensorsFactory : ISensorHubFactory
{
    public ISensorHub Create() => new SensorHub(new WindowsSensorBackend(), new PowerWatcher());
}

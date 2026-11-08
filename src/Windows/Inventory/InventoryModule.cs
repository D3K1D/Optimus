using System.Text.Json.Nodes;
using OptimizationTool.Engine;

namespace OptimizationTool.Windows.Inventory;

/// <summary>Onboarding inventory: seven local, read-only, no-AI steps. No collectors and no rules.</summary>
public sealed class InventoryModule : IAuditModule
{
    public string Name => "inventory";
    public IEnumerable<ICollector> Collectors(CollectorOptions options) => [];
    public IEnumerable<IRule> Rules() => [];

    public IEnumerable<IInventoryStep> InventorySteps(CollectorOptions options) =>
    [
        new Step<SpecsRaw>("Specs", Readers.ReadSpecs, InventoryNormalize.Specs),
        new Step<GraphicsRaw>("Graphics", Readers.ReadGraphics, InventoryNormalize.Graphics),
        new Step<DisplaysRaw>("Displays", Readers.ReadDisplays, InventoryNormalize.Displays),
        new Step<DevicesRaw>("Devices", Readers.ReadDevices, InventoryNormalize.Devices),
        new Step<AudioRaw>("Audio", Readers.ReadAudio, InventoryNormalize.Audio),
        new Step<NetworkRaw>("Network", Readers.ReadNetwork, InventoryNormalize.Network),
        new Step<DriversRaw>("Drivers", Readers.ReadDrivers, r => InventoryNormalize.Drivers(r, DateTime.UtcNow)),
    ];

    sealed class Step<T>(string group, Func<T> read, Func<T, InventoryStepResult> normalize) : IInventoryStep
    {
        public string Group => group;
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);

        public async Task<InventoryStepResult> RunAsync(CancellationToken ct)
        {
            try
            {
                // Blocking Windows calls run on a pool (MTA) thread; the timeout or cancellation abandons it.
                var raw = await Task.Run(read, ct).WaitAsync(ct).ConfigureAwait(false);
                return normalize(raw);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                var status = e is UnauthorizedAccessException ? "denied" : e is PlatformNotSupportedException or EntryPointNotFoundException or DllNotFoundException ? "unsupported" : "failed";
                return new InventoryStepResult([], [], new CoverageReceipt(status, 0, e.GetType().Name), new JsonObject());
            }
        }
    }
}

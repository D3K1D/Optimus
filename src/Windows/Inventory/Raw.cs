namespace OptimizationTool.Windows.Inventory;

// Raw read results (no Windows calls past this point). Null = could not be read.
internal sealed record SpecsRaw(int? LogicalCores, int? PhysicalCores, ulong? RamKilobytes, string? EditionName, int? Build, bool? HasBattery);

/// <summary>One present device's driver facts. Provider/version are device-class data, never identifiers.</summary>
internal sealed record DriverRec(DateTime? Date, string? Version, string? Provider)
{
    public bool HasDriver => Date is not null || !string.IsNullOrEmpty(Version);
}

internal sealed record GraphicsRaw(int? GpuCount, IReadOnlyList<DriverRec>? DisplayDrivers);
internal sealed record DisplaysRaw(int? Count, int? MaxCurrentHz);

internal enum PointerKind { Mouse, Touchpad, Other }

/// <summary>Hub ports: a null entry means the port count was unreadable. Hubs are external hubs only; root hubs are counted separately.</summary>
internal sealed record DevicesRaw(IReadOnlyList<PointerKind>? Pointers, int? Keyboards, IReadOnlyList<int?>? Hubs, int RootHubs);

internal sealed record AudioRaw(int? Render, int? Capture);

internal sealed record AdapterRec(bool IsWifi, bool IsEthernet, bool IsUp, bool? IsHardware);
internal sealed record NetworkRaw(IReadOnlyList<AdapterRec>? Adapters);

internal sealed record DriversRaw(IReadOnlyList<DriverRec>? Devices);

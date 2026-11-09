using System.Runtime.InteropServices;
using OptimizationTool.Engine.Bench;

namespace OptimizationTool.Windows.Sensors;

/// <summary>Pure: GetSystemPowerStatus / GUID_ACDC_POWER_SOURCE values → <see cref="PowerState"/> words.</summary>
internal static class PowerNormalizer
{
    /// <summary>ACLineStatus 0 = offline, 1 = online, anything else (255) = unknown.</summary>
    public static string Ac(byte acLineStatus) => acLineStatus switch
    {
        0 => PowerStates.Offline,
        1 => PowerStates.Online,
        _ => PowerStates.Unknown,
    };

    /// <summary>BatteryFlag 128 = no system battery, 255 = unknown status, any other value = a battery is present.</summary>
    public static string Battery(byte batteryFlag) => batteryFlag switch
    {
        128 => PowerStates.Absent,
        255 => PowerStates.Unknown,
        _ => PowerStates.Present,
    };

    public static PowerState FromStatus(byte acLineStatus, byte batteryFlag) => new(Ac(acLineStatus), Battery(batteryFlag));

    /// <summary>GUID_ACDC_POWER_SOURCE data: 0 = AC, 1 = DC (battery), 2 = short-term (UPS) → offline.</summary>
    public static string AcFromSetting(uint value) => value switch
    {
        0 => PowerStates.Online,
        1 or 2 => PowerStates.Offline,
        _ => PowerStates.Unknown,
    };
}

/// <summary>
/// AC/battery state from GetSystemPowerStatus, and a GUID_ACDC_POWER_SOURCE subscription
/// (PowerSettingRegisterNotification with DEVICE_NOTIFY_CALLBACK: no window, no thread). Raises
/// <see cref="Changed"/> when the AC state changes. Dispose unregisters.
/// </summary>
internal sealed unsafe class PowerWatcher : IPowerReader
{
    private nint _registration;
    private GCHandle _self;
    private string? _lastAc;
    private readonly Lock _gate = new();

    public event Action<PowerState>? Changed;

    public PowerWatcher()
    {
        _lastAc = Current().Ac;
        _self = GCHandle.Alloc(this);
        var p = new Native.DeviceNotifySubscribeParameters { Callback = &OnNotify, Context = GCHandle.ToIntPtr(_self) };
        if (Native.PowerSettingRegisterNotification(Native.GuidAcdcPowerSource, Native.DeviceNotifyCallback, &p, out _registration) != 0)
        {
            _registration = 0;
            _self.Free();
        }
    }

    public bool Subscribed => _registration != 0;

    public PowerState Current() =>
        Native.GetSystemPowerStatus(out var s) ? PowerNormalizer.FromStatus(s.ACLineStatus, s.BatteryFlag) : new PowerState(PowerStates.Unknown, PowerStates.Unknown);

    [UnmanagedCallersOnly]
    private static uint OnNotify(nint context, uint type, nint setting)
    {
        try
        {
            if (type != Native.PbtPowerSettingChange || context == 0) return 0;
            if (GCHandle.FromIntPtr(context).Target is not PowerWatcher w) return 0;
            // POWERBROADCAST_SETTING: GUID PowerSetting; DWORD DataLength; UCHAR Data[] (a DWORD here).
            string? ac = null;
            if (setting != 0 && *(uint*)(setting + 16) >= 4) ac = PowerNormalizer.AcFromSetting(*(uint*)(setting + 20));
            w.Raise(ac);
        }
        catch (Exception) { /* never throw into the power manager */ }
        return 0;
    }

    private void Raise(string? acFromSetting)
    {
        var now = Current();
        if (acFromSetting is not null && acFromSetting != now.Ac && now.Ac == PowerStates.Unknown) now = now with { Ac = acFromSetting };
        Action<PowerState>? handler;
        lock (_gate)
        {
            if (now.Ac == _lastAc) return;
            _lastAc = now.Ac;
            handler = Changed;
        }
        handler?.Invoke(now);
    }

    public void Dispose()
    {
        if (_registration != 0)
        {
            Native.PowerSettingUnregisterNotification(_registration);
            _registration = 0;
            if (_self.IsAllocated) _self.Free();
        }
    }
}

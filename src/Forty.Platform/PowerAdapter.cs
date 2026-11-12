using System.Runtime.InteropServices;
using Forty.Core;
using Windows.Devices.Power;
using Windows.System.Power;

namespace Forty.Platform;

/// <summary>Windows' effective power mode: what the PC is actually doing about power right now.</summary>
public enum EffectivePowerMode { BatterySaver, BetterBattery, Balanced, HighPerformance, MaxPerformance, GameMode, MixedReality, Unknown }

/// <summary>
/// Battery, charging and battery saver (F47, F127, F128) and the effective power mode, which also
/// reports game mode (F130, F134). Event-driven: Windows calls in when something changes.
/// </summary>
public sealed class PowerAdapter() : Adapter("power")
{
    readonly object _gate = new();
    IntPtr _modeRegistration;
    PowerModeCallback? _modeCallback;   // kept alive for as long as the registration exists

    public PowerSnapshot Snapshot { get; private set; } = new(null, true, false, false);
    public EffectivePowerMode Mode { get; private set; } = EffectivePowerMode.Unknown;

    public event Action<PowerSnapshot>? Changed;
    public event Action<EffectivePowerMode>? ModeChanged;

    protected override void OnStart()
    {
        PowerManager.BatteryStatusChanged += (_, _) => Refresh();
        PowerManager.PowerSupplyStatusChanged += (_, _) => Refresh();
        PowerManager.RemainingChargePercentChanged += (_, _) => Refresh();
        PowerManager.EnergySaverStatusChanged += (_, _) => Refresh();
        Battery.AggregateBattery.ReportUpdated += (_, _) => Refresh();
        Refresh();

        _modeCallback = (mode, _) =>
        {
            Mode = mode is >= 0 and <= 6 ? (EffectivePowerMode)mode : EffectivePowerMode.Unknown;
            ModeChanged?.Invoke(Mode);
        };
        int hr = PowerRegisterForEffectivePowerModeNotifications(EffectivePowerModeV2, _modeCallback, IntPtr.Zero, out _modeRegistration);
        if (hr != 0) Report(Availability.Limited, $"effective power mode is not available (0x{hr:X8})");
    }

    /// <summary>Reads everything again and raises <see cref="Changed"/> if anything differs.</summary>
    public void Refresh()
    {
        PowerSnapshot next;
        try
        {
            bool hasBattery = PowerManager.BatteryStatus != BatteryStatus.NotPresent;
            var report = hasBattery ? Battery.AggregateBattery.GetReport() : null;
            next = new PowerSnapshot(
                Percent: hasBattery ? PowerManager.RemainingChargePercent : null,
                PluggedIn: PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent,
                Charging: PowerManager.BatteryStatus == BatteryStatus.Charging,
                Saver: PowerManager.EnergySaverStatus == EnergySaverStatus.On,
                ChargeRateMilliwatts: report?.ChargeRateInMilliwatts,
                RemainingMilliwattHours: report?.RemainingCapacityInMilliwattHours,
                FullMilliwattHours: report?.FullChargeCapacityInMilliwattHours,
                DesignMilliwattHours: report?.DesignCapacityInMilliwattHours);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            Report(Availability.Limited, ex.Message);
            return;
        }
        lock (_gate)
        {
            if (next == Snapshot) return;
            Snapshot = next;
        }
        Changed?.Invoke(next);
    }

    public override void Dispose()
    {
        if (_modeRegistration != IntPtr.Zero) PowerUnregisterFromEffectivePowerModeNotifications(_modeRegistration);
        _modeRegistration = IntPtr.Zero;
        base.Dispose();
    }

    const uint EffectivePowerModeV2 = 2;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate void PowerModeCallback(int mode, IntPtr context);

    [DllImport("powrprof.dll")]
    static extern int PowerRegisterForEffectivePowerModeNotifications(uint version, PowerModeCallback callback, IntPtr context, out IntPtr registration);

    [DllImport("powrprof.dll")]
    static extern int PowerUnregisterFromEffectivePowerModeNotifications(IntPtr registration);
}

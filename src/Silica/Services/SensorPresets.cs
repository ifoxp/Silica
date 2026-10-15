using Silica.Core.Sensors;

namespace Silica.Services;

public enum SensorPreset { Essentials, Cooling, Power, Gaming, Temperatures, Minimal }

/// <summary>Ready-made sets of sensors for the Sensors page filter and the overlay.</summary>
public static class SensorPresets
{
    /// <summary>Filters offered on the Sensors page, in display order.</summary>
    public static readonly SensorPreset[] PageFilters = [SensorPreset.Essentials, SensorPreset.Cooling, SensorPreset.Power];

    /// <summary>Sets that fill the overlay, in display order.</summary>
    public static readonly SensorPreset[] OverlaySets = [SensorPreset.Gaming, SensorPreset.Temperatures, SensorPreset.Minimal];

    public static string Title(SensorPreset preset) => preset switch
    {
        SensorPreset.Essentials => "Essentials",
        SensorPreset.Cooling => "Cooling",
        SensorPreset.Power => "Power",
        SensorPreset.Gaming => "Gaming",
        SensorPreset.Temperatures => "Temperatures",
        _ => "Minimal"
    };

    /// <summary>True for presets that are a fixed list of sensors; the others select by sensor type.</summary>
    public static bool IsListed(SensorPreset preset) => preset is not (SensorPreset.Cooling or SensorPreset.Power);

    public static bool MatchesKind(SensorPreset preset, SensorReading s) => preset switch
    {
        SensorPreset.Cooling => s.Kind is SensorKind.Temperature or SensorKind.Fan or SensorKind.Control,
        SensorPreset.Power => s.Kind is SensorKind.Power or SensorKind.Voltage or SensorKind.Current,
        _ => false
    };

    /// <summary>The sensors of a list-based preset, in the order they should be shown.</summary>
    public static IEnumerable<SensorReading> Pick(SensorPreset preset, KeySensors k)
    {
        var hotSpot = KeySensors.Pick(k.Gpu, SensorKind.Temperature, "GPU Hot Spot");
        IEnumerable<SensorReading?> list = preset switch
        {
            SensorPreset.Essentials =>
            [
                k.CpuLoad, k.CpuTemp, k.CpuClock, k.CpuPower,
                k.GpuLoad, k.GpuTemp, hotSpot, k.GpuClock, k.GpuPower, k.GpuMemoryUsed,
                k.RamLoad, k.RamUsed, .. k.StorageTemps, .. k.Fans
            ],
            SensorPreset.Gaming => [k.CpuLoad, k.CpuTemp, k.GpuLoad, k.GpuTemp, k.GpuClock, k.GpuPower, k.GpuMemoryUsed, k.RamUsed],
            SensorPreset.Temperatures => [k.CpuTemp, k.GpuTemp, hotSpot, .. k.StorageTemps.Take(2), .. k.Fans.Take(2)],
            SensorPreset.Minimal => [k.CpuLoad, k.CpuTemp, k.GpuLoad, k.GpuTemp],
            _ => []
        };
        return list.OfType<SensorReading>().Distinct();
    }
}

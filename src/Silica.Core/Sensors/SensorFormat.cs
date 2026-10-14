using System.Globalization;
using Silica.Core.Util;

namespace Silica.Core.Sensors;

public static class SensorFormat
{
    public static bool Fahrenheit { get; set; }

    public static string Unit(SensorKind kind) => Fmt.Unit(RawUnit(kind));

    private static string RawUnit(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => Fahrenheit ? "°F" : "°C",
        SensorKind.Load or SensorKind.Control or SensorKind.Level or SensorKind.Humidity => "%",
        SensorKind.Clock => "MHz",
        SensorKind.Voltage => "V",
        SensorKind.Current => "A",
        SensorKind.Power => "W",
        SensorKind.Fan => "RPM",
        SensorKind.Flow => "L/h",
        SensorKind.Data => "GB",
        SensorKind.SmallData => "MB",
        SensorKind.Throughput => "B/s",
        SensorKind.Energy => "mWh",
        SensorKind.Noise => "dBA",
        SensorKind.TimeSpan => "s",
        SensorKind.Frequency => "Hz",
        SensorKind.Conductivity => "µS/cm",
        _ => ""
    };

    /// <summary>Converts a raw value into the unit the user sees (only temperature differs).</summary>
    public static double Display(SensorKind kind, double value) =>
        kind == SensorKind.Temperature && Fahrenheit ? value * 9 / 5 + 32 : value;

    public static string Format(SensorKind kind, float? value)
    {
        if (value is not { } raw) return "—";
        var v = Display(kind, raw);
        var c = CultureInfo.CurrentCulture;
        return kind switch
        {
            SensorKind.Throughput => Fmt.Speed(v),
            SensorKind.Data => v >= 1024 ? (v / 1024).ToString("0.00", c) + " " + Fmt.Unit("TB") : v.ToString("0.0", c) + " " + Fmt.Unit("GB"),
            SensorKind.SmallData => v >= 1024 ? (v / 1024).ToString("0.0", c) + " " + Fmt.Unit("GB") : v.ToString("0", c) + " " + Fmt.Unit("MB"),
            SensorKind.Clock => v >= 1000 ? (v / 1000).ToString("0.00", c) + " " + Fmt.Unit("GHz") : v.ToString("0", c) + " " + Fmt.Unit("MHz"),
            SensorKind.Voltage => v.ToString("0.000", c) + " " + Unit(kind),
            SensorKind.Fan => v.ToString("0", c) + " " + Unit(kind),
            SensorKind.Factor => v.ToString("0.00", c),
            SensorKind.TimeSpan => Fmt.Duration(TimeSpan.FromSeconds(v)),
            SensorKind.Temperature or SensorKind.Power or SensorKind.Current => v.ToString("0.0", c) + " " + Unit(kind),
            _ => v.ToString(v >= 100 ? "0" : "0.#", c) + (Unit(kind) is { Length: > 0 } u ? (u == "%" ? " %" : " " + u) : "")
        };
    }

    /// <summary>Compact form without unit, for tray icons and tight cells.</summary>
    public static string Short(SensorKind kind, float? value)
    {
        if (value is not { } raw) return "—";
        var v = Display(kind, raw);
        return kind switch
        {
            SensorKind.Clock when v >= 1000 => (v / 1000).ToString("0.0", CultureInfo.CurrentCulture),
            _ => v.ToString("0", CultureInfo.CurrentCulture)
        };
    }
}

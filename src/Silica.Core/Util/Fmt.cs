using System.Globalization;

namespace Silica.Core.Util;

/// <summary>Formatting helpers. Unit symbols and text templates go through the hooks below, so the app can translate them.</summary>
public static class Fmt
{
    private static readonly string[] BinUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>Translates a unit symbol ("GB", "MHz", "d"); the app plugs its language here.</summary>
    public static Func<string, string> Unit { get; set; } = u => u;

    /// <summary>Translates an English text template such as "{0}, week {1}".</summary>
    public static Func<string, string> Text { get; set; } = s => s;

    /// <summary>Plural-aware count text such as "{0} threads".</summary>
    public static Func<long, string, string> Plural { get; set; } = (n, key) => string.Format(CultureInfo.CurrentCulture, key, n);

    public static string T(string template, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Text(template), args);

    /// <summary>1024-based size, e.g. "15.9 GB".</summary>
    public static string Bytes(double bytes, int decimals = 1)
    {
        if (bytes < 0) return "—";
        var i = 0;
        while (bytes >= 1024 && i < BinUnits.Length - 1) { bytes /= 1024; i++; }
        return i == 0
            ? bytes.ToString("0", CultureInfo.CurrentCulture) + " " + Unit("B")
            : bytes.ToString("0." + new string('#', decimals), CultureInfo.CurrentCulture) + " " + Unit(BinUnits[i]);
    }

    public static string? Bytes(ulong? bytes) => bytes is { } b ? Bytes((double)b) : null;
    public static string? Bytes(long? bytes) => bytes is { } b ? Bytes((double)b) : null;

    /// <summary>1000-based size as used by drive vendors, e.g. "1.0 TB".</summary>
    public static string DecimalBytes(double bytes)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB", "PB"];
        var i = 0;
        while (bytes >= 1000 && i < u.Length - 1) { bytes /= 1000; i++; }
        return bytes.ToString(i == 0 ? "0" : "0.#", CultureInfo.CurrentCulture) + " " + Unit(u[i]);
    }

    public static string Mhz(double mhz) => mhz >= 1000
        ? (mhz / 1000).ToString("0.00", CultureInfo.CurrentCulture) + " " + Unit("GHz")
        : mhz.ToString("0", CultureInfo.CurrentCulture) + " " + Unit("MHz");

    public static string? Mhz(long? mhz) => mhz is > 0 ? Mhz((double)mhz.Value) : null;

    public static string BitRate(double bps)
    {
        if (bps <= 0) return "—";
        string[] u = ["bps", "Kbps", "Mbps", "Gbps", "Tbps"];
        var i = 0;
        while (bps >= 1000 && i < u.Length - 1) { bps /= 1000; i++; }
        return bps.ToString("0.#", CultureInfo.CurrentCulture) + " " + Unit(u[i]);
    }

    /// <summary>Throughput in bytes per second, e.g. "12.3 MB/s".</summary>
    public static string Speed(double bytesPerSec) => Bytes(bytesPerSec) + Unit("/s");

    public static string Percent(double value, int decimals = 0) =>
        value.ToString("F" + decimals, CultureInfo.CurrentCulture) + " %";

    public static string Date(DateTime? d) => d is { } v ? v.ToString("d", CultureInfo.CurrentCulture) : "";
    public static string DateTime(DateTime? d) => d is { } v ? v.ToString("g", CultureInfo.CurrentCulture) : "";

    /// <summary>"3 d 04:12:55" style duration.</summary>
    public static string Duration(TimeSpan t) => t.TotalDays >= 1
        ? $"{(int)t.TotalDays} {Unit("d")} {t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}"
        : $"{t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}";

    public static string Number(double v, int decimals = 0) => v.ToString("N" + decimals, CultureInfo.CurrentCulture);

    public static string Hex(ulong v, int digits = 0) => "0x" + v.ToString(digits > 0 ? "X" + digits : "X", CultureInfo.InvariantCulture);
}

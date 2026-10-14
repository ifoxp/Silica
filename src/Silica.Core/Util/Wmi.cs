using System.Globalization;
using System.Management;

namespace Silica.Core.Util;

/// <summary>A WMI result row with tolerant typed getters (missing/null properties return null).</summary>
public sealed class WmiRow(ManagementBaseObject obj)
{
    public ManagementBaseObject Raw => obj;

    public object? this[string name]
    {
        get
        {
            try { return obj[name]; }
            catch (ManagementException) { return null; }
        }
    }

    public string? Str(string name)
    {
        var v = this[name];
        return v switch
        {
            null => null,
            string s => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
            string[] a => a.Length == 0 ? null : string.Join(", ", a.Where(x => !string.IsNullOrWhiteSpace(x))),
            _ => Convert.ToString(v, CultureInfo.InvariantCulture)
        };
    }

    public long? Long(string name)
    {
        var v = this[name];
        if (v == null) return null;
        try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { return null; }
    }

    public ulong? ULong(string name)
    {
        var v = this[name];
        if (v == null) return null;
        try { return Convert.ToUInt64(v, CultureInfo.InvariantCulture); } catch { return null; }
    }

    public int? Int(string name) => (int?)Long(name);

    public bool? Bool(string name) => this[name] is bool b ? b : null;

    /// <summary>Parses CIM_DATETIME strings ("20240101000000.000000+000").</summary>
    public DateTime? Date(string name)
    {
        var s = Str(name);
        if (s == null) return null;
        try { return ManagementDateTimeConverter.ToDateTime(s); } catch { return null; }
    }

    public T[]? Array<T>(string name) => this[name] as T[];
}

public static class Wmi
{
    /// <summary>Runs a WQL query. Returns an empty list on any failure (WMI class missing, access denied…).</summary>
    public static List<WmiRow> Query(string wql, string scope = @"root\cimv2")
    {
        var list = new List<WmiRow>();
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, wql);
            searcher.Options.Timeout = TimeSpan.FromSeconds(20);
            foreach (var o in searcher.Get()) list.Add(new WmiRow(o));
        }
        catch { /* class/namespace not available or access denied */ }
        return list;
    }

    public static WmiRow? First(string wql, string scope = @"root\cimv2") => Query(wql, scope).FirstOrDefault();
}

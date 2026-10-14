using Microsoft.Win32;

namespace Silica.Core.Util;

/// <summary>Tolerant registry readers. All methods return null/empty on missing keys or access errors.</summary>
public static class Reg
{
    public static object? Get(RegistryKey root, string path, string? name, RegistryView view = RegistryView.Registry64)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(HiveOf(root), view);
            using var key = baseKey.OpenSubKey(path);
            return key?.GetValue(name);
        }
        catch { return null; }
    }

    public static string? Str(RegistryKey root, string path, string? name, RegistryView view = RegistryView.Registry64) =>
        Get(root, path, name, view) switch
        {
            null => null,
            string s => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
            string[] a => string.Join(", ", a),
            byte[] b => BitConverter.ToString(b),
            var o => o.ToString()
        };

    public static long? Long(RegistryKey root, string path, string? name, RegistryView view = RegistryView.Registry64) =>
        Get(root, path, name, view) switch
        {
            int i => i,
            long l => l,
            byte[] { Length: >= 8 } b => BitConverter.ToInt64(b),
            byte[] { Length: >= 4 } b => BitConverter.ToInt32(b),
            string s when long.TryParse(s, out var l) => l,
            _ => null
        };

    public static string[] SubKeys(RegistryKey root, string path, RegistryView view = RegistryView.Registry64)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(HiveOf(root), view);
            using var key = baseKey.OpenSubKey(path);
            return key?.GetSubKeyNames() ?? [];
        }
        catch { return []; }
    }

    /// <summary>Opens a key for enumeration of values; caller disposes. Null when missing.</summary>
    public static RegistryKey? Open(RegistryKey root, string path, RegistryView view = RegistryView.Registry64)
    {
        try { return RegistryKey.OpenBaseKey(HiveOf(root), view).OpenSubKey(path); }
        catch { return null; }
    }

    private static RegistryHive HiveOf(RegistryKey root) => root.Name switch
    {
        "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
        "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
        "HKEY_CLASSES_ROOT" => RegistryHive.ClassesRoot,
        "HKEY_USERS" => RegistryHive.Users,
        "HKEY_CURRENT_CONFIG" => RegistryHive.CurrentConfig,
        _ => RegistryHive.LocalMachine
    };
}

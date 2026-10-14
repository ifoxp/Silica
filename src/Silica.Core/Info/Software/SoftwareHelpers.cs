using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Silica.Core.Util;

namespace Silica.Core.Info.Software;

internal static class SoftwareHelpers
{
    private static readonly ConcurrentDictionary<string, string> Descriptions = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public static string FileDescription(string path) => Descriptions.GetOrAdd(path, static p =>
    {
        try { return FileVersionInfo.GetVersionInfo(p).FileDescription?.Trim() ?? ""; }
        catch { return ""; }
    });

    // Runs work on its own background thread so a stuck COM call cannot hang the caller.
    // On timeout the thread is abandoned; it is a background thread, so it never blocks shutdown.
    public static bool TryRun<T>(Func<T> work, TimeSpan timeout, out T? result, bool sta = false)
    {
        T? value = default;
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try { value = work(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
        }) { IsBackground = true, Name = "Silica COM worker" };
        thread.SetApartmentState(sta ? ApartmentState.STA : ApartmentState.MTA);
        thread.Start();
        if (!thread.Join(timeout))
        {
            result = default;
            return false;
        }
        error?.Throw();
        result = value;
        return true;
    }

    public static void Release(object? com)
    {
        if (com != null && Marshal.IsComObject(com))
        {
            try { Marshal.FinalReleaseComObject(com); } catch { }
        }
    }

    // COM DATE uses 1899-12-30 for "never"; Task Scheduler uses 1999-11-30.
    public static DateTime? ComDate(object? value) =>
        value is DateTime d && d.Year >= 2000 ? d : null;

    public static string NormalizeImagePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var p = path.Trim().Trim('"');
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (p.StartsWith(@"\??\", StringComparison.Ordinal)) p = p[4..];
        if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) p = Path.Combine(windir, p[12..]);
        else if (p.StartsWith("System32\\", StringComparison.OrdinalIgnoreCase)) p = Path.Combine(windir, p);
        return Environment.ExpandEnvironmentVariables(p);
    }

    public static DeviceGuardState? ReadDeviceGuard()
    {
        var row = Wmi.First("SELECT * FROM Win32_DeviceGuard", @"root\Microsoft\Windows\DeviceGuard");
        if (row == null) return null;
        var running = ToInts(row["SecurityServicesRunning"]);
        var configured = ToInts(row["SecurityServicesConfigured"]);
        return new DeviceGuardState(row.Int("VirtualizationBasedSecurityStatus") ?? 0, configured, running);
    }

    private static int[] ToInts(object? value) => value is Array a
        ? a.Cast<object>().Select(o => Convert.ToInt32(o)).ToArray()
        : [];
}

internal sealed record DeviceGuardState(int VbsStatus, int[] Configured, int[] Running)
{
    public const int CredentialGuard = 1, Hvci = 2;

    public string Vbs => VbsStatus switch
    {
        2 => "Running",
        1 => "Enabled",
        _ => "Off"
    };

    public string Service(int id) =>
        Running.Contains(id) ? "Running" : Configured.Contains(id) ? "Enabled" : "Off";
}

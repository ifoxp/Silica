using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace Silica.Services;

public static class Elevation
{
    public static bool IsElevated { get; } = Check();

    /// <summary>
    /// Starts a new elevated instance. Returns false if the user declined the UAC prompt.
    /// <paramref name="replacing"/> tells the new instance to wait until this one has exited.
    /// </summary>
    public static bool RestartElevated(IEnumerable<string>? args, bool replacing)
    {
        try
        {
            Settings.SaveNow();
            var forwarded = (args ?? []).Where(a => a != "--restarted").Select(Quote);
            var line = string.Join(" ", replacing ? forwarded.Append("--restarted") : forwarded);
            Process.Start(new ProcessStartInfo(AppPaths.ExecutablePath, line) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static string Quote(string a) => a.Contains(' ') ? $"\"{a}\"" : a;

    private static bool Check()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

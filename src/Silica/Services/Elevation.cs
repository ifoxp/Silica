using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace Silica.Services;

public static class Elevation
{
    public static bool IsElevated { get; } = Check();

    /// <summary>Starts a new elevated instance. Returns false if the user declined the UAC prompt.</summary>
    public static bool RestartElevated()
    {
        try
        {
            Settings.SaveNow();
            Process.Start(new ProcessStartInfo(AppPaths.ExecutablePath, "--restarted") { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static bool Check()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

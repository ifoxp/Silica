using System.Diagnostics;
using System.IO;

namespace Silica.Services;

/// <summary>Opens files, folders and links without passing on Silica's administrator rights.</summary>
public static class ShellOpen
{
    // A new explorer.exe hands its argument to the user's running, unelevated shell and exits,
    // so the browser or editor that opens does not inherit Silica's elevated token.
    public static void Open(string target)
    {
        try
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            Process.Start(new ProcessStartInfo(explorer) { ArgumentList = { target }, UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open {target}: {ex.Message}");
        }
    }
}

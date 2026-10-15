using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Silica.Services;

/// <summary>
/// Removes Silica completely: the installed product through Windows Installer (or the portable files),
/// settings, logs, benchmark history and the startup task. The work runs in a hidden PowerShell that
/// waits until every Silica process has exited, since a running exe cannot be deleted.
/// </summary>
public static class Uninstaller
{
    // UpgradeCode from installer/Silica.wxs; it stays the same for every version.
    private const string UpgradeCode = "{7D3C8B8E-4A51-4E0E-9A5C-5B1F2C6D9E41}";

    public static string AppDataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Silica");

    /// <summary>Reports and sensor logs saved with the default locations.</summary>
    public static string DocumentsFolder => AppPaths.DefaultReportFolder;

    /// <summary>Product code of the installed copy this process runs from, or null for a portable copy.</summary>
    public static string? InstalledProductCode()
    {
        var code = new StringBuilder(39);
        if (MsiEnumRelatedProducts(UpgradeCode, 0, 0, code) != 0) return null;

        var exeDir = Path.GetDirectoryName(Path.GetFullPath(AppPaths.ExecutablePath))!.TrimEnd('\\');
        var location = ProductInfo(code.ToString(), "InstallLocation").TrimEnd('\\');
        // 1.0.0 did not record its folder; an exe in Program Files is that installation.
        if (location.Length == 0) return StartupService.IsAllowed ? code.ToString() : null;
        return string.Equals(location, exeDir, StringComparison.OrdinalIgnoreCase) ? code.ToString() : null;
    }

    public static void Run(bool deleteDocuments)
    {
        try { StartupService.Set(false); } catch (Exception ex) { Log.Warn($"Startup task: {ex.Message}"); }

        var exe = Path.GetFullPath(AppPaths.ExecutablePath);
        var exeDir = Path.GetDirectoryName(exe)!;
        var product = InstalledProductCode();
        var script = new StringBuilder();
        script.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
        script.AppendLine($"Get-Process Silica | Where-Object Path -eq {Quote(exe)} | Wait-Process -Timeout 60");
        script.AppendLine("Start-Sleep -Seconds 1");
        if (product != null)
        {
            script.AppendLine($"Start-Process msiexec.exe -ArgumentList '/x {product} /passive /norestart' -Wait");
        }
        else
        {
            // Only Silica's own files: a portable copy often sits in Downloads or on the desktop.
            foreach (var file in new[] { exe, Path.Combine(exeDir, "Silica.settings.json"), Path.Combine(exeDir, "silica.log"), Path.Combine(exeDir, "silica.log.old") })
                script.AppendLine($"Remove-Item -LiteralPath {Quote(file)} -Force");
            script.AppendLine($"Remove-Item -LiteralPath {Quote(Path.Combine(exeDir, "Languages"))} -Recurse -Force");
            script.AppendLine($"if (-not (Get-ChildItem -LiteralPath {Quote(exeDir)} -Force)) {{ Remove-Item -LiteralPath {Quote(exeDir)} }}");
        }
        script.AppendLine($"Remove-Item -LiteralPath {Quote(AppDataFolder)} -Recurse -Force");
        if (deleteDocuments) script.AppendLine($"Remove-Item -LiteralPath {Quote(DocumentsFolder)} -Recurse -Force");

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script.ToString()));
        // Started from the temp folder: a process whose current folder is the install folder keeps it from being deleted.
        Process.Start(new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}")
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() });
    }

    private static string Quote(string path) => "'" + path.Replace("'", "''") + "'";

    private static string ProductInfo(string product, string property)
    {
        var size = 1024;
        var value = new StringBuilder(size);
        return MsiGetProductInfo(product, property, value, ref size) == 0 ? value.ToString() : "";
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern int MsiEnumRelatedProducts(string upgradeCode, int reserved, int index, StringBuilder productCode);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern int MsiGetProductInfo(string product, string property, StringBuilder value, ref int size);
}

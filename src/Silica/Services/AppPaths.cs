using System.IO;

namespace Silica.Services;

public static class AppPaths
{
    private const string PortableSettingsFile = "Silica.settings.json";

    /// <summary>Portable mode: a settings file sits next to the exe.</summary>
    public static bool IsPortable { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, PortableSettingsFile));

    public static string DataDirectory { get; } = IsPortable
        ? AppContext.BaseDirectory
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Silica");

    public static string SettingsFile => IsPortable
        ? Path.Combine(AppContext.BaseDirectory, PortableSettingsFile)
        : Path.Combine(DataDirectory, "settings.json");

    public static string LogFile => Path.Combine(DataDirectory, "silica.log");

    public static string DefaultSensorLogFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Silica", "Sensor logs");

    public static string DefaultReportFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Silica");

    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Silica.exe");

    public static void EnsureDataDirectory() => Directory.CreateDirectory(DataDirectory);
}

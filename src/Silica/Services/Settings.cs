using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Silica.Core.Sensors;

namespace Silica.Services;

public enum AppTheme { System, Dark, Light }

public enum RenderingMode { Automatic, Hardware, Software }

public sealed class AlertRule
{
    public string SensorId { get; set; } = "";
    public string SensorName { get; set; } = "";
    public bool Above { get; set; } = true;
    public double Threshold { get; set; }
    public int DurationSeconds { get; set; } = 5;
    public bool Enabled { get; set; } = true;
}

public sealed class WindowPlacement
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1320;
    public double Height { get; set; } = 840;
    public bool Maximized { get; set; }
}

public sealed class AppSettings
{
    public string Language { get; set; } = "auto";
    public AppTheme Theme { get; set; } = AppTheme.System;
    public string Accent { get; set; } = "#4C8DFF";
    public bool Fahrenheit { get; set; }
    public string StartPage { get; set; } = "dashboard";
    public bool SidebarCollapsed { get; set; }
    public RenderingMode Rendering { get; set; } = RenderingMode.Automatic;
    public WindowPlacement Window { get; set; } = new();

    public int PollIntervalMs { get; set; } = 1000;
    public SensorGroups SensorGroups { get; set; } = SensorGroups.All;
    public bool ShowVirtualNetwork { get; set; }
    public List<string> ChartSensors { get; set; } = [];
    public List<string> HiddenSensors { get; set; } = [];

    public bool TrayEnabled { get; set; }
    public bool CloseToTray { get; set; }
    public bool MinimizeToTray { get; set; }
    public List<string> TraySensors { get; set; } = [];

    public bool OverlayEnabled { get; set; }
    public List<string> OverlaySensors { get; set; } = [];
    public double OverlayOpacity { get; set; } = 0.9;
    public double OverlayScale { get; set; } = 1.0;
    public double OverlayLeft { get; set; } = double.NaN;
    public double OverlayTop { get; set; } = double.NaN;
    public bool OverlayClickThrough { get; set; }

    public bool LoggingEnabled { get; set; }
    public string LogFolder { get; set; } = "";
    public int LogIntervalSeconds { get; set; } = 2;
    public List<string> LogSensors { get; set; } = [];

    public bool AlertsEnabled { get; set; } = true;
    public int AlertCooldownMinutes { get; set; } = 5;
    public List<AlertRule> Alerts { get; set; } = [];

    public bool DeepSearch { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
}

public static class Settings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private static Timer? _saveTimer;

    public static AppSettings Current { get; private set; } = new();

    /// <summary>False for scripted runs (screenshots, string dumps) so they never overwrite the user's settings.</summary>
    public static bool Persist { get; set; } = true;

    public static event Action? Changed;

    public static void Load()
    {
        try
        {
            // An empty file is how portable mode is switched on, so it simply means defaults.
            var text = File.Exists(AppPaths.SettingsFile) ? File.ReadAllText(AppPaths.SettingsFile) : "";
            Current = string.IsNullOrWhiteSpace(text) ? new AppSettings() : JsonSerializer.Deserialize<AppSettings>(text, Json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings reset, file unreadable: {ex.Message}");
            Current = new AppSettings();
        }
    }

    /// <summary>Saves shortly after the last change, so dragging a slider does not hammer the disk.</summary>
    public static void Touch()
    {
        Changed?.Invoke();
        _saveTimer ??= new Timer(_ => SaveNow());
        _saveTimer.Change(600, Timeout.Infinite);
    }

    public static void SaveNow()
    {
        if (!Persist) return;
        try
        {
            AppPaths.EnsureDataDirectory();
            var tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json));
            File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings not saved: {ex.Message}");
        }
    }
}

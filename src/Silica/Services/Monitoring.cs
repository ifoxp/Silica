using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Silica.Core.Sensors;
using Silica.Localization;
using Silica.Native;
using Silica.Views;

namespace Silica.Services;

/// <summary>
/// Everything that runs while the main window may be hidden: tray icons, overlay, sensor logging and alerts.
/// Sensors are only polled while one of these features is switched on.
/// </summary>
public static class Monitoring
{
    private static readonly DispatcherTimer Timer = new();
    private static readonly List<(string SensorId, TrayIcon Icon)> SensorIcons = [];
    private static readonly Dictionary<AlertRule, DateTime> Breach = [];
    private static readonly Dictionary<AlertRule, DateTime> Fired = [];
    private static TrayIcon? _appIcon;
    private static OverlayWindow? _overlay;
    private static IDisposable? _subscription;
    private static StreamWriter? _log;
    private static List<SensorReading> _logColumns = [];
    private static DateTime _lastLogWrite;
    private static bool _started;

    public static event Action? LoggingChanged;

    public static bool IsLogging => _log != null;
    public static string? LogFile { get; private set; }

    public static void Start()
    {
        if (_started) return;
        _started = true;
        Timer.Tick += (_, _) => Tick();
        Settings.Changed += Reconcile;
        Loc.Instance.LanguageChanged += () => { foreach (var (_, icon) in SensorIcons) icon.Dispose(); SensorIcons.Clear(); Reconcile(); };
        ThemeService.Changed += () => _overlay?.ApplyTheme();
        if (Settings.Current.LoggingEnabled) StartLogging();
        Reconcile();
    }

    public static void Stop()
    {
        StopLogging();
        foreach (var (_, icon) in SensorIcons) icon.Dispose();
        SensorIcons.Clear();
        _appIcon?.Dispose();
        _appIcon = null;
        _overlay?.Close();
        _overlay = null;
        Timer.Stop();
        _subscription?.Dispose();
    }

    /// <summary>Brings features in line with the current settings.</summary>
    public static void Reconcile()
    {
        var s = Settings.Current;

        if (s.TrayEnabled && _appIcon == null)
        {
            _appIcon = CreateIcon();
            _appIcon.Update(TrayIcon.FromIcon(new Uri("pack://application:,,,/Silica;component/Assets/Silica.ico")), "Silica");
        }
        else if (!s.TrayEnabled && _appIcon != null)
        {
            _appIcon.Dispose();
            _appIcon = null;
        }

        var wantedIcons = s.TrayEnabled ? s.TraySensors.Take(6).ToList() : [];
        foreach (var stale in SensorIcons.Where(x => !wantedIcons.Contains(x.SensorId)).ToList())
        {
            stale.Icon.Dispose();
            SensorIcons.Remove(stale);
        }

        if (s.OverlayEnabled && s.OverlaySensors.Count > 0)
        {
            if (_overlay == null)
            {
                _overlay = new OverlayWindow();
                _overlay.Show();
            }
            _overlay.ApplySettings();
        }
        else if (_overlay != null)
        {
            _overlay.Close();
            _overlay = null;
        }

        var needsSensors = wantedIcons.Count > 0 || _overlay != null || IsLogging || (s.AlertsEnabled && s.Alerts.Any(a => a.Enabled));
        if (needsSensors)
        {
            _subscription ??= SensorHub.Shared.Subscribe();
            Timer.Interval = SensorHub.Shared.Interval;
            Timer.Start();
        }
        else
        {
            Timer.Stop();
            _subscription?.Dispose();
            _subscription = null;
        }
    }

    private static TrayIcon CreateIcon()
    {
        var icon = new TrayIcon();
        icon.Click += ShowMainWindow;
        icon.RightClick += ShowTrayMenu;
        return icon;
    }

    private static void Tick()
    {
        var s = Settings.Current;
        var hub = SensorHub.Shared;

        if (s.TrayEnabled)
        {
            foreach (var id in s.TraySensors.Take(6))
            {
                var reading = hub.Find(id);
                if (reading == null) continue;
                var entry = SensorIcons.FirstOrDefault(x => x.SensorId == id);
                if (entry.Icon == null)
                {
                    entry = (id, CreateIcon());
                    SensorIcons.Add(entry);
                }
                var (bg, fg) = TrayColors(reading);
                entry.Icon.Update(TrayIcon.RenderText(SensorFormat.Short(reading.Kind, reading.Value), bg, fg),
                    $"{Loc.Instance.T(reading.Name)} ({reading.Device.Name})\n{SensorFormat.Format(reading.Kind, reading.Value)}");
            }
        }

        _overlay?.UpdateValues();
        if (_log != null && DateTime.Now - _lastLogWrite >= TimeSpan.FromSeconds(Math.Max(1, s.LogIntervalSeconds))) WriteLogRow();
        if (s.AlertsEnabled) CheckAlerts(s);
    }

    private static (Color Background, Color Foreground) TrayColors(SensorReading r)
    {
        var accent = ThemeService.Accent;
        if (r.Kind == SensorKind.Temperature && r.Value is > 85) return (Color.FromRgb(0xD9, 0x3A, 0x34), Colors.White);
        if (r.Kind == SensorKind.Temperature && r.Value is > 75) return (Color.FromRgb(0xC7, 0x86, 0x0E), Colors.White);
        return r.Kind switch
        {
            SensorKind.Temperature => (Color.FromRgb(0x1F, 0x24, 0x2C), Colors.White),
            _ => (accent, Colors.White)
        };
    }

    public static void ShowMainWindow()
    {
        if (Application.Current.MainWindow is MainWindow w) w.BringToFront();
    }

    private static void ShowTrayMenu()
    {
        var loc = Loc.Instance;
        var s = Settings.Current;
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };

        var open = new MenuItem { Header = loc.T("Open Silica"), FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) => ShowMainWindow();
        menu.Items.Add(open);

        var overlay = new MenuItem { Header = loc.T("Overlay"), IsChecked = s.OverlayEnabled, IsEnabled = s.OverlaySensors.Count > 0 };
        overlay.Click += (_, _) => { s.OverlayEnabled = !s.OverlayEnabled; Settings.Touch(); };
        menu.Items.Add(overlay);

        var record = new MenuItem { Header = loc.T("Record sensors"), IsChecked = IsLogging };
        record.Click += (_, _) => { if (IsLogging) StopLogging(); else StartLogging(); };
        menu.Items.Add(record);

        menu.Items.Add(new Separator());
        var exit = new MenuItem { Header = loc.T("Exit") };
        exit.Click += (_, _) => (Application.Current.MainWindow as MainWindow)?.ExitApplication();
        menu.Items.Add(exit);
        menu.IsOpen = true;
    }

    // Logging

    public static void StartLogging()
    {
        if (_log != null) return;
        try
        {
            var s = Settings.Current;
            var folder = string.IsNullOrWhiteSpace(s.LogFolder) ? AppPaths.DefaultSensorLogFolder : s.LogFolder;
            Directory.CreateDirectory(folder);
            LogFile = Path.Combine(folder, $"Silica {DateTime.Now:yyyy-MM-dd HHmmss}.csv");
            _log = new StreamWriter(LogFile, false, new UTF8Encoding(true)) { AutoFlush = true };
            _logColumns = [];
            _lastLogWrite = DateTime.MinValue;
            if (!s.LoggingEnabled) { s.LoggingEnabled = true; Settings.Touch(); }
        }
        catch (Exception ex)
        {
            Log.Error("Sensor log not started", ex);
            _log = null;
            LogFile = null;
            ErrorDialog.Show(Application.Current.MainWindow, ex, Loc.Instance.T("The sensor log file could not be created."));
        }
        Reconcile();
        LoggingChanged?.Invoke();
    }

    public static void StopLogging()
    {
        if (_log == null) return;
        _log.Dispose();
        _log = null;
        if (Settings.Current.LoggingEnabled) { Settings.Current.LoggingEnabled = false; Settings.Touch(); }
        Reconcile();
        LoggingChanged?.Invoke();
    }

    private static void WriteLogRow()
    {
        var hub = SensorHub.Shared;
        if (hub.State != SensorHubState.Ready || _log == null) return;
        var sep = CultureInfo.CurrentCulture.TextInfo.ListSeparator;
        if (_logColumns.Count == 0)
        {
            var wanted = Settings.Current.LogSensors;
            _logColumns = hub.AllSensors.Where(r => wanted.Count == 0 || wanted.Contains(r.Id)).ToList();
            if (_logColumns.Count == 0) return;
            var header = new StringBuilder(Loc.Instance.T("Time"));
            foreach (var c in _logColumns)
                header.Append(sep).Append(Csv($"{c.Device.Name} / {Loc.Instance.T(c.Name)} [{SensorFormat.Unit(c.Kind)}]", sep));
            _log.WriteLine(header);
        }

        var line = new StringBuilder(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        foreach (var c in _logColumns)
        {
            line.Append(sep);
            if (c.Value is { } v) line.Append(SensorFormat.Display(c.Kind, v).ToString("0.###", CultureInfo.CurrentCulture));
        }
        try
        {
            _log.WriteLine(line);
            _lastLogWrite = DateTime.Now;
        }
        catch (IOException ex)
        {
            Log.Error("Sensor log write failed", ex);
            StopLogging();
        }
    }

    private static string Csv(string s, string sep) =>
        s.Contains(sep) || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    // Alerts

    private static void CheckAlerts(AppSettings s)
    {
        var now = DateTime.Now;
        foreach (var rule in s.Alerts.Where(a => a.Enabled))
        {
            var reading = SensorHub.Shared.Find(rule.SensorId);
            if (reading?.Value is not { } raw) continue;
            var value = SensorFormat.Display(reading.Kind, raw);
            var breached = rule.Above ? value > rule.Threshold : value < rule.Threshold;
            if (!breached)
            {
                Breach.Remove(rule);
                continue;
            }
            if (!Breach.TryGetValue(rule, out var since)) Breach[rule] = since = now;
            if (now - since < TimeSpan.FromSeconds(rule.DurationSeconds)) continue;
            if (Fired.TryGetValue(rule, out var last) && now - last < TimeSpan.FromMinutes(Math.Max(1, s.AlertCooldownMinutes))) continue;
            Fired[rule] = now;

            var title = Loc.Instance.T(rule.Above ? "Value is too high" : "Value is too low");
            var text = $"{Loc.Instance.T(reading.Name)} ({reading.Device.Name}): {SensorFormat.Format(reading.Kind, raw)}";
            AlertToast.Show(title, text);
        }
    }
}

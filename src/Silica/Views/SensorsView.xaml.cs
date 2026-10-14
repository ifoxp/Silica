using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Silica.Controls;
using Silica.Core.Sensors;
using Silica.Localization;
using Silica.Services;

namespace Silica.Views;

public partial class SensorsView : UserControl, INavigable
{
    private readonly DispatcherTimer _timer = new();
    private readonly HashSet<string> _collapsed = [];
    private readonly List<SensorRow> _rows = [];
    private readonly List<LineChart> _charts = [];
    private IDisposable? _subscription;
    private SensorKind? _kindFilter;
    private int _window = 120;
    private bool _dirty = true;

    public SensorsView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Tick();
        SensorHub.Shared.DevicesChanged += () => Dispatcher.BeginInvoke(() => _dirty = true);
        Loc.Instance.LanguageChanged += () => { _dirty = true; Tick(); };
        Monitoring.LoggingChanged += UpdateRecordButton;
        // Charts never take more than about half of the page, so the sensor list stays usable on small windows.
        SizeChanged += (_, _) => ChartScroll.MaxHeight = Math.Max(160, ActualHeight * 0.42);
    }

    public void OnNavigatedTo(string? target)
    {
        DriverHint.Visibility = Elevation.IsElevated && !SensorHub.DriverInstalled ? Visibility.Visible : Visibility.Collapsed;
        _subscription ??= SensorHub.Shared.Subscribe();
        _timer.Interval = SensorHub.Shared.Interval;
        _timer.Start();
        UpdateRecordButton();
        Tick();
        if (target != null) Dispatcher.BeginInvoke(() => Reveal(target), DispatcherPriority.Loaded);
    }

    public void OnNavigatedFrom()
    {
        _timer.Stop();
        _subscription?.Dispose();
        _subscription = null;
    }

    private void Tick()
    {
        if (_dirty) Rebuild();
        foreach (var r in _rows) r.Update();
        foreach (var c in _charts) c.Refresh(SensorHub.Shared.Interval.TotalSeconds);
        UpdateLegendValues();

        var hub = SensorHub.Shared;
        Subtitle.Text = hub.State == SensorHubState.Ready
            ? Loc.Instance.F("{0} · updated every {1}", Loc.Instance.N(_rows.Count, "{0} sensors"), IntervalText(hub.Interval))
            : Loc.Instance.T("Reading sensors...");
    }

    private static string IntervalText(TimeSpan t) => t.TotalSeconds >= 1
        ? Loc.Instance.F("{0} s", t.TotalSeconds.ToString("0.#", CultureInfo.CurrentCulture))
        : Loc.Instance.F("{0} ms", t.TotalMilliseconds);

    private void Rebuild()
    {
        _dirty = false;
        var hub = SensorHub.Shared;
        var hidden = Settings.Current.HiddenSensors.ToHashSet();
        var q = TextMatch.Normalize(FilterBox.Text);
        var items = new List<SensorListRow>();
        var kinds = new SortedSet<SensorKind>();
        _rows.Clear();

        void Add(DeviceNode device)
        {
            var sensors = device.Sensors.Where(s => !hidden.Contains(s.Id)).ToList();
            foreach (var s in sensors) kinds.Add(s.Kind);
            var visible = sensors.Where(s => (_kindFilter == null || s.Kind == _kindFilter || (_kindFilter == SensorKind.Data && s.Kind == SensorKind.SmallData))
                                             && (q.Length == 0 || Matches(q, s, device))).ToList();
            var childStart = items.Count;
            if (visible.Count > 0)
            {
                var collapsed = _collapsed.Contains(device.Id) && q.Length == 0;
                items.Add(new DeviceRow(device, visible.Count, collapsed));
                if (!collapsed)
                {
                    foreach (var s in visible.OrderBy(s => s.Kind).ThenBy(s => s.Name, NaturalComparer.Instance))
                    {
                        var row = new SensorRow(s);
                        items.Add(row);
                        _rows.Add(row);
                    }
                }
            }
            foreach (var child in device.Children) Add(child);
        }

        foreach (var d in hub.Devices) Add(d);
        List.ItemsSource = items;
        BuildKindChips(kinds);
        ApplyPins();

        var empty = items.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty)
        {
            (EmptyTitle.Text, EmptyText.Text) = hub.State is SensorHubState.Opening or SensorHubState.Closed
                ? (Loc.Instance.T("Reading sensors..."), Loc.Instance.T("This usually takes a second or two."))
                : q.Length > 0 || _kindFilter != null
                    ? (Loc.Instance.T("No matching sensors"), Loc.Instance.T("Try another word or clear the filter."))
                    : Elevation.IsElevated
                        ? (Loc.Instance.T("No sensors found"), Loc.Instance.T("Check which hardware is monitored in Settings."))
                        : (Loc.Instance.T("No sensors found"), Loc.Instance.T("Most sensors need administrator rights. Restart Silica as administrator to see them."));
        }
    }

    private static bool Matches(string q, SensorReading s, DeviceNode device) =>
        TextMatch.Normalize(Loc.Instance.T(s.Name) + " " + s.Name + " " + device.Name + " " + Loc.Instance.T(SensorRow.KindTitle(s.Kind)))
            .Contains(q, StringComparison.Ordinal);

    private void BuildKindChips(IEnumerable<SensorKind> kinds)
    {
        KindChips.Children.Clear();
        KindChips.Children.Add(Chip(null, Loc.Instance.T("All")));
        foreach (var k in kinds.Select(k => k == SensorKind.SmallData ? SensorKind.Data : k).Distinct())
            KindChips.Children.Add(Chip(k, Loc.Instance.T(SensorRow.KindTitle(k))));
    }

    private ToggleButton Chip(SensorKind? kind, string text)
    {
        var chip = new ToggleButton { Style = (Style)FindResource("Chip"), Content = text, IsChecked = _kindFilter == kind };
        chip.Click += (_, _) =>
        {
            _kindFilter = kind;
            Rebuild();
        };
        return chip;
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        Rebuild();
        foreach (var r in _rows) r.Update();
    }

    private void OnDeviceClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DeviceRow row })
        {
            if (!_collapsed.Remove(row.Device.Id)) _collapsed.Add(row.Device.Id);
            Rebuild();
            foreach (var r in _rows) r.Update();
        }
    }

    private void OnSensorClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SensorRow row }) TogglePin(row.Reading.Id);
    }

    private void TogglePin(string id)
    {
        var pins = Settings.Current.ChartSensors;
        if (!pins.Remove(id))
        {
            if (pins.Count >= 8) pins.RemoveAt(0);
            pins.Add(id);
        }
        Settings.Touch();
        ApplyPins();
        foreach (var c in _charts) c.Refresh(SensorHub.Shared.Interval.TotalSeconds);
    }

    private void ApplyPins()
    {
        var pins = Settings.Current.ChartSensors;
        foreach (var r in _rows)
        {
            var i = pins.IndexOf(r.Reading.Id);
            r.Pinned = i >= 0;
            r.Dot = i >= 0 ? new SolidColorBrush(ThemeService.Series(i)) : Brushes.Transparent;
        }

        var readings = pins.Select(id => SensorHub.Shared.Find(id)).OfType<SensorReading>().ToList();
        Charts.Children.Clear();
        _charts.Clear();
        Legend.Children.Clear();
        ChartHint.Visibility = readings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var group in readings.GroupBy(r => r.Kind))
        {
            var series = group.Select(r => new ChartSeries(r, ThemeService.Series(pins.IndexOf(r.Id)), Loc.Instance.T(r.Name))).ToList();
            var chart = new LineChart
            {
                Height = readings.Count > 4 ? 150 : 180,
                Window = _window,
                Margin = new Thickness(0, 4, 0, 4),
                FixedMin = group.Key is SensorKind.Load or SensorKind.Control ? 0 : null,
                FixedMax = group.Key is SensorKind.Load or SensorKind.Control ? 100 : null,
                Series = series
            };
            _charts.Add(chart);
            Charts.Children.Add(chart);
        }

        foreach (var r in readings)
        {
            var dot = new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(ThemeService.Series(pins.IndexOf(r.Id))), Margin = new Thickness(0, 0, 8, 0) };
            var name = new TextBlock { Style = (Style)FindResource("Text.Caption"), Text = $"{Loc.Instance.T(r.Name)} · {r.Device.Name}", MaxWidth = 260 };
            var value = new TextBlock { Style = (Style)FindResource("Text.Number"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 6, 0), Tag = r };
            var close = new Button { Style = (Style)FindResource("Button.Icon"), Width = 22, Height = 22, FontSize = 9, Content = "", ToolTip = Loc.Instance.T("Remove from chart") };
            var id = r.Id;
            close.Click += (_, _) => TogglePin(id);
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(dot);
            panel.Children.Add(name);
            panel.Children.Add(value);
            panel.Children.Add(close);
            Legend.Children.Add(new Border
            {
                Background = (Brush)FindResource("Brush.SurfaceAlt"), CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 2, 4, 2),
                Margin = new Thickness(0, 0, 8, 6), Child = panel
            });
        }
        UpdateLegendValues();
    }

    private void UpdateLegendValues()
    {
        foreach (var b in Legend.Children.OfType<Border>())
            if (b.Child is StackPanel p && p.Children[2] is TextBlock { Tag: SensorReading r } t)
                t.Text = SensorFormat.Format(r.Kind, r.Value);
    }

    private void OnWindow(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out var w))
        {
            _window = w;
            foreach (var c in _charts)
            {
                c.Window = w;
                c.Refresh(SensorHub.Shared.Interval.TotalSeconds);
            }
        }
    }

    private void OnGetDriver(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://pawnio.eu") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"Browser: {ex.Message}"); }
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        SensorHub.Shared.ResetStatistics();
        foreach (var r in _rows) r.Update();
    }

    private void OnRecord(object sender, RoutedEventArgs e)
    {
        if (Monitoring.IsLogging) Monitoring.StopLogging();
        else Monitoring.StartLogging();
        UpdateRecordButton();
    }

    private void UpdateRecordButton()
    {
        Dispatcher.BeginInvoke(() =>
        {
            RecordButton.IsChecked = Monitoring.IsLogging;
            RecordText.Text = Monitoring.IsLogging ? Loc.Instance.T("Stop recording") : Loc.Instance.T("Record");
            RecordButton.ToolTip = Monitoring.IsLogging && Monitoring.LogFile != null
                ? Monitoring.LogFile
                : Loc.Instance.T("Write all sensor values to a CSV file");
        });
    }

    private void OnSensorMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu { PlacementTarget: FrameworkElement { DataContext: SensorRow row } } menu) return;
        var loc = Loc.Instance;
        var s = Settings.Current;
        var id = row.Reading.Id;
        menu.Items.Clear();

        MenuItem Item(string text, bool isChecked, Action action)
        {
            var mi = new MenuItem { Header = loc.T(text), IsChecked = isChecked };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
            return mi;
        }

        Item("Show on chart", s.ChartSensors.Contains(id), () => TogglePin(id));
        Item("Show in tray", s.TraySensors.Contains(id), () => { Toggle(s.TraySensors, id); if (s.TraySensors.Count > 0) s.TrayEnabled = true; Settings.Touch(); });
        Item("Show in overlay", s.OverlaySensors.Contains(id), () => { Toggle(s.OverlaySensors, id); if (s.OverlaySensors.Count > 0) s.OverlayEnabled = true; Settings.Touch(); });
        menu.Items.Add(new Separator());
        Item("Add alert...", false, () => AlertDialog.Show(Window.GetWindow(this), row.Reading));
        Item("Copy value", false, () =>
        {
            try { Clipboard.SetText($"{loc.T(row.Reading.Name)}: {row.Value}"); } catch { }
        });
        menu.Items.Add(new Separator());
        Item("Hide this sensor", false, () =>
        {
            s.HiddenSensors.Add(id);
            s.ChartSensors.Remove(id);
            Settings.Touch();
            Rebuild();
            foreach (var r in _rows) r.Update();
        });
        if (s.HiddenSensors.Count > 0)
            Item("Show hidden sensors", false, () =>
            {
                s.HiddenSensors.Clear();
                Settings.Touch();
                Rebuild();
                foreach (var r in _rows) r.Update();
            });
    }

    private static void Toggle(List<string> list, string id)
    {
        if (!list.Remove(id)) list.Add(id);
    }

    private void Reveal(string sensorId)
    {
        var reading = SensorHub.Shared.Find(sensorId);
        if (reading == null) return;
        FilterBox.Text = "";
        _kindFilter = null;
        _collapsed.Remove(reading.Device.Id);
        Rebuild();
        var row = _rows.FirstOrDefault(r => r.Reading.Id == sensorId);
        if (row == null) return;
        List.ScrollIntoView(row);
        List.SelectedItem = row;
        if (!Settings.Current.ChartSensors.Contains(sensorId)) TogglePin(sensorId);
    }
}

/// <summary>"Core #2" before "Core #10".</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();
    public int Compare(string? x, string? y) => SmartComparer.CompareText(x ?? "", y ?? "");
}

using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Silica.Controls;
using Silica.Core.Bench;
using Silica.Core.Sensors;
using Silica.Core.Stress;
using Silica.Core.Util;
using Silica.Localization;
using Silica.Services;

namespace Silica.Views;

public sealed class StressView : UserControl, INavigable
{
    private static readonly (StressComponent Component, string Title, string Hint, string Icon)[] Components =
    [
        (StressComponent.Cpu, "Processor", "Integer math on every core", ""),
        (StressComponent.Fpu, "Floating point", "Heavy vector math, the hottest load", ""),
        (StressComponent.Cache, "Cache", "Data that fits in the CPU caches", ""),
        (StressComponent.Memory, "Memory", "Writes and verifies patterns in RAM", ""),
        (StressComponent.Disk, "Disk", "Writes and verifies a temporary file", "")
    ];

    private readonly StressEngine _engine = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ObservableCollection<string> _log = [];
    private readonly Dictionary<StressComponent, ToggleButton> _toggles = [];
    private readonly List<LineChart> _charts = [];
    private IDisposable? _subscription;
    private bool _visible;

    private Button _start = null!;
    private TextBlock _startText = null!, _startIcon = null!;
    private TextBlock _state = null!, _elapsed = null!, _iterations = null!, _errors = null!, _throttle = null!;
    private Border _stateDot = null!;

    private double _peakClock;
    private DateTime _lowSince;
    private bool _throttleLogged;
    private int _throttleEvents;

    public StressView()
    {
        Content = BuildLayout();
        _timer.Tick += (_, _) => Tick();
        _engine.Log += message => Dispatcher.BeginInvoke(() => AddLog(message));
        _engine.ErrorDetected += (_, message) => Dispatcher.BeginInvoke(() => AddLog(message, error: true));
        Loc.Instance.LanguageChanged += () => { Content = BuildLayout(); UpdateStatus(); };
    }

    public void OnNavigatedTo(string? target)
    {
        _visible = true;
        _subscription ??= SensorHub.Shared.Subscribe();
        _timer.Start();
        BuildCharts();
        Tick();
    }

    public void OnNavigatedFrom()
    {
        _visible = false;
        if (_engine.IsRunning) return;
        _timer.Stop();
        _subscription?.Dispose();
        _subscription = null;
    }

    private FrameworkElement BuildLayout()
    {
        var loc = Loc.Instance;
        var app = Application.Current;
        T R<T>(string key) => (T)app.FindResource(key);

        var header = new Grid { Margin = new Thickness(28, 22, 28, 14) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(8), Background = R<Brush>("Brush.AccentSubtle"), Margin = new Thickness(0, 0, 14, 0),
            Child = new TextBlock { Style = R<Style>("Icon"), Text = "", FontSize = 18, Foreground = R<Brush>("Brush.AccentText") }
        });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Style = R<Style>("Text.Title"), Text = loc.T("Stability test") });
        titles.Children.Add(new TextBlock { Style = R<Style>("Text.Caption"), Text = loc.T("Loads the system and checks every result, so hidden instability shows up as errors"), Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);

        _startIcon = new TextBlock { Style = R<Style>("Icon"), FontSize = 12, Foreground = R<Brush>("Brush.OnAccent"), Margin = new Thickness(0, 0, 8, 0) };
        _startText = new TextBlock();
        var sc = new StackPanel { Orientation = Orientation.Horizontal };
        sc.Children.Add(_startIcon);
        sc.Children.Add(_startText);
        _start = new Button { Style = R<Style>("Button.Accent"), Content = sc, MinWidth = 120, VerticalAlignment = VerticalAlignment.Center };
        _start.Click += (_, _) => Toggle();
        Grid.SetColumn(_start, 2);
        header.Children.Add(_start);

        // Component toggles
        var toggles = new AutoFitPanel { MinItemWidth = 160, MaxColumns = 5, Spacing = 10, Margin = new Thickness(0, 0, 0, 16) };
        var previous = _toggles.ToDictionary(p => p.Key, p => p.Value.IsChecked == true);
        _toggles.Clear();
        foreach (var (component, title, hint, icon) in Components)
        {
            var stack = new StackPanel();
            var head = new DockPanel();
            var glyph = new TextBlock { Style = R<Style>("Icon"), Text = icon, FontSize = 14, Margin = new Thickness(0, 1, 8, 0), Foreground = R<Brush>("Brush.AccentText"), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(glyph, Dock.Left);
            head.Children.Add(glyph);
            head.Children.Add(new TextBlock { Style = R<Style>("Text.Section"), Text = loc.T(title), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None });
            stack.Children.Add(head);
            stack.Children.Add(new TextBlock { Style = R<Style>("Text.Caption"), Text = loc.T(hint), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 6, 0, 0) });
            var toggle = new ToggleButton
            {
                Style = R<Style>("Chip"), Content = stack, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top,
                Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0),
                IsChecked = previous.TryGetValue(component, out var was) ? was : component is StressComponent.Cpu or StressComponent.Fpu or StressComponent.Cache,
                IsEnabled = !_engine.IsRunning
            };
            _toggles[component] = toggle;
            toggles.Children.Add(toggle);
        }

        // Status
        _stateDot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        _state = new TextBlock { Style = R<Style>("Text.Section") };
        var stateRow = new StackPanel { Orientation = Orientation.Horizontal };
        stateRow.Children.Add(_stateDot);
        stateRow.Children.Add(_state);
        _elapsed = Metric();
        _iterations = Metric();
        _errors = Metric();
        _throttle = Metric();
        var metrics = new AutoFitPanel { MinItemWidth = 150, MaxColumns = 4, Spacing = 12, Margin = new Thickness(0, 14, 0, 0) };
        metrics.Children.Add(Labeled(loc.T("Elapsed"), _elapsed));
        metrics.Children.Add(Labeled(loc.T("Work units checked"), _iterations));
        metrics.Children.Add(Labeled(loc.T("Errors"), _errors));
        metrics.Children.Add(Labeled(loc.T("Throttling"), _throttle));
        var statusStack = new StackPanel();
        statusStack.Children.Add(stateRow);
        statusStack.Children.Add(metrics);
        var statusCard = new Border { Style = R<Style>("Card"), Child = statusStack, Margin = new Thickness(0, 0, 0, 16) };

        // Charts
        var chartsStack = new StackPanel { Name = "ChartsHost" };
        var chartsCard = new Border { Style = R<Style>("Card"), Child = chartsStack, Margin = new Thickness(0, 0, 0, 16), Padding = new Thickness(16, 12, 16, 8) };

        // Log
        var logList = new ItemsControl { ItemsSource = _log };
        logList.ItemTemplate = LogTemplate();
        var logStack = new StackPanel();
        logStack.Children.Add(new TextBlock { Style = R<Style>("Text.Section"), Text = loc.T("Events"), Margin = new Thickness(0, 0, 0, 8) });
        logStack.Children.Add(logList);
        var logCard = new Border { Style = R<Style>("Card"), Child = logStack };

        var body = new StackPanel { Margin = new Thickness(28, 0, 28, 28) };
        body.Children.Add(toggles);
        body.Children.Add(statusCard);
        body.Children.Add(chartsCard);
        body.Children.Add(logCard);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(header);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body, Focusable = false };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        _chartsHost = chartsStack;
        UpdateStatus();
        if (_visible) BuildCharts();
        return root;

        TextBlock Metric() => new() { Style = R<Style>("Text.Metric"), FontSize = 22, Text = "—" };
        FrameworkElement Labeled(string caption, TextBlock value)
        {
            var s = new StackPanel();
            s.Children.Add(new TextBlock { Style = R<Style>("Text.Caption"), Text = caption });
            s.Children.Add(value);
            return s;
        }
    }

    private StackPanel? _chartsHost;

    private DataTemplate LogTemplate()
    {
        var factory = new FrameworkElementFactory(typeof(TextBlock));
        factory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        factory.SetValue(TextBlock.StyleProperty, Application.Current.FindResource("Text.Mono"));
        factory.SetValue(TextBlock.ForegroundProperty, Application.Current.FindResource("Brush.Text2"));
        factory.SetValue(TextBlock.MarginProperty, new Thickness(0, 2, 0, 2));
        factory.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        return new DataTemplate { VisualTree = factory };
    }

    private void BuildCharts()
    {
        if (_chartsHost == null) return;
        _chartsHost.Children.Clear();
        _charts.Clear();
        var k = SensorHub.Shared.Key;
        var groups = new List<(string Title, SensorKind Kind, List<SensorReading> Readings)>
        {
            ("Temperature", SensorKind.Temperature, new[] { k.CpuTemp, k.GpuTemp }.OfType<SensorReading>().ToList()),
            ("Clock", SensorKind.Clock, new[] { k.CpuClock }.OfType<SensorReading>().ToList()),
            ("Power draw", SensorKind.Power, new[] { k.CpuPower }.OfType<SensorReading>().ToList()),
            ("Load", SensorKind.Load, new[] { k.CpuLoad }.OfType<SensorReading>().ToList())
        };
        var shown = groups.Where(g => g.Readings.Count > 0).ToList();
        if (shown.Count == 0)
        {
            _chartsHost.Children.Add(new TextBlock
            {
                Style = (Style)Application.Current.FindResource("Text.Secondary"), Margin = new Thickness(0, 4, 0, 8), TextWrapping = TextWrapping.Wrap,
                Text = SensorHub.Shared.State == SensorHubState.Ready
                    ? Loc.Instance.T("No processor sensors are available. Run Silica as administrator to watch temperatures during the test.")
                    : Loc.Instance.T("Reading sensors...")
            });
            return;
        }

        var grid = new AutoFitPanel { MinItemWidth = 360, MaxColumns = 2, Spacing = 12 };
        var colorIndex = 0;
        foreach (var (title, kind, readings) in shown)
        {
            var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            stack.Children.Add(new TextBlock { Style = (Style)Application.Current.FindResource("Text.Caption"), Text = Loc.Instance.T(title) });
            var chart = new LineChart
            {
                Height = 150, Window = 600,
                FixedMin = kind == SensorKind.Load ? 0 : null, FixedMax = kind == SensorKind.Load ? 100 : null,
                Series = readings.Select(r => new ChartSeries(r, ThemeService.Series(colorIndex++), $"{Loc.Instance.T(r.Name)} ({r.Device.Name})")).ToList()
            };
            stack.Children.Add(chart);
            _charts.Add(chart);
            grid.Children.Add(stack);
        }
        _chartsHost.Children.Add(grid);
    }

    private void Toggle()
    {
        if (_engine.IsRunning)
        {
            _lastIterations = _engine.Iterations;
            _lastErrors = _engine.ErrorCount;
            _engine.Stop();
            _runEnd = DateTime.Now;
            AddLog(Loc.Instance.F("Test stopped after {0}", Fmt.Duration(_runEnd.Value - (_runStart ?? _runEnd.Value))));
            UpdateStatus();
            if (!_visible) OnNavigatedFrom();
            return;
        }

        var components = _toggles.Where(p => p.Value.IsChecked == true).Aggregate(StressComponent.None, (a, p) => a | p.Key);
        if (components == StressComponent.None)
        {
            ErrorDialog.Message(Window.GetWindow(this), Loc.Instance.T("Nothing selected"), Loc.Instance.T("Choose at least one component to test."));
            return;
        }

        _log.Clear();
        _peakClock = 0;
        _throttleEvents = 0;
        _throttleLogged = false;
        _lowSince = default;
        SensorHub.Shared.ResetStatistics();
        try
        {
            _engine.Start(components);
        }
        catch (Exception ex)
        {
            Log.Error("Stability test failed to start", ex);
            ErrorDialog.Show(Window.GetWindow(this), ex, Loc.Instance.T("The test could not be started."));
            return;
        }
        _runStart = DateTime.Now;
        _runEnd = null;
        AddLog(Loc.Instance.F("Test started: {0}", string.Join(", ", Components.Where(c => components.HasFlag(c.Component)).Select(c => Loc.Instance.T(c.Title)))));
        _subscription ??= SensorHub.Shared.Subscribe();
        _timer.Start();
        BuildCharts();
        UpdateStatus();
    }

    private void Tick()
    {
        if (_charts.Count == 0 && SensorHub.Shared.State == SensorHubState.Ready && SensorHub.Shared.Key.CpuLoad != null) BuildCharts();
        foreach (var c in _charts) c.Refresh(SensorHub.Shared.Interval.TotalSeconds);
        if (_engine.IsRunning) WatchThrottling();
        UpdateStatus();
    }

    private void WatchThrottling()
    {
        var k = SensorHub.Shared.Key;
        if (k.CpuClock?.Value is not { } clock || k.CpuLoad?.Value is not { } load) return;
        var elapsed = DateTime.Now - (_runStart ?? DateTime.Now);
        if (load < 85) { _lowSince = default; return; }
        if (elapsed < TimeSpan.FromSeconds(20)) { _peakClock = Math.Max(_peakClock, clock); return; }
        if (_peakClock <= 0 || clock > _peakClock * 0.85)
        {
            _lowSince = default;
            _throttleLogged = false;
            return;
        }
        if (_lowSince == default) _lowSince = DateTime.Now;
        if (DateTime.Now - _lowSince < TimeSpan.FromSeconds(5) || _throttleLogged) return;
        _throttleLogged = true;
        _throttleEvents++;
        var drop = (1 - clock / _peakClock) * 100;
        var temp = k.CpuTemp?.Value is { } t ? $", {SensorFormat.Format(SensorKind.Temperature, t)}" : "";
        AddLog(Loc.Instance.F("Clock dropped by {0}% under full load{1}: the processor is throttling", Math.Round(drop), temp), error: true);
    }

    private void UpdateStatus()
    {
        if (_state == null) return;
        var loc = Loc.Instance;
        var running = _engine.IsRunning;
        var app = Application.Current;
        _startIcon.Text = running ? "" : "";
        _startText.Text = loc.T(running ? "Stop" : "Start");
        _start.Style = (Style)app.FindResource(running ? "Button.Danger" : "Button.Accent");
        foreach (var t in _toggles.Values) t.IsEnabled = !running;

        var started = _runStart != null;
        var errors = running ? _engine.ErrorCount : _lastErrors;
        var iterations = running ? _engine.Iterations : _lastIterations;
        _state.Text = running
            ? errors > 0 ? loc.T("Running, errors found") : loc.T("Running, no errors so far")
            : !started ? loc.T("Ready") : errors > 0 ? loc.T("Stopped, errors were found") : loc.T("Stopped, no errors");
        _stateDot.Background = (Brush)app.FindResource(errors > 0 ? "Brush.Danger" : running ? "Brush.Success" : started ? "Brush.Accent" : "Brush.Text3");

        _elapsed.Text = _runStart is { } start ? Fmt.Duration((_runEnd ?? DateTime.Now) - start) : "—";
        _iterations.Text = started ? iterations.ToString("N0", CultureInfo.CurrentCulture) : "—";
        _errors.Text = started ? errors.ToString("N0", CultureInfo.CurrentCulture) : "—";
        _errors.Foreground = (Brush)app.FindResource(errors > 0 ? "Brush.Danger" : "Brush.Text");
        _throttle.Text = !started ? "—" : _throttleEvents == 0 ? loc.T("Not detected") : loc.N(_throttleEvents, "{0} times");
        _throttle.Foreground = (Brush)app.FindResource(_throttleEvents > 0 ? "Brush.Warning" : "Brush.Text");
    }

    private DateTime? _runStart, _runEnd;
    private long _lastIterations, _lastErrors;

    private void AddLog(string message, bool error = false)
    {
        var colon = message.IndexOf(": ", StringComparison.Ordinal);
        var text = colon > 0 ? Loc.Instance.T(message[..colon]) + message[colon..] : Loc.Instance.T(message);
        _log.Insert(0, $"{DateTime.Now:HH:mm:ss}  {(error ? "! " : "  ")}{text}");
        while (_log.Count > 200) _log.RemoveAt(_log.Count - 1);
    }
}

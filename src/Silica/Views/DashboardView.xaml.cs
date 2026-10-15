using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Silica.Core.Sensors;
using Silica.Core.Util;
using Silica.Localization;
using Silica.Services;

namespace Silica.Views;

public partial class DashboardView : UserControl, INavigable
{
    private readonly DispatcherTimer _timer = new();
    private readonly List<(SensorReading Reading, TextBlock Value)> _thermals = [];
    private IDisposable? _subscription;
    private KeySensors? _builtFor;
    private DateTime _volumesAt;
    private string? _modules;
    private int _cores;

    public DashboardView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Tick();
        // Quick actions move under the title when the page gets narrow, instead of squeezing it.
        SizeChanged += (_, _) =>
        {
            var narrow = ActualWidth < 1000;
            Grid.SetRow(QuickActions, narrow ? 1 : 0);
            Grid.SetColumn(QuickActions, narrow ? 0 : 1);
            QuickActions.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            QuickActions.Margin = narrow ? new Thickness(0, 14, 0, 0) : new Thickness(0);
        };
        Loc.Instance.LanguageChanged += () => { _builtFor = null; UpdateStatic(); Tick(); };
        LoadStaticAsync();
    }

    public void OnNavigatedTo(string? target)
    {
        _subscription ??= SensorHub.Shared.Subscribe();
        _timer.Interval = SensorHub.Shared.Interval;
        _timer.Start();
        _volumesAt = default;
        Tick();
    }

    public void OnNavigatedFrom()
    {
        _timer.Stop();
        _subscription?.Dispose();
        _subscription = null;
    }

    private void LoadStaticAsync()
    {
        UpdateStatic();
        Task.Run(() =>
        {
            var modules = Wmi.Query("SELECT Capacity, SMBIOSMemoryType, ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory");
            string? text = null;
            if (modules.Count > 0)
            {
                var sizes = modules.Select(m => (long)(m.ULong("Capacity") ?? 0)).ToList();
                var same = sizes.Distinct().Count() == 1;
                var type = modules[0].Int("SMBIOSMemoryType") switch
                {
                    34 => "DDR5", 26 => "DDR4", 24 => "DDR3", 35 => "LPDDR5", 30 => "LPDDR4", _ => ""
                };
                var speed = modules[0].Int("ConfiguredClockSpeed") is > 0 and var s ? s : modules[0].Int("Speed");
                text = (same ? $"{sizes.Count} × {Fmt.Bytes(sizes[0], 0)}" : Fmt.Bytes(sizes.Sum(), 0))
                       + (type.Length > 0 ? " " + type : "") + (speed is > 0 ? $"-{speed}" : "");
            }
            var cores = Wmi.Query("SELECT NumberOfCores FROM Win32_Processor").Sum(r => r.Int("NumberOfCores") ?? 0);
            Dispatcher.BeginInvoke(() =>
            {
                _modules = text;
                _cores = cores;
                UpdateStatic();
            });
        });
    }

    private void UpdateStatic()
    {
        var loc = Loc.Instance;
        MachineName.Text = Environment.MachineName;
        var parts = new List<string> { OsName() };
        parts.Add(loc.F("Uptime {0}", Fmt.Duration(TimeSpan.FromMilliseconds(Environment.TickCount64))));
        if (!Elevation.IsElevated) parts.Add(loc.T("Limited mode"));
        MachineLine.Text = string.Join("   ·   ", parts);
        CpuCores.Text = _cores > 0 ? $"{_cores} / {Environment.ProcessorCount}" : Environment.ProcessorCount.ToString(CultureInfo.CurrentCulture);
        RamModules.Text = _modules ?? "";
    }

    private void Tick()
    {
        var hub = SensorHub.Shared;
        var k = hub.Key;
        var loc = Loc.Instance;
        UpdateStatic();

        CpuName.Text = k.Cpu?.Name ?? CpuNameFallback();
        Set(CpuLoad, k.CpuLoad, v => v.ToString("0", CultureInfo.CurrentCulture) + "%");
        CpuBar.Value = k.CpuLoad?.Value ?? 0;
        CpuTemp.Text = Format(k.CpuTemp);
        CpuTemp.Foreground = TemperatureBrush(k.CpuTemp);
        CpuClock.Text = Format(k.CpuClock);
        CpuPower.Text = Format(k.CpuPower);
        CpuTemp.ToolTip = k.CpuTemp == null && !Elevation.IsElevated ? loc.T("Needs administrator rights") : null;
        ShowLoad(CpuChart, k.CpuLoad);

        GpuName.Text = k.Gpu?.Name ?? "—";
        Set(GpuLoad, k.GpuLoad, v => v.ToString("0", CultureInfo.CurrentCulture) + "%");
        GpuBar.Value = k.GpuLoad?.Value ?? 0;
        GpuTemp.Text = Format(k.GpuTemp);
        GpuTemp.Foreground = TemperatureBrush(k.GpuTemp);
        GpuClock.Text = Format(k.GpuClock);
        GpuPower.Text = Format(k.GpuPower);
        GpuMemory.Text = k.GpuMemoryUsed?.Value is { } used
            ? k.GpuMemoryTotal?.Value is { } total and > 0
                ? $"{used / 1024:0.0} / {total / 1024:0} {Fmt.Unit("GB")}"
                : SensorFormat.Format(SensorKind.SmallData, used)
            : "—";
        ShowLoad(GpuChart, k.GpuLoad);

        if (k.RamUsed?.Value is { } ramUsed && k.RamAvailable?.Value is { } ramFree)
        {
            var ramTotal = ramUsed + ramFree;
            RamUsed.Text = ramUsed.ToString("0.0", CultureInfo.CurrentCulture);
            RamTotal.Text = "/ " + ramTotal.ToString("0.0", CultureInfo.CurrentCulture) + " " + Fmt.Unit("GB");
            RamBar.Value = k.RamLoad?.Value ?? ramUsed / ramTotal * 100;
            RamPercent.Text = loc.F("{0}% in use", Math.Round(RamBar.Value));
        }
        RamSpark.Reading = k.RamLoad;
        RamSpark.Refresh();

        UpdateNetwork(k);
        if (DateTime.Now - _volumesAt > TimeSpan.FromSeconds(15)) UpdateVolumes();
        UpdateThermals(k);
    }

    private void UpdateNetwork(KeySensors k)
    {
        double down = 0, up = 0;
        SensorReading? busiest = null, busiestUp = null;
        DeviceNode? adapter = null;
        foreach (var nic in k.Networks)
        {
            var d = KeySensors.Pick(nic, SensorKind.Throughput, "Download Speed");
            var u = KeySensors.Pick(nic, SensorKind.Throughput, "Upload Speed");
            down += d?.Value ?? 0;
            up += u?.Value ?? 0;
            if (busiest == null || (d?.Max ?? 0) > (busiest.Max ?? 0))
            {
                busiest = d;
                busiestUp = u;
                adapter = nic;
            }
        }
        NetDown.Text = k.Networks.Count > 0 ? Fmt.Speed(down) : "—";
        NetUp.Text = k.Networks.Count > 0 ? Fmt.Speed(up) : "—";
        NetDownSpark.Reading = busiest;
        NetUpSpark.Reading = busiestUp;
        NetDownSpark.Refresh();
        NetUpSpark.Refresh();
        NetAdapter.Text = adapter == null ? "" : $"{adapter.Name}   {Ipv4Of(adapter.Name)}".Trim();
    }

    private void UpdateVolumes()
    {
        _volumesAt = DateTime.Now;
        var rows = new List<FrameworkElement>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady || d.TotalSize == 0) continue;
                var usedPct = 100.0 * (d.TotalSize - d.TotalFreeSpace) / d.TotalSize;
                var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? d.Name.TrimEnd('\\') : $"{d.Name.TrimEnd('\\')}  {d.VolumeLabel}";
                var head = new DockPanel();
                var free = new TextBlock
                {
                    Style = (Style)FindResource("Text.Caption"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    Text = Loc.Instance.F("{0} free of {1}", Fmt.Bytes(d.TotalFreeSpace), Fmt.Bytes(d.TotalSize))
                };
                DockPanel.SetDock(free, Dock.Right);
                head.Children.Add(free);
                head.Children.Add(new TextBlock { Style = (Style)FindResource("Text.Base"), Text = label });
                var bar = new ProgressBar { Maximum = 100, Value = usedPct, Margin = new Thickness(0, 6, 0, 0) };
                if (usedPct > 90) bar.Foreground = (Brush)FindResource("Brush.Danger");
                else if (usedPct > 80) bar.Foreground = (Brush)FindResource("Brush.Warning");
                var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
                stack.Children.Add(head);
                stack.Children.Add(bar);
                rows.Add(stack);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Volumes.ItemsSource = rows.Take(5).ToList();
    }

    private void UpdateThermals(KeySensors k)
    {
        if (!ReferenceEquals(_builtFor, k))
        {
            _builtFor = k;
            Thermals.Children.Clear();
            _thermals.Clear();
            var list = new List<(string Title, SensorReading Reading)>();
            if (k.CpuTemp != null) list.Add((Loc.Instance.T("Processor"), k.CpuTemp));
            if (k.GpuTemp != null) list.Add((Loc.Instance.T("Graphics"), k.GpuTemp));
            if (KeySensors.Pick(k.Gpu, SensorKind.Temperature, "GPU Hot Spot") is { } hot) list.Add((Loc.Instance.T("GPU hot spot"), hot));
            if (KeySensors.Pick(k.Gpu, SensorKind.Temperature, "GPU Memory Junction") is { } junction) list.Add((Loc.Instance.T("Video memory"), junction));
            foreach (var t in k.StorageTemps.Take(4)) list.Add((t.Device.Name, t));
            var board = SensorHub.Shared.Devices.Where(d => d.Kind == DeviceKind.Motherboard).SelectMany(d => d.AllSensors())
                .Where(s => s.Kind == SensorKind.Temperature && s.Value is > 0).Take(3);
            foreach (var t in board) list.Add((Loc.Instance.T(t.Name), t));
            foreach (var f in k.Fans.Take(6)) list.Add((Loc.Instance.T(f.Name), f));

            var s = Settings.Current;
            foreach (var (original, reading) in list)
            {
                if (s.HiddenSensors.Contains(reading.Id)) continue;
                var title = s.SensorNames.GetValueOrDefault(reading.Id) ?? original;
                var value = new TextBlock { Style = (Style)FindResource("Text.Number"), FontSize = 20, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)FindResource("Font.Display") };
                var stack = new StackPanel();
                stack.Children.Add(new TextBlock { Style = (Style)FindResource("Text.Caption"), Text = title });
                stack.Children.Add(value);
                var tile = new Border
                {
                    Style = (Style)FindResource("Panel"), Width = 178, Margin = new Thickness(0, 0, 10, 10), Child = stack,
                    // The tooltip says where the reading comes from, which matters once it has been renamed.
                    ToolTip = $"{Loc.Instance.T(reading.Name)}  ·  {reading.Device.Name}",
                    ContextMenu = new ContextMenu()
                };
                tile.ContextMenu.Opened += (_, _) => FillTileMenu(tile.ContextMenu, reading, original);
                Thermals.Children.Add(tile);
                _thermals.Add((reading, value));
            }

            var hub = SensorHub.Shared;
            ThermalsEmpty.Visibility = _thermals.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ThermalsEmpty.Text = hub.State == SensorHubState.Opening || hub.Devices.Count == 0
                ? Loc.Instance.T("Reading sensors...")
                : Elevation.IsElevated
                    ? Loc.Instance.T("This computer does not report temperatures or fan speeds.")
                    : Loc.Instance.T("Temperatures and fan speeds need administrator rights.");
        }

        foreach (var (reading, value) in _thermals)
        {
            // Without administrator rights some temperature sensors exist but read zero.
            value.Text = reading.Kind == SensorKind.Temperature && reading.Value is <= 0 ? "—" : SensorFormat.Format(reading.Kind, reading.Value);
            value.Foreground = TemperatureBrush(reading);
        }
    }

    // Normal ranges differ a lot: 90 °C is routine for a desktop processor and a problem for an SSD.
    private static (float Warn, float Danger) Limits(SensorReading r) => r.Device.Kind switch
    {
        DeviceKind.Cpu => (85, 95),
        DeviceKind.Gpu when r.Name.Contains("Hot Spot") || r.Name.Contains("Memory") => (95, 105),
        DeviceKind.Gpu => (80, 88),
        DeviceKind.Storage => (60, 70),
        _ => (75, 90)
    };

    private Brush TemperatureBrush(SensorReading? r)
    {
        if (r is not { Kind: SensorKind.Temperature, Value: > 0 and var v }) return (Brush)FindResource("Brush.Text");
        var (warn, danger) = Limits(r);
        return (Brush)FindResource(v >= danger ? "Brush.Danger" : v >= warn ? "Brush.Warning" : "Brush.Text");
    }

    private void FillTileMenu(ContextMenu menu, SensorReading reading, string original)
    {
        var loc = Loc.Instance;
        var s = Settings.Current;
        menu.Items.Clear();
        void Item(string text, Action action)
        {
            var mi = new MenuItem { Header = loc.T(text) };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }
        Item("Rename...", () => Rename(reading, original));
        Item("Hide this sensor", () =>
        {
            s.HiddenSensors.Add(reading.Id);
            Settings.Touch();
            _builtFor = null;
            Tick();
        });
        if (s.HiddenSensors.Count > 0)
            Item("Show hidden sensors", () =>
            {
                s.HiddenSensors.Clear();
                Settings.Touch();
                _builtFor = null;
                Tick();
            });
    }

    private void Rename(SensorReading reading, string original)
    {
        var s = Settings.Current;
        var box = new TextBox { Text = s.SensorNames.GetValueOrDefault(reading.Id) ?? original, MaxLength = 40 };
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        if (!ErrorDialog.Confirm(Window.GetWindow(this), Loc.Instance.T("Rename sensor"),
                Loc.Instance.T("Name shown on the overview. Leave it empty to use the original name."), Loc.Instance.T("OK"), "\uE8AC", box))
            return;
        var name = box.Text.Trim();
        if (name.Length == 0 || name == original) s.SensorNames.Remove(reading.Id);
        else s.SensorNames[reading.Id] = name;
        Settings.Touch();
        _builtFor = null;
        Tick();
    }

    private static void ShowLoad(Controls.LineChart chart, SensorReading? load)
    {
        if (load == null) chart.Series = [];
        else if (chart.Series.Count == 0 || chart.Series[0].Reading != load)
            chart.Series = [new Controls.ChartSeries(load, ThemeService.Accent, Loc.Instance.T("Load"))];
        chart.Refresh(SensorHub.Shared.Interval.TotalSeconds);
    }

    private static void Set(TextBlock target, SensorReading? reading, Func<float, string> format) =>
        target.Text = reading?.Value is { } v ? format(v) : "—";

    private static string Format(SensorReading? reading) =>
        reading?.Value is { } v && v > 0 ? SensorFormat.Format(reading.Kind, v) : "—";

    private static string? _cpuName;

    private static string CpuNameFallback() => _cpuName ??=
        (Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string)?.Trim() ?? "—";

    private static string? _osName;

    private static string OsName()
    {
        if (_osName != null) return _osName;
        const string key = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var name = Registry.GetValue(key, "ProductName", "Windows") as string ?? "Windows";
        if (int.TryParse(Registry.GetValue(key, "CurrentBuild", "0") as string, out var build) && build >= 22000)
            name = name.Replace("Windows 10", "Windows 11");
        var version = Registry.GetValue(key, "DisplayVersion", null) as string;
        return _osName = version == null ? name : $"{name} {version}";
    }

    private static string Ipv4Of(string adapterName)
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == adapterName);
            return nic?.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "";
        }
        catch (NetworkInformationException) { return ""; }
    }

    private void OnGo(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id }) (Window.GetWindow(this) as MainWindow)?.Navigate(id);
    }

    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id }) (Window.GetWindow(this) as MainWindow)?.Navigate(id);
    }
}

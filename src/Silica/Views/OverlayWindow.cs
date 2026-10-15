using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Silica.Core.Sensors;
using Silica.Localization;
using Silica.Services;

namespace Silica.Views;

/// <summary>Always-on-top panel with a few sensor values. Never takes focus.</summary>
public sealed class OverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_LAYERED = 0x80000;

    private readonly Border _panel = new();
    private readonly Grid _grid = new();
    private readonly List<(SensorReading Reading, TextBlock Value)> _rows = [];
    private string _builtFor = "";

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "Silica overlay";
        UseLayoutRounding = true;
        FlowDirection = Loc.Instance.FlowDirection;
        FontFamily = (FontFamily)FindResource("Font.UI");

        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _panel.Child = _grid;
        _panel.CornerRadius = new CornerRadius(8);
        _panel.Padding = new Thickness(14, 10, 14, 10);
        _panel.BorderThickness = new Thickness(1);
        Content = _panel;

        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        LocationChanged += (_, _) =>
        {
            Settings.Current.OverlayLeft = Left;
            Settings.Current.OverlayTop = Top;
            Settings.Touch();
        };
        ContextMenuOpening += (_, _) => ContextMenu = BuildMenu();
        ContextMenu = BuildMenu();

        var s = Settings.Current;
        if (!double.IsNaN(s.OverlayLeft) && !double.IsNaN(s.OverlayTop))
        {
            Left = s.OverlayLeft;
            Top = s.OverlayTop;
        }
        else
        {
            Left = SystemParameters.WorkArea.Right - 260;
            Top = SystemParameters.WorkArea.Top + 40;
        }
        SourceInitialized += (_, _) => ApplyStyles();
        ApplyTheme();
    }

    public void ApplyTheme()
    {
        var surface = ThemeService.ColorOf("C.Surface");
        _panel.Background = new SolidColorBrush(Color.FromArgb(235, surface.R, surface.G, surface.B));
        _panel.BorderBrush = ThemeService.BrushOf("Brush.StrokeStrong");
        _builtFor = "";
        UpdateValues();
    }

    public void ApplySettings()
    {
        var s = Settings.Current;
        Opacity = Math.Clamp(s.OverlayOpacity, 0.3, 1);
        LayoutTransform = new ScaleTransform(s.OverlayScale, s.OverlayScale);
        ApplyStyles();
        _builtFor = "";
        UpdateValues();
    }

    public void UpdateValues()
    {
        var ids = Settings.Current.OverlaySensors;
        var key = string.Join("|", ids) + Loc.Instance.Revision;
        if (key != _builtFor) Build(ids, key);
        foreach (var (reading, value) in _rows) value.Text = SensorFormat.Format(reading.Kind, reading.Value);
    }

    private void Build(List<string> ids, string key)
    {
        var readings = ids.Select(id => SensorHub.Shared.Find(id)).OfType<SensorReading>().ToList();
        if (readings.Count == 0 && ids.Count > 0 && SensorHub.Shared.State != SensorHubState.Ready) return;
        _builtFor = key;
        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        _rows.Clear();
        for (var i = 0; i < readings.Count; i++)
        {
            var r = readings[i];
            _grid.RowDefinitions.Add(new RowDefinition());
            var label = new TextBlock
            {
                Text = $"{ShortDevice(r.Device)} {Loc.Instance.T(r.Name)}",
                Foreground = ThemeService.BrushOf("Brush.Text2"),
                FontSize = 12.5,
                Margin = new Thickness(0, 2, 18, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
            var value = new TextBlock
            {
                Foreground = ThemeService.BrushOf("Brush.Text"),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                FontFamily = (FontFamily)FindResource("Font.Display"),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 2, 0, 2)
            };
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            _grid.Children.Add(label);
            _grid.Children.Add(value);
            _rows.Add((r, value));
        }
    }

    private static string ShortDevice(DeviceNode d) => d.Kind switch
    {
        DeviceKind.Cpu => "CPU",
        DeviceKind.Gpu => "GPU",
        DeviceKind.Memory => "RAM",
        DeviceKind.Storage => "SSD",
        DeviceKind.Network => "NET",
        _ => ""
    };

    private ContextMenu BuildMenu()
    {
        var loc = Loc.Instance;
        var menu = new ContextMenu();
        var through = new MenuItem { Header = loc.T("Click-through"), IsChecked = Settings.Current.OverlayClickThrough };
        through.Click += (_, _) => { Settings.Current.OverlayClickThrough = !Settings.Current.OverlayClickThrough; Settings.Touch(); };
        var open = new MenuItem { Header = loc.T("Open Silica") };
        open.Click += (_, _) => Monitoring.ShowMainWindow();
        var hide = new MenuItem { Header = loc.T("Hide overlay") };
        hide.Click += (_, _) => { Settings.Current.OverlayEnabled = false; Settings.Touch(); };
        menu.Items.Add(open);
        menu.Items.Add(through);
        menu.Items.Add(new Separator());
        menu.Items.Add(hide);
        return menu;
    }

    private void ApplyStyles()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var style = GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
        style = Settings.Current.OverlayClickThrough ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;
        SetWindowLong(hwnd, GWL_EXSTYLE, style);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}

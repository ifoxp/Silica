using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using Silica.Core.Sensors;
using Silica.Localization;
using Silica.Services;

namespace Silica.Views;

/// <summary>Creates an alert rule for one sensor.</summary>
public static class AlertDialog
{
    public static void Show(Window? owner, SensorReading reading)
    {
        var loc = Loc.Instance;
        var app = Application.Current;
        var win = new Window
        {
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Title = loc.T("New alert"),
            Background = ThemeService.BrushOf("Brush.Surface"),
            FontFamily = (FontFamily)app.FindResource("Font.UI"),
            UseLayoutRounding = true,
            FlowDirection = loc.FlowDirection
        };
        WindowChrome.SetWindowChrome(win, new WindowChrome { CaptionHeight = 0, GlassFrameThickness = new Thickness(0, 0, 0, 1), ResizeBorderThickness = new Thickness(0) });
        win.SourceInitialized += (_, _) => ThemeService.ApplyWindowFrame(win);
        win.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not TextBox) win.DragMove(); };

        var unit = SensorFormat.Unit(reading.Kind);
        var current = reading.Value is { } v ? SensorFormat.Display(reading.Kind, v) : 0;
        var suggestion = reading.Kind switch
        {
            SensorKind.Temperature => SensorFormat.Fahrenheit ? 185 : 85,
            SensorKind.Load => 95,
            SensorKind.Fan => 300,
            _ => Math.Round(current * 1.2, 1)
        };

        var root = new StackPanel { Margin = new Thickness(24, 22, 24, 20) };
        root.Children.Add(new TextBlock { Style = (Style)app.FindResource("Text.Section"), FontSize = 16, Text = loc.T("New alert") });
        root.Children.Add(new TextBlock
        {
            Style = (Style)app.FindResource("Text.Secondary"), Margin = new Thickness(0, 6, 0, 16), TextWrapping = TextWrapping.Wrap,
            Text = $"{loc.T(reading.Name)} · {reading.Device.Name} · {loc.T("now")} {SensorFormat.Format(reading.Kind, reading.Value)}"
        });

        var above = new RadioButton { Style = (Style)app.FindResource("Segment"), Content = loc.T("Above"), GroupName = "Cond", IsChecked = reading.Kind != SensorKind.Fan };
        var below = new RadioButton { Style = (Style)app.FindResource("Segment"), Content = loc.T("Below"), GroupName = "Cond", IsChecked = reading.Kind == SensorKind.Fan };
        var seg = new StackPanel { Orientation = Orientation.Horizontal };
        seg.Children.Add(above);
        seg.Children.Add(below);
        var threshold = new TextBox { Text = suggestion.ToString(CultureInfo.CurrentCulture), Width = 110, Margin = new Thickness(12, 0, 8, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Border { Style = (Style)app.FindResource("SegmentHost"), Child = seg });
        row.Children.Add(threshold);
        row.Children.Add(new TextBlock { Style = (Style)app.FindResource("Text.Secondary"), Text = unit, VerticalAlignment = VerticalAlignment.Center });
        root.Children.Add(Field(app, loc.T("Notify when the value is"), row));

        var duration = new TextBox { Text = "5", Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        var drow = new StackPanel { Orientation = Orientation.Horizontal };
        drow.Children.Add(duration);
        drow.Children.Add(new TextBlock { Style = (Style)app.FindResource("Text.Secondary"), Text = loc.T("seconds in a row"), VerticalAlignment = VerticalAlignment.Center });
        root.Children.Add(Field(app, loc.T("For at least"), drow));

        var error = new TextBlock { Style = (Style)app.FindResource("Text.Caption"), Foreground = ThemeService.BrushOf("Brush.Danger"), Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
        root.Children.Add(error);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = loc.T("Cancel"), MinWidth = 96, IsCancel = true };
        var add = new Button { Content = loc.T("Add alert"), MinWidth = 110, IsDefault = true, Margin = new Thickness(8, 0, 0, 0), Style = (Style)app.FindResource("Button.Accent") };
        add.Click += (_, _) =>
        {
            if (!double.TryParse(threshold.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
            {
                error.Text = loc.T("Enter a number for the threshold.");
                error.Visibility = Visibility.Visible;
                return;
            }
            if (!int.TryParse(duration.Text, out var d) || d < 0 || d > 3600) d = 5;
            Settings.Current.Alerts.Add(new AlertRule
            {
                SensorId = reading.Id, SensorName = $"{reading.Device.Name} / {reading.Name}",
                Above = above.IsChecked == true, Threshold = t, DurationSeconds = d
            });
            Settings.Current.AlertsEnabled = true;
            Settings.Touch();
            win.DialogResult = true;
        };
        bar.Children.Add(cancel);
        bar.Children.Add(add);
        root.Children.Add(bar);

        win.Content = new Border { BorderBrush = ThemeService.BrushOf("Brush.StrokeStrong"), BorderThickness = new Thickness(1), Child = root };
        if (win.ShowDialog() == true) (owner as MainWindow)?.ShowToast(loc.T("Alert added"));
    }

    private static FrameworkElement Field(Application app, string caption, FrameworkElement content)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        stack.Children.Add(new TextBlock { Style = (Style)app.FindResource("Text.Caption"), Text = caption, Margin = new Thickness(0, 0, 0, 6) });
        stack.Children.Add(content);
        return stack;
    }
}

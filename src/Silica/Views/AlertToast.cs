using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Silica.Services;

namespace Silica.Views;

/// <summary>Bottom-right notification that disappears on its own.</summary>
public static class AlertToast
{
    private static readonly List<Window> Open = [];

    public static void Show(string title, string text)
    {
        var win = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            ResizeMode = ResizeMode.NoResize,
            Width = 360,
            SizeToContent = SizeToContent.Height,
            FontFamily = (FontFamily)Application.Current.FindResource("Font.UI"),
            UseLayoutRounding = true,
            FlowDirection = Silica.Localization.Loc.Instance.FlowDirection
        };

        var stack = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock { Style = (Style)Application.Current.FindResource("Icon"), Text = "", Foreground = ThemeService.BrushOf("Brush.Warning"), FontSize = 15, Margin = new Thickness(0, 0, 10, 0) });
        head.Children.Add(new TextBlock { Style = (Style)Application.Current.FindResource("Text.Section"), Text = title });
        stack.Children.Add(head);
        stack.Children.Add(new TextBlock { Style = (Style)Application.Current.FindResource("Text.Secondary"), Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(25, 6, 0, 0) });
        stack.Children.Add(new TextBlock { Style = (Style)Application.Current.FindResource("Text.Caption"), Text = "Silica · " + DateTime.Now.ToString("t"), Foreground = ThemeService.BrushOf("Brush.Text3"), Margin = new Thickness(25, 8, 0, 0) });

        var panel = new Border
        {
            Child = stack,
            Margin = new Thickness(12),
            Padding = new Thickness(16, 14, 16, 14),
            CornerRadius = new CornerRadius(8),
            Background = ThemeService.BrushOf("Brush.Surface"),
            BorderBrush = ThemeService.BrushOf("Brush.StrokeStrong"),
            BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.35, Color = ThemeService.ColorOf("C.Shadow") },
            Cursor = System.Windows.Input.Cursors.Hand
        };
        panel.MouseLeftButtonUp += (_, _) => { win.Close(); Monitoring.ShowMainWindow(); };
        win.Content = panel;

        win.Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            var offset = Open.Where(w => w != win).Sum(w => w.ActualHeight);
            win.Left = area.Right - win.ActualWidth - 8;
            win.Top = area.Bottom - win.ActualHeight - 8 - offset;
            win.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        };
        win.Closed += (_, _) => Open.Remove(win);
        Open.Add(win);
        win.Show();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
        timer.Tick += (_, _) => { timer.Stop(); win.Close(); };
        timer.Start();
    }
}

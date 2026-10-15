using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using Silica.Localization;
using Silica.Services;

namespace Silica.Views;

/// <summary>Small themed message box used instead of the system one.</summary>
public static class ErrorDialog
{
    public static void Show(Window? owner, Exception ex, string? message = null) =>
        Message(owner, Loc.Instance.T("Something went wrong"),
            message ?? Loc.Instance.T("Silica ran into an unexpected problem and recovered. Details were written to the log file."),
            "", ex.Message);

    public static void Message(Window? owner, string title, string text, string icon = "", string? details = null) =>
        Build(owner, title, text, icon, details, [(Loc.Instance.T("Close"), true)]).ShowDialog();

    /// <summary>Returns true when the user picks the primary action.</summary>
    public static bool Confirm(Window? owner, string title, string text, string primary, string icon = "",
        FrameworkElement? extra = null, bool destructive = false)
    {
        var dlg = Build(owner, title, text, icon, null, [(Loc.Instance.T("Cancel"), false), (primary, true)], extra, destructive);
        return dlg.ShowDialog() == true;
    }

    private static Window Build(Window? owner, string title, string text, string icon, string? details, (string Text, bool Result)[] buttons,
        FrameworkElement? extra = null, bool destructive = false)
    {
        var win = new Window
        {
            Owner = owner is { IsVisible: true } ? owner : null,
            WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = owner is not { IsVisible: true },
            Title = title,
            Background = ThemeService.BrushOf("Brush.Surface"),
            FontFamily = (FontFamily)Application.Current.FindResource("Font.UI"),
            UseLayoutRounding = true,
            FlowDirection = Loc.Instance.FlowDirection
        };
        WindowChrome.SetWindowChrome(win, new WindowChrome { CaptionHeight = 0, GlassFrameThickness = new Thickness(0, 0, 0, 1), ResizeBorderThickness = new Thickness(0) });
        win.SourceInitialized += (_, _) => ThemeService.ApplyWindowFrame(win);
        win.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) win.DragMove(); };

        var root = new StackPanel { Margin = new Thickness(24, 22, 24, 20) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new TextBlock { Style = (Style)win.FindResource("Icon"), Text = icon, FontSize = 18, Foreground = ThemeService.BrushOf("Brush.AccentText"), Margin = new Thickness(0, 0, 12, 0) });
        head.Children.Add(new TextBlock { Style = (Style)win.FindResource("Text.Section"), Text = title, FontSize = 16 });
        root.Children.Add(head);
        root.Children.Add(new TextBlock { Style = (Style)win.FindResource("Text.Secondary"), Text = text, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None });
        if (!string.IsNullOrWhiteSpace(details))
        {
            root.Children.Add(new Border
            {
                Style = (Style)win.FindResource("Panel"),
                Margin = new Thickness(0, 12, 0, 0),
                Child = new TextBlock { Style = (Style)win.FindResource("Text.Mono"), Text = details, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Foreground = ThemeService.BrushOf("Brush.Text2") }
            });
        }

        if (extra != null)
        {
            extra.Margin = new Thickness(0, 14, 0, 0);
            root.Children.Add(extra);
        }

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        for (var i = 0; i < buttons.Length; i++)
        {
            var (caption, result) = buttons[i];
            var primary = i == buttons.Length - 1;
            var b = new Button
            {
                Content = caption,
                MinWidth = 96,
                Margin = new Thickness(8, 0, 0, 0),
                // Enter must not start something that cannot be undone.
                IsDefault = primary && !destructive,
                IsCancel = !primary || buttons.Length == 1,
                Style = primary ? (Style)win.FindResource(destructive ? "Button.Danger" : "Button.Accent") : (Style)win.FindResource(typeof(Button))
            };
            b.Click += (_, _) => { win.DialogResult = result; };
            bar.Children.Add(b);
        }
        root.Children.Add(bar);
        win.Content = new Border { BorderBrush = ThemeService.BrushOf("Brush.StrokeStrong"), BorderThickness = new Thickness(1), Child = root };
        return win;
    }
}

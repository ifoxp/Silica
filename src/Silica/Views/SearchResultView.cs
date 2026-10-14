using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Silica.Localization;
using Silica.Services;

namespace Silica.Views;

/// <summary>Visual for one row of the search popup.</summary>
public static class SearchResultView
{
    public static FrameworkElement Create(SearchHit hit)
    {
        var app = Application.Current;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconBox = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(7), HorizontalAlignment = HorizontalAlignment.Left,
            Background = hit.Kind == SearchKind.Page ? ThemeService.BrushOf("Brush.AccentSubtle") : ThemeService.BrushOf("Brush.SurfaceAlt"),
            Child = new TextBlock
            {
                Style = (Style)app.FindResource("Icon"), Text = hit.Icon, FontSize = 14,
                Foreground = hit.Kind == SearchKind.Page ? ThemeService.BrushOf("Brush.AccentText") : ThemeService.BrushOf("Brush.Text2")
            }
        };
        grid.Children.Add(iconBox);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Style = (Style)app.FindResource("Text.Base"), Text = hit.Title });
        if (hit.Subtitle.Length > 0)
            text.Children.Add(new TextBlock { Style = (Style)app.FindResource("Text.Caption"), Text = hit.Subtitle, Margin = new Thickness(0, 1, 0, 0) });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var kind = new TextBlock
        {
            Style = (Style)app.FindResource("Text.Caption"),
            Foreground = ThemeService.BrushOf("Brush.Text3"),
            Text = Loc.Instance.T(hit.Kind switch
            {
                SearchKind.Page => "Page",
                SearchKind.Setting => "Setting",
                SearchKind.Sensor => "Sensor",
                _ => "Value"
            }),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 4, 0)
        };
        Grid.SetColumn(kind, 2);
        grid.Children.Add(kind);

        var border = new Border { Child = grid, Padding = new Thickness(8, 7, 8, 7), CornerRadius = new CornerRadius(7), Tag = hit, Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent };
        border.MouseEnter += (_, _) => { if (!IsSelected(border)) border.Background = ThemeService.BrushOf("Brush.Hover"); };
        border.MouseLeave += (_, _) => { if (!IsSelected(border)) border.Background = Brushes.Transparent; };
        border.Loaded += (_, _) =>
        {
            var item = FindItem(border);
            if (item == null) return;
            item.Selected += (_, _) => border.Background = ThemeService.BrushOf("Brush.Selection");
            item.Unselected += (_, _) => border.Background = Brushes.Transparent;
            if (item.IsSelected) border.Background = ThemeService.BrushOf("Brush.Selection");
        };
        return border;
    }

    private static bool IsSelected(FrameworkElement e) => FindItem(e)?.IsSelected == true;

    private static ListBoxItem? FindItem(DependencyObject d)
    {
        while (d != null && d is not ListBoxItem) d = VisualTreeHelper.GetParent(d);
        return d as ListBoxItem;
    }
}

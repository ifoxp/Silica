using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using Silica.Controls;
using Silica.Localization;
using Silica.Services;
using Silica.Core.Info;
using Silica.Core.Model;
using Silica.Core.Report;

namespace Silica.Views;

public partial class InfoPageView : UserControl, INavigable
{
    private const double TwoColumnWidth = 1060;

    private readonly IInfoProvider _provider;
    private InfoPage? _page;
    private bool _loading;
    private bool _twoColumns;
    private string? _pendingTarget;
    private readonly List<(FrameworkElement Card, List<(FrameworkElement Row, InfoProperty Item)> Rows)> _propertyCards = [];
    private readonly List<(FrameworkElement Card, DataGrid Grid, TableBlock Block)> _tables = [];

    public InfoPageView(IInfoProvider provider)
    {
        _provider = provider;
        InitializeComponent();
        PageIcon.Text = provider.Icon;
        UpdateTexts();
        Loc.Instance.LanguageChanged += () =>
        {
            UpdateTexts();
            if (_page == null) return;
            if (IsVisible) _ = Load(true);
            else _page = null;
        };
        SizeChanged += (_, _) =>
        {
            var two = ActualWidth >= TwoColumnWidth;
            if (two != _twoColumns && _page != null) Render(_page);
            UpdateTableHeights();
        };
    }

    public void OnNavigatedTo(string? target)
    {
        _pendingTarget = target;
        if (_page == null && !_loading) _ = Load(false);
        else if (_page != null && target != null) Highlight(target);
    }

    public void OnNavigatedFrom() { }

    public void Refresh() => _ = Load(true);

    private void UpdateTexts()
    {
        PageTitle.Text = Loc.Instance.T(_provider.Title);
        UpdateSubtitle();
    }

    private void UpdateSubtitle()
    {
        PageSubtitle.Text = _page == null
            ? Loc.Instance.T("Reading data...")
            : Loc.Instance.F("Updated at {0}", _page.CollectedAt.ToString("t", System.Globalization.CultureInfo.CurrentCulture));
    }

    private async Task Load(bool refresh)
    {
        _loading = true;
        RefreshButton.IsEnabled = false;
        if (_page == null) ShowSkeleton();
        try
        {
            var page = await PageStore.GetAsync(_provider, refresh);
            _page = page;
            Render(page);
            if (_pendingTarget != null) Highlight(_pendingTarget);
        }
        finally
        {
            _loading = false;
            RefreshButton.IsEnabled = true;
            UpdateSubtitle();
        }
    }

    private void ShowSkeleton()
    {
        Body.Children.Clear();
        var pulse = new DoubleAnimation(0.35, 0.8, TimeSpan.FromMilliseconds(800)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        for (var c = 0; c < 2; c++)
        {
            var stack = new StackPanel();
            stack.Children.Add(Bar(160, 14, new Thickness(0, 0, 0, 18)));
            for (var i = 0; i < 6; i++)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.4, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.6, GridUnitType.Star) });
                row.Children.Add(Bar(90 + i * 13 % 60, 10, default));
                var v = Bar(140 + i * 37 % 120, 10, default);
                Grid.SetColumn(v, 1);
                row.Children.Add(v);
                stack.Children.Add(row);
            }
            var card = new Border { Style = (Style)FindResource("Card"), Child = stack, Margin = new Thickness(0, 0, 0, 16) };
            card.BeginAnimation(OpacityProperty, pulse);
            Body.Children.Add(card);
        }

        Border Bar(double w, double h, Thickness m) => new()
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(h / 2), Margin = m,
            HorizontalAlignment = HorizontalAlignment.Left, Background = (Brush)FindResource("Brush.Track")
        };
    }

    private void Render(InfoPage page)
    {
        _twoColumns = ActualWidth >= TwoColumnWidth;
        Body.Children.Clear();
        _propertyCards.Clear();
        _tables.Clear();

        if (page.Warnings.Count > 0) Body.Children.Add(WarningPanel(page.Warnings));

        if (page.Blocks.Count == 0)
        {
            Body.Children.Add(EmptyState());
            return;
        }

        // Consecutive property cards flow into two balanced columns; tables always take the full width.
        var run = new List<PropertyBlock>();
        foreach (var block in page.Blocks)
        {
            if (block is PropertyBlock pb) { run.Add(pb); continue; }
            FlushRun(run);
            if (block is TableBlock tb) Body.Children.Add(TableCard(tb));
        }
        FlushRun(run);
        ApplyFilter();
        UpdateTableHeights();
    }

    private void FlushRun(List<PropertyBlock> run)
    {
        if (run.Count == 0) return;
        if (!_twoColumns || run.Count == 1)
        {
            foreach (var b in run) Body.Children.Add(PropertyCard(b));
        }
        else
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var left = new StackPanel();
            var right = new StackPanel();
            Grid.SetColumn(right, 2);
            grid.Children.Add(left);
            grid.Children.Add(right);
            double hl = 0, hr = 0;
            foreach (var b in run)
            {
                var weight = b.Items.Count + 2.5;
                if (hl <= hr) { left.Children.Add(PropertyCard(b)); hl += weight; }
                else { right.Children.Add(PropertyCard(b)); hr += weight; }
            }
            Body.Children.Add(grid);
        }
        run.Clear();
    }

    private FrameworkElement PropertyCard(PropertyBlock block)
    {
        var loc = Loc.Instance;
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Text.Section"),
            Text = block.LocalizeTitle ? loc.T(block.Title) : block.Title,
            Margin = new Thickness(0, 0, 0, 8)
        });

        var rows = new List<(FrameworkElement, InfoProperty)>();
        foreach (var item in block.Items)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.42, GridUnitType.Star), MinWidth = 120 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.58, GridUnitType.Star) });
            grid.Children.Add(new TextBlock { Style = (Style)FindResource("PropLabel"), Text = loc.T(item.Label) });
            var value = new TextBlock { Style = (Style)FindResource("PropValue"), Text = item.LocalizeValue ? loc.T(item.Value) : item.Value };
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);

            var row = new Border
            {
                Child = grid,
                Padding = new Thickness(8, 0, 8, 0),
                Margin = new Thickness(-8, 0, -8, 0),
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = item,
                ToolTip = loc.T("Click to copy"),
                ContextMenu = RowMenu(item)
            };
            row.MouseEnter += (_, _) => row.Background = (Brush)FindResource("Brush.Hover");
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, _) => Copy(value.Text);
            stack.Children.Add(row);
            rows.Add((row, item));
        }

        var card = new Border { Style = (Style)FindResource("Card"), Child = stack, Margin = new Thickness(0, 0, 0, 16) };
        _propertyCards.Add((card, rows));
        return card;
    }

    private ContextMenu RowMenu(InfoProperty item)
    {
        var loc = Loc.Instance;
        var label = loc.T(item.Label);
        var value = item.LocalizeValue ? loc.T(item.Value) : item.Value;
        var menu = new ContextMenu();
        var copyValue = new MenuItem { Header = loc.T("Copy value") };
        copyValue.Click += (_, _) => Copy(value);
        var copyLine = new MenuItem { Header = loc.T("Copy line") };
        copyLine.Click += (_, _) => Copy($"{label}: {value}");
        menu.Items.Add(copyValue);
        menu.Items.Add(copyLine);
        return menu;
    }

    private FrameworkElement TableCard(TableBlock block)
    {
        var loc = Loc.Instance;
        var stack = new DockPanel { LastChildFill = true };

        var header = new DockPanel { Margin = new Thickness(18, 14, 18, 6), LastChildFill = false };
        header.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Text.Section"),
            Text = block.LocalizeTitle ? loc.T(block.Title) : block.Title,
            VerticalAlignment = VerticalAlignment.Center
        });
        var count = new Border
        {
            Background = (Brush)FindResource("Brush.Track"), CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1, 8, 2),
            Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Style = (Style)FindResource("Text.Caption"), Text = block.Rows.Count.ToString(System.Globalization.CultureInfo.CurrentCulture) }
        };
        header.Children.Add(count);
        DockPanel.SetDock(header, Dock.Top);
        stack.Children.Add(header);

        var grid = new DataGrid { Margin = new Thickness(6, 0, 6, 8), IsSynchronizedWithCurrentItem = false };
        for (var i = 0; i < block.Columns.Length; i++)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = loc.T(block.Columns[i]),
                Binding = new Binding($"[{i}]"),
                SortMemberPath = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Width = ColumnWidth(block, i),
                ElementStyle = (Style)FindResource("CellText")
            });
        }
        var rows = block.Rows.Select(r => r.Select((cell, i) => ValueVocabulary.Translate(cell, i < block.Columns.Length ? block.Columns[i] : "")).ToArray()).ToList();
        var view = new ListCollectionView(rows);
        grid.ItemsSource = view;
        grid.Sorting += (_, e) =>
        {
            e.Handled = true;
            var dir = e.Column.SortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
            foreach (var c in grid.Columns) c.SortDirection = null;
            e.Column.SortDirection = dir;
            view.CustomSort = new SmartComparer(int.Parse(e.Column.SortMemberPath, System.Globalization.CultureInfo.InvariantCulture), dir);
        };
        grid.PreviewMouseWheel += (_, e) =>
        {
            // Let the page scroll when the table is already at its end.
            var sv = FindScrollViewer(grid);
            if (sv == null) return;
            if ((e.Delta > 0 && sv.VerticalOffset <= 0) || (e.Delta < 0 && sv.VerticalOffset >= sv.ScrollableHeight))
            {
                e.Handled = true;
                Scroller.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent });
            }
        };
        stack.Children.Add(grid);

        var card = new Border { Style = (Style)FindResource("Card"), Child = stack, Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 16) };
        _tables.Add((card, grid, block));
        return card;
    }

    private static DataGridLength ColumnWidth(TableBlock block, int column)
    {
        // Typical (not longest) content decides the share, so one long path does not squeeze every other column.
        var lengths = block.Rows.Take(300).Select(r => column < r.Length ? r[column].Length : 0).Order().ToList();
        var typical = lengths.Count == 0 ? 0 : lengths[(int)(lengths.Count * 0.85)];
        var len = Math.Max(typical, Loc.Instance.T(block.Columns[column]).Length + 2);
        return new DataGridLength(Math.Clamp(len / 14.0, 0.55, 2.6), DataGridLengthUnitType.Star);
    }

    private void UpdateTableHeights()
    {
        // Tables scroll inside themselves so row virtualization keeps big lists fast.
        var max = Math.Max(320, Scroller.ActualHeight - 90);
        foreach (var (_, grid, _) in _tables) grid.MaxHeight = max;
    }

    private FrameworkElement WarningPanel(IEnumerable<string> warnings)
    {
        var stack = new StackPanel();
        foreach (var w in warnings)
        {
            var colon = w.IndexOf(": ", StringComparison.Ordinal);
            var text = colon > 0 ? Loc.Instance.T(w[..colon]) + w[colon..] : Loc.Instance.T(w);
            stack.Children.Add(new TextBlock { Style = (Style)FindResource("Text.Base"), Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) });
        }
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new TextBlock { Style = (Style)FindResource("Icon"), Text = "", Foreground = (Brush)FindResource("Brush.Warning"), Margin = new Thickness(0, 1, 12, 0), VerticalAlignment = VerticalAlignment.Top });
        Grid.SetColumn(stack, 1);
        grid.Children.Add(stack);
        return new Border
        {
            Child = grid, Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 16), CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource("Brush.WarningSubtle")
        };
    }

    private FrameworkElement EmptyState()
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 80, 0, 0) };
        stack.Children.Add(new TextBlock { Style = (Style)FindResource("Icon"), Text = "", FontSize = 34, Foreground = (Brush)FindResource("Brush.Text3") });
        stack.Children.Add(new TextBlock { Style = (Style)FindResource("Text.Section"), Text = Loc.Instance.T("Nothing to show"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 4) });
        stack.Children.Add(new TextBlock { Style = (Style)FindResource("Text.Secondary"), Text = Loc.Instance.T("Windows did not report any data for this page."), HorizontalAlignment = HorizontalAlignment.Center });
        return stack;
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var q = TextMatch.Normalize(FilterBox.Text);
        foreach (var (card, rows) in _propertyCards)
        {
            var any = false;
            foreach (var (row, item) in rows)
            {
                var visible = q.Length == 0 || Matches(q, Loc.Instance.T(item.Label), item.Label, item.Value);
                row.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                any |= visible;
            }
            card.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var (card, grid, _) in _tables)
        {
            var view = (ListCollectionView)grid.ItemsSource;
            view.Filter = q.Length == 0 ? null : o => o is string[] cells && Matches(q, cells);
            card.Visibility = view.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static bool Matches(string q, params string[] texts) =>
        texts.Any(t => TextMatch.Normalize(t).Contains(q, StringComparison.Ordinal));

    private void Highlight(string target)
    {
        _pendingTarget = null;
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var (_, rows) in _propertyCards)
                foreach (var (row, item) in rows)
                    if (item.Label == target)
                    {
                        Flash(row);
                        return;
                    }
            foreach (var (_, grid, _) in _tables)
            {
                var hit = grid.Items.OfType<string[]>().FirstOrDefault(r => r.Contains(target));
                if (hit == null) continue;
                grid.BringIntoView();
                grid.ScrollIntoView(hit);
                grid.SelectedItem = hit;
                return;
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static void Flash(Border row)
    {
        row.BringIntoView();
        var accent = ThemeService.Accent;
        var brush = new SolidColorBrush(Color.FromArgb(0, accent.R, accent.G, accent.B));
        row.Background = brush;
        var anim = new ColorAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1800) };
        anim.KeyFrames.Add(new LinearColorKeyFrame(Color.FromArgb(90, accent.R, accent.G, accent.B), KeyTime.FromPercent(0.12)));
        anim.KeyFrames.Add(new LinearColorKeyFrame(Color.FromArgb(90, accent.R, accent.G, accent.B), KeyTime.FromPercent(0.6)));
        anim.KeyFrames.Add(new LinearColorKeyFrame(Color.FromArgb(0, accent.R, accent.G, accent.B), KeyTime.FromPercent(1)));
        brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    private static void Flash(FrameworkElement element)
    {
        if (element is Border b) Flash(b);
    }

    private void Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            (Window.GetWindow(this) as MainWindow)?.ShowToast(Loc.Instance.T("Copied"));
        }
        catch (Exception ex)
        {
            Log.Warn($"Clipboard: {ex.Message}");
        }
    }

    private void OnCopyAll(object sender, RoutedEventArgs e)
    {
        if (_page != null) Copy(ReportExporter.ToText([_page], Reports.Meta(), Loc.Instance.T));
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (_page == null) return;
        var name = Reports.SafeFileName(Loc.Instance.T(_provider.Title));
        Reports.SaveWithDialog([_page], name, Window.GetWindow(this));
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    private static ScrollViewer? FindScrollViewer(DependencyObject d)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var child = VisualTreeHelper.GetChild(d, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }
}

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Silica.Core.Bench;
using Silica.Core.Util;
using Silica.Localization;
using Silica.Services;

namespace Silica.Views;

public sealed class DiskBenchView : UserControl, INavigable
{
    private static readonly (string Read, string Write, string Title, string Hint)[] Rows =
    [
        ("seq-read", "seq-write", "Sequential", "1 MiB, queue 8"),
        ("rnd-read-q32", "rnd-write-q32", "Random, deep queue", "4 KiB, queue 32, 4 threads"),
        ("rnd-read", "rnd-write", "Random, single request", "4 KiB, queue 1")
    ];

    private readonly ComboBox _drive = new() { MinWidth = 260 };
    private readonly ComboBox _size = new() { MinWidth = 110 };
    private readonly Button _start = new();
    private readonly TextBlock _startText = new();
    private readonly TextBlock _startIcon = new();
    private readonly ProgressBar _progress = new() { Maximum = 1, Margin = new Thickness(0, 16, 0, 0), Visibility = Visibility.Hidden };
    private readonly TextBlock _status = new();
    private readonly Dictionary<string, (TextBlock Value, TextBlock Detail, Border Tile)> _tiles = [];
    private readonly BenchHistory _history = BenchHistory.Load();
    private CancellationTokenSource? _cts;

    public DiskBenchView()
    {
        Content = BuildLayout();
        _drive.SelectionChanged += (_, _) => { if (_cts == null) ShowLast(); };
        Loc.Instance.LanguageChanged += () => { Content = BuildLayout(); OnNavigatedTo(null); };
    }

    public void OnNavigatedTo(string? target)
    {
        if (_cts != null) return;
        var selected = (_drive.SelectedItem as ComboBoxItem)?.Tag as string;
        _drive.Items.Clear();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? d.Name : $"{d.Name}  {d.VolumeLabel}";
                _drive.Items.Add(new ComboBoxItem
                {
                    Content = $"{label}   ·   {Loc.Instance.F("{0} free", Fmt.Bytes(d.AvailableFreeSpace))}",
                    Tag = d.RootDirectory.FullName
                });
            }
            catch (IOException) { }
        }
        _drive.SelectedItem = _drive.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == selected) ?? (_drive.Items.Count > 0 ? _drive.Items[0] : null);
        ShowLast();
    }

    public void OnNavigatedFrom() { }

    private FrameworkElement BuildLayout()
    {
        var loc = Loc.Instance;
        var app = Application.Current;
        _tiles.Clear();

        var header = new Grid { Margin = new Thickness(28, 22, 28, 14) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.Children.Add(new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(8), Background = (Brush)app.FindResource("Brush.AccentSubtle"), Margin = new Thickness(0, 0, 14, 0),
            Child = new TextBlock { Style = (Style)app.FindResource("Icon"), Text = "", FontSize = 18, Foreground = (Brush)app.FindResource("Brush.AccentText") }
        });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Style = (Style)app.FindResource("Text.Title"), Text = loc.T("Disk benchmark") });
        titles.Children.Add(new TextBlock { Style = (Style)app.FindResource("Text.Caption"), Text = loc.T("Measures real drive speed with the Windows cache bypassed"), Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);

        _size.Items.Clear();
        foreach (var bytes in new[] { 256L << 20, 1L << 30, 4L << 30 })
            _size.Items.Add(new ComboBoxItem { Content = Fmt.Bytes(bytes, 0), Tag = bytes });
        _size.SelectedIndex = 1;

        _startIcon.Style = (Style)app.FindResource("Icon");
        _startIcon.FontSize = 12;
        _startIcon.Foreground = (Brush)app.FindResource("Brush.OnAccent");
        _startIcon.Margin = new Thickness(0, 0, 8, 0);
        var startContent = new StackPanel { Orientation = Orientation.Horizontal };
        startContent.Children.Add(Take(_startIcon));
        startContent.Children.Add(Take(_startText));
        _start.Content = startContent;
        _start.Style = (Style)app.FindResource("Button.Accent");
        _start.MinWidth = 120;
        _start.Click -= OnStart;
        _start.Click += OnStart;
        SetIdleButton();

        var controls = new WrapPanel { Margin = new Thickness(0, 0, 0, 0) };
        controls.Children.Add(Labeled(loc.T("Drive"), _drive));
        controls.Children.Add(Labeled(loc.T("Test file"), _size));
        var startHost = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        startHost.Children.Add(Take(_start));
        controls.Children.Add(startHost);

        _status.Style = (Style)app.FindResource("Text.Caption");
        _status.Margin = new Thickness(0, 10, 0, 0);
        _status.TextWrapping = TextWrapping.Wrap;
        var setup = new StackPanel();
        setup.Children.Add(controls);
        setup.Children.Add(Take(_progress));
        setup.Children.Add(Take(_status));
        var setupCard = new Border { Style = (Style)app.FindResource("Card"), Child = setup, Margin = new Thickness(0, 0, 0, 16) };

        var table = new Grid();
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        table.ColumnDefinitions.Add(new ColumnDefinition());
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        table.ColumnDefinitions.Add(new ColumnDefinition());
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddCell(table, new TextBlock { Style = (Style)app.FindResource("Text.Overline"), Text = loc.T("Read"), Margin = new Thickness(2, 0, 0, 8) }, 0, 2);
        AddCell(table, new TextBlock { Style = (Style)app.FindResource("Text.Overline"), Text = loc.T("Write"), Margin = new Thickness(2, 0, 0, 8) }, 0, 4);

        for (var i = 0; i < Rows.Length; i++)
        {
            var (read, write, title, hint) = Rows[i];
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(new TextBlock { Style = (Style)app.FindResource("Text.Section"), Text = loc.T(title) });
            name.Children.Add(new TextBlock { Style = (Style)app.FindResource("Text.Caption"), Text = loc.T(hint), Margin = new Thickness(0, 2, 0, 0) });
            AddCell(table, name, i + 1, 0);
            AddCell(table, Tile(read), i + 1, 2);
            AddCell(table, Tile(write), i + 1, 4);
        }

        var body = new StackPanel { Margin = new Thickness(28, 0, 28, 28) };
        body.Children.Add(setupCard);
        body.Children.Add(table);
        body.Children.Add(new TextBlock
        {
            Style = (Style)app.FindResource("Text.Caption"), Margin = new Thickness(2, 6, 0, 0), TextWrapping = TextWrapping.Wrap,
            Text = loc.T("The test writes a temporary file and deletes it afterwards. MB/s here means 1,000,000 bytes per second, like drive makers use.")
        });

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(header);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body, Focusable = false };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        return root;
    }

    /// <summary>The controls are reused when the layout is rebuilt for another language.</summary>
    private static T Take<T>(T element) where T : FrameworkElement
    {
        if (element.Parent is Panel p) p.Children.Remove(element);
        return element;
    }

    private static FrameworkElement Labeled(string caption, FrameworkElement control)
    {
        if (control.Parent is Panel old) old.Children.Remove(control);
        var stack = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        stack.Children.Add(new TextBlock { Style = (Style)Application.Current.FindResource("Text.Caption"), Text = caption, Margin = new Thickness(0, 0, 0, 6) });
        stack.Children.Add(control);
        return stack;
    }

    private static void AddCell(Grid grid, FrameworkElement e, int row, int column)
    {
        Grid.SetRow(e, row);
        Grid.SetColumn(e, column);
        grid.Children.Add(e);
    }

    private Border Tile(string testId)
    {
        var app = Application.Current;
        var value = new TextBlock { Style = (Style)app.FindResource("Text.Metric"), Text = "—" };
        var unit = new TextBlock { Style = (Style)app.FindResource("Text.Secondary"), Text = Fmt.Unit("MB") + Fmt.Unit("/s"), Margin = new Thickness(6, 0, 0, 5), VerticalAlignment = VerticalAlignment.Bottom };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(value);
        row.Children.Add(unit);
        var detail = new TextBlock { Style = (Style)app.FindResource("Text.Caption"), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None };
        var stack = new StackPanel();
        stack.Children.Add(row);
        stack.Children.Add(detail);
        var tile = new Border { Style = (Style)app.FindResource("Card"), Child = stack, Margin = new Thickness(0, 0, 0, 16) };
        _tiles[testId] = (value, detail, tile);
        return tile;
    }

    private void SetIdleButton()
    {
        _startIcon.Text = "";
        _startText.Text = Loc.Instance.T("Start");
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_cts != null)
        {
            _cts.Cancel();
            return;
        }
        if ((_drive.SelectedItem as ComboBoxItem)?.Tag is not string root) return;
        var size = (_size.SelectedItem as ComboBoxItem)?.Tag is long l ? l : 1L << 30;

        _cts = new CancellationTokenSource();
        _startIcon.Text = "";
        _startText.Text = Loc.Instance.T("Stop");
        _drive.IsEnabled = _size.IsEnabled = false;
        _progress.Visibility = Visibility.Visible;
        _progress.Value = 0;
        _status.Text = Loc.Instance.T("Preparing the test file...");
        foreach (var (v, d, t) in _tiles.Values) { v.Text = "—"; d.Text = ""; t.ClearValue(Border.BorderBrushProperty); }

        var progress = new Progress<(double Fraction, DiskBenchStep? Completed)>(p =>
        {
            _progress.Value = p.Fraction;
            if (p.Completed is { } step) ShowStep(step, true);
            else _status.Text = Loc.Instance.T("Testing, avoid using this drive meanwhile...");
        });

        try
        {
            var token = _cts.Token;
            var result = await Task.Run(() => new DiskBenchmark().Run(new DiskBenchOptions(root, size), token, progress), token);
            _history.Add(result);
            _history.Save();
            _status.Text = Loc.Instance.F("Finished at {0}", DateTime.Now.ToString("t", CultureInfo.CurrentCulture));
        }
        catch (OperationCanceledException)
        {
            _status.Text = Loc.Instance.T("Stopped. The temporary file was removed.");
        }
        catch (Exception ex)
        {
            Log.Error("Disk benchmark failed", ex);
            _status.Text = Loc.Instance.T(ex.Message);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetIdleButton();
            _drive.IsEnabled = _size.IsEnabled = true;
            _progress.Visibility = Visibility.Hidden;
            foreach (var (_, _, t) in _tiles.Values) t.ClearValue(Border.BorderBrushProperty);
        }
    }

    private void ShowStep(DiskBenchStep step, bool highlight)
    {
        if (!_tiles.TryGetValue(step.TestId, out var tile)) return;
        tile.Value.Text = (step.BytesPerSecond / 1e6).ToString(step.BytesPerSecond >= 1e8 ? "N0" : "N1", CultureInfo.CurrentCulture);
        var parts = new List<string>();
        if (step.Iops is { } iops) parts.Add(Loc.Instance.F("{0} IOPS", iops.ToString("N0", CultureInfo.CurrentCulture)));
        if (step.LatencyUs is { } lat) parts.Add(Loc.Instance.F("{0} µs average", lat.ToString(lat >= 100 ? "N0" : "N1", CultureInfo.CurrentCulture)));
        tile.Detail.Text = string.Join("   ·   ", parts);
        if (highlight) tile.Tile.BorderBrush = (Brush)Application.Current.FindResource("Brush.Accent");
    }

    private void ShowLast()
    {
        if ((_drive.SelectedItem as ComboBoxItem)?.Tag is not string root) return;
        var last = _history.DiskResults.Where(r => string.Equals(Path.GetPathRoot(r.Directory), root, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.When).FirstOrDefault();
        foreach (var (v, d, _) in _tiles.Values) { v.Text = "—"; d.Text = ""; }
        if (last == null)
        {
            _status.Text = Loc.Instance.T("Choose a drive and press Start. The full run takes about half a minute.");
            return;
        }
        foreach (var step in last.Steps) ShowStep(step, false);
        _status.Text = Loc.Instance.F("Last run: {0}", last.When.ToString("g", CultureInfo.CurrentCulture));
    }
}

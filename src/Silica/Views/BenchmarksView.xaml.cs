using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Silica.Core.Bench;
using Silica.Localization;
using Silica.Services;

namespace Silica.Views;

public partial class BenchmarksView : UserControl, INavigable
{
    private readonly BenchHistory _history = BenchHistory.Load();
    private readonly Dictionary<string, BenchCard> _cards = [];
    private List<BenchReference> _references = BenchReferences.Load();
    private CancellationTokenSource? _cts;
    private bool _running;

    public BenchmarksView()
    {
        InitializeComponent();
        Build();
        Loc.Instance.LanguageChanged += Build;
        OneThread.Checked += (_, _) => RefreshCards();
        AllThreads.Checked += (_, _) => RefreshCards();
    }

    private int Threads => OneThread.IsChecked == true ? 1 : Environment.ProcessorCount;

    public void OnNavigatedTo(string? target) => RefreshCards();
    public void OnNavigatedFrom() { }

    private void Build()
    {
        Body.Children.Clear();
        _cards.Clear();
        Subtitle.Text = Loc.Instance.F("{0} · {1}", BenchReferences.ProcessorName(),
            Loc.Instance.N(Environment.ProcessorCount, "{0} threads"));

        foreach (var group in Benchmarks.All.GroupBy(b => b.Kind))
        {
            Body.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("Text.Overline"),
                Text = Loc.Instance.T(group.Key switch
                {
                    BenchKind.Memory => "Memory",
                    BenchKind.Cpu => "Processor",
                    _ => "Floating point"
                }).ToUpper(CultureInfo.CurrentUICulture),
                Margin = new Thickness(2, Body.Children.Count == 0 ? 0 : 10, 0, 10)
            });
            var grid = new UniformGrid2(2, 16);
            foreach (var bench in group)
            {
                var card = new BenchCard(this, bench);
                _cards[bench.Id] = card;
                grid.Add(card.Root);
            }
            Body.Children.Add(grid.Root);
        }
        RefreshCards();
    }

    private void RefreshCards()
    {
        foreach (var card in _cards.Values) card.Show(_history, _references, Threads, _running);
    }

    public void RunOne(IBenchmark bench)
    {
        if (_running) { _cts?.Cancel(); return; }
        _ = RunQueue([bench]);
    }

    private void OnRunAll(object sender, RoutedEventArgs e)
    {
        if (_running) { _cts?.Cancel(); return; }
        _ = RunQueue(Benchmarks.All.ToList());
    }

    private async Task RunQueue(List<IBenchmark> queue)
    {
        _running = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var threads = Threads;
        RunAllIcon.Text = "";
        RunAllText.Text = Loc.Instance.T("Stop");
        RefreshCards();

        foreach (var bench in queue)
        {
            if (token.IsCancellationRequested) break;
            var card = _cards[bench.Id];
            card.SetRunning(true, 0);
            var progress = new Progress<double>(p => card.SetRunning(true, p));
            try
            {
                var effective = bench.Id == "mem-latency" ? 1 : threads;
                var result = await Task.Factory.StartNew(() => bench.Run(new BenchContext(effective, token, progress)),
                    token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                if (!token.IsCancellationRequested)
                {
                    _history.Add(result);
                    _history.Save();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Error($"Benchmark {bench.Id} failed", ex);
                ErrorDialog.Show(Window.GetWindow(this), ex, Loc.Instance.T("The benchmark stopped because of an error."));
                break;
            }
            finally
            {
                card.SetRunning(false, 0);
            }
            card.Show(_history, _references, threads, true);
        }

        _running = false;
        RunAllIcon.Text = "";
        RunAllText.Text = Loc.Instance.T("Run all");
        RefreshCards();
    }

    private void OnMore(object sender, RoutedEventArgs e)
    {
        var loc = Loc.Instance;
        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };

        var export = new MenuItem { Header = loc.T("Export my results..."), IsEnabled = _history.Results.Count > 0 };
        export.Click += (_, _) =>
        {
            var dlg = new SaveFileDialog { FileName = Reports.SafeFileName($"Silica benchmarks {Environment.MachineName}"), Filter = "Silica (*.json)|*.json" };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            BenchReferences.Export(BenchReferences.FromHistory(_history), dlg.FileName);
            (Window.GetWindow(this) as MainWindow)?.ShowToast(loc.T("Results exported"));
        };
        var import = new MenuItem { Header = loc.T("Compare with another computer...") };
        import.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = "Silica (*.json)|*.json" };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                BenchReferences.Import(dlg.FileName);
                _references = BenchReferences.Load();
                RefreshCards();
            }
            catch (Exception ex)
            {
                ErrorDialog.Message(Window.GetWindow(this), loc.T("File not imported"), loc.T("This file does not contain Silica benchmark results."), details: ex.Message);
            }
        };
        menu.Items.Add(export);
        menu.Items.Add(import);

        if (_references.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var r in _references)
            {
                var item = new MenuItem { Header = loc.F("Remove {0}", $"{r.Computer} ({r.Processor})") };
                var reference = r;
                item.Click += (_, _) =>
                {
                    BenchReferences.Remove(reference);
                    _references = BenchReferences.Load();
                    RefreshCards();
                };
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new Separator());
        var clear = new MenuItem { Header = loc.T("Clear my results"), IsEnabled = _history.Results.Count > 0 && !_running };
        clear.Click += (_, _) =>
        {
            if (!ErrorDialog.Confirm(Window.GetWindow(this), loc.T("Clear results?"), loc.T("All saved benchmark results of this computer will be deleted."), loc.T("Clear"))) return;
            _history.Clear();
            _history.Save();
            RefreshCards();
        };
        menu.Items.Add(clear);
        menu.IsOpen = true;
    }

    /// <summary>Two equal columns of cards that fall back to one on narrow windows.</summary>
    private sealed class UniformGrid2
    {
        private readonly int _gap;
        public Grid Root { get; } = new();
        private int _count;

        public UniformGrid2(int columns, int gap)
        {
            _gap = gap;
            Root.ColumnDefinitions.Add(new ColumnDefinition());
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(gap) });
            Root.ColumnDefinitions.Add(new ColumnDefinition());
        }

        public void Add(FrameworkElement e)
        {
            var row = _count / 2;
            if (Root.RowDefinitions.Count <= row) Root.RowDefinitions.Add(new RowDefinition());
            Grid.SetRow(e, row);
            Grid.SetColumn(e, _count % 2 == 0 ? 0 : 2);
            e.Margin = new Thickness(0, 0, 0, _gap);
            Root.Children.Add(e);
            _count++;
        }
    }
}

/// <summary>Visual card for one benchmark.</summary>
internal sealed class BenchCard
{
    private readonly BenchmarksView _owner;
    private readonly IBenchmark _bench;
    private readonly TextBlock _score = new();
    private readonly TextBlock _unit = new();
    private readonly TextBlock _meta = new();
    private readonly StackPanel _bars = new() { Margin = new Thickness(0, 14, 0, 0) };
    private readonly ProgressBar _progress = new() { Maximum = 1, Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button _run = new();

    public Border Root { get; }

    public BenchCard(BenchmarksView owner, IBenchmark bench)
    {
        _owner = owner;
        _bench = bench;
        var app = Application.Current;
        var loc = Loc.Instance;

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Style = (Style)app.FindResource("Text.Section"), Text = loc.T(bench.Title) });
        titles.Children.Add(new TextBlock
        {
            Style = (Style)app.FindResource("Text.Caption"), Text = loc.T(bench.Description), TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.None, Margin = new Thickness(0, 3, 12, 0)
        });
        head.Children.Add(titles);
        _run.Style = (Style)app.FindResource("Button.Icon");
        _run.Width = 36;
        _run.Height = 36;
        _run.Background = (Brush)app.FindResource("Brush.SurfaceAlt");
        _run.Click += (_, _) => _owner.RunOne(_bench);
        Grid.SetColumn(_run, 1);
        head.Children.Add(_run);

        _score.Style = (Style)app.FindResource("Text.Metric");
        _unit.Style = (Style)app.FindResource("Text.Secondary");
        _unit.Margin = new Thickness(6, 0, 0, 5);
        _unit.VerticalAlignment = VerticalAlignment.Bottom;
        var scoreRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        scoreRow.Children.Add(_score);
        scoreRow.Children.Add(_unit);
        _meta.Style = (Style)app.FindResource("Text.Caption");
        _meta.Margin = new Thickness(0, 2, 0, 0);

        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(scoreRow);
        stack.Children.Add(_meta);
        stack.Children.Add(_progress);
        stack.Children.Add(_bars);
        Root = new Border { Style = (Style)app.FindResource("Card"), Child = stack };
    }

    public void SetRunning(bool running, double progress)
    {
        _progress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        _progress.Value = progress;
        _run.Content = running ? "" : "";
        _run.ToolTip = Loc.Instance.T(running ? "Stop" : "Run");
        if (running)
        {
            _meta.Text = Loc.Instance.T("Running, keep other programs idle for accurate results...");
            _bars.Visibility = Visibility.Collapsed;
        }
        else _bars.Visibility = Visibility.Visible;
    }

    public void Show(BenchHistory history, List<BenchReference> references, int threads, bool anyRunning)
    {
        var loc = Loc.Instance;
        var single = threads == 1 && _bench.Id != "mem-latency";
        bool SameMode(BenchResult r) => _bench.Id == "mem-latency" || (r.Threads == 1) == single;

        var runs = history.For(_bench.Id).Where(SameMode).ToList();
        var latest = runs.FirstOrDefault();
        _run.Content = "";
        _run.ToolTip = loc.T("Run");
        _run.IsEnabled = !anyRunning;
        _unit.Text = loc.T(_bench.Unit);
        _unit.Visibility = latest == null ? Visibility.Collapsed : Visibility.Visible;

        if (latest == null)
        {
            _score.Text = "—";
            _score.Foreground = (Brush)Application.Current.FindResource("Brush.Text3");
            _meta.Text = loc.T("Not run yet");
        }
        else
        {
            _score.Text = FormatScore(latest.Score);
            _score.ClearValue(TextBlock.ForegroundProperty);
            var previous = runs.Skip(1).FirstOrDefault();
            var parts = new List<string> { latest.When.ToString("g", CultureInfo.CurrentCulture) };
            if (previous != null && previous.Score > 0)
            {
                var delta = (latest.Score - previous.Score) / previous.Score * 100;
                if (!_bench.HigherIsBetter) delta = -delta;
                parts.Add(loc.F("{0} vs previous run", (delta >= 0 ? "+" : "") + delta.ToString("0.0", CultureInfo.CurrentCulture) + "%"));
            }
            _meta.Text = string.Join("   ·   ", parts);
        }

        _bars.Children.Clear();
        var entries = new List<(string Label, string Sub, double Score, bool Mine)>();
        if (latest != null) entries.Add((loc.T("This computer"), loc.T("latest run"), latest.Score, true));
        var best = _bench.HigherIsBetter ? runs.MaxBy(r => r.Score) : runs.MinBy(r => r.Score);
        if (best != null && latest != null && best != latest) entries.Add((loc.T("This computer"), loc.T("best run"), best.Score, true));
        foreach (var reference in references)
        {
            var r = reference.Results.Where(x => x.BenchmarkId == _bench.Id && SameMode(x)).FirstOrDefault();
            if (r != null) entries.Add((reference.Computer, reference.Processor, r.Score, false));
        }
        if (entries.Count < 2 && latest != null) return;
        if (entries.Count == 0) return;

        var top = _bench.HigherIsBetter ? entries.Max(e => e.Score) : entries.Min(e => e.Score);
        foreach (var e in entries.OrderBy(e => _bench.HigherIsBetter ? -e.Score : e.Score))
        {
            var fraction = top <= 0 ? 0 : _bench.HigherIsBetter ? e.Score / top : top / e.Score;
            _bars.Children.Add(BarRow(e.Label, e.Sub, fraction, FormatScore(e.Score), e.Mine));
        }
    }

    private static FrameworkElement BarRow(string label, string sub, double fraction, string value, bool mine)
    {
        var app = Application.Current;
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.42, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.58, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        var names = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        names.Children.Add(new TextBlock { Style = (Style)app.FindResource("Text.Base"), FontSize = 12, Text = label });
        names.Children.Add(new TextBlock { Style = (Style)app.FindResource("Text.Caption"), FontSize = 11, Text = sub });
        grid.Children.Add(names);

        var track = new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = (Brush)app.FindResource("Brush.Track"), VerticalAlignment = VerticalAlignment.Center };
        var fill = new Border
        {
            Height = 8, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left,
            Background = mine ? (Brush)app.FindResource("Brush.Accent") : (Brush)app.FindResource("Brush.Text3")
        };
        var host = new Grid { VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(track);
        host.Children.Add(fill);
        host.SizeChanged += (_, _) => fill.Width = Math.Max(4, host.ActualWidth * Math.Clamp(fraction, 0, 1));
        Grid.SetColumn(host, 1);
        grid.Children.Add(host);

        var v = new TextBlock { Style = (Style)app.FindResource("Text.Base"), FontSize = 12, Text = value, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(v, 2);
        grid.Children.Add(v);
        return grid;
    }

    private static string FormatScore(double score) =>
        score >= 100 ? score.ToString("N0", CultureInfo.CurrentCulture) : score.ToString("0.0#", CultureInfo.CurrentCulture);
}

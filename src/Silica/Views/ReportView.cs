using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;
using Silica.Core.Info;
using Silica.Core.Model;
using Silica.Localization;
using Silica.Services;

namespace Silica.Views;

public sealed class ReportView : UserControl, INavigable
{
    private readonly Dictionary<string, CheckBox> _checks = [];
    private readonly HashSet<string> _unchecked = [];
    private ReportFormat _format = ReportFormat.Html;
    private bool _openWhenReady = true;
    private bool _busy;
    private Button _create = null!;
    private ProgressBar _progress = null!;
    private TextBlock _status = null!;

    public ReportView()
    {
        Content = Build();
        Loc.Instance.LanguageChanged += () => Content = Build();
    }

    public void OnNavigatedTo(string? target) { }
    public void OnNavigatedFrom() { }

    private FrameworkElement Build()
    {
        var loc = Loc.Instance;
        var app = Application.Current;
        T R<T>(string key) => (T)app.FindResource(key);
        _checks.Clear();

        var header = new Grid { Margin = new Thickness(28, 22, 28, 14) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.Children.Add(new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Background = R<Brush>("Brush.AccentSubtle"), Margin = new Thickness(0, 0, 14, 0),
            Child = new TextBlock { Style = R<Style>("Icon"), Text = "", FontSize = 18, Foreground = R<Brush>("Brush.AccentText") }
        });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Style = R<Style>("Text.Title"), Text = loc.T("Report") });
        titles.Children.Add(new TextBlock { Style = R<Style>("Text.Caption"), Text = loc.T("Save everything Silica knows about this computer to a file"), Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);

        // Format and actions
        var formats = new UniformGrid { Columns = 2 };
        foreach (var (f, label) in new[] { (ReportFormat.Html, "Web page"), (ReportFormat.Text, "Plain text"), (ReportFormat.Markdown, "Markdown"), (ReportFormat.Json, "JSON") })
        {
            var rb = new RadioButton { Style = R<Style>("Segment"), GroupName = "ReportFormat", Content = loc.T(label), IsChecked = _format == f };
            var format = f;
            rb.Checked += (_, _) => _format = format;
            formats.Children.Add(rb);
        }
        var open = new CheckBox { Content = loc.T("Open when ready"), IsChecked = _openWhenReady, Margin = new Thickness(0, 14, 0, 0) };
        open.Checked += (_, _) => _openWhenReady = true;
        open.Unchecked += (_, _) => _openWhenReady = false;

        _create = new Button { Style = R<Style>("Button.Accent"), Content = loc.T("Create report"), MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 18, 0, 0), IsEnabled = !_busy };
        _create.Click += async (_, _) => await Create();
        _progress = new ProgressBar { Maximum = 1, Margin = new Thickness(0, 16, 0, 0), Visibility = _busy ? Visibility.Visible : Visibility.Collapsed };
        _status = new TextBlock { Style = R<Style>("Text.Caption"), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };

        var side = new StackPanel();
        side.Children.Add(new TextBlock { Style = R<Style>("Text.Section"), Text = loc.T("Format"), Margin = new Thickness(0, 0, 0, 10) });
        side.Children.Add(new Border { Style = R<Style>("SegmentHost"), Child = formats, HorizontalAlignment = HorizontalAlignment.Stretch });
        side.Children.Add(new TextBlock
        {
            Style = R<Style>("Text.Caption"), Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap,
            Text = loc.T("A web page opens in any browser and prints well. JSON is meant for other programs.")
        });
        side.Children.Add(open);
        side.Children.Add(_create);
        side.Children.Add(_progress);
        side.Children.Add(_status);
        var sideCard = new Border { Style = R<Style>("Card"), Child = side, VerticalAlignment = VerticalAlignment.Top };

        // Page selection
        var pages = new StackPanel();
        var presets = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        presets.Children.Add(Preset(loc.T("Everything"), _ => true));
        presets.Children.Add(Preset(loc.T("Hardware only"), p => p.Category == InfoCategory.Hardware));
        presets.Children.Add(Preset(loc.T("Software only"), p => p.Category == InfoCategory.Software));
        presets.Children.Add(Preset(loc.T("Nothing"), _ => false));
        pages.Children.Add(new TextBlock { Style = R<Style>("Text.Section"), Text = loc.T("Pages"), Margin = new Thickness(0, 0, 0, 10) });
        pages.Children.Add(presets);

        foreach (var group in InfoProviders.All.GroupBy(p => p.Category))
        {
            pages.Children.Add(new TextBlock
            {
                Style = R<Style>("Text.Overline"), Margin = new Thickness(0, 12, 0, 8),
                Text = loc.T(group.Key == InfoCategory.Hardware ? "Hardware" : "Software").ToUpper(System.Globalization.CultureInfo.CurrentUICulture)
            });
            var grid = new UniformGrid { Columns = 2 };
            foreach (var p in group)
            {
                var cb = new CheckBox { Content = loc.T(p.Title), IsChecked = !_unchecked.Contains(p.Id), Margin = new Thickness(0, 0, 12, 10) };
                var id = p.Id;
                cb.Checked += (_, _) => _unchecked.Remove(id);
                cb.Unchecked += (_, _) => _unchecked.Add(id);
                _checks[p.Id] = cb;
                grid.Children.Add(cb);
            }
            pages.Children.Add(grid);
        }
        var pagesCard = new Border { Style = R<Style>("Card"), Child = pages, Margin = new Thickness(0, 0, 16, 0) };

        var layout = new Grid { Margin = new Thickness(28, 0, 28, 28) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
        layout.Children.Add(pagesCard);
        Grid.SetColumn(sideCard, 1);
        layout.Children.Add(sideCard);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(header);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = layout, Focusable = false };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        return root;

        ToggleButton Preset(string text, Func<IInfoProvider, bool> select)
        {
            var chip = new ToggleButton { Style = R<Style>("Chip"), Content = text };
            chip.Click += (_, _) =>
            {
                chip.IsChecked = false;
                foreach (var p in InfoProviders.All)
                {
                    var on = select(p);
                    if (on) _unchecked.Remove(p.Id); else _unchecked.Add(p.Id);
                    if (_checks.TryGetValue(p.Id, out var cb)) cb.IsChecked = on;
                }
            };
            return chip;
        }
    }

    private async Task Create()
    {
        var selected = InfoProviders.All.Where(p => !_unchecked.Contains(p.Id)).ToList();
        var loc = Loc.Instance;
        if (selected.Count == 0)
        {
            _status.Text = loc.T("Choose at least one page.");
            return;
        }

        var ext = _format switch { ReportFormat.Html => ".html", ReportFormat.Markdown => ".md", ReportFormat.Json => ".json", _ => ".txt" };
        var dialog = new SaveFileDialog
        {
            FileName = Reports.DefaultName() + ext,
            Filter = $"{loc.T(_format switch { ReportFormat.Html => "Web page", ReportFormat.Text => "Plain text", ReportFormat.Markdown => "Markdown", _ => "JSON" })} (*{ext})|*{ext}",
            InitialDirectory = Directory.Exists(AppPaths.DefaultReportFolder) ? AppPaths.DefaultReportFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        _busy = true;
        _create.IsEnabled = false;
        _progress.Visibility = Visibility.Visible;
        _progress.Value = 0;
        try
        {
            var pages = new List<InfoPage>();
            foreach (var p in selected)
            {
                _status.Text = loc.F("Reading {0}...", loc.T(p.Title));
                pages.Add(await PageStore.GetAsync(p));
                _progress.Value = (double)pages.Count / selected.Count;
            }
            var format = _format;
            var path = dialog.FileName;
            await Task.Run(() => Reports.Write(path, pages, format));
            _status.Text = loc.F("Saved to {0}", path);
            if (_openWhenReady) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Report failed", ex);
            _status.Text = loc.T("The report could not be saved.") + " " + ex.Message;
        }
        finally
        {
            _busy = false;
            _create.IsEnabled = true;
            _progress.Visibility = Visibility.Collapsed;
        }
    }
}

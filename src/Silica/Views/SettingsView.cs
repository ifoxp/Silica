using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Microsoft.Win32;
using Silica.Core.Sensors;
using Silica.Localization;
using Silica.Services;

namespace Silica.Views;

public sealed class SettingsView : UserControl, INavigable
{
    private readonly Dictionary<string, FrameworkElement> _sections = [];
    private ScrollViewer _scroll = null!;
    private bool _building;

    private IDisposable? _sensors;

    public SettingsView()
    {
        Content = Build();
        // Names of tray and overlay sensors are only known once the hardware has been read.
        SensorHub.Shared.DevicesChanged += () => Dispatcher.BeginInvoke(RefreshSensorLists);
        Loc.Instance.LanguageChanged += () => Content = Build();
        ThemeService.Changed += () => { if (!_building) Content = Build(); };
    }

    public void OnNavigatedTo(string? target)
    {
        var s = Settings.Current;
        if (_sensors == null && s.TraySensors.Count + s.OverlaySensors.Count > 0 && SensorHub.Shared.State != SensorHubState.Ready)
            _sensors = SensorHub.Shared.Subscribe();
        Content = Build();
        if (target != null && _sections.TryGetValue(target, out var section))
            Dispatcher.BeginInvoke(() =>
            {
                section.BringIntoView(new Rect(0, 0, 1, _scroll.ViewportHeight));
                var accent = ThemeService.Accent;
                var brush = new SolidColorBrush(Colors.Transparent);
                if (section is Border b)
                {
                    b.BorderBrush = brush;
                    var anim = new ColorAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1600) };
                    anim.KeyFrames.Add(new LinearColorKeyFrame(accent, KeyTime.FromPercent(0.15)));
                    anim.KeyFrames.Add(new LinearColorKeyFrame(accent, KeyTime.FromPercent(0.6)));
                    anim.KeyFrames.Add(new LinearColorKeyFrame(ThemeService.ColorOf("C.Stroke"), KeyTime.FromPercent(1)));
                    brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
                }
            }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void OnNavigatedFrom()
    {
        _sensors?.Dispose();
        _sensors = null;
    }

    private static T R<T>(string key) => (T)Application.Current.FindResource(key);
    private static string T(string key) => Loc.Instance.T(key);

    private FrameworkElement Build()
    {
        _building = true;
        _sections.Clear();
        _sensorLists.Clear();
        var s = Settings.Current;

        var header = new Grid { Margin = new Thickness(28, 22, 28, 14) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.Children.Add(new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(8), Background = R<Brush>("Brush.AccentSubtle"), Margin = new Thickness(0, 0, 14, 0),
            Child = new TextBlock { Style = R<Style>("Icon"), Text = "", FontSize = 18, Foreground = R<Brush>("Brush.AccentText") }
        });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Style = R<Style>("Text.Title"), Text = T("Settings") });
        titles.Children.Add(new TextBlock
        {
            Style = R<Style>("Text.Caption"), Margin = new Thickness(0, 2, 0, 0),
            Text = AppPaths.IsPortable ? T("Portable mode: settings are stored next to the program") : T("Changes apply immediately")
        });
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);

        var body = new StackPanel { Margin = new Thickness(28, 0, 28, 28), MaxWidth = 920, HorizontalAlignment = HorizontalAlignment.Left };

        var themeSeg = Segmented([(T("System"), AppTheme.System), (T("Dark"), AppTheme.Dark), (T("Light"), AppTheme.Light)], s.Theme, v =>
        {
            s.Theme = v;
            Settings.Touch();
            ThemeService.Apply();
        });
        var swatches = new WrapPanel();
        foreach (var value in ThemeService.AccentPresets.Prepend(ThemeService.SystemAccent))
        {
            var system = value == ThemeService.SystemAccent;
            var color = system ? ThemeService.WindowsAccent() : (Color)ColorConverter.ConvertFromString(value);
            var selected = string.Equals(s.Accent, value, StringComparison.OrdinalIgnoreCase);
            var dot = new Grid { Width = 30, Height = 30 };
            dot.Children.Add(new Ellipse { Fill = new SolidColorBrush(color), Width = 24, Height = 24 });
            // The Windows colour carries a small mark so it reads as following the system rather than as one more preset.
            if (system) dot.Children.Add(new TextBlock { Style = R<Style>("Icon"), Text = "\uE770", FontSize = 11, Foreground = Brushes.White });
            if (selected) dot.Children.Add(new Ellipse { Stroke = R<Brush>("Brush.Text"), StrokeThickness = 2, Width = 30, Height = 30 });
            var swatch = new RadioButton
            {
                Style = R<Style>("Button.Ghost"), Content = dot, GroupName = "accent", IsChecked = selected,
                Padding = new Thickness(2), MinHeight = 0, Margin = new Thickness(0, 0, system ? 14 : 4, 0),
                ToolTip = system ? T("Same as Windows") : null
            };
            System.Windows.Automation.AutomationProperties.SetName(swatch, system ? T("Same as Windows") : value);
            swatch.Checked += (_, _) =>
            {
                s.Accent = value;
                Settings.Touch();
                ThemeService.Apply();
            };
            swatches.Children.Add(swatch);
        }
        var rendering = Segmented([(T("Automatic"), RenderingMode.Automatic), (T("Graphics"), RenderingMode.Hardware), (T("Processor"), RenderingMode.Software)],
            s.Rendering, v =>
            {
                s.Rendering = v;
                Settings.Touch();
                Performance.ApplyRenderingToAll();
            });
        var appearance = new List<FrameworkElement>
        {
            Row("Theme", "System follows the Windows setting", themeSeg),
            Row("Accent color", null, swatches)
        };
        if (ThemeService.MicaSupported)
            appearance.Add(Switch("Translucent window background", "The desktop colour shows through the title bar and sidebar, as in Windows 11 apps", s.Mica, v =>
            {
                s.Mica = v;
                ThemeService.Apply();
            }));
        appearance.Add(Row("Drawing", "Drawing on the processor uses about 50 MB less memory; the graphics card is smoother on 4K screens", rendering));
        body.Children.Add(Section("appearance", "Appearance", "", [.. appearance]));

        // Fifty-odd languages would make a page of their own, so only the likely ones are listed up front.
        var langs = new StackPanel();
        langs.Children.Add(LanguageRow("auto", T("Automatic"), T("Same as Windows"), null, s.Language is "auto" or ""));
        var suggested = Loc.Instance.Suggested();
        foreach (var pack in suggested)
            langs.Children.Add(LanguageRow(pack.Code, pack.Name, pack.EnglishName, Loc.Instance.Completeness(pack), s.Language == pack.Code));

        var allRows = new List<(LanguagePack Pack, FrameworkElement Row)>();
        var allList = new StackPanel();
        foreach (var pack in Loc.Instance.Available.Except(suggested))
        {
            var row = LanguageRow(pack.Code, pack.Name, pack.EnglishName, Loc.Instance.Completeness(pack), s.Language == pack.Code);
            allRows.Add((pack, row));
            allList.Children.Add(row);
        }
        var noMatch = new TextBlock { Style = R<Style>("Text.Secondary"), Text = T("No language matches."), Margin = new Thickness(0, 8, 0, 8), Visibility = Visibility.Collapsed };
        allList.Children.Add(noMatch);
        var filter = new TextBox { Tag = T("Find a language"), Margin = new Thickness(0, 4, 0, 8), MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left, Width = 360 };
        filter.TextChanged += (_, _) =>
        {
            var q = filter.Text.Trim();
            var shown = 0;
            foreach (var (pack, row) in allRows)
            {
                var hit = q.Length == 0 || pack.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                          || pack.EnglishName.Contains(q, StringComparison.OrdinalIgnoreCase) || pack.Code.StartsWith(q, StringComparison.OrdinalIgnoreCase);
                row.Visibility = hit ? Visibility.Visible : Visibility.Collapsed;
                if (hit) shown++;
            }
            noMatch.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        var allPanel = new StackPanel { Visibility = Visibility.Collapsed };
        allPanel.Children.Add(filter);
        allPanel.Children.Add(allList);
        var chevron = new TextBlock { Style = R<Style>("Icon"), Text = "", FontSize = 10, Foreground = R<Brush>("Brush.AccentText"), Margin = new Thickness(8, 1, 0, 0),
                                      RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(-90) };
        var showAllContent = new StackPanel { Orientation = Orientation.Horizontal };
        showAllContent.Children.Add(new TextBlock { Text = Loc.Instance.F("All languages ({0})", Loc.Instance.Available.Count), Foreground = R<Brush>("Brush.AccentText") });
        showAllContent.Children.Add(chevron);
        var showAll = new ToggleButton
        {
            Style = R<Style>("Button.Ghost"), Margin = new Thickness(-10, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Content = showAllContent
        };
        System.Windows.Automation.AutomationProperties.SetName(showAll, Loc.Instance.F("All languages ({0})", Loc.Instance.Available.Count));
        showAll.Checked += (_, _) => { allPanel.Visibility = Visibility.Visible; ((RotateTransform)chevron.RenderTransform).Angle = 0; filter.Focus(); };
        showAll.Unchecked += (_, _) => { allPanel.Visibility = Visibility.Collapsed; ((RotateTransform)chevron.RenderTransform).Angle = -90; };
        langs.Children.Add(showAll);
        langs.Children.Add(allPanel);
        var langButtons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        langButtons.Children.Add(SmallButton("Open languages folder", () =>
        {
            var folder = Loc.ExternalFolders().Last();
            Directory.CreateDirectory(folder);
            ShellOpen.Open(folder);
        }));
        langButtons.Children.Add(SmallButton("Create translation template", CreateTemplate));
        langButtons.Children.Add(SmallButton("Reload languages", () =>
        {
            Loc.Instance.Reload();
            Loc.Instance.Use(s.Language);
        }));
        var langNote = new TextBlock
        {
            Style = R<Style>("Text.Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
            Text = T("To add a language, create a template, translate the values in any text editor and put the file into the languages folder.")
        };
        body.Children.Add(Section("language", "Language", "", Stack(langs, langButtons, langNote)));

        var unitSeg = Segmented([("°C", false), ("°F", true)], s.Fahrenheit, v => { s.Fahrenheit = v; Settings.Touch(); });
        var intervalText = new TextBlock { Style = R<Style>("Text.Base"), Width = 60, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var interval = new Slider { Minimum = 0.5, Maximum = 5, Width = 220, TickFrequency = 0.5, IsSnapToTickEnabled = true, Value = s.PollIntervalMs / 1000.0 };
        void ShowInterval() => intervalText.Text = Loc.Instance.F("{0} s", interval.Value.ToString("0.0", CultureInfo.CurrentCulture));
        ShowInterval();
        interval.ValueChanged += (_, _) => { s.PollIntervalMs = (int)(interval.Value * 1000); ShowInterval(); Settings.Touch(); };
        var intervalRow = new StackPanel { Orientation = Orientation.Horizontal };
        intervalRow.Children.Add(interval);
        intervalRow.Children.Add(intervalText);

        var groups = new WrapPanel();
        foreach (var (g, title) in new[]
                 {
                     (SensorGroups.Cpu, "Processor"), (SensorGroups.Gpu, "Graphics"), (SensorGroups.Memory, "Memory"), (SensorGroups.Motherboard, "Motherboard"),
                     (SensorGroups.Storage, "Storage"), (SensorGroups.Network, "Network"), (SensorGroups.Controller, "Fan controllers"), (SensorGroups.Battery, "Battery"),
                     (SensorGroups.Psu, "Power supply")
                 })
        {
            var chip = new ToggleButton { Style = R<Style>("Chip"), Content = T(title), IsChecked = s.SensorGroups.HasFlag(g) };
            var group = g;
            void Changed(object sender, RoutedEventArgs e)
            {
                s.SensorGroups = chip.IsChecked == true ? s.SensorGroups | group : s.SensorGroups & ~group;
                Settings.Touch();
            }
            chip.Checked += Changed;
            chip.Unchecked += Changed;
            groups.Children.Add(chip);
        }
        body.Children.Add(Section("monitoring", "Monitoring", "",
            Row("Temperature unit", null, unitSeg),
            Row("Polling interval", "Longer intervals use less processor time", intervalRow),
            Row("Monitored hardware", "Turning off what you do not need makes polling lighter", groups),
            Switch("Show virtual network adapters", "VPN, Hyper-V and filter drivers", s.ShowVirtualNetwork, v => s.ShowVirtualNetwork = v)));

        body.Children.Add(Section("tray", "Tray icon", "",
            Switch("Show Silica in the notification area", null, s.TrayEnabled, v => s.TrayEnabled = v),
            Switch("Minimize to the notification area", null, s.MinimizeToTray, v => s.MinimizeToTray = v),
            Switch("Close to the notification area", "The close button hides the window; exit from the tray menu", s.CloseToTray, v => s.CloseToTray = v),
            Row("Sensors in the tray", "Right-click a sensor on the Sensors page to add it", SensorList(s.TraySensors))));

        var opacity = new Slider { Minimum = 0.3, Maximum = 1, Width = 220, Value = s.OverlayOpacity };
        opacity.ValueChanged += (_, _) => { s.OverlayOpacity = opacity.Value; Settings.Touch(); };
        var scale = new Slider { Minimum = 0.8, Maximum = 1.6, Width = 220, Value = s.OverlayScale };
        scale.ValueChanged += (_, _) => { s.OverlayScale = scale.Value; Settings.Touch(); };
        var overlaySwitch = (CheckBox)Switch("Show the overlay panel", "A small always-on-top panel with the sensors you choose", s.OverlayEnabled, v => s.OverlayEnabled = v);
        var overlaySets = new WrapPanel();
        foreach (var preset in SensorPresets.OverlaySets)
            overlaySets.Children.Add(SmallButton(SensorPresets.Title(preset), () => ApplyOverlayPreset(preset, overlaySwitch)));
        body.Children.Add(Section("overlay", "Overlay", "",
            overlaySwitch,
            Row("Ready-made sets", "Replace the overlay sensors with a set picked for the task", overlaySets),
            Row("Opacity", null, opacity),
            Row("Size", null, scale),
            Switch("Click-through", "Mouse clicks pass through the panel to the window below", s.OverlayClickThrough, v => s.OverlayClickThrough = v),
            Row("Sensors in the overlay", "Right-click a sensor on the Sensors page to add it", SensorList(s.OverlaySensors))));

        var folderText = new TextBlock
        {
            Style = R<Style>("Text.Secondary"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
            Text = string.IsNullOrWhiteSpace(s.LogFolder) ? AppPaths.DefaultSensorLogFolder : s.LogFolder
        };
        var folderRow = new DockPanel();
        var browse = SmallButton("Change...", () =>
        {
            var dlg = new OpenFolderDialog { InitialDirectory = folderText.Text };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            s.LogFolder = dlg.FolderName;
            folderText.Text = dlg.FolderName;
            Settings.Touch();
        });
        DockPanel.SetDock(browse, Dock.Right);
        folderRow.Children.Add(browse);
        folderRow.Children.Add(folderText);
        var logInterval = Segmented(new[] { 1, 2, 5, 10, 60 }.Select(n => (Loc.Instance.F("{0} s", n), n)), s.LogIntervalSeconds, v => { s.LogIntervalSeconds = v; Settings.Touch(); });
        body.Children.Add(Section("logging", "Sensor logging", "",
            Switch("Record sensors to a CSV file", "Opens in Excel or any spreadsheet", Monitoring.IsLogging, v =>
            {
                if (v) Monitoring.StartLogging(); else Monitoring.StopLogging();
            }, touch: false),
            Row("Folder", null, folderRow),
            Row("Write every", null, logInterval)));

        var rules = new StackPanel();
        if (s.Alerts.Count == 0)
            rules.Children.Add(new TextBlock { Style = R<Style>("Text.Secondary"), TextWrapping = TextWrapping.Wrap, Text = T("No alerts yet. Right-click a sensor on the Sensors page and choose Add alert.") });
        foreach (var rule in s.Alerts.ToList())
        {
            var line = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var remove = new Button { Style = R<Style>("Button.Icon"), Content = "", ToolTip = T("Delete"), Width = 30, Height = 30 };
            remove.Click += (_, _) => { s.Alerts.Remove(rule); Settings.Touch(); Content = Build(); };
            DockPanel.SetDock(remove, Dock.Right);
            var enabled = new CheckBox { IsChecked = rule.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            enabled.Checked += (_, _) => { rule.Enabled = true; Settings.Touch(); };
            enabled.Unchecked += (_, _) => { rule.Enabled = false; Settings.Touch(); };
            DockPanel.SetDock(enabled, Dock.Left);
            line.Children.Add(remove);
            line.Children.Add(enabled);
            line.Children.Add(new TextBlock
            {
                Style = R<Style>("Text.Base"), VerticalAlignment = VerticalAlignment.Center,
                Text = $"{rule.SensorName}   {(rule.Above ? ">" : "<")} {rule.Threshold.ToString(CultureInfo.CurrentCulture)}   ·   {Loc.Instance.F("{0} s", rule.DurationSeconds)}"
            });
            rules.Children.Add(line);
        }
        var cooldown = Segmented(new[] { 1, 5, 15, 60 }.Select(n => (Loc.Instance.F("{0} min", n), n)), s.AlertCooldownMinutes, v => { s.AlertCooldownMinutes = v; Settings.Touch(); });
        body.Children.Add(Section("alerts", "Alerts", "",
            Switch("Show alerts", "A notification appears when a sensor crosses its limit", s.AlertsEnabled, v => s.AlertsEnabled = v),
            Row("Repeat the same alert at most every", null, cooldown),
            Row("Rules", null, rules)));

        var startupOn = false;
        try { startupOn = StartupService.IsEnabled(); } catch { }
        // An existing task can always be removed, a new one only for the installed copy.
        var startupAllowed = StartupService.IsAllowed || startupOn;
        FrameworkElement? startupSwitch = null;
        startupSwitch = Switch("Start with Windows", startupAllowed ? "Starts after you sign in, without a UAC prompt" : null, startupOn, v =>
            {
                try
                {
                    StartupService.Set(v);
                    s.StartWithWindows = v;
                    if (!StartupService.IsAllowed) startupSwitch!.IsEnabled = false;
                }
                catch (Exception ex)
                {
                    Log.Error("Startup task", ex);
                    ErrorDialog.Show(Window.GetWindow(this), ex, T("Windows did not allow changing the startup task."));
                }
            });
        FrameworkElement startupRow = startupSwitch;
        if (!startupAllowed)
        {
            // The reason stays at full contrast; only the switch itself is dimmed.
            startupSwitch.IsEnabled = false;
            startupSwitch.Margin = new Thickness(0, 10, 0, 2);
            startupRow = Stack(startupSwitch, new TextBlock
            {
                Style = R<Style>("Text.Caption"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 0, 64, 10),
                Text = T("Available in the installed version. A portable copy can be replaced by other programs, so Windows must not start it with administrator rights")
            });
        }
        body.Children.Add(Section("startup", "Startup", "",
            startupRow,
            Switch("Start minimized", "Starts in the notification area when the tray icon is on", s.StartMinimized, v => s.StartMinimized = v)));

        body.Children.Add(Section("search", "Search", "",
            Switch("Search inside all pages", "Reads the remaining pages in the background the first time you search", s.DeepSearch, v => s.DeepSearch = v)));

        var about = new StackPanel();
        about.Children.Add(new TextBlock { Style = R<Style>("Text.Base"), Text = $"Silica {Reports.Version}", FontWeight = FontWeights.SemiBold });
        about.Children.Add(new TextBlock
        {
            Style = R<Style>("Text.Secondary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            Text = T("Free and open source under the MIT license. Sensor access is provided by LibreHardwareMonitor (MPL 2.0) and the PawnIO driver.")
        });
        about.Children.Add(new TextBlock
        {
            Style = R<Style>("Text.Caption"), Margin = new Thickness(0, 8, 0, 0),
            Text = Loc.Instance.F("Sensor driver: {0}", T(SensorHub.DriverInstalled ? "PawnIO installed" : "PawnIO not installed"))
        });
        var aboutButtons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        aboutButtons.Children.Add(SmallButton("Open data folder", () =>
        {
            AppPaths.EnsureDataDirectory();
            ShellOpen.Open(AppPaths.DataDirectory);
        }));
        if (File.Exists(AppPaths.LogFile))
            aboutButtons.Children.Add(SmallButton("Open log file", () => ShellOpen.Open(AppPaths.LogFile)));
        about.Children.Add(aboutButtons);
        body.Children.Add(Section("about", "About", "", about));

        var removeButton = new Button
        {
            Content = T("Remove Silica"), Style = R<Style>("Button.Danger"), HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 12, 0, 4), Padding = new Thickness(16, 7, 16, 7)
        };
        removeButton.Click += (_, _) => ConfirmRemove();
        var removePanel = new StackPanel();
        removePanel.Children.Add(new TextBlock
        {
            Style = R<Style>("Text.Secondary"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None,
            Text = T("Deletes the program together with its settings, logs, benchmark history and startup task, so nothing is left behind.")
        });
        removePanel.Children.Add(removeButton);
        body.Children.Add(Section("remove", "Remove Silica", "", removePanel));

        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body, Focusable = false };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(header);
        Grid.SetRow(_scroll, 1);
        root.Children.Add(_scroll);
        _building = false;
        return root;
    }

    private Border Section(string anchor, string title, string icon, params FrameworkElement[] rows)
    {
        var stack = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        head.Children.Add(new TextBlock { Style = R<Style>("Icon"), Text = icon, FontSize = 16, Foreground = R<Brush>("Brush.AccentText"), Margin = new Thickness(0, 0, 10, 0) });
        head.Children.Add(new TextBlock { Style = R<Style>("Text.Section"), FontSize = 18, Text = T(title) });
        stack.Children.Add(head);
        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0) stack.Children.Add(new Border { Style = R<Style>("Divider"), Margin = new Thickness(0, 2, 0, 2) });
            stack.Children.Add(rows[i]);
        }
        var card = new Border { Style = R<Style>("Card"), Child = stack, Margin = new Thickness(0, 0, 0, 16) };
        _sections[anchor] = card;
        return card;
    }

    private static FrameworkElement Row(string title, string? hint, FrameworkElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.38, GridUnitType.Star), MinWidth = 200 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.62, GridUnitType.Star) });
        var labels = new StackPanel { Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Style = R<Style>("Text.Base"), Text = T(title), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None });
        if (hint != null)
            labels.Children.Add(new TextBlock { Style = R<Style>("Text.Caption"), Text = T(hint), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 2, 0, 0) });
        grid.Children.Add(labels);
        control.VerticalAlignment = VerticalAlignment.Center;
        if (control is not Panel and not Slider) control.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private static FrameworkElement Switch(string title, string? hint, bool value, Action<bool> set, bool touch = true)
    {
        var labels = new StackPanel();
        labels.Children.Add(new TextBlock { Style = R<Style>("Text.Base"), Text = T(title), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None });
        if (hint != null)
            labels.Children.Add(new TextBlock { Style = R<Style>("Text.Caption"), Text = T(hint), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 2, 0, 0) });
        var cb = new CheckBox { Style = R<Style>("Switch"), Content = labels, IsChecked = value, Margin = new Thickness(0, 10, 0, 10), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        System.Windows.Automation.AutomationProperties.SetName(cb, T(title));
        void Changed(object sender, RoutedEventArgs e)
        {
            set(cb.IsChecked == true);
            if (touch) Settings.Touch();
        }
        cb.Checked += Changed;
        cb.Unchecked += Changed;
        return cb;
    }

    private static FrameworkElement Segmented<TValue>(IEnumerable<(string Text, TValue Value)> options, TValue current, Action<TValue> set)
    {
        var group = Guid.NewGuid().ToString("N");
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (text, value) in options)
        {
            var rb = new RadioButton { Style = R<Style>("Segment"), GroupName = group, Content = text, IsChecked = Equals(value, current) };
            var v = value;
            rb.Checked += (_, _) => set(v);
            panel.Children.Add(rb);
        }
        return new Border { Style = R<Style>("SegmentHost"), Child = panel };
    }

    private static Button SmallButton(string text, Action action)
    {
        var b = new Button { Content = T(text), Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(12, 5, 12, 5) };
        b.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex)
            {
                Log.Error(text, ex);
                ErrorDialog.Show(Window.GetWindow(b), ex);
            }
        };
        return b;
    }

    private static StackPanel Stack(params FrameworkElement[] children)
    {
        var s = new StackPanel { Margin = new Thickness(0, 8, 0, 4) };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    private FrameworkElement LanguageRow(string code, string name, string english, double? completeness, bool selected)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var dot = new Grid { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left };
        dot.Children.Add(new Ellipse { Stroke = selected ? R<Brush>("Brush.Accent") : R<Brush>("Brush.StrokeStrong"), StrokeThickness = 1.5 });
        if (selected) dot.Children.Add(new Ellipse { Fill = R<Brush>("Brush.Accent"), Width = 8, Height = 8 });
        grid.Children.Add(dot);
        var labels = new StackPanel { Orientation = Orientation.Horizontal };
        labels.Children.Add(new TextBlock { Style = R<Style>("Text.Base"), Text = name });
        if (english.Length > 0 && english != name)
            labels.Children.Add(new TextBlock { Style = R<Style>("Text.Caption"), Text = english, Margin = new Thickness(10, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(labels, 1);
        grid.Children.Add(labels);
        if (completeness is { } c)
        {
            var missing = c < 0.999;
            var pct = new TextBlock
            {
                Style = R<Style>("Text.Caption"),
                Text = missing ? Loc.Instance.F("{0}% translated", Math.Floor(c * 100)) : T("Complete"),
                Foreground = missing ? R<Brush>("Brush.Warning") : R<Brush>("Brush.Text3")
            };
            Grid.SetColumn(pct, 2);
            grid.Children.Add(pct);
        }
        var row = new RadioButton
        {
            Style = R<Style>("Button.Ghost"), Content = grid, GroupName = "language", IsChecked = selected,
            Padding = new Thickness(10, 9, 10, 9), Margin = new Thickness(-10, 0, -10, 0), MinHeight = 0,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        System.Windows.Automation.AutomationProperties.SetName(row, english.Length > 0 && english != name ? $"{name}, {english}" : name);
        row.Checked += (_, _) =>
        {
            Settings.Current.Language = code;
            Settings.Touch();
            Loc.Instance.Use(code);
        };
        return row;
    }

    private void ConfirmRemove()
    {
        var installed = Uninstaller.InstalledProductCode() != null;
        var extra = new StackPanel();
        CheckBox? documents = null;
        if (Directory.Exists(Uninstaller.DocumentsFolder))
        {
            documents = new CheckBox
            {
                IsChecked = true, Margin = new Thickness(0, 0, 0, 10),
                Content = new TextBlock
                {
                    Style = R<Style>("Text.Base"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None,
                    Text = Loc.Instance.F("Also delete saved reports and sensor logs in {0}", Uninstaller.DocumentsFolder)
                }
            };
            extra.Children.Add(documents);
        }
        if (SensorHub.DriverInstalled)
        {
            extra.Children.Add(new TextBlock
            {
                Style = R<Style>("Text.Caption"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None,
                Text = T("The PawnIO driver stays installed because other monitoring programs use it too. You can remove it in Windows Settings, Apps.")
            });
        }

        var text = T(installed
            ? "Silica will close and Windows will uninstall it. Its settings, logs, benchmark history and startup task are deleted as well."
            : "Silica will close and delete its program file, settings, logs, benchmark history and startup task.");
        if (!ErrorDialog.Confirm(Window.GetWindow(this), T("Remove Silica?"), text, T("Remove Silica"), "",
                extra.Children.Count > 0 ? extra : null, destructive: true))
            return;

        try
        {
            Uninstaller.Run(documents?.IsChecked == true);
            (Application.Current.MainWindow as MainWindow)?.ExitApplication();
        }
        catch (Exception ex)
        {
            Log.Error("Uninstall", ex);
            ErrorDialog.Show(Window.GetWindow(this), ex, T("Silica could not start the removal."));
        }
    }

    private readonly List<(WrapPanel Host, List<string> Ids)> _sensorLists = [];

    // The set is resolved from the live sensors, so the hardware is read first if nothing else has done it yet.
    private void ApplyOverlayPreset(SensorPreset preset, CheckBox overlaySwitch)
    {
        var hub = SensorHub.Shared;
        var subscription = hub.Subscribe();
        void Apply()
        {
            var ids = SensorPresets.Pick(preset, hub.Key).Select(r => r.Id).ToList();
            subscription.Dispose();
            if (ids.Count == 0) return;
            Settings.Current.OverlaySensors = ids;
            overlaySwitch.IsChecked = true;
            Settings.Touch();
            RefreshSensorLists();
        }
        if (hub.State == SensorHubState.Ready && hub.Devices.Count > 0)
        {
            Apply();
            return;
        }
        void OnDevices()
        {
            hub.DevicesChanged -= OnDevices;
            Dispatcher.BeginInvoke(Apply);
        }
        hub.DevicesChanged += OnDevices;
    }

    private void RefreshSensorLists()
    {
        foreach (var (host, ids) in _sensorLists) FillSensorList(host, ids);
    }

    private FrameworkElement SensorList(List<string> ids)
    {
        var wrap = new WrapPanel();
        _sensorLists.Add((wrap, ids));
        FillSensorList(wrap, ids);
        return wrap;
    }

    private void FillSensorList(WrapPanel wrap, List<string> ids)
    {
        wrap.Children.Clear();
        if (ids.Count == 0)
        {
            wrap.Children.Add(new TextBlock { Style = R<Style>("Text.Secondary"), Text = T("None") });
            return;
        }
        foreach (var id in ids.ToList())
        {
            var reading = SensorHub.Shared.Find(id);
            var name = reading == null ? T("Reading sensors...") : $"{T(reading.Name)} · {reading.Device.Name}";
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock { Style = R<Style>("Text.Caption"), Text = name, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 280 });
            var close = new Button { Style = R<Style>("Button.Icon"), Width = 22, Height = 22, FontSize = 9, Content = "", ToolTip = T("Remove"), Margin = new Thickness(4, 0, 0, 0) };
            close.Click += (_, _) => { ids.Remove(id); Settings.Touch(); FillSensorList(wrap, ids); };
            panel.Children.Add(close);
            wrap.Children.Add(new Border { Background = R<Brush>("Brush.SurfaceAlt"), CornerRadius = new CornerRadius(13), Padding = new Thickness(10, 2, 4, 2), Margin = new Thickness(0, 0, 6, 6), Child = panel });
        }
    }

    private void CreateTemplate()
    {
        var dlg = new SaveFileDialog
        {
            FileName = "new-language.json",
            Filter = "JSON (*.json)|*.json",
            InitialDirectory = Loc.ExternalFolders().Last()
        };
        Directory.CreateDirectory(dlg.InitialDirectory);
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        File.WriteAllText(dlg.FileName, LanguageTemplate.Build(Loc.Instance.Current));
        (Window.GetWindow(this) as MainWindow)?.ShowToast(T("Template saved"));
    }
}

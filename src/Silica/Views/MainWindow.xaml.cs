using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Silica.Controls;
using Silica.Localization;
using Silica.Services;

namespace Silica.Views;

/// <summary>Implemented by views that need to know when they become visible.</summary>
public interface INavigable
{
    void OnNavigatedTo(string? target);
    void OnNavigatedFrom();
}

public partial class MainWindow : Window
{
    private readonly IReadOnlyList<NavItem> _items = NavCatalog.Build();
    private readonly Dictionary<string, RadioButton> _buttons = [];
    private NavItem? _current;
    private readonly System.Windows.Threading.DispatcherTimer _searchRefresh = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _reallyClose;
    private bool _startHidden;

    public MainWindow()
    {
        InitializeComponent();
        BuildNavigation();
        RestorePlacement();

        AdminBanner.Visibility = Elevation.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        Loc.Instance.LanguageChanged += RebuildNavigationTexts;
        SourceInitialized += (_, _) =>
        {
            ThemeService.ApplyWindowFrame(this);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        };
        StateChanged += (_, _) => OnStateChanged();
        IsVisibleChanged += (_, _) => UpdateActivity();
        Loaded += (_, _) => Navigate(Settings.Current.StartPage);
        Closing += OnClosing;

        // Results grow while pages are read in the background; refresh the open popup at a calm pace.
        _searchRefresh.Tick += (_, _) =>
        {
            _searchRefresh.Stop();
            if (SearchPopup.IsOpen) UpdateSearch();
        };
        SearchService.IndexChanged += () => Dispatcher.BeginInvoke(() =>
        {
            if (SearchPopup.IsOpen && !_searchRefresh.IsEnabled) _searchRefresh.Start();
        });

        InputBindings.Add(new KeyBinding(new RelayCommand(FocusSearch), Key.K, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(FocusSearch), Key.F, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(RefreshCurrent), Key.F5, ModifierKeys.None));
    }

    public NavItem? Current => _current;

    public void StartHidden()
    {
        _startHidden = true;
        Show();
        Hide();
    }

    public void BringToFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void ExitApplication()
    {
        _reallyClose = true;
        Close();
    }

    public void Navigate(string id, string? target = null)
    {
        var item = _items.FirstOrDefault(i => i.Id == id) ?? _items[0];
        if (_buttons.TryGetValue(item.Id, out var button) && button.IsChecked != true) button.IsChecked = true;

        if (_current != item)
        {
            (_current?.View as INavigable)?.OnNavigatedFrom();
            _current = item;
            Host.Content = item.View;
            item.View.Opacity = 0;
            item.View.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
            if (item.Id != "settings" && !App.Args.IsAutomation)
            {
                Settings.Current.StartPage = item.Id;
                Settings.Touch();
            }
        }
        (item.View as INavigable)?.OnNavigatedTo(target);
    }

    public void ShowToast(string text)
    {
        ToastText.Text = text;
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1700))));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2000))));
        Toast.BeginAnimation(OpacityProperty, anim);
    }

    private void BuildNavigation()
    {
        NavPanel.Children.Clear();
        NavFooter.Children.Clear();
        _buttons.Clear();

        NavSection? section = null;
        foreach (var item in _items)
        {
            var button = new RadioButton
            {
                Style = (Style)FindResource("NavItem"),
                Content = Loc.Instance.T(item.Title),
                Tag = item
            };
            Ui.SetIcon(button, item.Icon);
            button.Checked += (_, _) => Navigate(item.Id);
            _buttons[item.Id] = button;

            if (item.Id == "settings")
            {
                NavFooter.Children.Add(button);
                continue;
            }

            if (item.Section != section)
            {
                section = item.Section;
                if (item.Section != NavSection.Main)
                    NavPanel.Children.Add(new TextBlock
                    {
                        Style = (Style)FindResource("NavSection"),
                        Text = SectionTitle(item.Section),
                        Tag = item.Section
                    });
            }
            NavPanel.Children.Add(button);
        }
        ApplySidebarState();
    }

    private void RebuildNavigationTexts()
    {
        foreach (var (_, b) in _buttons)
        {
            var item = (NavItem)b.Tag;
            b.Content = Loc.Instance.T(item.Title);
            b.ToolTip = Settings.Current.SidebarCollapsed ? b.Content : null;
        }
        foreach (var t in NavPanel.Children.OfType<TextBlock>())
            if (t.Tag is NavSection s) t.Text = SectionTitle(s);
    }

    private static string SectionTitle(NavSection s) => Loc.Instance.T(s switch
    {
        NavSection.Hardware => "Hardware",
        NavSection.Software => "Software",
        _ => "Tools"
    }).ToUpper(System.Globalization.CultureInfo.CurrentUICulture);

    private void ApplySidebarState()
    {
        var compact = Settings.Current.SidebarCollapsed;
        SidebarColumn.Width = new GridLength(compact ? 62 : 236);
        Ui.SetCompact(NavPanel, compact);
        Ui.SetCompact(NavFooter, compact);
        foreach (var t in NavPanel.Children.OfType<TextBlock>()) t.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        foreach (var (_, b) in _buttons) b.ToolTip = compact ? b.Content : null;
        SidebarToggle.ToolTip = Loc.Instance.T(compact ? "Expand sidebar" : "Collapse sidebar");
    }

    private void OnToggleSidebar(object sender, RoutedEventArgs e)
    {
        Settings.Current.SidebarCollapsed = !Settings.Current.SidebarCollapsed;
        Settings.Touch();
        ApplySidebarState();
    }

    private void RefreshCurrent()
    {
        if (_current?.View is InfoPageView page) page.Refresh();
    }

    private void OnRestartElevated(object sender, RoutedEventArgs e)
    {
        if (Elevation.RestartElevated()) ExitApplication();
    }

    private void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnSearchFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        SearchService.WarmUp();
        if (SearchBox.Text.Length > 0) UpdateSearch();
    }

    private void OnSearchLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!SearchPopup.IsKeyboardFocusWithin && !SearchResults.IsMouseOver) SearchPopup.IsOpen = false;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => UpdateSearch();

    public void UpdateSearch()
    {
        var text = SearchBox.Text.Trim();
        if (text.Length == 0)
        {
            SearchPopup.IsOpen = false;
            return;
        }

        var hits = SearchService.Query(text);
        var keep = Math.Max(0, SearchResults.SelectedIndex);
        SearchResults.ItemsSource = hits.Select(SearchResultView.Create).ToList();
        if (hits.Count > 0) SearchResults.SelectedIndex = Math.Min(keep, hits.Count - 1);
        SearchEmpty.Visibility = hits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchStatus.Text = SearchService.IsIndexing
            ? Loc.Instance.T("Still reading system data, more results will appear")
            : Loc.Instance.N(hits.Count, "{0} results");
        SearchPopup.IsOpen = true;
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        var count = SearchResults.Items.Count;
        switch (e.Key)
        {
            case Key.Down when count > 0:
                SearchResults.SelectedIndex = (SearchResults.SelectedIndex + 1) % count;
                SearchResults.ScrollIntoView(SearchResults.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up when count > 0:
                SearchResults.SelectedIndex = (SearchResults.SelectedIndex - 1 + count) % count;
                SearchResults.ScrollIntoView(SearchResults.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter when SearchResults.SelectedItem is FrameworkElement { Tag: SearchHit hit }:
                OpenHit(hit);
                e.Handled = true;
                break;
            case Key.Escape:
                if (SearchBox.Text.Length > 0 && !SearchPopup.IsOpen) SearchBox.Clear();
                SearchPopup.IsOpen = false;
                Host.Focus();
                Keyboard.ClearFocus();
                e.Handled = true;
                break;
        }
    }

    private void OnSearchResultClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(SearchResults, d) is ListBoxItem { Content: FrameworkElement { Tag: SearchHit hit } })
            OpenHit(hit);
    }

    private void OpenHit(SearchHit hit)
    {
        SearchPopup.IsOpen = false;
        Keyboard.ClearFocus();
        Navigate(hit.NavId, hit.Target);
    }

    private void OnMinimize(object sender, RoutedEventArgs e)
    {
        if (Settings.Current.TrayEnabled && Settings.Current.MinimizeToTray) Hide();
        else WindowState = WindowState.Minimized;
    }

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private bool _active = true;

    /// <summary>A minimized or hidden window stops its page from polling sensors and redrawing.</summary>
    private void UpdateActivity()
    {
        var active = IsVisible && WindowState != WindowState.Minimized;
        if (active == _active || _current == null) return;
        _active = active;
        if (active) (_current.View as INavigable)?.OnNavigatedTo(null);
        else (_current.View as INavigable)?.OnNavigatedFrom();
    }

    private void OnStateChanged()
    {
        UpdateActivity();
        // A maximized chrome-less window extends past the screen by the frame width.
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        if (WindowState == WindowState.Minimized && Settings.Current.TrayEnabled && Settings.Current.MinimizeToTray) Hide();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_reallyClose && Settings.Current.TrayEnabled && Settings.Current.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        SavePlacement();
        (_current?.View as INavigable)?.OnNavigatedFrom();
        Application.Current.Shutdown();
    }

    private void RestorePlacement()
    {
        var p = Settings.Current.Window;
        Width = Math.Max(MinWidth, p.Width);
        Height = Math.Max(MinHeight, p.Height);
        if (!double.IsNaN(p.Left) && !double.IsNaN(p.Top) && IsOnScreen(p.Left, p.Top, Width, Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = p.Left;
            Top = p.Top;
        }
        if (p.Maximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        var p = Settings.Current.Window;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.IsEmpty) return;
        p.Left = bounds.Left;
        p.Top = bounds.Top;
        p.Width = bounds.Width;
        p.Height = bounds.Height;
        p.Maximized = WindowState == WindowState.Maximized;
        Settings.SaveNow();
    }

    private static bool IsOnScreen(double left, double top, double width, double height)
    {
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        return screen.IntersectsWith(new Rect(left + 40, top + 10, Math.Max(1, width - 80), 30));
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_startHidden) Hide();
    }

    // Lets Windows 11 show snap layouts when hovering the maximize button.
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x0084, WM_NCLBUTTONDOWN = 0x00A1, HTMAXBUTTON = 9;
        if (msg is WM_NCHITTEST or WM_NCLBUTTONDOWN && MaxButton.IsLoaded)
        {
            var x = (short)(lParam.ToInt64() & 0xFFFF);
            var y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
            var point = MaxButton.PointFromScreen(new Point(x, y));
            if (point.X >= 0 && point.Y >= 0 && point.X < MaxButton.ActualWidth && point.Y < MaxButton.ActualHeight)
            {
                if (msg == WM_NCLBUTTONDOWN)
                {
                    handled = true;
                    OnMaximize(this, new RoutedEventArgs());
                    return IntPtr.Zero;
                }
                handled = true;
                MaxButton.Background = (Brush)FindResource("Brush.Hover");
                return HTMAXBUTTON;
            }
            MaxButton.ClearValue(BackgroundProperty);
        }
        return IntPtr.Zero;
    }
}

public sealed class RelayCommand(Action action) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action();
}

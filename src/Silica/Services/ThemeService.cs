using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Silica.Services;

public static class ThemeService
{
    public static readonly string[] AccentPresets =
        ["#4C8DFF", "#2BB3A3", "#8B6CF6", "#E5578A", "#F08A3C", "#4DAA57", "#7A8699"];

    private static ResourceDictionary? _palette;

    public static bool IsDark { get; private set; } = true;
    public static Color Accent { get; private set; } = (Color)ColorConverter.ConvertFromString("#4C8DFF");

    public static event Action? Changed;

    public static void Initialize()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && Settings.Current.Theme == AppTheme.System)
                Application.Current?.Dispatcher.BeginInvoke(Apply);
        };
        Apply();
    }

    public static void Apply()
    {
        var s = Settings.Current;
        IsDark = s.Theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => !SystemUsesLightTheme()
        };

        var resources = Application.Current.Resources;
        var palette = new ResourceDictionary
        {
            Source = new Uri(IsDark ? "pack://application:,,,/Silica;component/Themes/Dark.xaml" : "pack://application:,,,/Silica;component/Themes/Light.xaml")
        };
        if (_palette != null) resources.MergedDictionaries.Remove(_palette);
        // App.xaml ships the dark palette so the designer has colors; drop it on first apply.
        foreach (var stale in resources.MergedDictionaries.Where(d => d.Source?.OriginalString.EndsWith("Dark.xaml", StringComparison.Ordinal) == true
                                                                   || d.Source?.OriginalString.EndsWith("Light.xaml", StringComparison.Ordinal) == true).ToList())
            resources.MergedDictionaries.Remove(stale);
        resources.MergedDictionaries.Insert(0, palette);
        _palette = palette;

        try { Accent = (Color)ColorConverter.ConvertFromString(s.Accent); }
        catch (FormatException) { Accent = (Color)ColorConverter.ConvertFromString(AccentPresets[0]); }

        var a = Accent;
        resources["Brush.Accent"] = Freeze(a);
        resources["Brush.AccentHover"] = Freeze(IsDark ? Mix(a, Colors.White, 0.12) : Mix(a, Colors.Black, 0.1));
        resources["Brush.AccentPressed"] = Freeze(IsDark ? Mix(a, Colors.Black, 0.12) : Mix(a, Colors.Black, 0.2));
        resources["Brush.AccentSubtle"] = Freeze(Color.FromArgb(IsDark ? (byte)0x2A : (byte)0x1E, a.R, a.G, a.B));
        resources["Brush.AccentText"] = Freeze(IsDark ? Mix(a, Colors.White, 0.25) : Mix(a, Colors.Black, 0.15));
        resources["Brush.OnAccent"] = Freeze(Luminance(a) > 0.6 ? Color.FromRgb(0x10, 0x14, 0x1A) : Colors.White);
        resources["C.Accent"] = a;

        foreach (Window w in Application.Current.Windows) ApplyWindowFrame(w);
        Changed?.Invoke();
    }

    /// <summary>Dark title bar / border for a window, and rounded corners on Windows 11.</summary>
    public static void ApplyWindowFrame(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        var round = 2;
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
    }

    public static Brush BrushOf(string key) =>
        Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;

    public static Color ColorOf(string key) =>
        Application.Current.TryFindResource(key) is Color c ? c : Colors.Gray;

    public static Color Series(int index) => ColorOf("C.Series" + (index % 8 + 1));

    private static bool SystemUsesLightTheme() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0) is 1;

    private static SolidColorBrush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Color Mix(Color a, Color b, double t) => System.Windows.Media.Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

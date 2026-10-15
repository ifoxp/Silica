using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Silica.Views;

namespace Silica.Services;

/// <summary>
/// Drives the UI from the command line and saves a screenshot, e.g.
/// silica.exe --shot out.png --page sensors --theme light --lang uk --size 1400x900 --wait 4
/// </summary>
public static class Automation
{
    public static void Attach(MainWindow window, CommandLine args)
    {
        if (args.Theme is { } theme && Enum.TryParse<AppTheme>(theme, true, out var mode))
        {
            Settings.Current.Theme = mode;
            ThemeService.Apply();
        }
        if (args.Render is { } render && Enum.TryParse<RenderingMode>(render, true, out var rendering))
            Settings.Current.Rendering = rendering;
        if (args.Size is { } size && size.Split('x') is [var w, var h]
            && double.TryParse(w, CultureInfo.InvariantCulture, out var width) && double.TryParse(h, CultureInfo.InvariantCulture, out var height))
        {
            window.WindowState = WindowState.Normal;
            window.Width = width;
            window.Height = height;
        }
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 20;
        window.Top = 20;
        window.Topmost = true;
        window.Show();
        if (args.Size == "max") window.WindowState = WindowState.Maximized;
        var page = (args.Page ?? "dashboard").Split(':', 2);
        window.Navigate(page[0], page.Length > 1 ? page[1] : null);

        if (args.Search is { } query)
        {
            window.Dispatcher.BeginInvoke(() =>
            {
                window.SearchBox.Focus();
                window.SearchBox.Text = query;
            }, DispatcherPriority.ApplicationIdle);
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(args.Wait) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                if (args.Search != null)
                {
                    window.UpdateSearch();
                    var hits = SearchService.Query(args.Search);
                    File.WriteAllLines(args.ShotPath! + ".txt",
                        hits.Select(h => $"{h.Score:0.00} {h.Kind} {h.Title} | {h.Subtitle}").Prepend($"indexing={SearchService.IsIndexing} pages={PageStore.All.Count()}"));
                }
                Capture(window, args.ShotPath!);
                WriteMemory(args.ShotPath! + ".mem.txt");
            }
            catch (Exception ex)
            {
                Log.Error("Screenshot failed", ex);
            }
            Application.Current.Shutdown();
        };
        timer.Start();
    }

    private static void WriteMemory(string path)
    {
        var p = System.Diagnostics.Process.GetCurrentProcess();
        var before = GC.GetTotalMemory(false);
        var after = GC.GetTotalMemory(true);
        var info = GC.GetGCMemoryInfo();
        File.WriteAllLines(path,
        [
            $"working set {p.WorkingSet64 >> 20} MB, private {p.PrivateMemorySize64 >> 20} MB",
            $"managed before GC {before >> 20} MB, after full GC {after >> 20} MB, heap {info.HeapSizeBytes >> 20} MB, committed {info.TotalCommittedBytes >> 20} MB",
            $"threads {p.Threads.Count}, handles {p.HandleCount}",
            $"render {(PresentationSource.FromVisual(Application.Current.MainWindow) as HwndSource)?.CompositionTarget?.RenderMode}, sensors {Silica.Core.Sensors.SensorHub.Shared.State}, languages loaded {Silica.Localization.Loc.Instance.Available.Count(l => l.IsLoaded)}/{Silica.Localization.Loc.Instance.Available.Count}"
        ]);
    }

    private static void Capture(Window window, string path)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        GetWindowRect(hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(mem, bmp);
        BitBlt(mem, 0, 0, w, h, screen, r.Left, r.Top, 0x00CC0020 | 0x40000000);
        SelectObject(mem, old);
        var source = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        DeleteObject(bmp);
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(file);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}

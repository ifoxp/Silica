using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Silica.Services;

/// <summary>Memory-related switches: where the UI is drawn and giving memory back while hidden.</summary>
public static class Performance
{
    // Above this many pixels per screen, software drawing starts to cost noticeable processor time.
    private const long SoftwarePixelLimit = 2560L * 1600;

    /// <summary>
    /// WPF on the graphics card keeps a Direct3D device and its buffers (about 30-70 MB); the software
    /// renderer is just as sharp and on ordinary screens costs little extra processor time.
    /// </summary>
    public static bool UseSoftwareRendering => Settings.Current.Rendering switch
    {
        RenderingMode.Software => true,
        RenderingMode.Hardware => false,
        _ => LargestScreenPixels() <= SoftwarePixelLimit
    };

    public static void ApplyRendering(Window window)
    {
        if (PresentationSource.FromVisual(window) is HwndSource { CompositionTarget: { } target })
            target.RenderMode = UseSoftwareRendering ? RenderMode.SoftwareOnly : RenderMode.Default;
    }

    public static void ApplyRenderingToAll()
    {
        foreach (Window w in Application.Current.Windows) ApplyRendering(w);
    }

    /// <summary>Returns unused pages to Windows; they come back on demand when the window is shown again.</summary>
    public static void TrimWorkingSet()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
    }

    private static long LargestScreenPixels()
    {
        long largest = 0;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
                largest = Math.Max(largest, (long)(info.rcMonitor.Right - info.rcMonitor.Left) * (info.rcMonitor.Bottom - info.rcMonitor.Top));
            return true;
        }, IntPtr.Zero);
        return largest;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);
}

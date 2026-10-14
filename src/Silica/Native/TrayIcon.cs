using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Silica.Native;

/// <summary>Notification-area icon built directly on Shell_NotifyIcon.</summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_APP_TRAY = 0x8000 + 41;
    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    private const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_LBUTTONDBLCLK = 0x0203;

    private static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private static int _nextId = 1;

    private readonly HwndSource _window;
    private readonly int _id = _nextId++;
    private IntPtr _icon;
    private string _tip = "Silica";
    private bool _added;

    public event Action? Click;
    public event Action? RightClick;

    public TrayIcon()
    {
        _window = new HwndSource(new HwndSourceParameters("SilicaTray") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) });
        _window.AddHook(WndProc);
    }

    public IntPtr Handle => _window.Handle;

    public void Update(BitmapSource image, string tooltip)
    {
        var old = _icon;
        _icon = CreateIcon(image);
        _tip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        Apply(_added ? NIM_MODIFY : NIM_ADD);
        _added = true;
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    private void Apply(int message)
    {
        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _window.Handle,
            uID = _id,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_APP_TRAY,
            hIcon = _icon,
            szTip = _tip
        };
        Shell_NotifyIcon(message, ref data);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_APP_TRAY)
        {
            switch ((int)lParam & 0xFFFF)
            {
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    Click?.Invoke();
                    break;
                case WM_RBUTTONUP:
                    // The menu would not close on outside clicks unless our window is foreground.
                    SetForegroundWindow(_window.Handle);
                    RightClick?.Invoke();
                    break;
            }
            handled = true;
        }
        else if (msg == TaskbarCreated && _added)
        {
            Apply(NIM_ADD);
        }
        return IntPtr.Zero;
    }

    /// <summary>Icon size Windows uses in the notification area at the current DPI.</summary>
    public static int IconSize => Math.Max(16, GetSystemMetrics(49));

    /// <summary>Renders a short value ("54", "1.2") as a crisp tray icon.</summary>
    public static BitmapSource RenderText(string text, Color background, Color foreground)
    {
        var size = IconSize;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(background), null, new Rect(0, 0, size, size), size * 0.2, size * 0.2);
            var fontSize = size * (text.Length <= 2 ? 0.68 : text.Length == 3 ? 0.56 : 0.44);
            var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Condensed),
                fontSize, new SolidColorBrush(foreground), 1.0);
            dc.DrawText(ft, new Point((size - ft.Width) / 2, (size - ft.Height) / 2));
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        RenderOptions.SetClearTypeHint(visual, ClearTypeHint.Auto);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    public static BitmapSource FromIcon(Uri uri)
    {
        var decoder = BitmapDecoder.Create(uri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var size = IconSize;
        return decoder.Frames.OrderBy(f => Math.Abs(f.PixelWidth - size)).First();
    }

    private static IntPtr CreateIcon(BitmapSource source)
    {
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var pixels = new byte[w * h * 4];
        bgra.CopyPixels(pixels, w * 4, 0);
        var color = CreateBitmap(w, h, 1, 32, pixels);
        var mask = CreateBitmap(w, h, 1, 1, new byte[(w + 15) / 16 * 2 * h]);
        var info = new ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
        var icon = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);
        return icon;
    }

    public void Dispose()
    {
        if (_added) Apply(NIM_DELETE);
        _added = false;
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        _icon = IntPtr.Zero;
        _window.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO info);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, byte[] bits);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
}

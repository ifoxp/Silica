using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Silica.Localization;
using Silica.Services;

namespace Silica.Views;

public sealed class MonitorTestView : UserControl, INavigable
{
    public static readonly (string Title, string Hint)[] Patterns =
    [
        ("Black", "Bright dots are stuck pixels; look for backlight bleed at the edges"),
        ("White", "Dark dots are dead pixels"),
        ("Red", "Every pixel should be the same pure red"),
        ("Green", "Every pixel should be the same pure green"),
        ("Blue", "Every pixel should be the same pure blue"),
        ("Gray 25%", "Uniformity: brightness should be even across the screen"),
        ("Gray gradient", "Smooth from black to white without visible bands"),
        ("Color gradients", "Smooth red, green and blue ramps without steps"),
        ("Near black", "You should tell apart the darkest squares"),
        ("Near white", "You should tell apart the brightest squares"),
        ("Geometry", "Circles must be round and lines straight"),
        ("Sharpness", "A fine checkerboard should look like even gray, not stripes"),
        ("Text", "Small text should be crisp at every size"),
        ("Motion", "The moving block should have clean edges without trails")
    ];

    public MonitorTestView()
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

        var header = new Grid { Margin = new Thickness(28, 22, 28, 14) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Background = R<Brush>("Brush.AccentSubtle"), Margin = new Thickness(0, 0, 14, 0),
            Child = new TextBlock { Style = R<Style>("Icon"), Text = "", FontSize = 18, Foreground = R<Brush>("Brush.AccentText") }
        });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Style = R<Style>("Text.Title"), Text = loc.T("Monitor test") });
        titles.Children.Add(new TextBlock { Style = R<Style>("Text.Caption"), Text = loc.T("Full-screen patterns for dead pixels, uniformity, gradients and sharpness"), Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        var start = new Button { Style = R<Style>("Button.Accent"), Content = loc.T("Start test"), MinWidth = 120, VerticalAlignment = VerticalAlignment.Center };
        start.Click += (_, _) => new MonitorTestWindow(Window.GetWindow(this), 0).Show();
        Grid.SetColumn(start, 2);
        header.Children.Add(start);

        var wrap = new WrapPanel();
        for (var i = 0; i < Patterns.Length; i++)
        {
            var (title, hint) = Patterns[i];
            var index = i;
            var preview = new Border { Height = 70, CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = MonitorTestWindow.Pattern(i, 220, 70, preview: true), BorderBrush = R<Brush>("Brush.Stroke"), BorderThickness = new Thickness(1) };
            var stack = new StackPanel();
            stack.Children.Add(preview);
            stack.Children.Add(new TextBlock { Style = R<Style>("Text.Section"), Text = loc.T(title), Margin = new Thickness(0, 10, 0, 2) });
            stack.Children.Add(new TextBlock { Style = R<Style>("Text.Caption"), Text = loc.T(hint), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None });
            var card = new Border { Style = R<Style>("Card"), Width = 252, Margin = new Thickness(0, 0, 14, 14), Padding = new Thickness(14), Child = stack, Cursor = Cursors.Hand };
            card.MouseLeftButtonUp += (_, _) => new MonitorTestWindow(Window.GetWindow(this), index).Show();
            card.MouseEnter += (_, _) => card.BorderBrush = R<Brush>("Brush.Accent");
            card.MouseLeave += (_, _) => card.ClearValue(Border.BorderBrushProperty);
            wrap.Children.Add(card);
        }

        var body = new StackPanel { Margin = new Thickness(28, 0, 14, 28) };
        body.Children.Add(new TextBlock
        {
            Style = R<Style>("Text.Secondary"), Margin = new Thickness(0, 0, 0, 16), TextWrapping = TextWrapping.Wrap,
            Text = loc.T("The test opens on the monitor that shows Silica. Use the arrow keys, Space or a click to switch patterns, Esc to leave.")
        });
        body.Children.Add(wrap);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(header);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body, Focusable = false };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        return root;
    }
}

public sealed class MonitorTestWindow : Window
{
    private readonly Grid _root = new() { Background = Brushes.Black };
    private readonly Border _hint;
    private readonly TextBlock _hintTitle = new() { FontSize = 16, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _hintText = new() { FontSize = 13, Margin = new Thickness(0, 4, 0, 0) };
    private int _index;

    public MonitorTestWindow(Window? owner, int start)
    {
        _index = start;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        Background = Brushes.Black;
        Cursor = Cursors.None;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        FontFamily = (FontFamily)FindResource("Font.UI");
        if (owner != null)
        {
            // Open on the same monitor, then maximize there.
            Left = owner.Left + 40;
            Top = owner.Top + 40;
            Width = 400;
            Height = 300;
        }
        WindowStartupLocation = WindowStartupLocation.Manual;
        Loaded += (_, _) => { WindowState = WindowState.Maximized; Render(); };

        var hintStack = new StackPanel();
        hintStack.Children.Add(_hintTitle);
        hintStack.Children.Add(_hintText);
        hintStack.Children.Add(new TextBlock { FontSize = 12, Margin = new Thickness(0, 8, 0, 0), Opacity = 0.7, Text = Loc.Instance.T("Arrow keys or click: next pattern    Esc: exit") });
        _hint = new Border
        {
            Child = hintStack, Background = new SolidColorBrush(Color.FromArgb(220, 20, 22, 26)), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 12, 18, 12), Margin = new Thickness(0, 0, 0, 48),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false
        };
        TextElement(_hintTitle);
        TextElement(_hintText);
        Content = _root;
        KeyDown += OnKey;
        MouseLeftButtonUp += (_, _) => Step(1);
        MouseRightButtonUp += (_, _) => Step(-1);
        MouseMove += (_, _) => ShowHint();
    }

    private static void TextElement(TextBlock t) => t.Foreground = Brushes.White;

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Right or Key.Down or Key.Space or Key.Enter or Key.PageDown: Step(1); break;
            case Key.Left or Key.Up or Key.PageUp or Key.Back: Step(-1); break;
            case Key.H: ShowHint(); break;
        }
    }

    private void Step(int delta)
    {
        _index = (_index + delta + MonitorTestView.Patterns.Length) % MonitorTestView.Patterns.Length;
        Render();
    }

    private void Render()
    {
        _root.Children.Clear();
        _root.Children.Add(Pattern(_index, ActualWidth, ActualHeight, preview: false));
        _root.Children.Add(_hint);
        var (title, hint) = MonitorTestView.Patterns[_index];
        _hintTitle.Text = $"{_index + 1} / {MonitorTestView.Patterns.Length}   {Loc.Instance.T(title)}";
        _hintText.Text = Loc.Instance.T(hint);
        ShowHint();
    }

    private void ShowHint()
    {
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.2))));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.8))));
        _hint.BeginAnimation(OpacityProperty, anim);
    }

    public static FrameworkElement Pattern(int index, double width, double height, bool preview)
    {
        Brush Solid(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));
        switch (index)
        {
            case 0: return new Border { Background = Brushes.Black };
            case 1: return new Border { Background = Brushes.White };
            case 2: return new Border { Background = Solid(255, 0, 0) };
            case 3: return new Border { Background = Solid(0, 255, 0) };
            case 4: return new Border { Background = Solid(0, 0, 255) };
            case 5: return new Border { Background = Solid(64, 64, 64) };
            case 6: return new Border { Background = new LinearGradientBrush(Colors.Black, Colors.White, 0) };
            case 7:
            {
                var g = new UniformGrid { Rows = 4 };
                foreach (var c in new[] { Colors.Red, Color.FromRgb(0, 255, 0), Colors.Blue, Colors.White })
                    g.Children.Add(new Border { Background = new LinearGradientBrush(Colors.Black, c, 0) });
                return g;
            }
            case 8: return Steps(0, 2, preview);
            case 9: return Steps(255 - 22, 2, preview);
            case 10: return Geometry(width, height, preview);
            case 11:
            {
                var brush = new DrawingBrush(new GeometryDrawing(Brushes.White, null,
                    new GeometryGroup { Children = { new RectangleGeometry(new Rect(0, 0, 1, 1)), new RectangleGeometry(new Rect(1, 1, 1, 1)) } }))
                {
                    TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 2, 2), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None
                };
                RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
                var border = new Border { Background = Brushes.Black };
                border.Child = new Border { Background = brush };
                return border;
            }
            case 12: return TextSample(preview);
            default: return Motion(preview);
        }
    }

    private static FrameworkElement Steps(int start, int step, bool preview)
    {
        var g = new UniformGrid { Columns = 12, Rows = 1, Background = start == 0 ? Brushes.Black : Brushes.White };
        for (var i = 0; i < 12; i++)
        {
            var v = (byte)Math.Clamp(start + i * step, 0, 255);
            var cell = new Border { Background = new SolidColorBrush(Color.FromRgb(v, v, v)), Margin = new Thickness(preview ? 1 : 6) };
            if (!preview)
                cell.Child = new TextBlock
                {
                    Text = v.ToString(CultureInfo.InvariantCulture), FontSize = 12, Opacity = 0.6,
                    Foreground = v < 128 ? Brushes.Gray : Brushes.DimGray, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 12)
                };
            g.Children.Add(cell);
        }
        return g;
    }

    private static FrameworkElement Geometry(double width, double height, bool preview)
    {
        var canvas = new Canvas { Background = Brushes.Black, ClipToBounds = true };
        var pen = Brushes.White;
        var spacing = preview ? 10 : 50;
        for (double x = 0; x <= width; x += spacing) canvas.Children.Add(new System.Windows.Shapes.Line { X1 = x, X2 = x, Y1 = 0, Y2 = height, Stroke = Brushes.DimGray, StrokeThickness = 1 });
        for (double y = 0; y <= height; y += spacing) canvas.Children.Add(new System.Windows.Shapes.Line { X1 = 0, X2 = width, Y1 = y, Y2 = y, Stroke = Brushes.DimGray, StrokeThickness = 1 });
        var r = Math.Min(width, height) * 0.45;
        canvas.Children.Add(Circle(width / 2, height / 2, r, pen));
        canvas.Children.Add(Circle(width / 2, height / 2, r / 2, pen));
        var c = Math.Min(width, height) * 0.12;
        foreach (var (cx, cy) in new[] { (c, c), (width - c, c), (c, height - c), (width - c, height - c) })
            canvas.Children.Add(Circle(cx, cy, c * 0.8, pen));
        canvas.Children.Add(new System.Windows.Shapes.Line { X1 = 0, Y1 = 0, X2 = width, Y2 = height, Stroke = pen, StrokeThickness = 1 });
        canvas.Children.Add(new System.Windows.Shapes.Line { X1 = width, Y1 = 0, X2 = 0, Y2 = height, Stroke = pen, StrokeThickness = 1 });
        return canvas;
    }

    private static System.Windows.Shapes.Ellipse Circle(double cx, double cy, double r, Brush stroke)
    {
        var e = new System.Windows.Shapes.Ellipse { Width = r * 2, Height = r * 2, Stroke = stroke, StrokeThickness = 1.5 };
        Canvas.SetLeft(e, cx - r);
        Canvas.SetTop(e, cy - r);
        return e;
    }

    private static FrameworkElement TextSample(bool preview)
    {
        var stack = new StackPanel { Margin = new Thickness(preview ? 6 : 60), Background = Brushes.White };
        var sizes = preview ? new[] { 7.0, 9, 11 } : new[] { 8.0, 9, 10, 11, 12, 14, 16, 20, 28, 40 };
        foreach (var size in sizes)
            stack.Children.Add(new TextBlock
            {
                Text = $"{size:0} px   The quick brown fox jumps over the lazy dog   Швидка бура лисиця перестрибує через ледачого пса",
                FontSize = size, Foreground = Brushes.Black, Margin = new Thickness(0, 0, 0, preview ? 1 : 10)
            });
        return new Border { Background = Brushes.White, Child = stack };
    }

    private static FrameworkElement Motion(bool preview)
    {
        var canvas = new Canvas { Background = Brushes.Black, ClipToBounds = true };
        var block = new Border { Width = preview ? 24 : 160, Height = preview ? 24 : 160, Background = Brushes.White };
        canvas.Children.Add(block);
        if (preview)
        {
            Canvas.SetLeft(block, 40);
            Canvas.SetTop(block, 23);
            return canvas;
        }
        canvas.Loaded += (_, _) =>
        {
            Canvas.SetTop(block, (canvas.ActualHeight - block.Height) / 2);
            var anim = new DoubleAnimation(-block.Width, canvas.ActualWidth, TimeSpan.FromSeconds(2.5)) { RepeatBehavior = RepeatBehavior.Forever };
            block.BeginAnimation(Canvas.LeftProperty, anim);
        };
        return canvas;
    }
}

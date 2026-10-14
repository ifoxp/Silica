using System.Windows;
using System.Windows.Media;
using Silica.Services;
using Silica.Core.Sensors;

namespace Silica.Controls;

/// <summary>Tiny area chart of a sensor's recent history. Call <see cref="Refresh"/> after each poll.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ReadingProperty = DependencyProperty.Register(
        nameof(Reading), typeof(SensorReading), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((Sparkline)d).Refresh()));

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(Color?), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(int), typeof(Sparkline), new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FixedMaxProperty = DependencyProperty.Register(
        nameof(FixedMax), typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(bool), typeof(Sparkline), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(int), typeof(Sparkline), new FrameworkPropertyMetadata(0, (d, _) => ((Sparkline)d).Refresh()));

    private float[] _buffer = new float[60];

    public Sparkline() => FlowDirection = FlowDirection.LeftToRight;
    private int _count;

    public SensorReading? Reading
    {
        get => (SensorReading?)GetValue(ReadingProperty);
        set => SetValue(ReadingProperty, value);
    }

    /// <summary>Line colour; defaults to the accent.</summary>
    public Color? Color
    {
        get => (Color?)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public int Samples
    {
        get => (int)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    /// <summary>Upper bound of the scale (100 for percentages). NaN = auto.</summary>
    public double FixedMax
    {
        get => (double)GetValue(FixedMaxProperty);
        set => SetValue(FixedMaxProperty, value);
    }

    /// <summary>Bind to a counter that changes after every poll to redraw from XAML.</summary>
    public int Version
    {
        get => (int)GetValue(VersionProperty);
        set => SetValue(VersionProperty, value);
    }

    public bool Fill
    {
        get => (bool)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public void Refresh()
    {
        if (_buffer.Length != Samples) _buffer = new float[Samples];
        _count = Reading?.History.CopyLatest(_buffer) ?? 0;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 2 || h < 2) return;
        if (Fill) dc.DrawRectangle(ThemeService.BrushOf("Brush.Track"), null, new Rect(0, Math.Floor(h) - 1, w, 1));
        if (_count < 2) return;

        double min = double.MaxValue, max = double.MinValue;
        for (var i = 0; i < _count; i++)
        {
            min = Math.Min(min, _buffer[i]);
            max = Math.Max(max, _buffer[i]);
        }
        if (!double.IsNaN(FixedMax)) { min = 0; max = Math.Max(FixedMax, max); }
        else
        {
            // Keep flat lines in the middle instead of glued to an edge.
            var pad = Math.Max((max - min) * 0.15, Math.Abs(max) * 0.02 + 0.5);
            min -= pad;
            max += pad;
        }
        var range = Math.Max(max - min, 1e-6);

        var step = w / (Samples - 1);
        var x0 = w - step * (_count - 1);
        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            for (var i = 0; i < _count; i++)
            {
                var p = new Point(x0 + i * step, h - 1 - (_buffer[i] - min) / range * (h - 2));
                if (i == 0) ctx.BeginFigure(p, false, false);
                else ctx.LineTo(p, true, false);
            }
        }
        line.Freeze();

        var color = Color ?? ThemeService.Accent;
        if (Fill)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(x0, h), true, true);
                for (var i = 0; i < _count; i++)
                    ctx.LineTo(new Point(x0 + i * step, h - 1 - (_buffer[i] - min) / range * (h - 2)), false, false);
                ctx.LineTo(new Point(w, h), false, false);
            }
            area.Freeze();
            var fill = new LinearGradientBrush(
                System.Windows.Media.Color.FromArgb(70, color.R, color.G, color.B),
                System.Windows.Media.Color.FromArgb(0, color.R, color.G, color.B), 90);
            fill.Freeze();
            dc.DrawGeometry(fill, null, area);
        }

        var pen = new Pen(new SolidColorBrush(color), 1.5) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }
}

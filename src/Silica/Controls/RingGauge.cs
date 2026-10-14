using System.Windows;
using System.Windows.Media;
using Silica.Services;

namespace Silica.Controls;

/// <summary>Open ring (270°) showing a 0..1 value. Text goes on top of it in XAML.</summary>
public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(Color?), typeof(RingGauge), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double Sweep = 270;
    private const double StartAngle = 135;

    public RingGauge()
    {
        Loaded += (_, _) => ThemeService.Changed += InvalidateVisual;
        Unloaded += (_, _) => ThemeService.Changed -= InvalidateVisual;
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public Color? Color
    {
        get => (Color?)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < Thickness * 2) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = (size - Thickness) / 2;

        var track = new Pen(ThemeService.BrushOf("Brush.Track"), Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, track, Arc(center, radius, StartAngle, Sweep));

        var v = Math.Clamp(double.IsNaN(Value) ? 0 : Value, 0, 1);
        if (v <= 0.002) return;
        var c = Color ?? ThemeService.Accent;
        var pen = new Pen(new SolidColorBrush(c), Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, pen, Arc(center, radius, StartAngle, Sweep * v));
    }

    private static Geometry Arc(Point c, double r, double start, double sweep)
    {
        static Point At(Point c, double r, double deg)
        {
            var rad = deg * Math.PI / 180;
            return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
        }

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(At(c, r, start), false, false);
            ctx.ArcTo(At(c, r, start + sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }
}

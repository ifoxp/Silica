using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Silica.Localization;
using Silica.Services;
using Silica.Core.Sensors;

namespace Silica.Controls;

public sealed record ChartSeries(SensorReading Reading, Color Color, string Label);

/// <summary>
/// Time chart for sensors of one kind (one unit on the Y axis). Hovering shows a crosshair with values.
/// </summary>
public sealed class LineChart : FrameworkElement
{
    private const double AxisWidth = 52;
    private const double BottomHeight = 20;
    private const double TopPad = 8;

    private readonly List<(ChartSeries Series, float[] Data, int Count)> _data = [];
    private IReadOnlyList<ChartSeries> _series = [];
    private double? _hoverX;
    private double _intervalSeconds = 1;

    public LineChart()
    {
        Loaded += (_, _) => ThemeService.Changed += InvalidateVisual;
        Unloaded += (_, _) => ThemeService.Changed -= InvalidateVisual;
        ClipToBounds = true;
        FlowDirection = FlowDirection.LeftToRight;
    }

    /// <summary>How many samples fit across the chart.</summary>
    public int Window { get; set; } = 120;

    /// <summary>Optional fixed range (e.g. 0..100 for load).</summary>
    public double? FixedMin { get; set; }
    public double? FixedMax { get; set; }

    public IReadOnlyList<ChartSeries> Series
    {
        get => _series;
        set
        {
            _series = value;
            Refresh();
        }
    }

    public void Refresh(double intervalSeconds = 1)
    {
        _intervalSeconds = intervalSeconds;
        _data.Clear();
        foreach (var s in _series)
        {
            var buf = new float[Window];
            var n = s.Reading.History.CopyLatest(buf);
            _data.Add((s, buf, n));
        }
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _hoverX = e.GetPosition(this).X;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hoverX = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var plot = new Rect(AxisWidth, TopPad, Math.Max(0, w - AxisWidth - 6), Math.Max(0, h - BottomHeight - TopPad));
        if (plot.Width < 20 || plot.Height < 20) return;

        var kind = _series.Count > 0 ? _series[0].Reading.Kind : SensorKind.Other;
        if (_data.All(d => d.Count < 2))
        {
            var hint = new FormattedText(Loc.Instance.T("Collecting data..."), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 12, ThemeService.BrushOf("Brush.Text3"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(hint, new Point(plot.Left + (plot.Width - hint.Width) / 2, plot.Top + (plot.Height - hint.Height) / 2));
            return;
        }
        var (min, max) = Range(kind);
        var ticks = NiceTicks(min, max, Math.Max(2, (int)(plot.Height / 42)));
        min = ticks[0];
        max = ticks[^1];
        var range = Math.Max(max - min, 1e-9);

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var gridPen = new Pen(ThemeService.BrushOf("Brush.Grid"), 1);
        var text3 = ThemeService.BrushOf("Brush.Text3");
        var face = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        foreach (var t in ticks)
        {
            var y = Math.Round(plot.Bottom - (t - min) / range * plot.Height) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = new FormattedText(AxisLabel(kind, t), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 11, text3, dpi);
            dc.DrawText(label, new Point(plot.Left - 8 - label.Width, y - label.Height / 2));
        }

        var seconds = Window * _intervalSeconds;
        for (var i = 0; i <= 4; i++)
        {
            var x = plot.Left + plot.Width * i / 4;
            var ago = seconds * (4 - i) / 4;
            var caption = i == 4 ? Loc.Instance.T("now") : "-" + FormatAgo(ago);
            var ft = new FormattedText(caption, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 11, text3, dpi);
            var tx = i == 0 ? x : i == 4 ? x - ft.Width : x - ft.Width / 2;
            dc.DrawText(ft, new Point(tx, plot.Bottom + 4));
        }

        var step = plot.Width / (Window - 1);
        dc.PushClip(new RectangleGeometry(new Rect(plot.Left - 2, plot.Top - 2, plot.Width + 4, plot.Height + 4)));
        foreach (var (series, data, count) in _data)
        {
            if (count < 2) continue;
            var x0 = plot.Right - step * (count - 1);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                for (var i = 0; i < count; i++)
                {
                    var v = SensorFormat.Display(kind, data[i]);
                    var p = new Point(x0 + i * step, plot.Bottom - (v - min) / range * plot.Height);
                    if (i == 0) ctx.BeginFigure(p, false, false);
                    else ctx.LineTo(p, true, true);
                }
            }
            g.Freeze();
            var pen = new Pen(new SolidColorBrush(series.Color), 1.8) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            dc.DrawGeometry(null, pen, g);
        }
        dc.Pop();

        if (_hoverX is { } hx && hx >= plot.Left && hx <= plot.Right && _data.Count > 0)
            DrawHover(dc, plot, hx, step, min, range, kind, face, dpi);
    }

    private void DrawHover(DrawingContext dc, Rect plot, double hx, double step, double min, double range, SensorKind kind, Typeface face, double dpi)
    {
        var fromRight = (int)Math.Round((plot.Right - hx) / step);
        var x = plot.Right - fromRight * step;
        dc.DrawLine(new Pen(ThemeService.BrushOf("Brush.StrokeStrong"), 1), new Point(x, plot.Top), new Point(x, plot.Bottom));

        var lines = new List<(Color Color, string Text)>();
        foreach (var (series, data, count) in _data)
        {
            var idx = count - 1 - fromRight;
            if (idx < 0 || idx >= count) continue;
            var v = SensorFormat.Display(kind, data[idx]);
            var y = plot.Bottom - (v - min) / range * plot.Height;
            dc.DrawEllipse(new SolidColorBrush(series.Color), new Pen(ThemeService.BrushOf("Brush.Surface"), 2), new Point(x, y), 4, 4);
            lines.Add((series.Color, $"{series.Label}  {SensorFormat.Format(kind, data[idx])}"));
        }
        if (lines.Count == 0) return;

        var text = ThemeService.BrushOf("Brush.Text");
        var texts = lines.Select(l => new FormattedText(l.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 12, text, dpi)).ToList();
        var boxW = texts.Max(t => t.Width) + 30;
        var boxH = texts.Sum(t => t.Height + 3) + 14;
        var bx = x + 12 + boxW > plot.Right ? x - 12 - boxW : x + 12;
        var box = new Rect(bx, plot.Top + 6, boxW, boxH);
        dc.DrawRoundedRectangle(ThemeService.BrushOf("Brush.Tooltip"), new Pen(ThemeService.BrushOf("Brush.StrokeStrong"), 1), box, 6, 6);
        var ty = box.Top + 7;
        for (var i = 0; i < texts.Count; i++)
        {
            dc.DrawEllipse(new SolidColorBrush(lines[i].Color), null, new Point(box.Left + 12, ty + texts[i].Height / 2), 3.5, 3.5);
            dc.DrawText(texts[i], new Point(box.Left + 22, ty));
            ty += texts[i].Height + 3;
        }
    }

    private (double Min, double Max) Range(SensorKind kind)
    {
        double min = double.MaxValue, max = double.MinValue;
        foreach (var (_, data, count) in _data)
            for (var i = 0; i < count; i++)
            {
                var v = SensorFormat.Display(kind, data[i]);
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }
        if (min > max) { min = 0; max = 1; }
        if (FixedMin is { } fmin) min = Math.Min(fmin, min);
        if (FixedMax is { } fmax) max = Math.Max(fmax, max);
        if (max - min < 1e-6) { min -= 1; max += 1; }
        // Temperatures and clocks read better with headroom instead of touching the edges.
        if (FixedMin == null) min -= (max - min) * 0.08;
        if (FixedMax == null) max += (max - min) * 0.08;
        if (FixedMin == null && min < 0 && kind is not SensorKind.Temperature and not SensorKind.Current and not SensorKind.Power) min = 0;
        return (min, max);
    }

    private static double[] NiceTicks(double min, double max, int maxTicks)
    {
        var raw = (max - min) / maxTicks;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var norm = raw / mag;
        var step = (norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 2.5 ? 2.5 : norm <= 5 ? 5 : 10) * mag;
        var start = Math.Floor(min / step) * step;
        var end = Math.Ceiling(max / step) * step;
        var list = new List<double>();
        for (var v = start; v <= end + step * 0.5; v += step) list.Add(Math.Round(v, 10));
        return [.. list];
    }

    private static string AxisLabel(SensorKind kind, double v)
    {
        var c = CultureInfo.CurrentCulture;
        return kind switch
        {
            SensorKind.Throughput => v >= 1048576 ? (v / 1048576).ToString("0.#", c) + "M" : v >= 1024 ? (v / 1024).ToString("0", c) + "K" : v.ToString("0", c),
            SensorKind.Voltage => v.ToString("0.00", c),
            SensorKind.Clock when v >= 1000 => (v / 1000).ToString("0.0", c) + "G",
            _ => Math.Abs(v) >= 100 || v == Math.Floor(v) ? v.ToString("0", c) : v.ToString("0.#", c)
        } + (kind is SensorKind.Temperature ? "°" : kind is SensorKind.Load or SensorKind.Control or SensorKind.Level ? "%" : "");
    }

    private static string FormatAgo(double seconds) => seconds >= 60 && Math.Abs(seconds / 60 - Math.Round(seconds / 60)) < 0.01
        ? Loc.Instance.F("{0} min", Math.Round(seconds / 60))
        : seconds >= 120
            ? Loc.Instance.F("{0} min", (seconds / 60).ToString("0.#", CultureInfo.CurrentCulture))
            : Loc.Instance.F("{0} s", Math.Round(seconds));
}

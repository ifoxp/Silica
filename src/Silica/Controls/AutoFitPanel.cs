using System.Windows;
using System.Windows.Controls;

namespace Silica.Controls;

/// <summary>
/// Lays children out in as many equal columns as fit (like CSS "repeat(auto-fit, minmax(MinItemWidth, 1fr))").
/// Items in a row share the tallest height, so cards line up; narrow windows simply get fewer columns.
/// </summary>
public sealed class AutoFitPanel : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(AutoFitPanel), new FrameworkPropertyMetadata(300.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(AutoFitPanel), new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(AutoFitPanel), new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty StretchRowsProperty = DependencyProperty.Register(
        nameof(StretchRows), typeof(bool), typeof(AutoFitPanel), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private readonly List<double> _rowHeights = [];
    private int _columns = 1;

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>When true, every item in a row gets the row's height.</summary>
    public bool StretchRows
    {
        get => (bool)GetValue(StretchRowsProperty);
        set => SetValue(StretchRowsProperty, value);
    }

    protected override Size MeasureOverride(Size available)
    {
        var visible = InternalChildren.OfType<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        var width = double.IsInfinity(available.Width) ? MinItemWidth * Math.Max(1, MaxColumns) : available.Width;
        _columns = Math.Clamp((int)((width + Spacing) / (MinItemWidth + Spacing)), 1, Math.Max(1, Math.Min(MaxColumns, visible.Count)));
        var itemWidth = Math.Max(0, (width - Spacing * (_columns - 1)) / _columns);

        _rowHeights.Clear();
        for (var i = 0; i < visible.Count; i++)
        {
            visible[i].Measure(new Size(itemWidth, double.PositiveInfinity));
            var row = i / _columns;
            if (_rowHeights.Count <= row) _rowHeights.Add(0);
            _rowHeights[row] = Math.Max(_rowHeights[row], visible[i].DesiredSize.Height);
        }
        var height = _rowHeights.Sum() + Spacing * Math.Max(0, _rowHeights.Count - 1);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var visible = InternalChildren.OfType<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        var itemWidth = Math.Max(0, (final.Width - Spacing * (_columns - 1)) / _columns);
        double y = 0;
        for (var i = 0; i < visible.Count; i++)
        {
            var row = i / _columns;
            var col = i % _columns;
            if (col == 0 && row > 0) y += _rowHeights[row - 1] + Spacing;
            var h = StretchRows ? _rowHeights[row] : visible[i].DesiredSize.Height;
            visible[i].Arrange(new Rect(col * (itemWidth + Spacing), y, itemWidth, h));
        }
        return final;
    }
}

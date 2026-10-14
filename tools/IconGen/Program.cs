using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Draws the Silica mark (a quartz crystal) and writes a multi-size .ico plus a PNG preview.
// Usage: IconGen <output folder>

var output = args.Length > 0 ? args[0] : ".";
Directory.CreateDirectory(output);
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
var pngs = new List<byte[]>();

var thread = new Thread(() =>
{
    foreach (var size in sizes) pngs.Add(Png(Render(size)));
    File.WriteAllBytes(Path.Combine(output, "Silica.png"), Png(Render(512)));
    if (args.Length > 1) File.WriteAllBytes(args[1], Png(Strip(sizes)));
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();

using (var ico = new BinaryWriter(File.Create(Path.Combine(output, "Silica.ico"))))
{
    ico.Write((short)0);
    ico.Write((short)1);
    ico.Write((short)sizes.Length);
    var offset = 6 + 16 * sizes.Length;
    for (var i = 0; i < sizes.Length; i++)
    {
        ico.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        ico.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        ico.Write((byte)0);
        ico.Write((byte)0);
        ico.Write((short)1);
        ico.Write((short)32);
        ico.Write(pngs[i].Length);
        ico.Write(offset);
        offset += pngs[i].Length;
    }
    foreach (var p in pngs) ico.Write(p);
}
Console.WriteLine($"Written to {Path.GetFullPath(output)}");

static BitmapSource Render(int size)
{
    var s = size / 256.0;
    Point P(double x, double y) => new(x * s, y * s);

    // Small sizes get a slightly wider, shorter crystal so the facets stay readable.
    var small = size <= 24;
    var top = small ? 6 : 4;
    var shoulder = small ? 92 : 96;
    var front = small ? 118 : 122;
    var left = small ? 36 : 50;
    var right = 256 - left;
    var bottom = small ? 206 : 200;
    var tip = small ? 250 : 252;

    var T = P(128, top);
    var L = P(left, shoulder);
    var C = P(128, front);
    var R = P(right, shoulder);
    var BL = P(left, bottom);
    var BC = P(128, tip);
    var BR = P(right, bottom);

    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        Face(dc, [T, L, C], Lin("#B9F0FF", "#7CCBFF", T, C));
        Face(dc, [T, C, R], Lin("#73BEFF", "#4F8DFF", T, R));
        Face(dc, [L, C, BC, BL], Lin("#5A9BFF", "#3A63E8", L, BC));
        Face(dc, [C, R, BR, BC], Lin("#3557DA", "#5A3FD1", C, BR));

        if (size >= 32)
        {
            var edge = new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), Math.Max(1, 2.2 * s));
            dc.DrawLine(edge, C, BC);
            dc.DrawLine(edge, L, C);
            dc.DrawLine(edge, C, R);
        }
    }

    var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bmp.Render(visual);
    return bmp;
}

static BitmapSource Strip(int[] sizes)
{
    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0D, 0x0F, 0x13)), null, new Rect(0, 0, 900, 150));
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF7)), null, new Rect(0, 150, 900, 150));
        foreach (var row in new[] { 0, 150 })
        {
            double x = 10;
            foreach (var sz in sizes.Where(z => z <= 128))
            {
                var img = Render(sz);
                dc.DrawImage(img, new Rect(x, row + 10, sz, sz));
                x += sz + 14;
            }
        }
    }
    var bmp = new RenderTargetBitmap(900, 300, 96, 96, PixelFormats.Pbgra32);
    bmp.Render(visual);
    return bmp;
}

static void Face(DrawingContext dc, Point[] points, Brush brush)
{
    var g = new StreamGeometry();
    using (var ctx = g.Open())
    {
        ctx.BeginFigure(points[0], true, true);
        for (var i = 1; i < points.Length; i++) ctx.LineTo(points[i], true, true);
    }
    // A hairline of the same brush closes the anti-aliasing seams between facets.
    dc.DrawGeometry(brush, new Pen(brush, 0.6), g);
}

static Brush Lin(string from, string to, Point a, Point b) => new LinearGradientBrush(
    (Color)ColorConverter.ConvertFromString(from), (Color)ColorConverter.ConvertFromString(to), a, b) { MappingMode = BrushMappingMode.Absolute };

static byte[] Png(BitmapSource bmp)
{
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(bmp));
    using var ms = new MemoryStream();
    enc.Save(ms);
    return ms.ToArray();
}

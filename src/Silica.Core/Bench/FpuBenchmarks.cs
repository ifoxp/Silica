using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Silica.Core.Bench;

internal static class Simd
{
    public static ulong Sink;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<double> Mad(Vector256<double> a, Vector256<double> b, Vector256<double> c) =>
        Fma.IsSupported ? Fma.MultiplyAdd(a, b, c) : a * b + c;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<float> Mad(Vector256<float> a, Vector256<float> b, Vector256<float> c) =>
        Fma.IsSupported ? Fma.MultiplyAdd(a, b, c) : a * b + c;

    // 512-bit paths call Vector512.FusedMultiplyAdd directly (accelerated Vector512 implies AVX-512F);
    // a Mad overload for them was not inlined in larger loops and ran an order of magnitude slower.
    public static bool Use512 => Vector512.IsHardwareAccelerated;
    public static bool Use256 => Vector256.IsHardwareAccelerated;
}

/// <summary>Escape-time fractals rendered in horizontal bands that threads take round-robin.</summary>
internal static class Fractal
{
    public const int MaxIter = 256;
    public const int BandRows = 8;

    public static BenchResult Run(IBenchmark b, BenchContext ctx, int width, int height, Func<int, long> renderRow, string details)
    {
        var sw = Stopwatch.StartNew();
        var bands = height / BandRows;
        long next = -1;
        var (pixels, seconds) = BenchRunner.Run(ctx, ctx.Threads, _ => () =>
        {
            var row = (int)(Interlocked.Increment(ref next) % bands) * BandRows;
            long iterations = 0;
            for (var r = row; r < row + BandRows; r++) iterations += renderRow(r);
            Simd.Sink += (ulong)iterations;
            return (long)width * BandRows;
        });
        return BenchRunner.Result(b, ctx.Threads, pixels / seconds / 1e6, sw,
            $"{details}, {width}x{height}, {MaxIter} iterations max, {BenchRunner.Isa}, {BenchRunner.Threads(ctx.Threads)}");
    }
}

public sealed class FpuMandelbrotBenchmark : IBenchmark
{
    private const int Width = 1600, Height = 1200;
    private const double X0 = -2.1, Y0 = -0.975, Step = 2.6 / Width;

    public string Id => "fpu-mandel";
    public string Title => "FP64 Mandelbrot";
    public string Description => "Renders the Mandelbrot set in double precision";
    public BenchKind Kind => BenchKind.Fpu;
    public string Unit => "Mpix/s";
    public bool HigherIsBetter => true;

    public BenchResult Run(BenchContext ctx) =>
        Fractal.Run(this, ctx, Width, Height, Simd.Use512 ? Row512 : Simd.Use256 ? Row256 : RowScalar, "FP64");

    internal static long Row256(int row)
    {
        var ci = Vector256.Create(Y0 + row * Step);
        var four = Vector256.Create(4.0);
        var lanes = Vector256.Create(0.0, 1, 2, 3) * Step;
        var half = Vector256.Create(4 * Step);
        long total = 0;
        for (var x = 0; x < Width; x += 8)
        {
            var cr0 = Vector256.Create(X0 + x * Step) + lanes;
            var cr1 = cr0 + half;
            Vector256<double> zr0 = default, zi0 = default, zr1 = default, zi1 = default;
            Vector256<long> n0 = default, n1 = default;
            for (var i = 0; i < Fractal.MaxIter; i++)
            {
                var rr0 = zr0 * zr0; var ii0 = zi0 * zi0;
                var rr1 = zr1 * zr1; var ii1 = zi1 * zi1;
                var m0 = Vector256.LessThanOrEqual(rr0 + ii0, four);
                var m1 = Vector256.LessThanOrEqual(rr1 + ii1, four);
                if ((m0 | m1).ExtractMostSignificantBits() == 0) break;
                n0 -= m0.AsInt64();
                n1 -= m1.AsInt64();
                zi0 = Simd.Mad(zr0 + zr0, zi0, ci); zr0 = rr0 - ii0 + cr0;
                zi1 = Simd.Mad(zr1 + zr1, zi1, ci); zr1 = rr1 - ii1 + cr1;
            }
            total += Vector256.Sum(n0 + n1);
        }
        return total;
    }

    internal static long Row512(int row)
    {
        var ci = Vector512.Create(Y0 + row * Step);
        var four = Vector512.Create(4.0);
        var lanes = Vector512.Create(0.0, 1, 2, 3, 4, 5, 6, 7) * Step;
        var half = Vector512.Create(8 * Step);
        long total = 0;
        for (var x = 0; x < Width; x += 16)
        {
            var cr0 = Vector512.Create(X0 + x * Step) + lanes;
            var cr1 = cr0 + half;
            Vector512<double> zr0 = default, zi0 = default, zr1 = default, zi1 = default;
            Vector512<long> n0 = default, n1 = default;
            for (var i = 0; i < Fractal.MaxIter; i++)
            {
                var rr0 = zr0 * zr0; var ii0 = zi0 * zi0;
                var rr1 = zr1 * zr1; var ii1 = zi1 * zi1;
                var m0 = Vector512.LessThanOrEqual(rr0 + ii0, four);
                var m1 = Vector512.LessThanOrEqual(rr1 + ii1, four);
                if ((m0 | m1).ExtractMostSignificantBits() == 0) break;
                n0 -= m0.AsInt64();
                n1 -= m1.AsInt64();
                zi0 = Vector512.FusedMultiplyAdd(zr0 + zr0, zi0, ci); zr0 = rr0 - ii0 + cr0;
                zi1 = Vector512.FusedMultiplyAdd(zr1 + zr1, zi1, ci); zr1 = rr1 - ii1 + cr1;
            }
            total += Vector512.Sum(n0 + n1);
        }
        return total;
    }

    internal static long RowScalar(int row)
    {
        var ci = Y0 + row * Step;
        long total = 0;
        for (var x = 0; x < Width; x++)
        {
            var cr = X0 + x * Step;
            double zr = 0, zi = 0;
            var i = 0;
            for (; i < Fractal.MaxIter; i++)
            {
                double rr = zr * zr, ii = zi * zi;
                if (rr + ii > 4) break;
                zi = 2 * zr * zi + ci;
                zr = rr - ii + cr;
            }
            total += i;
        }
        return total;
    }
}

public sealed class FpuJuliaBenchmark : IBenchmark
{
    private const int Width = 1920, Height = 1080;
    private const float X0 = -1.6f, Y0 = -0.9f, Step = 3.2f / Width, Cr = -0.8f, Ci = 0.156f;

    public string Id => "fpu-julia";
    public string Title => "FP32 Julia";
    public string Description => "Renders a Julia set in single precision";
    public BenchKind Kind => BenchKind.Fpu;
    public string Unit => "Mpix/s";
    public bool HigherIsBetter => true;

    public BenchResult Run(BenchContext ctx) =>
        Fractal.Run(this, ctx, Width, Height, Simd.Use512 ? Row512 : Simd.Use256 ? Row256 : RowScalar, "FP32");

    internal static long Row256(int row)
    {
        var cr = Vector256.Create(Cr);
        var ci = Vector256.Create(Ci);
        var four = Vector256.Create(4f);
        var lanes = Vector256.Create(0f, 1, 2, 3, 4, 5, 6, 7) * Step;
        var y = Vector256.Create(Y0 + row * Step);
        long total = 0;
        for (var x = 0; x < Width; x += 16)
        {
            var zr0 = Vector256.Create(X0 + x * Step) + lanes;
            var zr1 = Vector256.Create(X0 + (x + 8) * Step) + lanes;
            Vector256<float> zi0 = y, zi1 = y;
            Vector256<int> n0 = default, n1 = default;
            for (var i = 0; i < Fractal.MaxIter; i++)
            {
                var rr0 = zr0 * zr0; var ii0 = zi0 * zi0;
                var rr1 = zr1 * zr1; var ii1 = zi1 * zi1;
                var m0 = Vector256.LessThanOrEqual(rr0 + ii0, four);
                var m1 = Vector256.LessThanOrEqual(rr1 + ii1, four);
                if ((m0 | m1).ExtractMostSignificantBits() == 0) break;
                n0 -= m0.AsInt32();
                n1 -= m1.AsInt32();
                zi0 = Simd.Mad(zr0 + zr0, zi0, ci); zr0 = rr0 - ii0 + cr;
                zi1 = Simd.Mad(zr1 + zr1, zi1, ci); zr1 = rr1 - ii1 + cr;
            }
            total += Vector256.Sum(n0 + n1);
        }
        return total;
    }

    internal static long Row512(int row)
    {
        var cr = Vector512.Create(Cr);
        var ci = Vector512.Create(Ci);
        var four = Vector512.Create(4f);
        var lanes = Vector512.Create(0f, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15) * Step;
        var y = Vector512.Create(Y0 + row * Step);
        long total = 0;
        for (var x = 0; x < Width; x += 32)
        {
            var zr0 = Vector512.Create(X0 + x * Step) + lanes;
            var zr1 = Vector512.Create(X0 + (x + 16) * Step) + lanes;
            Vector512<float> zi0 = y, zi1 = y;
            Vector512<int> n0 = default, n1 = default;
            for (var i = 0; i < Fractal.MaxIter; i++)
            {
                var rr0 = zr0 * zr0; var ii0 = zi0 * zi0;
                var rr1 = zr1 * zr1; var ii1 = zi1 * zi1;
                var m0 = Vector512.LessThanOrEqual(rr0 + ii0, four);
                var m1 = Vector512.LessThanOrEqual(rr1 + ii1, four);
                if ((m0 | m1).ExtractMostSignificantBits() == 0) break;
                n0 -= m0.AsInt32();
                n1 -= m1.AsInt32();
                zi0 = Vector512.FusedMultiplyAdd(zr0 + zr0, zi0, ci); zr0 = rr0 - ii0 + cr;
                zi1 = Vector512.FusedMultiplyAdd(zr1 + zr1, zi1, ci); zr1 = rr1 - ii1 + cr;
            }
            total += Vector512.Sum(n0 + n1);
        }
        return total;
    }

    internal static long RowScalar(int row)
    {
        var y = Y0 + row * Step;
        long total = 0;
        for (var x = 0; x < Width; x++)
        {
            float zr = X0 + x * Step, zi = y;
            var i = 0;
            for (; i < Fractal.MaxIter; i++)
            {
                float rr = zr * zr, ii = zi * zi;
                if (rr + ii > 4) break;
                zi = 2 * zr * zi + Ci;
                zr = rr - ii + Cr;
            }
            total += i;
        }
        return total;
    }
}

public sealed class FpuNBodyBenchmark : IBenchmark
{
    private const int N = 8192;
    private const int BlockBodies = 32;
    private const double Soft = 1e-4;
    private const double FlopsPerInteraction = 20;

    public string Id => "fpu-nbody";
    public string Title => "FP64 N-Body";
    public string Description => "Simulates gravity between thousands of bodies";
    public BenchKind Kind => BenchKind.Fpu;
    public string Unit => "GFLOPS";
    public bool HigherIsBetter => true;

    public BenchResult Run(BenchContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var rng = new Rng(0xB0D1E5);
        var x = new double[N]; var y = new double[N]; var z = new double[N]; var m = new double[N];
        for (var i = 0; i < N; i++)
        {
            x[i] = rng.NextDouble() * 2 - 1;
            y[i] = rng.NextDouble() * 2 - 1;
            z[i] = rng.NextDouble() * 2 - 1;
            m[i] = (0.5 + rng.NextDouble()) / N;
        }

        long next = -1;
        var blocks = N / BlockBodies;
        var (interactions, seconds) = BenchRunner.Run(ctx, ctx.Threads, _ =>
        {
            var acc = new double[BlockBodies * 3];
            return () =>
            {
                var first = (int)(Interlocked.Increment(ref next) % blocks) * BlockBodies;
                if (Simd.Use512) Forces512(x, y, z, m, first, acc);
                else if (Simd.Use256) Forces256(x, y, z, m, first, acc);
                else ForcesScalar(x, y, z, m, first, acc);
                Simd.Sink += (ulong)BitConverter.DoubleToInt64Bits(acc[0] + acc[^1]);
                return (long)BlockBodies * N;
            };
        });
        var perSecond = interactions / seconds;
        return BenchRunner.Result(this, ctx.Threads, perSecond * FlopsPerInteraction / 1e9, sw,
            $"{N} bodies, {perSecond / 1e9:0.000} G interactions/s, {BenchRunner.Isa}, {BenchRunner.Threads(ctx.Threads)}");
    }

    internal static unsafe void Forces256(double[] x, double[] y, double[] z, double[] m, int first, double[] acc)
    {
        var soft = Vector256.Create(Soft);
        var one = Vector256.Create(1.0);
        fixed (double* px = x, py = y, pz = z, pm = m)
        {
            for (var b = 0; b < BlockBodies; b++)
            {
                var i = first + b;
                var xi = Vector256.Create(px[i]);
                var yi = Vector256.Create(py[i]);
                var zi = Vector256.Create(pz[i]);
                Vector256<double> ax = default, ay = default, az = default;
                for (var j = 0; j < N; j += 4)
                {
                    var dx = Vector256.Load(px + j) - xi;
                    var dy = Vector256.Load(py + j) - yi;
                    var dz = Vector256.Load(pz + j) - zi;
                    var r2 = Simd.Mad(dx, dx, Simd.Mad(dy, dy, Simd.Mad(dz, dz, soft)));
                    var inv = one / Vector256.Sqrt(r2);
                    var s = inv * inv * (inv * Vector256.Load(pm + j));
                    ax = Simd.Mad(dx, s, ax);
                    ay = Simd.Mad(dy, s, ay);
                    az = Simd.Mad(dz, s, az);
                }
                acc[b * 3] = Vector256.Sum(ax);
                acc[b * 3 + 1] = Vector256.Sum(ay);
                acc[b * 3 + 2] = Vector256.Sum(az);
            }
        }
    }

    internal static unsafe void Forces512(double[] x, double[] y, double[] z, double[] m, int first, double[] acc)
    {
        var soft = Vector512.Create(Soft);
        var one = Vector512.Create(1.0);
        fixed (double* px = x, py = y, pz = z, pm = m)
        {
            for (var b = 0; b < BlockBodies; b++)
            {
                var i = first + b;
                var xi = Vector512.Create(px[i]);
                var yi = Vector512.Create(py[i]);
                var zi = Vector512.Create(pz[i]);
                Vector512<double> ax = default, ay = default, az = default;
                for (var j = 0; j < N; j += 8)
                {
                    var dx = Vector512.Load(px + j) - xi;
                    var dy = Vector512.Load(py + j) - yi;
                    var dz = Vector512.Load(pz + j) - zi;
                    var r2 = Vector512.FusedMultiplyAdd(dx, dx, Vector512.FusedMultiplyAdd(dy, dy, Vector512.FusedMultiplyAdd(dz, dz, soft)));
                    var inv = one / Vector512.Sqrt(r2);
                    var s = inv * inv * (inv * Vector512.Load(pm + j));
                    ax = Vector512.FusedMultiplyAdd(dx, s, ax);
                    ay = Vector512.FusedMultiplyAdd(dy, s, ay);
                    az = Vector512.FusedMultiplyAdd(dz, s, az);
                }
                acc[b * 3] = Vector512.Sum(ax);
                acc[b * 3 + 1] = Vector512.Sum(ay);
                acc[b * 3 + 2] = Vector512.Sum(az);
            }
        }
    }

    internal static void ForcesScalar(double[] x, double[] y, double[] z, double[] m, int first, double[] acc)
    {
        for (var b = 0; b < BlockBodies; b++)
        {
            var i = first + b;
            double ax = 0, ay = 0, az = 0;
            for (var j = 0; j < N; j++)
            {
                double dx = x[j] - x[i], dy = y[j] - y[i], dz = z[j] - z[i];
                var inv = 1 / Math.Sqrt(dx * dx + dy * dy + dz * dz + Soft);
                var s = inv * inv * inv * m[j];
                ax += dx * s; ay += dy * s; az += dz * s;
            }
            acc[b * 3] = ax; acc[b * 3 + 1] = ay; acc[b * 3 + 2] = az;
        }
    }
}

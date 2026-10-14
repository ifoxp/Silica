using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using Silica.Core.Bench;

namespace Silica.Core.Stress;

internal static class CpuStress
{
    private const int MixRounds = 1 << 23;
    private const int Seeds = 8;
    private const int HashBytes = 256 << 10;
    private const long Queens12 = 14200;

    // References are computed once, before the load ramps up; every later run must reproduce them bit for bit.
    private static readonly Lazy<(ulong[] Mix, byte[][] Hash)> References = new(() =>
    {
        var mix = new ulong[Seeds];
        var hash = new byte[Seeds][];
        for (var s = 0; s < Seeds; s++)
        {
            mix[s] = Mix((ulong)s, MixRounds);
            hash[s] = SHA256.HashData(HashInput(s));
        }
        return (mix, hash);
    });

    public static void Run(StressEngine engine, int index, CancellationToken token)
    {
        var refs = References.Value;
        var inputs = Enumerable.Range(0, Seeds).Select(HashInput).ToArray();
        var digest = new byte[32];
        for (var k = index; !token.IsCancellationRequested; k++)
        {
            var seed = k / 3 % Seeds;
            switch (k % 3)
            {
                case 0:
                    var mix = Mix((ulong)seed, MixRounds);
                    if (mix != refs.Mix[seed])
                        engine.Fail(StressComponent.Cpu, "CPU integer result mismatch", $"expected {refs.Mix[seed]:X16}, got {mix:X16}");
                    break;
                case 1:
                    var all = (1 << 12) - 1;
                    var n = CpuQueensBenchmark.Solve(all, 0, 0, 0);
                    if (n != Queens12)
                        engine.Fail(StressComponent.Cpu, "CPU integer result mismatch", $"12-queens: expected {Queens12}, got {n}");
                    break;
                default:
                    SHA256.HashData(inputs[seed], digest);
                    if (!digest.AsSpan().SequenceEqual(refs.Hash[seed]))
                        engine.Fail(StressComponent.Cpu, "CPU hash mismatch", $"SHA-256 block {seed}");
                    break;
            }
            engine.Tick();
        }
    }

    private static byte[] HashInput(int seed)
    {
        var data = new byte[HashBytes];
        var rng = new Rng((ulong)seed + 101);
        foreach (ref var b in data.AsSpan()) b = (byte)rng.Next();
        return data;
    }

    /// <summary>Multiply, rotate, popcount, CRC and division mixed into a long dependency chain.</summary>
    internal static ulong Mix(ulong seed, int rounds)
    {
        ulong a = Rng.SplitMix(seed), b = Rng.SplitMix(a), h = 0;
        uint crc = 0;
        for (var i = 0; i < rounds; i++)
        {
            a = a * 0xD1342543DE82EF95UL + 1;
            var hi = Math.BigMul(a ^ b, 0x9E3779B97F4A7C15UL, out var lo);
            b = BitOperations.RotateLeft(hi ^ lo, (int)(a >> 58));
            h += b ^ ((ulong)BitOperations.PopCount(a) << 32) ^ (ulong)BitOperations.LeadingZeroCount(b);
            crc = BitOperations.Crc32C(crc, h);
            if ((i & 7) == 0) h ^= b / ((a >> 40) | 1);
        }
        return h ^ crc;
    }
}

internal static class FpuStress
{
    private const int Steps = 1 << 20;
    private const int Seeds = 4;

    private static readonly Lazy<ulong[]> References = new(() =>
        Enumerable.Range(0, Seeds).Select(Rotate).ToArray());

    public static void Run(StressEngine engine, int index, CancellationToken token)
    {
        var refs = References.Value;
        for (var k = index; !token.IsCancellationRequested; k++)
        {
            var seed = k % Seeds;
            var sum = Rotate(seed);
            if (sum != refs[seed])
                engine.Fail(StressComponent.Fpu, "FPU result mismatch", $"seed {seed}: expected {refs[seed]:X16}, got {sum:X16}");
            engine.Tick();
        }
    }

    /// <summary>
    /// Six independent 2-D rotations by an irrational angle: values never settle, so the FMA units keep toggling,
    /// while the magnitude stays bounded. IEEE arithmetic on one code path is bit-exact, which makes the checksum stable.
    /// </summary>
    internal static ulong Rotate(int seed)
    {
        var angle = 0.6180339887498949 + seed * 1e-3;
        double c = Math.Cos(angle), s = Math.Sin(angle);
        if (Vector512.IsHardwareAccelerated) return Rotate512(seed, c, s);
        if (Vector256.IsHardwareAccelerated) return Rotate256(seed, c, s);
        return RotateScalar(seed, c, s);
    }

    private static double Start(int seed, int pair, int lane, bool y)
    {
        var t = seed * 0.37 + pair * 1.13 + lane * 0.071;
        return (1 + pair * 0.25) * (y ? Math.Sin(t) : Math.Cos(t));
    }

    private static Vector256<double> Init256(int seed, int pair, bool y) =>
        Vector256.Create(Start(seed, pair, 0, y), Start(seed, pair, 1, y), Start(seed, pair, 2, y), Start(seed, pair, 3, y));

    private static Vector512<double> Init512(int seed, int pair, bool y) =>
        Vector512.Create(Init256(seed, pair, y), Vector256.Create(Start(seed, pair, 4, y), Start(seed, pair, 5, y), Start(seed, pair, 6, y), Start(seed, pair, 7, y)));

    private static ulong Rotate256(int seed, double cos, double sin)
    {
        Vector256<double> x0 = Init256(seed, 0, false), y0 = Init256(seed, 0, true), x1 = Init256(seed, 1, false), y1 = Init256(seed, 1, true),
            x2 = Init256(seed, 2, false), y2 = Init256(seed, 2, true), x3 = Init256(seed, 3, false), y3 = Init256(seed, 3, true),
            x4 = Init256(seed, 4, false), y4 = Init256(seed, 4, true), x5 = Init256(seed, 5, false), y5 = Init256(seed, 5, true);
        var c = Vector256.Create(cos);
        var s = Vector256.Create(sin);
        var ns = Vector256.Create(-sin);
        for (var i = 0; i < Steps; i++)
        {
            var t0 = Simd.Mad(x0, c, y0 * ns); y0 = Simd.Mad(x0, s, y0 * c); x0 = t0;
            var t1 = Simd.Mad(x1, c, y1 * ns); y1 = Simd.Mad(x1, s, y1 * c); x1 = t1;
            var t2 = Simd.Mad(x2, c, y2 * ns); y2 = Simd.Mad(x2, s, y2 * c); x2 = t2;
            var t3 = Simd.Mad(x3, c, y3 * ns); y3 = Simd.Mad(x3, s, y3 * c); x3 = t3;
            var t4 = Simd.Mad(x4, c, y4 * ns); y4 = Simd.Mad(x4, s, y4 * c); x4 = t4;
            var t5 = Simd.Mad(x5, c, y5 * ns); y5 = Simd.Mad(x5, s, y5 * c); x5 = t5;
        }
        var bits = x0.AsUInt64() ^ (y0.AsUInt64() << 1) ^ (x1.AsUInt64() << 2) ^ (y1.AsUInt64() << 3)
                   ^ (x2.AsUInt64() << 4) ^ (y2.AsUInt64() << 5) ^ (x3.AsUInt64() << 6) ^ (y3.AsUInt64() << 7)
                   ^ (x4.AsUInt64() << 8) ^ (y4.AsUInt64() << 9) ^ (x5.AsUInt64() << 10) ^ (y5.AsUInt64() << 11);
        return Fold(bits.GetElement(0), bits.GetElement(1), bits.GetElement(2), bits.GetElement(3));
    }

    private static ulong Rotate512(int seed, double cos, double sin)
    {
        Vector512<double> x0 = Init512(seed, 0, false), y0 = Init512(seed, 0, true), x1 = Init512(seed, 1, false), y1 = Init512(seed, 1, true),
            x2 = Init512(seed, 2, false), y2 = Init512(seed, 2, true), x3 = Init512(seed, 3, false), y3 = Init512(seed, 3, true),
            x4 = Init512(seed, 4, false), y4 = Init512(seed, 4, true), x5 = Init512(seed, 5, false), y5 = Init512(seed, 5, true);
        var c = Vector512.Create(cos);
        var s = Vector512.Create(sin);
        var ns = Vector512.Create(-sin);
        for (var i = 0; i < Steps; i++)
        {
            var t0 = Vector512.FusedMultiplyAdd(x0, c, y0 * ns); y0 = Vector512.FusedMultiplyAdd(x0, s, y0 * c); x0 = t0;
            var t1 = Vector512.FusedMultiplyAdd(x1, c, y1 * ns); y1 = Vector512.FusedMultiplyAdd(x1, s, y1 * c); x1 = t1;
            var t2 = Vector512.FusedMultiplyAdd(x2, c, y2 * ns); y2 = Vector512.FusedMultiplyAdd(x2, s, y2 * c); x2 = t2;
            var t3 = Vector512.FusedMultiplyAdd(x3, c, y3 * ns); y3 = Vector512.FusedMultiplyAdd(x3, s, y3 * c); x3 = t3;
            var t4 = Vector512.FusedMultiplyAdd(x4, c, y4 * ns); y4 = Vector512.FusedMultiplyAdd(x4, s, y4 * c); x4 = t4;
            var t5 = Vector512.FusedMultiplyAdd(x5, c, y5 * ns); y5 = Vector512.FusedMultiplyAdd(x5, s, y5 * c); x5 = t5;
        }
        var bits = x0.AsUInt64() ^ (y0.AsUInt64() << 1) ^ (x1.AsUInt64() << 2) ^ (y1.AsUInt64() << 3)
                   ^ (x2.AsUInt64() << 4) ^ (y2.AsUInt64() << 5) ^ (x3.AsUInt64() << 6) ^ (y3.AsUInt64() << 7)
                   ^ (x4.AsUInt64() << 8) ^ (y4.AsUInt64() << 9) ^ (x5.AsUInt64() << 10) ^ (y5.AsUInt64() << 11);
        var lo = bits.GetLower();
        var hi = bits.GetUpper();
        return Fold(lo.GetElement(0), lo.GetElement(1), lo.GetElement(2), lo.GetElement(3))
               ^ BitOperations.RotateLeft(Fold(hi.GetElement(0), hi.GetElement(1), hi.GetElement(2), hi.GetElement(3)), 17);
    }

    private static ulong RotateScalar(int seed, double c, double s)
    {
        var x = new double[24];
        var y = new double[24];
        for (var k = 0; k < 24; k++)
        {
            x[k] = Start(seed, k / 4, k % 4, false);
            y[k] = Start(seed, k / 4, k % 4, true);
        }
        for (var i = 0; i < Steps / 4; i++)
            for (var k = 0; k < 24; k++)
            {
                var t = x[k] * c + y[k] * -s;
                y[k] = x[k] * s + y[k] * c;
                x[k] = t;
            }
        ulong h = 0;
        for (var k = 0; k < 24; k++)
            h = BitOperations.RotateLeft(h, 7) ^ (ulong)BitConverter.DoubleToInt64Bits(x[k]) ^ (ulong)BitConverter.DoubleToInt64Bits(y[k]);
        return h;
    }

    private static ulong Fold(ulong a, ulong b, ulong c, ulong d) =>
        a ^ BitOperations.RotateLeft(b, 13) ^ BitOperations.RotateLeft(c, 29) ^ BitOperations.RotateLeft(d, 47);
}

internal static unsafe class CacheStress
{
    private const int Passes = 16;
    private const ulong Key = 0xA5A5_3C3C_0F0F_9696UL, Add = 0x0123_4567_89AB_CDEFUL;

    public static void Run(StressEngine engine, int index, int threads, CancellationToken token)
    {
        // Alternate between a working set that fits a private L2 and this thread's share of a typical L3.
        nuint small = 192 << 10;
        var large = (nuint)Math.Clamp((16 << 20) / Math.Max(1, threads), 512 << 10, 4 << 20);
        using var block = new NativeBlock(large, 64);
        var p = (ulong*)block.Pointer;
        for (var k = 0UL; !token.IsCancellationRequested; k++)
        {
            var count = ((k & 1) == 0 ? small : large) / 8;
            var seed = ((ulong)index << 40) + k;
            for (nuint i = 0; i < count; i++) p[i] = Rng.SplitMix(seed + i);
            for (var pass = 0; pass < Passes; pass++) Transform(p, count);

            long bad = 0;
            nuint first = 0;
            ulong expected = 0, actual = 0;
            for (nuint i = 0; i < count; i++)
            {
                var e = Rng.SplitMix(seed + i);
                for (var pass = 0; pass < Passes; pass++) e = Step(e);
                if (p[i] == e) continue;
                if (bad++ == 0) (first, expected, actual) = (i, e, p[i]);
            }
            if (bad > 0)
                engine.Fail(StressComponent.Cache, "Cache verification failed",
                    $"{bad} words, first at offset {first * 8}, expected {expected:X16}, read {actual:X16}");
            engine.Tick();
        }
    }

    private static ulong Step(ulong v) => BitOperations.RotateLeft(v ^ Key, 13) + Add;

    private static void Transform(ulong* p, nuint count)
    {
        nuint i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var key = Vector256.Create(Key);
            var add = Vector256.Create(Add);
            for (; i + 4 <= count; i += 4)
            {
                var v = Vector256.Load(p + i) ^ key;
                Vector256.Store((Vector256.ShiftLeft(v, 13) | Vector256.ShiftRightLogical(v, 51)) + add, p + i);
            }
        }
        for (; i < count; i++) p[i] = Step(p[i]);
    }
}

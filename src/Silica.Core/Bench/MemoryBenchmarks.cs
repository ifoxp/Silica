using System.Diagnostics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Silica.Core.Bench;

internal unsafe delegate long StreamKernel(byte* slice, nuint offset, nuint length);

internal static unsafe class MemoryKernels
{
    public const nuint TotalBytes = 1u << 30;
    public const nuint StepBytes = 8 << 20;
    public const double MB = 1_000_000;

    public static ulong Sink;

    /// <summary>Splits the buffer into per-thread slices (64 KiB multiples) and touches each slice on its own thread.</summary>
    public static nuint SliceSize(nuint total, int threads) => total / (nuint)threads & ~(nuint)0xFFFF;

    public static ulong Read(byte* p, nuint len)
    {
        var end = p + len;
        if (Vector512.IsHardwareAccelerated)
        {
            Vector512<ulong> a = default, b = default, c = default, d = default;
            for (; p < end; p += 256)
            {
                a += Vector512.LoadAligned((ulong*)p);
                b += Vector512.LoadAligned((ulong*)(p + 64));
                c += Vector512.LoadAligned((ulong*)(p + 128));
                d += Vector512.LoadAligned((ulong*)(p + 192));
            }
            return Vector512.Sum(a + b + c + d);
        }
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<ulong> a = default, b = default, c = default, d = default;
            for (; p < end; p += 128)
            {
                a += Vector256.LoadAligned((ulong*)p);
                b += Vector256.LoadAligned((ulong*)(p + 32));
                c += Vector256.LoadAligned((ulong*)(p + 64));
                d += Vector256.LoadAligned((ulong*)(p + 96));
            }
            return Vector256.Sum(a + b + c + d);
        }
        ulong s0 = 0, s1 = 0, s2 = 0, s3 = 0;
        for (; p < end; p += 32)
        {
            s0 += *(ulong*)p;
            s1 += *(ulong*)(p + 8);
            s2 += *(ulong*)(p + 16);
            s3 += *(ulong*)(p + 24);
        }
        return s0 + s1 + s2 + s3;
    }

    public static void Write(byte* p, nuint len, ulong pattern)
    {
        var end = p + len;
        if (Vector512.IsHardwareAccelerated && Avx512F.IsSupported)
        {
            var v = Vector512.Create(pattern);
            for (; p < end; p += 256)
            {
                Avx512F.StoreAlignedNonTemporal((ulong*)p, v);
                Avx512F.StoreAlignedNonTemporal((ulong*)(p + 64), v);
                Avx512F.StoreAlignedNonTemporal((ulong*)(p + 128), v);
                Avx512F.StoreAlignedNonTemporal((ulong*)(p + 192), v);
            }
            Sse.StoreFence();
            return;
        }
        if (Avx.IsSupported)
        {
            var v = Vector256.Create(pattern);
            for (; p < end; p += 128)
            {
                Avx.StoreAlignedNonTemporal((ulong*)p, v);
                Avx.StoreAlignedNonTemporal((ulong*)(p + 32), v);
                Avx.StoreAlignedNonTemporal((ulong*)(p + 64), v);
                Avx.StoreAlignedNonTemporal((ulong*)(p + 96), v);
            }
            Sse.StoreFence();
            return;
        }
        new Span<ulong>(p, (int)(len / 8)).Fill(pattern);
    }

    public static bool NonTemporalWrites => Avx.IsSupported;

    /// <summary>Runs a streaming kernel over per-thread slices of one large buffer.</summary>
    public static BenchResult RunStreaming(IBenchmark b, BenchContext ctx, StreamKernel kernel, string details)
    {
        var sw = Stopwatch.StartNew();
        var threads = ctx.Threads;
        var slice = SliceSize(TotalBytes, threads);
        var step = Math.Min(StepBytes, slice);
        using var block = new NativeBlock(slice * (nuint)threads);
        var basePtr = block.Pointer;

        var (units, seconds) = BenchRunner.Run(ctx, threads, t =>
        {
            var start = basePtr + slice * (nuint)t;
            new Span<byte>(start, (int)slice).Fill((byte)(0x5A + t));
            nuint offset = 0;
            return () =>
            {
                var done = kernel(start, offset, step);
                offset += step;
                if (offset + step > slice) offset = 0;
                return done;
            };
        });

        var score = units / seconds / MB;
        return BenchRunner.Result(b, threads, score, sw, $"{details}, {BenchRunner.Isa}, {BenchRunner.Threads(threads)}");
    }
}

public sealed class MemoryReadBenchmark : IBenchmark
{
    public string Id => "mem-read";
    public string Title => "Memory Read";
    public string Description => "How fast data is read from RAM";
    public BenchKind Kind => BenchKind.Memory;
    public string Unit => "MB/s";
    public bool HigherIsBetter => true;

    public unsafe BenchResult Run(BenchContext ctx) =>
        MemoryKernels.RunStreaming(this, ctx, static (p, offset, len) =>
        {
            MemoryKernels.Sink += MemoryKernels.Read(p + offset, len);
            return (long)len;
        }, "1 GiB buffer");
}

public sealed class MemoryWriteBenchmark : IBenchmark
{
    public string Id => "mem-write";
    public string Title => "Memory Write";
    public string Description => "How fast data is written to RAM";
    public BenchKind Kind => BenchKind.Memory;
    public string Unit => "MB/s";
    public bool HigherIsBetter => true;

    public unsafe BenchResult Run(BenchContext ctx) =>
        MemoryKernels.RunStreaming(this, ctx, static (p, offset, len) =>
        {
            MemoryKernels.Write(p + offset, len, 0x0123456789ABCDEFUL ^ offset);
            return (long)len;
        }, MemoryKernels.NonTemporalWrites ? "1 GiB buffer, non-temporal stores" : "1 GiB buffer");
}

public sealed class MemoryCopyBenchmark : IBenchmark
{
    public string Id => "mem-copy";
    public string Title => "Memory Copy";
    public string Description => "How fast data is copied inside RAM";
    public BenchKind Kind => BenchKind.Memory;
    public string Unit => "MB/s";
    public bool HigherIsBetter => true;

    // Each slice is split in two halves; a step copies a chunk from the first half to the second.
    public unsafe BenchResult Run(BenchContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var threads = ctx.Threads;
        var slice = MemoryKernels.SliceSize(MemoryKernels.TotalBytes, threads);
        var half = slice / 2;
        var step = Math.Min(MemoryKernels.StepBytes, half);
        using var block = new NativeBlock(slice * (nuint)threads);
        var basePtr = block.Pointer;

        var (units, seconds) = BenchRunner.Run(ctx, threads, t =>
        {
            var src = basePtr + slice * (nuint)t;
            var dst = src + half;
            new Span<byte>(src, (int)slice).Fill((byte)(0x3C + t));
            nuint offset = 0;
            return () =>
            {
                Buffer.MemoryCopy(src + offset, dst + offset, (long)step, (long)step);
                offset += step;
                if (offset + step > half) offset = 0;
                return (long)step;
            };
        });

        return BenchRunner.Result(this, threads, units / seconds / MemoryKernels.MB, sw,
            $"1 GiB buffer, {BenchRunner.Threads(threads)}");
    }
}

public sealed class MemoryLatencyBenchmark : IBenchmark
{
    private const nuint Bytes = 512u << 20;
    private const int Line = 64;
    private const int LoadsPerStep = 1 << 18;

    public string Id => "mem-latency";
    public string Title => "Memory Latency";
    public string Description => "Delay of a single random access to RAM, lower is better";
    public BenchKind Kind => BenchKind.Memory;
    public string Unit => "ns";
    public bool HigherIsBetter => false;

    public unsafe BenchResult Run(BenchContext ctx)
    {
        var sw = Stopwatch.StartNew();
        using var block = new NativeBlock(Bytes);
        var basePtr = block.Pointer;
        BuildChain(basePtr, (int)(Bytes / Line), ctx);

        var (loads, seconds) = BenchRunner.Run(ctx, 1, _ =>
        {
            var p = (nint)basePtr;
            return () =>
            {
                for (var i = 0; i < LoadsPerStep; i += 16)
                {
                    p = *(nint*)p; p = *(nint*)p; p = *(nint*)p; p = *(nint*)p;
                    p = *(nint*)p; p = *(nint*)p; p = *(nint*)p; p = *(nint*)p;
                    p = *(nint*)p; p = *(nint*)p; p = *(nint*)p; p = *(nint*)p;
                    p = *(nint*)p; p = *(nint*)p; p = *(nint*)p; p = *(nint*)p;
                }
                MemoryKernels.Sink += (ulong)p;
                return LoadsPerStep;
            };
        }, progressFrom: 0.15, measure: TimeSpan.FromSeconds(6));

        return BenchRunner.Result(this, 1, seconds * 1e9 / loads, sw, "512 MiB random cyclic chain, 64-byte lines, 1 thread");
    }

    /// <summary>Sattolo's shuffle yields one cycle through every cache line, so the chase visits all of them.</summary>
    private static unsafe void BuildChain(byte* basePtr, int lines, BenchContext ctx)
    {
        var next = new int[lines];
        for (var i = 0; i < lines; i++) next[i] = i;
        var rng = new Rng(0x5EED_1A7E_C0DEUL);
        for (var i = lines - 1; i > 0; i--)
        {
            var j = rng.Below(i);
            (next[i], next[j]) = (next[j], next[i]);
            if ((i & 0xFFFFF) == 0)
            {
                ctx.Token.ThrowIfCancellationRequested();
                ctx.Report(0.1 * (lines - i) / lines);
            }
        }
        for (var i = 0; i < lines; i++)
            *(nint*)(basePtr + (nint)i * Line) = (nint)(basePtr + (nint)next[i] * Line);
        ctx.Report(0.15);
    }
}

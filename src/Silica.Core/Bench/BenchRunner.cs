using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Silica.Core.Bench;

/// <summary>
/// Time-boxed runner: every thread repeats a fixed-size step; steps that complete inside the measured
/// window are counted. Steps straddling the start are counted and those straddling the end are not,
/// so the edge error cancels out on average.
/// </summary>
internal static class BenchRunner
{
    public static readonly TimeSpan Warmup = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan Measure = TimeSpan.FromSeconds(7);

    private const int Init = 0, WarmingUp = 1, Measuring = 2, Stopping = 3;

    /// <param name="createWorker">Called on the worker thread (first-touch allocation); returns the step function, which returns work units done.</param>
    public static (double Units, double Seconds) Run(BenchContext ctx, int threads, Func<int, Func<long>> createWorker,
        double progressFrom = 0, TimeSpan? warmup = null, TimeSpan? measure = null)
    {
        var warm = warmup ?? Warmup;
        var total = warm + (measure ?? Measure);
        var counts = new long[threads * 16]; // one slot per 128 bytes avoids false sharing
        var phase = Init;
        Exception? error = null;
        var workers = new Thread[threads];
        using var ready = new CountdownEvent(threads);
        using var go = new ManualResetEventSlim();
        double t0 = 0, t1 = 0;

        try
        {
            for (var t = 0; t < threads; t++)
            {
                var index = t;
                workers[t] = new Thread(() =>
                {
                    try
                    {
                        Func<long> step;
                        try { step = createWorker(index); }
                        finally { ready.Signal(); }
                        go.Wait();
                        long done = 0;
                        while (Volatile.Read(ref phase) < Stopping)
                        {
                            var units = step();
                            if (Volatile.Read(ref phase) == Measuring) done += units;
                        }
                        counts[index * 16] = done;
                    }
                    catch (Exception e)
                    {
                        Interlocked.CompareExchange(ref error, e, null);
                        Volatile.Write(ref phase, Stopping);
                    }
                })
                {
                    IsBackground = true,
                    Name = "Silica bench " + t
                };
                workers[t].Start();
            }

            ready.Wait(ctx.Token);
            if (Volatile.Read(ref error) is null)
            {
                var sw = Stopwatch.StartNew();
                Volatile.Write(ref phase, WarmingUp);
                go.Set();
                while (Volatile.Read(ref error) is null)
                {
                    ctx.Token.ThrowIfCancellationRequested();
                    var elapsed = sw.Elapsed;
                    if (phase == WarmingUp && elapsed >= warm)
                    {
                        Volatile.Write(ref phase, Measuring);
                        t0 = sw.Elapsed.TotalSeconds;
                    }
                    else if (phase == Measuring && elapsed >= total)
                    {
                        Volatile.Write(ref phase, Stopping);
                        t1 = sw.Elapsed.TotalSeconds;
                        break;
                    }
                    ctx.Report(progressFrom + (1 - progressFrom) * (elapsed / total));
                    ctx.Token.WaitHandle.WaitOne(15);
                }
            }
        }
        finally
        {
            Volatile.Write(ref phase, Stopping);
            go.Set();
            foreach (var w in workers) w?.Join();
        }

        if (error is not null) ExceptionDispatchInfo.Throw(error);
        ctx.Report(1);
        double sum = 0;
        for (var t = 0; t < threads; t++) sum += counts[t * 16];
        return (sum, t1 - t0);
    }

    public static BenchResult Result(IBenchmark b, int threads, double score, Stopwatch elapsed, string? details) =>
        new(b.Id, score, b.Unit, b.HigherIsBetter, threads, elapsed.Elapsed, DateTime.Now, details);

    public static string Isa
    {
        get
        {
            if (Vector512.IsHardwareAccelerated && Avx512F.IsSupported) return "AVX-512";
            if (Avx2.IsSupported && Fma.IsSupported) return "AVX2+FMA";
            if (Avx.IsSupported) return "AVX";
            if (Vector128.IsHardwareAccelerated) return "SSE";
            return "scalar";
        }
    }

    public static string Threads(int n) => n == 1 ? "1 thread" : n + " threads";
}

/// <summary>Small deterministic PRNG (xorshift64*), so generated data is identical on every run.</summary>
internal struct Rng(ulong seed)
{
    private ulong _s = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

    public ulong Next()
    {
        _s ^= _s >> 12;
        _s ^= _s << 25;
        _s ^= _s >> 27;
        return _s * 0x2545F4914F6CDD1DUL;
    }

    /// <summary>Uniform in [0, bound) via multiply-high (Lemire), bias is negligible for our sizes.</summary>
    public int Below(int bound) => (int)Math.BigMul(Next(), (ulong)bound, out _);

    public double NextDouble() => (Next() >> 11) * (1.0 / (1UL << 53));

    public static ulong SplitMix(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }
}

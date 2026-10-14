namespace Silica.Core.Bench;

public enum BenchKind { Cpu, Fpu, Memory }

/// <summary>Everything a benchmark needs while running.</summary>
public sealed class BenchContext(int threads, CancellationToken token, IProgress<double>? progress = null)
{
    /// <summary>Worker threads to use (1 = single-thread run).</summary>
    public int Threads { get; } = Math.Max(1, threads);
    public CancellationToken Token { get; } = token;

    public void Report(double fraction) => progress?.Report(Math.Clamp(fraction, 0, 1));
}

public sealed record BenchResult(
    string BenchmarkId,
    double Score,
    string Unit,
    bool HigherIsBetter,
    int Threads,
    TimeSpan Elapsed,
    DateTime When,
    string? Details = null);

/// <summary>
/// A CPU/FPU/memory benchmark. Implementations are discovered by reflection (public parameterless ctor).
/// Each run should take roughly 5–15 seconds on a modern desktop CPU and must honour cancellation.
/// </summary>
public interface IBenchmark
{
    string Id { get; }

    /// <summary>English title, e.g. "CPU Queens".</summary>
    string Title { get; }

    /// <summary>English one-line description of what is measured.</summary>
    string Description { get; }

    BenchKind Kind { get; }

    /// <summary>Score unit, e.g. "MB/s", "ns", "points".</summary>
    string Unit { get; }

    bool HigherIsBetter { get; }

    BenchResult Run(BenchContext ctx);
}

public sealed record DiskBenchOptions(string Directory, long FileSizeBytes = 1L << 30, TimeSpan? DurationPerTest = null);

public sealed record DiskBenchStep(string TestId, string Title, double BytesPerSecond, double? Iops, double? LatencyUs);

public sealed record DiskBenchResult(string Directory, DateTime When, IReadOnlyList<DiskBenchStep> Steps);

[Flags]
public enum StressComponent
{
    None = 0,
    Cpu = 1,      // integer ALU work
    Fpu = 2,      // SIMD floating point (AVX/AVX2/FMA where available)
    Cache = 4,    // working set that fits in L2/L3
    Memory = 8,   // large buffers in RAM with verification
    Disk = 16     // sequential write/read/verify of a temp file
}

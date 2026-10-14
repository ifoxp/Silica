using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace Silica.Core.Bench;

public sealed class DiskBenchmark
{
    public const string TempFileName = "Silica-bench.tmp";

    private const FileOptions NoBuffering = (FileOptions)0x20000000;
    private const int SeqBlock = 1 << 20;
    private const int RndBlock = 4096;
    private const int PrepBlock = 4 << 20;
    private const long Reserve = 256L << 20;
    private const double PrepShare = 0.1;

    private sealed record Spec(string Id, string Title, bool Write, bool Random, int Block, int Outstanding);

    private static readonly Spec[] Specs =
    [
        new("seq-read", "Sequential read 1 MiB Q8", false, false, SeqBlock, 8),
        new("seq-write", "Sequential write 1 MiB Q8", true, false, SeqBlock, 8),
        new("rnd-read", "Random read 4 KiB Q1", false, true, RndBlock, 1),
        new("rnd-write", "Random write 4 KiB Q1", true, true, RndBlock, 1),
        new("rnd-read-q32", "Random read 4 KiB Q32T4", false, true, RndBlock, 32 * 4),
        new("rnd-write-q32", "Random write 4 KiB Q32T4", true, true, RndBlock, 32 * 4)
    ];

    public static IReadOnlyList<(string Id, string Title)> Tests { get; } = Specs.Select(s => (s.Id, s.Title)).ToArray();

    public DiskBenchResult Run(DiskBenchOptions options, CancellationToken token,
        IProgress<(double Fraction, DiskBenchStep? Completed)>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var duration = options.DurationPerTest ?? TimeSpan.FromSeconds(5);
        var size = Math.Max(16L << 20, options.FileSizeBytes / SeqBlock * SeqBlock);
        var dir = Path.GetFullPath(options.Directory);
        Directory.CreateDirectory(dir);
        CheckFreeSpace(dir, size);

        var path = Path.Combine(dir, TempFileName);
        var steps = new List<DiskBenchStep>();
        try
        {
            using (var handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
                       NoBuffering | FileOptions.WriteThrough | FileOptions.Asynchronous | FileOptions.DeleteOnClose))
            {
                Prepare(handle, size, token, f => progress?.Report((f * PrepShare, null)));
                for (var i = 0; i < Specs.Length; i++)
                {
                    var from = PrepShare + (1 - PrepShare) * i / Specs.Length;
                    var span = (1 - PrepShare) / Specs.Length;
                    var step = RunTest(handle, Specs[i], size, duration, token, f => progress?.Report((from + span * f, null)));
                    steps.Add(step);
                    progress?.Report((from + span, step));
                }
            }
            return new DiskBenchResult(dir, DateTime.Now, steps);
        }
        finally
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void CheckFreeSpace(string dir, long size)
    {
        long free;
        try { free = new DriveInfo(Path.GetPathRoot(dir)!).AvailableFreeSpace; }
        catch (ArgumentException) { return; } // network paths: let the write fail on its own if space runs out
        if (free < size + Reserve)
            throw new IOException("Not enough free disk space for the benchmark file.");
    }

    private static unsafe void Prepare(SafeFileHandle handle, long size, CancellationToken token, Action<double> report)
    {
        using var buffer = new AlignedMemory(PrepBlock);
        var words = new Span<ulong>(buffer.Pointer, PrepBlock / 8);
        var rng = new Rng(0xD15C);
        for (long offset = 0; offset < size; offset += PrepBlock)
        {
            token.ThrowIfCancellationRequested();
            foreach (ref var w in words) w = rng.Next();
            var length = (int)Math.Min(PrepBlock, size - offset);
            RandomAccess.WriteAsync(handle, buffer.Memory[..length], offset, token).AsTask().GetAwaiter().GetResult();
            report((double)(offset + length) / size);
        }
    }

    private static DiskBenchStep RunTest(SafeFileHandle handle, Spec spec, long size, TimeSpan duration,
        CancellationToken token, Action<double> report)
    {
        var buffers = new AlignedMemory[spec.Outstanding];
        var tasks = new Task<(long Ops, long Ticks)>[spec.Outstanding];
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        long sequential = -1;
        var blocks = size / spec.Block;
        var sw = Stopwatch.StartNew();
        try
        {
            for (var q = 0; q < spec.Outstanding; q++)
            {
                var buffer = buffers[q] = new AlignedMemory(spec.Block);
                var rng = new Rng((ulong)q * 7919 + 17);
                foreach (ref var b in buffer.GetSpan()) b = (byte)rng.Next();
                tasks[q] = Task.Run(async () =>
                {
                    long ops = 0, ticks = 0;
                    var memory = buffer.Memory;
                    while (!stop.IsCancellationRequested)
                    {
                        var block = spec.Random ? (long)Math.BigMul(rng.Next(), (ulong)blocks, out _)
                                                : Interlocked.Increment(ref sequential) % blocks;
                        var offset = block * spec.Block;
                        var t0 = Stopwatch.GetTimestamp();
                        try
                        {
                            if (spec.Write) await RandomAccess.WriteAsync(handle, memory, offset, stop.Token).ConfigureAwait(false);
                            else await RandomAccess.ReadAsync(handle, memory, offset, stop.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { break; }
                        ticks += Stopwatch.GetTimestamp() - t0;
                        ops++;
                    }
                    return (ops, ticks);
                }, CancellationToken.None);
            }

            var all = Task.WhenAll(tasks);
            while (!all.Wait(50))
            {
                var elapsed = sw.Elapsed;
                if (elapsed >= duration || token.IsCancellationRequested) stop.Cancel();
                report(Math.Min(1, elapsed / duration));
            }
            var seconds = sw.Elapsed.TotalSeconds;
            token.ThrowIfCancellationRequested();

            long totalOps = 0, totalTicks = 0;
            foreach (var t in tasks)
            {
                totalOps += t.Result.Ops;
                totalTicks += t.Result.Ticks;
            }
            var iops = totalOps / seconds;
            var latencyUs = totalOps > 0 ? totalTicks * 1e6 / Stopwatch.Frequency / totalOps : 0;
            return new DiskBenchStep(spec.Id, spec.Title, iops * spec.Block, spec.Random ? iops : null, latencyUs);
        }
        finally
        {
            stop.Cancel();
            // Buffers must outlive every in-flight I/O.
            foreach (var t in tasks)
            {
                try { t?.Wait(); }
                catch (AggregateException) { }
            }
            foreach (var b in buffers) ((IDisposable?)b)?.Dispose();
        }
    }
}

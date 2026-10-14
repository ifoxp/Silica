using System.Collections.Concurrent;
using System.Diagnostics;
using Silica.Core.Bench;

namespace Silica.Core.Stress;

/// <summary>
/// Stability test. Every worker verifies its own results; a mismatch means the hardware computed or
/// stored something wrong. Messages are "Stable text" or "Stable text: details" so the UI can translate the prefix.
/// </summary>
public sealed class StressEngine : IDisposable
{
    private const StressComponent AllComponents =
        StressComponent.Cpu | StressComponent.Fpu | StressComponent.Cache | StressComponent.Memory | StressComponent.Disk;

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, byte> _tempFiles = new();
    private CancellationTokenSource? _cts;
    private Thread[] _threads = [];
    private long _errors, _iterations;

    public bool IsRunning { get; private set; }
    public StressComponent Components { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public long ErrorCount => Interlocked.Read(ref _errors);
    public long Iterations => Interlocked.Read(ref _iterations);

    public event Action<string>? Log;
    public event Action<StressComponent, string>? ErrorDetected;

    public void Start(StressComponent components, int threads = 0, string? diskDirectory = null)
    {
        components &= AllComponents;
        if (components == StressComponent.None) throw new ArgumentException("No stress components selected.", nameof(components));

        lock (_gate)
        {
            if (IsRunning) throw new InvalidOperationException("Stress test is already running.");

            var plan = Plan(components, threads > 0 ? threads : Environment.ProcessorCount);
            var cts = new CancellationTokenSource();
            var token = cts.Token;
            var memThreads = plan.FirstOrDefault(p => p.Component == StressComponent.Memory).Count;
            var memBytes = memThreads > 0 ? MemoryStress.BytesPerThread(memThreads) : 0;
            var cacheThreads = plan.FirstOrDefault(p => p.Component == StressComponent.Cache).Count;
            var dir = string.IsNullOrWhiteSpace(diskDirectory) ? Path.GetTempPath() : diskDirectory;

            var list = new List<Thread>();
            foreach (var (component, count) in plan)
            {
                for (var i = 0; i < count; i++)
                {
                    var index = i;
                    ThreadStart body = component switch
                    {
                        StressComponent.Cpu => () => CpuStress.Run(this, index, token),
                        StressComponent.Fpu => () => FpuStress.Run(this, index, token),
                        StressComponent.Cache => () => CacheStress.Run(this, index, cacheThreads, token),
                        StressComponent.Memory => () => MemoryStress.Run(this, index, memBytes, token),
                        _ => () => DiskStress.Run(this, index, dir, token)
                    };
                    list.Add(new Thread(() => Guard(component, body))
                    {
                        IsBackground = true,
                        Priority = ThreadPriority.BelowNormal,
                        Name = $"Silica stress {Label(component)} {i}"
                    });
                }
            }

            Interlocked.Exchange(ref _errors, 0);
            Interlocked.Exchange(ref _iterations, 0);
            _cts = cts;
            _threads = [.. list];
            Components = components;
            StartedAt = DateTime.Now;
            IsRunning = true;
            foreach (var t in _threads) t.Start();
            Info("Stress test started: " + string.Join(", ", plan.Select(p => $"{Label(p.Component)} x{p.Count}")));
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!IsRunning) return;
            _cts!.Cancel();
            var sw = Stopwatch.StartNew();
            var stuck = 0;
            foreach (var t in _threads)
            {
                var left = TimeSpan.FromSeconds(3) - sw.Elapsed;
                if (!t.Join(left > TimeSpan.Zero ? left : TimeSpan.Zero)) stuck++;
            }
            foreach (var path in _tempFiles.Keys) TryDelete(path);
            if (stuck == 0) _cts.Dispose();
            _cts = null;
            _threads = [];
            IsRunning = false;
            StartedAt = null;
            if (stuck > 0) Info($"Workers still finishing: {stuck}");
            Info($"Stress test stopped: {Iterations} iterations, {ErrorCount} errors");
        }
    }

    public void Dispose() => Stop();

    internal static IReadOnlyList<(StressComponent Component, int Count)> Plan(StressComponent components, int threads)
    {
        var compute = new[] { StressComponent.Cpu, StressComponent.Fpu, StressComponent.Cache }
            .Where(c => components.HasFlag(c)).ToArray();
        var plan = new List<(StressComponent, int)>();

        var mem = 0;
        if (components.HasFlag(StressComponent.Memory))
            mem = compute.Length == 0 ? Math.Min(4, threads) : Math.Clamp(threads / (compute.Length + 1), 1, 4);

        var rest = Math.Max(threads - mem, compute.Length);
        for (var k = 0; k < compute.Length; k++)
            plan.Add((compute[k], rest / compute.Length + (k < rest % compute.Length ? 1 : 0)));
        if (mem > 0) plan.Add((StressComponent.Memory, mem));
        if (components.HasFlag(StressComponent.Disk)) plan.Add((StressComponent.Disk, 1));
        return plan;
    }

    internal void Tick() => Interlocked.Increment(ref _iterations);

    internal void Fail(StressComponent component, string message, string details)
    {
        Interlocked.Increment(ref _errors);
        try { ErrorDetected?.Invoke(component, message); }
        catch (Exception) { /* a faulty subscriber must not stop the test */ }
        Info(message + ": " + details);
    }

    internal void Info(string message)
    {
        try { Log?.Invoke(message); }
        catch (Exception) { }
    }

    internal void RegisterTempFile(string path) => _tempFiles[path] = 0;

    internal void ReleaseTempFile(string path)
    {
        TryDelete(path);
        _tempFiles.TryRemove(path, out _);
    }

    private void Guard(StressComponent component, ThreadStart body)
    {
        try { body(); }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Info($"Stress worker failed: {Label(component)}, {e.Message}");
        }
    }

    private static string Label(StressComponent c) => c switch
    {
        StressComponent.Cpu => "CPU",
        StressComponent.Fpu => "FPU",
        _ => c.ToString()
    };

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

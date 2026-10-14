using System.Text.Json;

namespace Silica.Core.Bench;

public sealed class BenchHistory
{
    private const int MaxEntries = 2000;
    private static readonly object FileLock = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object _gate = new();
    private readonly List<BenchResult> _results;
    private readonly List<DiskBenchResult> _disk;

    private BenchHistory(List<BenchResult> results, List<DiskBenchResult> disk)
    {
        _results = results;
        _disk = disk;
    }

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Silica", "bench-history.json");

    public static BenchHistory Load()
    {
        try
        {
            string text;
            lock (FileLock)
            {
                if (!File.Exists(FilePath)) return new([], []);
                text = File.ReadAllText(FilePath);
            }
            var data = JsonSerializer.Deserialize<Snapshot>(text, Json);
            return new(
                data?.Results?.Where(r => r is { BenchmarkId: not null, Unit: not null }).ToList() ?? [],
                data?.DiskResults?.Where(r => r is { Steps: not null, Directory: not null }).ToList() ?? []);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new([], []);
        }
    }

    public IReadOnlyList<BenchResult> Results { get { lock (_gate) return _results.ToArray(); } }

    public IReadOnlyList<DiskBenchResult> DiskResults { get { lock (_gate) return _disk.ToArray(); } }

    public void Add(BenchResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        lock (_gate)
        {
            _results.Add(r);
            if (_results.Count > MaxEntries) _results.RemoveRange(0, _results.Count - MaxEntries);
        }
    }

    public void Add(DiskBenchResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        lock (_gate)
        {
            _disk.Add(r);
            if (_disk.Count > MaxEntries) _disk.RemoveRange(0, _disk.Count - MaxEntries);
        }
    }

    public IEnumerable<BenchResult> For(string benchmarkId)
    {
        lock (_gate)
            return _results.Where(r => r.BenchmarkId == benchmarkId).OrderByDescending(r => r.When).ToArray();
    }

    public BenchResult? Best(string benchmarkId)
    {
        lock (_gate)
        {
            BenchResult? best = null;
            foreach (var r in _results)
            {
                if (r.BenchmarkId != benchmarkId || double.IsNaN(r.Score)) continue;
                if (best is null || (r.HigherIsBetter ? r.Score > best.Score : r.Score < best.Score)) best = r;
            }
            return best;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _results.Clear();
            _disk.Clear();
        }
    }

    public void Save()
    {
        string text;
        lock (_gate) text = JsonSerializer.Serialize(new Snapshot(_results, _disk), Json);

        lock (FileLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + "." + Environment.ProcessId + ".tmp";
            try
            {
                File.WriteAllText(temp, text);
                File.Move(temp, FilePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }

    private sealed record Snapshot(List<BenchResult>? Results, List<DiskBenchResult>? DiskResults);
}

using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using Silica.Core.Bench;

namespace Silica.Services;

/// <summary>Results of another computer, exported by Silica and imported here for comparison.</summary>
public sealed class BenchReference
{
    public string Computer { get; set; } = "";
    public string Processor { get; set; } = "";
    public DateTime Exported { get; set; }
    public List<BenchResult> Results { get; set; } = [];
}

public static class BenchReferences
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Folder => Path.Combine(AppPaths.DataDirectory, "References");

    public static List<BenchReference> Load()
    {
        var list = new List<BenchReference>();
        if (!Directory.Exists(Folder)) return list;
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<BenchReference>(File.ReadAllText(file), Json) is { Results.Count: > 0 } r) list.Add(r);
            }
            catch (Exception ex)
            {
                Log.Warn($"Benchmark reference skipped: {file}: {ex.Message}");
            }
        }
        return list;
    }

    public static BenchReference FromHistory(BenchHistory history)
    {
        var best = Benchmarks.All.Select(b => history.Best(b.Id)).OfType<BenchResult>().ToList();
        return new BenchReference
        {
            Computer = Environment.MachineName,
            Processor = ProcessorName(),
            Exported = DateTime.Now,
            Results = best
        };
    }

    public static void Export(BenchReference reference, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(reference, Json));

    /// <summary>Validates and copies a results file into the references folder.</summary>
    public static BenchReference Import(string path)
    {
        var reference = JsonSerializer.Deserialize<BenchReference>(File.ReadAllText(path), Json);
        if (reference is not { Results.Count: > 0 }) throw new InvalidDataException("No benchmark results in this file.");
        Directory.CreateDirectory(Folder);
        var name = Reports.SafeFileName($"{reference.Computer} {reference.Processor}".Trim());
        File.WriteAllText(Path.Combine(Folder, name + ".json"), JsonSerializer.Serialize(reference, Json));
        return reference;
    }

    public static void Remove(BenchReference reference)
    {
        if (!Directory.Exists(Folder)) return;
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
        {
            try
            {
                var r = JsonSerializer.Deserialize<BenchReference>(File.ReadAllText(file), Json);
                if (r != null && r.Computer == reference.Computer && r.Exported == reference.Exported) File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
    }

    public static string ProcessorName() =>
        (Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string)?.Trim()
        ?? Environment.MachineName;
}

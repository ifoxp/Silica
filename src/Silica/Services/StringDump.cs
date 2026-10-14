using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Silica.Core.Bench;
using Silica.Core.Info;
using Silica.Core.Model;
using Silica.Core.Sensors;

namespace Silica.Services;

/// <summary>
/// silica.exe --dump-strings file.json: lists every English string that only exists at run time
/// (page labels, sensor names, benchmark texts), so tools/i18n.py can keep en.json complete.
/// </summary>
public static partial class StringDump
{
    public static int Run(string path)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        void Add(string? s)
        {
            if (!string.IsNullOrWhiteSpace(s)) keys.Add(s.Trim());
        }

        foreach (var provider in InfoProviders.All)
        {
            Add(provider.Title);
            InfoPage page;
            try { page = provider.Collect(CancellationToken.None); }
            catch (Exception ex) { Log.Error($"Dump: {provider.Id}", ex); continue; }
            foreach (var w in page.Warnings) Add(w.Split(": ")[0]);
            foreach (var block in page.Blocks)
            {
                if (block.LocalizeTitle) Add(block.Title);
                switch (block)
                {
                    case PropertyBlock pb:
                        foreach (var item in pb.Items)
                        {
                            Add(item.Label);
                            if (item.LocalizeValue) Add(item.Value);
                        }
                        break;
                    case TableBlock tb:
                        foreach (var c in tb.Columns) Add(c);
                        break;
                }
            }
        }

        foreach (var b in Benchmarks.All)
        {
            Add(b.Title);
            Add(b.Description);
            Add(b.Unit);
        }
        foreach (var (_, title) in DiskBenchmark.Tests) Add(title);

        using (SensorHub.Shared.Subscribe())
        {
            var until = DateTime.Now.AddSeconds(15);
            while (SensorHub.Shared.PollCount < 2 && DateTime.Now < until) Thread.Sleep(200);
            foreach (var s in SensorHub.Shared.AllSensors)
            {
                var m = TrailingNumber().Match(s.Name);
                Add(m.Success ? s.Name[..m.Index] + "{0}" + s.Name[(m.Index + m.Length)..] : s.Name);
            }
        }

        var json = JsonSerializer.Serialize(keys, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        File.WriteAllText(path, json);
        return 0;
    }

    [GeneratedRegex(@"(?<=[#\s])\d+(?=\D*$)")]
    private static partial Regex TrailingNumber();
}

using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using Silica.Core.Model;
using Silica.Core.Util;

namespace Silica.Core.Info.Software;

public sealed class EventLogProvider : IInfoProvider
{
    private const int MaxRows = 300;
    private const int Days = 7;
    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MessageBudget = TimeSpan.FromSeconds(4);

    public string Id => "events";
    public string Title => "Event log";
    public InfoCategory Category => InfoCategory.Software;
    public int Order => 120;
    public string Icon => "\uE9F9";

    private sealed record Counts(string Log, int Critical, int Error, int Warning, bool Partial);

    public InfoPage Collect(CancellationToken ct)
    {
        var b = new PageBuilder(Id, Title);
        var counts = new List<Counts>();
        var candidates = new List<(string Log, EventRecord Record)>();
        try
        {
            foreach (var log in (string[])["System", "Application"])
                b.Try(log + " log", () => counts.Add(Read(log, candidates, ct)));

            var newest = candidates
                .OrderByDescending(c => c.Record.TimeCreated ?? DateTime.MinValue)
                .Take(MaxRows)
                .ToList();

            var s = b.Table("Summary", "Log", "Critical", "Error", "Warning");
            foreach (var c in counts)
            {
                var more = c.Partial ? "+" : "";
                s.Row(c.Log, Fmt.Number(c.Critical) + more, Fmt.Number(c.Error) + more, Fmt.Number(c.Warning) + more);
            }
            if (counts.Any(c => c.Partial))
                b.Warn("The log is very large; counts marked with + are incomplete.");

            var watch = Stopwatch.StartNew();
            var t = b.Table($"Critical events and errors (last {Days} days)", "Time", "Level", "Log", "Source", "Event ID", "Message");
            foreach (var (log, r) in newest)
            {
                var message = watch.Elapsed < MessageBudget && !ct.IsCancellationRequested ? Message(r) : "";
                t.Row(Fmt.DateTime(r.TimeCreated), r.Level == 1 ? "Critical" : "Error", log, r.ProviderName, r.Id, message);
            }
            if (watch.Elapsed >= MessageBudget)
                b.Warn("Some event messages were skipped because loading them took too long.");
        }
        finally
        {
            foreach (var (_, r) in candidates) r.Dispose();
        }
        return b.Build();
    }

    private static Counts Read(string log, List<(string, EventRecord)> keep, CancellationToken ct)
    {
        var xpath = $"*[System[(Level=1 or Level=2 or Level=3) and TimeCreated[timediff(@SystemTime) <= {(long)TimeSpan.FromDays(Days).TotalMilliseconds}]]]";
        var query = new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true };
        int critical = 0, error = 0, warning = 0, kept = 0;
        var partial = false;
        var watch = Stopwatch.StartNew();

        using var reader = new EventLogReader(query) { BatchSize = 256 };
        while (reader.ReadEvent() is { } r)
        {
            var keepIt = false;
            try
            {
                switch (r.Level)
                {
                    case 1: critical++; keepIt = kept < MaxRows; break;
                    case 2: error++; keepIt = kept < MaxRows; break;
                    default: warning++; break;
                }
                if (keepIt)
                {
                    keep.Add((log, r));
                    kept++;
                }
            }
            finally
            {
                if (!keepIt) r.Dispose();
            }
            if (watch.Elapsed > ReadBudget || ct.IsCancellationRequested)
            {
                partial = true;
                break;
            }
        }
        return new Counts(log, critical, error, warning, partial);
    }

    private static string Message(EventRecord r)
    {
        try
        {
            var text = r.FormatDescription();
            if (string.IsNullOrWhiteSpace(text)) return "";
            var line = text.Trim().Split('\n', 2)[0].Trim();
            return line.Length > 200 ? line[..199] + "\u2026" : line;
        }
        catch (EventLogException) { return ""; }
    }
}

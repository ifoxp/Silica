using System.Globalization;
using Silica.Core.Model;
using Silica.Core.Util;

namespace Silica.Core.Info.Software;

public sealed class UpdatesProvider : IInfoProvider
{
    private const int MaxHistory = 200;

    public string Id => "updates";
    public string Title => "Windows updates";
    public InfoCategory Category => InfoCategory.Software;
    public int Order => 60;
    public string Icon => "\uE895";

    private sealed record HistoryEntry(DateTime? Date, string Title, string Operation, string Result);

    public InfoPage Collect(CancellationToken ct)
    {
        var b = new PageBuilder(Id, Title);
        var summary = b.Group("Summary");

        b.Try("Installed updates", () =>
        {
            var rows = Wmi.Query("SELECT HotFixID, Description, InstalledOn, InstalledBy FROM Win32_QuickFixEngineering")
                .Select(r => (Id: r.Str("HotFixID") ?? "", Desc: r.Str("Description") ?? "", On: ParseQfeDate(r.Str("InstalledOn")), By: r.Str("InstalledBy") ?? ""))
                .OrderByDescending(x => x.On ?? DateTime.MinValue)
                .ToList();
            summary.Add("Installed updates", Fmt.Number(rows.Count))
                .Add("Last update installed", Fmt.Date(rows.FirstOrDefault().On));

            var t = b.Table("Installed updates", "HotFix ID", "Description", "Installed on", "Installed by");
            foreach (var x in rows) t.Row(x.Id, x.Desc, Fmt.Date(x.On), x.By);
        });

        b.Try("Update history", () =>
        {
            if (!SoftwareHelpers.TryRun(ReadHistory, TimeSpan.FromSeconds(8), out var history) || history == null)
            {
                b.Warn("Windows Update did not respond in time; update history is not shown.");
                return;
            }
            summary.Add("History entries", Fmt.Number(history.Count))
                .Add("Failed", Fmt.Number(history.Count(h => h.Result == "Failed")));

            var t = b.Table("Update history", "Date", "Title", "Operation", "Result");
            foreach (var h in history) t.Row(Fmt.DateTime(h.Date), h.Title, h.Operation, h.Result);
        });

        return b.Build();
    }

    // InstalledOn is normally M/d/yyyy regardless of locale; very old systems store a hex FILETIME.
    private static DateTime? ParseQfeDate(string? s)
    {
        if (s == null) return null;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        return long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ft) && ft > 0
            ? DateTime.FromFileTime(ft)
            : null;
    }

    private static List<HistoryEntry> ReadHistory()
    {
        var list = new List<HistoryEntry>();
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session");
        if (type == null) return list;
        dynamic session = Activator.CreateInstance(type)!;
        dynamic? searcher = null, entries = null;
        try
        {
            searcher = session.CreateUpdateSearcher();
            int total = searcher.GetTotalHistoryCount();
            if (total <= 0) return list;
            entries = searcher.QueryHistory(0, Math.Min(total, MaxHistory));
            int count = entries.Count;
            for (var i = 0; i < count; i++)
            {
                dynamic e = entries[i];
                try
                {
                    string title = e.Title ?? "";
                    if (title.Length == 0) continue;
                    DateTime? date = SoftwareHelpers.ComDate((object)e.Date) is { } utc ? DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime() : null;
                    list.Add(new HistoryEntry(date, title, Operation((int)e.Operation), ResultName((int)e.ResultCode)));
                }
                finally { SoftwareHelpers.Release(e); }
            }
        }
        finally
        {
            SoftwareHelpers.Release(entries);
            SoftwareHelpers.Release(searcher);
            SoftwareHelpers.Release(session);
        }
        return list;
    }

    private static string Operation(int op) => op switch
    {
        1 => "Installation",
        2 => "Uninstallation",
        _ => ""
    };

    private static string ResultName(int code) => code switch
    {
        1 => "In progress",
        2 => "Succeeded",
        3 => "Succeeded with errors",
        4 => "Failed",
        5 => "Aborted",
        _ => "Not started"
    };
}

using Silica.Core.Model;
using Silica.Core.Util;

namespace Silica.Core.Info.Software;

public sealed class ServicesProvider : IInfoProvider
{
    public string Id => "services";
    public string Title => "Services";
    public InfoCategory Category => InfoCategory.Software;
    public int Order => 20;
    public string Icon => "\uE912";

    public InfoPage Collect(CancellationToken ct)
    {
        var b = new PageBuilder(Id, Title);
        b.Try("Services", () =>
        {
            const string fields = "DisplayName, Name, State, StartMode, StartName, ProcessId, PathName";
            var rows = Wmi.Query($"SELECT {fields}, DelayedAutoStart FROM Win32_Service");
            if (rows.Count == 0) rows = Wmi.Query($"SELECT {fields} FROM Win32_Service");
            if (rows.Count == 0)
            {
                b.Warn("Service list is not available (WMI query failed).");
                return;
            }

            var services = rows.Select(r => new
            {
                Display = r.Str("DisplayName") ?? r.Str("Name") ?? "",
                Name = r.Str("Name") ?? "",
                State = r.Str("State") ?? "",
                Mode = StartMode(r.Str("StartMode"), r.Bool("DelayedAutoStart") == true),
                Account = r.Str("StartName"),
                Pid = r.Long("ProcessId") is > 0 and var pid ? (long?)pid : null,
                Path = r.Str("PathName")
            }).OrderBy(s => s.Display, StringComparer.CurrentCultureIgnoreCase).ToList();

            b.Group("Summary")
                .Add("Total", Fmt.Number(services.Count))
                .Add("Running", Fmt.Number(services.Count(s => s.State == "Running")))
                .Add("Stopped", Fmt.Number(services.Count(s => s.State == "Stopped")))
                .Add("Disabled", Fmt.Number(services.Count(s => s.Mode == "Disabled")));

            var t = b.Table("Services", "Display name", "Name", "State", "Start mode", "Account", "PID", "Path");
            foreach (var s in services) t.Row(s.Display, s.Name, s.State, s.Mode, s.Account, s.Pid, s.Path);
        });
        return b.Build();
    }

    internal static string StartMode(string? mode, bool delayed = false) => mode switch
    {
        "Auto" => delayed ? "Automatic (delayed)" : "Automatic",
        "Manual" => "Manual",
        "Disabled" => "Disabled",
        "Boot" => "Boot",
        "System" => "System",
        null => "",
        _ => mode
    };
}

using Silica.Core.Model;
using Silica.Core.Util;

namespace Silica.Core.Info.Hardware;

public sealed class PrintersProvider : IInfoProvider
{
    public string Id => "printers";
    public string Title => "Printers";
    public InfoCategory Category => InfoCategory.Hardware;
    public int Order => 110;
    public string Icon => "";

    public InfoPage Collect(CancellationToken ct)
    {
        var b = new PageBuilder(Id, Title);
        b.Try("Printers", () =>
        {
            var printers = Wmi.Query("SELECT Name, Default, Network, Shared, DriverName, PortName, PrinterStatus, WorkOffline FROM Win32_Printer")
                .OrderByDescending(r => r.Bool("Default") == true)
                .ThenBy(r => r.Str("Name"), StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (printers.Count == 0)
            {
                b.Group("Printers").Add("Installed printers", 0);
                return;
            }
            var t = b.Table("Printers", "Name", "Default", "Network", "Shared", "Driver", "Port", "Status");
            foreach (var r in printers)
            {
                t.Row(r.Str("Name"), YesNo(r.Bool("Default")), YesNo(r.Bool("Network")), YesNo(r.Bool("Shared")),
                    r.Str("DriverName"), r.Str("PortName"), r.Bool("WorkOffline") == true ? "Offline" : Status(r.Int("PrinterStatus")));
            }
        });
        return b.Build();
    }

    private static string YesNo(bool? v) => v == true ? "Yes" : "No";

    private static string? Status(int? s) => s switch
    {
        3 => "Idle",
        4 => "Printing",
        5 => "Warming up",
        6 => "Stopped",
        7 => "Offline",
        1 or 2 => "Unknown",
        _ => null
    };
}

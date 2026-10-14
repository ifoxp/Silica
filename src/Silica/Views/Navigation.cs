using System.Windows;
using Silica.Core.Info;

namespace Silica.Views;

public enum NavSection { Main, Hardware, Software, Tools }

/// <summary>One entry of the sidebar. Views are created on first visit and kept afterwards.</summary>
public sealed class NavItem(string id, string title, string icon, NavSection section, Func<FrameworkElement> factory, IInfoProvider? provider = null)
{
    private FrameworkElement? _view;

    public string Id { get; } = id;
    public string Title { get; } = title;
    public string Icon { get; } = icon;
    public NavSection Section { get; } = section;
    public IInfoProvider? Provider { get; } = provider;

    public FrameworkElement View => _view ??= factory();
    public bool IsCreated => _view != null;
}

public static class NavCatalog
{
    public static IReadOnlyList<NavItem> Build()
    {
        var items = new List<NavItem>
        {
            new("dashboard", "Overview", "", NavSection.Main, () => new DashboardView()),
            new("sensors", "Sensors", "", NavSection.Main, () => new SensorsView())
        };

        foreach (var p in InfoProviders.All)
        {
            var section = p.Category == InfoCategory.Hardware ? NavSection.Hardware : NavSection.Software;
            var provider = p;
            items.Add(new NavItem(p.Id, p.Title, p.Icon, section, () => new InfoPageView(provider), provider));
        }

        items.Add(new("benchmarks", "Benchmarks", "", NavSection.Tools, () => new BenchmarksView()));
        items.Add(new("diskbench", "Disk benchmark", "", NavSection.Tools, () => new DiskBenchView()));
        items.Add(new("stress", "Stability test", "", NavSection.Tools, () => new StressView()));
        items.Add(new("monitortest", "Monitor test", "", NavSection.Tools, () => new MonitorTestView()));
        items.Add(new("report", "Report", "", NavSection.Tools, () => new ReportView()));
        items.Add(new("settings", "Settings", "", NavSection.Main, () => new SettingsView()));
        return items;
    }
}

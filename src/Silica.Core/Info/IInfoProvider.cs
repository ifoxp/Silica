using Silica.Core.Model;

namespace Silica.Core.Info;

public enum InfoCategory { Hardware, Software }

/// <summary>
/// Collects one info page. Implementations are discovered by reflection (public, parameterless constructor),
/// so adding a new page is just adding a new class.
/// </summary>
public interface IInfoProvider
{
    /// <summary>Stable id, e.g. "cpu". Used for navigation, settings and reports.</summary>
    string Id { get; }

    /// <summary>English page title (the UI translates it).</summary>
    string Title { get; }

    InfoCategory Category { get; }

    /// <summary>Sort order inside the category (lower first).</summary>
    int Order { get; }

    /// <summary>Segoe Fluent Icons / MDL2 glyph, e.g. "".</summary>
    string Icon { get; }

    /// <summary>Collects the page. Called on a background thread; may be slow, must not throw for partial failures.</summary>
    InfoPage Collect(CancellationToken ct);
}

public static class InfoProviders
{
    private static IReadOnlyList<IInfoProvider>? _all;

    public static IReadOnlyList<IInfoProvider> All => _all ??= typeof(IInfoProvider).Assembly
        .GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IInfoProvider).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null)
        .Select(t => (IInfoProvider)Activator.CreateInstance(t)!)
        .OrderBy(p => p.Category).ThenBy(p => p.Order)
        .ToList();

    public static IInfoProvider? Find(string id) => All.FirstOrDefault(p => p.Id == id);
}

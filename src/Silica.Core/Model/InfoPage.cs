namespace Silica.Core.Model;

/// <summary>A single "label: value" line on an info page.</summary>
/// <param name="Label">English label; the UI translates it.</param>
/// <param name="LocalizeValue">True when the value is a fixed English word (Yes/No/Enabled…) that the UI may translate.</param>
public sealed record InfoProperty(string Label, string Value, bool LocalizeValue = false);

public abstract class InfoBlock
{
    /// <summary>English title of the block (translated by the UI). May be a device name, which is not translated.</summary>
    public string Title { get; init; } = "";

    /// <summary>When false, the UI shows <see cref="Title"/> as-is (e.g. it is a device name).</summary>
    public bool LocalizeTitle { get; init; } = true;
}

public sealed class PropertyBlock : InfoBlock
{
    public List<InfoProperty> Items { get; } = [];
}

public sealed class TableBlock : InfoBlock
{
    public required string[] Columns { get; init; }
    public List<string[]> Rows { get; } = [];
}

public sealed class InfoPage
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public List<InfoBlock> Blocks { get; } = [];
    public DateTime CollectedAt { get; init; } = DateTime.Now;

    /// <summary>Non-fatal problems while collecting (e.g. access denied for one source).</summary>
    public List<string> Warnings { get; } = [];
}

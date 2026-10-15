using System.Globalization;

namespace Silica.Core.Model;

/// <summary>Fluent helper for building <see cref="InfoPage"/>s. Empty values are skipped automatically.</summary>
public sealed class PageBuilder(string id, string title)
{
    private readonly InfoPage _page = new() { Id = id, Title = title };

    public PropertyBuilder Group(string title, bool localizeTitle = true)
    {
        var block = new PropertyBlock { Title = title, LocalizeTitle = localizeTitle };
        _page.Blocks.Add(block);
        return new PropertyBuilder(block);
    }

    public TableBuilder Table(string title, params string[] columns)
    {
        var block = new TableBlock { Title = title, Columns = columns };
        _page.Blocks.Add(block);
        return new TableBuilder(block);
    }

    public PageBuilder Warn(string message)
    {
        _page.Warnings.Add(message);
        return this;
    }

    /// <summary>Runs <paramref name="section"/>; on failure records a warning instead of failing the whole page.</summary>
    public PageBuilder Try(string what, Action section)
    {
        try { section(); }
        catch (Exception ex) { _page.Warnings.Add($"{what}: {ex.Message}"); }
        return this;
    }

    /// <summary>Builds the page, dropping blocks that ended up empty.</summary>
    public InfoPage Build()
    {
        _page.Blocks.RemoveAll(b => b switch
        {
            PropertyBlock p => p.Items.Count == 0,
            TableBlock t => t.Rows.Count == 0,
            _ => false
        });
        return _page;
    }
}

public sealed class PropertyBuilder(PropertyBlock block)
{
    public PropertyBlock Block => block;

    public PropertyBuilder Add(string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            block.Items.Add(new InfoProperty(label, value.Trim()));
        return this;
    }

    public PropertyBuilder Add(string label, object? value) => value switch
    {
        null => this,
        string s => Add(label, s),
        bool b => Flag(label, b),
        IFormattable f => Add(label, f.ToString(null, CultureInfo.CurrentCulture)),
        _ => Add(label, value.ToString())
    };

    /// <summary>Adds a row whose label is a name from the hardware (a monitor, a memory slot), never translated.</summary>
    public PropertyBuilder Named(string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) block.Items.Add(new InfoProperty(name, value.Trim(), LocalizeLabel: false));
        return this;
    }

    /// <summary>Adds Yes/No (translated by the UI).</summary>
    public PropertyBuilder Flag(string label, bool? value)
    {
        if (value is { } v) block.Items.Add(new InfoProperty(label, v ? "Yes" : "No", LocalizeValue: true));
        return this;
    }

    /// <summary>Adds a fixed English word that the UI may translate (Enabled, Disabled, Running, …).</summary>
    public PropertyBuilder Word(string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) block.Items.Add(new InfoProperty(label, value, LocalizeValue: true));
        return this;
    }
}

public sealed class TableBuilder(TableBlock block)
{
    public TableBlock Block => block;

    public TableBuilder Row(params object?[] cells)
    {
        var row = new string[block.Columns.Length];
        for (var i = 0; i < row.Length; i++)
            row[i] = i < cells.Length ? Convert.ToString(cells[i], CultureInfo.CurrentCulture)?.Trim() ?? "" : "";
        block.Rows.Add(row);
        return this;
    }
}

namespace Silica.Localization;

/// <summary>
/// Decides which table cells are fixed English words worth translating. Names, paths and descriptions
/// come from Windows or vendors and stay as they are.
/// </summary>
public static class ValueVocabulary
{
    /// <summary>Words translated in any column.</summary>
    public static readonly HashSet<string> Words = new(StringComparer.Ordinal)
    {
        "Yes", "No", "Enabled", "Disabled", "Running", "Stopped", "Paused", "OK", "Active", "Inactive", "Connected",
        "Disconnected", "Unknown", "Healthy", "Warning", "Unhealthy", "None", "Ready", "Default", "On", "Off"
    };

    /// <summary>Columns whose values are statuses or categories rather than names.</summary>
    public static readonly HashSet<string> StatusColumns = new(StringComparer.Ordinal)
    {
        "Type", "State", "Status", "Result", "Operation", "Start mode", "Scope", "Level", "Problem", "Health", "Default",
        "Network", "Shared", "Signed", "Disabled", "Password required", "Supported", "Definitions", "Protection", "Log",
        "Media type", "Bus type", "Input type", "Interface", "Partition style", "Associativity"
    };

    public static bool IsStatusColumn(string column) => StatusColumns.Contains(column);

    /// <summary>A cell value that looks like a fixed word or short phrase, not a name with digits or paths.</summary>
    public static bool LooksTranslatable(string cell) =>
        cell.Length is > 0 and <= 40 && !cell.Any(char.IsDigit) && !cell.Contains('\\') && !cell.Contains('.');

    public static string Translate(string cell, string column)
    {
        if (Words.Contains(cell)) return Loc.Instance.T(cell);
        return IsStatusColumn(column) && Loc.Instance.Has(cell) ? Loc.Instance.T(cell) : cell;
    }
}

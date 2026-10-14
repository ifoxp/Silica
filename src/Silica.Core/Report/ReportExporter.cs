using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Silica.Core.Model;

namespace Silica.Core.Report;

public sealed record ReportMeta(string AppName, string AppVersion, string ComputerName, DateTime Created, string Language = "en");

public static class ReportExporter
{
    public static string ToHtml(IReadOnlyList<InfoPage> pages, ReportMeta meta, Func<string, string>? tr = null) =>
        HtmlReport.Render(pages, meta, new Localizer(tr));

    public static string ToText(IReadOnlyList<InfoPage> pages, ReportMeta meta, Func<string, string>? tr = null)
    {
        const int MaxCell = 80;
        var t = new Localizer(tr);
        var sb = new StringBuilder();
        var rule = new string('=', 80);

        var title = $"{meta.AppName} {meta.AppVersion} - {t["Report"]}";
        sb.AppendLine(title).AppendLine(new string('=', title.Length));
        var metaRows = new[] { (t["Computer"], meta.ComputerName), (t["Created"], FormatCreated(meta.Created)) };
        var metaWidth = metaRows.Max(r => r.Item1.Length);
        foreach (var (label, value) in metaRows) sb.Append(label.PadRight(metaWidth)).Append(" : ").AppendLine(value);

        sb.AppendLine().AppendLine(t["Contents"]);
        for (var i = 0; i < pages.Count; i++) sb.AppendLine($"  {i + 1,2}. {t[pages[i].Title]}");

        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            sb.AppendLine().AppendLine(rule).AppendLine($"{i + 1}. {t[page.Title]}").AppendLine(rule);
            foreach (var w in page.Warnings) sb.AppendLine("! " + w);

            foreach (var block in page.Blocks)
            {
                var blockTitle = t.Title(block);
                sb.AppendLine();
                if (blockTitle.Length > 0) sb.AppendLine(blockTitle).AppendLine(new string('-', blockTitle.Length));

                if (block is PropertyBlock pb)
                {
                    var labels = pb.Items.Select(p => t[p.Label]).ToList();
                    var width = Math.Min(40, labels.Max(l => l.Length) + 3);
                    for (var j = 0; j < pb.Items.Count; j++)
                    {
                        var label = labels[j] + " ";
                        var lead = label.Length >= width ? label : label + new string('.', width - label.Length);
                        var lines = OneLine(t.Value(pb.Items[j])).Split('\n');
                        sb.Append("  ").Append(lead).Append(' ').AppendLine(lines[0]);
                        foreach (var more in lines.Skip(1)) sb.Append(' ', width + 3).AppendLine(more);
                    }
                }
                else if (block is TableBlock tb)
                {
                    var cols = tb.Columns.Select(c => t[c]).ToArray();
                    var cells = tb.Rows.Select(r => r.Select(c => Clip(Flat(c), MaxCell)).ToArray()).ToList();
                    var widths = cols.Select((c, k) => Math.Max(c.Length, cells.Count == 0 ? 0 : cells.Max(r => k < r.Length ? r[k].Length : 0))).ToArray();
                    AppendRow(sb, cols, widths);
                    sb.Append("  ").AppendLine(string.Join("  ", widths.Select(w => new string('-', w))));
                    foreach (var r in cells) AppendRow(sb, r, widths);
                }
            }
        }
        return sb.ToString();
    }

    public static string ToMarkdown(IReadOnlyList<InfoPage> pages, ReportMeta meta, Func<string, string>? tr = null)
    {
        var t = new Localizer(tr);
        var sb = new StringBuilder();
        sb.AppendLine($"# {Md(meta.AppName)} {Md(meta.AppVersion)} - {Md(t["Report"])}").AppendLine();
        sb.AppendLine($"**{Md(t["Computer"])}:** {Md(meta.ComputerName)}  ");
        sb.AppendLine($"**{Md(t["Created"])}:** {Md(FormatCreated(meta.Created))}").AppendLine();

        sb.AppendLine($"## {Md(t["Contents"])}").AppendLine();
        for (var i = 0; i < pages.Count; i++) sb.AppendLine($"{i + 1}. [{Md(t[pages[i].Title])}](#{Anchor(pages[i])})");

        foreach (var page in pages)
        {
            sb.AppendLine().AppendLine($"<a id=\"{Anchor(page)}\"></a>").AppendLine().AppendLine($"## {Md(t[page.Title])}").AppendLine();
            foreach (var w in page.Warnings) sb.AppendLine($"> {Md(w)}").AppendLine();

            foreach (var block in page.Blocks)
            {
                var blockTitle = t.Title(block);
                if (blockTitle.Length > 0) sb.AppendLine($"### {Md(blockTitle)}").AppendLine();
                if (block is PropertyBlock pb)
                {
                    foreach (var p in pb.Items) sb.AppendLine($"- **{Md(t[p.Label])}:** {Md(t.Value(p))}");
                }
                else if (block is TableBlock tb)
                {
                    sb.AppendLine("| " + string.Join(" | ", tb.Columns.Select(c => Md(t[c]))) + " |");
                    sb.AppendLine("|" + string.Concat(tb.Columns.Select(_ => " --- |")));
                    foreach (var r in tb.Rows) sb.AppendLine("| " + string.Join(" | ", r.Select(Md)) + " |");
                }
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    public static string ToJson(IReadOnlyList<InfoPage> pages, ReportMeta meta)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("app", meta.AppName);
            w.WriteString("version", meta.AppVersion);
            w.WriteString("computer", meta.ComputerName);
            w.WriteString("created", meta.Created);
            w.WriteStartArray("pages");
            foreach (var page in pages)
            {
                w.WriteStartObject();
                w.WriteString("id", page.Id);
                w.WriteString("title", page.Title);
                w.WriteStartArray("warnings");
                foreach (var warning in page.Warnings) w.WriteStringValue(warning);
                w.WriteEndArray();
                w.WriteStartArray("blocks");
                foreach (var block in page.Blocks) WriteBlock(w, block);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteBlock(Utf8JsonWriter w, InfoBlock block)
    {
        w.WriteStartObject();
        switch (block)
        {
            case PropertyBlock pb:
                w.WriteString("kind", "properties");
                w.WriteString("title", pb.Title);
                w.WriteStartArray("items");
                foreach (var p in pb.Items)
                {
                    w.WriteStartObject();
                    w.WriteString("label", p.Label);
                    w.WriteString("value", p.Value);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                break;
            case TableBlock tb:
                w.WriteString("kind", "table");
                w.WriteString("title", tb.Title);
                w.WriteStartArray("columns");
                foreach (var c in tb.Columns) w.WriteStringValue(c);
                w.WriteEndArray();
                w.WriteStartArray("rows");
                foreach (var r in tb.Rows)
                {
                    w.WriteStartArray();
                    foreach (var c in r) w.WriteStringValue(c);
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                break;
            default:
                w.WriteString("kind", "unknown");
                w.WriteString("title", block.Title);
                break;
        }
        w.WriteEndObject();
    }

    internal static string FormatCreated(DateTime d) => d.ToString("f", CultureInfo.CurrentCulture);

    internal static string Anchor(InfoPage page) => "page-" + page.Id;

    private static void AppendRow(StringBuilder sb, IReadOnlyList<string> cells, int[] widths)
    {
        sb.Append("  ");
        for (var k = 0; k < widths.Length; k++)
        {
            var c = k < cells.Count ? cells[k] : "";
            sb.Append(k == widths.Length - 1 ? c : c.PadRight(widths[k] + 2));
        }
        sb.AppendLine();
    }

    private static string OneLine(string s) => Clean(s.Replace("\r\n", "\n").Replace('\r', '\n'));

    private static string Flat(string s) => Clean(s.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' '));

    // Drops control characters (stray NULs in registry data, tabs) that would break alignment or markup.
    internal static string Clean(string s)
    {
        if (!s.Any(c => c < ' ' && c != '\n')) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c == '\t') sb.Append(' ');
            else if (c >= ' ' || c == '\n') sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "\u2026";

    private static string Md(string s)
    {
        var flat = Flat(s);
        var sb = new StringBuilder(flat.Length + 8);
        for (var i = 0; i < flat.Length; i++)
        {
            var ch = flat[i];
            // A backslash only escapes ASCII punctuation, so paths like C:\Users can stay as they are.
            var escape = ch is '`' or '*' or '_' or '[' or ']' or '<' or '>' or '|' or '#'
                || ch == '\\' && i + 1 < flat.Length && flat[i + 1] is > ' ' and < '\x7F' && !char.IsAsciiLetterOrDigit(flat[i + 1]);
            if (escape) sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }
}

internal sealed class Localizer(Func<string, string>? tr)
{
    public string this[string english] => string.IsNullOrEmpty(english) || tr == null ? english : tr(english) ?? english;

    public string Title(InfoBlock block) => block.LocalizeTitle ? this[block.Title] : block.Title;

    public string Value(InfoProperty p) => p.LocalizeValue ? this[p.Value] : p.Value;
}

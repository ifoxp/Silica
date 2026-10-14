using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Text.RegularExpressions;
using Silica.Core.Model;

namespace Silica.Core.Report;

internal static partial class HtmlReport
{
    private const string Css = """
        :root {
          color-scheme: light dark;
          --bg: #f4f6fa;
          --surface: #ffffff;
          --surface-alt: #f7f9fc;
          --border: #e2e7ef;
          --text: #18202c;
          --muted: #5e6b80;
          --accent: #4f8cff;
          --accent-text: #2f6fe6;
          --accent-soft: rgba(79, 140, 255, 0.12);
          --warn-bg: #fff6e3;
          --warn-border: #f2c46d;
          --warn-text: #7a5300;
          --shadow: 0 1px 2px rgba(16, 24, 40, 0.04), 0 4px 16px rgba(16, 24, 40, 0.05);
          --radius: 12px;
        }
        @media (prefers-color-scheme: dark) {
          :root {
            --bg: #0e1116;
            --surface: #161a22;
            --surface-alt: #1a1f29;
            --border: #262d3a;
            --text: #e5e9f0;
            --muted: #8f9bb0;
            --accent-text: #7eaaff;
            --accent-soft: rgba(79, 140, 255, 0.16);
            --warn-bg: rgba(255, 184, 48, 0.10);
            --warn-border: rgba(255, 184, 48, 0.45);
            --warn-text: #ffcf70;
            --shadow: 0 1px 2px rgba(0, 0, 0, 0.3);
          }
        }
        * { box-sizing: border-box; }
        html { scroll-behavior: smooth; -webkit-text-size-adjust: 100%; }
        body {
          margin: 0; background: var(--bg); color: var(--text);
          font: 14px/1.5 "Segoe UI Variable Text", "Segoe UI Variable", "Segoe UI", system-ui, -apple-system, sans-serif;
        }
        a { color: var(--accent-text); text-decoration: none; }
        a:hover { text-decoration: underline; }
        .layout { display: grid; grid-template-columns: 264px minmax(0, 1fr); min-height: 100vh; }
        .toc {
          position: sticky; top: 0; height: 100vh; overflow-y: auto;
          padding: 28px 18px; border-right: 1px solid var(--border); background: var(--surface);
        }
        .brand { display: flex; align-items: center; gap: 12px; padding: 0 8px 24px; }
        .mark { width: 30px; height: 30px; flex: none; display: block; }
        .brand-name { font-weight: 650; font-size: 15px; letter-spacing: 0.01em; }
        .brand-ver { color: var(--muted); font-size: 12px; }
        .toc-title {
          padding: 0 8px 8px; color: var(--muted); font-size: 11px; font-weight: 600;
          text-transform: uppercase; letter-spacing: 0.08em;
        }
        .toc ol { list-style: none; margin: 0; padding: 0; counter-reset: toc; }
        .toc li { counter-increment: toc; }
        .toc a {
          display: flex; gap: 10px; padding: 7px 8px; border-radius: 8px; color: var(--text);
        }
        .toc a::before {
          content: counter(toc, decimal-leading-zero); color: var(--muted);
          font-variant-numeric: tabular-nums; font-size: 12px; padding-top: 1px;
        }
        .toc a:hover { background: var(--accent-soft); color: var(--accent-text); text-decoration: none; }
        main { padding: 40px 48px 64px; max-width: 1280px; width: 100%; }
        .hero { padding-bottom: 28px; margin-bottom: 8px; border-bottom: 1px solid var(--border); }
        .eyebrow {
          display: inline-block; padding: 3px 10px; border-radius: 999px; background: var(--accent-soft);
          color: var(--accent-text); font-size: 12px; font-weight: 600; letter-spacing: 0.04em;
        }
        h1 { font-size: 30px; line-height: 1.2; font-weight: 650; margin: 14px 0 16px; overflow-wrap: anywhere; }
        .meta { display: flex; flex-wrap: wrap; gap: 8px 32px; margin: 0; }
        .meta dt { color: var(--muted); font-size: 12px; }
        .meta dd { margin: 2px 0 0; font-weight: 500; }
        .page { padding-top: 36px; scroll-margin-top: 12px; }
        h2 {
          display: flex; align-items: baseline; gap: 12px; font-size: 21px; font-weight: 650; margin: 0 0 16px;
        }
        h2 .num { color: var(--accent-text); font-size: 14px; font-variant-numeric: tabular-nums; }
        .notice {
          margin: 0 0 16px; padding: 10px 14px; list-style: none; border: 1px solid var(--warn-border);
          border-left-width: 4px; border-radius: 8px; background: var(--warn-bg); color: var(--warn-text); font-size: 13px;
        }
        .notice li + li { margin-top: 4px; }
        .cards { display: grid; grid-template-columns: repeat(auto-fill, minmax(360px, 1fr)); gap: 16px; margin-bottom: 16px; }
        .card {
          background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius);
          box-shadow: var(--shadow); min-width: 0;
        }
        .table-card { margin-bottom: 16px; }
        h3 {
          display: flex; align-items: center; gap: 8px; margin: 0; padding: 13px 18px;
          font-size: 13px; font-weight: 600; border-bottom: 1px solid var(--border);
        }
        h3 .count {
          margin-left: auto; padding: 1px 8px; border-radius: 999px; background: var(--surface-alt);
          border: 1px solid var(--border); color: var(--muted); font-size: 11px; font-weight: 500;
          font-variant-numeric: tabular-nums;
        }
        .kv { display: grid; grid-template-columns: minmax(130px, 42%) minmax(0, 1fr); margin: 0; padding: 6px 0; }
        .kv dt, .kv dd { margin: 0; padding: 6px 18px; border-top: 1px solid var(--border); }
        .kv dt:first-of-type, .kv dt:first-of-type + dd { border-top: 0; }
        .kv dt { color: var(--muted); }
        .kv dd { overflow-wrap: anywhere; font-weight: 500; padding-left: 0; }
        .scroll { overflow-x: auto; }
        table { border-collapse: collapse; width: 100%; font-size: 13px; }
        th {
          text-align: left; font-weight: 600; color: var(--muted); font-size: 12px; white-space: nowrap;
          padding: 9px 14px; background: var(--surface-alt); border-bottom: 1px solid var(--border);
        }
        td { padding: 7px 14px; vertical-align: top; border-bottom: 1px solid var(--border); overflow-wrap: break-word; }
        tbody tr:nth-child(even) td { background: var(--surface-alt); }
        tbody tr:last-child td { border-bottom: 0; }
        tbody tr:hover td { background: var(--accent-soft); }
        .r { text-align: right; font-variant-numeric: tabular-nums; white-space: nowrap; }
        .nw { white-space: nowrap; }
        footer { margin-top: 48px; color: var(--muted); font-size: 12px; }
        @media (max-width: 960px) {
          .layout { display: block; }
          .toc { position: static; height: auto; border-right: 0; border-bottom: 1px solid var(--border); padding: 20px 16px 16px; }
          .brand { padding: 0 0 16px; }
          .toc-title { padding: 0 0 8px; }
          .toc ol { display: flex; flex-wrap: wrap; gap: 6px; }
          .toc a { padding: 5px 10px; border: 1px solid var(--border); border-radius: 999px; font-size: 13px; }
          .toc a::before { font-size: 11px; }
          main { padding: 24px 16px 48px; }
          .cards { grid-template-columns: minmax(0, 1fr); }
        }
        @media (max-width: 520px) {
          h1 { font-size: 24px; }
          .kv { grid-template-columns: minmax(0, 1fr); }
          .kv dt { padding-bottom: 0; }
          .kv dd { border-top: 0; padding: 0 18px 6px; }
        }
        @media print {
          :root {
            --bg: #fff; --surface: #fff; --surface-alt: #f5f6f8; --border: #d9dde4; --text: #000; --muted: #444;
            --accent-text: #1f5fd6; --shadow: none;
          }
          body { font-size: 11px; }
          .layout { display: block; }
          .toc { display: none; }
          main { padding: 0; max-width: none; }
          .page { break-before: page; padding-top: 0; }
          .card { break-inside: avoid; }
          .table-card { break-inside: auto; }
          .scroll { overflow: visible; }
          tr { break-inside: avoid; }
          tbody tr:hover td { background: inherit; }
          footer { display: none; }
        }
        """;

    public static string Render(IReadOnlyList<InfoPage> pages, ReportMeta meta, Localizer t)
    {
        var sb = new StringBuilder(64 * 1024);
        var created = ReportExporter.FormatCreated(meta.Created);
        sb.Append("<!DOCTYPE html>\n<html lang=\"").Append(E(meta.Language)).Append("\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append("<meta name=\"generator\" content=\"").Append(E($"{meta.AppName} {meta.AppVersion}")).Append("\">\n")
            .Append("<title>").Append(E($"{meta.ComputerName} - {t["Report"]} - {meta.AppName}")).Append("</title>\n")
            .Append("<style>\n").Append(Css).Append("</style>\n</head>\n<body>\n<div class=\"layout\">\n");

        sb.Append("<nav class=\"toc\">\n<div class=\"brand\"><svg class=\"mark\" viewBox=\"0 0 256 256\" aria-hidden=\"true\"><defs><linearGradient id=\"g1\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\"><stop offset=\"0\" stop-color=\"#B9F0FF\"/><stop offset=\"1\" stop-color=\"#7CCBFF\"/></linearGradient><linearGradient id=\"g2\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\"><stop offset=\"0\" stop-color=\"#73BEFF\"/><stop offset=\"1\" stop-color=\"#4F8DFF\"/></linearGradient><linearGradient id=\"g3\" x1=\"0\" y1=\"0\" x2=\"0.4\" y2=\"1\"><stop offset=\"0\" stop-color=\"#5A9BFF\"/><stop offset=\"1\" stop-color=\"#3A63E8\"/></linearGradient><linearGradient id=\"g4\" x1=\"0\" y1=\"0\" x2=\"0.6\" y2=\"1\"><stop offset=\"0\" stop-color=\"#3557DA\"/><stop offset=\"1\" stop-color=\"#5A3FD1\"/></linearGradient></defs><polygon points=\"128,4 50,96 128,122\" fill=\"url(#g1)\"/><polygon points=\"128,4 128,122 206,96\" fill=\"url(#g2)\"/><polygon points=\"50,96 128,122 128,252 50,200\" fill=\"url(#g3)\"/><polygon points=\"128,122 206,96 206,200 128,252\" fill=\"url(#g4)\"/></svg><div><div class=\"brand-name\">")
            .Append(E(meta.AppName)).Append("</div><div class=\"brand-ver\">").Append(E(meta.AppVersion)).Append("</div></div></div>\n")
            .Append("<div class=\"toc-title\">").Append(E(t["Contents"])).Append("</div>\n<ol>\n");
        foreach (var page in pages)
            sb.Append("<li><a href=\"#").Append(ReportExporter.Anchor(page)).Append("\">").Append(E(t[page.Title])).Append("</a></li>\n");
        sb.Append("</ol>\n</nav>\n<main>\n");

        sb.Append("<header class=\"hero\">\n<span class=\"eyebrow\">").Append(E(t["Report"])).Append("</span>\n")
            .Append("<h1>").Append(E(meta.ComputerName)).Append("</h1>\n<dl class=\"meta\">\n");
        AppendMeta(sb, t["Computer"], meta.ComputerName);
        AppendMeta(sb, t["Created"], created);
        AppendMeta(sb, meta.AppName, meta.AppVersion);
        sb.Append("</dl>\n</header>\n");

        for (var i = 0; i < pages.Count; i++) AppendPage(sb, pages[i], i + 1, t);

        sb.Append("<footer>").Append(E($"{meta.AppName} {meta.AppVersion}")).Append(" &middot; ").Append(E(created)).Append("</footer>\n");
        sb.Append("</main>\n</div>\n</body>\n</html>\n");
        return sb.ToString();
    }

    private static void AppendMeta(StringBuilder sb, string label, string value) =>
        sb.Append("<div><dt>").Append(E(label)).Append("</dt><dd>").Append(E(value)).Append("</dd></div>\n");

    private static void AppendPage(StringBuilder sb, InfoPage page, int number, Localizer t)
    {
        sb.Append("<section class=\"page\" id=\"").Append(ReportExporter.Anchor(page)).Append("\">\n")
            .Append("<h2><span class=\"num\">").Append(number.ToString("00")).Append("</span>").Append(E(t[page.Title])).Append("</h2>\n");

        if (page.Warnings.Count > 0)
        {
            sb.Append("<ul class=\"notice\">\n");
            foreach (var w in page.Warnings) sb.Append("<li>").Append(E(w)).Append("</li>\n");
            sb.Append("</ul>\n");
        }

        // Consecutive property blocks share one responsive card grid; tables span the full width.
        var inGrid = false;
        foreach (var block in page.Blocks)
        {
            if (block is PropertyBlock && !inGrid) { sb.Append("<div class=\"cards\">\n"); inGrid = true; }
            if (block is not PropertyBlock && inGrid) { sb.Append("</div>\n"); inGrid = false; }

            switch (block)
            {
                case PropertyBlock pb: AppendProperties(sb, pb, t); break;
                case TableBlock tb: AppendTable(sb, tb, t); break;
            }
        }
        if (inGrid) sb.Append("</div>\n");
        sb.Append("</section>\n");
    }

    private static void AppendProperties(StringBuilder sb, PropertyBlock block, Localizer t)
    {
        sb.Append("<article class=\"card\">\n");
        AppendTitle(sb, t.Title(block), null);
        sb.Append("<dl class=\"kv\">\n");
        foreach (var p in block.Items)
            sb.Append("<dt>").Append(E(t[p.Label])).Append("</dt><dd>").Append(Multiline(t.Value(p))).Append("</dd>\n");
        sb.Append("</dl>\n</article>\n");
    }

    private static void AppendTable(StringBuilder sb, TableBlock block, Localizer t)
    {
        // Numbers align right; columns of short values (dates, states) never wrap.
        var open = new string[block.Columns.Length];
        var head = new string[block.Columns.Length];
        for (var c = 0; c < open.Length; c++)
        {
            var values = block.Rows.Select(r => c < r.Length ? r[c] : "").Where(v => v.Length > 0).ToList();
            var numeric = values.Count > 0 && values.All(v => NumericCell().IsMatch(v) && !VersionLike().IsMatch(v));
            var shortValues = values.Count > 0 && values.Max(v => v.Length) <= 24;
            open[c] = numeric ? "<td class=\"r\">" : shortValues ? "<td class=\"nw\">" : "<td>";
            head[c] = numeric ? "<th class=\"r\">" : "<th>";
        }

        sb.Append("<article class=\"card table-card\">\n");
        AppendTitle(sb, t.Title(block), block.Rows.Count);
        sb.Append("<div class=\"scroll\">\n<table>\n<thead><tr>");
        for (var c = 0; c < block.Columns.Length; c++)
            sb.Append(head[c]).Append(E(t[block.Columns[c]])).Append("</th>");
        sb.Append("</tr></thead>\n<tbody>\n");
        foreach (var row in block.Rows)
        {
            sb.Append("<tr>");
            for (var c = 0; c < block.Columns.Length; c++)
                sb.Append(open[c]).Append(Breakable(c < row.Length ? row[c] : "")).Append("</td>");
            sb.Append("</tr>\n");
        }
        sb.Append("</tbody>\n</table>\n</div>\n</article>\n");
    }

    private static void AppendTitle(StringBuilder sb, string title, int? count)
    {
        if (title.Length == 0 && count == null) return;
        sb.Append("<h3>").Append(E(title));
        if (count != null) sb.Append("<span class=\"count\">").Append(count.Value.ToString("N0")).Append("</span>");
        sb.Append("</h3>\n");
    }

    private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);

    private static string E(string s) => Encoder.Encode(ReportExporter.Clean(s));

    private static string Multiline(string s) => E(s.Replace("\r\n", "\n")).Replace("&#xA;", "<br>");

    // Lets long paths and URLs wrap at separators instead of stretching the column.
    private static string Breakable(string s)
    {
        var encoded = E(s);
        return s.Length < 32 ? encoded : SeparatorAfter().Replace(encoded, "$0<wbr>");
    }

    [GeneratedRegex(@"\\|/|&#x5C;|&#x2F;")]
    private static partial Regex SeparatorAfter();

    [GeneratedRegex(@"^[+-]?\d[\d\s\u00A0\u202F.,']*(\s?(B|KB|MB|GB|TB|%|d|\+))?$")]
    private static partial Regex NumericCell();

    [GeneratedRegex(@"^\d+\.\d+\.\d+")]
    private static partial Regex VersionLike();
}

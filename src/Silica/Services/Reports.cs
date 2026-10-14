using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using Silica.Core.Info;
using Silica.Core.Model;
using Silica.Core.Report;
using Silica.Localization;

namespace Silica.Services;

public enum ReportFormat { Html, Text, Markdown, Json }

public static class Reports
{
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0";

    public static ReportMeta Meta() => new("Silica", Version, Environment.MachineName, DateTime.Now);

    public static string Render(IReadOnlyList<InfoPage> pages, ReportFormat format) => format switch
    {
        ReportFormat.Html => ReportExporter.ToHtml(pages, Meta(), Loc.Instance.T),
        ReportFormat.Markdown => ReportExporter.ToMarkdown(pages, Meta(), Loc.Instance.T),
        ReportFormat.Json => ReportExporter.ToJson(pages, Meta()),
        _ => ReportExporter.ToText(pages, Meta(), Loc.Instance.T)
    };

    public static ReportFormat FormatFromExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".htm" or ".html" => ReportFormat.Html,
        ".md" => ReportFormat.Markdown,
        ".json" => ReportFormat.Json,
        _ => ReportFormat.Text
    };

    public static void Write(string path, IReadOnlyList<InfoPage> pages, ReportFormat format)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, Render(pages, format), new UTF8Encoding(false));
    }

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length == 0 ? "Silica" : clean;
    }

    public static string DefaultName() => $"Silica {Environment.MachineName} {DateTime.Now:yyyy-MM-dd HHmm}";

    /// <summary>Shows a save dialog and writes the report. Returns the path or null if cancelled.</summary>
    public static string? SaveWithDialog(IReadOnlyList<InfoPage> pages, string fileName, Window? owner)
    {
        var loc = Loc.Instance;
        var dialog = new SaveFileDialog
        {
            FileName = fileName,
            InitialDirectory = Directory.Exists(AppPaths.DefaultReportFolder) ? AppPaths.DefaultReportFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Filter = $"{loc.T("Web page")} (*.html)|*.html|{loc.T("Plain text")} (*.txt)|*.txt|Markdown (*.md)|*.md|JSON (*.json)|*.json",
            AddExtension = true
        };
        if (dialog.ShowDialog(owner) != true) return null;
        try
        {
            Write(dialog.FileName, pages, FormatFromExtension(dialog.FileName));
            (owner as Views.MainWindow)?.ShowToast(loc.T("Report saved"));
            return dialog.FileName;
        }
        catch (Exception ex)
        {
            Log.Error("Report not saved", ex);
            Views.ErrorDialog.Show(owner, ex, loc.T("The report could not be saved."));
            return null;
        }
    }
}

/// <summary>silica.exe --report file.html: collects every page without showing a window.</summary>
public static class HeadlessReport
{
    public static int Run(string path)
    {
        try
        {
            var pages = InfoProviders.All
                .Select(p => PageStore.GetAsync(p))
                .ToList()
                .Select(t => t.GetAwaiter().GetResult())
                .ToList();
            Reports.Write(path, pages, Reports.FormatFromExtension(path));
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Headless report failed", ex);
            return 1;
        }
    }
}

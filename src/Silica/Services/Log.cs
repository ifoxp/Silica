using System.IO;

namespace Silica.Services;

/// <summary>Small append-only log for warnings and errors. Rolls over at 512 KB.</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                AppPaths.EnsureDataDirectory();
                var file = new FileInfo(AppPaths.LogFile);
                if (file is { Exists: true, Length: > 512 * 1024 })
                    File.Move(file.FullName, file.FullName + ".old", overwrite: true);
                File.AppendAllText(file.FullName, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never take the app down */ }
    }
}

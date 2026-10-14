using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Silica.Core.Model;
using Silica.Core.Util;

namespace Silica.Core.Info.Software;

public sealed class ProcessesProvider : IInfoProvider
{
    public string Id => "processes";
    public string Title => "Processes";
    public InfoCategory Category => InfoCategory.Software;
    public int Order => 10;
    public string Icon => "\uE9D9";

    private sealed record Proc(string Name, int Pid, long WorkingSet, long Private, int Threads, int Handles, DateTime? Started, string Path);

    public InfoPage Collect(CancellationToken ct)
    {
        var b = new PageBuilder(Id, Title);
        b.Try("Processes", () =>
        {
            var list = new List<Proc>();
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        var (path, started) = QueryImage(p.Id);
                        list.Add(new Proc(p.ProcessName, p.Id, p.WorkingSet64, p.PrivateMemorySize64,
                            p.Threads.Count, p.HandleCount, started, path ?? ""));
                    }
                    catch (InvalidOperationException) { } // exited meanwhile
                }
            }
            ct.ThrowIfCancellationRequested();

            var paths = list.Select(x => x.Path).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
            Parallel.ForEach(paths, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                path => SoftwareHelpers.FileDescription(path));

            b.Group("Summary")
                .Add("Processes", Fmt.Number(list.Count))
                .Add("Threads", Fmt.Number(list.Sum(x => x.Threads)))
                .Add("Handles", Fmt.Number(list.Sum(x => (long)x.Handles)))
                .Add("Memory in use (working set)", Fmt.Bytes(list.Sum(x => x.WorkingSet)));

            var t = b.Table("Processes", "Name", "PID", "Memory", "Private memory", "Threads", "Handles", "Start time", "Path", "Description");
            foreach (var x in list.OrderByDescending(x => x.WorkingSet))
            {
                t.Row(x.Name, x.Pid, Fmt.Bytes(x.WorkingSet), Fmt.Bytes(x.Private), x.Threads, x.Handles,
                    Fmt.DateTime(x.Started), x.Path, x.Path.Length > 0 ? SoftwareHelpers.FileDescription(x.Path) : "");
            }
        });
        if (!SoftwareHelpers.IsElevated())
            b.Warn("Paths and start times of some system processes are only visible when running as administrator.");
        return b.Build();
    }

    // Avoids Process.MainModule/StartTime, which throw (slowly) for most system processes.
    private static (string? Path, DateTime? Started) QueryImage(int pid)
    {
        if (pid <= 4) return (null, null);
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return (null, null);
        try
        {
            string? path = null;
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            if (QueryFullProcessImageName(h, 0, sb, ref size)) path = sb.ToString(0, size);

            DateTime? started = null;
            if (GetProcessTimes(h, out var creation, out _, out _, out _) && creation > 0)
                started = DateTime.FromFileTime(creation);
            return (path, started);
        }
        finally { CloseHandle(h); }
    }

    private const int ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}

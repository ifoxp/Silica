using Silica.Core.Model;
using Silica.Core.Util;

namespace Silica.Core.Info.Software;

public sealed class TasksProvider : IInfoProvider
{
    public string Id => "tasks";
    public string Title => "Scheduled tasks";
    public InfoCategory Category => InfoCategory.Software;
    public int Order => 35;
    public string Icon => "\uE823";

    private sealed record TaskInfo(string Name, string Folder, string State, DateTime? LastRun, DateTime? NextRun, int LastResult, string Author);

    private sealed class Result
    {
        public List<TaskInfo> Tasks { get; } = [];
        public int MicrosoftTasks { get; set; }
    }

    public InfoPage Collect(CancellationToken ct)
    {
        var b = new PageBuilder(Id, Title);
        b.Try("Task Scheduler", () =>
        {
            if (!SoftwareHelpers.TryRun(() => Enumerate(ct), TimeSpan.FromSeconds(10), out var result) || result == null)
            {
                b.Warn("Task Scheduler did not respond in time.");
                return;
            }

            b.Group("Summary")
                .Add("Tasks shown", Fmt.Number(result.Tasks.Count))
                .Add("Running", Fmt.Number(result.Tasks.Count(t => t.State == "Running")))
                .Add("Disabled", Fmt.Number(result.Tasks.Count(t => t.State == "Disabled")))
                .Add("Microsoft tasks (hidden)", Fmt.Number(result.MicrosoftTasks));

            var t = b.Table("Scheduled tasks", "Name", "Folder", "State", "Last run", "Next run", "Last result", "Author");
            foreach (var x in result.Tasks.OrderBy(x => x.Folder, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                t.Row(x.Name, x.Folder, x.State, Fmt.DateTime(x.LastRun), Fmt.DateTime(x.NextRun),
                    x.LastRun == null ? "" : Fmt.Hex((uint)x.LastResult), x.Author);
            }
        });
        if (!SoftwareHelpers.IsElevated())
            b.Warn("Tasks created by other users are only visible when running as administrator.");
        return b.Build();
    }

    private static Result Enumerate(CancellationToken ct)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler is not available");
        dynamic service = Activator.CreateInstance(type)!;
        var result = new Result();
        try
        {
            service.Connect();
            dynamic root = service.GetFolder("\\");
            Walk(root, result, ct);
        }
        finally { SoftwareHelpers.Release(service); }
        return result;
    }

    private static void Walk(dynamic folder, Result result, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            string path = folder.Path;
            var isMicrosoft = path.StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase);

            dynamic tasks = folder.GetTasks(1); // TASK_ENUM_HIDDEN
            try
            {
                int count = tasks.Count;
                if (isMicrosoft) result.MicrosoftTasks += count;
                else
                    for (var i = 1; i <= count; i++)
                        result.Tasks.Add(ReadTask(tasks[i]));
            }
            finally { SoftwareHelpers.Release(tasks); }

            dynamic folders = folder.GetFolders(0);
            try
            {
                int count = folders.Count;
                for (var i = 1; i <= count; i++) Walk(folders[i], result, ct);
            }
            finally { SoftwareHelpers.Release(folders); }
        }
        finally { SoftwareHelpers.Release(folder); }
    }

    private static TaskInfo ReadTask(dynamic task)
    {
        try
        {
            string path = task.Path;
            string name = task.Name;
            var folder = path.Length > name.Length ? path[..^(name.Length + 1)] : "";
            int state = task.State;

            var author = "";
            try
            {
                dynamic def = task.Definition;
                dynamic info = def.RegistrationInfo;
                author = (string?)info.Author ?? "";
                SoftwareHelpers.Release(info);
                SoftwareHelpers.Release(def);
            }
            catch { }

            return new TaskInfo(name, folder.Length == 0 ? "\\" : folder, StateName(state),
                SoftwareHelpers.ComDate((object)task.LastRunTime), SoftwareHelpers.ComDate((object)task.NextRunTime), (int)task.LastTaskResult, author);
        }
        finally { SoftwareHelpers.Release(task); }
    }

    private static string StateName(int state) => state switch
    {
        1 => "Disabled",
        2 => "Queued",
        3 => "Ready",
        4 => "Running",
        _ => "Unknown"
    };
}

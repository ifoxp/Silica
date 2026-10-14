using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;

namespace Silica.Services;

/// <summary>
/// Starts Silica at sign-in through Task Scheduler. A Run registry key cannot start an app that requires
/// administrator rights without a UAC prompt; a task with the highest run level can.
/// </summary>
public static class StartupService
{
    private const string TaskName = "Silica";

    public static bool IsEnabled()
    {
        object? service = null, folder = null, task = null;
        try
        {
            service = Connect();
            folder = ((dynamic)service).GetFolder("\\");
            task = ((dynamic)folder).GetTask(TaskName);
            return task != null;
        }
        catch (COMException) { return false; }
        finally { Release(task, folder, service); }
    }

    public static void Set(bool enabled)
    {
        object? service = null, folder = null;
        try
        {
            service = Connect();
            folder = ((dynamic)service).GetFolder("\\");
            if (enabled)
                ((dynamic)folder).RegisterTask(TaskName, TaskXml(), 6, null, null, 3, null);
            else
            {
                try { ((dynamic)folder).DeleteTask(TaskName, 0); } catch (COMException) { }
            }
        }
        finally { Release(folder, service); }
    }

    private static object Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new COMException("Task Scheduler is not available");
        var service = Activator.CreateInstance(type)!;
        ((dynamic)service).Connect();
        return service;
    }

    private static void Release(params object?[] objects)
    {
        foreach (var o in objects)
            if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
    }

    private static string TaskXml()
    {
        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        var exe = SecurityElement.Escape(AppPaths.ExecutablePath);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts Silica when you sign in.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>PT10S</Delay></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>"{exe}"</Command><Arguments>--minimized</Arguments></Exec></Actions>
            </Task>
            """;
    }
}

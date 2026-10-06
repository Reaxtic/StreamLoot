using System.Runtime.InteropServices;
using System.Security.Principal;
using Core.Logging;

namespace Core.Services;

public static class WindowsAutoStartService
{
    public static string TaskName(string sid) => "StreamLoot.Autostart." + sid;

    public static bool Configure(bool enabled, string exePath, bool minimize)
    {
        object? serviceObject = null;
        object? folderObject = null;
        object? taskObject = null;
        try
        {
            string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("No current user SID.");
            serviceObject = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!);
            dynamic service = serviceObject!;
            service.Connect();
            folderObject = service.GetFolder("\\");
            dynamic folder = folderObject;
            string name = TaskName(sid);
            if (!enabled)
            {
                try { folder.DeleteTask(name, 0); }
                catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { }
                return true;
            }

            // Avoid replacing a currently running task on every app launch.
            try
            {
                taskObject = folder.GetTask(name);
                dynamic existing = taskObject;
                dynamic action = existing.Definition.Actions.Item(1);
                if ((bool)existing.Enabled && (string)action.Path == exePath
                    && (string)action.Arguments == StartupTaskPolicy.Arguments(minimize))
                    return true;
            }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { }
            if (taskObject != null) { Marshal.FinalReleaseComObject(taskObject); taskObject = null; }
            taskObject = folder.RegisterTask(name, StartupTaskPolicy.BuildXml(exePath, sid, minimize),
                6, sid, null, 3, null);
            AppLogger.Info("Autostart", "Registered user-logon task with a 30-second delay and explicit working directory.");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Autostart", $"Task Scheduler configuration failed; retaining registry fallback. {ex.Message}");
            return false;
        }
        finally
        {
            foreach (object? com in new[] { taskObject, folderObject, serviceObject })
                if (com != null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com);
        }
    }
}

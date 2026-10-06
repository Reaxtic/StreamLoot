using System.Xml.Linq;

namespace Core.Services;

public static class StartupTaskPolicy
{
    public static string Arguments(bool minimize) => minimize ? "--autostart --minimize" : "--autostart";

    public static string BuildXml(string exePath, string userSid, bool minimize)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement E(string name, object value) => new(ns + name, value);
        return new XElement(ns + "Task", new XAttribute("version", "1.2"),
            E("RegistrationInfo", E("Description", "Stream Loot: start after user logon.")),
            E("Triggers", E("LogonTrigger", new object[] { E("Enabled", true), E("UserId", userSid), E("Delay", "PT30S") })),
            E("Principals", new XElement(ns + "Principal", new XAttribute("id", "User"),
                E("UserId", userSid), E("LogonType", "InteractiveToken"), E("RunLevel", "LeastPrivilege"))),
            E("Settings", new object[] {
                E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", false),
                E("StopIfGoingOnBatteries", false), E("StartWhenAvailable", true),
                E("AllowStartOnDemand", true), E("Enabled", true), E("RunOnlyIfIdle", false),
                E("ExecutionTimeLimit", "PT0S") }),
            new XElement(ns + "Actions", new XAttribute("Context", "User"),
                E("Exec", new object[] { E("Command", exePath), E("Arguments", Arguments(minimize)),
                    E("WorkingDirectory", System.IO.Path.GetDirectoryName(exePath) ?? "") })))
            .ToString();
    }
}

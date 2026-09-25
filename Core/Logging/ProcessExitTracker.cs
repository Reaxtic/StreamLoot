using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Core.Logging
{
    /// <summary>Records intentional exits, including paths that bypass WPF's OnExit.</summary>
    public static class ProcessExitTracker
    {
        private static string? _reason;
        private static string? _runMarker;
        private static int _finalExitLogged;

        public static void Initialize()
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => LogExit("ProcessExit");
            try
            {
                string processName = Process.GetCurrentProcess().ProcessName;
                foreach (string marker in Directory.EnumerateFiles(AppLogger.LogDirectoryPath, "run-*.active"))
                {
                    string[] parts = Path.GetFileNameWithoutExtension(marker).Split('-');
                    bool stillRunning = false;
                    if (parts.Length > 1 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
                    {
                        try
                        {
                            using Process previous = Process.GetProcessById(pid);
                            stillRunning = !previous.HasExited && previous.ProcessName == processName;
                        }
                        catch (ArgumentException) { }
                        catch (InvalidOperationException) { }
                        catch (System.ComponentModel.Win32Exception) { }
                    }

                    if (stillRunning)
                        continue;

                    AppLogger.Warn("Exit", $"Previous run ended without a recorded exit (crash, forced termination, or power loss). marker={Path.GetFileName(marker)}; started={File.ReadAllText(marker)}");
                    File.Delete(marker);
                }

                _runMarker = Path.Combine(AppLogger.LogDirectoryPath, $"run-{Environment.ProcessId}-{Guid.NewGuid():N}.active");
                File.WriteAllText(_runMarker, DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                AppLogger.Error("Exit", "Could not maintain process run marker.", ex);
            }
        }

        public static void RecordReason(string reason)
        {
            Volatile.Write(ref _reason, reason);
            AppLogger.Info("Exit", $"Exit requested. pid={Environment.ProcessId}; reason={reason}");
        }

        public static void RecordReasonIfUnset(string reason)
        {
            if (Interlocked.CompareExchange(ref _reason, reason, null) == null)
                AppLogger.Info("Exit", $"Exit requested. pid={Environment.ProcessId}; reason={reason}");
        }

        public static void LogExit(string source, int? exitCode = null)
        {
            if (Interlocked.Exchange(ref _finalExitLogged, 1) != 0)
                return;

            string reason = Volatile.Read(ref _reason) ?? "No explicit exit reason (external termination or untracked shutdown)";
            AppLogger.Info("Exit", $"Process exiting. pid={Environment.ProcessId}; source={source}; exitCode={exitCode?.ToString() ?? "unknown"}; reason={reason}");
            try
            {
                if (_runMarker != null)
                    File.Delete(_runMarker);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Exit", "Could not clear process run marker.", ex);
            }
        }
    }
}

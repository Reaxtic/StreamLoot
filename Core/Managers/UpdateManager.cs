using System.IO.Compression;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Core.Services;
using Core.Logging;
using System.IO;

namespace Core.Managers
{
    public sealed class UpdateManager
    {
        private static readonly Lazy<UpdateManager> _instance = new(() => new UpdateManager());
        public static UpdateManager Instance => _instance.Value;

        private readonly string _repositoryOwner = "Reaxtic";
        private readonly string _repositoryName = "StreamLoot";

        public event EventHandler<ProgressEventArgs>? DownloadProgress;

        private UpdateManager()
        { }

        /// <summary>
        /// Downloads the latest GitHub RELEASE (.zip asset), extracts it, and applies it via a small script that
        /// waits for this process to exit, copies the new files over the install directory (user data like the
        /// WebView2 profile is left untouched — no /MIR), and restarts the app. Binaries are distributed through
        /// GitHub Releases; the repository itself intentionally contains no build output.
        /// </summary>
        public async Task DownloadUpdate()
        {
            string basePath = Path.Combine(Environment.ExpandEnvironmentVariables("%APPDATA%"), "Stream Loot");
            string updatePath = Path.Combine(basePath, "Update");

            try
            {
                Directory.CreateDirectory(updatePath);
                Report(0, "Checking the latest release...");

                using HttpClient client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("StreamLoot-Updater");

                string releaseJson = await client.GetStringAsync(
                    $"https://api.github.com/repos/{_repositoryOwner}/{_repositoryName}/releases/latest");

                string? zipUrl = null;
                string? tag = null;
                using (JsonDocument doc = JsonDocument.Parse(releaseJson))
                {
                    tag = doc.RootElement.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() : null;
                    if (doc.RootElement.TryGetProperty("assets", out JsonElement assets))
                        foreach (JsonElement asset in assets.EnumerateArray())
                        {
                            string name = asset.GetProperty("name").GetString() ?? "";
                            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                            {
                                zipUrl = asset.GetProperty("browser_download_url").GetString();
                                break;
                            }
                        }
                }
                if (string.IsNullOrEmpty(zipUrl))
                    throw new InvalidOperationException("The latest GitHub release has no .zip asset attached.");

                AppLogger.Info("UpdateManager", $"Downloading update {tag} from {zipUrl}");
                string zipPath = Path.Combine(updatePath, "update.zip");
                using (HttpResponseMessage resp = await client.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? -1;
                    await using Stream src = await resp.Content.ReadAsStreamAsync();
                    await using FileStream dst = File.Create(zipPath);
                    byte[] buffer = new byte[81920];
                    long readTotal = 0;
                    int read;
                    while ((read = await src.ReadAsync(buffer)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, read));
                        readTotal += read;
                        if (total > 0)
                            Report((int)(readTotal * 90 / total), $"Downloading {tag}... {readTotal / 1048576} MB");
                    }
                }

                Report(92, "Extracting...");
                string extractPath = Path.Combine(updatePath, "extracted");
                if (Directory.Exists(extractPath))
                    Directory.Delete(extractPath, true);
                ZipFile.ExtractToDirectory(zipPath, extractPath);

                // The zip may wrap everything in a single top-level folder — locate the folder holding the exe.
                string? payloadDir = Directory
                    .GetFiles(extractPath, "Stream Loot.exe", SearchOption.AllDirectories)
                    .Select(Path.GetDirectoryName)
                    .FirstOrDefault();
                if (payloadDir == null)
                    throw new InvalidOperationException("The downloaded update does not contain 'Stream Loot.exe'.");

                Report(96, "Restarting to apply the update...");
                string appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
                string script = Path.Combine(updatePath, "apply-update.cmd");
                int pid = Environment.ProcessId;
                File.WriteAllText(script,
                    "@echo off\r\n" +
                    ":waitloop\r\n" +
                    $"tasklist /FI \"PID eq {pid}\" | find \"{pid}\" >nul && (timeout /t 1 /nobreak >nul & goto waitloop)\r\n" +
                    $"robocopy \"{payloadDir}\" \"{appDir}\" /E /R:3 /W:1 >nul\r\n" +
                    $"start \"\" \"{Path.Combine(appDir, "Stream Loot.exe")}\" --updated\r\n" +
                    "del \"%~f0\"\r\n");

                Process.Start(new ProcessStartInfo
                {
                    FileName = script,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                NotificationManager.ShowNotification("Update Error", $"An error occurred while updating the application.\n{ex.Message}\n\nTry again later.", 300);
                AppLogger.Error("UpdateManager", "DownloadUpdate failed.", ex);
            }
        }

        private void Report(int progress, string status)
            => DownloadProgress?.Invoke(this, new ProgressEventArgs(progress, status));
        /// <summary>
        /// Raises the event that reports progress updates during an operation.
        /// </summary>
        /// <param name="sender">The source of the event. Typically, this is the object that initiated the progress update.</param>
        /// <param name="e">A ProgressEventArgs object that contains the progress data, such as the percentage completed. Must not be
        /// null.</param>
        private void OnProgressChanged(object sender, ProgressEventArgs e)
        {
            DownloadProgress?.Invoke(this, e);
        }
    }
}
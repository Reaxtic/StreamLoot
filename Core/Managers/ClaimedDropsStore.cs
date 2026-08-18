using System.Text.Json;
using Core.Logging;
using System.IO;

namespace Core.Managers
{
    /// <summary>
    /// Permanent local record of drops that were successfully claimed.
    ///
    /// Twitch's API gives us no way to learn that a drop is already collected: the campaign-details response
    /// carries no per-drop <c>self</c> state and the inventory's <c>gameEventDrops</c> comes back empty, while
    /// <c>dropCampaignsInProgress</c> by definition drops finished campaigns. So every refresh rebuilt a claimed
    /// reward as "0 min, unclaimed", the Inventory showed it as pending and the miner kept re-watching a campaign
    /// that can never credit again. Remembering our own claims locally fixes both.
    /// </summary>
    public sealed class ClaimedDropsStore
    {
        public static ClaimedDropsStore Instance { get; } = new ClaimedDropsStore();

        private static string FilePath => Path.Combine(
            Environment.ExpandEnvironmentVariables("%APPDATA%"), "Stream Loot", "ClaimedDrops.json");
        private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

        private readonly object _sync = new();
        private HashSet<string> _claimed = new(StringComparer.Ordinal);

        private ClaimedDropsStore() => Load();

        public bool HasAny { get { lock (_sync) return _claimed.Count != 0; } }

        private static string Key(string campaignId, string rewardId) => campaignId + "|" + rewardId;

        public bool IsClaimed(string campaignId, string rewardId)
        {
            lock (_sync) return _claimed.Contains(Key(campaignId, rewardId));
        }

        /// <summary>Records a claim. Persists immediately — losing this record means re-mining a finished drop.</summary>
        public void Add(string campaignId, string rewardId)
        {
            if (string.IsNullOrWhiteSpace(campaignId) || string.IsNullOrWhiteSpace(rewardId))
                return;
            lock (_sync)
            {
                if (!_claimed.Add(Key(campaignId, rewardId)))
                    return;
                Save();
            }
            AppLogger.Info("ClaimedDrops", $"Recorded claimed drop {campaignId}|{rewardId} (total {_claimed.Count}).");
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(_claimed, _jsonOptions));
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ClaimedDrops", $"Saving claimed drops failed: {ex.Message}");
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    SeedFromLogs();
                    return;
                }
                HashSet<string>? loaded = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(FilePath));
                if (loaded != null)
                    _claimed = new HashSet<string>(loaded, StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ClaimedDrops", $"Loading claimed drops failed (starting fresh): {ex.Message}");
            }
        }

        /// <summary>
        /// First-run migration: every successful claim was already written to the diagnostic log, so past claims
        /// can be recovered from there instead of re-mining drops the user already owns.
        /// </summary>
        private void SeedFromLogs()
        {
            try
            {
                string logsDir = Path.Combine(Environment.ExpandEnvironmentVariables("%APPDATA%"), "Stream Loot", "logs");
                if (!Directory.Exists(logsDir))
                    return;

                System.Text.RegularExpressions.Regex rx = new(
                    @"Applied immediate claimed-state update\. campaignId=([^,\s]+), rewardId=([^\s]+)");

                foreach (string file in Directory.GetFiles(logsDir, "app-*.log"))
                    foreach (string line in File.ReadLines(file))
                    {
                        System.Text.RegularExpressions.Match m = rx.Match(line);
                        if (m.Success)
                            _claimed.Add(Key(m.Groups[1].Value, m.Groups[2].Value));
                    }

                if (_claimed.Count != 0)
                {
                    Save();
                    AppLogger.Info("ClaimedDrops", $"Recovered {_claimed.Count} previously claimed drop(s) from the logs.");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ClaimedDrops", $"Seeding claimed drops from logs failed: {ex.Message}");
            }
        }
    }
}

using System.Text.Json;
using Core.Logging;
using Core.Enums;
using System.IO;

namespace Core.Managers
{
    /// <summary>A single claimed drop, as shown in the Statistics claim history.</summary>
    public sealed record ClaimedDropRecord(DateTime When, string Platform, string Campaign, string Reward);

    /// <summary>
    /// Persists lightweight mining statistics (watched minutes per day and a claimed-drops log) to
    /// %APPDATA%\Stream Loot\Stats.json. Writes are throttled; consumers refresh via <see cref="StatsChanged"/>.
    /// </summary>
    public sealed class StatsManager
    {
        public static StatsManager Instance { get; } = new StatsManager();

        public event Action? StatsChanged;

        private static readonly string _filePath = Path.Combine(
            Environment.ExpandEnvironmentVariables("%APPDATA%"), "Stream Loot", "Stats.json");
        private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

        private readonly object _sync = new();
        private Dictionary<string, int> _minutesPerDay = new(StringComparer.Ordinal); // "yyyy-MM-dd" -> minutes
        private List<ClaimedDropRecord> _claims = new();
        private DateTime _lastSave = DateTime.MinValue;
        private bool _dirty;

        private StatsManager() => Load();

        public void AddWatchedMinutes(Platform platform, int minutes)
        {
            if (minutes <= 0)
                return;
            lock (_sync)
            {
                string day = DateTime.Now.ToString("yyyy-MM-dd");
                _minutesPerDay[day] = _minutesPerDay.TryGetValue(day, out int cur) ? cur + minutes : minutes;
                _dirty = true;
            }
            SaveIfDue();
            StatsChanged?.Invoke();
        }

        public void AddClaimedDrop(Platform platform, string campaign, string reward)
        {
            lock (_sync)
            {
                _claims.Insert(0, new ClaimedDropRecord(DateTime.Now, platform.ToString(), campaign, reward));
                if (_claims.Count > 500)
                    _claims.RemoveRange(500, _claims.Count - 500);
                _dirty = true;
            }
            SaveIfDue(force: true); // a claim is a rare, important event — persist immediately
            StatsChanged?.Invoke();
        }

        public int MinutesToday
        {
            get { lock (_sync) return _minutesPerDay.TryGetValue(DateTime.Now.ToString("yyyy-MM-dd"), out int m) ? m : 0; }
        }

        public int MinutesLast7Days
        {
            get
            {
                lock (_sync)
                {
                    int total = 0;
                    for (int i = 0; i < 7; i++)
                    {
                        string day = DateTime.Now.AddDays(-i).ToString("yyyy-MM-dd");
                        if (_minutesPerDay.TryGetValue(day, out int m))
                            total += m;
                    }
                    return total;
                }
            }
        }

        public int MinutesTotal
        {
            get { lock (_sync) return _minutesPerDay.Values.Sum(); }
        }

        public int TotalClaimed
        {
            get { lock (_sync) return _claims.Count; }
        }

        public IReadOnlyList<ClaimedDropRecord> ClaimHistory
        {
            get { lock (_sync) return _claims.ToList(); }
        }

        private void SaveIfDue(bool force = false)
        {
            lock (_sync)
            {
                if (!_dirty || (!force && (DateTime.Now - _lastSave) < TimeSpan.FromMinutes(1)))
                    return;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                    StatsModel model = new StatsModel { MinutesPerDay = _minutesPerDay, Claims = _claims };
                    File.WriteAllText(_filePath, JsonSerializer.Serialize(model, _jsonOptions));
                    _lastSave = DateTime.Now;
                    _dirty = false;
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("Stats", $"Saving stats failed: {ex.Message}");
                }
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_filePath))
                    return;
                StatsModel? model = JsonSerializer.Deserialize<StatsModel>(File.ReadAllText(_filePath));
                if (model == null)
                    return;
                _minutesPerDay = model.MinutesPerDay ?? new Dictionary<string, int>(StringComparer.Ordinal);
                _claims = model.Claims ?? new List<ClaimedDropRecord>();
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Stats", $"Loading stats failed (starting fresh): {ex.Message}");
            }
        }

        private sealed class StatsModel
        {
            public Dictionary<string, int>? MinutesPerDay { get; set; }
            public List<ClaimedDropRecord>? Claims { get; set; }
        }
    }
}

using System.Runtime.CompilerServices;
using Timer = System.Timers.Timer;
using System.Net.Http.Headers;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Net.Http;
using Core.Logging;
using Core.Models;
using Core.Enums;
using System.IO;

namespace Core.Managers
{
    public sealed class UISettingsManager : INotifyPropertyChanged
    {
        private static readonly Lazy<UISettingsManager> _instance = new(() => new UISettingsManager());
        public static UISettingsManager Instance => _instance.Value;
        public event PropertyChangedEventHandler? PropertyChanged;
        public event Action<MiningPriorityMode>? MiningPriorityModeChanged;
        public event Action<Platform>? GameWhitelistChanged;
        private static readonly string _settingsFilePath = Path.Combine(Environment.ExpandEnvironmentVariables("%APPDATA%"), "Stream Loot", "Settings.json");
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true
        };

        // === SETTINGS PROPERTIES ===
        private bool _startWithWindows;
        private bool _minimizeToTrayOnStartup;
        private string _theme = "System";
        private UpdateFrequency _updateFrequency = UpdateFrequency.Daily;
        private bool _autoClaimRewards = true;
        private bool _notifyOnReadyToClaim;
        private bool _notifyOnAutoClaimed = true;
        private bool _verboseDebugLogging;
        private bool _updateAvailable = false;
        private bool _notifyOnNewUpdateAvailable = true;
        private DateTime? _lastUpdateCheck = null;
        private MiningPriorityMode _miningPriorityMode = MiningPriorityMode.AvailabilityThenProgress;
        private List<string> _twitchGameWhitelistSlugs = new List<string>();
        private List<string> _kickGameWhitelistSlugs = new List<string>();
        private List<string> _twitchGameBlacklistSlugs = new List<string>();
        private List<string> _kickGameBlacklistSlugs = new List<string>();
        private bool _twitchGameFilterExclude;
        private bool _kickGameFilterExclude;
        private bool _isUpdatingGameFilterOptions;
        private bool _isLoadingSettings;
        private bool _softwareRendering;
        private bool _sleepWhenDone;
        private string _language = "en";
        private bool _firstRunCompleted;
        private double _twitchGameListHeight = 180;
        private double _kickGameListHeight = 180;
        private static double LimitGameListHeight(double value) => double.IsFinite(value) ? Math.Clamp(value, 100, 900) : 180;
        public double TwitchGameListHeight
        {
            get => _twitchGameListHeight;
            set { _twitchGameListHeight = LimitGameListHeight(value); OnPropertyChanged(nameof(TwitchGameListHeight)); }
        }
        public double KickGameListHeight
        {
            get => _kickGameListHeight;
            set { _kickGameListHeight = LimitGameListHeight(value); OnPropertyChanged(nameof(KickGameListHeight)); }
        }

        public ObservableCollection<GameFilterOption> TwitchGameFilterOptions { get; } = new ObservableCollection<GameFilterOption>();
        public ObservableCollection<GameFilterOption> KickGameFilterOptions { get; } = new ObservableCollection<GameFilterOption>();

        /// <summary>
        /// Gets or sets a value indicating whether the application starts automatically when Windows starts.
        /// </summary>
        /// <remarks>Disabling this option may also disable related startup behaviors, such as minimizing
        /// to the system tray on startup.</remarks>
        public bool StartWithWindows
        {
            get => _startWithWindows;
            set
            {
                if (SetField(ref _startWithWindows, value))
                {
                    if (!value && MinimizeToTrayOnStartup)
                        MinimizeToTrayOnStartup = false;
                }
            }
        }
        /// <summary>
        /// Gets or sets a value indicating whether the application should start minimized to the system tray on
        /// startup.
        /// </summary>
        /// <remarks>This property can only be enabled if the application is configured to start with
        /// Windows. If StartWithWindows is disabled, setting this property to true has no effect and the value will
        /// remain false.</remarks>
        public bool MinimizeToTrayOnStartup
        {
            get => _minimizeToTrayOnStartup;
            set
            {
                // Prevent enabling if StartWithWindows is off
                if (!StartWithWindows)
                {
                    if (_minimizeToTrayOnStartup != false)
                        SetField(ref _minimizeToTrayOnStartup, false);

                    return;
                }
                else
                    SetField(ref _minimizeToTrayOnStartup, value);
            }
        }
        /// <summary>
        /// Gets or sets the name of the current application theme.
        /// </summary>
        public string Theme
        {
            get => _theme;
            set => SetField(ref _theme, value);
        }
        /// <summary>
        /// Gets or sets the frequency at which updates are performed.
        /// </summary>
        public UpdateFrequency UpdateFrequency
        {
            get => _updateFrequency;
            set
            {
                SetField(ref _updateFrequency, value);

                if (value == UpdateFrequency.Never)
                    NotifyOnNewUpdateAvailable = false;
                else if (UpdateFrequency == UpdateFrequency.OnLaunch && !_isLoadingSettings)
                    _ = CheckForUpdatesAsync(true);

                OnPropertyChanged(nameof(IsUpdateNotificationEnabled));
            }
        }
        /// <summary>
        /// Gets or sets a value indicating whether rewards are automatically claimed when they become available.
        /// </summary>
        /// <remarks>Enabling this property may automatically disable certain notification options, such
        /// as notifications for rewards ready to claim or for rewards that have been auto-claimed. Changing this
        /// property can affect related notification settings.</remarks>
        public bool AutoClaimRewards
        {
            get => _autoClaimRewards;
            set
            {
                if (SetField(ref _autoClaimRewards, value))
                {
                    if (value && NotifyOnReadyToClaim)
                        NotifyOnReadyToClaim = false;

                    if (!value && NotifyOnAutoClaimed)
                        NotifyOnAutoClaimed = false;
                }
            }
        }
        public MiningPriorityMode MiningPriorityMode
        {
            get => _miningPriorityMode;
            set
            {
                if (SetField(ref _miningPriorityMode, value) && !_isLoadingSettings)
                    MiningPriorityModeChanged?.Invoke(value);
            }
        }
        /// <summary>
        /// Gets or sets a value indicating whether a notification should be sent when rewards are ready to be claimed.
        /// </summary>
        /// <remarks>This property cannot be enabled if automatic reward claiming is active. If <see
        /// cref="AutoClaimRewards"/> is <see langword="true"/>, setting this property to <see langword="true"/> has no
        /// effect and the value remains <see langword="false"/>.</remarks>
        public bool NotifyOnReadyToClaim
        {
            get => _notifyOnReadyToClaim;
            set
            {
                // Prevent enabling if AutoClaimRewards is on
                if (AutoClaimRewards)
                {
                    if (_notifyOnReadyToClaim != false)
                        SetField(ref _notifyOnReadyToClaim, false);

                    return;
                }
                else
                    SetField(ref _notifyOnReadyToClaim, value);
            }
        }
        /// <summary>
        /// Gets or sets a value indicating whether a notification is sent when rewards are automatically claimed.
        /// </summary>
        /// <remarks>This property can only be enabled if automatic reward claiming is active. If
        /// automatic reward claiming is disabled, setting this property to true has no effect and the value remains
        /// false.</remarks>
        public bool NotifyOnAutoClaimed
        {
            get => _notifyOnAutoClaimed;
            set
            {
                // Prevent enabling if AutoClaimRewards is off
                if (!AutoClaimRewards)
                {
                    if (_notifyOnAutoClaimed != false)
                        SetField(ref _notifyOnAutoClaimed, false);

                    return;
                }
                else
                    SetField(ref _notifyOnAutoClaimed, value);
            }
        }
        /// <summary>
        /// Gets or sets a value indicating whether verbose diagnostic logging is enabled.
        /// </summary>
        public bool VerboseDebugLogging
        {
            get => _verboseDebugLogging;
            set => SetField(ref _verboseDebugLogging, value);
        }
        /// <summary>
        /// Run WebView2 without GPU acceleration (helps on machines with unstable graphics drivers).
        /// Takes effect after an app restart.
        /// </summary>
        public bool SoftwareRendering
        {
            get => _softwareRendering;
            set => SetField(ref _softwareRendering, value);
        }
        /// <summary>Put the computer to sleep once every campaign is fully mined and claimed.</summary>
        public bool SleepWhenDone
        {
            get => _sleepWhenDone;
            set => SetField(ref _sleepWhenDone, value);
        }
        /// <summary>UI language code ("en" / "pl").</summary>
        public string Language
        {
            get => _language;
            set
            {
                if (SetField(ref _language, value))
                    Loc.Instance.SetLanguage(value);
            }
        }
        /// <summary>Whether the first-run onboarding has already been shown.</summary>
        public bool FirstRunCompleted
        {
            get => _firstRunCompleted;
            set => SetField(ref _firstRunCompleted, value);
        }
        /// <summary>
        /// Gets or sets a value indicating whether a software update is available.
        /// </summary>
        public bool UpdateAvailable
        {
            get => _updateAvailable;
            set
            {
                SetField(ref _updateAvailable, value);

                if (value && NotifyOnNewUpdateAvailable)
                    NotificationManager.ShowNotification("Update Available", "A new version is available.");
            }
        }
        /// <summary>
        /// Gets or sets a value indicating whether the application should notify the user when a new update is
        /// available.
        /// </summary>
        public bool NotifyOnNewUpdateAvailable
        {
            get => _notifyOnNewUpdateAvailable;
            set => SetField(ref _notifyOnNewUpdateAvailable, value);
        }
        /// <summary>
        /// Gets a value indicating whether update notifications are enabled.
        /// </summary>
        public bool IsUpdateNotificationEnabled => UpdateFrequency != UpdateFrequency.Never;

        public string TwitchWhitelistSummary => BuildSummary("Twitch", _twitchGameWhitelistSlugs, _twitchGameBlacklistSlugs);
        public string KickWhitelistSummary => BuildSummary("Kick", _kickGameWhitelistSlugs, _kickGameBlacklistSlugs);

        private static string BuildSummary(string platform, List<string> whitelist, List<string> blacklist)
        {
            string allowPart = whitelist.Count == 0
                ? $"All active {platform} games are allowed"
                : $"{whitelist.Count} {platform} game(s) prioritized; other games are fallback";
            return blacklist.Count == 0 ? allowPart : $"{allowPart} • {blacklist.Count} blocked";
        }

        public IReadOnlyList<string> TwitchGameWhitelistSlugs => _twitchGameWhitelistSlugs.AsReadOnly();
        public IReadOnlyList<string> KickGameWhitelistSlugs => _kickGameWhitelistSlugs.AsReadOnly();
        public IReadOnlyList<string> TwitchGameBlacklistSlugs => _twitchGameBlacklistSlugs.AsReadOnly();
        public IReadOnlyList<string> KickGameBlacklistSlugs => _kickGameBlacklistSlugs.AsReadOnly();

        /// <summary>
        /// When true, the selected Twitch games are excluded (mine everything else) instead of being an allow-list.
        /// </summary>
        public bool TwitchGameFilterExclude
        {
            get => _twitchGameFilterExclude;
            set
            {
                if (SetField(ref _twitchGameFilterExclude, value))
                {
                    OnPropertyChanged(nameof(TwitchWhitelistSummary));
                    GameWhitelistChanged?.Invoke(Platform.Twitch);
                }
            }
        }

        /// <summary>
        /// When true, the selected Kick games are excluded (mine everything else) instead of being an allow-list.
        /// </summary>
        public bool KickGameFilterExclude
        {
            get => _kickGameFilterExclude;
            set
            {
                if (SetField(ref _kickGameFilterExclude, value))
                {
                    OnPropertyChanged(nameof(KickWhitelistSummary));
                    GameWhitelistChanged?.Invoke(Platform.Kick);
                }
            }
        }

        private UISettingsManager()
        {
            LoadSettings();
            _ = CheckForUpdatesAsync(); // Fire and forget
        }

        private async Task CheckForUpdatesAsync(bool skipLoad = false)
        {
            if (UpdateFrequency != UpdateFrequency.Never)
            {
                if (!skipLoad)
                    LoadSettings(); // Ensure we have the latest settings, this includes last time we checked for an update

                if (_lastUpdateCheck.HasValue)
                {
                    TimeSpan timeSinceLastCheck = DateTime.Now - _lastUpdateCheck.Value;

                    switch (UpdateFrequency)
                    {
                        case UpdateFrequency.OnLaunch:
                            // Always check on launch
                            break;
                        case UpdateFrequency.Daily:
                            if (timeSinceLastCheck < TimeSpan.FromDays(1))
                            {
                                TimeSpan timeLeft = TimeSpan.FromDays(1) - timeSinceLastCheck;

                                // Create a timer, to check again in "timeLeft" and skip for now
                                Timer timer = new Timer(timeLeft.TotalMilliseconds);

                                timer.Elapsed += async (sender, e) =>
                                {
                                    timer.Stop();
                                    timer.Dispose();
                                    await CheckForUpdatesAsync();
                                };

                                timer.Start();

                                AppLogger.Debug("UISettings", $"[Update Check] Skipping check. Next check in {timeLeft.TotalHours:F1} hours.");

                                return; // Skip check
                            }
                            break;
                        case UpdateFrequency.Weekly:
                            if (timeSinceLastCheck < TimeSpan.FromDays(7))
                            {
                                TimeSpan timeLeft = TimeSpan.FromDays(7) - timeSinceLastCheck;

                                // Create a timer, to check again in "timeLeft" and skip for now
                                Timer timer = new Timer(timeLeft.TotalMilliseconds);

                                timer.Elapsed += async (sender, e) =>
                                {
                                    timer.Stop();
                                    timer.Dispose();
                                    await CheckForUpdatesAsync();
                                };

                                timer.Start();

                                AppLogger.Debug("UISettings", $"[Update Check] Skipping check. Next check in {timeLeft.TotalHours:F1} hours.");

                                return; // Skip check
                            }
                            break;
                    }
                }

                FileVersionInfo localVersionInfo = FileVersionInfo.GetVersionInfo(Utility.GetExePath());
                UpdateInfo? serverUpdateInfo;

                try
                {
                    using HttpClient client = new HttpClient();
                    client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue
                    {
                        NoCache = true
                    };

                    client.DefaultRequestHeaders.Add("Cache-Control", "no-cache");

                    serverUpdateInfo = JsonSerializer.Deserialize<UpdateInfo>(await client.GetStringAsync("https://raw.githubusercontent.com/Reaxtic/StreamLoot/main/updateInfo.sdc")) ?? new UpdateInfo();
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("UISettings", $"Update check request failed: {ex.Message}");
                    UpdateAvailable = false;
                    return;
                }

                if (Version.TryParse(serverUpdateInfo.Version, out Version? serverVersion) && Version.TryParse(localVersionInfo.FileVersion, out Version? localVersion))
                    UpdateAvailable = serverVersion > localVersion;
                else
                    UpdateAvailable = false;

                _lastUpdateCheck = DateTime.Now;
                SaveSettings();
            }
        }
        /// <summary>
        /// Loads application settings from the configuration file, if it exists, and applies them to the current
        /// instance.
        /// </summary>
        /// <remarks>If the configuration file does not exist or cannot be read, default settings are
        /// used. Invalid or inaccessible files are ignored without throwing an exception.</remarks>
        private void LoadSettings()
        {
            if (!File.Exists(_settingsFilePath))
                return; // First run - use defaults

            bool migratedLegacyExcludeMode = false;
            _isLoadingSettings = true;
            try
            {
                string json = File.ReadAllText(_settingsFilePath);
                SettingsModel? settings = JsonSerializer.Deserialize<SettingsModel>(json, _jsonOptions);

                if (settings != null)
                {
                    StartWithWindows = settings.StartWithWindows;
                    MinimizeToTrayOnStartup = settings.MinimizeToTrayOnStartup;
                    Theme = settings.Theme ?? "System";
                    UpdateFrequency = settings.UpdateFrequency;
                    AutoClaimRewards = settings.AutoClaimRewards;
                    MiningPriorityMode = settings.MiningPriorityMode;
                    TwitchGameListHeight = settings.TwitchGameListHeight;
                    KickGameListHeight = settings.KickGameListHeight;
                    NotifyOnReadyToClaim = settings.NotifyOnReadyToClaim;
                    NotifyOnAutoClaimed = settings.NotifyOnAutoClaimed;
                    VerboseDebugLogging = settings.VerboseDebugLogging;
                    NotifyOnNewUpdateAvailable = settings.NotifyOnNewUpdateAvailable;
                    _lastUpdateCheck = settings.LastUpdateCheck;
                    _twitchGameWhitelistSlugs = NormalizeWhitelist(settings.TwitchGameWhitelistSlugs);
                    _kickGameWhitelistSlugs = NormalizeWhitelist(settings.KickGameWhitelistSlugs);
                    _twitchGameBlacklistSlugs = NormalizeWhitelist(settings.TwitchGameBlacklistSlugs);
                    _kickGameBlacklistSlugs = NormalizeWhitelist(settings.KickGameBlacklistSlugs);

                    // The old global "exclude selected" mode was ambiguous next to the per-game Exclude switch.
                    // Preserve its effective behaviour by moving those selections to the explicit blacklist once,
                    // then keep the remaining left-hand selections as a normal allow-list.
                    if (settings.TwitchGameFilterExclude)
                    {
                        _twitchGameBlacklistSlugs = NormalizeWhitelist(_twitchGameBlacklistSlugs.Concat(_twitchGameWhitelistSlugs));
                        _twitchGameWhitelistSlugs.Clear();
                        migratedLegacyExcludeMode = true;
                    }

                    if (settings.KickGameFilterExclude)
                    {
                        _kickGameBlacklistSlugs = NormalizeWhitelist(_kickGameBlacklistSlugs.Concat(_kickGameWhitelistSlugs));
                        _kickGameWhitelistSlugs.Clear();
                        migratedLegacyExcludeMode = true;
                    }

                    // Backing fields stay false: the legacy mode is no longer exposed in the UI.
                    _twitchGameFilterExclude = false;
                    _kickGameFilterExclude = false;
                    _softwareRendering = settings.SoftwareRendering;
                    _sleepWhenDone = settings.SleepWhenDone;
                    _language = string.IsNullOrWhiteSpace(settings.Language) ? "en" : settings.Language!;
                    _firstRunCompleted = settings.FirstRunCompleted;
                    Loc.Instance.SetLanguage(_language);
                }
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
            {
                AppLogger.Warn("UISettings", $"LoadSettings failed and defaults are used. {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _isLoadingSettings = false;
            }

            OnPropertyChanged(nameof(TwitchWhitelistSummary));
            OnPropertyChanged(nameof(KickWhitelistSummary));

            if (migratedLegacyExcludeMode)
            {
                AppLogger.Info("UISettings", "Migrated legacy game-filter exclude mode to explicit per-game exclusions.");
                SaveSettings();
            }

            UpdateStartupRegistry();
        }
        /// <summary>
        /// Saves the current application settings to the settings file in JSON format.
        /// </summary>
        /// <remarks>If the settings file or its directory does not exist, they are created automatically.
        /// Any I/O or access errors encountered during the save operation are silently ignored; the method does not
        /// throw exceptions in these cases.</remarks>
        public void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_settingsFilePath)!);

                SettingsModel settings = new SettingsModel
                {
                    StartWithWindows = StartWithWindows,
                    MinimizeToTrayOnStartup = MinimizeToTrayOnStartup,
                    Theme = Theme,
                    UpdateFrequency = UpdateFrequency,
                    AutoClaimRewards = AutoClaimRewards,
                    MiningPriorityMode = MiningPriorityMode,
                    TwitchGameListHeight = TwitchGameListHeight,
                    KickGameListHeight = KickGameListHeight,
                    NotifyOnReadyToClaim = NotifyOnReadyToClaim,
                    NotifyOnAutoClaimed = NotifyOnAutoClaimed,
                    VerboseDebugLogging = VerboseDebugLogging,
                    NotifyOnNewUpdateAvailable = NotifyOnNewUpdateAvailable,
                    LastUpdateCheck = _lastUpdateCheck,
                    TwitchGameWhitelistSlugs = [.. _twitchGameWhitelistSlugs],
                    KickGameWhitelistSlugs = [.. _kickGameWhitelistSlugs],
                    TwitchGameBlacklistSlugs = [.. _twitchGameBlacklistSlugs],
                    KickGameBlacklistSlugs = [.. _kickGameBlacklistSlugs],
                    TwitchGameFilterExclude = _twitchGameFilterExclude,
                    KickGameFilterExclude = _kickGameFilterExclude,
                    SoftwareRendering = _softwareRendering,
                    SleepWhenDone = _sleepWhenDone,
                    Language = _language,
                    FirstRunCompleted = _firstRunCompleted
                };

                string json = JsonSerializer.Serialize(settings, _jsonOptions);
                File.WriteAllText(_settingsFilePath, json);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                AppLogger.Warn("UISettings", $"SaveSettings failed. {ex.GetType().Name}: {ex.Message}");
            }
        }
        /// <summary>
        /// Raises the PropertyChanged event to notify listeners that a property value has changed.
        /// </summary>
        /// <remarks>Call this method in a property's setter to notify subscribers that the property's
        /// value has changed. This is commonly used to support data binding in applications that implement the
        /// INotifyPropertyChanged interface.</remarks>
        /// <param name="propertyName">The name of the property that changed. This value is optional and is automatically provided when called from
        /// a property setter.</param>
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        /// <summary>
        /// Sets the specified field to a new value and raises a property changed notification if the value has changed.
        /// </summary>
        /// <remarks>This method is typically used in property setters to implement the
        /// INotifyPropertyChanged pattern. It also performs additional actions such as updating startup settings and
        /// saving configuration when certain properties change.</remarks>
        /// <typeparam name="T">The type of the field and value being set.</typeparam>
        /// <param name="field">A reference to the field to update. The field is set to the new value if it differs from the current value.</param>
        /// <param name="value">The new value to assign to the field.</param>
        /// <param name="propertyName">The name of the property associated with the field. This is used for property change notification. If not
        /// specified, the caller member name is used.</param>
        /// <returns>true if the field value was changed and a property change notification was raised; otherwise, false.</returns>
        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;

            field = value;
            OnPropertyChanged(propertyName);

            // Handle special cases
            if (propertyName is nameof(StartWithWindows) or nameof(MinimizeToTrayOnStartup))
                UpdateStartupRegistry();

            // Auto-save whenever a setting changes (lightweight & convenient)
            if (!_isLoadingSettings)
                Task.Run(SaveSettings); // Fire-and-forget on background thread

            return true;
        }
        /// <summary>
        /// Updates the Windows startup registry entry to configure whether the application launches automatically when
        /// the user logs in.
        /// </summary>
        /// <remarks>This method adds or removes the application's registry entry based on the current
        /// startup and minimize settings. It does not throw exceptions if registry access fails; errors are logged for
        /// diagnostic purposes. This method should be called whenever the startup-related settings change to ensure the
        /// registry reflects the desired behavior.</remarks>
        private void UpdateStartupRegistry()
        {
            // Loading partial settings and the temporary updater must never rewrite the launch target.
            if (_isLoadingSettings || Environment.GetCommandLineArgs().Contains("--updating")) return;
            string keyName = "StreamLoot";
            string exePath = Utility.GetExePath();

            try
            {
                if (!StartWithWindows)
                {
                    Core.Services.WindowsAutoStartService.Configure(false, exePath, false);
                    // Just remove it - clean and simple
                    Utility.RemoveFromRegistry(keyName);
                    return;
                }

                if (Core.Services.WindowsAutoStartService.Configure(true, exePath, MinimizeToTrayOnStartup))
                {
                    Utility.RemoveFromRegistry(keyName); // one launch mechanism, not duplicate startup entries
                    return;
                }

                // Restricted machines retain the existing current-user registry fallback.
                if (MinimizeToTrayOnStartup)
                {
                    // Launch minimized
                    Utility.WriteToRegistry(keyName, exePath, ["--autostart", "--minimize"]);
                }
                else
                {
                    // Launch normally
                    Utility.WriteToRegistry(keyName, exePath, ["--autostart"]);
                }
            }
            catch (Exception ex)
            {
                // Never let registry errors crash settings flow
                AppLogger.Error("UISettings", "[Startup Registry] Failed", ex);
                // Optional: show non-blocking toast later if you want
            }
        }

        public void UpdateAvailableGameFilterOptions(IEnumerable<DropsCampaign> campaigns)
        {
            _isUpdatingGameFilterOptions = true;
            try
            {
                List<(Platform platform, string slug, string displayName)> options = campaigns
                    .Where(c => !string.IsNullOrWhiteSpace(c.Slug))
                    .Select(c => (c.Platform, c.Slug.Trim().ToLowerInvariant(), c.GameName))
                    .GroupBy(x => $"{x.Platform}:{x.Item2}", StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .OrderBy(x => x.Platform)
                    .ThenBy(x => x.Item3, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                RebuildOptionsCollection(
                    TwitchGameFilterOptions,
                    Platform.Twitch,
                    options.Where(x => x.platform == Platform.Twitch),
                    _twitchGameWhitelistSlugs);

                RebuildOptionsCollection(
                    KickGameFilterOptions,
                    Platform.Kick,
                    options.Where(x => x.platform == Platform.Kick),
                    _kickGameWhitelistSlugs);
            }
            finally
            {
                _isUpdatingGameFilterOptions = false;
            }

            OnPropertyChanged(nameof(TwitchWhitelistSummary));
            OnPropertyChanged(nameof(KickWhitelistSummary));
        }

        /// <summary>Clears the per-game block-list for the platform (all games become mineable again).</summary>
        public void ClearGameBlacklist(Platform platform)
        {
            if (platform == Platform.Twitch)
                _twitchGameBlacklistSlugs = new List<string>();
            else
                _kickGameBlacklistSlugs = new List<string>();

            ObservableCollection<GameFilterOption> options = platform == Platform.Twitch
                ? TwitchGameFilterOptions
                : KickGameFilterOptions;

            _isUpdatingGameFilterOptions = true;
            try
            {
                foreach (GameFilterOption option in options)
                    option.IsExcluded = false;
            }
            finally
            {
                _isUpdatingGameFilterOptions = false;
            }

            OnPropertyChanged(platform == Platform.Twitch ? nameof(TwitchWhitelistSummary) : nameof(KickWhitelistSummary));
            Task.Run(SaveSettings);
            GameWhitelistChanged?.Invoke(platform);
        }

        public void ClearGameWhitelist(Platform platform)
        {
            if (platform == Platform.Twitch)
                _twitchGameWhitelistSlugs = new List<string>();
            else
                _kickGameWhitelistSlugs = new List<string>();

            ObservableCollection<GameFilterOption> options = platform == Platform.Twitch
                ? TwitchGameFilterOptions
                : KickGameFilterOptions;

            _isUpdatingGameFilterOptions = true;
            try
            {
                foreach (GameFilterOption option in options)
                {
                    option.IsSelected = false;
                    option.PriorityOrder = int.MaxValue;
                }

                List<GameFilterOption> inactiveOptions = options
                    .Where(x => x.DisplayName.EndsWith(" (inactive)", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (GameFilterOption option in inactiveOptions)
                {
                    option.PropertyChanged -= OnGameFilterOptionPropertyChanged;
                    options.Remove(option);
                }
            }
            finally
            {
                _isUpdatingGameFilterOptions = false;
            }

            OnPropertyChanged(platform == Platform.Twitch ? nameof(TwitchWhitelistSummary) : nameof(KickWhitelistSummary));
            Task.Run(SaveSettings);
            GameWhitelistChanged?.Invoke(platform);
        }

        public bool IsCampaignAllowedByWhitelist(DropsCampaign campaign)
        {
            List<string> whitelist = campaign.Platform == Platform.Twitch
                ? _twitchGameWhitelistSlugs
                : _kickGameWhitelistSlugs;
            List<string> blacklist = campaign.Platform == Platform.Twitch
                ? _twitchGameBlacklistSlugs
                : _kickGameBlacklistSlugs;

            string campaignSlug = campaign.Slug?.Trim().ToLowerInvariant() ?? string.Empty;

            // Per-game block wins over everything: an excluded game is never mined.
            if (blacklist.Contains(campaignSlug, StringComparer.OrdinalIgnoreCase))
                return false;

            return true; // A priority is a preference, not an allow-list.
        }

        public int GamePriority(DropsCampaign campaign)
        {
            var list = campaign.Platform == Platform.Twitch ? _twitchGameWhitelistSlugs : _kickGameWhitelistSlugs;
            int index = list.FindIndex(s => string.Equals(s, campaign.Slug?.Trim(), StringComparison.OrdinalIgnoreCase));
            return index < 0 ? int.MaxValue : index;
        }

        private void RefreshPriorityRanks(Platform platform)
        {
            var list = platform == Platform.Twitch ? _twitchGameWhitelistSlugs : _kickGameWhitelistSlugs;
            var options = platform == Platform.Twitch ? TwitchGameFilterOptions : KickGameFilterOptions;
            foreach (var option in options) { int i = list.IndexOf(option.Slug); option.PriorityOrder = i < 0 ? int.MaxValue : i; }
        }

        public void MoveGamePriority(GameFilterOption option, int direction)
        {
            var list = option.Platform == Platform.Twitch ? _twitchGameWhitelistSlugs : _kickGameWhitelistSlugs;
            int index = list.IndexOf(option.Slug), next = index + direction;
            if (index < 0 || next < 0 || next >= list.Count) return;
            (list[index], list[next]) = (list[next], list[index]);
            RefreshPriorityRanks(option.Platform);
            Task.Run(SaveSettings);
            GameWhitelistChanged?.Invoke(option.Platform);
        }

        private void RebuildOptionsCollection(
            ObservableCollection<GameFilterOption> collection,
            Platform platform,
            IEnumerable<(Platform platform, string slug, string displayName)> options,
            List<string> whitelist)
        {
            List<string> blacklist = platform == Platform.Twitch ? _twitchGameBlacklistSlugs : _kickGameBlacklistSlugs;
            foreach (GameFilterOption existing in collection)
                existing.PropertyChanged -= OnGameFilterOptionPropertyChanged;

            collection.Clear();

            HashSet<string> seenSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach ((Platform optionPlatform, string slug, string displayName) in options)
            {
                GameFilterOption option = new GameFilterOption(
                    optionPlatform,
                    slug,
                    displayName,
                    whitelist.Contains(slug, StringComparer.OrdinalIgnoreCase),
                    blacklist.Contains(slug, StringComparer.OrdinalIgnoreCase))
                {
                    // CollectionView sorts on insertion. Assign the rank BEFORE adding the row, including
                    // delayed miner rebuilds; changing it afterwards leaves a non-live view alphabetical.
                    PriorityOrder = whitelist.IndexOf(slug) is var rank && rank >= 0 ? rank : int.MaxValue
                };

                option.PropertyChanged += OnGameFilterOptionPropertyChanged;
                collection.Add(option);
                seenSlugs.Add(slug);
            }

            foreach (string slug in whitelist)
            {
                if (seenSlugs.Contains(slug))
                    continue;

                GameFilterOption option = new GameFilterOption(
                    platform,
                    slug,
                    $"{slug} (inactive)",
                    true)
                {
                    PriorityOrder = whitelist.IndexOf(slug)
                };

                option.PropertyChanged += OnGameFilterOptionPropertyChanged;
                collection.Add(option);
            }
            RefreshPriorityRanks(platform);
        }

        private void OnGameFilterOptionPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_isUpdatingGameFilterOptions)
                return;

            if (sender is not GameFilterOption option)
                return;

            // Per-game block switch: maintained independently of the allow-list.
            if (e.PropertyName == nameof(GameFilterOption.IsExcluded))
            {
                List<string> blacklist = option.Platform == Platform.Twitch
                    ? _twitchGameBlacklistSlugs
                    : _kickGameBlacklistSlugs;

                if (option.IsExcluded)
                {
                    if (!blacklist.Contains(option.Slug, StringComparer.OrdinalIgnoreCase))
                        blacklist.Add(option.Slug);
                }
                else
                {
                    blacklist.RemoveAll(x => string.Equals(x, option.Slug, StringComparison.OrdinalIgnoreCase));
                }

                OnPropertyChanged(option.Platform == Platform.Twitch ? nameof(TwitchWhitelistSummary) : nameof(KickWhitelistSummary));
                Task.Run(SaveSettings);
                GameWhitelistChanged?.Invoke(option.Platform);
                return;
            }

            if (e.PropertyName != nameof(GameFilterOption.IsSelected))
                return;

            List<string> whitelist = option.Platform == Platform.Twitch
                ? _twitchGameWhitelistSlugs
                : _kickGameWhitelistSlugs;

            if (option.IsSelected)
            {
                if (!whitelist.Contains(option.Slug, StringComparer.OrdinalIgnoreCase))
                    whitelist.Add(option.Slug);
            }
            else
            {
                whitelist.RemoveAll(x => string.Equals(x, option.Slug, StringComparison.OrdinalIgnoreCase));

                if (option.DisplayName.EndsWith(" (inactive)", StringComparison.OrdinalIgnoreCase))
                {
                    ObservableCollection<GameFilterOption> collection = option.Platform == Platform.Twitch
                        ? TwitchGameFilterOptions
                        : KickGameFilterOptions;

                    _isUpdatingGameFilterOptions = true;
                    try
                    {
                        option.PropertyChanged -= OnGameFilterOptionPropertyChanged;
                        collection.Remove(option);
                    }
                    finally
                    {
                        _isUpdatingGameFilterOptions = false;
                    }
                }
            }

            if (option.Platform == Platform.Twitch)
                _twitchGameWhitelistSlugs = NormalizeWhitelist(whitelist);
            else
                _kickGameWhitelistSlugs = NormalizeWhitelist(whitelist);

            OnPropertyChanged(option.Platform == Platform.Twitch ? nameof(TwitchWhitelistSummary) : nameof(KickWhitelistSummary));
            RefreshPriorityRanks(option.Platform);
            Task.Run(SaveSettings);
            GameWhitelistChanged?.Invoke(option.Platform);
        }

        private static List<string> NormalizeWhitelist(IEnumerable<string>? values)
        {
            if (values == null)
                return new List<string>();

            return values
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}

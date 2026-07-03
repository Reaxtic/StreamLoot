using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Core.Interfaces;
using System.Windows;
using System.Timers;
using Core.Logging;
using Core.Models;
using Core.Enums;
using System.IO;

namespace Core.Managers
{
    public sealed class DropsInventoryManager
    {
        private static readonly Lazy<DropsInventoryManager> _instance = new(() => new DropsInventoryManager());
        public static DropsInventoryManager Instance => _instance.Value;

        public ObservableCollection<DropsCampaign> ActiveCampaigns { get; } = new ObservableCollection<DropsCampaign>();

        public IWebViewHost? TwitchWebView { get; private set; }
        public IWebViewHost? KickWebView { get; private set; }

        public event Action<byte, byte>? TwitchProgressChanged;
        public event Action<byte, byte>? KickProgressChanged;
        public event Action<string>? MinerStatusChanged;
        public event Action<string>? TwitchChannelChanged;
        public event Action<string>? KickChannelChanged;
        // (campaign name, game image URL). Empty name + null URL means "cleared".
        public event Action<string, string?>? TwitchCampaignChanged;
        public event Action<string, string?>? KickCampaignChanged;
        // (reward/item name, reward image URL). Empty name + null URL means "cleared".
        public event Action<string, string?>? TwitchDropChanged;
        public event Action<string, string?>? KickDropChanged;
        // Live/offline status of the currently watched streams (from the 30s health check).
        public event Action<bool>? TwitchStreamOnlineChanged;
        public event Action<bool>? KickStreamOnlineChanged;
        // Raised when the cached campaign list is detected as stale (contains ended campaigns) — e.g. after the app
        // ran for days across a PC sleep without a successful refresh. The Dashboard responds by reloading campaigns.
        public event Action? ReloadCampaignsRequested;
        private DateTime _lastStaleReloadRequest = DateTime.MinValue;

        // Currently watched campaigns
        private DropsCampaign? _currentTwitchCampaign;
        private string? _currentTwitchLogin; // login of the Twitch streamer currently being watched
        private string? _lastTwitchDropId; // id of the last reward reported via TwitchDropChanged
        private string? _lastKickDropId;   // id of the last reward reported via KickDropChanged
        private DropsCampaign? _currentKickCampaign;
        private IGqlService? _twitchGqlService;

        private int _twitchWatchedSeconds;
        private int _kickWatchedSeconds;
        private int _twitchDropWatchedSeconds;
        private int _kickDropWatchedSeconds;

        private bool _lastKnownKickOnlineState;
        private bool _lastKnownTwitchOnlineState;

        // Throttle the Twitch live/category GQL eligibility probe: doing it on every 30s health tick hammered
        // Twitch's GQL (~120 calls/hour just for this), which risks tripping their integrity/bot rate-limits.
        // We re-probe at most every ~2 min and reuse the cached verdict in between.
        private DateTime _lastTwitchEligibilityCheck = DateTime.MinValue;
        private bool _cachedTwitchEligible;

        // Live online state from the most recent health check (updated every 30s, set true at selection). The
        // optimistic per-minute tick only advances while this is true, so a bar can't keep climbing after the
        // watched streamer goes offline (which earns nothing on the server).
        private volatile bool _twitchCurrentlyOnline;
        private volatile bool _kickCurrentlyOnline;

        // Timer for live ticking
        private readonly System.Timers.Timer _liveProgressTimer = new(1000);
        private System.Timers.Timer? _recheckTimer;
        private System.Timers.Timer? _streamHealthTimer;
        // Sends a "minute-watched" event to Twitch every minute so drop watch time is credited server-side via the
        // analytics endpoint (the DevilXD approach), instead of relying on the hidden player which Twitch throttles.
        private readonly System.Timers.Timer _twitchWatchTimer = new(60 * 1000);

        private int _twitchAppliedMinuteBucket;
        private int _kickAppliedMinuteBucket;

        private readonly SemaphoreSlim _startWatchingLock = new(1, 1);
        private CancellationTokenSource? _startWatchingCts;
        private bool _isPaused;
        private readonly object _lastStreamerSync = new();
        private readonly Dictionary<string, string> _lastTwitchStreamers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _lastKickStreamers = new(StringComparer.OrdinalIgnoreCase);

        // Manual user overrides.
        private string? _forcedCampaignId; // when set, selection mines this campaign instead of auto-picking
        private string? _currentKickLogin; // login of the Kick streamer currently being watched

        // Campaigns whose SERVER progress stays frozen while we watch them (i.e. not actually crediting — e.g. an
        // R6S drop with a broken Ubisoft link). They get deprioritised in auto-selection so the miner moves to a
        // campaign that really credits. Reset on every full campaign refresh so they're periodically retried.
        private readonly object _creditSync = new();
        private readonly HashSet<string> _notCreditingCampaignIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (int Minutes, int FrozenCount)> _creditTracking = new(StringComparer.Ordinal);
        // Channels (logins) whose server progress froze while watched — they earn nothing right now (e.g. a 24/7
        // rerun, or a channel that stopped running the campaign's drops). Avoided when picking a streamer so the
        // miner rotates to a fresh live channel of the SAME campaign instead of returning to the dead one.
        private readonly HashSet<string> _stalledTwitchLogins = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _stalledKickLogins = new(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastCreditMarksCleared = DateTime.Now;
        // Failed claims are retried every ~10 min (see AutoClaimReadyRewardsAsync) — e.g. after the user links
        // their game account, the pending reward is collected within minutes instead of at the hourly refresh.
        private DateTime _lastFailedClaimMarksCleared = DateTime.Now;

        // Pin suspension: when the pinned campaign has NO live streamers, the pin is temporarily suspended and the
        // miner falls back to the best available campaign. The health check polls (throttled) whether a pinned
        // channel came back live and, when it did, lifts the suspension so mining returns to the user's choice.
        private volatile bool _pinSuspendedNoStreamers;
        private DateTime _pinSuspendedAt = DateTime.MinValue;
        private DateTime _lastPinOnlineCheck = DateTime.MinValue;

        // Stall-triggered re-evaluations are throttled: when the stalled channel is the ONLY live option, rotation
        // re-selects it and the stall flag stays set — without a throttle the health check would then force a
        // re-evaluation every ~30s, visibly resetting the dashboard cards over and over.
        private DateTime _lastTwitchStallReeval = DateTime.MinValue;
        private DateTime _lastKickStallReeval = DateTime.MinValue;
        private readonly HashSet<string> _skipTwitchLogins = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _skipKickLogins = new(StringComparer.OrdinalIgnoreCase);
        // A channel the user explicitly picked (e.g. from the Dashboard channel list). It is watched directly,
        // bypassing the live/category gate, so an explicit pick is never silently swapped back to another channel.
        // Tuple: (campaign id the pick is for, full channel URL). Consumed on the next selection for that campaign.
        private (string CampaignId, string Url)? _forcedTwitchStreamer;
        private (string CampaignId, string Url)? _forcedKickStreamer;
        // True when the most recent selection used an explicit user pick; lets the post-navigation eligibility
        // gate skip the fragile DOM category check (but still respect the online check) for that pick.
        private bool _twitchSelectionForced;
        private bool _kickSelectionForced;
        public string? ForcedCampaignId => _forcedCampaignId;
        private readonly object _campaignSnapshotSync = new();
        private List<DropsCampaign> _lastKnownCampaigns = new();

        private static readonly string _lastWatchedStreamersFilePath = Path.Combine(
            Environment.ExpandEnvironmentVariables("%APPDATA%"),
            "Stream Loot",
            "LastWatchedStreamers.json");

        // Persisted "Mine this" pin so the chosen campaign keeps being mined across restarts.
        private static readonly string _forcedCampaignFilePath = Path.Combine(
            Environment.ExpandEnvironmentVariables("%APPDATA%"),
            "Stream Loot",
            "ForcedCampaign.txt");

        private static void SaveForcedCampaignId(string? campaignId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(campaignId))
                {
                    if (File.Exists(_forcedCampaignFilePath)) File.Delete(_forcedCampaignFilePath);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_forcedCampaignFilePath)!);
                    File.WriteAllText(_forcedCampaignFilePath, campaignId);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Selection", $"Failed to persist forced campaign: {ex.Message}");
            }
        }

        private static string? LoadForcedCampaignId()
        {
            try
            {
                if (File.Exists(_forcedCampaignFilePath))
                {
                    string v = File.ReadAllText(_forcedCampaignFilePath).Trim();
                    return string.IsNullOrWhiteSpace(v) ? null : v;
                }
            }
            catch { }
            return null;
        }

        private static bool IsVerboseDebugEnabled => UISettingsManager.Instance.VerboseDebugLogging;

        /// <summary>
        /// Logs a message at the informational level if verbose debug logging is enabled.
        /// </summary>
        /// <param name="scope">The logical scope or category associated with the log message. Used to group related log entries.</param>
        /// <param name="message">The message to log. Should provide relevant information about the operation or event.</param>
        private static void VerboseLog(string scope, string message)
        {
            if (IsVerboseDebugEnabled)
                AppLogger.Info(scope, message);
        }

        /// <summary>
        /// Initializes a new instance of the DropsInventoryManager class.
        /// </summary>
        /// <remarks>This constructor is private to enforce the singleton pattern. It sets up event
        /// handlers and initializes internal state required for managing drops inventory. Instances of this class can
        /// only be created internally within the class.</remarks>
        private DropsInventoryManager()
        {
            LoadLastWatchedStreamers();
            _forcedCampaignId = LoadForcedCampaignId(); // restore a "Mine this" pin across restarts
            UISettingsManager.Instance.MiningPriorityModeChanged += OnMiningPriorityModeChanged;
            UISettingsManager.Instance.GameWhitelistChanged += OnGameWhitelistChanged;

            _liveProgressTimer.Elapsed += OnLiveProgressTick;
            _liveProgressTimer.AutoReset = true;

            _twitchWatchTimer.Elapsed += OnTwitchWatchHeartbeat;
            _twitchWatchTimer.AutoReset = true;
            _twitchWatchTimer.Start();
        }

        /// <summary>
        /// Every minute, registers a "minute-watched" event with Twitch for the current channel so drop progress is
        /// credited server-side regardless of whether the hidden player is actually decoding video.
        /// </summary>
        private async void OnTwitchWatchHeartbeat(object? sender, ElapsedEventArgs e)
        {
            try
            {
                string? login = _currentTwitchLogin;
                if (!_isPaused && _twitchGqlService != null && !string.IsNullOrWhiteSpace(login))
                    await _twitchGqlService.SendWatchHeartbeatAsync(login!);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("TwitchWatch", $"Heartbeat tick failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles changes to the mining priority mode by applying the specified mode.
        /// </summary>
        /// <param name="mode">The new mining priority mode to apply.</param>
        private void OnMiningPriorityModeChanged(MiningPriorityMode mode)
        {
            _ = ApplyMiningPriorityModeChangeAsync(mode);
        }
        /// <summary>
        /// Handles changes to the game whitelist for the specified platform.
        /// </summary>
        /// <param name="platform">The platform for which the game whitelist has changed.</param>
        private void OnGameWhitelistChanged(Platform platform)
        {
            _ = ApplyGameWhitelistChangeAsync(platform);
        }
        /// <summary>
        /// Applies a change to the mining priority mode and triggers an immediate re-evaluation of active campaigns if
        /// applicable.
        /// </summary>
        /// <remarks>If the miner is paused, there are no active campaigns, or no webviews are
        /// initialized, the re-evaluation is skipped. Logging is performed to indicate the outcome of the
        /// operation.</remarks>
        /// <param name="mode">The new mining priority mode to apply. Determines how mining resources are prioritized during stream
        /// evaluation.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        private async Task ApplyMiningPriorityModeChangeAsync(MiningPriorityMode mode)
        {
            try
            {
                AppLogger.Info("Miner", $"Mining priority mode changed to {mode}. Triggering immediate re-evaluation.");

                if (_isPaused)
                {
                    AppLogger.Warn("Miner", "Priority mode changed while miner is paused; re-evaluation skipped.");
                    return;
                }

                if (!ActiveCampaigns.Any())
                {
                    AppLogger.Warn("Miner", "Priority mode changed but there are no active campaigns; re-evaluation skipped.");
                    return;
                }

                if (TwitchWebView == null && KickWebView == null)
                {
                    AppLogger.Warn("Miner", "Priority mode changed but no webviews are initialized; re-evaluation skipped.");
                    return;
                }

                AppLogger.Debug("Miner", $"Immediate re-evaluation starting after priority mode change. activeCampaigns={ActiveCampaigns.Count}");
                await StartWatchingStreams(true);
                AppLogger.Info("Miner", "Immediate re-evaluation completed after priority mode change.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Miner", "Failed to apply mining priority mode change immediately.", ex);
            }
        }
        /// <summary>
        /// Applies changes to the game whitelist for the specified platform and triggers an immediate re-evaluation of
        /// active campaigns if appropriate.
        /// </summary>
        /// <remarks>Re-evaluation is skipped if the miner is paused, if there are no active campaigns
        /// after filtering, or if no webviews are initialized. Logging is performed to provide information about the
        /// operation's progress and any conditions that prevent re-evaluation.</remarks>
        /// <param name="platform">The platform for which the game whitelist has changed. Determines which set of campaigns and streams are
        /// affected by the update.</param>
        /// <returns>A task that represents the asynchronous operation of applying the whitelist change and re-evaluating active
        /// campaigns.</returns>
        private async Task ApplyGameWhitelistChangeAsync(Platform platform)
        {
            try
            {
                AppLogger.Info("Miner", $"{platform} game whitelist changed. Triggering immediate re-evaluation.");

                RefreshActiveCampaignsFromLatestSnapshot();

                if (_isPaused)
                {
                    AppLogger.Warn("Miner", "Whitelist changed while miner is paused; re-evaluation skipped.");
                    return;
                }

                if (!ActiveCampaigns.Any())
                {
                    AppLogger.Warn("Miner", "Whitelist changed but there are no active campaigns after filtering; re-evaluation skipped.");
                    return;
                }

                if (TwitchWebView == null && KickWebView == null)
                {
                    AppLogger.Warn("Miner", "Whitelist changed but no webviews are initialized; re-evaluation skipped.");
                    return;
                }

                AppLogger.Debug("Miner", $"Immediate re-evaluation starting after whitelist change. activeCampaigns={ActiveCampaigns.Count}");
                await StartWatchingStreams(true, platform); // only the platform whose whitelist changed
                AppLogger.Info("Miner", "Immediate re-evaluation completed after whitelist change.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Miner", "Failed to apply game whitelist change immediately.", ex);
            }
        }
        /// <summary>
        /// Refreshes the list of active campaigns using the most recent campaign snapshot and updates the UI
        /// accordingly.
        /// </summary>
        /// <remarks>This method synchronizes the active campaigns with the latest known snapshot and
        /// applies UI filters to determine which campaigns are displayed. It must be called on the UI thread, as it
        /// updates UI-bound collections and settings.</remarks>
        private void RefreshActiveCampaignsFromLatestSnapshot()
        {
            List<DropsCampaign> snapshot;
            lock (_campaignSnapshotSync)
            {
                snapshot = [.. _lastKnownCampaigns];
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                List<DropsCampaign> sourceCampaigns = snapshot.Count != 0
                    ? snapshot
                    : [.. ActiveCampaigns];

                UISettingsManager.Instance.UpdateAvailableGameFilterOptions(sourceCampaigns);

                List<DropsCampaign> filteredCampaigns = sourceCampaigns
                    .Where(c => UISettingsManager.Instance.IsCampaignAllowedByWhitelist(c))
                    .Where(c => c.StartsAt <= DateTimeOffset.Now && c.EndsAt > DateTimeOffset.Now)
                    .OrderBy(x => x.Platform).ThenBy(x => x.GameName)
                    .ToList();

                ActiveCampaigns.Clear();
                foreach (DropsCampaign campaign in filteredCampaigns)
                    ActiveCampaigns.Add(campaign);

                UpdateCurrentSelectionFlags();
            });
        }
        /// <summary>
        /// Applies the specified number of minutes of progress to the active campaign for the given platform and
        /// campaign identifier.
        /// </summary>
        /// <remarks>If the specified campaign is not found or has no progress to make, no changes are
        /// applied. The method updates the progress for all rewards in the campaign and synchronizes the current
        /// campaign selection if applicable. This method must be called from the UI thread, as it updates UI-bound
        /// collections.</remarks>
        /// <param name="platform">The platform on which the campaign is active. Determines which campaign collection to update.</param>
        /// <param name="campaignId">The unique identifier of the campaign to which progress will be applied.</param>
        /// <param name="minutesToAdd">The number of minutes to add to the campaign's progress. Must be greater than zero.</param>
        private void ApplyMinuteProgressToActiveCampaign(Platform platform, string campaignId, int minutesToAdd)
        {
            if (minutesToAdd <= 0)
                return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                DropsCampaign? campaign = ActiveCampaigns.FirstOrDefault(c => c.Platform == platform && c.Id == campaignId);
                if (campaign == null || !campaign.HasProgressToMake())
                {
                    bool hadSelection = platform == Platform.Twitch ? _currentTwitchCampaign != null : _currentKickCampaign != null;

                    switch (platform)
                    {
                        case Platform.Twitch:
                            _currentTwitchCampaign = null;
                            break;
                        case Platform.Kick:
                            _currentKickCampaign = null;
                            break;
                    }

                    UpdateCurrentSelectionFlags();

                    // The watched campaign just completed — immediately pick the next available campaign
                    // on THIS platform only (the other platform keeps mining undisturbed).
                    if (hadSelection)
                    {
                        AppLogger.Info("Miner", $"{platform} campaign '{campaign?.Name ?? campaignId}' has no progress left; selecting next campaign on {platform}.");
                        _ = Task.Run(async () => await StartWatchingStreams(true, platform));
                    }
                    return;
                }

                int campaignIndex = ActiveCampaigns.IndexOf(campaign);
                if (campaignIndex < 0)
                    return;

                List<DropsReward> updatedRewards = new List<DropsReward>(campaign.Rewards.Count);
                foreach (DropsReward reward in campaign.Rewards)
                {
                    int newProgress = reward.IsClaimed || reward.ProgressMinutes >= reward.RequiredMinutes
                        ? reward.ProgressMinutes
                        : Math.Min(reward.ProgressMinutes + minutesToAdd, reward.RequiredMinutes);

                    updatedRewards.Add(reward with { ProgressMinutes = newProgress });
                }

                VerboseLog("MinuteTick", $"campaignId={campaign.Id}, platform={campaign.Platform}, minutesAdded={minutesToAdd}, rewardsUpdated={campaign.Rewards.Count}, unclaimedRewards={campaign.Rewards.Count(r => !r.IsClaimed)}");
                VerboseLog("RewardTransition", $"platform={platform}, campaignId={campaignId}, minutesAdded={minutesToAdd}, rewards={string.Join(", ", updatedRewards.Select(r => $"{r.Name}:{r.ProgressMinutes}/{r.RequiredMinutes}(claimed={r.IsClaimed})"))}");

                DropsCampaign updatedCampaign = campaign with { Rewards = updatedRewards };
                ActiveCampaigns[campaignIndex] = updatedCampaign;

                if (platform == Platform.Twitch && _currentTwitchCampaign?.Id == campaignId)
                    _currentTwitchCampaign = updatedCampaign;

                if (platform == Platform.Kick && _currentKickCampaign?.Id == campaignId)
                    _currentKickCampaign = updatedCampaign;

                UpdateCurrentSelectionFlags();
            });
        }

        /// <summary>
        /// Handles the timer tick event to update live progress for active Twitch and Kick campaigns.
        /// </summary>
        /// <remarks>This method increments the watched time for each active campaign and raises the
        /// corresponding progress changed events. It is intended to be used as an event handler for timer-based
        /// progress updates.</remarks>
        /// <param name="sender">The source of the event, typically the timer that triggered the tick.</param>
        /// <param name="e">An ElapsedEventArgs object that contains the event data.</param>
        private void OnLiveProgressTick(object? sender, ElapsedEventArgs e)
        {
            DropsCampaign? currentTwitchCampaign = _currentTwitchCampaign;
            if (currentTwitchCampaign != null && _twitchCurrentlyOnline)
            {
                _twitchWatchedSeconds++;
                _twitchDropWatchedSeconds++;

                DropsReward? nextTwitchReward = currentTwitchCampaign.Rewards
                    .Where(r => !r.IsClaimed)
                    .OrderBy(r => r.RequiredMinutes)
                    .FirstOrDefault();

                VerboseLog("DropPointer", $"Twitch nextReward={nextTwitchReward?.Name ?? "none"}, nextRewardId={nextTwitchReward?.Id ?? "none"}, requiredMinutes={nextTwitchReward?.RequiredMinutes ?? 0}, dropWatchedSeconds={_twitchDropWatchedSeconds}");
                RaiseTwitchDropChangedIfNeeded(nextTwitchReward);

                int twitchMinuteBucket = _twitchWatchedSeconds / 60;
                if (twitchMinuteBucket > _twitchAppliedMinuteBucket)
                {
                    int minutesToApply = twitchMinuteBucket - _twitchAppliedMinuteBucket;
                    _twitchAppliedMinuteBucket = twitchMinuteBucket;
                    ApplyMinuteProgressToActiveCampaign(Platform.Twitch, currentTwitchCampaign.Id, minutesToApply);
                    ApplyMinuteProgressToCoProgressingCampaigns(Platform.Twitch, currentTwitchCampaign, minutesToApply);
                }

                byte twitchCampPct = CalculateLiveCampaignProgress(currentTwitchCampaign);
                byte twitchDropPct = CalculateLiveDropProgress(currentTwitchCampaign, _twitchDropWatchedSeconds);
                VerboseLog("LiveProgress", $"Twitch tick campaignId={currentTwitchCampaign.Id}, campaignWatchedSeconds={_twitchWatchedSeconds}, dropWatchedSeconds={_twitchDropWatchedSeconds}, campaignPct={twitchCampPct}, dropPct={twitchDropPct}");
                TwitchProgressChanged?.Invoke(twitchCampPct, twitchDropPct);
            }

            DropsCampaign? currentKickCampaign = _currentKickCampaign;
            if (currentKickCampaign != null && _kickCurrentlyOnline)
            {
                _kickWatchedSeconds++;
                _kickDropWatchedSeconds++;

                DropsReward? nextKickReward = currentKickCampaign.Rewards
                    .Where(r => !r.IsClaimed)
                    .OrderBy(r => r.RequiredMinutes)
                    .FirstOrDefault();

                VerboseLog("DropPointer", $"Kick nextReward={nextKickReward?.Name ?? "none"}, nextRewardId={nextKickReward?.Id ?? "none"}, requiredMinutes={nextKickReward?.RequiredMinutes ?? 0}, dropWatchedSeconds={_kickDropWatchedSeconds}");
                RaiseKickDropChangedIfNeeded(nextKickReward);

                int kickMinuteBucket = _kickWatchedSeconds / 60;
                if (kickMinuteBucket > _kickAppliedMinuteBucket)
                {
                    int minutesToApply = kickMinuteBucket - _kickAppliedMinuteBucket;
                    _kickAppliedMinuteBucket = kickMinuteBucket;
                    ApplyMinuteProgressToActiveCampaign(Platform.Kick, currentKickCampaign.Id, minutesToApply);
                    ApplyMinuteProgressToCoProgressingCampaigns(Platform.Kick, currentKickCampaign, minutesToApply);
                }

                byte kickCampPct = CalculateLiveCampaignProgress(currentKickCampaign);
                byte kickDropPct = CalculateLiveDropProgress(currentKickCampaign, _kickDropWatchedSeconds);
                VerboseLog("LiveProgress", $"Kick tick campaignId={currentKickCampaign.Id}, campaignWatchedSeconds={_kickWatchedSeconds}, dropWatchedSeconds={_kickDropWatchedSeconds}, campaignPct={kickCampPct}, dropPct={kickDropPct}");
                KickProgressChanged?.Invoke(kickCampPct, kickDropPct);
            }
        }
        /// <summary>
        /// Initializes the Twitch and Kick web views using the specified hosts.
        /// </summary>
        /// <param name="twitch">The host instance to associate with the Twitch web view. Cannot be null.</param>
        /// <param name="kick">The host instance to associate with the Kick web view. Cannot be null.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="twitch"/> or <paramref name="kick"/> is null.</exception>
        public void InitializeWebViews(IWebViewHost twitch, IWebViewHost kick)
        {
            TwitchWebView = twitch ?? throw new ArgumentNullException(nameof(twitch));
            KickWebView = kick ?? throw new ArgumentNullException(nameof(kick));
        }
        /// <summary>
        /// Updates the list of active campaigns based on the specified collection.
        /// </summary>
        /// <remarks>This method clears the current active campaigns and repopulates the list with
        /// eligible campaigns from the provided collection. The update is performed on the application's UI thread.
        /// After updating, the method initiates stream watching for the active campaigns.</remarks>
        /// <param name="campaigns">A collection of <see cref="DropsCampaign"/> objects to evaluate and update as active campaigns. Only
        /// campaigns that have progress to make, have started, and have not yet ended are considered.</param>
        /// <summary>
        /// Combines a freshly fetched campaign with the previously displayed one. When the fresh data
        /// carries a real progress signal (any reward with progress or claimed), the SERVER is
        /// authoritative — including corrections downward, so locally over-estimated ticks get fixed.
        /// Only when the fresh campaign is completely blank (likely a failed progress capture) do we keep
        /// the higher local values to avoid wiping what the user already earned.
        /// </summary>
        private static DropsCampaign MergeCampaignProgress(DropsCampaign fresh, DropsCampaign previous)
        {
            // Availability is computed client-side (not part of the server snapshot), so carry it across reloads.
            fresh = fresh with { Availability = previous.Availability, OnlineChannels = previous.OnlineChannels };

            bool freshHasData = fresh.Rewards.Any(r => r.ProgressMinutes > 0 || r.IsClaimed);
            if (freshHasData)
                return fresh; // trust the server snapshot entirely

            Dictionary<string, DropsReward> previousRewards = new(StringComparer.Ordinal);
            foreach (DropsReward r in previous.Rewards)
                previousRewards[r.Id] = r;

            List<DropsReward> mergedRewards = new List<DropsReward>(fresh.Rewards.Count);
            foreach (DropsReward reward in fresh.Rewards)
            {
                if (previousRewards.TryGetValue(reward.Id, out DropsReward? prev))
                {
                    mergedRewards.Add(reward with
                    {
                        ProgressMinutes = Math.Max(reward.ProgressMinutes, prev.ProgressMinutes),
                        IsClaimed = reward.IsClaimed || prev.IsClaimed
                    });
                }
                else
                {
                    mergedRewards.Add(reward);
                }
            }

            return fresh with { Rewards = mergedRewards };
        }

        public void UpdateCampaigns(IEnumerable<DropsCampaign> campaigns, IGqlService? twitchGqlService, bool startWatching = true)
        {
            _twitchGqlService = twitchGqlService;
            List<DropsCampaign> allCampaigns = campaigns.ToList();

            // Give "not crediting" campaigns another chance only occasionally (~hourly) — NOT on every 20-min
            // refresh, otherwise a permanently-stuck campaign (e.g. R6S with a broken Ubisoft link) kept coming
            // back, getting re-selected, watched ~6 min, re-flagged, and switched off — looking like a reset.
            lock (_creditSync)
            {
                if ((DateTime.Now - _lastCreditMarksCleared) > TimeSpan.FromMinutes(60))
                {
                    _notCreditingCampaignIds.Clear();
                    _creditTracking.Clear();
                    _stalledTwitchLogins.Clear();
                    _stalledKickLogins.Clear();
                    _lastCreditMarksCleared = DateTime.Now;
                }
            }
            lock (_failedClaimRewardIds)
            {
                _failedClaimRewardIds.Clear();
            }

            lock (_campaignSnapshotSync)
            {
                _lastKnownCampaigns = [.. allCampaigns];
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                UISettingsManager.Instance.UpdateAvailableGameFilterOptions(allCampaigns);

                // Snapshot current progress so a refresh never WIPES what the user already
                // earned/accumulated (e.g. when a server-side progress capture momentarily returns
                // empty). We keep the higher of local vs server per reward.
                Dictionary<string, DropsCampaign> existingById = new(StringComparer.Ordinal);
                foreach (DropsCampaign existing in ActiveCampaigns)
                    existingById[existing.Id] = existing;

                List<DropsCampaign> filteredCampaigns = allCampaigns
                    .Where(c => UISettingsManager.Instance.IsCampaignAllowedByWhitelist(c))
                    .ToList();

                ActiveCampaigns.Clear();
                foreach (DropsCampaign? c in filteredCampaigns.Where(c => c.StartsAt <= DateTimeOffset.Now && c.EndsAt > DateTimeOffset.Now).OrderBy(x => x.Platform).ThenBy(x => x.GameName))
                {
                    DropsCampaign toAdd = existingById.TryGetValue(c.Id, out DropsCampaign? previous)
                        ? MergeCampaignProgress(c, previous)
                        : c;
                    ActiveCampaigns.Add(toAdd);
                }

                // Keep the "currently watched" references pointing at the freshly-rebuilt objects so a platform
                // that isn't being re-selected (scoped restart) keeps a valid reference for the live timer/UI.
                if (_currentTwitchCampaign != null)
                    _currentTwitchCampaign = ActiveCampaigns.FirstOrDefault(c => c.Id == _currentTwitchCampaign.Id) ?? _currentTwitchCampaign;
                if (_currentKickCampaign != null)
                    _currentKickCampaign = ActiveCampaigns.FirstOrDefault(c => c.Id == _currentKickCampaign.Id) ?? _currentKickCampaign;

                UpdateCurrentSelectionFlags();
            });

            if (startWatching && !_isPaused)
                _ = StartWatchingStreams(); // Fire and forget - will handle its own loop
        }
        /// <summary>
        /// Temporarily pauses stream watching and waits for any active watch cycle to exit.
        /// </summary>
        public async Task PauseWatchingAsync()
        {
            _isPaused = true;
            _startWatchingCts?.Cancel();

            _recheckTimer?.Stop();
            _streamHealthTimer?.Stop();
            _liveProgressTimer.Stop();

            await _startWatchingLock.WaitAsync();
            _startWatchingLock.Release();
        }
        /// <summary>
        /// Resumes stream watching if it was previously paused.
        /// </summary>
        public async Task ResumeWatchingAsync(Platform? onlyPlatform = null)
        {
            if (!_isPaused)
                return;

            _isPaused = false;
            // Scope the restart so connecting/refreshing one platform doesn't re-select (reset) the other,
            // which is still mining. The skipped platform keeps its current stream and baseline.
            await StartWatchingStreams(restartedInternally: false, onlyPlatform: onlyPlatform);
        }

        // === Manual user overrides (driven from the UI) ===

        /// <summary>
        /// Pins a specific campaign to mine. Pass null to return to automatic priority selection.
        /// Triggers an immediate re-evaluation.
        /// </summary>
        public async Task SetForcedCampaignAsync(string? campaignId)
        {
            string? previousForcedId = _forcedCampaignId;
            _forcedCampaignId = string.IsNullOrWhiteSpace(campaignId) ? null : campaignId;
            _pinSuspendedNoStreamers = false; // any pin change starts fresh (a new pin gets its own live check)
            SaveForcedCampaignId(_forcedCampaignId); // persist so the pin survives a restart
            AppLogger.Info("Selection", _forcedCampaignId == null
                ? "Cleared forced campaign; back to automatic selection."
                : $"User forced campaign id={_forcedCampaignId}.");

            lock (_lastStreamerSync)
            {
                _skipTwitchLogins.Clear();
                _skipKickLogins.Clear();
            }

            UpdateCurrentSelectionFlags(); // reflect the pin (IsPinned badge / Unpin button) immediately

            // Re-evaluate ONLY the platform involved (the campaign being pinned, or the one being unpinned), so the
            // OTHER platform keeps mining undisturbed — pinning a Twitch campaign must not reset Kick, and vice versa.
            string? relevantId = _forcedCampaignId ?? previousForcedId;
            Platform? scope = string.IsNullOrWhiteSpace(relevantId)
                ? null
                : ActiveCampaigns.FirstOrDefault(c => c.Id == relevantId)?.Platform;

            if (!_isPaused)
                await StartWatchingStreams(true, scope);
        }

        /// <summary>
        /// Skips the streamer currently watched on the given platform and switches to a different live
        /// one for the same campaign.
        /// </summary>
        public async Task SkipCurrentStreamerAsync(Platform platform)
        {
            DropsCampaign? campaign = platform == Platform.Twitch ? _currentTwitchCampaign : _currentKickCampaign;
            if (campaign == null)
                return;

            string? login = platform == Platform.Twitch ? _currentTwitchLogin : _currentKickLogin;
            if (!string.IsNullOrWhiteSpace(login))
            {
                lock (_lastStreamerSync)
                {
                    (platform == Platform.Twitch ? _skipTwitchLogins : _skipKickLogins).Add(login!);
                }
            }

            ForgetLastStreamerUrl(platform, campaign.Id);
            AppLogger.Info("Selection", $"User requested streamer switch on {platform} (skipping '{login}').");

            if (!_isPaused)
                await StartWatchingStreams(true, platform); // re-select only this platform
        }

        /// <summary>
        /// Sets a specific streamer (full URL or just the channel name) to watch for the campaign
        /// currently being mined on the given platform.
        /// </summary>
        public async Task SetPreferredStreamerAsync(Platform platform, string streamerUrlOrName)
        {
            DropsCampaign? campaign = platform == Platform.Twitch ? _currentTwitchCampaign : _currentKickCampaign;
            if (campaign == null || string.IsNullOrWhiteSpace(streamerUrlOrName))
                return;

            string url = NormalizeStreamerUrl(platform, streamerUrlOrName.Trim());
            string login = GetStreamerNameFromUrl(url);

            // Already watching this exact channel: nothing to do. Avoids a pointless re-navigation that
            // would briefly reset the on-screen progress to 0 (e.g. clicking the channel you're already on).
            string? currentLogin = platform == Platform.Twitch ? _currentTwitchLogin : _currentKickLogin;
            if (!string.IsNullOrWhiteSpace(currentLogin) && string.Equals(currentLogin, login, StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Info("Selection", $"Preferred {platform} streamer '{login}' is already being watched; skipping restart.");
                return;
            }

            lock (_lastStreamerSync)
            {
                (platform == Platform.Twitch ? _skipTwitchLogins : _skipKickLogins).Remove(login);

                // Mark it as an explicit user pick so selection watches it directly (no live/category gate).
                if (platform == Platform.Twitch)
                    _forcedTwitchStreamer = (campaign.Id, url);
                else
                    _forcedKickStreamer = (campaign.Id, url);
            }

            RememberLastStreamerUrl(platform, campaign.Id, url);
            AppLogger.Info("Selection", $"User set preferred {platform} streamer '{url}' for campaign '{campaign.Name}'.");

            if (!_isPaused)
                await StartWatchingStreams(true, platform); // re-select only this platform
        }

        private static string NormalizeStreamerUrl(Platform platform, string input)
        {
            if (input.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return input;

            string host = platform == Platform.Twitch ? "https://www.twitch.tv/" : "https://kick.com/";
            return host + input.TrimStart('/');
        }

        /// <summary>
        /// Verifies whether a given channel is currently a valid participant for the campaign being mined
        /// on the platform: for Twitch this checks live + correct category via GraphQL; for both platforms
        /// it checks participation against the campaign's channel list. Returns a human-readable result.
        /// </summary>
        public async Task<string> CheckChannelForCampaignAsync(Platform platform, string channelInput)
        {
            DropsCampaign? campaign = platform == Platform.Twitch ? _currentTwitchCampaign : _currentKickCampaign;
            if (campaign == null)
                return $"No {platform} campaign is being mined right now.";

            if (string.IsNullOrWhiteSpace(channelInput))
                return "Enter a channel name or URL first.";

            string login = GetStreamerNameFromUrl(NormalizeStreamerUrl(platform, channelInput.Trim()));
            if (string.IsNullOrWhiteSpace(login))
                return "Could not read a channel name from that input.";

            bool inCampaignList = ChannelIsInCampaign(campaign, login);

            if (platform == Platform.Twitch)
            {
                bool? eligible = await IsTwitchStreamEligibleViaGqlAsync(login, campaign.Slug);

                if (eligible == true)
                {
                    if (!campaign.IsGeneralDrop && !inCampaignList)
                        return $"⚠ '{login}' is live in {campaign.GameName}, but is NOT in this campaign's channel list — it will not earn this drop.";
                    return $"✓ '{login}' is live and streaming {campaign.GameName} — good to mine.";
                }

                if (eligible == false)
                    return $"✗ '{login}' is offline or not streaming {campaign.GameName} right now.";

                // GQL couldn't decide (no slug etc.) – fall back to list membership info.
                return inCampaignList
                    ? $"'{login}' is in this campaign's channel list, but its live status could not be verified."
                    : $"Could not verify '{login}'.";
            }

            // Kick: a non-disruptive live check for an arbitrary channel isn't available here, so report
            // participation (live status is still verified by the watcher once selected).
            if (!campaign.IsGeneralDrop)
            {
                return inCampaignList
                    ? $"✓ '{login}' is part of this Kick campaign (live status is confirmed when watching)."
                    : $"✗ '{login}' is not in this Kick campaign's channel list.";
            }

            return $"Any live {campaign.GameName} channel counts for this general Kick drop; live status is verified once watching starts.";
        }

        private bool ChannelIsInCampaign(DropsCampaign campaign, string login)
        {
            if (campaign.ConnectUrls == null || campaign.ConnectUrls.Count == 0)
                return false;

            return campaign.ConnectUrls.Any(u => string.Equals(GetStreamerNameFromUrl(u), login, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsSameGame(DropsCampaign a, DropsCampaign b)
        {
            if (!string.IsNullOrWhiteSpace(a.Slug) && !string.IsNullOrWhiteSpace(b.Slug))
                return string.Equals(a.Slug, b.Slug, StringComparison.OrdinalIgnoreCase);

            return !string.IsNullOrWhiteSpace(a.GameName)
                && string.Equals(a.GameName, b.GameName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when campaign <paramref name="c"/> earns progress simultaneously while the user watches
        /// <paramref name="watched"/> on channel <paramref name="login"/>: same game AND either a general
        /// drop or a campaign whose channel list includes the watched channel.
        /// </summary>
        private bool CampaignCoProgresses(DropsCampaign c, DropsCampaign watched, string? login)
        {
            if (c.Platform != watched.Platform || c.Id == watched.Id || !IsSameGame(c, watched))
                return false;

            // A campaign that lists specific channels only earns while watching one of THOSE channels — even when
            // it's flagged "general" (e.g. Kick "Football Drop" tied to a particular streamer). Only a true category
            // drop (no listed channels) earns on any same-game channel. This stops the co-mining display from adding
            // fake progress to channel-bound campaigns whose channel you aren't watching.
            bool hasChannels = CampaignChannelLogins(c).Count > 0;
            if (c.IsGeneralDrop && !hasChannels)
                return true;

            return !string.IsNullOrWhiteSpace(login) && ChannelIsInCampaign(c, login!);
        }

        /// <summary>
        /// Returns channels you can pick to watch for the campaign currently selected on the platform.
        /// Twitch: the participating channels confirmed LIVE (and in the right category) via GraphQL.
        /// Kick: the campaign's participating channels (live status is confirmed once you start watching).
        /// </summary>
        public async Task<IReadOnlyList<ChannelCandidate>> GetChannelCandidatesAsync(Platform platform)
        {
            DropsCampaign? campaign = platform == Platform.Twitch ? _currentTwitchCampaign : _currentKickCampaign;
            if (campaign == null)
                return Array.Empty<ChannelCandidate>();

            // Only real channel URLs ("kick.com/name" / "twitch.tv/name"), not category/browse/directory links.
            List<string> logins = (campaign.ConnectUrls ?? (IReadOnlyList<string>)Array.Empty<string>())
                .Where(IsRealChannelUrl)
                .Select(GetStreamerNameFromUrl)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (platform == Platform.Twitch)
            {
                if (_twitchGqlService == null || string.IsNullOrWhiteSpace(campaign.Slug))
                    return Array.Empty<ChannelCandidate>();

                try
                {
                    // General drops aren't tied to specific channels: list the game directory's live drops-enabled
                    // channels (with viewer counts). Channel-specific drops: confirm which participating channels are live.
                    if (logins.Count == 0)
                    {
                        List<(string Login, int Viewers)> dir = await _twitchGqlService.QueryLiveDirectoryChannelsAsync(campaign.Slug, 30);
                        return dir
                            .Select(d => new ChannelCandidate(d.Login, $"https://www.twitch.tv/{d.Login}", true, d.Viewers))
                            .ToList();
                    }

                    // Prefer the variant that also returns viewer counts; fall back to the login-only query
                    // (shown as a green dot without a number) if the raw query yields nothing.
                    List<(string Login, int Viewers)> liveWithViewers = await _twitchGqlService.QueryLiveChannelsWithViewersBySlugAsync(logins, campaign.Slug);
                    if (liveWithViewers.Count != 0)
                    {
                        return liveWithViewers
                            .Select(d => new ChannelCandidate(d.Login, $"https://www.twitch.tv/{d.Login}", true, d.Viewers))
                            .ToList();
                    }

                    List<string> live = await _twitchGqlService.QueryLiveChannelsBySlugAsync(logins, campaign.Slug);
                    return live
                        .Select(l => new ChannelCandidate(l, $"https://www.twitch.tv/{l}", true))
                        .ToList();
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("TwitchSelection", $"Channel candidate lookup failed: {ex.Message}");
                    return Array.Empty<ChannelCandidate>();
                }
            }

            // Kick: participating channels, with live/online status (and viewer count) from Kick's channel API.
            List<string> kickSlugs = logins.Take(30).ToList();
            if (kickSlugs.Count == 0)
                return Array.Empty<ChannelCandidate>();

            Dictionary<string, int> kickStatus = new(StringComparer.OrdinalIgnoreCase);
            if (KickWebView != null)
            {
                try
                {
                    string statusJson = await await Application.Current.Dispatcher.InvokeAsync(
                        async () => await KickWebView!.FetchKickChannelStatusesAsync(kickSlugs));

                    using JsonDocument doc = JsonDocument.Parse(statusJson);
                    foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Number)
                            kickStatus[prop.Name] = prop.Value.GetInt32();
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("KickSelection", $"Channel status lookup failed: {ex.Message}");
                }
            }

            return kickSlugs
                .Select(l =>
                {
                    int viewers = kickStatus.TryGetValue(l, out int v) ? v : -1;
                    return new ChannelCandidate(l, $"https://kick.com/{l}", viewers >= 0, Math.Max(0, viewers));
                })
                .OrderByDescending(c => c.Online)
                .ThenByDescending(c => c.Viewers)
                .ToList();
        }

        private static bool IsRealChannelUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;
            try
            {
                string path = new Uri(url).AbsolutePath.Trim('/');
                if (string.IsNullOrEmpty(path) || path.Contains('/'))
                    return false; // multi-segment = category/browse/directory, not a channel
                return path is not ("category" or "browse" or "directory");
            }
            catch
            {
                return false;
            }
        }

        // Reserved first-path segments that are never channel names on Twitch/Kick.
        private static readonly HashSet<string> _nonChannelSegments = new(StringComparer.OrdinalIgnoreCase)
        {
            "category", "browse", "directory", "search", "videos", "clips", "video",
            "following", "subscriptions", "settings", "drops", "u", "popout"
        };

        /// <summary>
        /// Reduces a URL like "kick.com/westcol/videos/&lt;id&gt;" (a VOD link the directory scraper sometimes
        /// returns) to the channel root "kick.com/westcol". Returns false when the first path segment is a
        /// reserved word (e.g. category/directory), so a category page is never treated as a channel.
        /// </summary>
        private static bool TryNormalizeChannelUrl(Platform platform, string? url, out string channelUrl)
        {
            channelUrl = string.Empty;
            if (string.IsNullOrWhiteSpace(url))
                return false;
            try
            {
                string[] segments = new Uri(url).AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0 || _nonChannelSegments.Contains(segments[0]))
                    return false;

                string host = platform == Platform.Twitch ? "https://www.twitch.tv" : "https://kick.com";
                channelUrl = $"{host}/{segments[0]}";
                return true;
            }
            catch
            {
                return false;
            }
        }

        private readonly SemaphoreSlim _availabilityLock = new(1, 1);
        private DateTime _lastAvailabilityRefresh = DateTime.MinValue;

        private List<string> CampaignChannelLogins(DropsCampaign c) =>
            (c.ConnectUrls ?? (IReadOnlyList<string>)Array.Empty<string>())
                .Where(IsRealChannelUrl)
                .Select(GetStreamerNameFromUrl)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        /// <summary>
        /// Computes, for every active campaign, whether watchable streamers are currently live (so the Inventory
        /// can mark availability). General/category drops are marked <see cref="CampaignAvailability.Category"/>
        /// (earnable on any live channel of the game). Channel-specific drops are checked against their listed
        /// channels: Kick via its channel API (one batched lookup), Twitch via the persisted live-streams query.
        /// Throttled to once every 2 minutes unless forced. Does not affect mining selection.
        /// </summary>
        public async Task RefreshAvailabilityAsync(bool force = false)
        {
            if (!force && (DateTime.Now - _lastAvailabilityRefresh) < TimeSpan.FromMinutes(2))
                return;

            if (!await _availabilityLock.WaitAsync(0))
                return; // a refresh is already running

            try
            {
                List<DropsCampaign> snapshot = await Application.Current.Dispatcher.InvokeAsync(() => ActiveCampaigns.ToList());
                Dictionary<string, (CampaignAvailability State, int Online)> result = new(StringComparer.Ordinal);

                // Kick channel-specific: one batched status lookup for the union of all participating channels.
                Dictionary<string, int> kickStatus = new(StringComparer.OrdinalIgnoreCase);
                List<string> kickSlugs = snapshot
                    .Where(c => c.Platform == Platform.Kick)
                    .SelectMany(CampaignChannelLogins) // empty for true category drops; non-empty even if flagged "general"
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(60)
                    .ToList();

                if (kickSlugs.Count != 0 && KickWebView != null)
                {
                    try
                    {
                        string json = await await Application.Current.Dispatcher.InvokeAsync(
                            async () => await KickWebView!.FetchKickChannelStatusesAsync(kickSlugs, 12000));
                        using JsonDocument doc = JsonDocument.Parse(json);
                        foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                            if (p.Value.ValueKind == JsonValueKind.Number)
                                kickStatus[p.Name] = p.Value.GetInt32();
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("Availability", $"Kick status batch failed: {ex.Message}");
                    }
                }

                void ApplyResults()
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        for (int i = 0; i < ActiveCampaigns.Count; i++)
                        {
                            DropsCampaign c = ActiveCampaigns[i];
                            (CampaignAvailability State, int Online) info;
                            lock (result)
                            {
                                if (!result.TryGetValue(c.Id, out info))
                                    continue;
                            }
                            if (c.Availability != info.State || c.OnlineChannels != info.Online)
                                ActiveCampaigns[i] = c with { Availability = info.State, OnlineChannels = info.Online };
                        }
                    });
                }

                // Phase 1 — instant: general/category drops and Kick channel-specific (from the batched lookup).
                List<DropsCampaign> twitchToCheck = new();         // channel-specific (check listed channels)
                List<DropsCampaign> twitchGeneralToCheck = new();  // general (check the game directory)
                foreach (DropsCampaign c in snapshot)
                {
                    // Check real channels whenever the campaign has them — even when flagged "general", since some
                    // (e.g. Kick "Football Drop") are actually tied to specific channels. Only true category drops
                    // (no real channels) are marked Category.
                    List<string> chans = CampaignChannelLogins(c);

                    if (chans.Count == 0)
                    {
                        lock (result) result[c.Id] = (CampaignAvailability.Category, 0);
                        // For true Twitch general drops, try to upgrade to a live count from the game directory.
                        if (c.Platform == Platform.Twitch && _twitchGqlService != null && !string.IsNullOrWhiteSpace(c.Slug))
                            twitchGeneralToCheck.Add(c);
                    }
                    else if (c.Platform == Platform.Kick)
                    {
                        int online = chans.Count(ch => kickStatus.TryGetValue(ch, out int v) && v >= 0);
                        lock (result) result[c.Id] = (online > 0 ? CampaignAvailability.Available : CampaignAvailability.Unavailable, online);
                    }
                    else if (_twitchGqlService != null && !string.IsNullOrWhiteSpace(c.Slug))
                    {
                        twitchToCheck.Add(c);
                    }
                    else
                    {
                        lock (result) result[c.Id] = (CampaignAvailability.Unknown, 0);
                    }
                }
                ApplyResults();

                // Phase 2 — Twitch channel-specific, checked in parallel (capped) so badges fill in quickly.
                using (SemaphoreSlim gate = new SemaphoreSlim(5))
                {
                    await Task.WhenAll(twitchToCheck.Select(async c =>
                    {
                        await gate.WaitAsync();
                        try
                        {
                            List<(string Login, int Viewers)> live = await _twitchGqlService!
                                .QueryLiveChannelsWithViewersBySlugAsync(CampaignChannelLogins(c).Take(30).ToList(), c.Slug);
                            lock (result) result[c.Id] = (live.Count > 0 ? CampaignAvailability.Available : CampaignAvailability.Unavailable, live.Count);
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Warn("Availability", $"Twitch live check failed for '{c.Name}': {ex.Message}");
                            lock (result) result[c.Id] = (CampaignAvailability.Unknown, 0);
                        }
                        finally { gate.Release(); }
                    }));

                    // Twitch general drops: upgrade "Category drop" to a live count from the game directory when
                    // available. On any failure we leave the Category label (the directory query is best-effort).
                    await Task.WhenAll(twitchGeneralToCheck.Select(async c =>
                    {
                        await gate.WaitAsync();
                        try
                        {
                            List<(string Login, int Viewers)> live = await _twitchGqlService!
                                .QueryLiveDirectoryChannelsAsync(c.Slug, 30);
                            if (live.Count > 0)
                                lock (result) result[c.Id] = (CampaignAvailability.Available, live.Count);
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Warn("Availability", $"Twitch directory check failed for '{c.Name}': {ex.Message}");
                        }
                        finally { gate.Release(); }
                    }));
                }
                ApplyResults();

                _lastAvailabilityRefresh = DateTime.Now;
                AppLogger.Info("Availability", $"Availability refreshed for {result.Count} campaigns.");
            }
            finally
            {
                _availabilityLock.Release();
            }
        }

        /// <summary>
        /// Returns the other active campaigns that are progressing alongside the one currently watched on
        /// the given platform. These earn simultaneously while you watch the current channel.
        /// </summary>
        public IReadOnlyList<CoMiningCampaign> GetCoMiningCampaigns(Platform platform)
        {
            DropsCampaign? watched = platform == Platform.Twitch ? _currentTwitchCampaign : _currentKickCampaign;
            string? login = platform == Platform.Twitch ? _currentTwitchLogin : _currentKickLogin;

            if (watched == null)
                return Array.Empty<CoMiningCampaign>();

            List<CoMiningCampaign> result = new();
            foreach (DropsCampaign c in ActiveCampaigns)
            {
                if (!c.HasProgressToMake() || !CampaignCoProgresses(c, watched, login))
                    continue;

                List<CoMiningDrop> drops = c.Rewards
                    .Select(r => new CoMiningDrop(r.Name, r.ImageUrl, r.ProgressPercent))
                    .ToList();

                result.Add(new CoMiningCampaign(c.Name, c.IsGeneralDrop, drops));
            }

            return result;
        }

        /// <summary>
        /// Applies the per-minute tick to campaigns that co-progress with the watched one (general drops
        /// of the same game + campaigns listing the watched channel), so their UI stays live between
        /// server reconciles and finished drops get claimed promptly. The 20-min server refresh remains
        /// authoritative (merge keeps the higher value).
        /// </summary>
        private void ApplyMinuteProgressToCoProgressingCampaigns(Platform platform, DropsCampaign? watched, int minutesToAdd)
        {
            if (minutesToAdd <= 0 || watched == null)
                return;

            string? login = platform == Platform.Twitch ? _currentTwitchLogin : _currentKickLogin;

            Application.Current.Dispatcher.Invoke(() =>
            {
                for (int i = 0; i < ActiveCampaigns.Count; i++)
                {
                    DropsCampaign c = ActiveCampaigns[i];
                    if (!c.HasProgressToMake() || !CampaignCoProgresses(c, watched, login))
                        continue;

                    bool changed = false;
                    List<DropsReward> updatedRewards = new(c.Rewards.Count);
                    foreach (DropsReward reward in c.Rewards)
                    {
                        int newProgress = reward.IsClaimed || reward.ProgressMinutes >= reward.RequiredMinutes
                            ? reward.ProgressMinutes
                            : Math.Min(reward.ProgressMinutes + minutesToAdd, reward.RequiredMinutes);

                        if (newProgress != reward.ProgressMinutes)
                            changed = true;

                        updatedRewards.Add(reward with { ProgressMinutes = newProgress });
                    }

                    if (changed)
                        ActiveCampaigns[i] = c with { Rewards = updatedRewards };
                }
            });
        }

        /// <summary>
        /// Removes user-skipped logins from a candidate list. If every candidate was skipped, the skip
        /// list is cleared (wrap-around) so selection never gets permanently stuck.
        /// </summary>
        private List<string> FilterSkippedLogins(Platform platform, List<string> logins)
        {
            lock (_lastStreamerSync)
            {
                HashSet<string> skip = platform == Platform.Twitch ? _skipTwitchLogins : _skipKickLogins;
                if (skip.Count == 0 || logins.Count == 0)
                    return logins;

                List<string> filtered = logins.Where(l => !skip.Contains(l)).ToList();
                if (filtered.Count == 0)
                {
                    skip.Clear();
                    return logins;
                }

                return filtered;
            }
        }
        /// <summary>
        /// Calculates the overall progress percentage for a campaign using a hybrid approach:
        /// - Full credit for the required time of all claimed rewards
        /// - Plus progress from current watched time toward the remaining unclaimed rewards
        /// - Divided by the total required time across ALL rewards in the campaign.
        /// This gives a more "completionist" view of how much of the entire event is effectively done.
        /// </summary>
        /// <param name="campaign">The campaign containing the rewards for which progress is being calculated. Cannot be null.</param>
        /// <param name="totalWatchedSeconds">The total number of seconds watched by the user toward earning drops. Must be greater than or
        /// equal to 0.</param>
        /// <returns>A value between 0 and 100 representing the percentage of overall campaign completion.
        /// Returns 100 if all rewards are already claimed or if total required time is zero.</returns>
        private static byte CalculateLiveCampaignProgress(DropsCampaign? campaign)
        {
            if (campaign == null)
                return 0;

            // Total required minutes across ALL rewards (claimed + unclaimed)
            int totalRequiredMinutes = campaign.Rewards.Sum(r => r.RequiredMinutes);

            if (totalRequiredMinutes == 0)
                return 100; // No requirements → done

            
            int effectiveMinutes = campaign.Rewards.Sum(r => Math.Min(r.ProgressMinutes, r.RequiredMinutes));

            double percentage = (double)effectiveMinutes / totalRequiredMinutes * 100;
            return (byte)Math.Clamp((int)Math.Floor(percentage), 0, 100);

        }
        /// <summary>
        /// Calculates the progress percentage toward the next unclaimed live drop reward in the specified campaign.
        /// </summary>
        /// <param name="campaign">The drops campaign containing the list of rewards and their claim status.</param>
        /// <param name="totalWatchedSeconds">The total number of seconds the user has watched, used to determine progress toward the next reward.</param>
        /// <returns>A value between 0 and 100 representing the percentage of progress toward the next unclaimed reward. Returns
        /// 100 if all rewards have been claimed.</returns>
        private static byte CalculateLiveDropProgress(DropsCampaign? campaign, int totalWatchedSeconds)
        {
            if (campaign == null)
                return 0;

            // Find the next unclaimed reward
            List<DropsReward> unclaimedRewards = [.. campaign.Rewards.Where(r => !r.IsClaimed)];
            DropsReward? nextReward = unclaimedRewards
                .Where(r => !r.IsClaimed)
                .OrderBy(r => r.RequiredMinutes)
                .FirstOrDefault();

            if (nextReward == null)
            {
                VerboseLog("RewardProgress", $"campaignId={campaign.Id}, no next unclaimed reward found; returning 0.");
                return 0; // Nothing to claim
            }

            int requiredSeconds = nextReward.RequiredMinutes * 60;

            int effectiveProgressSeconds = Math.Clamp(totalWatchedSeconds, 0, requiredSeconds);
            double percentage = (double)effectiveProgressSeconds / requiredSeconds * 100;
            byte result = (byte)Math.Clamp((int)Math.Floor(percentage), 0, 100);

            VerboseLog(
                "RewardProgress",
                $"campaignId={campaign.Id}, campaignName='{campaign.Name}', rewardsUnclaimed={unclaimedRewards.Count}, nextRewardId={nextReward.Id}, nextRewardName='{nextReward.Name}', requiredSeconds={requiredSeconds}, totalWatchedSeconds={totalWatchedSeconds}, effectiveProgressSeconds={effectiveProgressSeconds}, computedPct={result}");

            return result;
        }
        /// <summary>
        /// Initiates monitoring of active campaign streams to progress eligible rewards on supported platforms.
        /// </summary>
        /// <remarks>This method evaluates all active campaigns and begins watching streams on platforms
        /// such as Twitch and Kick if progress can be made. It periodically re-evaluates which streams to watch based
        /// on reward progress and campaign status. If no campaigns are eligible for progress, stream monitoring is
        /// stopped. The method is safe to call repeatedly; any previous monitoring timers are stopped and disposed
        /// before starting new ones.</remarks>
        /// <returns>A task that represents the asynchronous operation of starting and managing stream monitoring.</returns>
        private int _claimPassRunning; // 0 = idle, 1 = running (guards re-entrancy)

        // Rewards whose claim attempt failed since the last server refresh. Local ticks can over-estimate
        // progress, so a failed (premature) claim is not retried until fresh server data arrives —
        // retrying every 30s would spam notifications and hammer the APIs for nothing.
        private readonly HashSet<string> _failedClaimRewardIds = new(StringComparer.Ordinal);

        // When a platform is idle (no campaign selected) but still has campaigns with progress to make,
        // the health check periodically retries selection. Throttled so failed attempts don't hammer
        // the WebView with navigations every 30 seconds.
        private DateTime _nextTwitchIdleRetryAt = DateTime.MinValue;
        private DateTime _nextKickIdleRetryAt = DateTime.MinValue;

        /// <summary>
        /// Claims every reward that is already complete (across all platforms). Safe to call frequently
        /// (e.g. from the health-check timer) so finished drops are claimed promptly instead of waiting
        /// for the next full refresh. Returns true if any claim attempt failed (caller may retry sooner).
        /// </summary>
        private async Task<bool> AutoClaimReadyRewardsAsync()
        {
            if (Interlocked.CompareExchange(ref _claimPassRunning, 1, 0) != 0)
                return false; // already running

            try
            {
                // Give previously failed claims another chance every ~10 minutes (a claim retry is one tiny request
                // per pending reward), instead of waiting for the hourly campaign refresh. Covers both cases:
                // the server crediting the last minutes, and the user linking their game account in the meantime.
                lock (_failedClaimRewardIds)
                {
                    if (_failedClaimRewardIds.Count != 0 && (DateTime.Now - _lastFailedClaimMarksCleared) > TimeSpan.FromMinutes(10))
                    {
                        _failedClaimRewardIds.Clear();
                        _lastFailedClaimMarksCleared = DateTime.Now;
                        AppLogger.Info("Miner", "Retrying previously failed claims (10-minute retry window).");
                    }
                }

                // ActiveCampaigns is a UI-thread ObservableCollection (mutated by the minute tick and
                // refreshes). This method may run on a timer thread, so snapshot it on the dispatcher —
                // enumerating it directly here crashes with "Collection was modified".
                List<DropsCampaign> campaignsSnapshot = await Application.Current.Dispatcher.InvokeAsync(() => ActiveCampaigns.ToList());

                List<(DropsCampaign campaign, DropsReward reward)> readyToClaimRewards = campaignsSnapshot
                    .SelectMany(c => c.Rewards
                        .Where(r => !r.IsClaimed && r.ProgressMinutes >= r.RequiredMinutes)
                        .Select(r => (c, r)))
                    .Where(p => { lock (_failedClaimRewardIds) { return !_failedClaimRewardIds.Contains(p.c.Id + "|" + p.r.Id); } })
                    .ToList();

                if (readyToClaimRewards.Count == 0)
                    return false;

                if (!UISettingsManager.Instance.AutoClaimRewards)
                {
                    if (UISettingsManager.Instance.NotifyOnReadyToClaim)
                        NotificationManager.ShowNotification("Drop Ready to Claim", $"You have {readyToClaimRewards.Count} drops rewards ready to claim. Please claim them manually.");
                    return false;
                }

                bool anyFailed = false;
                foreach ((DropsCampaign parentCampaign, DropsReward item) in readyToClaimRewards)
                {

                    bool claimResult = false;
                    if (parentCampaign.Platform == Platform.Twitch && _twitchGqlService != null)
                        claimResult = await _twitchGqlService.ClaimDropAsync(parentCampaign.Id, item.Id);
                    else if (parentCampaign.Platform == Platform.Kick)
                        claimResult = await await Application.Current.Dispatcher.InvokeAsync(async () => await KickWebView!.ClaimKickDropAsync(parentCampaign.Id, item.Id));

                    if (claimResult)
                    {
                        if (MarkRewardClaimedInActiveCampaigns(parentCampaign.Id, item.Id))
                            AppLogger.Info("Miner", $"Applied immediate claimed-state update. campaignId={parentCampaign.Id}, rewardId={item.Id}");
                        else
                            AppLogger.Warn("Miner", $"Failed to apply immediate claimed-state update. campaignId={parentCampaign.Id}, rewardId={item.Id}");

                        if (UISettingsManager.Instance.NotifyOnAutoClaimed)
                            NotificationManager.ShowNotification("Drop Claimed", $"Successfully claimed drop reward: {item.Name}");
                    }
                    else
                    {
                        anyFailed = true;
                        // Most likely the local tick over-estimated and the server hasn't credited the
                        // full time yet. Don't retry until the next server refresh corrects the numbers.
                        lock (_failedClaimRewardIds)
                        {
                            _failedClaimRewardIds.Add(parentCampaign.Id + "|" + item.Id);
                        }
                        AppLogger.Warn("Miner", $"Claim failed (likely not yet complete server-side); will retry after next server refresh. campaignId={parentCampaign.Id}, rewardId={item.Id}, name='{item.Name}'");
                    }
                }

                return anyFailed;
            }
            catch (Exception ex)
            {
                // Never let a claim pass take the process down (it runs on a timer thread).
                AppLogger.Error("Miner", "AutoClaimReadyRewardsAsync failed.", ex);
                return true;
            }
            finally
            {
                Interlocked.Exchange(ref _claimPassRunning, 0);
            }
        }

        public async Task StartWatchingStreams(bool restartedInternally = false, Platform? onlyPlatform = null)
        {
            await _startWatchingLock.WaitAsync();
            try
            {
                VerboseLog("StartWatching",
                    $"ENTERING StartWatchingStreams | restarted={restartedInternally} | " +
                    $"paused={_isPaused} | activeCampaigns={ActiveCampaigns.Count} | " +
                    $"twitchCurrent={_currentTwitchCampaign?.Id ?? "null"} | " +
                    $"kickCurrent={_currentKickCampaign?.Id ?? "null"} | " +
                    $"twitchSeconds={_twitchWatchedSeconds} | twitchApplied={_twitchAppliedMinuteBucket}");

                if (_isPaused)
                    return;

                _startWatchingCts?.Cancel();
                _startWatchingCts = new CancellationTokenSource();
                CancellationToken token = _startWatchingCts.Token;

                // Immediately stop the live progress timer to prevent ticks during unstable state
                _liveProgressTimer?.Stop();

                // Reset current selections and progress. When scoped to a single platform (e.g. the user
                // switched a streamer there), leave the OTHER platform's selection/progress untouched.
                bool resetTwitch = onlyPlatform != Platform.Kick;
                bool resetKick = onlyPlatform != Platform.Twitch;

                if (resetTwitch)
                {
                    _currentTwitchLogin = null;
                    TwitchChannelChanged?.Invoke(string.Empty);
                    TwitchCampaignChanged?.Invoke(string.Empty, null);
                    _lastTwitchDropId = null;
                    TwitchDropChanged?.Invoke(string.Empty, null);
                    TwitchProgressChanged?.Invoke(0, 0);
                    _twitchAppliedMinuteBucket = _twitchWatchedSeconds / 60;
                }

                if (resetKick)
                {
                    _currentKickLogin = null;
                    KickChannelChanged?.Invoke(string.Empty);
                    KickCampaignChanged?.Invoke(string.Empty, null);
                    _lastKickDropId = null;
                    KickDropChanged?.Invoke(string.Empty, null);
                    KickProgressChanged?.Invoke(0, 0);
                    _kickAppliedMinuteBucket = _kickWatchedSeconds / 60;
                }

                VerboseLog("StartWatching", $"AFTER reset | twitchApplied={_twitchAppliedMinuteBucket} | kickApplied={_kickAppliedMinuteBucket}");

                AppLogger.Debug("Miner", "[DropsInventoryManager] Starting stream watching process...");
                AppLogger.Info("Miner", $"StartWatchingStreams invoked. restartedInternally={restartedInternally}, activeCampaigns={ActiveCampaigns.Count}, paused={_isPaused}");

                if (!restartedInternally)
                    MinerStatusChanged?.Invoke("Starting");
                else
                    MinerStatusChanged?.Invoke("Evaluating");

                // Stop any existing timer
                _recheckTimer?.Stop();
                _streamHealthTimer?.Stop();
                _recheckTimer?.Dispose();
                _streamHealthTimer?.Dispose();
                _recheckTimer = null;
                _streamHealthTimer = null;

                if (!ActiveCampaigns.Any())
                {
                    AppLogger.Debug("Miner", "[DropsInventoryManager] No active campaigns with progress to make. Stopping stream watching.");
                    AppLogger.Info("Miner", "No active campaigns found during start; switching to Idle.");
                    MinerStatusChanged?.Invoke("Idle");
                    _currentTwitchCampaign = null;
                    _currentKickCampaign = null;
                    UpdateCurrentSelectionFlags();
                    return;
                }

                if (token.IsCancellationRequested)
                    return;

                DateTime nextCheckAt = DateTime.Now.AddHours(1); // Fallback: recheck in 1 hour
                Platform? nextCheckPlatform = null; // which platform's reward completion drives nextCheckAt (null = both)

                // Claim any rewards that are already complete (also runs on the 30s health check for
                // speed). A failed claim does NOT schedule a 1-minute full restart anymore — that used to
                // reset both platforms every minute whenever the local tick over-estimated progress.
                await AutoClaimReadyRewardsAsync();

                List<DropsCampaign> snapshot = [.. ActiveCampaigns];
                if (!snapshot.Any(c => c.HasProgressToMake()))
                {
                    AppLogger.Debug("Miner", "[DropsInventoryManager] No campaigns with progress to make after claim. Stopping stream watching.");
                    AppLogger.Info("Miner", "No campaigns with progress after claim pass; switching to Idle.");
                    MinerStatusChanged?.Invoke("Idle");
                    _currentTwitchCampaign = null;
                    _currentKickCampaign = null;
                    UpdateCurrentSelectionFlags();
                    return;
                }

                if (token.IsCancellationRequested)
                    return;

                // Auto-unpin a finished pin: when the pinned campaign is fully claimed, has ended, or is gone from
                // the list, the pin has nothing left to do — clear it so selection returns to automatic priorities.
                // A stale pin is not just cosmetic: it also disables the keep-current/sticky paths ("pinned to a
                // different campaign"), causing needless stream re-navigation on every re-evaluation.
                if (!string.IsNullOrWhiteSpace(_forcedCampaignId)
                    && !snapshot.Any(c => c.Id == _forcedCampaignId && c.HasProgressToMake() && c.IsWithinActiveWindow()))
                {
                    AppLogger.Info("Selection", $"Pinned campaign {_forcedCampaignId} is completed/ended — auto-unpinning, returning to automatic selection.");
                    _forcedCampaignId = null;
                    _pinSuspendedNoStreamers = false;
                    SaveForcedCampaignId(null);
                    UpdateCurrentSelectionFlags();
                }

                // If the cached list contains campaigns that have already ended, it's stale (the app likely ran across
                // a PC sleep / fetch outage without a refresh). Ask the Dashboard to reload it — throttled so this
                // can't spin. Selection below still proceeds using only the campaigns that are genuinely active now.
                if (snapshot.Any(c => c.EndsAt <= DateTimeOffset.Now) && (DateTime.Now - _lastStaleReloadRequest) > TimeSpan.FromMinutes(5))
                {
                    _lastStaleReloadRequest = DateTime.Now;
                    AppLogger.Info("Miner", "Cached campaign list contains ended campaigns — requesting a reload.");
                    ReloadCampaignsRequested?.Invoke();
                }

                // Group campaigns by platform
                List<DropsCampaign> twitchCampaigns = snapshot.Where(c => c.Platform == Platform.Twitch && c.HasProgressToMake() && c.IsWithinActiveWindow()).ToList();
                List<DropsCampaign> kickCampaigns = snapshot.Where(c => c.Platform == Platform.Kick && c.HasProgressToMake() && c.IsWithinActiveWindow()).ToList();

                // ----------------------------------------------------------------
                // Twitch handling
                // ----------------------------------------------------------------
                if (twitchCampaigns.Count != 0 && TwitchWebView != null && onlyPlatform != Platform.Kick)
                {
                    if (token.IsCancellationRequested)
                        return;

                    // Keep the current Twitch stream through a routine refresh instead of re-navigating it (which
                    // briefly resets the card). Progress stays accurate via the 3-min server reconcile. Only kept
                    // when the same campaign is still eligible and the user hasn't just forced a specific channel.
                    bool keepTwitch = false;
                    {
                        bool forcedPending;
                        lock (_lastStreamerSync) { forcedPending = _forcedTwitchStreamer != null; }
                        // Don't keep the current campaign if the user pinned a DIFFERENT one via "Mine this".
                        // A SUSPENDED pin (streamers offline, mining a fallback) does not count — the fallback
                        // stream should be held steady until the health check brings the pin back.
                        bool forcedToOther = !string.IsNullOrWhiteSpace(_forcedCampaignId)
                            && !_pinSuspendedNoStreamers
                            && !string.Equals(_forcedCampaignId, _currentTwitchCampaign?.Id, StringComparison.Ordinal);

                        // Don't keep a campaign that was flagged as not actually crediting (server frozen while
                        // watched) — otherwise keep-current would silently re-pin it and the auto stall-skip never
                        // takes effect (the miner would sit on e.g. a broken-Ubisoft-link R6S campaign forever).
                        bool currentNotCrediting = IsNotCrediting(_currentTwitchCampaign?.Id)
                            || IsChannelStalled(Platform.Twitch, _currentTwitchLogin);

                        DropsCampaign? fresh = _currentTwitchCampaign == null ? null : twitchCampaigns.FirstOrDefault(c => c.Id == _currentTwitchCampaign.Id);
                        if (!forcedPending && !forcedToOther && !currentNotCrediting && fresh != null && !string.IsNullOrWhiteSpace(_currentTwitchLogin)
                            && fresh.HasProgressToMake()
                            && await IsTwitchStreamEligibleViaGqlAsync(_currentTwitchLogin, fresh.Slug) == true)
                        {
                            _currentTwitchCampaign = fresh;
                            _lastKnownTwitchOnlineState = true;
                            _twitchCurrentlyOnline = true;
                            _cachedTwitchEligible = true; // just confirmed eligible via GQL — seed the throttle cache
                            _lastTwitchEligibilityCheck = DateTime.Now;
                            UpdateCurrentSelectionFlags();

                            _twitchWatchedSeconds = fresh.Rewards.Sum(r => Math.Min(r.ProgressMinutes, r.RequiredMinutes) * 60);
                            DropsReward? nextR = fresh.Rewards.Where(r => !r.IsClaimed).OrderBy(r => r.RequiredMinutes).FirstOrDefault();
                            int beforeNext = fresh.Rewards.Where(r => !r.IsClaimed && r.RequiredMinutes < (nextR?.RequiredMinutes ?? 0)).Sum(r => r.RequiredMinutes);
                            _twitchDropWatchedSeconds = Math.Max(0, (nextR?.ProgressMinutes ?? 0) - beforeNext) * 60;
                            _twitchAppliedMinuteBucket = _twitchWatchedSeconds / 60;

                            DropsReward? soonest = fresh.Rewards.Where(r => !r.IsClaimed && r.ProgressMinutes < r.RequiredMinutes).OrderBy(r => r.RequiredMinutes - r.ProgressMinutes).FirstOrDefault();
                            if (soonest != null)
                            {
                                DateTime est = DateTime.Now.AddMinutes(soonest.RequiredMinutes - soonest.ProgressMinutes);
                                if (est < nextCheckAt) { nextCheckAt = est; nextCheckPlatform = Platform.Twitch; }
                            }

                            TwitchProgressChanged?.Invoke(CalculateLiveCampaignProgress(fresh), CalculateLiveDropProgress(fresh, _twitchDropWatchedSeconds));
                            RaiseTwitchDropChangedIfNeeded(nextR);
                            AppLogger.Info("TwitchSelection", $"Kept current Twitch stream '{_currentTwitchLogin}' through refresh (no re-navigation).");
                            keepTwitch = true;
                        }
                    }

                    // Pinned campaign handling: attempt 0 tries ONLY the pinned campaign. If none of its streamers
                    // is live, the pin is temporarily SUSPENDED — attempt 1 falls back to the best other campaign so
                    // no time is wasted idling — and the health check returns to the pin as soon as a pinned channel
                    // goes live again.
                    bool twitchPinActive = !keepTwitch && !string.IsNullOrWhiteSpace(_forcedCampaignId) && twitchCampaigns.Any(c => c.Id == _forcedCampaignId);
                    for (int selectionAttempt = 0; selectionAttempt < 2; selectionAttempt++)
                    {
                    List<DropsCampaign> remainingTwitchCampaigns;
                    if (keepTwitch)
                        remainingTwitchCampaigns = new List<DropsCampaign>();
                    else if (twitchPinActive && !_pinSuspendedNoStreamers)
                        remainingTwitchCampaigns = twitchCampaigns.Where(c => c.Id == _forcedCampaignId).ToList();
                    else if (twitchPinActive)
                        remainingTwitchCampaigns = twitchCampaigns.Where(c => c.Id != _forcedCampaignId).ToList();
                    else
                        remainingTwitchCampaigns = [.. twitchCampaigns];

                    while (remainingTwitchCampaigns.Count != 0)
                    {
                        // If the user just picked a specific channel, select that channel's campaign first so the
                        // pick isn't lost to a higher-priority campaign that happens to rank above it.
                        string? forcedTwitchCampId;
                        lock (_lastStreamerSync) { forcedTwitchCampId = _forcedTwitchStreamer?.CampaignId; }

                        // Sticky selection: a user-forced channel wins; otherwise prefer the campaign we're already
                        // watching (if it's still a candidate) so we don't needlessly jump to a different campaign
                        // that merely ranks higher. Only fall back to priority ranking when neither applies.
                        // A user-forced channel wins; a "Mine this" pinned campaign (handled inside SelectBestCampaign)
                        // wins next; otherwise stay sticky on the campaign we're already watching; else pick by priority.
                        bool campaignPinned = !string.IsNullOrWhiteSpace(_forcedCampaignId) && !_pinSuspendedNoStreamers;
                        DropsCampaign? bestTwitch =
                            forcedTwitchCampId != null
                                ? remainingTwitchCampaigns.FirstOrDefault(c => c.Id == forcedTwitchCampId) ?? await SelectBestCampaign(remainingTwitchCampaigns)
                            : (!campaignPinned && _currentTwitchCampaign != null && !IsNotCrediting(_currentTwitchCampaign.Id) && remainingTwitchCampaigns.FirstOrDefault(c => c.Id == _currentTwitchCampaign.Id) is { } stickyTwitch)
                                ? stickyTwitch
                                : await SelectBestCampaign(remainingTwitchCampaigns);
                        if (bestTwitch == null)
                            break;

                        if (token.IsCancellationRequested)
                            return;

                        string twitchUrl = await SelectTwitchStreamerForCampaign(bestTwitch);
                        if (token.IsCancellationRequested)
                            return;

                        if (string.IsNullOrWhiteSpace(twitchUrl))
                        {
                            AppLogger.Warn("TwitchSelection", $"Twitch campaign '{bestTwitch.Name}' produced empty streamer URL; trying next candidate.");
                            remainingTwitchCampaigns.Remove(bestTwitch);
                            continue;
                        }

                        await await Application.Current.Dispatcher.InvokeAsync(async () => await TwitchWebView!.NavigateAsync(twitchUrl));
                        await Task.Delay(1500);
                        await DismissTwitchMatureContentGateAsync();
                        await SetTwitchStreamToLowestQualityAsync();
                        await await Application.Current.Dispatcher.InvokeAsync(async () => await TwitchWebView!.ForceRefreshAsync());
                        await Task.Delay(5000);

                        _currentTwitchCampaign = bestTwitch;

                        // Prefer the authoritative GQL check (live + correct category) over fragile DOM
                        // scraping; only fall back to DOM when GQL cannot determine the result.
                        string twitchLogin = GetStreamerNameFromUrl(twitchUrl);
                        bool? gqlEligible = await IsTwitchStreamEligibleViaGqlAsync(twitchLogin, bestTwitch.Slug);

                        bool twitchOnline;
                        bool twitchCorrectCategory;
                        if (gqlEligible.HasValue)
                        {
                            twitchOnline = gqlEligible.Value;
                            twitchCorrectCategory = gqlEligible.Value;
                        }
                        else
                        {
                            twitchOnline = await IsTwitchStreamOnline();
                            twitchCorrectCategory = await IsTwitchStreamCategoryCorrect();
                        }

                        // An explicit user pick is trusted on category (fragile DOM/GQL category check), but must
                        // still be actually online to be worth watching.
                        if (!twitchOnline || (!twitchCorrectCategory && !_twitchSelectionForced))
                        {
                            AppLogger.Warn("TwitchSelection", $"Twitch campaign '{bestTwitch.Name}' failed streamer eligibility. online={twitchOnline}, categoryOk={twitchCorrectCategory}, forced={_twitchSelectionForced}");
                            _currentTwitchCampaign = null;
                            _currentTwitchLogin = null;
                            UpdateCurrentSelectionFlags();
                            remainingTwitchCampaigns.Remove(bestTwitch);
                            continue;
                        }

                        _currentTwitchLogin = twitchLogin;
                        _lastKnownTwitchOnlineState = true;
                        _twitchCurrentlyOnline = true;
                        _cachedTwitchEligible = true; // just confirmed eligible via GQL — seed the throttle cache
                        _lastTwitchEligibilityCheck = DateTime.Now;
                        UpdateCurrentSelectionFlags();

                        // Sync baseline NOW - right after selection, before any further logic
                        _twitchWatchedSeconds = bestTwitch.Rewards
                            .Sum(r => Math.Min(r.ProgressMinutes, r.RequiredMinutes) * 60);

                        DropsReward? nextTwitchReward = bestTwitch.Rewards
                            .Where(r => !r.IsClaimed)
                            .OrderBy(r => r.RequiredMinutes)
                            .FirstOrDefault();

                        int twitchMinutesBeforeNextReward = bestTwitch.Rewards
                            .Where(r => !r.IsClaimed && r.RequiredMinutes < nextTwitchReward!.RequiredMinutes)
                            .Sum(r => r.RequiredMinutes);
                        _twitchDropWatchedSeconds = Math.Max(0, (nextTwitchReward?.ProgressMinutes ?? 0) - twitchMinutesBeforeNextReward) * 60;

                        _twitchAppliedMinuteBucket = _twitchWatchedSeconds / 60;

                        VerboseLog("SelectionBaseline",
                            $"Twitch baseline SET | " +
                            $"campaignId={bestTwitch.Id} | " +
                            $"watchedSeconds={_twitchWatchedSeconds} | " +
                            $"dropWatchedSeconds={_twitchDropWatchedSeconds} | " +
                            $"appliedBucket={_twitchAppliedMinuteBucket}");

                        VerboseLog("SelectionBaseline", $"Twitch campaignId={bestTwitch.Id}, campaignWatchedSecondsBaseline={_twitchWatchedSeconds}, dropWatchedSecondsBaseline={_twitchDropWatchedSeconds}, nextRewardId={nextTwitchReward?.Id ?? "none"}, unclaimedRewards={bestTwitch.Rewards.Count(r => !r.IsClaimed)}");

                        byte initialTwitchPct = CalculateLiveCampaignProgress(bestTwitch);
                        byte initialTwitchDropPct = CalculateLiveDropProgress(bestTwitch, _twitchDropWatchedSeconds);
                        TwitchProgressChanged?.Invoke(initialTwitchPct, initialTwitchDropPct);
                        RaiseTwitchDropChangedIfNeeded(nextTwitchReward);

                        AppLogger.Debug("TwitchSelection", $"[DropsInventoryManager] Watching Twitch stream: {twitchUrl}");
                        AppLogger.Info("TwitchSelection", $"Selected Twitch stream '{twitchUrl}' for campaign '{bestTwitch.Name}' ({bestTwitch.Id}).");
                        RememberLastStreamerUrl(Platform.Twitch, bestTwitch.Id, twitchUrl);

                        DropsReward? soonestTwitch = bestTwitch.Rewards
                            .Where(r => !r.IsClaimed && r.ProgressMinutes < r.RequiredMinutes)
                            .OrderBy(r => r.RequiredMinutes - r.ProgressMinutes)
                            .FirstOrDefault();

                        if (soonestTwitch != null)
                        {
                            DateTime est = DateTime.Now.AddMinutes(soonestTwitch.RequiredMinutes - soonestTwitch.ProgressMinutes);
                            if (est < nextCheckAt)
                            {
                                nextCheckAt = est;
                                nextCheckPlatform = Platform.Twitch;
                            }
                        }

                        break;
                    }

                    // Selected, or nothing to fall back to — leave the attempt loop.
                    if (_currentTwitchCampaign != null || !twitchPinActive || _pinSuspendedNoStreamers)
                        break;

                    // The pinned campaign produced no watchable stream (its streamers are offline) — suspend the pin
                    // and retry with the other campaigns; the health check re-pins when someone goes live.
                    _pinSuspendedNoStreamers = true;
                    _pinSuspendedAt = DateTime.Now;
                    AppLogger.Info("Selection", $"Pinned campaign {_forcedCampaignId} has no live streamers — temporarily mining other campaigns until one returns.");
                    }

                    if (_currentTwitchCampaign == null)
                    {
                        AppLogger.Warn("TwitchSelection", $"No Twitch campaign passed eligibility checks. candidates={twitchCampaigns.Count}");
                    }
                }

                // ----------------------------------------------------------------
                // Kick handling
                // ----------------------------------------------------------------
                if (kickCampaigns.Count != 0 && KickWebView != null && onlyPlatform != Platform.Twitch)
                {
                    if (token.IsCancellationRequested)
                        return;

                    // Pinned campaign handling mirrors Twitch: attempt 0 tries ONLY the pinned campaign; if none of
                    // its channels is live, the pin is temporarily suspended and attempt 1 mines the best other
                    // campaign. The health check returns to the pin once a pinned channel goes live again.
                    bool kickPinActive = !string.IsNullOrWhiteSpace(_forcedCampaignId) && kickCampaigns.Any(c => c.Id == _forcedCampaignId);

                    string? forcedKickPreId;
                    lock (_lastStreamerSync) { forcedKickPreId = _forcedKickStreamer?.CampaignId; }

                    // ONE batched status call for the listed channels of ALL candidate campaigns, reused by both the
                    // pin attempt and a possible fallback attempt. Campaigns without listed channels (true category
                    // drops) are kept for the directory fallback. On lookup failure everything is kept (old behaviour).
                    List<string> preflightSlugs = kickCampaigns
                        .SelectMany(CampaignChannelLogins)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(60)
                        .ToList();

                    Dictionary<string, int>? preStatus = null;
                    if (preflightSlugs.Count != 0)
                    {
                        try
                        {
                            string statusJson = await await Application.Current.Dispatcher.InvokeAsync(
                                async () => await KickWebView!.FetchKickChannelStatusesAsync(preflightSlugs, 12000));
                            Dictionary<string, int> statuses = new(StringComparer.OrdinalIgnoreCase);
                            using JsonDocument doc = JsonDocument.Parse(statusJson);
                            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                                if (p.Value.ValueKind == JsonValueKind.Number)
                                    statuses[p.Name] = p.Value.GetInt32();
                            preStatus = statuses;
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Warn("KickSelection", $"Kick pre-filter status lookup failed: {ex.Message}");
                        }
                    }

                    // Drop the previous Kick selection before re-picking: Kick has no keep-current fast-path, so if
                    // every candidate was pre-filtered out (all listed channels offline) the loop below won't run and
                    // we must NOT keep silently "watching" the now-offline channel (its bar would keep climbing while
                    // the server credits nothing). Clearing here means an all-offline situation ends as Idle.
                    _currentKickCampaign = null;
                    _currentKickLogin = null;
                    _kickCurrentlyOnline = false;

                    for (int kickAttempt = 0; kickAttempt < 2; kickAttempt++)
                    {
                    List<DropsCampaign> remainingKickCampaigns;
                    if (kickPinActive && !_pinSuspendedNoStreamers)
                        remainingKickCampaigns = kickCampaigns.Where(c => c.Id == _forcedCampaignId).ToList();
                    else if (kickPinActive)
                        remainingKickCampaigns = kickCampaigns.Where(c => c.Id != _forcedCampaignId).ToList();
                    else
                        remainingKickCampaigns = [.. kickCampaigns];

                    if (preStatus != null)
                    {
                        int before = remainingKickCampaigns.Count;
                        remainingKickCampaigns = remainingKickCampaigns.Where(c =>
                        {
                            if (c.Id == forcedKickPreId) return true;
                            List<string> chans = CampaignChannelLogins(c);
                            if (chans.Count == 0) return true; // category drop -> handled via directory
                            return chans.Any(ch => preStatus.TryGetValue(ch, out int v) && v >= 0);
                        }).ToList();

                        AppLogger.Info("KickSelection", $"Pre-filtered Kick campaigns by live channel: {before} -> {remainingKickCampaigns.Count}.");
                    }

                    while (remainingKickCampaigns.Count != 0)
                    {
                        // If the user just picked a specific channel, select that channel's campaign first so the
                        // pick isn't lost to a higher-priority campaign that happens to rank above it.
                        string? forcedKickCampId;
                        lock (_lastStreamerSync) { forcedKickCampId = _forcedKickStreamer?.CampaignId; }

                        DropsCampaign? bestKick = forcedKickCampId != null
                            ? remainingKickCampaigns.FirstOrDefault(c => c.Id == forcedKickCampId) ?? await SelectBestCampaign(remainingKickCampaigns)
                            : await SelectBestCampaign(remainingKickCampaigns);
                        if (bestKick == null)
                            break;

                        if (token.IsCancellationRequested)
                            return;

                        string kickUrl = await SelectKickStreamerForCampaign(bestKick);
                        if (token.IsCancellationRequested)
                            return;

                        if (string.IsNullOrWhiteSpace(kickUrl))
                        {
                            AppLogger.Warn("KickSelection", $"Kick campaign '{bestKick.Name}' produced empty streamer URL; trying next candidate.");
                            remainingKickCampaigns.Remove(bestKick);
                            continue;
                        }

                        await await Application.Current.Dispatcher.InvokeAsync(async () => await KickWebView!.NavigateAsync(kickUrl));
                        await Task.Delay(1500);
                        await DismissKickMatureContentGateAsync();
                        await SetKickStreamToLowestQualityAsync();
                        await await Application.Current.Dispatcher.InvokeAsync(async () => await KickWebView!.ForceRefreshAsync());
                        await Task.Delay(5000);

                        _currentKickCampaign = bestKick;
                        bool kickOnline = await IsKickStreamOnline(GetStreamerNameFromUrl(kickUrl));
                        // Channel-bound campaigns (specific participating channels, incl. "Football Drop") earn on
                        // that channel regardless of what it's currently streaming, so category is not required.
                        bool kickChannelBound = CampaignChannelLogins(bestKick).Count > 0;
                        bool kickCorrectCategory = kickChannelBound || await IsKickStreamCategoryCorrect();

                        // An explicit user pick (or a channel-bound campaign) is trusted on category, but must still
                        // be actually online to be worth watching.
                        if (!kickOnline || (!kickCorrectCategory && !_kickSelectionForced))
                        {
                            AppLogger.Warn("KickSelection", $"Kick campaign '{bestKick.Name}' failed streamer eligibility. online={kickOnline}, categoryOk={kickCorrectCategory}, channelBound={kickChannelBound}, forced={_kickSelectionForced}");
                            _currentKickCampaign = null;
                            _currentKickLogin = null;
                            UpdateCurrentSelectionFlags();
                            remainingKickCampaigns.Remove(bestKick);
                            continue;
                        }

                        _currentKickLogin = GetStreamerNameFromUrl(kickUrl);
                        _lastKnownKickOnlineState = true;
                        _kickCurrentlyOnline = true;
                        UpdateCurrentSelectionFlags();

                        _kickWatchedSeconds = bestKick.Rewards
                            .Sum(r => Math.Min(r.ProgressMinutes, r.RequiredMinutes) * 60);

                        DropsReward? nextKickReward = bestKick.Rewards
                            .Where(r => !r.IsClaimed)
                            .OrderBy(r => r.RequiredMinutes)
                            .FirstOrDefault();

                        int kickMinutesBeforeNextReward = bestKick.Rewards
                            .Where(r => !r.IsClaimed && r.RequiredMinutes < nextKickReward!.RequiredMinutes)
                            .Sum(r => r.RequiredMinutes);
                        _kickDropWatchedSeconds = Math.Max(0, (nextKickReward?.ProgressMinutes ?? 0) - kickMinutesBeforeNextReward) * 60;

                        _kickAppliedMinuteBucket = _kickWatchedSeconds / 60;

                        VerboseLog("SelectionBaseline", $"Kick campaignId={bestKick.Id}, campaignWatchedSecondsBaseline={_kickWatchedSeconds}, dropWatchedSecondsBaseline={_kickDropWatchedSeconds}, nextRewardId={nextKickReward?.Id ?? "none"}, unclaimedRewards={bestKick.Rewards.Count(r => !r.IsClaimed)}");

                        byte initialKickPct = CalculateLiveCampaignProgress(bestKick);
                        byte initialKickDropPct = CalculateLiveDropProgress(bestKick, _kickDropWatchedSeconds);
                        KickProgressChanged?.Invoke(initialKickPct, initialKickDropPct);
                        RaiseKickDropChangedIfNeeded(nextKickReward);

                        AppLogger.Debug("KickSelection", $"[DropsInventoryManager] Watching Kick stream: {kickUrl}");
                        AppLogger.Info("KickSelection", $"Selected Kick stream '{kickUrl}' for campaign '{bestKick.Name}' ({bestKick.Id}).");
                        RememberLastStreamerUrl(Platform.Kick, bestKick.Id, kickUrl);

                        DropsReward? soonestKick = bestKick.Rewards
                            .Where(r => !r.IsClaimed && r.ProgressMinutes < r.RequiredMinutes)
                            .OrderBy(r => (r.RequiredMinutes - r.ProgressMinutes))
                            .FirstOrDefault();

                        if (soonestKick != null)
                        {
                            DateTime est = DateTime.Now.AddMinutes(soonestKick.RequiredMinutes - soonestKick.ProgressMinutes);
                            if (est < nextCheckAt)
                            {
                                nextCheckAt = est;
                                nextCheckPlatform = Platform.Kick;
                            }
                        }

                        break;
                    }

                    // Selected, or nothing to fall back to — leave the attempt loop.
                    if (_currentKickCampaign != null || !kickPinActive || _pinSuspendedNoStreamers)
                        break;

                    // The pinned campaign has no live channel — suspend the pin and retry with the other campaigns;
                    // the health check re-pins when someone goes live.
                    _pinSuspendedNoStreamers = true;
                    _pinSuspendedAt = DateTime.Now;
                    AppLogger.Info("Selection", $"Pinned campaign {_forcedCampaignId} has no live streamers — temporarily mining other campaigns until one returns.");
                    }

                    if (_currentKickCampaign == null)
                    {
                        AppLogger.Warn("KickSelection", $"No Kick campaign passed eligibility checks. candidates={kickCampaigns.Count}");
                        // Surface the idle state on the Kick card so it doesn't keep showing a stale campaign whose
                        // bar climbs while nothing is actually being earned. Makes "no live channels — waiting" visible.
                        ClearKickCard();
                    }
                }

                if (_currentTwitchCampaign == null && _currentKickCampaign == null)
                {
                    AppLogger.Warn("Miner", "No stream selected after evaluation cycle; status may oscillate with health checks.");
                }

                // Start periodic health check
                StartStreamHealthMonitoring();

                // ONLY NOW restart the live progress timer - state is consistent
                _liveProgressTimer?.Start();

                // Set timer to re-evaluate when the next reward is expected to complete (or fallback)
                double delayMs = Math.Max((nextCheckAt - DateTime.Now).TotalMilliseconds, 60000);
                _recheckTimer = new System.Timers.Timer(delayMs);
                Platform? recheckScope = nextCheckPlatform;
                _recheckTimer.Elapsed += async (s, e) =>
                {
                    _recheckTimer?.Stop();
                    AppLogger.Debug("Miner", "[DropsInventoryManager] Re-evaluating streams for active campaigns.");
                    AppLogger.Info("Miner", $"Scheduled re-evaluation triggered. scope={recheckScope?.ToString() ?? "both"}");
                    // Re-evaluate only the platform whose reward completion scheduled this check, so the
                    // other platform keeps mining undisturbed (e.g. a Kick drop finishing no longer resets Twitch).
                    await StartWatchingStreams(true, recheckScope);
                };
                _recheckTimer.AutoReset = false;
                _recheckTimer.Start();

                AppLogger.Debug("Miner", $"[DropsInventoryManager] Next stream re-evaluation in ~{delayMs / 60000:F1} minutes at {nextCheckAt:u}");
                AppLogger.Info("Miner", $"Next re-evaluation in {delayMs / 1000:F0}s at {nextCheckAt:u}. twitchSelected={_currentTwitchCampaign != null}, kickSelected={_currentKickCampaign != null}");

                MinerStatusChanged?.Invoke(_currentTwitchCampaign != null || _currentKickCampaign != null ? "Mining" : "Idle");
            }
            catch (Exception ex)
            {
                // A transient failure mid-selection (network drop, PC wake before DNS is ready, etc.) used to leave
                // the watcher dead: the timers were stopped at the start of this method and never restarted, so
                // mining froze until a manual restart. Recover automatically — resume the live tick, restart health
                // monitoring, and retry selection shortly so the app heals itself once the network is back.
                AppLogger.Error("Miner", "StartWatchingStreams failed (likely transient network issue); scheduling recovery retry.", ex);
                try
                {
                    _liveProgressTimer?.Start();
                    StartStreamHealthMonitoring();

                    _recheckTimer?.Stop();
                    _recheckTimer?.Dispose();
                    _recheckTimer = new System.Timers.Timer(60_000);
                    _recheckTimer.AutoReset = false;
                    _recheckTimer.Elapsed += async (s, e) =>
                    {
                        _recheckTimer?.Stop();
                        AppLogger.Info("Miner", "Recovery re-evaluation after earlier failure.");
                        await StartWatchingStreams(true);
                    };
                    _recheckTimer.Start();
                }
                catch (Exception inner)
                {
                    AppLogger.Warn("Miner", $"Failed to schedule recovery retry: {inner.Message}");
                }
            }
            finally
            {
                _startWatchingLock.Release();
            }
        }
        /// <summary>
        /// Marks the specified reward as claimed in the active campaign with the given campaign identifier.
        /// </summary>
        /// <remarks>If the specified campaign or reward is not found in the active campaigns, no changes
        /// are made and the method returns false. The method updates the claimed status and progress of the reward, and
        /// synchronizes related campaign selections.</remarks>
        /// <param name="campaignId">The identifier of the campaign in which to mark the reward as claimed. Cannot be null or empty.</param>
        /// <param name="rewardId">The identifier of the reward to mark as claimed. Cannot be null or empty.</param>
        /// <returns>true if the reward was successfully marked as claimed; otherwise, false.</returns>
        private bool MarkRewardClaimedInActiveCampaigns(string campaignId, string rewardId)
        {
            bool updated = false;

            Application.Current.Dispatcher.Invoke(() =>
            {
                DropsCampaign? existingCampaign = ActiveCampaigns.FirstOrDefault(c => c.Id == campaignId);
                if (existingCampaign == null)
                    return;

                int campaignIndex = ActiveCampaigns.IndexOf(existingCampaign);
                if (campaignIndex < 0)
                    return;

                bool rewardFound = false;
                List<DropsReward> updatedRewards = new List<DropsReward>(existingCampaign.Rewards.Count);
                foreach (DropsReward reward in existingCampaign.Rewards)
                {
                    if (reward.Id == rewardId)
                    {
                        rewardFound = true;
                        updatedRewards.Add(reward with
                        {
                            IsClaimed = true,
                            ProgressMinutes = Math.Max(reward.ProgressMinutes, reward.RequiredMinutes)
                        });
                    }
                    else
                    {
                        updatedRewards.Add(reward);
                    }
                }

                if (!rewardFound)
                    return;

                DropsCampaign updatedCampaign = existingCampaign with { Rewards = updatedRewards };
                ActiveCampaigns[campaignIndex] = updatedCampaign;

                if (_currentTwitchCampaign?.Id == campaignId)
                    _currentTwitchCampaign = updatedCampaign;

                if (_currentKickCampaign?.Id == campaignId)
                    _currentKickCampaign = updatedCampaign;

                UpdateCurrentSelectionFlags();
                updated = true;
            });

            return updated;
        }
        /// <summary>
        /// Updates the selection flags for active campaigns and their rewards to reflect the current campaign and
        /// reward based on the active platform and progress.
        /// </summary>
        /// <remarks>This method must be called on the UI thread, as it updates observable collections
        /// bound to the user interface. It ensures that only one campaign and one reward per platform are marked as
        /// current at any time. If there are no active campaigns, the method exits without making changes.</remarks>
        private void UpdateCurrentSelectionFlags()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (ActiveCampaigns.Count == 0)
                    return;

                List<DropsCampaign> updatedCampaigns = new List<DropsCampaign>(ActiveCampaigns.Count);

                foreach (DropsCampaign campaign in ActiveCampaigns)
                {
                    bool isCurrentCampaign = (campaign.Platform == Platform.Twitch && campaign.Id == _currentTwitchCampaign?.Id) ||
                                             (campaign.Platform == Platform.Kick && campaign.Id == _currentKickCampaign?.Id);

                    DropsReward? currentReward = null;
                    if (isCurrentCampaign)
                    {
                        currentReward = campaign.Rewards
                            .Where(r => !r.IsClaimed)
                            .OrderBy(r => Math.Max(0, r.RequiredMinutes - r.ProgressMinutes))
                            .FirstOrDefault();
                    }

                    List<DropsReward> updatedRewards = new List<DropsReward>(campaign.Rewards.Count);
                    foreach (DropsReward reward in campaign.Rewards)
                    {
                        bool isCurrentReward = isCurrentCampaign && currentReward != null && reward.Id == currentReward.Id;
                        updatedRewards.Add(reward with { IsCurrentReward = isCurrentReward });
                    }

                    updatedCampaigns.Add(campaign with
                    {
                        IsCurrentCampaign = isCurrentCampaign,
                        IsPinned = !string.IsNullOrWhiteSpace(_forcedCampaignId) && campaign.Id == _forcedCampaignId,
                        Rewards = updatedRewards
                    });
                }

                ActiveCampaigns.Clear();
                foreach (DropsCampaign? c in updatedCampaigns.OrderBy(x => x.Platform).ThenBy(x => x.GameName))
                {
                    ActiveCampaigns.Add(c);
                }
            });
        }
        /// <summary>
        /// Begins periodic monitoring of the health status of the Twitch and Kick streams, triggering a re-evaluation
        /// if either stream is detected as unhealthy.
        /// </summary>
        /// <remarks>This method sets up a timer to check the online status of both streams every 30
        /// seconds. If either stream is offline, in the wrong category, or Twitch is showing an ad, monitoring is
        /// temporarily stopped and an immediate re-selection of streams is initiated. This helps ensure that the
        /// application responds promptly to changes in stream availability.</remarks>
        private void StartStreamHealthMonitoring()
        {
            _streamHealthTimer = new System.Timers.Timer(30 * 1000); // Every 30 seconds
            _streamHealthTimer.Elapsed += async (s, e) =>
            {
                try
                {
                // Claim any drops that finished since the last check (fast claim, ~30s instead of waiting
                // for the next full refresh).
                await AutoClaimReadyRewardsAsync();

                // Run the entire check on the UI thread
                await await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    bool twitchOnline;
                    bool twitchCorrectCategory;
                    if (_currentTwitchCampaign == null)
                    {
                        twitchOnline = false;
                        twitchCorrectCategory = false;
                    }
                    else if ((DateTime.Now - _lastTwitchEligibilityCheck) < TimeSpan.FromMinutes(2))
                    {
                        // Re-use the recent verdict instead of hitting Twitch GQL again — gentler on their
                        // integrity/rate limits. A stream's live/category status rarely changes within 2 minutes.
                        twitchOnline = _cachedTwitchEligible;
                        twitchCorrectCategory = _cachedTwitchEligible;
                    }
                    else
                    {
                        // Prefer authoritative GQL (live + category); fall back to DOM if unavailable.
                        bool? gqlEligible = await IsTwitchStreamEligibleViaGqlAsync(_currentTwitchLogin, _currentTwitchCampaign.Slug);
                        if (gqlEligible.HasValue)
                        {
                            twitchOnline = gqlEligible.Value;
                            twitchCorrectCategory = gqlEligible.Value;
                        }
                        else
                        {
                            twitchOnline = await IsTwitchStreamOnline();
                            twitchCorrectCategory = await IsTwitchStreamCategoryCorrect();
                        }
                        _cachedTwitchEligible = twitchOnline;
                        _lastTwitchEligibilityCheck = DateTime.Now;
                    }
                    bool twitchShowingAd = _currentTwitchCampaign != null && await IsTwitchShowingAd();
                    bool kickOnline = _currentKickCampaign != null && await IsKickStreamOnline();
                    bool kickCorrectCategory = _currentKickCampaign != null && await IsKickStreamCategoryCorrect();

                    AppLogger.Debug("HealthCheck", $"Twitch: {(twitchOnline ? "ONLINE" : "OFFLINE")} | Kick: {(kickOnline ? "ONLINE" : "OFFLINE")}");
                    AppLogger.Debug("HealthCheck", $"Twitch category correct: {twitchCorrectCategory} | Kick category correct: {kickCorrectCategory} | Twitch showing ad: {twitchShowingAd}");
                    AppLogger.Info("HealthCheck", $"Twitch online={twitchOnline}, categoryOk={twitchCorrectCategory}, showingAd={twitchShowingAd}; Kick online={kickOnline}, categoryOk={kickCorrectCategory}");

                    // Freeze the optimistic tick the moment a platform is offline (no real progress is being earned).
                    _twitchCurrentlyOnline = _currentTwitchCampaign != null && twitchOnline;
                    _kickCurrentlyOnline = _currentKickCampaign != null && kickOnline;

                    TwitchStreamOnlineChanged?.Invoke(twitchOnline);
                    KickStreamOnlineChanged?.Invoke(kickOnline);

                    // Keep the display honest: snap each platform to the server's real progress (throttled to ~3 min),
                    // so the optimistic local tick can't keep showing more than what was actually credited. For Twitch
                    // this means a rate-limited/blocked account shows the real frozen value instead of a fake climb.
                    if (_currentKickCampaign != null && kickOnline)
                        await ReconcileKickProgressAsync();
                    if (_currentTwitchCampaign != null && twitchOnline)
                        await ReconcileTwitchProgressAsync();

                    // Group campaigns by platform
                    List<DropsCampaign> twitchCampaigns = [.. ActiveCampaigns.Where(c => c.Platform == Platform.Twitch && c.HasProgressToMake() && c.IsWithinActiveWindow())];
                    List<DropsCampaign> kickCampaigns = [.. ActiveCampaigns.Where(c => c.Platform == Platform.Kick && c.HasProgressToMake() && c.IsWithinActiveWindow())];

                    // NOTE: an ad is NOT a reason to switch — Twitch keeps crediting drop watch time during ads, and
                    // switching channel/campaign on every ad caused needless thrashing (jumping off a perfectly good
                    // campaign). Only switch when the stream is actually offline or in the wrong category.
                    // Pin resume: while the pin is suspended (its streamers were offline and we fell back to another
                    // campaign), poll — throttled to ~3 min — whether a pinned channel came back live. When it did,
                    // lift the suspension and re-evaluate the pin's platform so mining returns to the user's choice.
                    bool pinResumeTwitch = false;
                    bool pinResumeKick = false;
                    if (_pinSuspendedNoStreamers && !string.IsNullOrWhiteSpace(_forcedCampaignId)
                        && (DateTime.Now - _lastPinOnlineCheck) > TimeSpan.FromMinutes(3))
                    {
                        _lastPinOnlineCheck = DateTime.Now;
                        DropsCampaign? pinned = ActiveCampaigns.FirstOrDefault(c => c.Id == _forcedCampaignId);
                        if (pinned != null && await PinnedCampaignHasLiveChannelAsync(pinned))
                        {
                            AppLogger.Info("Selection", $"Pinned campaign '{pinned.Name}' has a live streamer again — returning to it.");
                            _pinSuspendedNoStreamers = false;
                            if (pinned.Platform == Platform.Twitch) pinResumeTwitch = true; else pinResumeKick = true;
                        }
                    }

                    // Re-evaluate when the watched stream isn't earning even though it's online:
                    //  - the current CHANNEL stalled (froze) → rotate to a fresh streamer of the same campaign
                    //    (this also applies to a PINNED campaign — the pin fixes the campaign, not the channel);
                    //  - the current CAMPAIGN was flagged not-crediting (non-pinned only) → switch campaign.
                    bool twitchStalled = (_currentTwitchCampaign != null && IsChannelStalled(Platform.Twitch, _currentTwitchLogin))
                        || (_currentTwitchCampaign != null && IsNotCrediting(_currentTwitchCampaign.Id)
                            && !string.Equals(_forcedCampaignId, _currentTwitchCampaign.Id, StringComparison.Ordinal));
                    bool kickStalled = (_currentKickCampaign != null && IsChannelStalled(Platform.Kick, _currentKickLogin))
                        || (_currentKickCampaign != null && IsNotCrediting(_currentKickCampaign.Id)
                            && !string.Equals(_forcedCampaignId, _currentKickCampaign.Id, StringComparison.Ordinal));

                    // Throttle stall triggers to one re-evaluation per ~5 min. When rotation has no alternative
                    // channel, the same stalled channel gets re-selected and the flag persists — untamed, that
                    // forced a re-selection every health tick (~30-40s), visibly resetting the dashboard cards.
                    if (twitchStalled)
                    {
                        if ((DateTime.Now - _lastTwitchStallReeval) < TimeSpan.FromMinutes(5))
                            twitchStalled = false;
                        else
                            _lastTwitchStallReeval = DateTime.Now;
                    }
                    if (kickStalled)
                    {
                        if ((DateTime.Now - _lastKickStallReeval) < TimeSpan.FromMinutes(5))
                            kickStalled = false;
                        else
                            _lastKickStallReeval = DateTime.Now;
                    }

                    bool twitchNeedsReevaluation = (twitchCampaigns.Count != 0 && (!twitchOnline || !twitchCorrectCategory) && _lastKnownTwitchOnlineState) || twitchStalled || pinResumeTwitch;
                    bool kickNeedsReevaluation = (kickCampaigns.Count != 0 && (!kickOnline || !kickCorrectCategory) && _lastKnownKickOnlineState) || kickStalled || pinResumeKick;

                    // Idle retry: a platform with no selection but campaigns still worth mining (e.g. its
                    // campaign just completed, or no streamer was live earlier) re-attempts selection
                    // periodically instead of staying stuck until the next full refresh.
                    if (!twitchNeedsReevaluation && _currentTwitchCampaign == null && twitchCampaigns.Count != 0 && DateTime.Now >= _nextTwitchIdleRetryAt)
                    {
                        twitchNeedsReevaluation = true;
                        _nextTwitchIdleRetryAt = DateTime.Now.AddMinutes(5);
                        AppLogger.Info("HealthCheck", "Twitch idle with campaigns available - retrying selection.");
                    }

                    if (!kickNeedsReevaluation && _currentKickCampaign == null && kickCampaigns.Count != 0 && DateTime.Now >= _nextKickIdleRetryAt)
                    {
                        kickNeedsReevaluation = true;
                        _nextKickIdleRetryAt = DateTime.Now.AddMinutes(5);
                        AppLogger.Info("HealthCheck", "Kick idle with campaigns available - retrying selection.");
                    }

                    if (twitchNeedsReevaluation || kickNeedsReevaluation)
                    {
                        if (!twitchOnline)
                            _lastKnownTwitchOnlineState = false;

                        if (!kickOnline)
                            _lastKnownKickOnlineState = false;

                        AppLogger.Debug("HealthCheck", "Stream unhealthy -> forcing re-evaluation");
                        AppLogger.Warn("HealthCheck", $"Forcing re-evaluation. twitchOnline={twitchOnline}, twitchCategoryOk={twitchCorrectCategory}, twitchAd={twitchShowingAd}, kickOnline={kickOnline}, kickCategoryOk={kickCorrectCategory}");
                        _streamHealthTimer?.Stop();

                        // Restart only the unhealthy platform so the healthy one keeps mining undisturbed.
                        Platform? scope = twitchNeedsReevaluation && kickNeedsReevaluation
                            ? null
                            : twitchNeedsReevaluation ? Platform.Twitch : Platform.Kick;
                        await StartWatchingStreams(true, scope);
                    }
                });
                }
                catch (Exception ex)
                {
                    // An unhandled exception in this async-void timer handler would kill the process.
                    AppLogger.Error("HealthCheck", "Health check tick failed.", ex);
                }
            };

            _streamHealthTimer.AutoReset = true;
            _streamHealthTimer.Start();
        }
        /// <summary>
        /// Selects the most optimal campaign from the provided list based on completion percentage and proximity to the
        /// next unclaimed reward.
        /// </summary>
        /// <remarks>This method prioritizes campaigns that are furthest along in completion. If there is
        /// a tie, it selects the campaign that requires the least additional time to claim its next reward. The method
        /// assumes that the input list contains at least one campaign; otherwise, an exception may be thrown.</remarks>
        /// <param name="campaigns">A list of available campaigns to evaluate. Cannot be null or empty.</param>
        /// <returns>The campaign that has the highest completion percentage. If multiple campaigns share the highest completion
        /// percentage, the campaign closest to earning its next unclaimed reward is selected.</returns>
        private Task<DropsCampaign?> SelectBestCampaign(List<DropsCampaign> campaigns)
        {
            // Manual override: if the user pinned a campaign and it's among the (still-progressing)
            // candidates, mine that one instead of auto-prioritising. Skipped while the pin is suspended
            // (its streamers are offline) so the fallback can pick a campaign that's actually watchable.
            if (!string.IsNullOrWhiteSpace(_forcedCampaignId) && !_pinSuspendedNoStreamers)
            {
                DropsCampaign? forced = campaigns.FirstOrDefault(c => c.Id == _forcedCampaignId && c.HasProgressToMake());
                if (forced != null)
                {
                    AppLogger.Info("Selection", $"Using user-forced campaign '{forced.Name}' ({forced.Id}).");
                    return Task.FromResult<DropsCampaign?>(forced);
                }
            }

            // Skip campaigns that proved to not actually credit (server frozen while watched) — but never go idle:
            // if every candidate is flagged, fall back to the full list.
            lock (_creditSync)
            {
                if (_notCreditingCampaignIds.Count != 0)
                {
                    List<DropsCampaign> crediting = campaigns.Where(c => !_notCreditingCampaignIds.Contains(c.Id)).ToList();
                    if (crediting.Count != 0)
                        campaigns = crediting;
                }
            }

            MiningPriorityMode mode = UISettingsManager.Instance.MiningPriorityMode;
            AppLogger.Debug("Selection", $"Selecting best campaign with mode={mode}, candidates={campaigns.Count}");
            List<DropsCampaign> prioritizedCampaigns = mode switch
            {
                MiningPriorityMode.EndingSoonest => [.. campaigns
                        .OrderBy(c => c.IsGeneralDrop)
                        .ThenBy(c => c.EndsAt)
                        .ThenBy(c => c.Rewards
                            .Where(r => !r.IsClaimed)
                            .Min(r => r.RequiredMinutes - r.ProgressMinutes))],
                MiningPriorityMode.LeastTimeToNextReward => [.. campaigns
                        .OrderBy(c => c.IsGeneralDrop)
                        .ThenBy(c => c.Rewards
                            .Where(r => !r.IsClaimed)
                            .Min(r => r.RequiredMinutes - r.ProgressMinutes))
                        .ThenByDescending(c => c.CompletionPercentage())],
                MiningPriorityMode.HighestCompletion => [.. campaigns
                        .OrderBy(c => c.IsGeneralDrop)
                        .ThenByDescending(c => c.CompletionPercentage())
                        .ThenBy(c => c.EndsAt)
                        .ThenBy(c => c.Rewards
                            .Where(r => !r.IsClaimed)
                            .Min(r => r.RequiredMinutes - r.ProgressMinutes))],
                _ => [.. campaigns
                        .OrderBy(c => c.IsGeneralDrop)
                        .ThenByDescending(c => c.CompletionPercentage())
                        .ThenBy(c => c.Rewards
                            .Where(r => !r.IsClaimed)
                            .Min(r => r.RequiredMinutes - r.ProgressMinutes))],
            };

            DropsCampaign? selected = prioritizedCampaigns.FirstOrDefault();
            if (selected == null)
                AppLogger.Warn("Selection", "No campaign selected after priority sort.");
            else
                AppLogger.Info("Selection", $"Selected campaign '{selected.Name}' ({selected.Id}) with mode={mode}.");

            return Task.FromResult(selected);
        }
        /// <summary>
        /// Attempts to set the Kick stream playback quality to the lowest available option asynchronously.
        /// </summary>
        /// <remarks>This method performs a best-effort attempt to change the stream quality by executing
        /// JavaScript in the KickWebView. If KickWebView is null, the method returns immediately and no action is
        /// taken. Any errors encountered during script execution are silently ignored.</remarks>
        /// <returns>A task that represents the asynchronous operation.</returns>
        private async Task SetKickStreamToLowestQualityAsync()
        {
            if (KickWebView == null)
                return;

            // Open settings -> Quality -> Select lowest available (usually 160p or Audio Only)
            string js = @"
                (() => {
                    sessionStorage.setItem('stream_quality', '160');
                })();
            ";

            try
            {
                string result = await await Application.Current.Dispatcher.InvokeAsync(async () => await KickWebView.ExecuteScriptAsync(js));
                AppLogger.Debug("KickSelection", "[Kick] Quality set to lowest: 160p 30");
            }
            catch (Exception ex)
            {
                AppLogger.Warn("KickSelection", $"Failed setting Kick quality to lowest. {ex.Message}");
            }
        }
        /// <summary>
        /// Attempts to set the Twitch stream quality to the lowest available option using the embedded web view.
        /// </summary>
        /// <remarks>This method performs a best-effort attempt to change the stream quality by executing
        /// JavaScript in the Twitch web player. If the web view is not available or the required UI elements cannot be
        /// found, the operation is silently ignored. No exceptions are thrown for failures.</remarks>
        /// <returns>A task that represents the asynchronous operation. The task completes when the quality selection attempt has
        /// finished.</returns>
        /// <summary>
        /// Ensures the platform's (hidden) video element is actually playing. A paused/stalled player earns no
        /// drop credit on the server even when the channel is live, so this is called periodically by the health
        /// check to keep watch time accumulating.
        /// </summary>
        private async Task EnsureVideoPlayingAsync(Platform platform)
        {
            IWebViewHost? host = platform == Platform.Twitch ? TwitchWebView : KickWebView;
            if (host == null)
                return;

            const string js = @"
                (() => {
                    const vids = Array.from(document.querySelectorAll('video'));
                    if (vids.length === 0) return 'no-video';
                    let acted = 'playing';
                    for (const v of vids) {
                        try {
                            v.muted = true;
                            if (v.paused) { v.play().catch(() => {}); acted = 'resumed'; }
                        } catch (e) {}
                    }
                    return acted;
                })();
            ";

            try
            {
                string r = await await Application.Current.Dispatcher.InvokeAsync(async () => await host.ExecuteScriptAsync(js));
                if (r != null && r.Contains("resumed", StringComparison.OrdinalIgnoreCase))
                    AppLogger.Info("Playback", $"{platform} player was paused; called play() to keep earning.");
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Playback", $"{platform} ensure-playing failed: {ex.Message}");
            }
        }
        private async Task SetTwitchStreamToLowestQualityAsync()
        {
            if (TwitchWebView == null) return;

            // Open settings -> Quality -> Select lowest available (usually 160p or Audio Only)
            string js = @"
                (() => {
                    localStorage.setItem('video-quality', '{""default"":""160p30""}');
                })();
            ";

            try
            {
                string result = await await Application.Current.Dispatcher.InvokeAsync(async () => await TwitchWebView.ExecuteScriptAsync(js));
                AppLogger.Debug("TwitchSelection", "[Twitch] Quality set to 160p 30");
            }
            catch (Exception ex)
            {
                AppLogger.Warn("TwitchSelection", $"Failed setting Twitch quality to lowest. {ex.Message}");
            }
        }
        /// <summary>
        /// Attempts to automatically dismiss the mature content gate overlay in the Kick web view, if present.
        /// </summary>
        /// <remarks>This method performs a scripted click on the mature content confirmation button
        /// within the Kick web view, if the overlay is detected. If the web view is not available or the overlay is not
        /// present, no action is taken. Exceptions during script execution are ignored, as the operation is
        /// non-critical.</remarks>
        /// <returns>A task that represents the asynchronous operation. The task completes when the dismissal attempt has
        /// finished.</returns>
        private async Task DismissKickMatureContentGateAsync()
        {
            if (KickWebView == null)
                return;

            string js = @"
                (() => {
                    const button = document.querySelector('button[data-a-target=""player-overlay-mature-accept""]') ||
                                   document.querySelector('button:has-text(""Continue"")') ||
                                   document.querySelector('button:contains(""Continue"")');
                    if (button) {
                        button.click();
                        return true;
                    }
                    return false;
                })();
            ";

            try
            {
                string result = await await Application.Current.Dispatcher.InvokeAsync(async () => await KickWebView.ExecuteScriptAsync(js));
                if (result?.Trim('"').Equals("true", StringComparison.OrdinalIgnoreCase) == true)
                    AppLogger.Debug("KickSelection", "[Kick] Auto-accepted mature content gate.");
            }
            catch (Exception ex)
            {
                AppLogger.Warn("KickSelection", $"Failed dismissing Kick mature content gate. {ex.Message}");
            }
        }
        /// <summary>
        /// Attempts to automatically dismiss the mature content gate overlay in the Twitch web view by simulating a
        /// user acceptance action.
        /// </summary>
        /// <remarks>This method performs a script injection into the Twitch web view to locate and click
        /// the acceptance button for mature content. If the web view is not available or the gate is not present, no
        /// action is taken. The method is silent on failure and does not throw exceptions for script errors.</remarks>
        /// <returns>A task that represents the asynchronous operation. The task completes when the attempt to dismiss the mature
        /// content gate has finished.</returns>
        private async Task DismissTwitchMatureContentGateAsync()
        {
            if (TwitchWebView == null) return;

            string js = @"
                (() => {
                    const button = document.querySelector('button[data-a-target=""content-classification-gate-overlay-start-watching-button""]');

                    if (button) {
                        button.click();
                        return true;
                    }

                    return false;
                })();
            ";

            try
            {
                string result = await await Application.Current.Dispatcher.InvokeAsync(async () => await TwitchWebView.ExecuteScriptAsync(js));
                if (result?.Trim('"').Equals("true", StringComparison.OrdinalIgnoreCase) == true)
                    AppLogger.Debug("TwitchSelection", "[Twitch] Auto-accepted mature content gate.");
            }
            catch (Exception ex)
            {
                AppLogger.Warn("TwitchSelection", $"Failed dismissing Twitch mature content gate. {ex.Message}");
            }
        }
        /// <summary>
        /// Determines whether the Kick stream is currently online by evaluating the presence of a 'LIVE' indicator in
        /// the web view.
        /// </summary>
        /// <remarks>This method relies on the KickWebView instance to execute a script that checks for a
        /// 'LIVE' label in the page content. If KickWebView is null, the method returns <see
        /// langword="false"/>.</remarks>
        /// <returns>A <see langword="true"/> value if the Kick stream is online; otherwise, <see langword="false"/>.</returns>
        private async Task<bool> IsKickStreamOnline(string? login = null)
        {
            string? slug = !string.IsNullOrWhiteSpace(login) ? login : _currentKickLogin;
            if (KickWebView == null || string.IsNullOrWhiteSpace(slug))
                return false;

            // Use Kick's authoritative channel API (livestream present?) instead of scraping the DOM for the word
            // "LIVE" — that gave false positives (e.g. "Watch live" buttons / recommended-live sidebars) and made the
            // miner sit on offline channels showing fake local progress.
            try
            {
                string json = await await Application.Current.Dispatcher.InvokeAsync(
                    async () => await KickWebView.FetchKickChannelStatusesAsync(new[] { slug! }, 8000));

                using JsonDocument doc = JsonDocument.Parse(json);
                bool isOnline = doc.RootElement.TryGetProperty(slug!, out JsonElement v)
                                && v.ValueKind == JsonValueKind.Number
                                && v.GetInt32() >= 0; // viewer_count when live, -1 when offline/unknown

                AppLogger.Debug("KickSelection", $"[DropsInventoryManager] Kick stream online status for '{slug}': {isOnline}");
                return isOnline;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("KickSelection", $"Kick online check via API failed for '{slug}': {ex.Message}");
                return false;
            }
        }

        private DateTime _lastKickProgressReconcile = DateTime.MinValue;

        /// <summary>
        /// Pulls the authoritative Kick drops progress from the server (via a non-disruptive in-page fetch) and
        /// snaps the displayed progress to it, so the optimistic per-minute tick can't keep showing more than the
        /// real value. No navigation/reset of the watched stream. Throttled to once every ~3 minutes.
        /// </summary>
        private async Task ReconcileKickProgressAsync()
        {
            if (KickWebView == null || _currentKickCampaign == null || _isPaused)
                return;
            if ((DateTime.Now - _lastKickProgressReconcile).TotalMinutes < 3)
                return;
            _lastKickProgressReconcile = DateTime.Now;

            try
            {
                string json = await await Application.Current.Dispatcher.InvokeAsync(
                    async () => await KickWebView.FetchKickDropsProgressAsync());
                if (string.IsNullOrWhiteSpace(json))
                    return;

                Dictionary<string, int> campProgress = new(StringComparer.Ordinal);
                Dictionary<string, bool> rewardClaimed = new(StringComparer.Ordinal);
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    if (!doc.RootElement.TryGetProperty("data", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
                        return;
                    foreach (JsonElement item in arr.EnumerateArray())
                    {
                        if (!item.TryGetProperty("id", out JsonElement idEl)) continue;
                        string cid = idEl.GetString() ?? "";
                        if (item.TryGetProperty("progress_units", out JsonElement pu) && pu.ValueKind == JsonValueKind.Number)
                            campProgress[cid] = pu.GetInt32();
                        if (item.TryGetProperty("rewards", out JsonElement rewards) && rewards.ValueKind == JsonValueKind.Array)
                            foreach (JsonElement rw in rewards.EnumerateArray())
                                if (rw.TryGetProperty("id", out JsonElement rid) && rw.TryGetProperty("claimed", out JsonElement cl))
                                    rewardClaimed[rid.GetString() ?? ""] = cl.GetBoolean();
                    }
                }

                if (campProgress.Count == 0)
                    return;

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    for (int i = 0; i < ActiveCampaigns.Count; i++)
                    {
                        DropsCampaign c = ActiveCampaigns[i];
                        if (c.Platform != Platform.Kick)
                            continue;

                        if (campProgress.TryGetValue(c.Id, out int units))
                        {
                            List<DropsReward> updated = c.Rewards.Select(r => r with
                            {
                                ProgressMinutes = Math.Min(units, r.RequiredMinutes),
                                IsClaimed = rewardClaimed.TryGetValue(r.Id, out bool claimed) ? claimed : r.IsClaimed
                            }).ToList();
                            ActiveCampaigns[i] = c with { Rewards = updated };
                        }
                        else if (c.Rewards.Any(r => !r.IsClaimed && r.ProgressMinutes > 0))
                        {
                            // Not in the server progress response → genuinely 0 progress. Clear the optimistic local
                            // progress that the co-mining tick added (claimed rewards keep their state).
                            List<DropsReward> updated = c.Rewards
                                .Select(r => r.IsClaimed ? r : r with { ProgressMinutes = 0 })
                                .ToList();
                            ActiveCampaigns[i] = c with { Rewards = updated };
                        }
                    }

                    // Snap the watched Kick baseline to the server value so the live tick continues from the truth.
                    if (_currentKickCampaign != null)
                    {
                        DropsCampaign? fresh = ActiveCampaigns.FirstOrDefault(c => c.Id == _currentKickCampaign.Id);
                        if (fresh != null)
                        {
                            _currentKickCampaign = fresh;
                            _kickWatchedSeconds = fresh.Rewards.Sum(r => Math.Min(r.ProgressMinutes, r.RequiredMinutes) * 60);
                            DropsReward? nextR = fresh.Rewards.Where(r => !r.IsClaimed).OrderBy(r => r.RequiredMinutes).FirstOrDefault();
                            int beforeNext = fresh.Rewards.Where(r => !r.IsClaimed && r.RequiredMinutes < (nextR?.RequiredMinutes ?? 0)).Sum(r => r.RequiredMinutes);
                            _kickDropWatchedSeconds = Math.Max(0, (nextR?.ProgressMinutes ?? 0) - beforeNext) * 60;
                            _kickAppliedMinuteBucket = _kickWatchedSeconds / 60;
                            KickProgressChanged?.Invoke(CalculateLiveCampaignProgress(fresh), CalculateLiveDropProgress(fresh, _kickDropWatchedSeconds));
                        }
                    }
                });

                if (_currentKickCampaign != null && campProgress.TryGetValue(_currentKickCampaign.Id, out int kickMin))
                    TrackCampaignCrediting(_currentKickCampaign.Id, Platform.Kick, kickMin);

                AppLogger.Info("KickProgress", $"Reconciled Kick progress to server for {campProgress.Count} campaign(s).");
            }
            catch (Exception ex)
            {
                AppLogger.Warn("KickProgress", $"Reconcile failed: {ex.Message}");
            }
        }

        private DateTime _lastTwitchProgressReconcile = DateTime.MinValue;

        /// <summary>
        /// Pulls the authoritative Twitch drop progress from the server (GraphQL drops dashboard, cached headers so
        /// no re-navigation) and snaps the display to it. This keeps the Twitch bar honest — if the server isn't
        /// crediting (e.g. account rate-limited), the bar shows the real frozen value instead of the optimistic
        /// climb. Throttled to once every ~3 minutes.
        /// </summary>
        private async Task ReconcileTwitchProgressAsync()
        {
            if (_twitchGqlService == null || _currentTwitchCampaign == null || _isPaused)
                return;
            if ((DateTime.Now - _lastTwitchProgressReconcile).TotalMinutes < 3)
                return;
            _lastTwitchProgressReconcile = DateTime.Now;

            try
            {
                JsonArray dashboard = await _twitchGqlService.QueryFullDropsDashboardAsync();
                JsonArray? inProgress = dashboard.Count > 0
                    ? dashboard[0]?["data"]?["currentUser"]?["inventory"]?["dropCampaignsInProgress"]?.AsArray()
                    : null;
                if (inProgress == null)
                    return;

                Dictionary<string, (int Minutes, bool Claimed)> rewardProgress = new(StringComparer.Ordinal);
                foreach (JsonNode? camp in inProgress)
                {
                    JsonArray? drops = camp?["timeBasedDrops"]?.AsArray();
                    if (drops == null) continue;
                    foreach (JsonNode? d in drops)
                    {
                        string? rid = d?["id"]?.GetValue<string>();
                        if (string.IsNullOrEmpty(rid)) continue;
                        int min = d?["self"]?["currentMinutesWatched"]?.GetValue<int>() ?? 0;
                        bool claimed = d?["self"]?["isClaimed"]?.GetValue<bool>() ?? false;
                        rewardProgress[rid] = (min, claimed);
                    }
                }
                if (rewardProgress.Count == 0)
                    return;

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    for (int i = 0; i < ActiveCampaigns.Count; i++)
                    {
                        DropsCampaign c = ActiveCampaigns[i];
                        if (c.Platform != Platform.Twitch || !c.Rewards.Any(r => rewardProgress.ContainsKey(r.Id)))
                            continue;

                        List<DropsReward> updated = c.Rewards.Select(r =>
                            rewardProgress.TryGetValue(r.Id, out (int Minutes, bool Claimed) p)
                                ? r with { ProgressMinutes = Math.Min(p.Minutes, r.RequiredMinutes), IsClaimed = p.Claimed }
                                : r).ToList();
                        ActiveCampaigns[i] = c with { Rewards = updated };
                    }

                    if (_currentTwitchCampaign != null)
                    {
                        DropsCampaign? fresh = ActiveCampaigns.FirstOrDefault(c => c.Id == _currentTwitchCampaign.Id);
                        if (fresh != null)
                        {
                            _currentTwitchCampaign = fresh;
                            _twitchWatchedSeconds = fresh.Rewards.Sum(r => Math.Min(r.ProgressMinutes, r.RequiredMinutes) * 60);
                            DropsReward? nextR = fresh.Rewards.Where(r => !r.IsClaimed).OrderBy(r => r.RequiredMinutes).FirstOrDefault();
                            int beforeNext = fresh.Rewards.Where(r => !r.IsClaimed && r.RequiredMinutes < (nextR?.RequiredMinutes ?? 0)).Sum(r => r.RequiredMinutes);
                            _twitchDropWatchedSeconds = Math.Max(0, (nextR?.ProgressMinutes ?? 0) - beforeNext) * 60;
                            _twitchAppliedMinuteBucket = _twitchWatchedSeconds / 60;
                            TwitchProgressChanged?.Invoke(CalculateLiveCampaignProgress(fresh), CalculateLiveDropProgress(fresh, _twitchDropWatchedSeconds));
                        }
                    }
                });

                string watchedDiag = "n/a";
                if (_currentTwitchCampaign != null)
                {
                    DropsReward? cur = _currentTwitchCampaign.Rewards.Where(r => !r.IsClaimed).OrderBy(r => r.RequiredMinutes).FirstOrDefault();
                    if (cur != null && rewardProgress.TryGetValue(cur.Id, out (int Minutes, bool Claimed) cp))
                        watchedDiag = $"{cur.Name}={cp.Minutes}/{cur.RequiredMinutes} min";

                    // Detect a campaign that isn't actually crediting (server frozen while watched) and switch off it.
                    int serverTotal = _currentTwitchCampaign.Rewards
                        .Where(r => rewardProgress.ContainsKey(r.Id))
                        .Sum(r => rewardProgress[r.Id].Minutes);
                    TrackCampaignCrediting(_currentTwitchCampaign.Id, Platform.Twitch, serverTotal);
                }
                AppLogger.Info("TwitchProgress", $"Reconciled Twitch progress to server for {rewardProgress.Count} drop(s). watched: {watchedDiag}");
            }
            catch (Exception ex)
            {
                AppLogger.Warn("TwitchProgress", $"Reconcile failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Tracks whether the currently-watched campaign is actually earning SERVER progress. If it stays frozen for
        /// ~2 consecutive reconciles (~6 min) while watched, it's flagged as not-crediting and we re-select off it,
        /// so the miner stops wasting time on a campaign that won't progress (e.g. R6S without a Ubisoft link).
        /// A user-pinned campaign is never auto-switched (the user asked for it).
        /// </summary>
        /// <summary>
        /// Resets the Kick dashboard card to an explicit "waiting — no live channel" idle state, so a stale
        /// campaign whose bar would otherwise keep ticking is replaced by a clear indication that nothing is
        /// currently being mined on Kick.
        /// </summary>
        private void ClearKickCard()
        {
            _kickCurrentlyOnline = false;
            KickProgressChanged?.Invoke(0, 0);
            KickCampaignChanged?.Invoke("Waiting — no live channel", null);
            KickDropChanged?.Invoke(string.Empty, null);
            KickChannelChanged?.Invoke(string.Empty);
            KickStreamOnlineChanged?.Invoke(false);
        }

        /// <summary>True when the campaign was flagged as not actually crediting (server progress frozen while watched).</summary>
        private bool IsNotCrediting(string? campaignId)
        {
            if (string.IsNullOrEmpty(campaignId))
                return false;
            lock (_creditSync) { return _notCreditingCampaignIds.Contains(campaignId); }
        }

        /// <summary>True when this channel froze (earned no server progress) and should be skipped during selection.</summary>
        private bool IsChannelStalled(Platform platform, string? login)
        {
            if (string.IsNullOrWhiteSpace(login))
                return false;
            lock (_creditSync)
            {
                return platform == Platform.Twitch ? _stalledTwitchLogins.Contains(login) : _stalledKickLogins.Contains(login);
            }
        }

        /// <summary>
        /// Cheap "is anyone live for this campaign" probe used to resume a suspended pin. Twitch channel-bound:
        /// one batched persisted GQL; Twitch general: directory query. Kick channel-bound: channel API. Kick
        /// general (no listed channels) has no cheap probe, so the pin is simply retried after ~10 minutes.
        /// </summary>
        private async Task<bool> PinnedCampaignHasLiveChannelAsync(DropsCampaign campaign)
        {
            try
            {
                List<string> logins = CampaignChannelLogins(campaign);
                if (campaign.Platform == Platform.Twitch)
                {
                    if (_twitchGqlService == null)
                        return false;
                    if (logins.Count > 0)
                    {
                        List<string> live = await _twitchGqlService.QueryLiveChannelsBySlugAsync(logins.Take(40).ToList(), campaign.Slug);
                        return live.Count > 0;
                    }
                    List<(string Login, int Viewers)> dir = await _twitchGqlService.QueryLiveDirectoryChannelsAsync(campaign.Slug, 5);
                    return dir.Count > 0;
                }

                if (logins.Count > 0 && KickWebView != null)
                {
                    string json = await await Application.Current.Dispatcher.InvokeAsync(
                        async () => await KickWebView.FetchKickChannelStatusesAsync(logins.Take(40).ToList(), 9000));
                    using JsonDocument doc = JsonDocument.Parse(json);
                    foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.Number && p.Value.GetInt32() >= 0)
                            return true;
                    return false;
                }

                // Kick general drop: probing needs the shared WebView (disruptive), so just retry the pin once it
                // has been suspended for a while.
                return (DateTime.Now - _pinSuspendedAt) > TimeSpan.FromMinutes(10);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Selection", $"Pinned-campaign live check failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Removes channels known to be stalled (frozen, not crediting) from a candidate list so selection rotates
        /// to a fresh streamer. If every candidate is stalled, returns the original list unchanged — better to retry
        /// a stalled channel than to go idle with no stream at all.
        /// </summary>
        private List<string> ExcludeStalledLogins(Platform platform, List<string> logins)
        {
            if (logins.Count == 0)
                return logins;
            List<string> filtered;
            lock (_creditSync)
            {
                HashSet<string> stalled = platform == Platform.Twitch ? _stalledTwitchLogins : _stalledKickLogins;
                if (stalled.Count == 0)
                    return logins;
                filtered = logins.Where(l => !stalled.Contains(l)).ToList();
            }
            return filtered.Count != 0 ? filtered : logins;
        }

        private void TrackCampaignCrediting(string campaignId, Platform platform, int serverMinutes)
        {
            // Only FLAG here. The actual switch is driven by the 30s health check (which re-evaluates the flagged
            // platform on its proven awaited path); firing StartWatchingStreams from inside this reconcile — itself
            // invoked from the health check — proved unreliable (the fire-and-forget call could be swallowed).
            string? currentLogin = platform == Platform.Twitch ? _currentTwitchLogin : _currentKickLogin;
            bool pinned = string.Equals(_forcedCampaignId, campaignId, StringComparison.Ordinal);

            lock (_creditSync)
            {
                if (_creditTracking.TryGetValue(campaignId, out (int Minutes, int FrozenCount) prev))
                {
                    if (serverMinutes > prev.Minutes)
                    {
                        // Progress resumed — this campaign AND its current channel are crediting again.
                        _creditTracking[campaignId] = (serverMinutes, 0);
                        _notCreditingCampaignIds.Remove(campaignId);
                    }
                    else
                    {
                        int frozen = prev.FrozenCount + 1;
                        _creditTracking[campaignId] = (serverMinutes, frozen);
                        if (frozen >= 2)
                        {
                            // The channel we're watching isn't earning — blacklist it so selection rotates to a
                            // different live channel of the same campaign (the dead one was being reused from the
                            // remembered-streamer cache). Reset the frozen counter so the NEW channel gets a fair trial.
                            if (!string.IsNullOrWhiteSpace(currentLogin))
                            {
                                HashSet<string> stalled = platform == Platform.Twitch ? _stalledTwitchLogins : _stalledKickLogins;
                                if (stalled.Add(currentLogin))
                                    AppLogger.Warn("Selection", $"Channel '{currentLogin}' not crediting campaign {campaignId} (server frozen at {serverMinutes}m) — rotating to another streamer.");
                            }
                            _creditTracking[campaignId] = (serverMinutes, 0);

                            // Only give up on the whole campaign (skip it) when it's NOT pinned. A pinned campaign is
                            // the user's explicit choice, so we keep it and just hop channels.
                            if (!pinned)
                                _notCreditingCampaignIds.Add(campaignId);
                        }
                    }
                }
                else
                {
                    _creditTracking[campaignId] = (serverMinutes, 0);
                }
            }
        }
        /// <summary>
        /// Determines whether the current Kick stream category matches the expected category based on the active Kick
        /// campaign slug.
        /// </summary>
        /// <remarks>This method retrieves the category from the Kick web view and compares it to the slug
        /// of the current Kick campaign. Returns <see langword="false"/> if the web view is not initialized.</remarks>
        /// <returns>A task that represents the asynchronous operation. The task result contains <see langword="true"/> if the
        /// Kick stream category matches the current campaign slug; otherwise, <see langword="false"/>.</returns>
        private async Task<bool> IsKickStreamCategoryCorrect()
        {
            if (KickWebView == null)
                return false;

            string js = @"
                (() => {
                    const categoryElement = document.querySelector("".text-primary-base"");
                    return categoryElement ? categoryElement.href.trim() : '';
                })();
                ";

            string rawResult = await await Application.Current.Dispatcher.InvokeAsync(async () => await KickWebView.ExecuteScriptAsync(js));
            bool isCorrect = KickCategoryHrefMatchesCampaign(rawResult, _currentKickCampaign?.Slug);

            AppLogger.Debug("KickSelection", $"[DropsInventoryManager] Kick stream category correct status: {isCorrect}");
            return isCorrect;
        }
        /// <summary>
        /// Determines whether the Twitch stream is currently live by evaluating the status indicator in the embedded
        /// web view.
        /// </summary>
        /// <remarks>This method relies on the presence of a specific status indicator element in the
        /// Twitch web view. If the web view is not initialized or the indicator cannot be found, the method returns
        /// <see langword="false"/>.</remarks>
        /// <returns>A task that represents the asynchronous operation. The task result is <see langword="true"/> if the Twitch
        /// stream is live; otherwise, <see langword="false"/>.</returns>
        /// <summary>
        /// Raises <see cref="TwitchDropChanged"/> only when the targeted reward changes, to avoid per-tick spam.
        /// </summary>
        private void RaiseTwitchDropChangedIfNeeded(DropsReward? reward)
        {
            if (reward?.Id == _lastTwitchDropId)
                return;

            _lastTwitchDropId = reward?.Id;
            TwitchDropChanged?.Invoke(reward?.Name ?? string.Empty, reward?.ImageUrl);
        }

        /// <summary>
        /// Raises <see cref="KickDropChanged"/> only when the targeted reward changes, to avoid per-tick spam.
        /// </summary>
        private void RaiseKickDropChangedIfNeeded(DropsReward? reward)
        {
            if (reward?.Id == _lastKickDropId)
                return;

            _lastKickDropId = reward?.Id;
            KickDropChanged?.Invoke(reward?.Name ?? string.Empty, reward?.ImageUrl);
        }

        /// <summary>
        /// Authoritatively checks whether a Twitch streamer is live AND streaming the expected category,
        /// using the GraphQL API instead of fragile DOM scraping. Returns <c>null</c> when the check could
        /// not be performed (no GQL service, no login, no slug, or a transient error), so the caller can
        /// fall back to the DOM-based checks.
        /// </summary>
        private async Task<bool?> IsTwitchStreamEligibleViaGqlAsync(string? login, string? slug)
        {
            if (_twitchGqlService == null || string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(slug))
                return null;

            try
            {
                List<string> liveMatches = await _twitchGqlService.QueryLiveChannelsBySlugAsync(new[] { login }, slug);
                bool eligible = liveMatches.Any(l => string.Equals(l, login, StringComparison.OrdinalIgnoreCase));
                AppLogger.Debug("TwitchSelection", $"[GQL eligibility] login={login}, slug={slug} -> {eligible}");
                return eligible;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("TwitchSelection", $"GQL eligibility check failed for '{login}' (slug={slug}); falling back to DOM. {ex.Message}");
                return null;
            }
        }

        private async Task<bool> IsTwitchStreamOnline()
        {
            if (TwitchWebView == null)
                return false;

            string js = @"
                (() => {
                    const indicator = document.querySelector("".tw-channel-status-text-indicator"");
                    return indicator?.innerText?.trim() === ""LIVE"";
                })();
            ";

            string rawResult = await await Application.Current.Dispatcher.InvokeAsync(async () => await TwitchWebView.ExecuteScriptAsync(js));
            bool isOnline = rawResult?
                .Trim()
                .Trim('"')
                .Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;

            AppLogger.Debug("TwitchSelection", $"[DropsInventoryManager] Twitch stream online status: {isOnline}");
            return isOnline;
        }
        /// <summary>
        /// Determines asynchronously whether a Twitch advertisement is currently being displayed in the embedded web
        /// view.
        /// </summary>
        /// <remarks>This method checks for the presence of known Twitch ad indicators in the web view's
        /// DOM. It returns <see langword="false"/> if the web view is not available.</remarks>
        /// <returns>A task that represents the asynchronous operation. The task result is <see langword="true"/> if a Twitch ad
        /// is detected; otherwise, <see langword="false"/>.</returns>
        private async Task<bool> IsTwitchShowingAd()
        {
            if (TwitchWebView == null)
                return false;

            string js = @"
                (() => {
                    const adSelectors = [
                    '[data-a-target=""video-ad-countdown""]',
                    '[data-a-target=""video-ad-label""]',
                    '[data-test-selector=""ad-banner-default-text""]'
                  ];

                  // Check if ANY of these elements exist in the document
                  return adSelectors.some(selector => 
                    document.querySelector(selector) !== null
                  );
                })();
            ";

            string rawResult = await await Application.Current.Dispatcher.InvokeAsync(async () => await TwitchWebView.ExecuteScriptAsync(js));
            bool isAdShowing = rawResult?
                .Trim()
                .Trim('"')
                .Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;

            AppLogger.Debug("TwitchSelection", $"[DropsInventoryManager] Twitch showing ad status: {isAdShowing}");
            return isAdShowing;
        }
        /// <summary>
        /// Determines whether the current Twitch stream category matches the expected category for the active campaign.
        /// </summary>
        /// <remarks>This method retrieves the current category from the Twitch stream by executing a
        /// JavaScript snippet in the TwitchWebView. The comparison is case-insensitive and ignores leading or trailing
        /// whitespace. Returns <see langword="false"/> if the TwitchWebView is not initialized.</remarks>
        /// <returns>A task that represents the asynchronous operation. The task result contains <see langword="true"/> if the
        /// Twitch stream category matches the expected campaign category; otherwise, <see langword="false"/>.</returns>
        private async Task<bool> IsTwitchStreamCategoryCorrect()
        {
            if (TwitchWebView == null)
                return false;

            string js = @"
                (() => {
                    const links = Array.from(document.querySelectorAll('[data-a-target=stream-game-link]'));
                    return links
                        .map(link => link?.href?.trim())
                        .filter(Boolean)
                        .join('|');
                })();
                ";

            string rawResult = await await Application.Current.Dispatcher.InvokeAsync(async () => await TwitchWebView.ExecuteScriptAsync(js));
            bool isCorrect = TwitchCategoryHrefMatchesCampaign(rawResult, _currentTwitchCampaign?.Slug);

            AppLogger.Debug("TwitchSelection", $"[DropsInventoryManager] Twitch stream category correct status: {isCorrect}");
            return isCorrect;
        }
        /// <summary>
        /// Selects the appropriate Kick streamer URL for the specified drops campaign.
        /// </summary>
        /// <remarks>If the campaign is a general drop, the method attempts to locate a streamer whose
        /// campaign name matches the specified campaign. Otherwise, it returns the first URL in the campaign's
        /// connection list. The method relies on the KickWebView instance to navigate and execute JavaScript in order
        /// to extract the streamer URL.</remarks>
        /// <param name="campaign">The drops campaign for which to select a Kick streamer URL. Must not be null.</param>
        /// <returns>A string containing the URL of the selected Kick streamer for the campaign. Returns a category-matching
        /// connection URL for non-general campaigns; otherwise, the first streamer from the matching directory section,
        /// or an empty string if no suitable streamer is found.</returns>
        private async Task<string> SelectKickStreamerForCampaign(DropsCampaign campaign)
        {
            string streamerUrl = string.Empty;
            _kickSelectionForced = false;
            TryGetLastStreamerUrl(Platform.Kick, campaign.Id, out string? rememberedKickUrl);
            // Ignore a previously-remembered non-channel URL (e.g. a stale kick.com/category/... value).
            if (!string.IsNullOrWhiteSpace(rememberedKickUrl) && !IsRealChannelUrl(rememberedKickUrl))
                rememberedKickUrl = null;

            // Honour an explicit user pick directly (no live/category gate) so the chosen channel is never
            // silently swapped back to a different one.
            lock (_lastStreamerSync)
            {
                if (_forcedKickStreamer is { } forced &&
                    string.Equals(forced.CampaignId, campaign.Id, StringComparison.OrdinalIgnoreCase))
                {
                    streamerUrl = forced.Url;
                    _forcedKickStreamer = null;
                    _kickSelectionForced = true;
                    AppLogger.Info("KickSelection", $"Using user-picked Kick streamer directly for '{campaign.Name}': {streamerUrl}");
                }
            }

            string getStreamerCategoryJs = @"
                (() => {
                    const categoryElement = document.querySelector("".text-primary-base"");
                    return categoryElement ? categoryElement.href.trim() : '';
                })();
            ";
            string getFirstStreamerFromDirectoryJs;

            if (string.IsNullOrEmpty(campaign.Slug))
            {
                getFirstStreamerFromDirectoryJs = $@"
                    (() => {{
                        const link = document.querySelectorAll('section>div.group\\/card>a')[0].href
                        return link ? link.trim() : '';
                    }})();
                ";
            }
            else
            {
                getFirstStreamerFromDirectoryJs = $@"
                    (() => {{
                        const titles = document.querySelectorAll('h3.text-base.font-bold.leading-5');
                        if (titles.length === 0) return '';
                        let targetSection = null;
                        for (const h3 of titles) {{
                            if (h3.innerText.includes('{campaign.Name.Replace("'", "\\'")}')) {{
                                targetSection = h3.closest('section') || h3.parentElement.parentElement.parentElement.parentElement;
                                break;
                            }}
                        }}
                        if (!targetSection) return '';
                        const streamGrid = targetSection.querySelector(':scope > div:nth-child(2)') || targetSection.children[1];
                        if (!streamGrid || streamGrid.children.length === 0) return '';
                        const firstCard = streamGrid.children[0];
                        const link = firstCard.querySelector('a');
                        return link ? link.href.trim() : '';
                    }})();
                ";
            }

            if (!string.IsNullOrWhiteSpace(streamerUrl))
            {
                // User-picked channel already resolved above; skip the auto-selection heuristics.
            }
            else if (CampaignChannelLogins(campaign).Count > 0)
            {
                // Channel-bound campaign: specific participating channels. This also covers Kick "Football Drop"
                // campaigns that are flagged "general" but actually require a particular channel. The channel itself
                // is the requirement (category is irrelevant), so pick one that is live via Kick's API and watch it
                // directly — never scrape a directory (which returns null on a live channel page) or gate on category.
                List<string> channelLogins = CampaignChannelLogins(campaign);
                Dictionary<string, int> status = new(StringComparer.OrdinalIgnoreCase);
                try
                {
                    string json = await await Application.Current.Dispatcher.InvokeAsync(
                        async () => await KickWebView!.FetchKickChannelStatusesAsync(channelLogins.Take(40).ToList(), 10000));
                    using JsonDocument doc = JsonDocument.Parse(json);
                    foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.Number)
                            status[p.Name] = p.Value.GetInt32();
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("KickSelection", $"Channel status lookup failed for '{campaign.Name}': {ex.Message}");
                }

                List<string> online = FilterSkippedLogins(Platform.Kick,
                    channelLogins.Where(l => status.TryGetValue(l, out int v) && v >= 0).ToList());
                online = ExcludeStalledLogins(Platform.Kick, online);

                string? rememberedLogin = string.IsNullOrWhiteSpace(rememberedKickUrl) ? null : GetStreamerNameFromUrl(rememberedKickUrl!);
                string? pick = rememberedLogin != null && !IsChannelStalled(Platform.Kick, rememberedLogin) && online.Any(l => string.Equals(l, rememberedLogin, StringComparison.OrdinalIgnoreCase))
                    ? rememberedLogin
                    : online.FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(pick))
                {
                    streamerUrl = $"https://kick.com/{pick}";
                    AppLogger.Info("KickSelection", $"Selected live channel for '{campaign.Name}': {streamerUrl} ({online.Count} live of {channelLogins.Count}).");
                }
                else
                {
                    AppLogger.Warn("KickSelection", $"No live channel for channel-bound campaign '{campaign.Name}'.");
                }
            }
            else
            {
                // Step 1: Try remembered URL if available (validate category!)
                if (!string.IsNullOrWhiteSpace(rememberedKickUrl))
                {
                    AppLogger.Info("KickSelection", $"Trying remembered Kick streamer for general campaign '{campaign.Name}': {rememberedKickUrl}");

                    await await Application.Current.Dispatcher.InvokeAsync(async () =>
                        await KickWebView!.NavigateAsync(rememberedKickUrl));

                    await Task.Delay(1500);  // Consider → WaitForNetworkIdleAsync(5000, 500) for better sync

                    string categoryResult = await await Application.Current.Dispatcher.InvokeAsync(async () =>
                        await KickWebView!.ExecuteScriptAsync(getStreamerCategoryJs));

                    if (KickCategoryHrefMatchesCampaign(categoryResult, campaign.Slug))
                    {
                        AppLogger.Info("KickSelection", $"Remembered streamer still matches category for general campaign '{campaign.Name}': {rememberedKickUrl}");
                        streamerUrl = rememberedKickUrl!;
                    }
                    else
                    {
                        AppLogger.Warn("KickSelection", $"Remembered URL no longer matches category for general '{campaign.Name}': {rememberedKickUrl} | found: '{categoryResult}'");
                        // fall through to directory fallback
                    }
                }

                // Step 2: If no valid remembered → navigate category directory → extract first streamer via your JS
                if (string.IsNullOrWhiteSpace(streamerUrl) && campaign.ConnectUrls?.Any() == true)
                {
                    string directoryUrl = campaign.ConnectUrls[0];  // category page with live list

                    AppLogger.Info("KickSelection", $"Falling back to category directory for general campaign '{campaign.Name}': {directoryUrl}");

                    await await Application.Current.Dispatcher.InvokeAsync(async () =>
                        await KickWebView!.NavigateAsync(directoryUrl));

                    await Task.Delay(1500);  // Again - consider WaitForNetworkIdleAsync if needed

                    string firstStreamerRawResult = await await Application.Current.Dispatcher.InvokeAsync(async () => await KickWebView!.ExecuteScriptAsync(getFirstStreamerFromDirectoryJs));

                    streamerUrl = firstStreamerRawResult?.Trim().Trim('"') ?? string.Empty;

                    if (!string.IsNullOrWhiteSpace(streamerUrl))
                    {
                        AppLogger.Info("KickSelection", $"Selected first live streamer from directory for '{campaign.Name}': {streamerUrl}");
                    }
                    else
                    {
                        AppLogger.Warn("KickSelection", $"Failed to extract any first streamer from directory '{directoryUrl}' for general campaign '{campaign.Name}'");
                    }
                }
            }

            // The directory scraper sometimes returns a VOD link (kick.com/<chan>/videos/<id>); reduce it to the
            // channel root. A real category/directory URL can't be normalised and is discarded (watching it earns
            // nothing and only produced fake local progress). The online gate below still rejects offline channels.
            if (!string.IsNullOrWhiteSpace(streamerUrl) && !IsRealChannelUrl(streamerUrl))
            {
                if (TryNormalizeChannelUrl(Platform.Kick, streamerUrl, out string normalized))
                {
                    AppLogger.Info("KickSelection", $"Normalised non-root URL to channel for '{campaign.Name}': {streamerUrl} -> {normalized}");
                    streamerUrl = normalized;
                }
                else
                {
                    AppLogger.Warn("KickSelection", $"Resolved a non-channel URL for '{campaign.Name}' ({streamerUrl}); discarding so nothing fake is mined.");
                    streamerUrl = string.Empty;
                }
            }

            // Final logging and event
            if (!string.IsNullOrWhiteSpace(streamerUrl))
            {
                KickChannelChanged?.Invoke(GetStreamerNameFromUrl(streamerUrl));
                KickCampaignChanged?.Invoke(campaign.Name, campaign.GameImageUrl);
                AppLogger.Debug("KickSelection", $"[DropsInventoryManager] Selected Kick streamer URL for general campaign '{campaign.Name}': {streamerUrl}");
            }
            else
            {
                AppLogger.Warn("KickSelection", $"No valid Kick streamer URL resolved for general campaign '{campaign.Name}'.");
            }

            return streamerUrl;
        }
        /// <summary>
        /// Selects the appropriate Twitch streamer URL for the specified drops campaign.
        /// </summary>
        /// <remarks>For general drop campaigns, this method navigates the Twitch web view to the
        /// campaign's first connection URL and attempts to extract the URL of the first streamer listed in the
        /// directory. The returned URL may be empty if no streamer is found.</remarks>
        /// <param name="campaign">The drops campaign for which to select a Twitch streamer. Must not be null.</param>
        /// <returns>A string containing the URL of the selected Twitch streamer for the campaign. Returns the first connection
        /// URL that matches category if the campaign is not a general drop; otherwise, returns the URL of the first streamer found in the
        /// Twitch directory, or an empty string if none is found.</returns>
        private async Task<string> SelectTwitchStreamerForCampaign(DropsCampaign campaign)
        {
            string streamerUrl = string.Empty;
            _twitchSelectionForced = false;
            TryGetLastStreamerUrl(Platform.Twitch, campaign.Id, out string? rememberedTwitchUrl);
            // Ignore a previously-remembered non-channel URL (e.g. a stale twitch.tv/directory/... value).
            if (!string.IsNullOrWhiteSpace(rememberedTwitchUrl) && !IsRealChannelUrl(rememberedTwitchUrl))
                rememberedTwitchUrl = null;

            // Honour an explicit user pick directly (no live/category gate).
            lock (_lastStreamerSync)
            {
                if (_forcedTwitchStreamer is { } forced &&
                    string.Equals(forced.CampaignId, campaign.Id, StringComparison.OrdinalIgnoreCase))
                {
                    streamerUrl = forced.Url;
                    _forcedTwitchStreamer = null;
                    _twitchSelectionForced = true;
                    AppLogger.Info("TwitchSelection", $"Using user-picked Twitch streamer directly for '{campaign.Name}': {streamerUrl}");
                }
            }

            string getStreamerCategoryHrefJs = @"
                (() => {
                    const links = Array.from(document.querySelectorAll('[data-a-target=stream-game-link]'));
                    return links
                        .map(link => link?.href?.trim())
                        .filter(Boolean)
                        .join('|');
                })();
            ";

            string getFirstStreamerJs = @"
                (() => {
                    const firstItem = document.querySelector('div[data-target=""directory-first-item""]');
                    if (!firstItem) return '';
                    const link = firstItem.querySelector('a[href^=""\/""]');
                    return link ? 'https://www.twitch.tv' + link.getAttribute('href') : '';
                })();
            ";

            if (!string.IsNullOrWhiteSpace(streamerUrl))
            {
                // User-picked channel already resolved above; skip the auto-selection heuristics.
            }
            else if (!campaign.IsGeneralDrop)
            {
                IEnumerable<string> orderedConnectUrls = campaign.ConnectUrls;
                if (!string.IsNullOrWhiteSpace(rememberedTwitchUrl))
                {
                    orderedConnectUrls = new[] { rememberedTwitchUrl! }
                        .Concat(campaign.ConnectUrls)
                        .Distinct(StringComparer.OrdinalIgnoreCase);
                }

                // If there are many ConnectUrls, batch-check live status via GQL
                // instead of navigating the WebView one by one
                const int webViewThreshold = 10;
                if (campaign.ConnectUrls.Count > webViewThreshold)
                {
                    AppLogger.Info("TwitchSelection", $"Campaign '{campaign.Name}' has {campaign.ConnectUrls.Count} ConnectUrls - using batch GQL live check.");

                    List<string> loginNames = orderedConnectUrls
                        .Select(GetStreamerNameFromUrl)
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .ToList();

                    List<string> liveLogins = await _twitchGqlService!.QueryLiveChannelsBySlugAsync(loginNames, campaign.Slug);

                    // Honour user "skip streamer" requests; wrap around (clear) if everyone got skipped.
                    liveLogins = FilterSkippedLogins(Platform.Twitch, liveLogins);
                    liveLogins = ExcludeStalledLogins(Platform.Twitch, liveLogins);

                    if (liveLogins.Count == 0)
                    {
                        AppLogger.Warn("TwitchSelection", $"No live streamers found for campaign '{campaign.Name}' via batch GQL.");
                    }
                    else
                    {
                        // QueryLiveChannelsBySlug already guarantees each login is live AND in the
                        // correct category (server-side), so accept the first one directly instead of
                        // navigating and re-checking via fragile DOM scraping (which rejected valid streamers).
                        string acceptedLogin = liveLogins[0];
                        streamerUrl = $"https://www.twitch.tv/{acceptedLogin}";
                        AppLogger.Info("TwitchSelection", $"Batch GQL streamer accepted for campaign '{campaign.Name}': {streamerUrl} (live+category confirmed via GQL; {liveLogins.Count} candidates).");
                    }
                }
                else
                {
                    // Original sequential WebView path for small ConnectUrl lists
                    foreach (string connectUrl in orderedConnectUrls)
                    {
                        await await Application.Current.Dispatcher.InvokeAsync(async () => await TwitchWebView!.NavigateAsync(connectUrl));
                        await await Application.Current.Dispatcher.InvokeAsync(async () => await TwitchWebView!.WaitForNetworkIdleAsync(5000, 500));

                        string categoryHrefResult = await await Application.Current.Dispatcher.InvokeAsync(async () => await TwitchWebView!.ExecuteScriptAsync(getStreamerCategoryHrefJs));

                        if (TwitchCategoryHrefMatchesCampaign(categoryHrefResult, campaign.Slug))
                        {
                            streamerUrl = connectUrl;

                            if (!string.IsNullOrWhiteSpace(rememberedTwitchUrl) &&
                                string.Equals(connectUrl, rememberedTwitchUrl, StringComparison.OrdinalIgnoreCase))
                            {
                                AppLogger.Info("TwitchSelection", $"Remembered Twitch streamer accepted for campaign '{campaign.Name}': {connectUrl}");
                            }

                            break;
                        }

                        AppLogger.Warn("TwitchSelection", $"Twitch URL category mismatch for campaign '{campaign.Name}'. url='{connectUrl}', categoryHrefs='{categoryHrefResult.Trim().Trim('"')}', slug='{campaign.Slug}'");
                    }
                }
            }
            else
            {
                // General drop: rely on authoritative GraphQL (live + correct category) instead of fragile
                // DOM scraping, which intermittently returned empty and caused a needless reselect churn.

                // Step 1: keep the remembered streamer if it's still eligible (and not user-skipped).
                if (!string.IsNullOrWhiteSpace(rememberedTwitchUrl))
                {
                    string rememberedLogin = GetStreamerNameFromUrl(rememberedTwitchUrl!);
                    bool isSkipped;
                    lock (_lastStreamerSync)
                    {
                        isSkipped = _skipTwitchLogins.Contains(rememberedLogin);
                    }
                    // Don't return to a channel that stalled (froze with no credit) — rotate to a fresh one instead.
                    if (IsChannelStalled(Platform.Twitch, rememberedLogin))
                    {
                        isSkipped = true;
                        AppLogger.Info("TwitchSelection", $"Remembered Twitch streamer '{rememberedLogin}' is stalled (not crediting) — skipping it to rotate channel.");
                    }

                    if (!isSkipped)
                    {
                        AppLogger.Info("TwitchSelection", $"Checking remembered Twitch streamer (GQL) for general campaign '{campaign.Name}': {rememberedTwitchUrl}");
                        bool? eligible = await IsTwitchStreamEligibleViaGqlAsync(rememberedLogin, campaign.Slug);
                        if (eligible == true)
                        {
                            streamerUrl = rememberedTwitchUrl!;
                            AppLogger.Info("TwitchSelection", $"Remembered Twitch streamer still eligible for general campaign '{campaign.Name}': {rememberedTwitchUrl}");
                        }
                        else
                        {
                            AppLogger.Info("TwitchSelection", $"Remembered Twitch streamer no longer eligible for general '{campaign.Name}': {rememberedTwitchUrl} (eligible={eligible?.ToString() ?? "unknown"}).");
                        }
                    }
                }

                // Step 2: otherwise pick the top live, drops-enabled channel from the game directory via GQL,
                // honouring user "skip streamer" requests so the Switch button actually moves to a new channel.
                if (string.IsNullOrWhiteSpace(streamerUrl))
                {
                    List<(string Login, int Viewers)> directory = await _twitchGqlService!.QueryLiveDirectoryChannelsAsync(campaign.Slug, 30);
                    List<string> dirLogins = FilterSkippedLogins(Platform.Twitch, directory.Select(d => d.Login).ToList());
                    dirLogins = ExcludeStalledLogins(Platform.Twitch, dirLogins);

                    if (dirLogins.Count != 0)
                    {
                        streamerUrl = $"https://www.twitch.tv/{dirLogins[0]}";
                        AppLogger.Info("TwitchSelection", $"Selected live directory streamer (GQL) for general '{campaign.Name}': {streamerUrl} ({dirLogins.Count} live).");
                    }
                    else
                    {
                        AppLogger.Warn("TwitchSelection", $"No live directory streamers (GQL) for general '{campaign.Name}'.");
                    }
                }

                // Step 3: last-resort DOM directory scrape (only if GQL returned nothing, e.g. query rejected).
                if (string.IsNullOrWhiteSpace(streamerUrl) && campaign.ConnectUrls?.Any() == true)
                {
                    string directoryUrl = campaign.ConnectUrls[0];  // category/game directory page
                    AppLogger.Info("TwitchSelection", $"GQL directory empty; falling back to DOM directory for general '{campaign.Name}': {directoryUrl}");

                    await await Application.Current.Dispatcher.InvokeAsync(async () =>
                        await TwitchWebView!.NavigateAsync(directoryUrl));
                    await Task.Delay(1500);

                    string firstStreamerRawResult = await await Application.Current.Dispatcher.InvokeAsync(async () =>
                        await TwitchWebView!.ExecuteScriptAsync(getFirstStreamerJs));

                    streamerUrl = firstStreamerRawResult?.Trim().Trim('"') ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(streamerUrl))
                        AppLogger.Info("TwitchSelection", $"Selected first live streamer from DOM directory for '{campaign.Name}': {streamerUrl}");
                }
            }

            // Reduce a non-root URL (e.g. .../videos/<id>) to the channel root; discard true category/directory
            // URLs (watching them earns nothing). The online/eligibility gate below still rejects offline channels.
            if (!string.IsNullOrWhiteSpace(streamerUrl) && !IsRealChannelUrl(streamerUrl))
            {
                if (TryNormalizeChannelUrl(Platform.Twitch, streamerUrl, out string normalized))
                {
                    AppLogger.Info("TwitchSelection", $"Normalised non-root URL to channel for '{campaign.Name}': {streamerUrl} -> {normalized}");
                    streamerUrl = normalized;
                }
                else
                {
                    AppLogger.Warn("TwitchSelection", $"Resolved a non-channel URL for '{campaign.Name}' ({streamerUrl}); discarding so nothing fake is mined.");
                    streamerUrl = string.Empty;
                }
            }

            // Final logging and event
            if (!string.IsNullOrWhiteSpace(streamerUrl))
            {
                TwitchChannelChanged?.Invoke(GetStreamerNameFromUrl(streamerUrl));
                TwitchCampaignChanged?.Invoke(campaign.Name, campaign.GameImageUrl);
                AppLogger.Debug("TwitchSelection", $"[DropsInventoryManager] Selected Twitch streamer URL for general campaign '{campaign.Name}': {streamerUrl}");
            }
            else
            {
                AppLogger.Warn("TwitchSelection", $"No valid Twitch streamer URL resolved for general campaign '{campaign.Name}'.");
            }

            return streamerUrl;
        }
        /// <summary>
        /// Determines whether the specified category hrefs contain a directory path matching the given campaign slug.
        /// </summary>
        /// <remarks>The comparison is case-insensitive and ignores leading or trailing whitespace and
        /// quotes in the hrefs. Returns false if either parameter is null or consists only of whitespace.</remarks>
        /// <param name="rawCategoryHrefs">A string containing one or more category hrefs to search, which may include surrounding whitespace or
        /// quotes. Can be null.</param>
        /// <param name="campaignKey">The campaign slug to match within the category hrefs. Can be null.</param>
        /// <returns>true if the hrefs contain a directory path for the specified campaign slug; otherwise, false.</returns>
        private static bool TwitchCategoryHrefMatchesCampaign(string? rawCategoryHrefs, string? campaignKey)
        {
            if (string.IsNullOrWhiteSpace(rawCategoryHrefs) || string.IsNullOrWhiteSpace(campaignKey))
                return false;

            string expectedCategoryPath = $"/directory/category/{campaignKey}";
            string hrefs = rawCategoryHrefs.Trim().Trim('"');
            return hrefs.Contains(expectedCategoryPath, StringComparison.OrdinalIgnoreCase);
        }
        private static bool KickCategoryHrefMatchesCampaign(string? rawCategoryHrefs, string? campaignKey)
        {
            if (string.IsNullOrWhiteSpace(rawCategoryHrefs))
                return false;
            else if (string.IsNullOrWhiteSpace(campaignKey))
                return true;

            string expectedCategoryPath = $"/category/{campaignKey}";
            string hrefs = rawCategoryHrefs.Trim().Trim('"');
            return hrefs.Contains(expectedCategoryPath, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Attempts to retrieve the last known streamer URL for the specified platform and campaign slug.
        /// </summary>
        /// <remarks>This method is thread-safe. The returned URL may be null if no streamer has been
        /// recorded for the given campaign slug.</remarks>
        /// <param name="platform">The platform for which to retrieve the last streamer URL. Must be a valid value of the Platform enumeration.</param>
        /// <param name="campaignKey">The campaign identifier used to look up the streamer URL. Cannot be null, empty, or whitespace.</param>
        /// <param name="url">When this method returns, contains the last known streamer URL if found; otherwise, null.</param>
        /// <returns>true if a valid streamer URL was found for the specified platform and campaign slug; otherwise, false.</returns>
        private bool TryGetLastStreamerUrl(Platform platform, string? campaignKey, out string? url)
        {
            url = null;
            if (string.IsNullOrWhiteSpace(campaignKey))
                return false;

            lock (_lastStreamerSync)
            {
                Dictionary<string, string> source = platform == Platform.Twitch ? _lastTwitchStreamers : _lastKickStreamers;
                if (!source.TryGetValue(campaignKey, out string? remembered) || string.IsNullOrWhiteSpace(remembered))
                    return false;

                url = remembered;
                return true;
            }
        }
        /// <summary>
        /// Stores the last watched streamer URL for the specified platform and campaign if the provided values are
        /// valid.
        /// </summary>
        /// <remarks>If the campaign slug or streamer URL is invalid, the method does not update the
        /// stored value. Updates are persisted only if the value changes.</remarks>
        /// <param name="platform">The platform for which to record the last watched streamer URL. Determines whether Twitch or Kick is
        /// updated.</param>
        /// <param name="campaignKey">The unique identifier for the campaign. Cannot be null, empty, or whitespace.</param>
        /// <param name="streamerUrl">The URL of the streamer to remember. Cannot be null, empty, or whitespace.</param>
        private void RememberLastStreamerUrl(Platform platform, string? campaignKey, string? streamerUrl)
        {
            if (string.IsNullOrWhiteSpace(campaignKey) || string.IsNullOrWhiteSpace(streamerUrl))
                return;

            // Never remember a category/directory/browse URL as a "streamer" — watching such a page earns no
            // drops (the bug where Kick "watched" kick.com/category/irl and only ticked fake local progress).
            if (!IsRealChannelUrl(streamerUrl))
            {
                AppLogger.Warn("Selection", $"Refusing to remember non-channel URL as streamer ({platform}): {streamerUrl}");
                return;
            }

            bool changed = false;

            lock (_lastStreamerSync)
            {
                Dictionary<string, string> target = platform == Platform.Twitch ? _lastTwitchStreamers : _lastKickStreamers;
                if (!target.TryGetValue(campaignKey, out string? existing) || !string.Equals(existing, streamerUrl, StringComparison.OrdinalIgnoreCase))
                {
                    target[campaignKey] = streamerUrl;
                    changed = true;
                }
            }

            if (changed)
                SaveLastWatchedStreamers();
        }
        /// <summary>
        /// Removes the remembered streamer URL for the specified platform and campaign, if present.
        /// </summary>
        /// <param name="platform">The platform whose remembered streamer collection should be updated.</param>
        /// <param name="campaignKey">The campaign slug key associated with the remembered streamer.</param>
        private void ForgetLastStreamerUrl(Platform platform, string? campaignKey)
        {
            if (string.IsNullOrWhiteSpace(campaignKey))
                return;

            bool removed;
            lock (_lastStreamerSync)
            {
                Dictionary<string, string> target = platform == Platform.Twitch ? _lastTwitchStreamers : _lastKickStreamers;
                removed = target.Remove(campaignKey);
            }

            if (removed)
            {
                SaveLastWatchedStreamers();
                AppLogger.Info("Selection", $"Forgot remembered streamer for platform={platform}, campaignKey='{campaignKey}'.");
            }
        }
        /// <summary>
        /// Loads the last watched streamers from persistent storage and updates the internal state.
        /// </summary>
        /// <remarks>This method reads streamer information from a file and updates the Twitch and Kick
        /// streamer lists. If the file does not exist or contains invalid data, the lists are not modified. The
        /// operation is thread-safe and logs informational or warning messages based on the outcome.</remarks>
        private void LoadLastWatchedStreamers()
        {
            try
            {
                if (!File.Exists(_lastWatchedStreamersFilePath))
                    return;

                string json = File.ReadAllText(_lastWatchedStreamersFilePath);
                LastWatchedStreamersState? state = JsonSerializer.Deserialize<LastWatchedStreamersState>(json);
                if (state == null)
                    return;

                lock (_lastStreamerSync)
                {
                    _lastTwitchStreamers.Clear();
                    _lastKickStreamers.Clear();

                    foreach ((string key, string value) in state.TwitchBySlug)
                    {
                        if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                            _lastTwitchStreamers[key] = value;
                    }

                    foreach ((string key, string value) in state.KickBySlug)
                    {
                        if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                            _lastKickStreamers[key] = value;
                    }
                }

                AppLogger.Info("StreamSelection", $"Loaded remembered streamers. twitch={_lastTwitchStreamers.Count}, kick={_lastKickStreamers.Count}");
            }
            catch (Exception ex)
            {
                AppLogger.Warn("StreamSelection", $"Failed loading remembered streamers. {ex.Message}");
            }
        }
        /// <summary>
        /// Persists the current state of last watched streamers to disk in JSON format.
        /// </summary>
        /// <remarks>This method serializes the last watched Twitch and Kick streamers and writes them to
        /// the configured file path. If the target directory does not exist, it is created. Any errors during the save
        /// operation are logged as warnings. The method is thread-safe and should be called when the state needs to be
        /// updated on disk.</remarks>
        private void SaveLastWatchedStreamers()
        {
            try
            {
                LastWatchedStreamersState snapshot;

                lock (_lastStreamerSync)
                {
                    snapshot = new LastWatchedStreamersState
                    {
                        TwitchBySlug = _lastTwitchStreamers.ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase),
                        KickBySlug = _lastKickStreamers.ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase)
                    };
                }

                string? directory = Path.GetDirectoryName(_lastWatchedStreamersFilePath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                string json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_lastWatchedStreamersFilePath, json);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("StreamSelection", $"Failed saving remembered streamers. {ex.Message}");
            }
        }

        /// <summary>
        /// Represents the state containing mappings of streamer slugs to their Twitch and Kick usernames.
        /// </summary>
        /// <remarks>This class is used to track the last watched streamers for each platform. The
        /// dictionaries are case-insensitive with respect to streamer slugs.</remarks>
        private sealed class LastWatchedStreamersState
        {
            public Dictionary<string, string> TwitchBySlug { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> KickBySlug { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Extracts the streamer name from the specified Twitch or Kick channel URL.
        /// </summary>
        /// <remarks>This method expects the URL path to be in the format "/{streamerName}". If the URL is
        /// not valid or does not match the expected format, the method returns an empty string.</remarks>
        /// <param name="url">The URL of the Twitch or Kick channel from which to extract the streamer name. Must be a valid absolute URL.</param>
        /// <returns>The streamer name extracted from the URL, or an empty string if the URL is invalid or does not contain a
        /// streamer name.</returns>
        private string GetStreamerNameFromUrl(string url)
        {
            try
            {
                Uri uri = new Uri(url);
                string path = uri.AbsolutePath.Trim('/');
                // Twitch URLs are typically in the format /{streamerName}
                // Kick URLs are typically in the format /{streamerName}
                return path.Split('/')[0];
            }
            catch (Exception ex)
            {
                AppLogger.Warn("StreamSelection", $"Failed extracting streamer name from url '{url}'. {ex.Message}");
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// Provides extension methods for evaluating progress and completion metrics on DropsCampaign instances.
    /// </summary>
    /// <remarks>These methods assist in determining reward claim status and calculating aggregate progress
    /// for campaigns. All methods require a non-null DropsCampaign instance as input.</remarks>
    public static class DropsCampaignExtensions
    {
        /// <summary>
        /// Determines whether the specified campaign contains any rewards that have not yet been claimed and still
        /// require additional progress.
        /// </summary>
        /// <param name="campaign">The campaign to evaluate for unclaimed rewards with remaining progress requirements. Cannot be null.</param>
        /// <returns>true if at least one reward in the campaign is unclaimed and has not reached its required progress;
        /// otherwise, false.</returns>
        public static bool HasProgressToMake(this DropsCampaign campaign)
        {
            // Only rewards that still need WATCH TIME count as mineable progress. A fully-watched but unclaimed
            // reward (e.g. the game account isn't linked, so the claim keeps failing) must NOT keep the miner
            // parked on the campaign — watching earns nothing there. Claiming is handled separately by the
            // periodic claim pass, and the Inventory shows a "ready to claim / connect account" hint instead.
            return campaign.Rewards.Any(r => !r.IsClaimed && r.ProgressMinutes < r.RequiredMinutes);
        }

        /// <summary>
        /// True only while the campaign is in its live earning window (started and not yet ended). Guards against
        /// mining a campaign whose drops are no longer obtainable when the cached campaign list has gone stale
        /// (e.g. the app ran for days across a fetch outage / PC sleep without a successful refresh).
        /// </summary>
        public static bool IsWithinActiveWindow(this DropsCampaign campaign)
        {
            DateTimeOffset now = DateTimeOffset.Now;
            return campaign.StartsAt <= now && campaign.EndsAt > now;
        }
        /// <summary>
        /// Calculates the overall completion percentage of all rewards in the specified campaign that require progress.
        /// </summary>
        /// <remarks>Only rewards with a positive required minutes value are considered in the
        /// calculation. The percentage is based on the ratio of progress minutes to required minutes for each valid
        /// reward.</remarks>
        /// <param name="campaign">The campaign for which to calculate the completion percentage. Cannot be null.</param>
        /// <returns>A value between 0 and 100 representing the average completion percentage of all rewards with required
        /// progress. Returns 0 if there are no such rewards.</returns>
        public static double CompletionPercentage(this DropsCampaign campaign)
        {
            IEnumerable<DropsReward> valid = campaign.Rewards.Where(r => r.RequiredMinutes > 0);

            if (!valid.Any())
                return 0;

            return valid.Average(r => (double)r.ProgressMinutes / r.RequiredMinutes) * 100;
        }
    }
}
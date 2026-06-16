using UserControl = System.Windows.Controls.UserControl;
using System.Runtime.CompilerServices;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using System.ComponentModel;
using System.Windows;
using Core.Logging;
using Core.Managers;
using Core.Services;
using Core.Models;
using Core.Enums;

namespace UI.Views
{
    /// <summary>
    /// Interaction logic for DashboardView.xaml
    /// </summary>
    public partial class DashboardView : UserControl, INotifyPropertyChanged
    {
        // Authoritative server reconcile interval. Each refresh pulls real per-campaign progress for
        // ALL campaigns (so co-progressing same-game campaigns are corrected and their finished drops get
        // claimed). Kept moderate because a refresh briefly navigates the (shared) WebViews away from the
        // watched stream; the live UI stays current between refreshes via same-game local ticking.
        private readonly System.Timers.Timer _refreshTimer = new(TimeSpan.FromMinutes(60).TotalMilliseconds);

        // When a Twitch campaign fetch fails on Twitch's integrity check (transient — "please wait a while"), retry
        // soon with a short backoff instead of leaving Twitch empty until the next hourly refresh.
        private readonly System.Timers.Timer _twitchRetryTimer = new() { AutoReset = false };
        private int _twitchRetryCount;
        private static readonly int[] _twitchRetryDelaysSec = { 90, 180, 360 };

        private readonly SemaphoreSlim _loadDropsSemaphore = new(1, 1);
        private CancellationTokenSource? _currentLoadCts;
        private readonly object _loadTriggerLock = new();
        private bool _loadScheduled = false;
        // Which platform(s) a pending (debounced) load should (re)start watching for. If more than one platform
        // is pending, or a general refresh was requested, the scope is "both" (null).
        private readonly HashSet<Platform> _pendingLoadPlatforms = new();
        private bool _pendingLoadAll = false;

        private HiddenWebViewHost _twitchWebView = new();
        private HiddenWebViewHost _kickWebView = new();
        private TwitchGqlService? _twitchGqlService;

        private static bool _initialValidationCompleted = false;
        private static bool _isInitialized = false;

        private static readonly Lazy<DashboardView> _instance = new(() => new DashboardView());
        public static DashboardView Instance => _instance.Value;

        // Services
        private readonly TwitchLoginService _twitchService = new();
        private readonly KickLoginService _kickService = new();
        private readonly DropsService _dropsService;

        // Observable collection for UI binding
        private readonly ObservableCollection<DropsCampaign> _activeCampaigns = new();
        public IReadOnlyCollection<DropsCampaign> ActiveCampaigns => _activeCampaigns;

        // UI Properties
        private string _twitchConnectionStatus = "Not Connected";
        public string TwitchConnectionStatus
        {
            get => _twitchConnectionStatus;
            set
            {
                _twitchConnectionStatus = value;
                OnPropertyChanged();
            }
        }
        private string _twitchConnectionColor = "Red";
        public string TwitchConnectionColor
        {
            get => _twitchConnectionColor;
            set
            {
                _twitchConnectionColor = value;
                OnPropertyChanged();
            }
        }

        private string _kickConnectionStatus = "Not Connected";
        public string KickConnectionStatus
        {
            get => _kickConnectionStatus;
            set
            {
                _kickConnectionStatus = value;
                OnPropertyChanged();
            }
        }
        private string _kickConnectionColor = "Red";
        public string KickConnectionColor
        {
            get => _kickConnectionColor;
            set
            {
                _kickConnectionColor = value;
                OnPropertyChanged();
            }
        }

        private string _minerStatus = "Idle";
        public string MinerStatus
        {
            get => _minerStatus;
            set
            {
                _minerStatus = value;
                OnPropertyChanged();
            }
        }
        private string _minerStatusDetails = "Waiting";
        public string MinerStatusDetails
        {
            get => _minerStatusDetails;
            set
            {
                _minerStatusDetails = value;
                OnPropertyChanged();
            }
        }
        private byte _twitchCampaignProgress = 0;
        public byte TwitchCampaignProgress
        {
            get => _twitchCampaignProgress;
            set
            {
                _twitchCampaignProgress = value;
                OnPropertyChanged();
            }
        }
        private byte _twitchDropProgress = 0;
        public byte TwitchDropProgress
        {
            get => _twitchDropProgress;
            set
            {
                _twitchDropProgress = value;
                OnPropertyChanged();
            }
        }
        private byte _kickCampaignProgress = 0;
        public byte KickCampaignProgress
        {
            get => _kickCampaignProgress;
            set
            {
                _kickCampaignProgress = value;
                OnPropertyChanged();
            }
        }
        private byte _kickDropProgress = 0;
        public byte KickDropProgress
        {
            get => _kickDropProgress;
            set
            {
                _kickDropProgress = value;
                OnPropertyChanged();
            }
        }
        private string _twitchWatchedChannel = string.Empty;
        public string TwitchWatchedChannel
        {
            get => _twitchWatchedChannel;
            set
            {
                _twitchWatchedChannel = value;
                OnPropertyChanged();
            }
        }
        private string _kickWatchedChannel = string.Empty;
        public string KickWatchedChannel
        {
            get => _kickWatchedChannel;
            set
            {
                _kickWatchedChannel = value;
                OnPropertyChanged();
            }
        }
        private string _twitchCampaignName = string.Empty;
        public string TwitchCampaignName
        {
            get => _twitchCampaignName;
            set
            {
                _twitchCampaignName = value;
                OnPropertyChanged();
            }
        }
        private string _kickCampaignName = string.Empty;
        public string KickCampaignName
        {
            get => _kickCampaignName;
            set
            {
                _kickCampaignName = value;
                OnPropertyChanged();
            }
        }
        private string _twitchCampaignImageUrl = string.Empty;
        public string TwitchCampaignImageUrl
        {
            get => _twitchCampaignImageUrl;
            set
            {
                _twitchCampaignImageUrl = value;
                OnPropertyChanged();
            }
        }
        private string _kickCampaignImageUrl = string.Empty;
        public string KickCampaignImageUrl
        {
            get => _kickCampaignImageUrl;
            set
            {
                _kickCampaignImageUrl = value;
                OnPropertyChanged();
            }
        }
        private string _twitchDropName = string.Empty;
        public string TwitchDropName
        {
            get => _twitchDropName;
            set
            {
                _twitchDropName = value;
                OnPropertyChanged();
            }
        }
        private string _twitchDropImageUrl = string.Empty;
        public string TwitchDropImageUrl
        {
            get => _twitchDropImageUrl;
            set
            {
                _twitchDropImageUrl = value;
                OnPropertyChanged();
            }
        }
        private string _kickDropName = string.Empty;
        public string KickDropName
        {
            get => _kickDropName;
            set
            {
                _kickDropName = value;
                OnPropertyChanged();
            }
        }
        private string _kickDropImageUrl = string.Empty;
        public string KickDropImageUrl
        {
            get => _kickDropImageUrl;
            set
            {
                _kickDropImageUrl = value;
                OnPropertyChanged();
            }
        }
        private string _twitchStreamOnlineStatus = string.Empty;
        public string TwitchStreamOnlineStatus
        {
            get => _twitchStreamOnlineStatus;
            set
            {
                _twitchStreamOnlineStatus = value;
                OnPropertyChanged();
            }
        }
        private string _kickStreamOnlineStatus = string.Empty;
        public string KickStreamOnlineStatus
        {
            get => _kickStreamOnlineStatus;
            set
            {
                _kickStreamOnlineStatus = value;
                OnPropertyChanged();
            }
        }

        // Campaigns that earn simultaneously on the currently watched channel (general drops + same-channel campaigns).
        public ObservableCollection<CoMiningCampaign> KickAlsoMining { get; } = new ObservableCollection<CoMiningCampaign>();
        public ObservableCollection<CoMiningCampaign> TwitchAlsoMining { get; } = new ObservableCollection<CoMiningCampaign>();

        // Pickable channels for the current campaign (loaded on demand via the "Channels" button).
        public ObservableCollection<ChannelCandidate> TwitchChannels { get; } = new ObservableCollection<ChannelCandidate>();
        public ObservableCollection<ChannelCandidate> KickChannels { get; } = new ObservableCollection<ChannelCandidate>();

        private string _twitchChannelsStatus = "";
        public string TwitchChannelsStatus
        {
            get => _twitchChannelsStatus;
            set { _twitchChannelsStatus = value; OnPropertyChanged(); }
        }

        private string _kickChannelsStatus = "";
        public string KickChannelsStatus
        {
            get => _kickChannelsStatus;
            set { _kickChannelsStatus = value; OnPropertyChanged(); }
        }

        private void RefreshAlsoMining()
        {
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                UpdateCoMining(KickAlsoMining, DropsInventoryManager.Instance.GetCoMiningCampaigns(Platform.Kick));
                UpdateCoMining(TwitchAlsoMining, DropsInventoryManager.Instance.GetCoMiningCampaigns(Platform.Twitch));
            });
        }

        private static void UpdateCoMining(ObservableCollection<CoMiningCampaign> target, IReadOnlyList<CoMiningCampaign> source)
        {
            target.Clear();
            foreach (CoMiningCampaign c in source)
                target.Add(c);
        }

        /// <summary>
        /// Occurs when a property value changes.
        /// </summary>
        /// <remarks>This event is typically raised by the implementation of the INotifyPropertyChanged
        /// interface to notify subscribers that a property value has changed. Handlers receive the name of the property
        /// that changed in the event data. This event is commonly used in data binding scenarios to update UI elements
        /// when underlying data changes.</remarks>
        public event PropertyChangedEventHandler? PropertyChanged;
        /// <summary>
        /// Raises the PropertyChanged event to notify listeners that a property value has changed.
        /// </summary>
        /// <remarks>Use this method to implement the INotifyPropertyChanged interface in classes that
        /// support data binding. Calling this method with the correct property name ensures that UI elements or other
        /// listeners are updated when the property value changes.</remarks>
        /// <param name="name">The name of the property that changed. This value is optional and is automatically provided when called from
        /// a property setter.</param>
        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>
        /// Initializes a new instance of the DashboardView class and sets up event handlers for login status changes.
        /// </summary>
        /// <remarks>This constructor sets the data context to the current instance and subscribes to
        /// login status events for both Kick and Twitch platforms. Event handlers are automatically unsubscribed when
        /// the view is unloaded to prevent memory leaks.</remarks>
        private DashboardView()
        {
            InitializeComponent();
            DataContext = this;

            MinerStatus = "Initializing";
            MinerStatusDetails = "Please wait...";

            _twitchService = new TwitchLoginService();
            _kickService = new KickLoginService();

            _dropsService = new DropsService();

            _twitchGqlService = new TwitchGqlService(_twitchWebView);

            // Subscribe to progress updates ===
            DropsInventoryManager.Instance.TwitchProgressChanged += (campPct, dropPct) =>
            {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    TwitchCampaignProgress = campPct;
                    TwitchDropProgress = dropPct;
                });
            };

            DropsInventoryManager.Instance.KickProgressChanged += (campPct, dropPct) =>
            {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    KickCampaignProgress = campPct;
                    KickDropProgress = dropPct;
                });
            };

            DropsInventoryManager.Instance.MinerStatusChanged += status =>
            {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    switch (status)
                    {
                        case "Idle":
                            MinerStatus = "Idle";
                            MinerStatusDetails = "Waiting for drops";
                            break;
                        case "Starting":
                            MinerStatus = "Starting";
                            MinerStatusDetails = "Finding stream(s) to watch";
                            break;
                        case "Evaluating":
                            MinerStatus = "Evaluating";
                            MinerStatusDetails = "Checking stream(s) for drops eligibility";
                            break;
                        case "Mining":
                            MinerStatus = "Mining";
                            MinerStatusDetails = "Watching stream(s) to earn drops";
                            break;
                    }
                });
            };

            DropsInventoryManager.Instance.KickChannelChanged += channel =>
            {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    KickWatchedChannel = channel;
                    KickStreamOnlineStatus = string.Empty; // unknown until next health check
                });
            };

            DropsInventoryManager.Instance.TwitchChannelChanged += channel =>
            {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    TwitchWatchedChannel = channel;
                    TwitchStreamOnlineStatus = string.Empty; // unknown until next health check
                });
            };

            DropsInventoryManager.Instance.KickCampaignChanged += (campaign, imageUrl) =>
            {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    KickCampaignName = campaign;
                    KickCampaignImageUrl = imageUrl ?? string.Empty;
                });
                RefreshAlsoMining();
            };

            DropsInventoryManager.Instance.TwitchCampaignChanged += (campaign, imageUrl) =>
            {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    TwitchCampaignName = campaign;
                    TwitchCampaignImageUrl = imageUrl ?? string.Empty;
                });
                RefreshAlsoMining();
            };

            // Recompute the "also earning" list whenever campaigns/progress are refreshed from the server.
            DropsInventoryManager.Instance.ActiveCampaigns.CollectionChanged += (s, e) => RefreshAlsoMining();

            // ...and once a minute, so co-progressing campaigns tick visibly between server reconciles.
            DispatcherTimer alsoMiningTimer = new() { Interval = TimeSpan.FromMinutes(1) };
            alsoMiningTimer.Tick += (s, e) => RefreshAlsoMining();
            alsoMiningTimer.Start();

            DropsInventoryManager.Instance.TwitchStreamOnlineChanged += online =>
            {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    TwitchStreamOnlineStatus = string.IsNullOrEmpty(TwitchWatchedChannel) ? string.Empty : (online ? "online" : "offline");
                });
            };

            DropsInventoryManager.Instance.KickStreamOnlineChanged += online =>
            {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    KickStreamOnlineStatus = string.IsNullOrEmpty(KickWatchedChannel) ? string.Empty : (online ? "online" : "offline");
                });
            };

            DropsInventoryManager.Instance.KickDropChanged += (drop, imageUrl) =>
            {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    KickDropName = drop;
                    KickDropImageUrl = imageUrl ?? string.Empty;
                });
            };

            DropsInventoryManager.Instance.TwitchDropChanged += (drop, imageUrl) =>
            {
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    TwitchDropName = drop;
                    TwitchDropImageUrl = imageUrl ?? string.Empty;
                });
            };

            Loaded += async (s, e) => await OnLoadedAsync();
        }

        /// <summary>
        /// Asynchronously refreshes the list of active drops campaigns by retrieving the latest campaigns from the
        /// drops service.
        /// </summary>
        /// <remarks>After calling this method, the active campaigns list is updated to reflect the
        /// current set of active drops campaigns. Any previously stored campaigns are cleared before the new campaigns
        /// are added. This method should be awaited to ensure the refresh completes before accessing the updated
        /// campaigns.</remarks>
        /// <returns>A task that represents the asynchronous refresh operation.</returns>
        public async Task StartAutoRefreshDropsAsync()
        {
            ScheduleDropsLoad();

            _refreshTimer.Elapsed += async (s, e) => await Dispatcher.InvokeAsync(() => ScheduleDropsLoad());
            _refreshTimer.AutoReset = true; // Run forever
            _refreshTimer.Start();

            // Retry just Twitch (not Kick) after a transient integrity failure.
            _twitchRetryTimer.Elapsed += async (s, e) => await Dispatcher.InvokeAsync(() =>
            {
                AppLogger.Info("Dashboard", $"Retrying Twitch campaign load after integrity failure (attempt {_twitchRetryCount}).");
                ScheduleDropsLoad(Platform.Twitch);
            });
        }

        /// <summary>
        /// Looks at the result of the just-completed load: if Twitch is connected but its campaign fetch failed
        /// (integrity), schedule a soon, Twitch-only retry with a short backoff. Resets the backoff on success.
        /// </summary>
        private void HandleTwitchFetchOutcome(Platform? loadedScope)
        {
            // Only act when this load actually fetched Twitch.
            if (loadedScope == Platform.Kick || _twitchService.Status != ConnectionStatus.Connected || _twitchGqlService == null)
                return;

            if (_twitchGqlService.LastDashboardFetchFailed)
            {
                if (_twitchRetryCount >= _twitchRetryDelaysSec.Length)
                {
                    AppLogger.Warn("Dashboard", "Twitch still failing integrity after retries; leaving it for the next full refresh.");
                    return;
                }
                int delaySec = _twitchRetryDelaysSec[_twitchRetryCount];
                _twitchRetryCount++;
                _twitchRetryTimer.Stop();
                _twitchRetryTimer.Interval = TimeSpan.FromSeconds(delaySec).TotalMilliseconds;
                _twitchRetryTimer.Start();
                AppLogger.Warn("Dashboard", $"Twitch campaign fetch failed integrity — retrying in {delaySec}s (attempt {_twitchRetryCount}/{_twitchRetryDelaysSec.Length}).");
            }
            else
            {
                // Twitch loaded fine — clear any pending backoff.
                _twitchRetryCount = 0;
                _twitchRetryTimer.Stop();
            }
        }

        private async void OnTwitchChannelsClick(object sender, RoutedEventArgs e)
            => await LoadChannelsAsync(Platform.Twitch, TwitchChannels, s => TwitchChannelsStatus = s);

        private async void OnKickChannelsClick(object sender, RoutedEventArgs e)
            => await LoadChannelsAsync(Platform.Kick, KickChannels, s => KickChannelsStatus = s);

        private static async Task LoadChannelsAsync(Platform platform, ObservableCollection<ChannelCandidate> target, Action<string> setStatus)
        {
            setStatus("Checking live channels…");
            try
            {
                IReadOnlyList<ChannelCandidate> list = await DropsInventoryManager.Instance.GetChannelCandidatesAsync(platform);
                target.Clear();
                foreach (ChannelCandidate c in list)
                    target.Add(c);

                int online = list.Count(c => c.Online);
                setStatus(list.Count == 0
                    ? "No channels available for this campaign right now."
                    : $"{online} live • {list.Count} channel{(list.Count == 1 ? "" : "s")}");
            }
            catch
            {
                setStatus("Couldn't load channels.");
            }
        }

        private async void OnWatchChannelClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ChannelCandidate ch)
            {
                Platform platform = ch.Url.Contains("kick.com", StringComparison.OrdinalIgnoreCase) ? Platform.Kick : Platform.Twitch;
                await DropsInventoryManager.Instance.SetPreferredStreamerAsync(platform, ch.Login);
            }
        }
        /// <summary>
        /// Schedules a debounced background load of drops, ensuring that rapid consecutive triggers result in a single
        /// load operation after a delay.
        /// </summary>
        /// <remarks>This method prevents multiple load operations from being scheduled in quick
        /// succession by introducing a 2-second debounce period. It is thread-safe and intended to be called when a
        /// load should be triggered, but only after a period of inactivity. The actual load is performed asynchronously
        /// on the background dispatcher priority.</remarks>
        private void ScheduleDropsLoad(Platform? platform = null)
        {
            // Block all loads until initial validation is done.
            if (!_initialValidationCompleted)
                return;

            lock (_loadTriggerLock)
            {
                // Accumulate the requested scope while debouncing. null = general refresh -> both platforms.
                if (platform.HasValue)
                    _pendingLoadPlatforms.Add(platform.Value);
                else
                    _pendingLoadAll = true;

                if (_loadScheduled) return; // already scheduled
                _loadScheduled = true;
            }

            // Fire once, after 300ms of calm (debounced)
            Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(300); // absorb any rapid-fire triggers

                Platform? scope;
                lock (_loadTriggerLock)
                {
                    _loadScheduled = false;
                    // Scope to a single platform only when exactly one platform triggered the load and no
                    // general refresh was requested; otherwise (re)start both.
                    scope = (!_pendingLoadAll && _pendingLoadPlatforms.Count == 1)
                        ? _pendingLoadPlatforms.First()
                        : (Platform?)null;
                    _pendingLoadPlatforms.Clear();
                    _pendingLoadAll = false;
                }

                _ = LoadDropsAsync(scope); // safe - semaphore still protects concurrency
            }, DispatcherPriority.Background);
        }
        /// <summary>
        /// Asynchronously loads the list of active drops campaigns and updates the miner status properties to reflect
        /// the current loading state.
        /// </summary>
        /// <remarks>If a previous load operation is in progress, it will be canceled before starting a
        /// new one. The method updates status properties to indicate progress and results, including error messages if
        /// loading fails. This method should be called when the application needs to refresh the list of available
        /// campaigns.</remarks>
        /// <returns>A task that represents the asynchronous operation of loading active drops campaigns.</returns>
        private async Task LoadDropsAsync(Platform? onlyPlatform = null)
        {
            // Cancel any previous in-flight load
            _currentLoadCts?.Cancel();
            AppLogger.Info("Dashboard", "LoadDropsAsync invoked; previous load cancellation requested if active.");

            // Wait if another load is already running
            await _loadDropsSemaphore.WaitAsync();
            try
            {
                await DropsInventoryManager.Instance.PauseWatchingAsync();
                AppLogger.Info("Dashboard", "Watcher paused for campaign refresh.");

                using CancellationTokenSource cts = new CancellationTokenSource();
                _currentLoadCts = cts;

                if (_kickService.Status != ConnectionStatus.Connected && _twitchService.Status != ConnectionStatus.Connected)
                {
                    AppLogger.Warn("Dashboard", "Campaign load skipped: neither Twitch nor Kick is connected.");
                    MinerStatus = "Need login";
                    MinerStatusDetails = "Please login to Twitch and/or Kick to load campaigns.";
                    return;
                }

                MinerStatus = "Loading Campaigns";
                MinerStatusDetails = "Fetching latest drops...";

                _activeCampaigns.Clear();

                IReadOnlyList<DropsCampaign> allCampaigns = await _dropsService.GetAllActiveCampaignsAsync(_kickWebView, _kickService.Status, _twitchWebView, _twitchService.Status, _twitchGqlService, cts.Token);
                AppLogger.Info("Dashboard", $"Campaign load completed. totalCampaigns={allCampaigns.Count}, twitchStatus={_twitchService.Status}, kickStatus={_kickService.Status}");

                foreach (DropsCampaign? c in allCampaigns.OrderBy(x => x.Platform).ThenBy(x => x.GameName))
                    _activeCampaigns.Add(c);

                DropsInventoryManager.Instance.UpdateCampaigns(allCampaigns, _twitchGqlService, startWatching: false);

                // If Twitch silently returned nothing because of a transient integrity failure, retry it soon
                // (Twitch-only) instead of waiting for the next hourly refresh.
                HandleTwitchFetchOutcome(onlyPlatform);

                MinerStatus = "Idle";
                MinerStatusDetails = $"{_activeCampaigns.Count} active campaigns loaded";
            }
            catch (OperationCanceledException ex) when (_currentLoadCts?.IsCancellationRequested == true)
            {
                // Expected when a new load cancels the old one
                AppLogger.Info("Dashboard", $"LoadDropsAsync canceled due to superseding refresh request. {ex.Message}");
                return;
            }
            catch (Exception ex)
            {
                MinerStatus = "Failed to load campaigns";
                MinerStatusDetails = ex.Message;
                AppLogger.Error("Dashboard", "LoadDropsAsync failed.", ex);
            }
            finally
            {
                _loadDropsSemaphore.Release();
                _currentLoadCts = null;
                // Only (re)start the platform this load was scoped to, so refreshing/connecting one platform
                // doesn't reset the other while it's still mining.
                await DropsInventoryManager.Instance.ResumeWatchingAsync(onlyPlatform);
                AppLogger.Info("Dashboard", "Watcher resumed after campaign refresh.");
            }
        }
        /// <summary>
        /// Asynchronously validates the current Twitch credentials using the associated web view and service.
        /// </summary>
        /// <returns>A task that represents the asynchronous validation operation.</returns>
        private async Task ValidateTwitchCredentialsAsync()
        {
            await _twitchService.ValidateCredentialsAsync(_twitchWebView);
        }
        /// <summary>
        /// Validates the current Kick service credentials asynchronously.
        /// </summary>
        /// <returns>A task that represents the asynchronous validation operation.</returns>
        private async Task ValidateKickCredentialsAsync()
        {
            await _kickService.ValidateCredentialsAsync(_kickWebView);
        }
        /// <summary>
        /// Asynchronously validates the credentials for external services if they are not already connected.
        /// </summary>
        /// <returns>A task that represents the asynchronous validation operation.</returns>
        private async Task ValidateCredentialsAsync()
        {
            // Validate sequentially: the two WebViews share a CDP/WebView2 environment and running both
            // credential checks concurrently raced (intermittently both returned "not logged in").
            if (_twitchService.Status != ConnectionStatus.Connected)
                await ValidateTwitchCredentialsAsync();

            if (_kickService.Status != ConnectionStatus.Connected)
                await ValidateKickCredentialsAsync();
        }

        #region Event Handlers
        /// <summary>
        /// Performs asynchronous validation of Twitch and Kick services when the component is loaded.
        /// </summary>
        /// <returns>A task that represents the asynchronous operation.</returns>
        private async Task OnLoadedAsync()
        {
            if (!_isInitialized)
            {
                _twitchService.StatusChanged += OnTwitchStatusChanged;
                _kickService.StatusChanged += OnKickStatusChanged;

                _isInitialized = true;

                await ValidateCredentialsAsync();

                _initialValidationCompleted = true;
                DropsInventoryManager.Instance.InitializeWebViews(_twitchWebView, _kickWebView);

                // Load campaigns / drops
                await StartAutoRefreshDropsAsync();
            }
        }
        /// <summary>
        /// Handles changes to the Kick connection status and updates related UI elements accordingly.
        /// </summary>
        /// <remarks>This method updates the Kick connection status message, color indicator, and the
        /// enabled state of the Kick login button based on the provided status. It should be called whenever the
        /// connection status changes to ensure the UI reflects the current state.</remarks>
        /// <param name="status">The new connection status value indicating the current state of the Kick login process.</param>
        private void OnKickStatusChanged(ConnectionStatus status)
        {
            switch (status)
            {
                case ConnectionStatus.NotConnected:
                    KickConnectionStatus = "Not Connected";
                    KickConnectionColor = "Red";
                    KickLoginButton.IsEnabled = true;
                    break;

                case ConnectionStatus.Validating:
                    KickConnectionStatus = "Validating...";
                    KickConnectionColor = "Orange";
                    KickLoginButton.IsEnabled = false;
                    break;

                case ConnectionStatus.Connected:
                    KickConnectionStatus = "Connected";
                    KickConnectionColor = "Lime";
                    KickLoginButton.IsEnabled = false; // disable when already logged in
                    ScheduleDropsLoad(Platform.Kick); // start Kick only — don't reset Twitch if it's already mining
                    break;
                case ConnectionStatus.Connecting:
                    KickConnectionStatus = "Connecting...";
                    KickConnectionColor = "Yellow";
                    KickLoginButton.IsEnabled = false;
                    break;
            }
        }
        /// <summary>
        /// Updates the Twitch connection status display and related UI elements based on the specified connection
        /// status.
        /// </summary>
        /// <param name="status">The current connection status of the Twitch login process. Determines how the UI reflects the connection
        /// state.</param>
        private void OnTwitchStatusChanged(ConnectionStatus status)
        {
            switch (status)
            {
                case ConnectionStatus.NotConnected:
                    TwitchConnectionStatus = "Not Connected";
                    TwitchConnectionColor = "Red";
                    TwitchLoginButton.IsEnabled = true;
                    break;

                case ConnectionStatus.Validating:
                    TwitchConnectionStatus = "Validating...";
                    TwitchConnectionColor = "Orange";
                    TwitchLoginButton.IsEnabled = false;
                    break;

                case ConnectionStatus.Connected:
                    TwitchConnectionStatus = "Connected";
                    TwitchConnectionColor = "Lime";
                    TwitchLoginButton.IsEnabled = false; // disable when already logged in
                    ScheduleDropsLoad(Platform.Twitch); // start Twitch only — don't reset Kick if it's already mining
                    break;
                case ConnectionStatus.Connecting:
                    TwitchConnectionStatus = "Connecting...";
                    TwitchConnectionColor = "Yellow";
                    TwitchLoginButton.IsEnabled = false;
                    break;
            }
        }
        /// <summary>
        /// Handles the Click event for the Kick login button, displaying the login dialog and saving the session token
        /// if authentication is successful.
        /// </summary>
        /// <param name="sender">The source of the event, typically the Kick login button.</param>
        /// <param name="e">The event data associated with the Click event.</param>
        private void OnKickLoginClick(object sender, RoutedEventArgs e)
        {
            // Non-modal so the Twitch and Kick login windows can be open at the same time (log in to both in
            // parallel instead of one-after-another). Re-validate once this window is closed.
            KickLoginWindow window = new KickLoginWindow();
            window.Closed += async (_, _) => await ValidateKickCredentialsAsync();
            window.Show();
        }
        /// <summary>
        /// Handles the Click event for the Twitch login button, displaying the Twitch login window and initiating
        /// Twitch account validation.
        /// </summary>
        /// <param name="sender">The source of the event, typically the button that was clicked.</param>
        /// <param name="e">The event data associated with the click event.</param>
        private void OnTwitchLoginClick(object sender, RoutedEventArgs e)
        {
            // Non-modal so the Twitch and Kick login windows can be open at the same time (log in to both in
            // parallel instead of one-after-another). Re-validate once this window is closed.
            TwitchLoginWindow window = new TwitchLoginWindow();
            window.Closed += async (_, _) => await ValidateTwitchCredentialsAsync();
            window.Show();
        }
        #endregion
    }
}
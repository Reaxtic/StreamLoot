using MessageBox = System.Windows.MessageBox;
using System.Diagnostics;
using System.Windows;
using Core.Managers;
using Core.Logging;
using Core.Enums;
using Core.Models;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;

namespace UI.Views
{
    /// <summary>
    /// Interaction logic for SettingsView.xaml
    /// </summary>
    public partial class SettingsView : System.Windows.Controls.UserControl
    {
        private static readonly Lazy<SettingsView> _instance = new(() => new SettingsView());
        public static SettingsView Instance => _instance.Value;

        private ICollectionView? _twitchGameView;
        private ICollectionView? _kickGameView;
        private bool _gameRefreshPending;

        private SettingsView()
        {
            InitializeComponent();
            DataContext = UISettingsManager.Instance;

            _twitchGameView = CollectionViewSource.GetDefaultView(UISettingsManager.Instance.TwitchGameFilterOptions);
            _kickGameView = CollectionViewSource.GetDefaultView(UISettingsManager.Instance.KickGameFilterOptions);
            foreach (var view in new[] { _twitchGameView, _kickGameView })
            {
                view.SortDescriptions.Add(new SortDescription(nameof(GameFilterOption.PriorityOrder), ListSortDirection.Ascending));
                view.SortDescriptions.Add(new SortDescription(nameof(GameFilterOption.DisplayName), ListSortDirection.Ascending));
            }
            _twitchGameView.Filter = item => MatchesGameFilter(item, TwitchGameSearchBox.Text, HideExcludedTwitchCheckBox.IsChecked == true);
            _kickGameView.Filter = item => MatchesGameFilter(item, KickGameSearchBox.Text, HideExcludedKickCheckBox.IsChecked == true);
            // Refresh only after the model has finished updating all priority ranks, not on CheckBox.Checked
            // (which can precede the two-way binding). Also reset the viewport to the first priority.
            UISettingsManager.Instance.GameWhitelistChanged += _ => QueueGameListRefresh();

            // Reflect the saved language in the combo (the change handler only fires on a real user change).
            string lang = UISettingsManager.Instance.Language;
            foreach (object item in LanguageCombo.Items)
                if (item is System.Windows.Controls.ComboBoxItem cbi && string.Equals(cbi.Tag as string, lang, StringComparison.OrdinalIgnoreCase))
                { LanguageCombo.SelectedItem = cbi; break; }
            if (LanguageCombo.SelectedItem == null)
                LanguageCombo.SelectedIndex = 0;
        }

        private static bool MatchesGameFilter(object item, string? searchText, bool hideExcluded)
        {
            if (item is not GameFilterOption option)
                return false;

            if (hideExcluded && option.IsExcluded)
                return false;

            string query = searchText?.Trim() ?? string.Empty;
            return query.Length == 0 || option.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        }

        private void OnGameSearchTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            _twitchGameView?.Refresh();
            _kickGameView?.Refresh();
        }

        private void OnGameFilterVisibilityChanged(object sender, RoutedEventArgs e)
        {
            QueueGameListRefresh();
        }

        private void QueueGameListRefresh()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(QueueGameListRefresh)); return; }
            if (_gameRefreshPending) return;
            _gameRefreshPending = true;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() =>
            {
                _gameRefreshPending = false;
                _twitchGameView?.Refresh();
                _kickGameView?.Refresh();
                if (TwitchGameList.Items.Count > 0) TwitchGameList.ScrollIntoView(TwitchGameList.Items[0]);
                if (KickGameList.Items.Count > 0) KickGameList.ScrollIntoView(KickGameList.Items[0]);
            }));
        }

        private void OnPriorityUpClick(object sender, RoutedEventArgs e) => MovePriority(sender, -1);
        private void OnGameListResize(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            if (sender is not FrameworkElement element) return;
            var settings = UISettingsManager.Instance;
            if (element.Tag as string == "Twitch") settings.TwitchGameListHeight += e.VerticalChange;
            else if (element.Tag as string == "Kick") settings.KickGameListHeight += e.VerticalChange;
            e.Handled = true;
        }

        private void OnGameListResizeCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
            => Task.Run(UISettingsManager.Instance.SaveSettings);
        private void OnPriorityDownClick(object sender, RoutedEventArgs e) => MovePriority(sender, 1);
        private void MovePriority(object sender, int direction)
        {
            if (sender is FrameworkElement { DataContext: GameFilterOption option })
                UISettingsManager.Instance.MoveGamePriority(option, direction);
            QueueGameListRefresh();
        }

        private void OnLanguageChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (LanguageCombo.SelectedItem is System.Windows.Controls.ComboBoxItem cbi && cbi.Tag is string code)
                UISettingsManager.Instance.Language = code;
        }

        private async void OnUpdateButtonClick(object sender, RoutedEventArgs e)
        {
            if (UISettingsManager.Instance.UpdateAvailable)
                await UpdateManager.Instance.DownloadUpdate(); // Download and apply the update
        }

        private void OnRemoveAllAccountsButtonClick(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("NUKE ALL ACCOUNTS AND RESTART?", "DANGER", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            string mainExe = Process.GetCurrentProcess().MainModule!.FileName;
            string folderToNuke = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Stream Loot.exe.WebView2", "EBWebView", "Default", "Network");

            if (!Directory.Exists(folderToNuke))
            {
                MessageBox.Show("Nothing to nuke.");
                return;
            }

            // PURE CODE - NO EXTERNAL EXE, NO DLL, NO BULLSHIT
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/C timeout /t 5 && rmdir /s /q \"{folderToNuke}\" && start \"\" \"{mainExe}\"",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process.Start(psi);
            ProcessExitTracker.RecordReason("User requested account reset and restart");
            System.Windows.Application.Current.Shutdown();
            Environment.Exit(0);
        }

        private void OnOpenLogsFolderClick(object sender, RoutedEventArgs e)
        {
            try
            {
                AppLogger.Initialize();

                string logsDir = AppLogger.LogDirectoryPath;
                Directory.CreateDirectory(logsDir);

                Process.Start(new ProcessStartInfo
                {
                    FileName = logsDir,
                    UseShellExecute = true
                });

                AppLogger.Info("Settings", $"Opened logs folder: {logsDir}");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Settings", "Failed to open logs folder.", ex);
                MessageBox.Show($"Failed to open logs folder.\n\n{ex.Message}", "Logs", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnClearTwitchWhitelistClick(object sender, RoutedEventArgs e)
        {
            UISettingsManager.Instance.ClearGameWhitelist(Platform.Twitch);
        }

        private void OnClearKickWhitelistClick(object sender, RoutedEventArgs e)
        {
            UISettingsManager.Instance.ClearGameWhitelist(Platform.Kick);
        }

        private void OnClearTwitchBlacklistClick(object sender, RoutedEventArgs e)
        {
            UISettingsManager.Instance.ClearGameBlacklist(Platform.Twitch);
        }

        private void OnClearKickBlacklistClick(object sender, RoutedEventArgs e)
        {
            UISettingsManager.Instance.ClearGameBlacklist(Platform.Kick);
        }
    }
}

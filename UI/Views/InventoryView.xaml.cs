using Core.Managers;
using Core.Models;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Data;

namespace UI.Views
{
    /// <summary>
    /// Interaction logic for InventoryView.xaml
    /// </summary>
    public partial class InventoryView : System.Windows.Controls.UserControl
    {
        private static readonly Lazy<InventoryView> _instance = new(() => new InventoryView());
        public static InventoryView Instance => _instance.Value;

        private readonly ICollectionView _campaignsView;
        private bool _onlyAvailable;
        private bool _hideClaimed;

        private InventoryView()
        {
            InitializeComponent();
            DataContext = DropsInventoryManager.Instance;

            // Filter the shared campaigns view so the "Show only available" toggle can hide campaigns whose
            // listed channels are all offline. Unknown/Category/Available remain visible.
            _campaignsView = CollectionViewSource.GetDefaultView(DropsInventoryManager.Instance.ActiveCampaigns);
            _campaignsView.Filter = item =>
            {
                if (item is not DropsCampaign c)
                    return true;
                if (_onlyAvailable && c.Availability == CampaignAvailability.Unavailable)
                    return false;
                // "Hide claimed": drop campaigns whose rewards are all already claimed (nothing left to earn).
                if (_hideClaimed && c.Rewards.Count > 0 && c.Rewards.All(r => r.IsClaimed))
                    return false;
                return true;
            };

            // Re-check availability whenever the Inventory is opened (throttled inside the manager).
            Loaded += async (_, _) => await DropsInventoryManager.Instance.RefreshAvailabilityAsync();
        }

        private void OnOnlyAvailableChanged(object sender, RoutedEventArgs e)
        {
            _onlyAvailable = OnlyAvailableToggle.IsChecked == true;
            _campaignsView.Refresh();
        }

        private void OnHideClaimedChanged(object sender, RoutedEventArgs e)
        {
            _hideClaimed = HideClaimedToggle.IsChecked == true;
            _campaignsView.Refresh();
        }

        private async void OnRefreshAvailabilityClick(object sender, RoutedEventArgs e)
        {
            await DropsInventoryManager.Instance.RefreshAvailabilityAsync(force: true);
            _campaignsView.Refresh();
        }

        private async void OnMineThisClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is DropsCampaign campaign)
            {
                // Toggle: clicking the already-pinned campaign unpins it (back to automatic selection).
                await DropsInventoryManager.Instance.SetForcedCampaignAsync(campaign.IsPinned ? null : campaign.Id);
                _campaignsView.Refresh();
            }
        }

        private async void OnAutoMineClick(object sender, RoutedEventArgs e)
            => await DropsInventoryManager.Instance.SetForcedCampaignAsync(null);
    }
}
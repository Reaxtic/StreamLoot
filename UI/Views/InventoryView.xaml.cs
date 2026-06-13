using Core.Managers;
using Core.Models;
using System.Windows;

namespace UI.Views
{
    /// <summary>
    /// Interaction logic for InventoryView.xaml
    /// </summary>
    public partial class InventoryView : System.Windows.Controls.UserControl
    {
        private static readonly Lazy<InventoryView> _instance = new(() => new InventoryView());
        public static InventoryView Instance => _instance.Value;

        private InventoryView()
        {
            InitializeComponent();
            DataContext = DropsInventoryManager.Instance;
        }

        private async void OnMineThisClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is DropsCampaign campaign)
                await DropsInventoryManager.Instance.SetForcedCampaignAsync(campaign.Id);
        }

        private async void OnAutoMineClick(object sender, RoutedEventArgs e)
            => await DropsInventoryManager.Instance.SetForcedCampaignAsync(null);
    }
}
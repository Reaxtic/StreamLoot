using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Core.Managers;

namespace UI.Views
{
    /// <summary>
    /// Mining statistics: watched minutes (today / 7 days / total) and the claimed-drops history.
    /// </summary>
    public partial class StatisticsView : System.Windows.Controls.UserControl, INotifyPropertyChanged
    {
        private static readonly Lazy<StatisticsView> _instance = new(() => new StatisticsView());
        public static StatisticsView Instance => _instance.Value;

        public event PropertyChangedEventHandler? PropertyChanged;

        public ObservableCollection<ClaimedDropRecord> Claims { get; } = new();

        public string WatchedTodayText => FormatMinutes(StatsManager.Instance.MinutesToday);
        public string Watched7DaysText => FormatMinutes(StatsManager.Instance.MinutesLast7Days);
        public string WatchedTotalText => FormatMinutes(StatsManager.Instance.MinutesTotal);
        public string ClaimedCountText => StatsManager.Instance.TotalClaimed.ToString();
        public Visibility NoClaimsVisibility => StatsManager.Instance.TotalClaimed == 0 ? Visibility.Visible : Visibility.Collapsed;

        private StatisticsView()
        {
            InitializeComponent();
            DataContext = this;

            Loaded += (_, _) => Refresh();
            StatsManager.Instance.StatsChanged += () =>
                System.Windows.Application.Current.Dispatcher.InvokeAsync(Refresh);
        }

        private void Refresh()
        {
            OnPropertyChanged(nameof(WatchedTodayText));
            OnPropertyChanged(nameof(Watched7DaysText));
            OnPropertyChanged(nameof(WatchedTotalText));
            OnPropertyChanged(nameof(ClaimedCountText));
            OnPropertyChanged(nameof(NoClaimsVisibility));

            Claims.Clear();
            foreach (ClaimedDropRecord record in StatsManager.Instance.ClaimHistory.Take(100))
                Claims.Add(record);
        }

        private static string FormatMinutes(int minutes) =>
            minutes >= 60 ? $"{minutes / 60}h {minutes % 60}m" : $"{minutes}m";

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

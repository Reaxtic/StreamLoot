using System.Windows;

namespace UI.Views
{
    /// <summary>
    /// Interaction logic for TwitchLoginWindow.xaml
    /// </summary>
    public partial class TwitchLoginWindow : Window
    {
        public TwitchLoginWindow()
        {
            InitializeComponent();
            Initialize();
        }

        private async void Initialize()
        {
            // Keep the genuine WebView2 (Edge) User-Agent — Twitch supports it. Overriding it to a fake Chrome UA
            // mismatches the real browser fingerprint and makes Twitch flag the browser as unsupported.
            await Web.EnsureCoreWebView2Async();

            Web.Source = new Uri("https://twitch.tv/login");
        }
    }
}
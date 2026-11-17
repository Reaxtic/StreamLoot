using Core.Enums;
using Core.Managers;
using Core.Models;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;

// Render the actual view markup with illustrative data. No app startup, user profile,
// login, browser, mining engine, clipboard access or network image URLs are used.
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        string root = Path.GetFullPath(args[0]);
        Environment.SetEnvironmentVariable("APPDATA", Path.Combine(AppContext.BaseDirectory, "demo-profile"));
        string output = Path.Combine(root, "Github-Assets");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Loc.Instance.SetLanguage("en");
        var colors = Load(Path.Combine(root, "UI/Themes/Colors.xaml"));
        app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(colors.ToString()));
        var dark = Load(Path.Combine(root, "UI/Themes/Dark.xaml"));
        dark.Descendants().Where(e => e.Name.LocalName == "ResourceDictionary.MergedDictionaries").Remove();
        app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(dark.ToString()));
        var appMarkup = Load(Path.Combine(root, "UI/App.xaml"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = new XElement(ns + "ResourceDictionary", appMarkup.Attributes().Where(a => a.IsNamespaceDeclaration),
            appMarkup.Elements().Single(e => e.Name.LocalName == "Application.Resources").Elements());
        app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(resources.ToString()));
        string logo = Path.Combine(output, "logo.png");
        DropsCampaign Campaign(string id, string game, Platform platform, int progress, int required, bool pinned = false) =>
            new(id, "Example campaign", game.ToLowerInvariant(), game, logo,
                new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero),
                [new DropsReward(id + "-drop", "Example reward", logo, required, progress, IsCurrentReward: pinned)], platform, [], true,
                IsCurrentCampaign: pinned, Availability: CampaignAvailability.Available, OnlineChannels: 4,
                IsPinned: pinned, PinOrder: pinned ? 1 : 0, GamePriorityOrder: 1);
        var inventory = View("InventoryView", new { ActiveCampaigns = new[] {
            Campaign("demo-twitch", "Rust", Platform.Twitch, 42, 60, true),
            Campaign("demo-kick", "World of Warcraft", Platform.Kick, 72, 120),
            Campaign("demo-other", "AirMech", Platform.Twitch, 15, 90)
        } });
        ((ListBox)inventory.FindName("CampaignsList")).ItemsSource = ((dynamic)inventory.DataContext).ActiveCampaigns;
        Render(inventory, "Inventory", "inventory-current.png");
        var dashboard = View("DashboardView", new DemoDashboard());
        Render(dashboard, "Dashboard", "dashboard-current.png");
        app.Shutdown();
        Console.WriteLine("Rendered current XAML with illustrative data only.");

        FrameworkElement View(string name, object context)
        {
            var markup = Load(Path.Combine(root, "UI/Views/" + name + ".xaml"));
            var result = (FrameworkElement)XamlReader.Parse(markup.ToString());
            result.DataContext = context;
            return result;
        }
        void Render(FrameworkElement view, string section, string file)
        {
            var layout = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(14, 16, 20)), Width = 1440, Height = 850 };
            var header = new Border { Background = new SolidColorBrush(Color.FromRgb(20, 24, 30)), Padding = new Thickness(24, 14, 24, 14) };
            header.Child = new TextBlock { Text = "Stream Loot  ·  " + section + "     |     Illustrative demo data — no real account", Foreground = Brushes.White, FontSize = 16 };
            DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
            layout.Children.Add(view);
            var window = new Window { Content = layout, Width = 1440, Height = 850, WindowStyle = WindowStyle.None,
                Left = -30000, Top = -30000, ShowInTaskbar = false, ShowActivated = false };
            window.Show(); window.UpdateLayout();
            for (int i = 0; i < 5; i++) Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            void AddPlaceholderArt(DependencyObject node)
            {
                if (node is Image image) image.Source = new BitmapImage(new Uri(logo));
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) AddPlaceholderArt(VisualTreeHelper.GetChild(node, i));
            }
            AddPlaceholderArt(layout);
            // Let the real progress-bar animations settle before the static capture.
            var settledAt = DateTime.UtcNow.AddSeconds(1.2);
            while (DateTime.UtcNow < settledAt)
            {
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                Thread.Sleep(25);
            }
            layout.Measure(new Size(1440, 850)); layout.Arrange(new Rect(0, 0, 1440, 850)); layout.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1440, 850, 96, 96, PixelFormats.Pbgra32); bitmap.Render(layout);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(output, file))) encoder.Save(stream);
            window.Close();
        }
    }
    private static XElement Load(string file)
    {
        var xml = XElement.Load(file);
        foreach (var element in xml.DescendantsAndSelf())
            if (element.Name.NamespaceName.StartsWith("clr-namespace:UI") && !element.Name.NamespaceName.Contains("assembly="))
                element.Name = XName.Get(element.Name.LocalName, element.Name.NamespaceName + ";assembly=Stream Loot");
        var handlers = new HashSet<string> { "Click", "Loaded", "Unloaded", "Checked", "Unchecked", "SelectionChanged", "DragDelta", "DragCompleted", "MouseLeftButtonDown", "PreviewMouseLeftButtonDown", "RequestNavigate" };
        foreach (var attr in xml.DescendantsAndSelf().Attributes().ToArray())
        {
            if (attr.Name.LocalName == "Class" || handlers.Contains(attr.Name.LocalName)) attr.Remove();
            else if (attr.IsNamespaceDeclaration && attr.Value.StartsWith("clr-namespace:UI") && !attr.Value.Contains("assembly=")) attr.Value += ";assembly=Stream Loot";
            else if (attr.Name.NamespaceName.StartsWith("clr-namespace:UI") && !attr.Name.NamespaceName.Contains("assembly="))
            {
                attr.Parent!.SetAttributeValue(XName.Get(attr.Name.LocalName, attr.Name.NamespaceName + ";assembly=Stream Loot"), attr.Value);
                attr.Remove();
            }
        }
        return xml;
    }
}

public sealed class DemoDashboard
{
    public string MinerStatus => "Mining";
    public string MinerStatusDetails => "Twitch and Kick running independently • illustrative data";
    public string TwitchConnectionStatus => "Connected";
    public string KickConnectionStatus => "Connected";
    public string TwitchConnectionColor => "#00FF66";
    public string KickConnectionColor => "#00FF66";
    public Visibility TwitchAuthorizationVisibility => Visibility.Collapsed;
    public string TwitchCampaignName => "Rust — example campaign";
    public string KickCampaignName => "World of Warcraft — example campaign";
    public int TwitchCampaignProgress => 70;
    public int KickCampaignProgress => 60;
    public string TwitchDropName => "Example reward";
    public string KickDropName => "Example reward";
    public int TwitchDropProgress => 70;
    public int KickDropProgress => 60;
    public string TwitchWatchedChannel { get; set; } = "demo_twitch_channel";
    public string KickWatchedChannel { get; set; } = "demo_kick_channel";
    public string TwitchChannelsStatus => "Example eligible channels";
    public string KickChannelsStatus => "Example eligible channels";
    public ChannelCandidate[] TwitchChannels => [new("demo_twitch_channel", "", true, 120)];
    public ChannelCandidate[] KickChannels => [new("demo_kick_channel", "", true, 80)];
    public CoMiningCampaign[] TwitchAlsoMining => [];
    public CoMiningCampaign[] KickAlsoMining => [];
}

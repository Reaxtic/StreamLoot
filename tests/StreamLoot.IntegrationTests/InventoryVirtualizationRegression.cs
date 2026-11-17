using Core.Enums;
using Core.Managers;
using Core.Models;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UI.Views;

static class InventoryVirtualizationRegression
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Application? app = null;
            Window? window = null;
            try
            {
                void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
                var manager = (DropsInventoryManager)RuntimeHelpers.GetUninitializedObject(typeof(DropsInventoryManager));
                var rows = new ObservableCollection<DropsCampaign>(Enumerable.Range(0, 150).Select(i =>
                    new DropsCampaign("ui-" + i, "Test campaign " + i, "game", "Test game", null,
                        DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1),
                        Enumerable.Range(0, 1 + i % 4).Select(r => new DropsReward("r-" + i + "-" + r, "Reward", null, 60, 0, i % 3 == 0)).ToArray(),
                        i % 2 == 0 ? Platform.Twitch : Platform.Kick, Array.Empty<string>(), true, Availability: i % 4 == 0 ? CampaignAvailability.Unavailable : CampaignAvailability.Available)));
                typeof(DropsInventoryManager).GetField("<ActiveCampaigns>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(manager, rows);
                // Loaded availability refresh is intentionally suppressed; this test owns no browser/network.
                typeof(DropsInventoryManager).GetField("_lastAvailabilityRefresh", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(manager, DateTime.MaxValue);
                var lazy = (Lazy<DropsInventoryManager>)typeof(DropsInventoryManager).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
                typeof(Lazy<DropsInventoryManager>).GetField("_factory", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(lazy, new Func<DropsInventoryManager>(() => manager));
                _ = lazy.Value;
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources["AccentButtonStyle"] = new Style(typeof(Button));
                app.Resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
                var view = InventoryView.Instance;
                window = new Window { Content = view, Width = 1400, Height = 800, Left = -30000, Top = -30000, ShowInTaskbar = false, ShowActivated = false };
                window.Show();
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                window.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                var list = (ListBox)view.FindName("CampaignsList");
                int Realized() => Enumerable.Range(0, list.Items.Count).Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) != null);
                Check(VirtualizingPanel.GetIsVirtualizing(list) && VirtualizingPanel.GetVirtualizationMode(list) == VirtualizationMode.Recycling,
                    "actual inventory enables virtualization and recycling");
                int initial = Realized();
                Check(initial > 0 && initial < 30, $"actual inventory realizes {initial} cards rather than all {rows.Count}");
                list.ScrollIntoView(rows[100]);
                window.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                window.UpdateLayout();
                Check(list.ItemContainerGenerator.ContainerFromIndex(100) != null && Realized() < 30,
                    "scrolling variable-height cards keeps a bounded realized set");
                Check(list.ItemContainerGenerator.ContainerFromIndex(0) == null, "off-screen inventory cards are recycled");
                ((CheckBox)view.FindName("HideClaimedToggle")).IsChecked = true;
                Check(list.Items.Count == rows.Count(c => c.Rewards.Any(r => !r.IsClaimed)), "hide-claimed filter survives virtualization");
                ((CheckBox)view.FindName("OnlyAvailableToggle")).IsChecked = true;
                Check(list.Items.Count == rows.Count(c => c.Rewards.Any(r => !r.IsClaimed) && c.Availability != CampaignAvailability.Unavailable),
                    "available-campaign filter survives virtualization");
                Check(rows.Count == 150, "inventory filters do not delete campaigns or change exclusions");
                var hideTwitch = (Button)view.FindName("HideTwitchButton");
                var hideKick = (Button)view.FindName("HideKickButton");
                hideTwitch.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check((string)hideTwitch.Content == Loc.Instance["Inv.ShowTwitch"], "hidden platform button changes to localized Show label");
                Check(list.Items.Cast<DropsCampaign>().All(c => c.Platform == Platform.Kick) && list.Items.Count > 0,
                    "hide Twitch button filters only Twitch and composes with existing inventory filters");
                hideKick.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(list.Items.Count == 0 && rows.Count == 150, "both platform buttons may hide all rows without deleting mining campaigns");
                hideTwitch.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(list.Items.Cast<DropsCampaign>().All(c => c.Platform == Platform.Twitch) && list.Items.Count > 0,
                    "show Twitch restores Twitch while Kick remains hidden");
                hideKick.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(list.Items.Count == rows.Count(c => c.Rewards.Any(r => !r.IsClaimed) && c.Availability != CampaignAvailability.Unavailable),
                    "show both platforms restores the filtered inventory");
                Check(System.Windows.Data.CollectionViewSource.GetDefaultView(rows).Filter == null,
                    "platform filters do not modify the shared collection view or mining source");
                var settings = UISettingsManager.Instance;
                ((List<string>)typeof(UISettingsManager).GetField("_twitchGameWhitelistSlugs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(settings)!).Add("game");
                settings.UpdateAvailableGameFilterOptions(rows);
                var sample = list.Items.Cast<DropsCampaign>().First(c => c.Platform == Platform.Twitch);
                list.ScrollIntoView(sample);
                window.UpdateLayout();
                T? FindNamed<T>(DependencyObject parent, string name) where T : FrameworkElement
                {
                    if (parent is T element && element.Name == name) return element;
                    for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
                    {
                        var found = FindNamed<T>(System.Windows.Media.VisualTreeHelper.GetChild(parent, i), name);
                        if (found != null) return found;
                    }
                    return null;
                }
                var card = (DependencyObject)list.ItemContainerGenerator.ContainerFromItem(sample);
                var gameNameText = FindNamed<TextBox>(card, "GameNameText")!;
                Check(gameNameText.IsReadOnly && gameNameText.Text == sample.GameName,
                    "inventory game title is selectable read-only text, distinct from the campaign name");
                gameNameText.SelectAll();
                Check(gameNameText.SelectedText == sample.GameName, "full game name can be selected for Ctrl+C");
                var copyButton = FindNamed<Button>(card, "CopyGameNameButton")!;
                var clipboardField = typeof(InventoryView).GetField("_copyGameNameToClipboard", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var originalCopy = clipboardField.GetValue(view);
                string? copiedName = null;
                try
                {
                    clipboardField.SetValue(view, new Action<string>(text => copiedName = text));
                    copyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(copiedName == sample.GameName && copiedName != sample.Name,
                        "real inventory Copy button copies game name without modifying the system clipboard during tests");
                    Check(rows.Count == 150 && settings.GamePriority(sample) == 0 && settings.IsCampaignAllowedByWhitelist(sample),
                        "copying a game name preserves campaign source, priorities and exclusions");
                    clipboardField.SetValue(view, new Action<string>(_ => throw new System.Runtime.InteropServices.ExternalException("Clipboard busy")));
                    copyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var waitUntil = DateTime.UtcNow.AddSeconds(2);
                    while (DateTime.UtcNow < waitUntil && ((TextBlock)view.FindName("ExclusionNotice")).Text != Loc.Instance["Inv.CopyFailed"])
                    {
                        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                        Thread.Sleep(20);
                    }
                    Check(((TextBlock)view.FindName("ExclusionNotice")).Text == Loc.Instance["Inv.CopyFailed"],
                        "busy clipboard produces a bounded retry and visible error rather than crashing");
                }
                finally { clipboardField.SetValue(view, originalCopy); }
                Button? ExcludeButton(DependencyObject parent)
                {
                    if (parent is Button b && b.Name == "ExcludeGameButton") return b;
                    for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
                    {
                        var result = ExcludeButton(System.Windows.Media.VisualTreeHelper.GetChild(parent, i));
                        if (result != null) return result;
                    }
                    return null;
                }
                var exclude = ExcludeButton((DependencyObject)list.ItemContainerGenerator.ContainerFromItem(sample))!;
                Check(exclude != null, "actual inventory campaign renders Exclude game button");
                var events = new List<Platform>();
                settings.GameWhitelistChanged += events.Add;
                settings.SaveSettings(); // Clean baseline before testing the asynchronous checkbox save path.
                exclude!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(settings.TwitchGameFilterOptions.Single(o => o.Slug == "game").IsExcluded,
                    "inventory button immediately checks the same exclusion option used by Settings");
                Check(!settings.IsCampaignAllowedByWhitelist(sample) && !settings.IsCampaignAllowedByWhitelist(sample with { Id = "another-campaign" }),
                    "one-click exclusion blocks all campaigns for the selected game");
                Check(settings.IsCampaignAllowedByWhitelist(sample with { Platform = Platform.Kick }), "inventory exclusion does not affect the other platform");
                Check(settings.GamePriority(sample) == 0 && events.SequenceEqual(new[] { Platform.Twitch }),
                    "inventory exclusion preserves priority and emits one platform-scoped mining update");
                exclude.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(events.Count == 1, "repeated exclusion is idempotent");
                bool persisted = false;
                for (int attempt = 0; attempt < 80 && !persisted; attempt++)
                {
                    try
                    {
                        // Observe the asynchronous save without denying its writer access to the file.
                        using var stream = new System.IO.FileStream(System.IO.Path.Combine(Environment.GetEnvironmentVariable("APPDATA")!, "Stream Loot", "Settings.json"),
                            System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
                        using var saved = System.Text.Json.JsonDocument.Parse(stream);
                        persisted = saved.RootElement.GetProperty("TwitchGameBlacklistSlugs").EnumerateArray().Any(x => x.GetString() == "game");
                    }
                    catch (System.IO.IOException) { }
                    catch (System.Text.Json.JsonException) { }
                    if (!persisted) Thread.Sleep(25);
                }
                Check(persisted, "inventory exclusion is automatically persisted in isolated Settings.json");
                settings.TwitchGameFilterOptions.Single(o => o.Slug == "game").IsExcluded = false;
                Check(settings.IsCampaignAllowedByWhitelist(sample), "unchecking exclusion in Settings restores eligibility");
                Check(!settings.SetGameExcluded(sample with { Slug = "" }, true), "category-less campaign cannot silently exclude an unrelated game");
                settings.GameWhitelistChanged -= events.Add;
                window.Width = 1000;
                window.UpdateLayout();
                foreach (var button in new[] { hideTwitch, hideKick })
                {
                    var point = button.TransformToAncestor(view).Transform(new Point());
                    Check(point.X >= 0 && point.X + button.ActualWidth <= view.ActualWidth,
                        "platform button remains inside inventory at narrow window width");
                }
                typeof(DropsInventoryManager).GetMethod("ApplyConfirmedAvailability", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(manager, new object?[] { rows[0].Id, true });
                Check(rows[0].Availability == CampaignAvailability.Available && rows[0].OnlineChannels >= 1,
                    "real manager updates stale offline inventory row after a confirmed live health check");
                BrowserRecoveryRegression.Run();
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); app?.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(90))) throw new TimeoutException("Inventory/browser test did not finish.");
        if (failure != null) throw new Exception("Inventory UI regression failed.", failure);
    }
}

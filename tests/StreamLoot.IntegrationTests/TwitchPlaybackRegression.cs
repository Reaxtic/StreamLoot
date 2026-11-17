using Core.Enums;
using Core.Managers;
using Core.Models;
using Core.Services;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

static class TwitchPlaybackRegression
{
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
    static void Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
    public static void Run()
    {
        const string channel = "test-channel";
        var now = DateTime.Now;
        var playing = new TwitchPlaybackSnapshot("https://www.twitch.tv/test-channel?forceReload=test", true, false, false, 4);
        Check(TwitchPlaybackPolicy.CanObserveStall(playing, channel), "selected live page with playing video can be observed for campaign stalls");
        foreach (var url in new[] { "https://www.twitch.tv/drops/campaigns", "https://www.twitch.tv/drops/inventory",
            "https://www.twitch.tv/other-channel", "https://www.twitch.tv/test-channel/videos", "https://twitch.tv.evil.invalid/test-channel" })
            Check(!TwitchPlaybackPolicy.CanObserveStall(playing with { Url = url }, channel), "dashboard, other pages and VOD cannot count as the selected live player");
        Check(TwitchPlaybackPolicy.Parse(JsonSerializer.Serialize("{\"url\":\"https://www.twitch.tv/test-channel\",\"hasVideo\":true,\"paused\":false,\"ended\":false,\"readyState\":4}"))?.IsPlaying == true,
            "WebView2 double-encoded player snapshot is read correctly");
        Check(TwitchPlaybackPolicy.Parse("null") == null && TwitchPlaybackPolicy.Parse("bad JSON") == null,
            "unreadable player state remains unknown");
        Check(TwitchPlaybackPolicy.ShouldRecover(playing with { Url = "https://www.twitch.tv/drops/campaigns" }, channel, now, now, DateTime.MinValue),
            "wrong local page is recovered immediately without changing the campaign");
        Check(!TwitchPlaybackPolicy.ShouldRecover(playing with { Paused = true }, channel, now, now, DateTime.MinValue),
            "initial buffering or pause gets a one-minute grace");
        Check(TwitchPlaybackPolicy.ShouldRecover(playing with { Paused = true }, channel, now.AddMinutes(-1), now, DateTime.MinValue),
            "persistent paused player can be recovered");
        Check(!TwitchPlaybackPolicy.ShouldRecover(playing with { Paused = true }, channel, now.AddMinutes(-5), now, now.AddSeconds(-60)),
            "local recovery cannot reload the player every health tick");
        Check(!TwitchPlaybackPolicy.ShouldRecover(null, channel, now.AddMinutes(-5), now, DateTime.MinValue),
            "an unknown state cannot trigger blind recovery");

        var reward = new DropsReward("reward", "reward", null, 180, 93);
        var campaign = new DropsCampaign("campaign", "campaign", "game", "game", null,
            DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1), new[] { reward }, Platform.Twitch, Array.Empty<string>(), true);
        Check(ReferenceEquals(campaign, CampaignRewardUpdatePolicy.Apply(campaign, new[] { reward with { } })),
            "unchanged server minutes retain the existing inventory row and visual tree");
        Check(!ReferenceEquals(campaign, CampaignRewardUpdatePolicy.Apply(campaign, new[] { reward with { ProgressMinutes = 94 } })),
            "new server minutes update their inventory row");
        Check(CampaignRewardUpdatePolicy.Apply(campaign, new[] { reward with { IsClaimed = true } }).Rewards[0].IsClaimed,
            "confirmed claims remain visible after differential updates");

        var miner = (DropsInventoryManager)RuntimeHelpers.GetUninitializedObject(typeof(DropsInventoryManager));
        var samples = new Dictionary<string, (int Minutes, int FrozenCount, DateTime LastSampleAt)> { [campaign.Id] = (93, 1, now.AddMinutes(-4)) };
        var policy = new TwitchCampaignRetryPolicy();
        Set(miner, "_twitchCampaignRetry", policy);
        Set(miner, "_twitchConnectionRecovery", new TwitchConnectionRecoveryPolicy());
        Set(miner, "_creditSync", new object());
        Set(miner, "_creditTracking", samples);
        Set(miner, "_notCreditingCampaignIds", new HashSet<string>());
        Set(miner, "_pinnedQueue", new List<string>());
        Set(miner, "<ActiveCampaigns>k__BackingField", new ObservableCollection<DropsCampaign> { campaign });
        Set(miner, "_currentTwitchLogin", channel);
        Set(miner, "_twitchPlayerCanObserveStall", false);
        typeof(DropsInventoryManager).GetMethod("TrackCampaignCrediting", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(miner, new object[] { campaign.Id, Platform.Twitch, 93 });
        Check(!samples.ContainsKey(campaign.Id) && !policy.IsChannelStalled(campaign.Id, channel, now),
            "local playback failure clears stale samples without penalizing the streamer");
    }
}

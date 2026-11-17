using Core.Services;

static class PlaybackRecoveryRegression
{
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
    public static void Run()
    {
        var liveAvailability = CampaignAvailabilityPolicy.Resolve(Core.Models.CampaignAvailability.Unavailable, 0, true);
        Check(liveAvailability.State == Core.Models.CampaignAvailability.Available && liveAvailability.Online == 1,
            "confirmed watched channel immediately replaces stale offline inventory badge");
        Check(CampaignAvailabilityPolicy.Resolve(Core.Models.CampaignAvailability.Available, 3, true).Online == 3,
            "confirmed channel does not erase known additional live streamers");
        Check(CampaignAvailabilityPolicy.Kick(new[] { "channel" }, new Dictionary<string, int>()).State == Core.Models.CampaignAvailability.Unknown,
            "missing Kick status is unknown rather than no live streamers");
        Check(CampaignAvailabilityPolicy.Kick(new[] { "channel" }, new Dictionary<string, int> { ["channel"] = -2 }).State == Core.Models.CampaignAvailability.Unknown,
            "Kick HTTP failure is unknown rather than offline");
        Check(CampaignAvailabilityPolicy.Kick(new[] { "channel" }, new Dictionary<string, int> { ["channel"] = -1 }).State == Core.Models.CampaignAvailability.Unavailable,
            "confirmed offline Kick channel remains unavailable");
        Check(!KickStreamSelectionPolicy.CategoryMatches("rust", "just-sleeping"), "listed online Kick channel in wrong category is not drop-eligible");
        Check(KickStreamSelectionPolicy.CategoryMatches("rust", "Rust"), "correct Kick category is case-insensitive");
        Check(KickStreamSelectionPolicy.CategoryMatches("", "just-sleeping"), "category-less Kick campaigns remain valid in any category");
        Check(!KickStreamSelectionPolicy.CategoryMatches("rust", null), "unknown category cannot be proof of Rust eligibility");
        foreach (string domain in new[] { "twitch.tv", "kick.com" })
        {
            var recovery = new PlaybackRecoveryState();
            DateTime now = new(2026, 10, 16, 10, 0, 0);
            var paused = new TwitchPlaybackSnapshot("https://" + domain + "/channel", true, true, false, 0, 0);
            Check(recovery.Observe(paused, "channel", domain, now) == PlaybackRepair.None, domain + " buffering grace");
            Check(recovery.Observe(paused, "channel", domain, now.AddMinutes(1)) == PlaybackRepair.Reload, domain + " first repair");
            Check(recovery.Observe(paused, "channel", domain, now.AddSeconds(90)) == PlaybackRepair.None, domain + " reload throttled");
            Check(recovery.Observe(paused, "channel", domain, now.AddMinutes(3)) == PlaybackRepair.Reload, domain + " second repair");
            Check(recovery.Observe(paused, "channel", domain, now.AddMinutes(5)) == PlaybackRepair.Recreate, domain + " dead controller escalates to recreation");
            recovery.Reset();
            Check(recovery.Observe(paused with { ReadyState = 4 }, "channel", domain, now) == PlaybackRepair.Reload,
                domain + " loaded but paused media resumes without buffering grace");
            recovery.Reset();
            recovery.Observe(null, "channel", domain, now);
            Check(recovery.Observe(null, "channel", domain, now.AddMinutes(1)) == PlaybackRepair.Reload, domain + " repeated unreadable state cannot stall forever");
            recovery.Reset();
            var playing = paused with { Paused = false, ReadyState = 4, CurrentTime = 10 };
            recovery.Observe(playing, "channel", domain, now);
            Check(recovery.Healthy, domain + " real player initially accepted");
            Check(recovery.Observe(playing, "channel", domain, now.AddMinutes(1)) == PlaybackRepair.Reload && !recovery.Healthy,
                domain + " readyState playing but frozen position repaired");
            recovery.Reset();
            for (int i = 0; i < 10; i++)
                Check(recovery.Observe(playing with { CurrentTime = 10 + i * 30 }, "channel", domain, now.AddSeconds(i * 30)) == PlaybackRepair.None && recovery.Healthy,
                    domain + " advancing media does not trigger repair");
            Check(recovery.Observe(playing with { Url = "https://" + domain + "/other" }, "channel", domain, now.AddMinutes(6)) == PlaybackRepair.Reload,
                domain + " wrong channel repaired immediately");
            recovery.Reset();
            Check(recovery.Observe(paused, "new-channel", domain, now) == PlaybackRepair.Reload, domain + " wrong page after resume is not retained");
        }
    }
}

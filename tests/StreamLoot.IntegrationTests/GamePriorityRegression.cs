using Core.Enums;
using Core.Interfaces;
using Core.Managers;
using Core.Models;
using Core.Services;
using System.Reflection;
using System.Runtime.CompilerServices;

static class GamePriorityRegression
{
    public static async Task Run()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
        var settings = UISettingsManager.Instance;
        var ranks = (List<string>)typeof(UISettingsManager).GetField("_twitchGameWhitelistSlugs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(settings)!;
        var excluded = (List<string>)typeof(UISettingsManager).GetField("_twitchGameBlacklistSlugs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(settings)!;
        var oldRanks = ranks.ToArray(); var oldExcluded = excluded.ToArray();
        try
        {
            ranks.Clear(); ranks.AddRange(["rust", "the-quinfall", "delta-force"]);
            excluded.Clear();
            DropsCampaign Campaign(string slug, int progress = 0) => new(slug, slug, slug, slug, null,
                DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1),
                [new DropsReward(slug + "-reward", "Reward", null, 600, progress)], Platform.Twitch, [], true);
            var lineage = Campaign("lineage-ii", 599);
            var rust = Campaign("rust"); var quinfall = Campaign("the-quinfall"); var delta = Campaign("delta-force");
            var campaigns = new List<DropsCampaign> { lineage, rust, quinfall, delta };
            var pins = new List<string>(); var stalled = new HashSet<string>();
            var manager = (DropsInventoryManager)RuntimeHelpers.GetUninitializedObject(typeof(DropsInventoryManager));
            Set(manager, "_creditSync", new object()); Set(manager, "_campaignSnapshotSync", new object());
            Set(manager, "_notCreditingCampaignIds", stalled); Set(manager, "_twitchCampaignRetry", new TwitchCampaignRetryPolicy());
            Set(manager, "_pinnedQueue", pins); Set(manager, "_lastKnownCampaigns", campaigns);
            var gql = DispatchProxy.Create<IGqlService, PriorityDirectoryProxy>();
            var directory = (PriorityDirectoryProxy)(object)gql;
            directory.Live.UnionWith(["the-quinfall", "delta-force"]);
            Set(manager, "_twitchGqlService", gql);
            async Task<DropsCampaign?> Higher(DropsCampaign current, List<DropsCampaign>? source = null) =>
                await (Task<DropsCampaign?>)typeof(DropsInventoryManager).GetMethod("FindHigherPriorityGameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(manager, [Platform.Twitch, current, source ?? campaigns])!;
            Check((await Higher(lineage))?.Id == quinfall.Id, "real manager preempts nearly completed Lineage II with available Quinfall, skipping offline Rust");
            directory.Live.Add("rust");
            Check((await Higher(quinfall))?.Id == rust.Id, "higher available game resumes ahead of lower priority");
            directory.Live.Remove("rust");
            Check(await Higher(quinfall) == null, "same game rank and lower priorities do not interrupt current campaign");
            pins.Add(lineage.Id);
            Check(await Higher(lineage) == null, "manual campaign pin outranks game priority");
            Set(manager, "_twitchPinSuspended", true);
            Check((await Higher(lineage))?.Id == quinfall.Id, "suspended offline pin allows available priority fallback");
            Set(manager, "_twitchPinSuspended", false);
            excluded.Add(lineage.Slug);
            Check((await Higher(lineage))?.Id == quinfall.Id, "excluded pin cannot block an allowed priority");
            var selected = await (Task<DropsCampaign?>)typeof(DropsInventoryManager).GetMethod("SelectBestCampaign", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(manager, [new List<DropsCampaign> { lineage, quinfall }])!;
            Check(selected?.Id == quinfall.Id, "real selector applies exclusions before manual pins");
            excluded.Add(quinfall.Slug);
            Check((await Higher(lineage))?.Id == delta.Id, "excluded higher game is skipped");
            excluded.Clear(); pins.Clear(); stalled.Add(quinfall.Id);
            Check((await Higher(lineage))?.Id == delta.Id, "non-crediting campaign cooldown permits next available priority");
            stalled.Clear();
            Check(await Higher(lineage, [quinfall with { EndsAt = DateTimeOffset.Now.AddDays(-1) }]) == null, "ended campaign never preempts");
            Check(await Higher(lineage, [quinfall with { StartsAt = DateTimeOffset.Now.AddDays(1) }]) == null, "future campaign never preempts");
            Check(await Higher(lineage, [quinfall with { Platform = Platform.Kick }]) == null, "Kick priority cannot preempt Twitch selection");
            directory.Live.Clear();
            Check(await Higher(lineage) == null, "no available priority preserves healthy current stream");
            var badge = quinfall with { GamePriorityOrder = 2 };
            Check(badge.GamePriorityOrder == 2 && !badge.IsPinned && badge.PinOrder == 0 && pins.Count == 0,
                "game priority badge does not manufacture campaign pins");
        }
        finally { ranks.Clear(); ranks.AddRange(oldRanks); excluded.Clear(); excluded.AddRange(oldExcluded); }
    }
}

public class PriorityDirectoryProxy : DispatchProxy
{
    public HashSet<string> Live { get; } = new();
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name == "QueryLiveDirectoryChannelsAsync")
            return Task.FromResult(Live.Contains((string)args![0]!) ? new List<(string Login, int Viewers)> { ("eligible-channel", 20) } : []);
        throw new InvalidOperationException("Unexpected network call: " + method?.Name);
    }
}

using Core.Services;
using Core.Managers;
using Core.Models;
using Core.Enums;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Net;
using System.Net.Http;
using System.Collections.ObjectModel;

// Tests use fake HTTP and do not instantiate application singletons or change the user's startup entry.
void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
void Set(object target, string field, object value) => target.GetType()
    .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
TwitchGqlService Service(bool fail)
{
    var service = (TwitchGqlService)RuntimeHelpers.GetUninitializedObject(typeof(TwitchGqlService));
    Set(service, "_clientId", "test-client");
    Set(service, "_integrityToken", "test-integrity");
    Set(service, "_httpClient", new HttpClient(new FakeDirectoryHandler(fail)));
    return service;
}
bool failed = false;
try { await Service(true).QueryLiveDirectoryChannelsAsync("test-game", throwOnFailure: true); }
catch (HttpRequestException) { failed = true; }
Check(failed, "strict eligibility preserves transport failure as unknown rather than empty directory");
Check((await Service(true).QueryLiveDirectoryChannelsAsync("test-game")).Count == 0, "legacy directory callers retain bounded fallback");
Check((await Service(false).QueryLiveDirectoryChannelsAsync("test-game", throwOnFailure: true)).Count == 0, "successful empty directory remains genuinely unavailable");

var miner = (DropsInventoryManager)RuntimeHelpers.GetUninitializedObject(typeof(DropsInventoryManager));
var samples = new Dictionary<string, (int Minutes, int FrozenCount, DateTime LastSampleAt)>();
var policy = new TwitchCampaignRetryPolicy();
var campaign = new DropsCampaign("test-campaign", "test-campaign", "test-game", "test-game", null,
    DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1),
    new[] { new DropsReward("test-reward", "test-reward", null, 180, 93) }, Platform.Twitch, Array.Empty<string>(), true);
Set(miner, "_twitchCampaignRetry", policy);
Set(miner, "_twitchConnectionRecovery", new TwitchConnectionRecoveryPolicy());
Set(miner, "_creditSync", new object());
Set(miner, "_creditTracking", samples);
Set(miner, "_notCreditingCampaignIds", new HashSet<string>());
Set(miner, "_pinnedQueue", new List<string>());
Set(miner, "<ActiveCampaigns>k__BackingField", new ObservableCollection<DropsCampaign> { campaign });
Set(miner, "_currentTwitchCampaign", campaign);
Set(miner, "_currentTwitchLogin", "test-channel");
void Call(string method, params object[] values) => typeof(DropsInventoryManager)
    .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(miner, values);
samples[campaign.Id] = (93, 1, DateTime.Now.AddMinutes(-4));
Call("RecordTwitchConnectionFailure", campaign.Id);
Check(!samples.ContainsKey(campaign.Id), "interruption clears pre-outage frozen samples in real manager");
Call("TrackCampaignCrediting", campaign.Id, Platform.Twitch, 93);
Check(!samples.ContainsKey(campaign.Id), "server read during interruption does not create a stall sample");
Call("RecordTwitchConnectionSuccess", campaign.Id);
Call("TrackCampaignCrediting", campaign.Id, Platform.Twitch, 93);
Check(!samples.ContainsKey(campaign.Id), "recovery grace prevents immediate false rejection in real manager");
Check(!policy.IsChannelStalled(campaign.Id, "test-channel", DateTime.Now), "transport failure does not penalize campaign channel");
Console.WriteLine("All integration tests passed.");
await TwitchAuthorizationRegression.Run();

sealed class FakeDirectoryHandler(bool fail) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (fail) throw new HttpRequestException("Simulated network interruption");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"data\":{\"game\":{\"streams\":{\"edges\":[]}}}}")
        });
    }
}

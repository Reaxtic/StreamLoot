using Core.Enums;
using Core.Interfaces;
using Core.Managers;
using Core.Models;
using Core.Services;
using System.Collections;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

static class TwitchDashboardRegression
{
    internal const string Inventory = "{\"data\":{\"currentUser\":{\"id\":\"test-user\",\"inventory\":{\"dropCampaignsInProgress\":[],\"gameEventDrops\":[]}}}}";
    internal const string Dashboard = "{\"data\":{\"currentUser\":{\"id\":\"test-user\",\"dropCampaigns\":[{\"id\":\"test-campaign\",\"status\":\"ACTIVE\"}]}}}";
    internal const string Valid = "[" + Inventory + "," + Dashboard + "]";
    internal const string NullCampaigns = "[" + Inventory + ",{\"data\":{\"currentUser\":{\"id\":\"test-user\",\"dropCampaigns\":null}}}]";
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
    static void Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
    static object? Get(object target, string field) => target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target);

    public static async Task Run()
    {
        Check(TwitchDashboardResponse.Parse(Valid).Count == 2, "complete Twitch dashboard is accepted");
        Check(TwitchDashboardResponse.Parse(Valid.Replace("[{\"id\":\"test-campaign\",\"status\":\"ACTIVE\"}]", "[]")).Count == 2,
            "genuinely empty campaign array remains a successful read");
        foreach (string invalid in new[] { NullCampaigns, Valid.Replace("\"dropCampaigns\"", "\"missingField\""),
            "[" + Inventory + ",{\"data\":{\"currentUser\":null}}]", "[{}," + Dashboard + "]",
            "[" + Inventory.Replace("test-user", "other-user") + "," + Dashboard + "]",
            "[" + Inventory + "," + Dashboard.Replace("{\"data\"", "{\"errors\":[{\"message\":\"test error\"}],\"data\"") + "]" })
        {
            bool rejected = false;
            try { TwitchDashboardResponse.Parse(invalid); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "missing/partial/unauthenticated Twitch response is not an empty dashboard");
        }

        using (var fixture = new Fixture(browserValid: true, nativeValid: false, deviceValid: false))
        {
            await fixture.Service.QueryFullDropsDashboardAsync();
            Check(fixture.DeviceReads == 0 && fixture.Handler.DeviceRequests == 0 && fixture.Handler.BrowserRequests == 1,
                "saved device token cannot override a healthy browser session");
            Check(!fixture.Service.LastDashboardFetchFailed, "success is recorded only after dashboard validation");
        }
        using (var fixture = new Fixture(browserValid: true, nativeValid: false, deviceValid: false))
        {
            var player = DispatchProxy.Create<IWebViewHost, DashboardHost>();
            await player.NavigateAsync("https://www.twitch.tv/test-channel");
            Set(fixture.Service, "_headersValidUntilUtc", DateTimeOffset.UtcNow.AddMinutes(-1));
            await fixture.Service.QueryFullDropsDashboardAsync();
            Check(fixture.Host.LastUrl.Contains("/drops/campaigns") && ((DashboardHost)(object)player).LastUrl == "https://www.twitch.tv/test-channel",
                "expired session refresh navigates the API surface without navigating its separate player");
        }
        using (var fixture = new Fixture(browserValid: false, nativeValid: true, deviceValid: false))
        {
            fixture.Service.DeviceAuthorization.RequireAuthorization();
            await fixture.Service.QueryFullDropsDashboardAsync(allowWebViewFallback: true);
            Check(fixture.Host.NativeCaptures == 1 && fixture.Host.InventoryCaptures == 1 && fixture.DeviceReads == 0,
                "HTTP 200 plus null campaigns reaches browser capture before device fallback");
            Check(!fixture.Service.LastDashboardFetchFailed && fixture.Service.DeviceAuthorization.State.Stage == TwitchAuthorizationStage.Idle,
                "complete native inventory recovery clears failure and stale authorization notice");
        }
        using (var fixture = new Fixture(browserValid: false, nativeValid: false, deviceValid: false))
        {
            for (int retry = 0; retry < 2; retry++)
            {
                bool rejected = false;
                try { await fixture.Service.QueryFullDropsDashboardAsync(allowWebViewFallback: true); }
                catch (InvalidOperationException) { rejected = true; }
                Check(rejected && fixture.Service.LastDashboardFetchFailed, "valid device token with null campaign list remains a failed read");
            }
            Check(fixture.Handler.Validations == 1 && fixture.Handler.DeviceRequests == 1 && fixture.DeviceReads == 1,
                "unusable device session is not retried on every campaign refresh");
            Check((string?)Get(fixture.Service, "_clientId") == "browser-client" &&
                fixture.Service.DeviceAuthorization.State.Stage == TwitchAuthorizationStage.Idle,
                "failed device fallback retains browser headers without prompting for another code");

            var miner = (DropsInventoryManager)RuntimeHelpers.GetUninitializedObject(typeof(DropsInventoryManager));
            Set(miner, "_twitchGqlService", fixture.Service);
            var completed = new DropsCampaign("kick-done", "done", "test", "test", null, DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(1), new[] { new DropsReward("done", "done", null, 1, 1, true) }, Platform.Kick, Array.Empty<string>(), true);
            typeof(DropsInventoryManager).GetMethod("HandleEverythingDone", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(miner, new object[] { new List<DropsCampaign> { completed } });
            Check(Get(miner, "_allDoneAnnounced") is false, "failed Twitch read cannot announce all done or schedule PC sleep");
        }
        using (var fixture = new Fixture(browserValid: false, nativeValid: false, deviceValid: true))
        {
            await fixture.Service.QueryFullDropsDashboardAsync();
            Check(fixture.Handler.Validations == 1 && fixture.Handler.DeviceRequests == 1 &&
                (string?)Get(fixture.Service, "_clientId") == "ue6666qo983tsx6so1t0vnawi233wa" && !fixture.Service.LastDashboardFetchFailed,
                "device fallback is selected only after token validation and complete campaign/progress data");
            await (Task)typeof(TwitchGqlService).GetMethod("RefreshHeadersAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(fixture.Service, new object[] { CancellationToken.None, true })!;
            Check((string?)Get(fixture.Service, "_clientId") == "browser-client" && Get(fixture.Service, "_omitIntegrityHeader") is false &&
                (string?)Get(fixture.Service, "_accessToken") == "OAuth BrowserMixedCase" && fixture.Client.DefaultRequestHeaders.UserAgent.ToString().Contains("Windows NT"),
                "forced refresh restores browser client, header mode, user agent and original token case");
        }
        using (var fixture = new Fixture(browserValid: true, nativeValid: false, deviceValid: false))
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            bool cancelled = false;
            try { await fixture.Service.QueryFullDropsDashboardAsync(cts.Token, true); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && fixture.DeviceReads == 0 && fixture.Host.NativeCaptures == 0,
                "cancelled dashboard request does not continue to other sessions or authorization");
        }
    }

    sealed class Fixture : IDisposable
    {
        public readonly TwitchGqlService Service;
        public readonly DashboardHandler Handler;
        public readonly HttpClient Client;
        public readonly DashboardHost Host;
        public int DeviceReads;
        public Fixture(bool browserValid, bool nativeValid, bool deviceValid)
        {
            var proxy = DispatchProxy.Create<IWebViewHost, DashboardHost>();
            Host = (DashboardHost)(object)proxy;
            Host.NativeValid = nativeValid;
            Handler = new(browserValid, deviceValid);
            Client = new(Handler);
            Service = (TwitchGqlService)RuntimeHelpers.GetUninitializedObject(typeof(TwitchGqlService));
            Set(Service, "_host", proxy);
            Set(Service, "_httpClient", Client);
            Set(Service, "_clientId", "browser-client");
            Set(Service, "_integrityToken", "test-integrity");
            Set(Service, "_accessToken", "OAuth BrowserMixedCase");
            Set(Service, "_headersValidUntilUtc", DateTimeOffset.UtcNow.AddHours(1));
            Set(Service, "_headerRefreshLock", new SemaphoreSlim(1, 1));
            Set(Service, "_deviceAuthorizationSaveLock", new SemaphoreSlim(1, 1));
            Set(Service, "_deviceAuthorization", new TwitchDeviceAuthorizationFlow("test-client"));
            Set(Service, "_loadDeviceToken", new Func<string?>(() => { DeviceReads++; return "DeviceMixedCase"; }));
            Set(Service, "_hashCacheSync", new object());
            Type entryType = typeof(TwitchGqlService).GetNestedType("GqlHashCacheEntry", BindingFlags.NonPublic)!;
            var cache = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), entryType))!;
            foreach (string operation in new[] { "Inventory", "ViewerDropsDashboard" })
            {
                object entry = Activator.CreateInstance(entryType)!;
                entryType.GetProperty("Hash")!.SetValue(entry, new string('a', 64));
                cache.Add(operation, entry);
            }
            Set(Service, "_gqlHashCache", cache);
        }
        public void Dispose() => Service.Dispose();
    }

    sealed class DashboardHandler(bool browserValid, bool deviceValid) : HttpMessageHandler
    {
        public int BrowserRequests, DeviceRequests, Validations;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string body;
            if (request.RequestUri!.Host == "id.twitch.tv")
            {
                Validations++;
                body = "{\"client_id\":\"ue6666qo983tsx6so1t0vnawi233wa\",\"user_id\":\"test-user\",\"expires_in\":3600}";
            }
            else if (request.Headers.GetValues("Client-ID").Single() == "browser-client")
            { BrowserRequests++; body = browserValid ? Valid : NullCampaigns; }
            else
            { DeviceRequests++; body = deviceValid ? Valid : NullCampaigns; }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    public class DashboardHost : DispatchProxy
    {
        public bool NativeValid;
        public int NativeCaptures, InventoryCaptures;
        public string LastUrl = "about:blank";
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            return method!.Name switch
            {
                "NavigateAsync" => Navigate((string)args![0]!),
                "EnsureInitializedAsync" => Task.CompletedTask,
                "GetCookieValueAsync" => Task.FromResult<string?>((string)args![1]! == "auth-token" ? "BrowserMixedCase" : "test-device"),
                "CaptureRequestHeaderAsync" => Task.FromResult((string)args![0]! switch { "Client-ID" => "browser-client", "Client-Integrity" => "test-integrity", _ => "test-device" }),
                "CaptureGqlRequestBodyContainingAsync" => Task.FromResult("[{\"operationName\":\"" + args![0] + "\",\"extensions\":{\"persistedQuery\":{\"sha256Hash\":\"" + new string('a', 64) + "\"}}}]"),
                "CaptureViewerDropsDashboardResponseAsync" => CaptureNative(),
                "CaptureViewerDropsProgressResponseAsync" => CaptureInventory(),
                _ => throw new NotSupportedException(method.Name)
            };
        }
        Task Navigate(string url) { LastUrl = url; return Task.CompletedTask; }
        Task<string> CaptureNative()
        {
            NativeCaptures++;
            return NativeValid ? Task.FromResult(Dashboard) : Task.FromException<string>(new TimeoutException("Simulated unavailable native dashboard"));
        }
        Task<string> CaptureInventory() { InventoryCaptures++; return Task.FromResult(Inventory); }
    }
}

using Core.Services;
using System.Net;
using System.Net.Http;

static class TwitchAuthorizationRegression
{
    public static async Task Run()
    {
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new AuthorizationHandler();
        using var flow = new TwitchDeviceAuthorizationFlow("test-client", new HttpClient(handler),
            delay: (_, ct) => gate.Task.WaitAsync(ct));
        flow.StateChanged += state => { if (state.Stage == TwitchAuthorizationStage.Waiting) waiting.TrySetResult(); };
        flow.RequireAuthorization();
        Check(handler.DeviceRequests == 0 && flow.State.Stage == TwitchAuthorizationStage.Required, "background authorization requirement does not generate a code");
        var first = flow.StartAsync();
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(ReferenceEquals(first, flow.StartAsync()), "simultaneous manual starts share one device code");
        string? originalCode = flow.State.UserCode;
        flow.RequireAuthorization();
        Check(flow.State.Stage == TwitchAuthorizationStage.Waiting && flow.State.UserCode == originalCode && handler.DeviceRequests == 1,
            "background refresh cannot replace or cancel a pending code");
        Check(flow.State.ExpiresAt > DateTimeOffset.UtcNow && flow.State.VerificationUrl!.StartsWith("https://www.twitch.tv/activate"),
            "authorization exposes official page and actual server expiry for countdown");
        gate.TrySetResult();
        Check(await first == "test-only-access" && flow.State.Stage == TwitchAuthorizationStage.Approved,
            "server approval completes the manual flow");

        var clock = new TestClock();
        var pendingHandler = new AuthorizationHandler(pending: true);
        using var expiring = new TwitchDeviceAuthorizationFlow("test-client", new HttpClient(pendingHandler), clock,
            (wait, _) => { clock.Advance(wait); return Task.CompletedTask; });
        Check(await expiring.StartAsync() == null && expiring.State.Stage == TwitchAuthorizationStage.Expired,
            "code expires at server deadline without a modal wait");
        Check(pendingHandler.DeviceRequests == 1, "expired code is not automatically renewed");
        await expiring.StartAsync();
        Check(pendingHandler.DeviceRequests == 2, "explicit retry obtains a new code");

        var cancelGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellable = new TwitchDeviceAuthorizationFlow("test-client", new HttpClient(new AuthorizationHandler()),
            delay: (_, ct) => cancelGate.Task.WaitAsync(ct));
        cancellable.StateChanged += s => { if (s.Stage == TwitchAuthorizationStage.Waiting) cancelWaiting.TrySetResult(); };
        var pendingTask = cancellable.StartAsync();
        await cancelWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellable.Cancel();
        Check(await pendingTask == null && cancellable.State.Stage == TwitchAuthorizationStage.Cancelled,
            "manual cancel ends authorization without terminating the application");
    }

    sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan wait) => _now += wait;
    }

    sealed class AuthorizationHandler(bool pending = false) : HttpMessageHandler
    {
        public int DeviceRequests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            bool device = request.RequestUri!.AbsolutePath.EndsWith("/device");
            if (device) DeviceRequests++;
            string body = device
                ? "{\"device_code\":\"test-device\",\"user_code\":\"TEST-CODE\",\"verification_uri\":\"https://www.twitch.tv/activate\",\"expires_in\":30,\"interval\":5}"
                : pending ? "{\"message\":\"authorization_pending\"}" : "{\"access_token\":\"test-only-access\"}";
            return Task.FromResult(new HttpResponseMessage(device || !pending ? HttpStatusCode.OK : HttpStatusCode.BadRequest)
                { Content = new StringContent(body) });
        }
    }
}

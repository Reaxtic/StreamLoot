using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace Core.Services;

public enum TwitchAuthorizationStage { Idle, Required, Requesting, Waiting, Approved, Expired, Cancelled, Failed }
public sealed record TwitchAuthorizationState(TwitchAuthorizationStage Stage,
    string? UserCode = null, string? VerificationUrl = null, DateTimeOffset? ExpiresAt = null, string? Error = null);

/// <summary>A user-started, single-flight authorization; inventory cancellation never owns this lifetime.</summary>
public sealed class TwitchDeviceAuthorizationFlow : IDisposable
{
    private readonly HttpClient _client;
    private readonly string _clientId;
    private readonly object _sync = new();
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource? _cts;
    private Task<string?>? _running;
    public TwitchAuthorizationState State { get; private set; } = new(TwitchAuthorizationStage.Idle);
    public event Action<TwitchAuthorizationState>? StateChanged;

    public TwitchDeviceAuthorizationFlow(string clientId, HttpClient? client = null,
        TimeProvider? time = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _clientId = clientId;
        _client = client ?? new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
            { Timeout = TimeSpan.FromSeconds(30) };
        _time = time ?? TimeProvider.System;
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, ct));
    }

    private void Publish(TwitchAuthorizationState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    public void RequireAuthorization()
    {
        lock (_sync)
        {
            if (_running is { IsCompleted: false }) return;
            Publish(new(TwitchAuthorizationStage.Required)); // no code, browser or modal dialog in background
        }
    }

    public Task<string?> StartAsync()
    {
        lock (_sync)
        {
            if (_running is { IsCompleted: false }) return _running;
            _cts?.Dispose();
            _cts = new();
            return _running = RunAsync(_cts.Token);
        }
    }

    public void Cancel() { lock (_sync) _cts?.Cancel(); }

    private async Task<string?> RunAsync(CancellationToken ct)
    {
        await Task.Yield();
        Publish(new(TwitchAuthorizationStage.Requesting));
        try
        {
            using var request = new FormUrlEncodedContent(new Dictionary<string, string>
                { ["client_id"] = _clientId, ["scopes"] = "" });
            using var response = await _client.PostAsync("https://id.twitch.tv/oauth2/device", request, ct);
            response.EnsureSuccessStatusCode();
            var device = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
            string code = device["device_code"]?.GetValue<string>() ?? throw new InvalidOperationException("Missing device code.");
            string userCode = device["user_code"]?.GetValue<string>() ?? throw new InvalidOperationException("Missing user code.");
            int expires = device["expires_in"]?.GetValue<int>() ?? throw new InvalidOperationException("Missing code lifetime.");
            if (expires <= 0) throw new InvalidOperationException("Invalid code lifetime.");
            int interval = Math.Max(5, device["interval"]?.GetValue<int>() ?? 5);
            var uri = new Uri(device["verification_uri"]?.GetValue<string>() ?? "https://www.twitch.tv/activate");
            if (uri.Scheme != "https" || (uri.Host != "www.twitch.tv" && uri.Host != "twitch.tv"))
                throw new InvalidOperationException("Unexpected authorization host.");
            string url = uri.AbsoluteUri;
            if (!url.Contains("device-code=", StringComparison.OrdinalIgnoreCase))
                url += (url.Contains('?') ? "&" : "?") + "device-code=" + Uri.EscapeDataString(userCode);
            DateTimeOffset deadline = _time.GetUtcNow().AddSeconds(expires);
            var waiting = new TwitchAuthorizationState(TwitchAuthorizationStage.Waiting, userCode, url, deadline);
            Publish(waiting);
            while (_time.GetUtcNow() < deadline)
            {
                TimeSpan remaining = deadline - _time.GetUtcNow();
                await _delay(TimeSpan.FromSeconds(Math.Min(interval, remaining.TotalSeconds)), ct);
                if (_time.GetUtcNow() >= deadline) break;
                using var tokenRequest = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = _clientId, ["device_code"] = code,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                });
                using var tokenResponse = await _client.PostAsync("https://id.twitch.tv/oauth2/token", tokenRequest, ct);
                var tokenData = JsonNode.Parse(await tokenResponse.Content.ReadAsStringAsync(ct));
                if (tokenResponse.IsSuccessStatusCode)
                {
                    string token = tokenData?["access_token"]?.GetValue<string>() ?? throw new InvalidOperationException("Missing access token.");
                    Publish(new(TwitchAuthorizationStage.Approved));
                    return token;
                }
                string? error = tokenData?["error"]?.GetValue<string>() ?? tokenData?["message"]?.GetValue<string>();
                if (error == "authorization_pending") continue;
                if (error == "slow_down") { interval += 5; continue; }
                if (error is "expired_token" or "invalid device code") break;
                throw new InvalidOperationException(error == "access_denied" ? "Access was denied." : "Twitch rejected authorization.");
            }
            Publish(new(TwitchAuthorizationStage.Expired));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { Publish(new(TwitchAuthorizationStage.Cancelled)); }
        catch (Exception ex)
        { Publish(new(TwitchAuthorizationStage.Failed, Error: ex is HttpRequestException ? "Connection failed. Try again." : ex.Message)); }
        return null;
    }

    public void Dispose() { Cancel(); _client.Dispose(); _cts?.Dispose(); }
}

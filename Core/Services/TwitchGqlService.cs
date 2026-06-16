using System.Text.Json.Nodes;
using System.Net.Http.Json;
using System.Text.Json;
using System.Net.Http;
using Core.Interfaces;
using Core.Logging;
using Core.Managers;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.IO;

namespace Core.Services
{
    public sealed class TwitchGqlService : IGqlService
    {
        private readonly IWebViewHost _host;
        private readonly HttpClient _httpClient;

        private string? _clientId;
        private string? _integrityToken;
        private string? _deviceId;
        private string? _accessToken;
        private string? _userId;

        /// <inheritdoc />
        public bool LastDashboardFetchFailed { get; private set; }

        // Watch heartbeat ("minute-watched") state — credits drop watch time directly via Twitch's analytics
        // endpoint, the way DevilXD's miner does, instead of relying on the hidden player actually decoding video.
        private string? _spadeUrl;
        private (string Login, string ChannelId, string BroadcastId, DateTime Fetched)? _streamIdCache;

        // Header (Client-Integrity etc.) caching. Capturing these requires a full
        // navigation of the heavy Twitch SPA in the hidden WebView, so we avoid
        // doing it on every GQL call and instead reuse the token until it expires.
        private DateTimeOffset _headersValidUntilUtc = DateTimeOffset.MinValue;
        private readonly SemaphoreSlim _headerRefreshLock = new(1, 1);

        // Used only as a fallback when the integrity token's own expiry cannot be read.
        private static readonly TimeSpan _headerFallbackTtl = TimeSpan.FromMinutes(30);

        // Upper bound on how long captured headers are reused even if the token claims a
        // far-future expiry – bounds the impact of a stale/poisoned token.
        private static readonly TimeSpan _headerMaxCacheTtl = TimeSpan.FromHours(6);

        // Safety margin so we refresh slightly before the token actually expires.
        private static readonly TimeSpan _headerExpirySkew = TimeSpan.FromMinutes(2);

        private readonly object _hashCacheSync = new();
        private readonly Dictionary<string, GqlHashCacheEntry> _gqlHashCache = new(StringComparer.OrdinalIgnoreCase);

        private static readonly string _gqlHashCacheFilePath = Path.Combine(
            Environment.ExpandEnvironmentVariables("%APPDATA%"),
            "Stream Loot",
            "GqlHashCache.json");

        public string UserId
        {
            set => _userId = value;
        }

        public TwitchGqlService(IWebViewHost host, HttpClient? httpClient = null)
        {
            _host = host;
            _httpClient = httpClient ?? new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            })
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130.0.0.0 Safari/537.36"
            );

            LoadHashCacheFromDisk();
        }
        /// <summary>
        /// Indicates whether the cached Twitch headers are still present and not yet expired.
        /// </summary>
        private bool HeadersAreFresh =>
            !string.IsNullOrEmpty(_clientId)
            && !string.IsNullOrEmpty(_integrityToken)
            && DateTimeOffset.UtcNow < _headersValidUntilUtc;

        /// <summary>
        /// Asynchronously refreshes the required HTTP headers by navigating to the Twitch campaigns page and capturing
        /// the latest values. If a previously captured token is still valid the navigation is skipped, unless
        /// <paramref name="force"/> is set (e.g. after the server rejected the current integrity token).
        /// </summary>
        /// <remarks>This method updates internal header values used for authenticated requests to Twitch
        /// services. It should be called whenever header values may have changed or need to be refreshed.</remarks>
        /// <param name="ct">A cancellation token that can be used to cancel the operation.</param>
        /// <param name="force">When true, ignores the cached token and always performs a fresh capture.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the Client-Integrity token cannot be captured from the HTTP headers.</exception>
        private async Task RefreshHeadersAsync(CancellationToken ct = default, bool force = false)
        {
            // Fast path: a previously captured token is still valid, so skip the
            // expensive WebView navigation entirely.
            if (!force && HeadersAreFresh)
                return;

            // Single-flight: collapse concurrent refreshes into one navigation.
            await _headerRefreshLock.WaitAsync(ct);

            try
            {
                // Re-check after acquiring the lock; another caller may have just refreshed.
                if (!force && HeadersAreFresh)
                    return;

                const int maxAttempts = 10;
                const int baseDelayMs = 5 * 1000; // Start with 5s delay on retry

                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    try
                    {
                        AppLogger.Debug("TwitchGql", $"[RefreshHeaders] Attempt {attempt}/{maxAttempts} – Navigating to drops/campaigns");

                        // Fresh navigation every attempt (important for clean integrity token)
                        await _host.NavigateAsync($"https://www.twitch.tv/drops/campaigns?t={DateTimeOffset.Now.ToUnixTimeMilliseconds()}");

                        // Parallel capture tasks
                        Task<string> clientIdTask = _host.CaptureRequestHeaderAsync("Client-ID", "gql.twitch.tv", 10000, ct);
                        Task<string> integrityTask = _host.CaptureRequestHeaderAsync("Client-Integrity", "gql.twitch.tv", 10000, ct);
                        Task<string> deviceIdTask = _host.CaptureRequestHeaderAsync("X-Device-Id", "gql.twitch.tv", 10000, ct);
                        Task<string> authTokenTask = GetAuthTokenFromCookieAsync();

                        string[] results = await Task.WhenAll(clientIdTask, integrityTask, deviceIdTask, authTokenTask);

                        string clientId = results[0];
                        string integrityToken = results[1];
                        string deviceId = results[2];
                        string accessToken = results[3];

                        // Basic validation
                        if (string.IsNullOrEmpty(integrityToken))
                        {
                            throw new InvalidOperationException("Captured Client-Integrity token was null or empty");
                        }

                        if (string.IsNullOrEmpty(clientId))
                        {
                            throw new InvalidOperationException("Captured Client-ID was null or empty");
                        }

                        // Success! Assign and exit
                        _clientId = clientId;
                        _integrityToken = integrityToken;
                        _deviceId = deviceId ?? _deviceId; // Device-ID is optional – keep old if missing
                        _accessToken = accessToken;
                        _headersValidUntilUtc = ComputeHeadersValidUntil(integrityToken);

                        AppLogger.Debug("TwitchGql", $"[RefreshHeaders] Success on attempt {attempt} – Got fresh headers (valid until {_headersValidUntilUtc:u})");
                        return;
                    }
                    catch (Exception ex) when (attempt < maxAttempts)
                    {
                        AppLogger.Warn("TwitchGql", $"[RefreshHeaders] Attempt {attempt} failed: {ex.Message}. Retrying in {baseDelayMs * attempt}ms...");

                        // Exponential backoff: 5s -> 10s -> 15s
                        await Task.Delay(baseDelayMs * attempt, ct);
                    }
                }

                // All attempts failed
                throw new InvalidOperationException($"Failed to refresh headers after {maxAttempts} attempts. Last capture likely poisoned or page didn't trigger GQL requests.");
            }
            finally
            {
                _headerRefreshLock.Release();
            }
        }

        /// <summary>
        /// Determines how long the freshly captured headers can be reused. Prefers the
        /// integrity token's own <c>exp</c> claim (minus a small safety margin); falls
        /// back to a conservative fixed TTL when the expiry cannot be read.
        /// </summary>
        private static DateTimeOffset ComputeHeadersValidUntil(string integrityToken)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;

            DateTimeOffset? exp = TryReadJwtExpiry(integrityToken);
            if (exp is null)
                return now + _headerFallbackTtl; // expiry unknown – be conservative

            DateTimeOffset adjusted = exp.Value - _headerExpirySkew;

            // Guard against a clock-skewed/already-expired token: never cache into the past.
            if (adjusted <= now)
                return now;

            // Cap how far ahead we trust the token, even if it claims a long lifetime.
            DateTimeOffset ceiling = now + _headerMaxCacheTtl;
            return adjusted < ceiling ? adjusted : ceiling;
        }

        /// <summary>
        /// Reads the <c>exp</c> claim (UTC) from a JWT, or null if the value is not a parseable JWT.
        /// </summary>
        private static DateTimeOffset? TryReadJwtExpiry(string jwt)
        {
            try
            {
                string[] parts = jwt.Split('.');
                if (parts.Length < 2)
                    return null;

                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                switch (payload.Length % 4)
                {
                    case 2: payload += "=="; break;
                    case 3: payload += "="; break;
                }

                byte[] bytes = Convert.FromBase64String(payload);
                using JsonDocument doc = JsonDocument.Parse(bytes);

                if (doc.RootElement.TryGetProperty("exp", out JsonElement expElement) &&
                    expElement.TryGetInt64(out long expSeconds))
                {
                    return DateTimeOffset.FromUnixTimeSeconds(expSeconds);
                }
            }
            catch
            {
                // Not a JWT we can read – caller falls back to a fixed TTL.
            }

            return null;
        }
        /// <summary>
        /// Retrieves the Twitch authentication token from the browser cookie asynchronously.
        /// </summary>
        /// <returns>A string containing the OAuth authentication token in the format "OAuth {token}". The token is returned in
        /// lowercase.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the "auth-token" cookie is not found for the Twitch domain.</exception>
        private async Task<string> GetAuthTokenFromCookieAsync()
        {
            string? token = await _host.GetCookieValueAsync("https://twitch.tv", "auth-token");
            if (string.IsNullOrEmpty(token))
                throw new InvalidOperationException("auth-token cookie not found");

            return "OAuth " + token.ToLower();
        }
        /// <summary>
        /// Asynchronously retrieves the SHA-256 hash of a persisted GraphQL query for the specified operation name.
        /// </summary>
        /// <param name="operationName">The name of the GraphQL operation for which to retrieve the persisted query hash. Cannot be null or empty.</param>
        /// <param name="ct">A cancellation token that can be used to cancel the operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the SHA-256 hash of the
        /// persisted query associated with the specified operation name.</returns>
        /// <exception cref="InvalidOperationException">Thrown if a persisted query hash cannot be found for the specified operation name.</exception>
        private async Task<string> GetPersistedQueryHashAsync(string operationName, CancellationToken ct = default, string? urlOverride = null, bool allowCached = true)
        {
            if (allowCached && TryGetCachedHash(operationName, requireFresh: true, out string? cachedHash))
            {
                LogCacheDebug($"Using cached hash for '{operationName}'.");
                return cachedHash!;
            }

            try
            {
                if (!string.IsNullOrEmpty(urlOverride))
                    await _host.NavigateAsync($"{urlOverride}?t={DateTimeOffset.Now.ToUnixTimeMilliseconds()}");
                else
                    await _host.NavigateAsync($"https://www.twitch.tv/drops/campaigns?t={DateTimeOffset.Now.ToUnixTimeMilliseconds()}");

                string payload = await _host.CaptureGqlRequestBodyContainingAsyncWithRetry(operationName, 5000, 10, ct: ct);

                using JsonDocument document = JsonDocument.Parse(payload);
                JsonElement root = document.RootElement;

                IEnumerable<JsonElement> operations = root.ValueKind == JsonValueKind.Array
                    ? root.EnumerateArray()
                    : Enumerable.Repeat(root, 1);

                foreach (JsonElement operation in operations)
                {
                    if (!operation.TryGetProperty("operationName", out JsonElement opNameElement) ||
                        opNameElement.GetString() != operationName)
                        continue;

                    if (operation.TryGetProperty("extensions", out JsonElement extensions) &&
                        extensions.TryGetProperty("persistedQuery", out JsonElement persistedQuery) &&
                        persistedQuery.TryGetProperty("sha256Hash", out JsonElement hashElement))
                    {
                        string hash = hashElement.GetString()!;
                        SetCachedHash(operationName, hash);
                        return hash;
                    }
                }

                throw new InvalidOperationException($"Persisted query hash not found for operation: {operationName}");
            }
            catch (Exception ex) when (allowCached && TryGetCachedHash(operationName, requireFresh: false, out string? fallbackHash))
            {
                LogCacheWarn($"Live hash capture failed for '{operationName}'. Using cached fallback. {ex.Message}");
                return fallbackHash!;
            }
        }

        public async Task<List<string>> QueryLiveChannelsBySlugAsync(IReadOnlyList<string> channelLogins, string gameSlug, CancellationToken ct = default)
        {
            if (_clientId == null || _integrityToken == null)
                await RefreshHeadersAsync(ct);

            const string operationName = "StreamMetadata";
            const string hash = "b57f9b910f8cd1a4659d894fe7550ccc81ec9052c01e438b290fd66a040b9b93";
            const int batchSize = 30;

            SetCachedHash(operationName, hash);

            List<string> liveMatches = new();
            int totalBatches = (int)Math.Ceiling(channelLogins.Count / (double)batchSize);

            AppLogger.Info("TwitchGql", $"QueryLiveChannelsBySlug started. totalChannels={channelLogins.Count}, gameSlug={gameSlug}, batches={totalBatches}");

            for (int i = 0; i < channelLogins.Count; i += batchSize)
            {
                List<string> batch = channelLogins.Skip(i).Take(batchSize).ToList();

                JsonArray payload = new();
                foreach (string login in batch)
                {
                    payload.Add(new JsonObject
                    {
                        ["operationName"] = operationName,
                        ["variables"] = new JsonObject
                        {
                            ["channelLogin"] = login,
                            ["includeIsDJ"] = true
                        },
                        ["extensions"] = new JsonObject
                        {
                            ["persistedQuery"] = new JsonObject
                            {
                                ["version"] = 1,
                                ["sha256Hash"] = hash
                            }
                        }
                    });
                }

                using HttpRequestMessage request = new(HttpMethod.Post, "https://gql.twitch.tv/gql")
                {
                    Content = JsonContent.Create(payload)
                };
                request.Headers.TryAddWithoutValidation("Client-ID", _clientId);
                request.Headers.TryAddWithoutValidation("Client-Integrity", _integrityToken);
                request.Headers.TryAddWithoutValidation("Authorization", _accessToken);
                if (!string.IsNullOrEmpty(_deviceId))
                    request.Headers.TryAddWithoutValidation("X-Device-Id", _deviceId);

                HttpResponseMessage response = await _httpClient.SendAsync(request, ct);
                string jsonText = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode || jsonText.Contains("\"errors\""))
                {
                    AppLogger.Warn("TwitchGql", $"QueryLiveChannelsBySlug batch failed at offset={i}. Refreshing headers and retrying.");
                    await RefreshHeadersAsync(ct, force: true);

                    using HttpRequestMessage retryRequest = new(HttpMethod.Post, "https://gql.twitch.tv/gql")
                    {
                        Content = JsonContent.Create(payload)
                    };
                    retryRequest.Headers.TryAddWithoutValidation("Client-ID", _clientId);
                    retryRequest.Headers.TryAddWithoutValidation("Client-Integrity", _integrityToken);
                    retryRequest.Headers.TryAddWithoutValidation("Authorization", _accessToken);
                    if (!string.IsNullOrEmpty(_deviceId))
                        retryRequest.Headers.TryAddWithoutValidation("X-Device-Id", _deviceId);

                    response = await _httpClient.SendAsync(retryRequest, ct);
                    jsonText = await response.Content.ReadAsStringAsync(ct);
                }

                response.EnsureSuccessStatusCode();
                JsonArray responseArray = JsonNode.Parse(jsonText)!.AsArray();

                for (int j = 0; j < batch.Count; j++)
                {
                    JsonNode? stream = responseArray[j]?["data"]?["user"]?["stream"];
                    if (stream == null) continue;

                    string? type = stream["type"]?.GetValue<string>();
                    string? slug = stream["game"]?["slug"]?.GetValue<string>();

                    if (type == "live" && string.Equals(slug, gameSlug, StringComparison.OrdinalIgnoreCase))
                        liveMatches.Add(batch[j]);
                }

                AppLogger.Debug("TwitchGql", $"QueryLiveChannelsBySlug batch offset={i}, batchSize={batch.Count}, matchesFound={liveMatches.Count}");
            }

            AppLogger.Info("TwitchGql", $"QueryLiveChannelsBySlug completed. liveMatches={liveMatches.Count}/{channelLogins.Count}");
            return liveMatches;
        }

        /// <summary>
        /// Like <see cref="QueryLiveChannelsBySlugAsync"/>, but also returns each live channel's current viewer count.
        /// Uses a raw GraphQL query (users(logins:)) batched in groups of 30, sorted by viewers.
        /// </summary>
        public async Task<List<(string Login, int Viewers)>> QueryLiveChannelsWithViewersBySlugAsync(IReadOnlyList<string> channelLogins, string gameSlug, CancellationToken ct = default)
        {
            if (channelLogins == null || channelLogins.Count == 0 || string.IsNullOrWhiteSpace(gameSlug))
                return new List<(string, int)>();

            if (_clientId == null || _integrityToken == null)
                await RefreshHeadersAsync(ct);

            // Use the same reliable persisted StreamMetadata query as the eligibility check (raw GraphQL queries
            // are rejected by Twitch's integrity enforcement). viewersCount is read defensively: when the persisted
            // response includes it the picker shows a live viewer number, otherwise it falls back to a "live" label.
            const string operationName = "StreamMetadata";
            const string hash = "b57f9b910f8cd1a4659d894fe7550ccc81ec9052c01e438b290fd66a040b9b93";
            const int batchSize = 30;
            SetCachedHash(operationName, hash);

            List<(string, int)> result = new();
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < channelLogins.Count; i += batchSize)
            {
                List<string> batch = channelLogins.Skip(i).Take(batchSize).ToList();

                JsonArray payload = new();
                foreach (string login in batch)
                {
                    payload.Add(new JsonObject
                    {
                        ["operationName"] = operationName,
                        ["variables"] = new JsonObject { ["channelLogin"] = login, ["includeIsDJ"] = true },
                        ["extensions"] = new JsonObject
                        {
                            ["persistedQuery"] = new JsonObject { ["version"] = 1, ["sha256Hash"] = hash }
                        }
                    });
                }

                async Task<HttpResponseMessage> SendBatchAsync()
                {
                    HttpRequestMessage request = new(HttpMethod.Post, "https://gql.twitch.tv/gql")
                    {
                        Content = JsonContent.Create(payload)
                    };
                    request.Headers.TryAddWithoutValidation("Client-ID", _clientId);
                    request.Headers.TryAddWithoutValidation("Client-Integrity", _integrityToken);
                    request.Headers.TryAddWithoutValidation("Authorization", _accessToken);
                    if (!string.IsNullOrEmpty(_deviceId))
                        request.Headers.TryAddWithoutValidation("X-Device-Id", _deviceId);
                    return await _httpClient.SendAsync(request, ct);
                }

                try
                {
                    HttpResponseMessage response = await SendBatchAsync();
                    string jsonText = await response.Content.ReadAsStringAsync(ct);

                    if (!response.IsSuccessStatusCode || jsonText.Contains("\"errors\""))
                    {
                        await RefreshHeadersAsync(ct, force: true);
                        response = await SendBatchAsync();
                        jsonText = await response.Content.ReadAsStringAsync(ct);
                    }

                    if (!response.IsSuccessStatusCode)
                        continue;

                    JsonArray responseArray = JsonNode.Parse(jsonText)!.AsArray();
                    for (int j = 0; j < batch.Count; j++)
                    {
                        JsonNode? stream = responseArray[j]?["data"]?["user"]?["stream"];
                        if (stream == null)
                            continue;

                        string? type = stream["type"]?.GetValue<string>();
                        string? slug = stream["game"]?["slug"]?.GetValue<string>();
                        int viewers = stream["viewersCount"]?.GetValue<int>() ?? 0;

                        if (type == "live" && string.Equals(slug, gameSlug, StringComparison.OrdinalIgnoreCase) && seen.Add(batch[j]))
                            result.Add((batch[j], viewers));
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("TwitchGql", $"QueryLiveChannelsWithViewers batch failed at offset={i}: {ex.Message}");
                }
            }

            result.Sort((a, b) => b.Item2.CompareTo(a.Item2));
            AppLogger.Info("TwitchGql", $"QueryLiveChannelsWithViewers completed. live={result.Count}/{channelLogins.Count}, slug={gameSlug}");
            return result;
        }

        /// <summary>
        /// Lists currently-LIVE, drops-enabled channels in a game's directory (used for "general" drop campaigns
        /// that aren't tied to specific channels). Returns each channel's login and current viewer count, sorted by viewers.
        /// </summary>
        public async Task<List<(string Login, int Viewers)>> QueryLiveDirectoryChannelsAsync(string gameSlug, int limit = 30, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(gameSlug))
                return new List<(string, int)>();

            if (_clientId == null || _integrityToken == null)
                await RefreshHeadersAsync(ct);

            // Raw GraphQL query (not persisted): the game directory filtered to live drops-enabled streams, by viewers.
            const string query = "query StreamLootDirectory($slug: String!, $limit: Int!) { " +
                "game(slug: $slug) { streams(first: $limit, options: { sort: VIEWER_COUNT, systemFilters: [DROPS_ENABLED] }) { " +
                "edges { node { viewersCount broadcaster { login } } } } } }";

            JsonObject payload = new()
            {
                ["operationName"] = "StreamLootDirectory",
                ["query"] = query,
                ["variables"] = new JsonObject { ["slug"] = gameSlug, ["limit"] = Math.Clamp(limit, 1, 60) }
            };

            async Task<HttpResponseMessage> SendAsync()
            {
                HttpRequestMessage request = new(HttpMethod.Post, "https://gql.twitch.tv/gql")
                {
                    Content = JsonContent.Create(payload)
                };
                request.Headers.TryAddWithoutValidation("Client-ID", _clientId);
                request.Headers.TryAddWithoutValidation("Client-Integrity", _integrityToken);
                request.Headers.TryAddWithoutValidation("Authorization", _accessToken);
                if (!string.IsNullOrEmpty(_deviceId))
                    request.Headers.TryAddWithoutValidation("X-Device-Id", _deviceId);
                return await _httpClient.SendAsync(request, ct);
            }

            try
            {
                HttpResponseMessage response = await SendAsync();
                string jsonText = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode || jsonText.Contains("\"errors\""))
                {
                    AppLogger.Warn("TwitchGql", "QueryLiveDirectoryChannels failed; refreshing headers and retrying.");
                    await RefreshHeadersAsync(ct, force: true);
                    response = await SendAsync();
                    jsonText = await response.Content.ReadAsStringAsync(ct);
                }

                if (!response.IsSuccessStatusCode)
                {
                    AppLogger.Warn("TwitchGql", $"QueryLiveDirectoryChannels HTTP {(int)response.StatusCode}.");
                    return new List<(string, int)>();
                }

                JsonNode? root = JsonNode.Parse(jsonText);
                JsonArray? edges = root?["data"]?["game"]?["streams"]?["edges"]?.AsArray();
                if (edges == null)
                {
                    AppLogger.Warn("TwitchGql", "QueryLiveDirectoryChannels returned no edges.");
                    return new List<(string, int)>();
                }

                List<(string, int)> result = new();
                HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
                foreach (JsonNode? edge in edges)
                {
                    string? login = edge?["node"]?["broadcaster"]?["login"]?.GetValue<string>();
                    int viewers = edge?["node"]?["viewersCount"]?.GetValue<int>() ?? 0;
                    if (!string.IsNullOrWhiteSpace(login) && seen.Add(login))
                        result.Add((login, viewers));
                }

                AppLogger.Info("TwitchGql", $"QueryLiveDirectoryChannels completed. slug={gameSlug}, live={result.Count}");
                return result;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("TwitchGql", $"QueryLiveDirectoryChannels exception: {ex.Message}");
                return new List<(string, int)>();
            }
        }

        /// <summary>
        /// Sends a single "minute-watched" analytics event to Twitch's spade endpoint for the given live channel.
        /// This is how drop watch time is actually credited server-side (DevilXD's approach), so progress accrues
        /// even when the hidden embedded player isn't decoding video. Returns true if the event was accepted.
        /// </summary>
        public async Task<bool> SendWatchHeartbeatAsync(string login, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(_userId))
                return false;

            try
            {
                if (_clientId == null || _integrityToken == null)
                    await RefreshHeadersAsync(ct);

                (string ChannelId, string BroadcastId)? ids = await GetStreamIdsAsync(login, ct);
                if (ids == null)
                {
                    AppLogger.Debug("TwitchWatch", $"No live stream id for '{login}'; heartbeat skipped.");
                    return false;
                }

                string? spade = await GetSpadeUrlAsync(login, ct);
                if (string.IsNullOrWhiteSpace(spade))
                    return false;

                // channel_id / broadcast_id / user_id must be JSON NUMBERS (not strings) for the drops backend to
                // count the event — otherwise spade still answers 204 but the watch time is ignored.
                string json = "[{\"event\":\"minute-watched\",\"properties\":{" +
                              $"\"channel_id\":{ids.Value.ChannelId}," +
                              $"\"broadcast_id\":{ids.Value.BroadcastId}," +
                              "\"player\":\"site\"," +
                              $"\"user_id\":{_userId}}}}}]";
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

                using FormUrlEncodedContent content = new(new[] { new KeyValuePair<string, string>("data", b64) });
                using HttpRequestMessage req = new(HttpMethod.Post, spade) { Content = content };
                HttpResponseMessage resp = await _httpClient.SendAsync(req, ct);

                AppLogger.Info("TwitchWatch", $"minute-watched '{login}' (ch={ids.Value.ChannelId}, b={ids.Value.BroadcastId}) -> HTTP {(int)resp.StatusCode}.");
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("TwitchWatch", $"Watch heartbeat failed for '{login}': {ex.Message}");
                return false;
            }
        }

        /// <summary>Gets the channel id and current broadcast (stream) id for a login via the persisted StreamMetadata query; null if offline.</summary>
        private async Task<(string ChannelId, string BroadcastId)?> GetStreamIdsAsync(string login, CancellationToken ct)
        {
            if (_streamIdCache is { } cache && string.Equals(cache.Login, login, StringComparison.OrdinalIgnoreCase)
                && (DateTime.Now - cache.Fetched).TotalSeconds < 50)
                return (cache.ChannelId, cache.BroadcastId);

            const string operationName = "StreamMetadata";
            const string hash = "b57f9b910f8cd1a4659d894fe7550ccc81ec9052c01e438b290fd66a040b9b93";
            SetCachedHash(operationName, hash);

            JsonArray payload = new()
            {
                new JsonObject
                {
                    ["operationName"] = operationName,
                    ["variables"] = new JsonObject { ["channelLogin"] = login, ["includeIsDJ"] = true },
                    ["extensions"] = new JsonObject { ["persistedQuery"] = new JsonObject { ["version"] = 1, ["sha256Hash"] = hash } }
                }
            };

            using HttpRequestMessage request = new(HttpMethod.Post, "https://gql.twitch.tv/gql") { Content = JsonContent.Create(payload) };
            request.Headers.TryAddWithoutValidation("Client-ID", _clientId);
            request.Headers.TryAddWithoutValidation("Client-Integrity", _integrityToken);
            request.Headers.TryAddWithoutValidation("Authorization", _accessToken);
            if (!string.IsNullOrEmpty(_deviceId))
                request.Headers.TryAddWithoutValidation("X-Device-Id", _deviceId);

            HttpResponseMessage response = await _httpClient.SendAsync(request, ct);
            string text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return null;

            JsonNode? user = JsonNode.Parse(text)?.AsArray().FirstOrDefault()?["data"]?["user"];
            string? channelId = user?["id"]?.GetValue<string>();
            string? broadcastId = user?["stream"]?["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(broadcastId))
                return null;

            _streamIdCache = (login, channelId!, broadcastId!, DateTime.Now);
            return (channelId!, broadcastId!);
        }

        /// <summary>Resolves Twitch's spade analytics URL (cached), reading it from the site config; falls back to the known endpoint.</summary>
        private async Task<string?> GetSpadeUrlAsync(string login, CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(_spadeUrl))
                return _spadeUrl;

            try
            {
                string page = await _httpClient.GetStringAsync($"https://www.twitch.tv/{login}", ct);
                Match settings = Regex.Match(page, @"https://(?:static\.twitchcdn\.net|assets\.twitch\.tv)/config/settings\.[0-9a-f]+\.js");
                if (settings.Success)
                {
                    string settingsJs = await _httpClient.GetStringAsync(settings.Value, ct);
                    Match spade = Regex.Match(settingsJs, "\"spade_url\":\"(https://[^\"]+)\"");
                    if (spade.Success)
                    {
                        _spadeUrl = spade.Groups[1].Value;
                        AppLogger.Info("TwitchWatch", $"Resolved spade URL: {_spadeUrl}");
                        return _spadeUrl;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("TwitchWatch", $"Spade URL resolution failed, using fallback: {ex.Message}");
            }

            _spadeUrl = "https://spade.twitch.tv/track"; // long-stable fallback endpoint
            return _spadeUrl;
        }
        /// <summary>
        /// Attempts to claim a Twitch drop reward for the specified campaign and reward identifiers asynchronously.
        /// </summary>
        /// <remarks>Returns <see langword="false"/> if the claim request fails or if the response
        /// contains errors. This method requires valid authentication and may refresh headers automatically if
        /// needed.</remarks>
        /// <param name="campaignId">The unique identifier of the campaign associated with the drop reward to claim.</param>
        /// <param name="rewardId">The unique identifier of the reward to be claimed within the specified campaign.</param>
        /// <param name="ct">A cancellation token that can be used to cancel the operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result is <see langword="true"/> if the drop
        /// reward was successfully claimed; otherwise, <see langword="false"/>.</returns>
        public async Task<bool> ClaimDropAsync(string campaignId, string rewardId, CancellationToken ct = default)
        {
            AppLogger.Info("TwitchGql", $"ClaimDrop started. campaignId={campaignId}, rewardId={rewardId}");
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () => await RefreshHeadersAsync(ct));

            // Step 1. Construct the payload, according to the above format
            string operationName = "DropsPage_ClaimDropRewards";
            string dropInstanceID = $"{_userId}#{campaignId}#{rewardId}";
            string hash = "a455deea71bdc9015b78eb49f4acfbce8baa7ccbedd28e549bb025bd0f751930";
            SetCachedHash(operationName, hash);

            JsonArray payload = new JsonArray
            {
                new JsonObject
                {
                    ["operationName"] = operationName,
                    ["variables"] = new JsonObject
                    {
                        ["input"] = new JsonObject
                        {
                            ["dropInstanceID"] = dropInstanceID
                        }
                    },
                    ["extensions"] = new JsonObject
                    {
                        ["persistedQuery"] = new JsonObject
                        {
                            ["version"] = 1,
                            ["sha256Hash"] = hash
                        }
                    }
                }
            };

            // Step 2. Send the request
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "https://gql.twitch.tv/gql")
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.TryAddWithoutValidation("Client-ID", _clientId);
            request.Headers.TryAddWithoutValidation("Client-Integrity", _integrityToken);
            request.Headers.TryAddWithoutValidation("Authorization", _accessToken);

            if (!string.IsNullOrEmpty(_deviceId))
                request.Headers.TryAddWithoutValidation("X-Device-Id", _deviceId);

            HttpResponseMessage response = await _httpClient.SendAsync(request, ct);
            string jsonText = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode || jsonText.Contains("\"errors\""))
            {
                AppLogger.Warn("TwitchGql", $"ClaimDrop failed. status={(int)response.StatusCode}, hasErrors={jsonText.Contains("\"errors\"")}");
                return false;
            }

            response.EnsureSuccessStatusCode();
            JsonNode? root = JsonNode.Parse(jsonText);
            JsonNode? result = root?[0];

            bool isConnected = result?["data"]?
                        ["claimDropRewards"]?
                        ["isUserAccountConnected"]?
                        .GetValue<bool>()
                  ?? false;

            AppLogger.Info("TwitchGql", $"ClaimDrop completed. success={isConnected}");

            return isConnected;
        }
        /// <summary>
        /// Queries the Twitch Drops dashboard and returns the full dashboard data as a JSON object.
        /// </summary>
        /// <remarks>This method performs multiple GraphQL queries to retrieve both inventory and
        /// dashboard information in a single request. The returned JSON object corresponds to the
        /// 'ViewerDropsDashboard' response. If authentication headers are invalid or expired, the method automatically
        /// refreshes them and retries the request.</remarks>
        /// <param name="ct">A cancellation token that can be used to cancel the operation.</param>
        /// <returns>A JSON object containing the data from the Twitch Drops dashboard. The object includes information about the
        /// user's drops campaigns and inventory.</returns>
        public async Task<JsonArray> QueryFullDropsDashboardAsync(CancellationToken ct = default, bool allowWebViewFallback = false)
        {
            await RefreshHeadersAsync(ct);

            // Fetch hashes
            string dashboardHash = await GetPersistedQueryHashAsync("ViewerDropsDashboard", ct);
            string inventoryHash = await GetPersistedQueryHashAsync("Inventory", ct, "https://www.twitch.tv/drops/inventory");

            JsonArray payload = BuildDashboardPayload(inventoryHash, dashboardHash);

            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "https://gql.twitch.tv/gql")
            {
                Content = JsonContent.Create(payload)
            };

            request.Headers.TryAddWithoutValidation("Client-ID", _clientId);
            request.Headers.TryAddWithoutValidation("Client-Integrity", _integrityToken);
            request.Headers.TryAddWithoutValidation("Authorization", _accessToken);

            if (!string.IsNullOrEmpty(_deviceId))
                request.Headers.TryAddWithoutValidation("X-Device-Id", _deviceId);

            HttpResponseMessage response = await _httpClient.SendAsync(request, ct);
            string jsonText = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode || jsonText.Contains("\"errors\""))
            {
                AppLogger.Warn("TwitchGql", $"QueryFullDropsDashboard initial call failed. status={(int)response.StatusCode}, hasErrors={jsonText.Contains("\"errors\"")}. Refreshing headers and retrying.");
                await RefreshHeadersAsync(ct, force: true);

                dashboardHash = await GetPersistedQueryHashAsync("ViewerDropsDashboard", ct, allowCached: false);
                inventoryHash = await GetPersistedQueryHashAsync("Inventory", ct, "https://www.twitch.tv/drops/inventory", allowCached: false);
                payload = BuildDashboardPayload(inventoryHash, dashboardHash);

                using HttpRequestMessage newRequest = new HttpRequestMessage(HttpMethod.Post, "https://gql.twitch.tv/gql")
                {
                    Content = JsonContent.Create(payload)
                };

                newRequest.Headers.TryAddWithoutValidation("Client-ID", _clientId);
                newRequest.Headers.TryAddWithoutValidation("Client-Integrity", _integrityToken);
                newRequest.Headers.TryAddWithoutValidation("Authorization", _accessToken);

                if (!string.IsNullOrEmpty(_deviceId))
                    newRequest.Headers.TryAddWithoutValidation("X-Device-Id", _deviceId);

                response = await _httpClient.SendAsync(newRequest, ct);
                jsonText = await response.Content.ReadAsStringAsync(ct);

                if (jsonText.Contains("\"errors\""))
                {
                    AppLogger.Error("TwitchGql", "QueryFullDropsDashboard retry still returned GraphQL errors.");

                    // Token-replay keeps failing Twitch's integrity check. Fall back to reading the dashboard the way
                    // the real browser does: navigate the WebView to /drops/campaigns and capture its own response —
                    // the browser solves the Kasada challenge natively, so this works where the replay can't.
                    if (allowWebViewFallback)
                    {
                        try
                        {
                            JsonArray native = await QueryFullDropsDashboardViaWebViewAsync(ct);
                            LastDashboardFetchFailed = false;
                            AppLogger.Info("TwitchGql", "QueryFullDropsDashboard recovered via native WebView capture.");
                            return native;
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Warn("TwitchGql", $"Native WebView dashboard fallback failed: {ex.Message}");
                        }
                    }

                    LastDashboardFetchFailed = true;
                    throw new InvalidOperationException("Failed integrity, please wait a while and try again.");
                }
            }

            response.EnsureSuccessStatusCode();

            JsonArray responseArray = JsonNode.Parse(jsonText)!.AsArray();
            LastDashboardFetchFailed = false;
            AppLogger.Info("TwitchGql", "QueryFullDropsDashboard completed successfully.");
            return responseArray;
        }

        /// <summary>
        /// Reads the drops dashboard the way the real site does: navigates the WebView to /drops/campaigns and captures
        /// the browser's own (post-integrity-challenge) GraphQL response, then reshapes it into the array the callers
        /// expect ([0] = inventory/in-progress, [1] = ViewerDropsDashboard with dropCampaigns). Used as a fallback when
        /// the HttpClient token-replay keeps failing Twitch's integrity check. Must only be called while the watcher is
        /// paused, since it navigates the shared Twitch WebView away from any watched stream.
        /// </summary>
        private async Task<JsonArray> QueryFullDropsDashboardViaWebViewAsync(CancellationToken ct)
        {
            string body = await _host.CaptureViewerDropsDashboardResponseAsync(20000, ct);
            JsonNode? parsed = JsonNode.Parse(body);

            JsonObject? dropsDashboard = null;
            JsonObject? inventory = null;

            IEnumerable<JsonNode?> elements = parsed is JsonArray arr ? arr : new[] { parsed };
            foreach (JsonNode? el in elements)
            {
                if (el is not JsonObject obj)
                    continue;
                JsonNode? currentUser = obj["data"]?["currentUser"];
                if (currentUser == null)
                    continue;
                if (dropsDashboard == null && currentUser["dropCampaigns"] is JsonArray)
                    dropsDashboard = obj;
                if (inventory == null && currentUser["inventory"]?["dropCampaignsInProgress"] is JsonArray)
                    inventory = obj;
            }

            if (dropsDashboard == null)
                throw new InvalidOperationException("Native dashboard capture did not contain a dropCampaigns array.");

            // Mirror the batch shape the providers index into: [0] = inventory, [1] = ViewerDropsDashboard.
            return new JsonArray(
                (JsonNode?)(inventory?.DeepClone()) ?? new JsonObject(),
                dropsDashboard.DeepClone());
        }
        /// <summary>
        /// Retrieves detailed information for multiple Twitch drop campaigns in a single batch operation.
        /// </summary>
        /// <remarks>The method processes requests in batches to optimize network usage. If authentication
        /// headers are invalid or expired, they are refreshed automatically. The returned dictionary may contain fewer
        /// entries than requested if some campaigns are not found or accessible.</remarks>
        /// <param name="requests">A read-only list of tuples, each containing a drop campaign ID and the associated channel login for which to
        /// retrieve details.</param>
        /// <param name="ct">A cancellation token that can be used to cancel the operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains a dictionary mapping each drop
        /// campaign ID to its corresponding details as a JsonObject. If a campaign is not found, it will not be
        /// included in the dictionary.</returns>
        public async Task<Dictionary<string, JsonObject>> QueryDropCampaignDetailsBatchAsync(IReadOnlyList<(string dropID, string channelLogin)> requests, CancellationToken ct = default)
        {
            if (_clientId == null || _integrityToken == null)
                await RefreshHeadersAsync(ct);

            // 1. Get the REAL current hash
            string liveHash = await GetCurrentDropCampaignDetailsHashInternalAsync(allowCached: true, ct);

            Dictionary<string, JsonObject> results = new Dictionary<string, JsonObject>();
            const int batchSize = 20;

            for (int i = 0; i < requests.Count; i += batchSize)
            {
                List<(string dropID, string channelLogin)> batch = requests.Skip(i).Take(batchSize).ToList();

                JsonArray payload = BuildDropCampaignDetailsPayload(batch, liveHash);

                using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "https://gql.twitch.tv/gql")
                {
                    Content = JsonContent.Create(payload)
                };

                request.Headers.TryAddWithoutValidation("Client-ID", _clientId);
                request.Headers.TryAddWithoutValidation("Client-Integrity", _integrityToken);
                request.Headers.TryAddWithoutValidation("Authorization", _accessToken);
                if (!string.IsNullOrEmpty(_deviceId))
                    request.Headers.TryAddWithoutValidation("X-Device-Id", _deviceId);

                HttpResponseMessage response = await _httpClient.SendAsync(request, ct);
                string jsonText = await response.Content.ReadAsStringAsync(ct);

                // print the payload as json text for debugging
                AppLogger.Debug("TwitchGql", request.Content != null
                    ? await request.Content.ReadAsStringAsync(ct)
                    : "No request content");

                // Auto-retry on integrity fail
                if (!response.IsSuccessStatusCode || jsonText.Contains("\"errors\""))
                {
                    AppLogger.Warn("TwitchGql", $"DropCampaignDetails batch call failed. batchStart={i}, status={(int)response.StatusCode}, hasErrors={jsonText.Contains("\"errors\"")}. Refreshing headers and retrying.");
                    await RefreshHeadersAsync(ct, force: true);

                    liveHash = await GetCurrentDropCampaignDetailsHashInternalAsync(allowCached: false, ct);
                    payload = BuildDropCampaignDetailsPayload(batch, liveHash);

                    using HttpRequestMessage retryRequest = new HttpRequestMessage(HttpMethod.Post, "https://gql.twitch.tv/gql")
                    {
                        Content = JsonContent.Create(payload)
                    };

                    retryRequest.Headers.TryAddWithoutValidation("Client-ID", _clientId);
                    retryRequest.Headers.TryAddWithoutValidation("Client-Integrity", _integrityToken);
                    retryRequest.Headers.TryAddWithoutValidation("Authorization", _accessToken);
                    if (!string.IsNullOrEmpty(_deviceId))
                        retryRequest.Headers.TryAddWithoutValidation("X-Device-Id", _deviceId);

                    response = await _httpClient.SendAsync(retryRequest, ct);
                    jsonText = await response.Content.ReadAsStringAsync(ct);
                }

                response.EnsureSuccessStatusCode();
                JsonArray batchResponse = JsonNode.Parse(jsonText)!.AsArray();

                // Map responses back to dropID
                for (int j = 0; j < batch.Count; j++)
                {
                    JsonObject? data = batchResponse[j]?["data"]?["user"]?["dropCampaign"]?.AsObject();
                    if (data != null)
                        results[batch[j].dropID] = data;
                }
            }

            AppLogger.Debug("TwitchGql", $"[GQL] Fetched {results.Count} campaigns with full details");
            AppLogger.Info("TwitchGql", $"DropCampaignDetails fetch completed. totalResults={results.Count}");
            return results;
        }
        /// <summary>
        /// Asynchronously retrieves the SHA-256 hash of the current Twitch Drop campaign details by simulating user
        /// interaction with the Twitch Drops campaigns page.
        /// </summary>
        /// <remarks>This method navigates to the Twitch Drops campaigns page, triggers the loading of
        /// campaign details, and captures the associated GraphQL request to extract the hash. The operation may take
        /// several seconds to complete due to required page loading and interaction delays.</remarks>
        /// <param name="ct">A cancellation token that can be used to cancel the operation.</param>
        /// <returns>A string containing the SHA-256 hash of the current DropCampaignDetails GraphQL request.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the DropCampaignDetails hash cannot be found on the page.</exception>
        public async Task<string> GetCurrentDropCampaignDetailsHashAsync(CancellationToken ct = default)
        {
            return await GetCurrentDropCampaignDetailsHashInternalAsync(allowCached: true, ct);
        }

        private async Task<string> GetCurrentDropCampaignDetailsHashInternalAsync(bool allowCached, CancellationToken ct = default)
        {
            const string operationName = "DropCampaignDetails";

            if (allowCached && TryGetCachedHash(operationName, requireFresh: true, out string? cachedHash))
            {
                LogCacheDebug("Using cached hash for 'DropCampaignDetails'.");
                return cachedHash!;
            }

            // 1. Go to drops page
            await _host.NavigateAsync($"https://www.twitch.tv/drops/campaigns?t={DateTimeOffset.Now.ToUnixTimeMilliseconds()}");

            string clickScript = @"
                (async () => {
                    // Wait a bit more for React to render
                    await new Promise(r => setTimeout(r, 3000));

                    document.querySelectorAll(
                      '[role=""heading""][aria-level=""3""] button'
                    ).forEach(ind => ind.closest('button')?.click());

                })();
            ";

            // 3. Capture the real payload
            string payloadJson;
            try
            {
                payloadJson = await _host.CaptureGqlRequestBodyContainingAsyncWithRetry("DropCampaignDetails", 8000, 10, clickScript, ct);
            }
            catch (Exception ex) when (allowCached && TryGetCachedHash(operationName, requireFresh: false, out string? fallbackHash))
            {
                LogCacheWarn($"DropCampaignDetails capture failed; using cached fallback. {ex.Message}");
                return fallbackHash!;
            }

            // 4. Parse just the hash
            JsonArray payload = JsonNode.Parse(payloadJson)!.AsArray();

            string? hash = payload
                .OfType<JsonObject>()
                .Where(op => op["operationName"]?.GetValue<string>() == "DropCampaignDetails")
                .Select(op => op["extensions"]?["persistedQuery"]?["sha256Hash"]?.GetValue<string>())
                .FirstOrDefault(h => h != null);

            if (string.IsNullOrEmpty(hash))
                throw new InvalidOperationException("DropCampaignDetails hash not found - try again");

            SetCachedHash(operationName, hash);

            AppLogger.Debug("TwitchGql", $"[GQL] Live DropCampaignDetails hash captured: {hash}");
            return hash!;
        }

        private static JsonArray BuildDashboardPayload(string inventoryHash, string dashboardHash)
        {
            return new JsonArray
            {
                new JsonObject
                {
                    ["operationName"] = "Inventory",
                    ["variables"] = new JsonObject { ["fetchRewardCampaigns"] = true },
                    ["extensions"] = new JsonObject
                    {
                        ["persistedQuery"] = new JsonObject
                        {
                            ["version"] = 1,
                            ["sha256Hash"] = inventoryHash
                        }
                    }
                },
                new JsonObject
                {
                    ["operationName"] = "ViewerDropsDashboard",
                    ["variables"] = new JsonObject { ["fetchRewardCampaigns"] = true },
                    ["extensions"] = new JsonObject
                    {
                        ["persistedQuery"] = new JsonObject
                        {
                            ["version"] = 1,
                            ["sha256Hash"] = dashboardHash
                        }
                    }
                }
            };
        }

        private static JsonArray BuildDropCampaignDetailsPayload(IReadOnlyList<(string dropID, string channelLogin)> batch, string hash)
        {
            JsonArray payload = new();

            foreach ((string dropID, string channelLogin) in batch)
            {
                payload.Add(new JsonObject
                {
                    ["operationName"] = "DropCampaignDetails",
                    ["variables"] = new JsonObject
                    {
                        ["dropID"] = dropID,
                        ["channelLogin"] = channelLogin
                    },
                    ["extensions"] = new JsonObject
                    {
                        ["persistedQuery"] = new JsonObject
                        {
                            ["version"] = 1,
                            ["sha256Hash"] = hash
                        }
                    }
                });
            }

            return payload;
        }

        private bool TryGetCachedHash(string operationName, bool requireFresh, out string? hash)
        {
            lock (_hashCacheSync)
            {
                if (!_gqlHashCache.TryGetValue(operationName, out GqlHashCacheEntry? entry) || string.IsNullOrWhiteSpace(entry.Hash))
                {
                    hash = null;
                    return false;
                }

                hash = entry.Hash;
                return true;
            }
        }

        private void SetCachedHash(string operationName, string hash)
        {
            if (string.IsNullOrWhiteSpace(operationName) || string.IsNullOrWhiteSpace(hash))
                return;

            lock (_hashCacheSync)
            {
                _gqlHashCache[operationName] = new GqlHashCacheEntry
                {
                    Hash = hash,
                    UpdatedUtc = DateTimeOffset.UtcNow
                };
            }

            SaveHashCacheToDisk();
        }

        private void LoadHashCacheFromDisk()
        {
            try
            {
                if (!File.Exists(_gqlHashCacheFilePath))
                    return;

                string json = File.ReadAllText(_gqlHashCacheFilePath, Encoding.UTF8);
                Dictionary<string, GqlHashCacheEntry>? loaded = JsonSerializer.Deserialize<Dictionary<string, GqlHashCacheEntry>>(json);

                if (loaded == null || loaded.Count == 0)
                    return;

                lock (_hashCacheSync)
                {
                    _gqlHashCache.Clear();

                    foreach ((string key, GqlHashCacheEntry value) in loaded)
                    {
                        if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value.Hash))
                            _gqlHashCache[key] = value;
                    }
                }

                LogCacheInfo($"Loaded {_gqlHashCache.Count} cached GQL hash entries.");
            }
            catch (Exception ex)
            {
                LogCacheWarn($"Failed to load cache file. {ex.Message}");
            }
        }

        private void SaveHashCacheToDisk()
        {
            try
            {
                Dictionary<string, GqlHashCacheEntry> snapshot;

                lock (_hashCacheSync)
                {
                    snapshot = _gqlHashCache.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
                }

                string? directory = Path.GetDirectoryName(_gqlHashCacheFilePath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                string json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_gqlHashCacheFilePath, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                LogCacheWarn($"Failed to save cache file. {ex.Message}");
            }
        }

        private static bool IsVerboseCacheLoggingEnabled()
        {
            try
            {
                return UISettingsManager.Instance.VerboseDebugLogging;
            }
            catch
            {
                return false;
            }
        }

        private static void LogCacheDebug(string message)
        {
            if (IsVerboseCacheLoggingEnabled())
                AppLogger.Debug("TwitchGql", $"[HashCache] {message}");
        }

        private static void LogCacheInfo(string message)
        {
            if (IsVerboseCacheLoggingEnabled())
                AppLogger.Info("TwitchGql", $"[HashCache] {message}");
        }

        private static void LogCacheWarn(string message)
        {
            if (IsVerboseCacheLoggingEnabled())
                AppLogger.Warn("TwitchGql", $"[HashCache] {message}");
        }

        private sealed class GqlHashCacheEntry
        {
            public string Hash { get; set; } = string.Empty;
            public DateTimeOffset UpdatedUtc { get; set; }
        }

        public void Dispose()
        {
            _httpClient.Dispose();
            _headerRefreshLock.Dispose();
        }
    }
}
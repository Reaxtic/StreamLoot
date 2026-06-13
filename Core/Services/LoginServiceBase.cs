using Core.Interfaces;
using Core.Enums;

namespace Core.Services
{
    public abstract class LoginServiceBase : ILoginService
    {
        private ConnectionStatus _connectionStatus;
        public ConnectionStatus? Status
        {
            get => _connectionStatus;
            protected set
            {
                if (value.HasValue && _connectionStatus != value.Value)
                {
                    _connectionStatus = value.Value;
                    UpdateStatus(_connectionStatus);
                }
            }
        }

        protected LoginServiceBase()
        {
            StatusChanged += status => {
                _connectionStatus = status;
            };
        }

        /// <summary>
        /// Occurs when the connection status changes.
        /// </summary>
        /// <remarks>Subscribers are notified whenever the connection status transitions to a new state.
        /// Handlers receive the updated <see cref="ConnectionStatus"/> value as an argument. This event is typically
        /// used to monitor connectivity and respond to status changes in real time.</remarks>
        public event Action<ConnectionStatus>? StatusChanged;
        /// <summary>
        /// Raises the StatusChanged event to notify subscribers of a change in connection status.
        /// </summary>
        /// <remarks>This method should be called whenever the connection status changes to ensure that
        /// all registered listeners are notified. If there are no subscribers to the StatusChanged event, this method
        /// has no effect.</remarks>
        /// <param name="status">The new connection status to be provided to event subscribers.</param>
        protected void UpdateStatus(ConnectionStatus status) => StatusChanged?.Invoke(status);
        public abstract Task ValidateCredentialsAsync(IWebViewHost host);
        protected static async Task<string> GetPageHtmlAsync(IWebViewHost host)
        {
            string htmlRaw = await host.ExecuteScriptAsync("document.documentElement.outerHTML;");
            return System.Text.Json.JsonSerializer.Deserialize<string>(htmlRaw) ?? "";
        }

        /// <summary>
        /// Determines login state from the platform's auth cookie — the authoritative session signal the app
        /// already uses for every API call. This is deterministic, unlike scraping a single-page app's HTML
        /// (which races React hydration and intermittently reported a logged-in user as logged out).
        /// Retries briefly because the cookie store can lag a fresh navigation.
        /// </summary>
        protected static async Task<bool> IsLoggedInByCookieAsync(IWebViewHost host, string url, string cookieName, int attempts = 4)
        {
            for (int i = 0; i < attempts; i++)
            {
                try
                {
                    string? value = await host.GetCookieValueAsync(url, cookieName);
                    if (!string.IsNullOrWhiteSpace(value))
                        return true;
                }
                catch
                {
                    // Cookie store may not be ready immediately after navigation; fall through and retry.
                }

                if (i < attempts - 1)
                {
                    try { await host.WaitForNetworkIdleAsync(2500, 500); }
                    catch { await Task.Delay(700); }
                }
            }

            return false;
        }
    }
}
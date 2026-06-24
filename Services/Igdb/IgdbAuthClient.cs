using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Playnite.SDK;

namespace PlayPresence.Services.Igdb
{
    /// <summary>
    /// Obtains and caches a Twitch (IGDB) application access token using the OAuth2
    /// client-credentials flow. Tokens are refreshed proactively before expiry and access is
    /// serialized so concurrent callers never trigger duplicate token requests.
    /// </summary>
    public sealed class IgdbAuthClient : IDisposable
    {
        private const string TokenEndpoint = "https://id.twitch.tv/oauth2/token";

        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly HttpClient httpClient;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

        private string clientId;
        private string clientSecret;

        private string cachedToken;
        private DateTime tokenExpiresUtc = DateTime.MinValue;

        public IgdbAuthClient(HttpClient httpClient)
        {
            this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        /// <summary>Replace the credentials and invalidate any cached token.</summary>
        public void Configure(string newClientId, string newClientSecret)
        {
            if (clientId != newClientId || clientSecret != newClientSecret)
            {
                clientId = newClientId;
                clientSecret = newClientSecret;
                cachedToken = null;
                tokenExpiresUtc = DateTime.MinValue;
            }
        }

        public bool HasCredentials =>
            !string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret);

        public string ClientId => clientId;

        /// <summary>Force the next call to obtain a fresh token (used after a 401 from IGDB).</summary>
        public void InvalidateToken()
        {
            cachedToken = null;
            tokenExpiresUtc = DateTime.MinValue;
        }

        /// <summary>
        /// Returns a valid access token, fetching or refreshing one if needed.
        /// Throws on failure so callers can degrade gracefully.
        /// </summary>
        public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            if (!HasCredentials)
            {
                throw new InvalidOperationException("IGDB credentials are not configured.");
            }

            if (IsTokenValid())
            {
                return cachedToken;
            }

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (IsTokenValid())
                {
                    return cachedToken;
                }

                var url = $"{TokenEndpoint}?client_id={Uri.EscapeDataString(clientId)}" +
                          $"&client_secret={Uri.EscapeDataString(clientSecret)}" +
                          "&grant_type=client_credentials";

                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                using (var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException(
                            $"Twitch token request failed ({(int)response.StatusCode}). " +
                            "Verify the IGDB Client ID and Client Secret.");
                    }

                    var parsed = JsonConvert.DeserializeObject<TwitchTokenResponse>(body);
                    if (parsed == null || string.IsNullOrWhiteSpace(parsed.AccessToken))
                    {
                        throw new InvalidOperationException("Twitch token response did not contain an access token.");
                    }

                    cachedToken = parsed.AccessToken;
                    // Refresh five minutes early to avoid edge-of-expiry failures.
                    var lifetime = Math.Max(60, parsed.ExpiresIn - 300);
                    tokenExpiresUtc = DateTime.UtcNow.AddSeconds(lifetime);
                    Logger.Info("PlayPresence: acquired IGDB access token.");
                    return cachedToken;
                }
            }
            finally
            {
                gate.Release();
            }
        }

        private bool IsTokenValid()
        {
            return !string.IsNullOrEmpty(cachedToken) && DateTime.UtcNow < tokenExpiresUtc;
        }

        public void Dispose()
        {
            gate.Dispose();
        }
    }
}

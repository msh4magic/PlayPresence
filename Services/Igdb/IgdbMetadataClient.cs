using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Playnite.SDK;

namespace PlayPresence.Services.Igdb
{
    /// <summary>
    /// Thin, resilient client over the IGDB v4 REST API. Handles authentication headers,
    /// the 4-requests/second rate limit, transient-failure retries with backoff, and builds
    /// crisp image URLs from IGDB image ids.
    /// </summary>
    public sealed class IgdbMetadataClient : IDisposable
    {
        private const string GamesEndpoint = "https://api.igdb.com/v4/games";
        private const string PlatformsEndpoint = "https://api.igdb.com/v4/platforms";
        private const string ImageBaseUrl = "https://images.igdb.com/igdb/image/upload";

        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly HttpClient httpClient;
        private readonly IgdbAuthClient auth;
        private readonly SemaphoreSlim rateLimiter;

        public IgdbMetadataClient(HttpClient httpClient, IgdbAuthClient auth, int maxConcurrentRequests = 3)
        {
            this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            this.auth = auth ?? throw new ArgumentNullException(nameof(auth));
            rateLimiter = new SemaphoreSlim(Math.Max(1, Math.Min(maxConcurrentRequests, 4)));
        }

        public bool IsConfigured => auth.HasCredentials;

        /// <summary>Search games by title, returning rich candidate records for scoring.</summary>
        public async Task<List<IgdbGame>> SearchGamesAsync(string title, int limit, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return new List<IgdbGame>();
            }

            var safeTitle = title.Replace("\"", " ").Trim();
            var query =
                "fields name, url, first_release_date, total_rating_count, " +
                "cover.image_id, artworks.image_id, alternative_names.name, " +
                "genres.name, involved_companies.developer, involved_companies.company.name; " +
                $"search \"{safeTitle}\"; limit {Math.Max(1, Math.Min(limit, 25))};";

            var json = await PostAsync(GamesEndpoint, query, cancellationToken).ConfigureAwait(false);
            return JsonConvert.DeserializeObject<List<IgdbGame>>(json) ?? new List<IgdbGame>();
        }

        /// <summary>Fetch a single game by its exact IGDB id (used for manual match overrides).</summary>
        public async Task<IgdbGame> GetGameByIdAsync(long id, CancellationToken cancellationToken)
        {
            if (id <= 0)
            {
                return null;
            }

            var query =
                "fields name, url, first_release_date, total_rating_count, " +
                "cover.image_id, artworks.image_id, alternative_names.name, " +
                "genres.name, involved_companies.developer, involved_companies.company.name; " +
                $"where id = {id}; limit 1;";

            var json = await PostAsync(GamesEndpoint, query, cancellationToken).ConfigureAwait(false);
            var games = JsonConvert.DeserializeObject<List<IgdbGame>>(json) ?? new List<IgdbGame>();
            return games.Count > 0 ? games[0] : null;
        }

        /// <summary>Look up a platform (for its logo) by name.</summary>
        public async Task<IgdbPlatform> FindPlatformAsync(string platformName, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(platformName))
            {
                return null;
            }

            var safeName = platformName.Replace("\"", " ").Trim();
            var query =
                "fields name, platform_logo.image_id; " +
                $"search \"{safeName}\"; limit 5;";

            var json = await PostAsync(PlatformsEndpoint, query, cancellationToken).ConfigureAwait(false);
            var platforms = JsonConvert.DeserializeObject<List<IgdbPlatform>>(json) ?? new List<IgdbPlatform>();

            // Prefer an exact (case-insensitive) name match, else the first result with a logo.
            foreach (var platform in platforms)
            {
                if (string.Equals(platform.Name, platformName, StringComparison.OrdinalIgnoreCase)
                    && platform.PlatformLogo != null)
                {
                    return platform;
                }
            }
            foreach (var platform in platforms)
            {
                if (platform.PlatformLogo != null)
                {
                    return platform;
                }
            }
            return platforms.Count > 0 ? platforms[0] : null;
        }

        /// <summary>Build a crisp image URL from an IGDB image id.</summary>
        public static string BuildImageUrl(string imageId, string size)
        {
            if (string.IsNullOrWhiteSpace(imageId))
            {
                return null;
            }
            return $"{ImageBaseUrl}/t_{size}/{imageId}.png";
        }

        private async Task<string> PostAsync(string endpoint, string apicalypseQuery, CancellationToken cancellationToken)
        {
            const int maxAttempts = 4;
            var attempt = 0;

            while (true)
            {
                attempt++;
                cancellationToken.ThrowIfCancellationRequested();

                await rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var token = await auth.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

                    using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
                    {
                        request.Headers.TryAddWithoutValidation("Client-ID", auth.ClientId);
                        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                        request.Content = new StringContent(apicalypseQuery, Encoding.UTF8, "text/plain");

                        using (var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false))
                        {
                            if (response.IsSuccessStatusCode)
                            {
                                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            }

                            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt < maxAttempts)
                            {
                                // Token may have been revoked early; force a refresh and retry.
                                auth.InvalidateToken();
                            }

                            if ((IsTransient(response.StatusCode) || response.StatusCode == HttpStatusCode.Unauthorized)
                                && attempt < maxAttempts)
                            {
                                // fall through to backoff
                            }
                            else
                            {
                                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                                throw new HttpRequestException(
                                    $"IGDB request failed ({(int)response.StatusCode}): {Truncate(body, 200)}");
                            }
                        }
                    }
                }
                finally
                {
                    rateLimiter.Release();
                }

                // Exponential backoff with light jitter for 429/5xx.
                var delayMs = (int)(Math.Pow(2, attempt) * 150) + new Random().Next(0, 120);
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }
        }

        private static bool IsTransient(HttpStatusCode status)
        {
            return status == (HttpStatusCode)429
                || status == HttpStatusCode.InternalServerError
                || status == HttpStatusCode.BadGateway
                || status == HttpStatusCode.ServiceUnavailable
                || status == HttpStatusCode.GatewayTimeout;
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max)
            {
                return value;
            }
            return value.Substring(0, max) + "...";
        }

        public void Dispose()
        {
            rateLimiter.Dispose();
        }
    }
}

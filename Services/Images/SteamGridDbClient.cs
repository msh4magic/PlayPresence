using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Playnite.SDK;

namespace PlayPresence.Services.Images
{
    // --- Minimal SteamGridDB response models -------------------------------

    internal sealed class SgdbSearchResponse
    {
        [JsonProperty("success")] public bool Success { get; set; }
        [JsonProperty("data")] public List<SgdbGame> Data { get; set; }
    }

    internal sealed class SgdbGame
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
    }

    internal sealed class SgdbGridResponse
    {
        [JsonProperty("success")] public bool Success { get; set; }
        [JsonProperty("data")] public List<SgdbGrid> Data { get; set; }
    }

    internal sealed class SgdbGrid
    {
        [JsonProperty("url")] public string Url { get; set; }
        [JsonProperty("thumb")] public string Thumb { get; set; }
        [JsonProperty("width")] public int Width { get; set; }
        [JsonProperty("height")] public int Height { get; set; }
    }

    /// <summary>
    /// Lightweight client for SteamGridDB. Used as a higher-quality / fallback art source: its
    /// grids are available in square-ish ratios that look noticeably better than tall IGDB covers
    /// inside Discord's large-image slot. Requires a free user API key.
    /// </summary>
    public sealed class SteamGridDbClient : IDisposable
    {
        private const string SearchEndpoint = "https://www.steamgriddb.com/api/v2/search/autocomplete/";
        private const string GridsEndpoint = "https://www.steamgriddb.com/api/v2/grids/game/";
        private const string IconsEndpoint = "https://www.steamgriddb.com/api/v2/icons/game/";

        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly HttpClient httpClient;
        private readonly SemaphoreSlim rateLimiter = new SemaphoreSlim(2);
        private string apiKey;

        public SteamGridDbClient(HttpClient httpClient)
        {
            this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(apiKey);

        public void Configure(string newApiKey)
        {
            apiKey = newApiKey;
        }

        /// <summary>
        /// Returns the best square cover URL for the given title, or null if none/unconfigured.
        /// Prefers 1:1 grids (600x600 / 1024x1024); falls back to the first available grid.
        /// </summary>
        public async Task<string> FindCoverUrlAsync(string title, CancellationToken cancellationToken)
        {
            if (!IsConfigured || string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            try
            {
                var gameId = await SearchGameIdAsync(title, cancellationToken).ConfigureAwait(false);
                if (gameId <= 0)
                {
                    return null;
                }

                // Ask for square ratios first; SteamGridDB returns best matches.
                var grids = await GetGridsAsync(gameId, cancellationToken).ConfigureAwait(false);
                if (grids == null || grids.Count == 0)
                {
                    return null;
                }

                foreach (var grid in grids)
                {
                    if (grid.Width > 0 && grid.Height > 0 && grid.Width == grid.Height &&
                        !string.IsNullOrWhiteSpace(grid.Url))
                    {
                        return grid.Url;
                    }
                }
                // No perfectly-square art; return the first valid url as a fallback.
                foreach (var grid in grids)
                {
                    if (!string.IsNullOrWhiteSpace(grid.Url))
                    {
                        return grid.Url;
                    }
                }
                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "PlayPresence: SteamGridDB lookup failed.");
                return null;
            }
        }

        /// <summary>
        /// Returns the best square game ICON URL (not a cropped cover), or null. Prefers PNG/WebP
        /// icons at 1:1; this is what gives Discord a clean square icon look.
        /// </summary>
        public async Task<string> FindIconUrlAsync(string title, CancellationToken cancellationToken)
        {
            if (!IsConfigured || string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            try
            {
                var gameId = await SearchGameIdAsync(title, cancellationToken).ConfigureAwait(false);
                if (gameId <= 0)
                {
                    return null;
                }

                var url = IconsEndpoint + gameId + "?types=static";
                var json = await GetAsync(url, cancellationToken).ConfigureAwait(false);
                var parsed = JsonConvert.DeserializeObject<SgdbGridResponse>(json);
                if (parsed == null || !parsed.Success || parsed.Data == null)
                {
                    return null;
                }

                // Prefer raster, square icons that Discord renders reliably (skip .ico).
                foreach (var icon in parsed.Data)
                {
                    if (!string.IsNullOrWhiteSpace(icon.Url) && IsRasterImage(icon.Url) &&
                        (icon.Width == 0 || icon.Height == 0 || icon.Width == icon.Height))
                    {
                        return icon.Url;
                    }
                }
                foreach (var icon in parsed.Data)
                {
                    if (!string.IsNullOrWhiteSpace(icon.Url) && IsRasterImage(icon.Url))
                    {
                        return icon.Url;
                    }
                }
                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "PlayPresence: SteamGridDB icon lookup failed.");
                return null;
            }
        }

        private static bool IsRasterImage(string url)
        {
            var u = url.ToLowerInvariant();
            // Discord renders png/jpg/webp; .ico is unreliable as a large image.
            return u.Contains(".png") || u.Contains(".jpg") || u.Contains(".jpeg") || u.Contains(".webp");
        }

        private async Task<long> SearchGameIdAsync(string title, CancellationToken cancellationToken)
        {
            var url = SearchEndpoint + Uri.EscapeDataString(title.Trim());
            var json = await GetAsync(url, cancellationToken).ConfigureAwait(false);
            var parsed = JsonConvert.DeserializeObject<SgdbSearchResponse>(json);
            if (parsed != null && parsed.Success && parsed.Data != null && parsed.Data.Count > 0)
            {
                return parsed.Data[0].Id;
            }
            return 0;
        }

        private async Task<List<SgdbGrid>> GetGridsAsync(long gameId, CancellationToken cancellationToken)
        {
            // dimensions covers the common square + portrait sizes; styles=alternate,official.
            var url = GridsEndpoint + gameId + "?dimensions=1024x1024,512x512,600x900,342x482&types=static";
            var json = await GetAsync(url, cancellationToken).ConfigureAwait(false);
            var parsed = JsonConvert.DeserializeObject<SgdbGridResponse>(json);
            return parsed != null && parsed.Success ? parsed.Data : null;
        }

        private async Task<string> GetAsync(string url, CancellationToken cancellationToken)
        {
            await rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    using (var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false))
                    {
                        if (response.StatusCode == HttpStatusCode.Unauthorized ||
                            response.StatusCode == HttpStatusCode.Forbidden)
                        {
                            Logger.Warn("PlayPresence: SteamGridDB rejected the API key.");
                            return string.Empty;
                        }
                        if (!response.IsSuccessStatusCode)
                        {
                            return string.Empty;
                        }
                        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                rateLimiter.Release();
            }
        }

        public void Dispose()
        {
            rateLimiter.Dispose();
        }
    }
}

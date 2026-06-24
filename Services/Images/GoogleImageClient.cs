using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Playnite.SDK;

namespace PlayPresence.Services.Images
{
    // --- Minimal Google Custom Search JSON API models ----------------------

    internal sealed class GoogleSearchResponse
    {
        [JsonProperty("items")] public List<GoogleSearchItem> Items { get; set; }
    }

    internal sealed class GoogleSearchItem
    {
        [JsonProperty("link")] public string Link { get; set; }
        [JsonProperty("mime")] public string Mime { get; set; }
        [JsonProperty("image")] public GoogleImageInfo Image { get; set; }
    }

    internal sealed class GoogleImageInfo
    {
        [JsonProperty("width")] public int Width { get; set; }
        [JsonProperty("height")] public int Height { get; set; }
        [JsonProperty("byteSize")] public long ByteSize { get; set; }
    }

    /// <summary>
    /// Optional, last-resort icon source backed by Google's official Programmable Search
    /// (Custom Search JSON API) with <c>searchType=image</c>. This is deliberately the lowest
    /// item in the art fallback chain because general web results are far less curated than IGDB
    /// or SteamGridDB; to keep results precise and professional it constrains the query, requests
    /// safe + transparent + square-ish raster icons, ranks candidates by squareness, and verifies
    /// the chosen URL actually serves an image before returning it.
    /// </summary>
    /// <remarks>
    /// Requires the user's own free API key and a Programmable Search Engine id (CX). The free tier
    /// allows ~100 queries/day. We never scrape Google HTML — only the sanctioned API is used.
    /// </remarks>
    public sealed class GoogleImageClient : IDisposable
    {
        private const string Endpoint = "https://www.googleapis.com/customsearch/v1";

        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly HttpClient httpClient;
        private readonly SemaphoreSlim rateLimiter = new SemaphoreSlim(1);
        private string apiKey;
        private string searchEngineId;

        public GoogleImageClient(HttpClient httpClient)
        {
            this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(searchEngineId);

        public void Configure(string newApiKey, string newSearchEngineId)
        {
            apiKey = newApiKey;
            searchEngineId = newSearchEngineId;
        }

        /// <summary>
        /// Searches for a square game icon. Returns a verified public image URL or null.
        /// </summary>
        /// <param name="gameName">The resolved game title.</param>
        /// <param name="platformName">Platform name, folded into the query to boost precision.</param>
        public async Task<string> FindIconUrlAsync(string gameName, string platformName, CancellationToken cancellationToken)
        {
            if (!IsConfigured || string.IsNullOrWhiteSpace(gameName))
            {
                return null;
            }

            try
            {
                // Precise query: exact title in quotes + platform + the word "icon".
                var platformPart = string.IsNullOrWhiteSpace(platformName) ? string.Empty : (" " + platformName);
                var query = "\"" + gameName.Trim() + "\"" + platformPart + " game icon";

                var url = Endpoint
                    + "?key=" + Uri.EscapeDataString(apiKey)
                    + "&cx=" + Uri.EscapeDataString(searchEngineId)
                    + "&q=" + Uri.EscapeDataString(query)
                    + "&searchType=image"
                    + "&num=6"
                    + "&safe=active"          // avoid unsafe results
                    + "&imgColorType=trans"   // favour transparent (icon-like) art
                    + "&fileType=png";        // raster icons Discord renders reliably

                var json = await GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(json))
                {
                    return null;
                }

                var parsed = JsonConvert.DeserializeObject<GoogleSearchResponse>(json);
                if (parsed?.Items == null || parsed.Items.Count == 0)
                {
                    return null;
                }

                // Rank candidates: valid raster, reasonable size, most square first.
                var ranked = new List<GoogleSearchItem>(parsed.Items);
                ranked.Sort((a, b) => Squareness(a).CompareTo(Squareness(b)));

                var verified = 0;
                foreach (var item in ranked)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsAcceptable(item))
                    {
                        continue;
                    }
                    if (verified >= 2)
                    {
                        break; // bound latency: verify at most two candidates
                    }
                    verified++;
                    if (await UrlServesImageAsync(item.Link, cancellationToken).ConfigureAwait(false))
                    {
                        Logger.Debug($"PlayPresence: Google image fallback selected for '{gameName}'.");
                        return item.Link;
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
                Logger.Warn(ex, "PlayPresence: Google image fallback failed (non-fatal).");
                return null;
            }
        }

        private static bool IsAcceptable(GoogleSearchItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Link))
            {
                return false;
            }
            if (!item.Link.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return false; // Discord needs https
            }
            var w = item.Image?.Width ?? 0;
            var h = item.Image?.Height ?? 0;
            if (w > 0 && h > 0)
            {
                // Reject extreme aspect ratios and tiny/huge images.
                var ratio = (double)Math.Max(w, h) / Math.Min(w, h);
                if (ratio > 1.6 || Math.Max(w, h) < 48 || Math.Max(w, h) > 2048)
                {
                    return false;
                }
            }
            return true;
        }

        private static double Squareness(GoogleSearchItem item)
        {
            var w = item?.Image?.Width ?? 0;
            var h = item?.Image?.Height ?? 0;
            if (w <= 0 || h <= 0)
            {
                return double.MaxValue; // unknown dimensions ranked last
            }
            return (double)Math.Max(w, h) / Math.Min(w, h); // 1.0 == perfectly square
        }

        private async Task<bool> UrlServesImageAsync(string url, CancellationToken cancellationToken)
        {
            try
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(5));
                    using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        // Only need the headers to confirm it is a live image.
                        using (var response = await httpClient
                            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                            .ConfigureAwait(false))
                        {
                            if (!response.IsSuccessStatusCode)
                            {
                                return false;
                            }
                            var mediaType = response.Content?.Headers?.ContentType?.MediaType;
                            return !string.IsNullOrEmpty(mediaType) &&
                                   mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false; // our own 5s timeout fired
            }
            catch
            {
                return false;
            }
        }

        private async Task<string> GetAsync(string url, CancellationToken cancellationToken)
        {
            await rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using (var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        Logger.Warn($"PlayPresence: Google Custom Search returned {(int)response.StatusCode}.");
                        return string.Empty;
                    }
                    return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
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

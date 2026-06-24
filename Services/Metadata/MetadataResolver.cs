using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PlayPresence.Models;
using PlayPresence.Services.Igdb;
using PlayPresence.Services.Matching;
using Playnite.SDK;

namespace PlayPresence.Services.Metadata
{
    /// <summary>Per-resolve options sourced from the plugin settings (keeps the resolver decoupled).</summary>
    public sealed class MetadataResolverOptions
    {
        public bool IgdbEnabled { get; set; } = true;
        public bool SmallImageEnabled { get; set; } = true;
        public ImageQuality Quality { get; set; } = ImageQuality.High;
        public LargeImageSource LargeImageSource { get; set; } = LargeImageSource.Cover;
        public int CacheDays { get; set; } = 30;
        public double MatchThreshold { get; set; } = MatchScorer.DefaultAcceptThreshold;

        /// <summary>Use SteamGridDB for square, Discord-optimized art (and as an IGDB art fallback).</summary>
        public bool SteamGridDbEnabled { get; set; } = false;

        /// <summary>When true, SteamGridDB art is preferred over the IGDB cover when both exist.</summary>
        public bool PreferSteamGridDb { get; set; } = false;

        /// <summary>Last-resort: use Google Custom Search for an icon when curated sources miss.</summary>
        public bool GoogleFallbackEnabled { get; set; } = false;
    }

    /// <summary>
    /// Default <see cref="IMetadataResolver"/>: normalizes the game/ROM name, searches IGDB,
    /// scores candidates for confidence, enriches with platform logo, caches the result and
    /// degrades gracefully when IGDB is unavailable or no confident match exists.
    /// </summary>
    public sealed class MetadataResolver : IMetadataResolver
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly IgdbMetadataClient igdb;
        private readonly MetadataCache cache;
        private readonly Func<MetadataResolverOptions> optionsProvider;
        private readonly Services.Images.SteamGridDbClient steamGridDb;
        private readonly Services.Images.GoogleImageClient googleImages;

        // Small in-memory cache of platform-name -> logo URL to avoid repeat platform lookups.
        private readonly ConcurrentDictionary<string, string> platformLogoCache =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public MetadataResolver(
            IgdbMetadataClient igdb,
            MetadataCache cache,
            Func<MetadataResolverOptions> optionsProvider,
            Services.Images.SteamGridDbClient steamGridDb = null,
            Services.Images.GoogleImageClient googleImages = null)
        {
            this.igdb = igdb ?? throw new ArgumentNullException(nameof(igdb));
            this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
            this.optionsProvider = optionsProvider ?? throw new ArgumentNullException(nameof(optionsProvider));
            this.steamGridDb = steamGridDb;
            this.googleImages = googleImages;
        }

        public async Task<ResolvedMetadata> ResolveAsync(GameContext context, CancellationToken cancellationToken)
        {
            var options = optionsProvider() ?? new MetadataResolverOptions();

            var primary = ChooseCleanTitle(context, out var hintedYear);
            var overrideSuffix = context.IgdbIdOverride.HasValue ? ("#" + context.IgdbIdOverride.Value) : string.Empty;
            var cacheKey = BuildCacheKey(primary + overrideSuffix, context.PlatformSpecificationId);

            if (cache.TryGet(cacheKey, options.CacheDays, out var cached))
            {
                Logger.Debug($"PlayPresence: metadata cache hit for '{primary}'.");
                return cached;
            }

            ResolvedMetadata resolved;
            try
            {
                if (context.IgdbIdOverride.HasValue && igdb.IsConfigured)
                {
                    resolved = await ResolveByIdAsync(context, options, context.IgdbIdOverride.Value, cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (options.IgdbEnabled && igdb.IsConfigured && !string.IsNullOrWhiteSpace(primary))
                {
                    resolved = await ResolveFromIgdbAsync(context, options, hintedYear, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    resolved = BuildFallback(context);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"PlayPresence: IGDB resolution failed for '{primary}', using fallback.");
                resolved = BuildFallback(context);
            }

            // Optionally upgrade/repair the large image via SteamGridDB (square art for Discord).
            await ApplySteamArtAsync(resolved, context, options, cancellationToken).ConfigureAwait(false);

            cache.Set(cacheKey, resolved);
            return resolved;
        }

        public void Invalidate(GameContext context)
        {
            if (context == null)
            {
                return;
            }
            var primary = ChooseCleanTitle(context, out _);
            var overrideSuffix = context.IgdbIdOverride.HasValue ? ("#" + context.IgdbIdOverride.Value) : string.Empty;
            var key = BuildCacheKey(primary + overrideSuffix, context.PlatformSpecificationId);
            cache.Remove(key);
            Logger.Debug($"PlayPresence: invalidated cache for '{primary}'.");
        }

        private async Task<ResolvedMetadata> ResolveByIdAsync(
            GameContext context, MetadataResolverOptions options, long igdbId, CancellationToken cancellationToken)
        {
            var game = await igdb.GetGameByIdAsync(igdbId, cancellationToken).ConfigureAwait(false);
            if (game == null)
            {
                Logger.Warn($"PlayPresence: IGDB override id {igdbId} returned no game; using fallback.");
                return BuildFallback(context);
            }

            var resolved = new ResolvedMetadata
            {
                IgdbId = game.Id,
                Title = string.IsNullOrWhiteSpace(game.Name) ? context.GameName : game.Name,
                IgdbUrl = game.Url,
                Developer = ExtractDeveloper(game),
                Genre = ExtractGenre(game),
                ReleaseYear = ExtractYear(game) ?? context.ReleaseYear,
                MatchConfidence = 1.0,
                FromIgdb = true,
                CoverImageUrl = BuildCoverUrl(game, options.Quality),
                ArtworkImageUrl = BuildArtworkUrl(game, options.Quality),
                ResolvedAtUtc = DateTime.UtcNow
            };

            if (options.SmallImageEnabled)
            {
                resolved.PlatformLogoUrl = await ResolvePlatformLogoAsync(context.IgdbPlatformName, cancellationToken)
                    .ConfigureAwait(false);
            }

            Logger.Info($"PlayPresence: applied manual IGDB override -> '{resolved.Title}' (id {igdbId}).");
            return resolved;
        }

        private async Task ApplySteamArtAsync(
            ResolvedMetadata resolved, GameContext context, MetadataResolverOptions options, CancellationToken cancellationToken)
        {
            var title = !string.IsNullOrWhiteSpace(resolved.Title) ? resolved.Title : context.GameName;
            // SteamGridDB now works automatically in the background whenever a key is present
            // (no separate on/off option), mirroring how IGDB credentials behave.
            var steamReady = steamGridDb != null && steamGridDb.IsConfigured;

            try
            {
                // ---- Icon style: priority SteamGridDB -> IGDB cover -> Google (if enabled) ----
                if (options.LargeImageSource == LargeImageSource.Icon)
                {
                    if (steamReady)
                    {
                        var iconUrl = await steamGridDb.FindIconUrlAsync(title, cancellationToken).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(iconUrl))
                        {
                            resolved.IconImageUrl = iconUrl;
                            Logger.Debug($"PlayPresence: applied SteamGridDB icon for '{title}'.");
                        }
                    }

                    // Google is the very last resort: only when SteamGridDB found nothing AND IGDB
                    // has no cover either, and only if the user explicitly enabled it.
                    if (string.IsNullOrWhiteSpace(resolved.IconImageUrl) &&
                        string.IsNullOrWhiteSpace(resolved.CoverImageUrl) &&
                        options.GoogleFallbackEnabled && googleImages != null && googleImages.IsConfigured)
                    {
                        var googleUrl = await googleImages
                            .FindIconUrlAsync(title, context.PlatformName, cancellationToken).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(googleUrl))
                        {
                            resolved.IconImageUrl = googleUrl;
                            Logger.Debug($"PlayPresence: applied Google image fallback for '{title}'.");
                        }
                    }
                    return;
                }

                // ---- Cover style: optionally upgrade/repair the cover with square grid art ----
                if (steamReady)
                {
                    var coverMissing = string.IsNullOrWhiteSpace(resolved.CoverImageUrl);
                    if (!options.PreferSteamGridDb && !coverMissing)
                    {
                        return;
                    }

                    var url = await steamGridDb.FindCoverUrlAsync(title, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        if (!coverMissing && string.IsNullOrWhiteSpace(resolved.ArtworkImageUrl))
                        {
                            resolved.ArtworkImageUrl = resolved.CoverImageUrl;
                        }
                        resolved.CoverImageUrl = url;
                        Logger.Debug($"PlayPresence: applied SteamGridDB cover for '{title}'.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "PlayPresence: extra art application failed (non-fatal).");
            }
        }

        private async Task<ResolvedMetadata> ResolveFromIgdbAsync(
            GameContext context,
            MetadataResolverOptions options,
            int? hintedYear,
            CancellationToken cancellationToken)
        {
            // Try each candidate name in priority order until we get a confident match.
            ScoredMatch match = null;
            IgdbGame matchedGame = null;

            foreach (var candidate in EnumerateSearchTitles(context))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var games = await igdb.SearchGamesAsync(candidate, 10, cancellationToken).ConfigureAwait(false);
                if (games.Count == 0)
                {
                    continue;
                }

                var scoreCandidates = games.Select(ToMatchCandidate).ToList();
                var best = MatchScorer.SelectBest(candidate, hintedYear ?? context.ReleaseYear, scoreCandidates, options.MatchThreshold);
                if (best != null)
                {
                    match = best;
                    matchedGame = games.First(g => g.Id == best.Candidate.Id);
                    break;
                }
            }

            if (match == null || matchedGame == null)
            {
                Logger.Info($"PlayPresence: no confident IGDB match for '{context.GameName}'.");
                return BuildFallback(context);
            }

            var resolved = new ResolvedMetadata
            {
                IgdbId = matchedGame.Id,
                Title = string.IsNullOrWhiteSpace(matchedGame.Name) ? context.GameName : matchedGame.Name,
                IgdbUrl = matchedGame.Url,
                Developer = ExtractDeveloper(matchedGame),
                Genre = ExtractGenre(matchedGame),
                ReleaseYear = ExtractYear(matchedGame) ?? context.ReleaseYear,
                MatchConfidence = match.Score,
                FromIgdb = true
            };

            resolved.CoverImageUrl = BuildCoverUrl(matchedGame, options.Quality);
            resolved.ArtworkImageUrl = BuildArtworkUrl(matchedGame, options.Quality);

            if (options.SmallImageEnabled)
            {
                resolved.PlatformLogoUrl = await ResolvePlatformLogoAsync(context.IgdbPlatformName, cancellationToken)
                    .ConfigureAwait(false);
            }

            Logger.Info($"PlayPresence: matched '{context.GameName}' -> '{resolved.Title}' (confidence {resolved.MatchConfidence:0.00}).");
            return resolved;
        }

        private async Task<string> ResolvePlatformLogoAsync(string igdbPlatformName, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(igdbPlatformName))
            {
                return null;
            }

            if (platformLogoCache.TryGetValue(igdbPlatformName, out var cachedUrl))
            {
                return cachedUrl;
            }

            try
            {
                var platform = await igdb.FindPlatformAsync(igdbPlatformName, cancellationToken).ConfigureAwait(false);
                var url = platform?.PlatformLogo != null
                    ? IgdbMetadataClient.BuildImageUrl(platform.PlatformLogo.ImageId, "logo_med")
                    : null;
                platformLogoCache[igdbPlatformName] = url; // cache even null to prevent repeat lookups
                return url;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "PlayPresence: platform logo lookup failed.");
                return null;
            }
        }

        private static MatchCandidate ToMatchCandidate(IgdbGame game)
        {
            return new MatchCandidate
            {
                Id = game.Id,
                Name = game.Name,
                AlternativeNames = game.AlternativeNames?.Select(a => a.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList()
                                   ?? new List<string>(),
                ReleaseYear = ExtractYear(game),
                IgdbPopularity = game.TotalRatingCount ?? 0
            };
        }

        private static string ChooseCleanTitle(GameContext context, out int? hintedYear)
        {
            hintedYear = null;
            foreach (var title in EnumerateSearchTitles(context))
            {
                return title; // first non-empty clean title
            }
            return context.GameName ?? string.Empty;
        }

        private static IEnumerable<string> EnumerateSearchTitles(GameContext context)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in context.CandidateNames)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }
                var clean = RomNameNormalizer.Normalize(raw).CleanTitle;
                if (!string.IsNullOrWhiteSpace(clean) && seen.Add(clean))
                {
                    yield return clean;
                }
            }
        }

        private static string BuildCoverUrl(IgdbGame game, ImageQuality quality)
        {
            if (game.Cover == null || string.IsNullOrWhiteSpace(game.Cover.ImageId))
            {
                return null;
            }
            var size = quality == ImageQuality.High ? "cover_big_2x"
                     : quality == ImageQuality.Medium ? "cover_big"
                     : "cover_small";
            return IgdbMetadataClient.BuildImageUrl(game.Cover.ImageId, size);
        }

        private static string BuildArtworkUrl(IgdbGame game, ImageQuality quality)
        {
            if (game.Artworks == null || game.Artworks.Count == 0 || string.IsNullOrWhiteSpace(game.Artworks[0].ImageId))
            {
                return null;
            }
            var size = quality == ImageQuality.Low ? "720p" : "1080p";
            return IgdbMetadataClient.BuildImageUrl(game.Artworks[0].ImageId, size);
        }

        private static string ExtractDeveloper(IgdbGame game)
        {
            if (game.InvolvedCompanies == null)
            {
                return null;
            }
            var dev = game.InvolvedCompanies.FirstOrDefault(c => c.Developer && c.Company != null);
            if (dev != null)
            {
                return dev.Company.Name;
            }
            var any = game.InvolvedCompanies.FirstOrDefault(c => c.Company != null);
            return any?.Company?.Name;
        }

        private static string ExtractGenre(IgdbGame game)
        {
            if (game.Genres == null || game.Genres.Count == 0)
            {
                return null;
            }
            return game.Genres[0].Name;
        }

        private static int? ExtractYear(IgdbGame game)
        {
            if (!game.FirstReleaseDate.HasValue)
            {
                return null;
            }
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return epoch.AddSeconds(game.FirstReleaseDate.Value).Year;
        }

        private static ResolvedMetadata BuildFallback(GameContext context)
        {
            return new ResolvedMetadata
            {
                Title = context.GameName,
                ReleaseYear = context.ReleaseYear,
                FromIgdb = false,
                MatchConfidence = 0,
                ResolvedAtUtc = DateTime.UtcNow
            };
        }

        private static string BuildCacheKey(string cleanTitle, string platformSpec)
        {
            return (cleanTitle ?? string.Empty).ToLowerInvariant() + "|" + (platformSpec ?? string.Empty).ToLowerInvariant();
        }
    }
}

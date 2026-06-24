using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using PlayPresence.Models;

namespace PlayPresence.Services.Presence
{
    /// <summary>
    /// Field/feature toggles and text templates that shape the Rich Presence. Sourced from the
    /// plugin settings and mapped into this POCO so the builder stays a pure, unit-testable unit
    /// with no dependency on Playnite's settings types.
    /// </summary>
    public sealed class PresenceOptions
    {
        // Templates. Supported tokens: {game} {platform} {developer} {publisher-less} {year}
        // {genre} {source} {emulator} {disc}. Empty/disabled tokens collapse away cleanly.
        public string DetailsTemplate { get; set; } = "{game}";
        public string StateTemplate { get; set; } = "{platform}";
        public string LargeImageTextTemplate { get; set; } = "{game}";

        public bool ShowElapsedTimer { get; set; } = true;

        // When true, the state line shows the launcher/emulator context (e.g. "on Steam",
        // "PlayStation 2 · PCSX2") instead of the plain platform. Off by default: the default
        // state line is the platform.
        public bool ShowSourceContext { get; set; } = false;
        public LargeImageSource LargeImageSource { get; set; } = LargeImageSource.Cover;
        public bool SmallImageEnabled { get; set; } = true;

        // Per-field visibility. When false, the matching token resolves to empty.
        public bool ShowPlatform { get; set; } = true;
        public bool ShowDeveloper { get; set; } = true;
        public bool ShowGenre { get; set; } = true;
        public bool ShowYear { get; set; } = true;
        public bool ShowSource { get; set; } = false;
        public bool ShowEmulator { get; set; } = true;
        public bool ShowDisc { get; set; } = true;
        public bool ShowFavorite { get; set; } = false;
        public bool ShowCompletionStatus { get; set; } = false;

        // Optional first button (custom). Label+Url must both be present to render.
        public bool EnableCustomButton { get; set; } = false;
        public string CustomButtonLabel { get; set; }
        public string CustomButtonUrl { get; set; }

        // Optional "View on IGDB" button using the resolved game's IGDB page.
        public bool EnableIgdbButton { get; set; } = true;
    }

    /// <summary>
    /// Composes a Discord-agnostic <see cref="PresenceModel"/> from settings, the launched game
    /// context and resolved metadata. Pure and deterministic: identical inputs always yield an
    /// identical model, which makes the presence layout fully unit-testable.
    /// </summary>
    public static class PresenceBuilder
    {
        private const string FallbackLargeImageKey = "playpresence_logo"; // optional uploaded Discord asset

        // Collapses leftover separator debris (e.g. " •  • ") after empty tokens are removed.
        private static readonly Regex SeparatorRuns =
            new Regex(@"\s*([•\-\|])\s*(?:[•\-\|]\s*)+", RegexOptions.Compiled);
        private static readonly Regex EdgeSeparators =
            new Regex(@"^[\s•\-\|]+|[\s•\-\|]+$", RegexOptions.Compiled);
        private static readonly Regex WhitespaceRuns =
            new Regex(@"\s{2,}", RegexOptions.Compiled);

        /// <summary>
        /// Builds the presence model for an active game session.
        /// </summary>
        /// <param name="options">Resolved presentation options/toggles.</param>
        /// <param name="context">The launched game context.</param>
        /// <param name="metadata">Resolved IGDB (or fallback) metadata.</param>
        /// <param name="sessionStartUtc">When the session began (drives the elapsed timer).</param>
        public static PresenceModel Build(
            PresenceOptions options,
            GameContext context,
            ResolvedMetadata metadata,
            DateTime sessionStartUtc)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (context == null) throw new ArgumentNullException(nameof(context));
            metadata = metadata ?? new ResolvedMetadata();

            var tokens = BuildTokens(options, context, metadata);

            var details = Clamp(Render(options.DetailsTemplate, tokens), PresenceModel.MaxFieldLength);
            var state = Clamp(Render(options.StateTemplate, tokens), PresenceModel.MaxFieldLength);
            var largeText = Clamp(Render(options.LargeImageTextTemplate, tokens), PresenceModel.MaxFieldLength);

            // Details must never be empty (Discord requires meaningful content); fall back to title.
            if (string.IsNullOrWhiteSpace(details))
            {
                details = Clamp(FirstNonEmpty(metadata.Title, context.GameName, "Playing"), PresenceModel.MaxFieldLength);
            }
            if (string.IsNullOrWhiteSpace(largeText))
            {
                largeText = details;
            }

            // Optional: replace the platform line with launcher/emulator context (off by default).
            if (options.ShowSourceContext)
            {
                var sourceLine = BuildSourceContext(context);
                if (!string.IsNullOrWhiteSpace(sourceLine))
                {
                    state = Clamp(sourceLine, PresenceModel.MaxFieldLength);
                }
            }

            var model = new PresenceModel
            {
                Details = details,
                State = string.IsNullOrWhiteSpace(state) ? null : state,
                LargeImageKey = ChooseLargeImage(options, metadata),
                LargeImageText = largeText,
                StartTimestampUtc = options.ShowElapsedTimer ? sessionStartUtc : (DateTime?)null
            };

            // No small side badge. There is no API source that yields a clean per-platform icon for
            // every system: SteamGridDB has no console logos at all, and IGDB platform logos are wide
            // wordmarks that Discord crops into an empty-looking circle in the small slot. The platform
            // is shown as text (in State) instead, which is reliable for every platform.

            AddButtons(options, metadata, model);
            return model;
        }

        /// <summary>
        /// Builds the lightweight "idle / browsing library" presence shown when no game is running.
        /// </summary>
        public static PresenceModel BuildIdle(string idleText, DateTime? sinceUtc)
        {
            var details = Clamp(string.IsNullOrWhiteSpace(idleText) ? "Browsing the library" : idleText,
                PresenceModel.MaxFieldLength);
            return new PresenceModel
            {
                Details = details,
                State = "In Playnite",
                LargeImageKey = FallbackLargeImageKey,
                LargeImageText = "Playnite",
                StartTimestampUtc = sinceUtc
            };
        }

        private static Dictionary<string, string> BuildTokens(
            PresenceOptions options, GameContext context, ResolvedMetadata metadata)
        {
            var year = metadata.ReleaseYear ?? context.ReleaseYear;

            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["game"] = FirstNonEmpty(metadata.Title, context.GameName),
                ["platform"] = options.ShowPlatform ? context.PlatformName : null,
                ["developer"] = options.ShowDeveloper ? metadata.Developer : null,
                ["genre"] = options.ShowGenre ? metadata.Genre : null,
                ["year"] = options.ShowYear && year.HasValue ? year.Value.ToString() : null,
                ["source"] = options.ShowSource ? context.Source : null,
                ["emulator"] = options.ShowEmulator ? context.EmulatorName : null,
                ["disc"] = options.ShowDisc ? context.DiscLabel : null,
                ["favorite"] = options.ShowFavorite && context.IsFavorite ? "★ Favorite" : null,
                ["status"] = options.ShowCompletionStatus ? context.CompletionStatus : null
            };
        }

        /// <summary>
        /// Builds the optional "where it's running" line: emulator context for emulated games
        /// (e.g. "PlayStation 2 · PCSX2"), otherwise the storefront/library (e.g. "on Steam"),
        /// otherwise the platform name.
        /// </summary>
        private static string BuildSourceContext(GameContext context)
        {
            if (!string.IsNullOrWhiteSpace(context.EmulatorName))
            {
                return string.IsNullOrWhiteSpace(context.PlatformName)
                    ? "via " + context.EmulatorName.Trim()
                    : context.PlatformName.Trim() + " · " + context.EmulatorName.Trim();
            }
            if (!string.IsNullOrWhiteSpace(context.Source))
            {
                return "on " + context.Source.Trim();
            }
            return context.PlatformName;
        }

        private static string ChooseLargeImage(PresenceOptions options, ResolvedMetadata metadata)
        {
            string url;
            if (options.LargeImageSource == LargeImageSource.Icon)
            {
                // Large image priority: the game's square icon from SteamGridDB, then the IGDB cover
                // as a fallback, then (below) the bundled square logo. The platform is shown as text,
                // never as the large image.
                url = FirstNonEmpty(metadata.IconImageUrl, metadata.CoverImageUrl);
            }
            else if (options.LargeImageSource == LargeImageSource.Artwork)
            {
                url = FirstNonEmpty(metadata.ArtworkImageUrl, metadata.CoverImageUrl,
                                    metadata.IconImageUrl, metadata.PlatformLogoUrl);
            }
            else
            {
                url = FirstNonEmpty(metadata.CoverImageUrl, metadata.ArtworkImageUrl,
                                    metadata.IconImageUrl, metadata.PlatformLogoUrl);
            }

            // Discord accepts a full external https URL as an image key; otherwise use the asset key.
            return string.IsNullOrWhiteSpace(url) ? FallbackLargeImageKey : url;
        }

        private static void AddButtons(PresenceOptions options, ResolvedMetadata metadata, PresenceModel model)
        {
            if (options.EnableCustomButton &&
                !string.IsNullOrWhiteSpace(options.CustomButtonLabel) &&
                IsValidHttpUrl(options.CustomButtonUrl))
            {
                model.Buttons.Add(new PresenceButton
                {
                    Label = Clamp(options.CustomButtonLabel, PresenceModel.MaxButtonLabelLength),
                    Url = options.CustomButtonUrl.Trim()
                });
            }

            if (options.EnableIgdbButton &&
                metadata.FromIgdb &&
                IsValidHttpUrl(metadata.IgdbUrl) &&
                model.Buttons.Count < PresenceModel.MaxButtons)
            {
                model.Buttons.Add(new PresenceButton
                {
                    Label = "View on IGDB",
                    Url = metadata.IgdbUrl.Trim()
                });
            }

            // Enforce Discord's hard cap defensively.
            while (model.Buttons.Count > PresenceModel.MaxButtons)
            {
                model.Buttons.RemoveAt(model.Buttons.Count - 1);
            }
        }

        /// <summary>Replaces {tokens}, drops empties, then tidies separators and whitespace.</summary>
        private static string Render(string template, Dictionary<string, string> tokens)
        {
            if (string.IsNullOrWhiteSpace(template))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(template.Length + 16);
            var i = 0;
            while (i < template.Length)
            {
                var c = template[i];
                if (c == '{')
                {
                    var end = template.IndexOf('}', i + 1);
                    if (end > i)
                    {
                        var key = template.Substring(i + 1, end - i - 1);
                        string value;
                        if (tokens.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value))
                        {
                            sb.Append(value.Trim());
                        }
                        i = end + 1;
                        continue;
                    }
                }

                sb.Append(c);
                i++;
            }

            return Tidy(sb.ToString());
        }

        private static string Tidy(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            value = SeparatorRuns.Replace(value, " $1 ");
            value = WhitespaceRuns.Replace(value, " ");
            value = EdgeSeparators.Replace(value, string.Empty);
            return value.Trim();
        }

        private static string Clamp(string value, int max)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }
            if (value.Length <= max)
            {
                return value;
            }
            // Reserve room for an ellipsis so truncation never looks accidental.
            return value.Substring(0, Math.Max(0, max - 1)).TrimEnd() + "\u2026";
        }

        private static bool IsValidHttpUrl(string url)
        {
            return !string.IsNullOrWhiteSpace(url)
                   && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
                   && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null)
            {
                return null;
            }
            foreach (var v in values)
            {
                if (!string.IsNullOrWhiteSpace(v))
                {
                    return v.Trim();
                }
            }
            return null;
        }
    }
}

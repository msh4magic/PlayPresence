using System;

namespace PlayPresence.Models
{
    /// <summary>
    /// The outcome of resolving a game against IGDB (or fallbacks). Cached on disk so repeated
    /// launches of the same game never hit the network.
    /// </summary>
    public sealed class ResolvedMetadata
    {
        public long IgdbId { get; set; }

        public string Title { get; set; }

        /// <summary>Direct, crisp cover art URL (used as Discord large image).</summary>
        public string CoverImageUrl { get; set; }

        /// <summary>Square game icon URL (preferred large image for the icon look; SteamGridDB).</summary>
        public string IconImageUrl { get; set; }

        /// <summary>Optional wide artwork URL (alternative large image source).</summary>
        public string ArtworkImageUrl { get; set; }

        /// <summary>Platform badge logo URL (used as Discord small image).</summary>
        public string PlatformLogoUrl { get; set; }

        public string Developer { get; set; }

        public string Genre { get; set; }

        public int? ReleaseYear { get; set; }

        /// <summary>Canonical IGDB web page for the game (used for the optional presence button).</summary>
        public string IgdbUrl { get; set; }

        /// <summary>Confidence (0..1) of the match that produced this metadata.</summary>
        public double MatchConfidence { get; set; }

        /// <summary>True when this entry came from a successful IGDB match rather than a fallback.</summary>
        public bool FromIgdb { get; set; }

        /// <summary>UTC timestamp when this entry was cached.</summary>
        public DateTime ResolvedAtUtc { get; set; }
    }
}

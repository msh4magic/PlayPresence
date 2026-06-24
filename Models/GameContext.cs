using System.Collections.Generic;

namespace PlayPresence.Models
{
    /// <summary>
    /// A library/runtime-agnostic snapshot of the game that was launched, assembled from
    /// Playnite's <c>Game</c> object. Keeping this decoupled from the SDK keeps the metadata
    /// and presence layers easy to reason about and to test.
    /// </summary>
    public sealed class GameContext
    {
        /// <summary>Playnite's display name for the game.</summary>
        public string GameName { get; set; }

        /// <summary>Ordered list of raw candidate names to try matching (game name first, then ROM file names).</summary>
        public List<string> CandidateNames { get; set; } = new List<string>();

        /// <summary>When set by a manual user override, the resolver fetches this exact IGDB game id.</summary>
        public long? IgdbIdOverride { get; set; }

        /// <summary>Friendly platform label, e.g. "PlayStation 2".</summary>
        public string PlatformName { get; set; }

        /// <summary>Playnite platform specification id, e.g. "sony_playstation2".</summary>
        public string PlatformSpecificationId { get; set; }

        /// <summary>Platform name used when querying IGDB for the platform logo, e.g. "PlayStation 2".</summary>
        public string IgdbPlatformName { get; set; }

        /// <summary>Library source name, e.g. "Steam", "GOG", "Emulation".</summary>
        public string Source { get; set; }

        /// <summary>Emulator display name when the game is emulated; otherwise null.</summary>
        public string EmulatorName { get; set; }

        /// <summary>Release year from Playnite metadata when available.</summary>
        public int? ReleaseYear { get; set; }

        public bool IsFavorite { get; set; }

        public string CompletionStatus { get; set; }

        /// <summary>Optional disc label parsed from the ROM filename, e.g. "Disc 1"; otherwise null.</summary>
        public string DiscLabel { get; set; }

        /// <summary>Local cover image path from Playnite (used as a last-resort fallback only).</summary>
        public string LocalCoverPath { get; set; }
    }
}

using System;
using System.Collections.Generic;

namespace PlayPresence.Services.Platforms
{
    /// <summary>The resolved, presentation-ready platform identity for a launched game.</summary>
    public sealed class PlatformIdentity
    {
        /// <summary>Human friendly label shown in Rich Presence, e.g. "PlayStation 2".</summary>
        public string FriendlyName { get; set; }

        /// <summary>Name used to query IGDB for the platform logo, e.g. "PlayStation 2".</summary>
        public string IgdbPlatformName { get; set; }
    }

    /// <summary>
    /// Maps Playnite platform <c>SpecificationId</c> values (stable, emulator-independent identifiers
    /// such as <c>sony_playstation2</c>) to a friendly display label and the matching IGDB platform
    /// name. This is pure BCL so it is trivially unit-testable and carries no Playnite/Discord deps.
    /// </summary>
    /// <remarks>
    /// We deliberately key off the specification id rather than folder names, exactly as the project
    /// requires. When a specification id is unknown we gracefully fall back to the Playnite platform
    /// name so brand-new or custom platforms still display correctly.
    /// </remarks>
    public static class PlatformResolver
    {
        // specificationId -> (friendly label, IGDB platform name). Curated from Playnite's
        // Platforms.yaml and IGDB's platforms endpoint naming.
        private static readonly Dictionary<string, PlatformIdentity> Map =
            new Dictionary<string, PlatformIdentity>(StringComparer.OrdinalIgnoreCase)
            {
                // --- PC ---
                ["pc_windows"] = Make("PC", "PC (Microsoft Windows)"),
                ["pc_linux"] = Make("PC", "Linux"),
                ["macintosh"] = Make("macOS", "Mac"),
                ["pc_dos"] = Make("DOS", "DOS"),

                // --- Sony ---
                ["sony_playstation"] = Make("PlayStation", "PlayStation"),
                ["sony_playstation2"] = Make("PlayStation 2", "PlayStation 2"),
                ["sony_playstation3"] = Make("PlayStation 3", "PlayStation 3"),
                ["sony_playstation4"] = Make("PlayStation 4", "PlayStation 4"),
                ["sony_playstation5"] = Make("PlayStation 5", "PlayStation 5"),
                ["sony_psp"] = Make("PSP", "PlayStation Portable"),
                ["sony_vita"] = Make("PS Vita", "PlayStation Vita"),

                // --- Microsoft ---
                ["xbox"] = Make("Xbox", "Xbox"),
                ["xbox360"] = Make("Xbox 360", "Xbox 360"),
                ["xbox_one"] = Make("Xbox One", "Xbox One"),
                ["xbox_series"] = Make("Xbox Series X|S", "Xbox Series X|S"),

                // --- Nintendo home consoles ---
                ["nintendo_nes"] = Make("NES", "Nintendo Entertainment System"),
                ["nintendo_famicom"] = Make("Famicom", "Family Computer"),
                ["nintendo_super_nes"] = Make("Super Nintendo", "Super Nintendo Entertainment System"),
                ["nintendo_superfamicom"] = Make("Super Famicom", "Super Famicom"),
                ["nintendo_64"] = Make("Nintendo 64", "Nintendo 64"),
                ["nintendo_gamecube"] = Make("GameCube", "Nintendo GameCube"),
                ["nintendo_wii"] = Make("Wii", "Wii"),
                ["nintendo_wiiu"] = Make("Wii U", "Wii U"),
                ["nintendo_switch"] = Make("Nintendo Switch", "Nintendo Switch"),

                // --- Nintendo handhelds ---
                ["nintendo_gameboy"] = Make("Game Boy", "Game Boy"),
                ["nintendo_gameboycolor"] = Make("Game Boy Color", "Game Boy Color"),
                ["nintendo_gameboyadvance"] = Make("Game Boy Advance", "Game Boy Advance"),
                ["nintendo_ds"] = Make("Nintendo DS", "Nintendo DS"),
                ["nintendo_3ds"] = Make("Nintendo 3DS", "Nintendo 3DS"),
                ["nintendo_virtualboy"] = Make("Virtual Boy", "Virtual Boy"),

                // --- Sega ---
                ["sega_mastersystem"] = Make("Master System", "Sega Master System/Mark III"),
                ["sega_genesis"] = Make("Genesis / Mega Drive", "Sega Mega Drive/Genesis"),
                ["sega_megadrive"] = Make("Mega Drive", "Sega Mega Drive/Genesis"),
                ["sega_32x"] = Make("Sega 32X", "Sega 32X"),
                ["sega_cd"] = Make("Sega CD", "Sega CD"),
                ["sega_saturn"] = Make("Saturn", "Sega Saturn"),
                ["sega_dreamcast"] = Make("Dreamcast", "Dreamcast"),
                ["sega_gamegear"] = Make("Game Gear", "Sega Game Gear"),
                ["sega_game_gear"] = Make("Game Gear", "Sega Game Gear"),

                // --- NEC ---
                ["nec_pcengine"] = Make("PC Engine", "TurboGrafx-16/PC Engine"),
                ["nec_turbografx_16"] = Make("TurboGrafx-16", "TurboGrafx-16/PC Engine"),
                ["nec_supergrafx"] = Make("SuperGrafx", "PC Engine SuperGrafx"),
                ["nec_pcfx"] = Make("PC-FX", "PC-FX"),

                // --- Atari ---
                ["atari_2600"] = Make("Atari 2600", "Atari 2600"),
                ["atari_5200"] = Make("Atari 5200", "Atari 5200"),
                ["atari_7800"] = Make("Atari 7800", "Atari 7800"),
                ["atari_jaguar"] = Make("Atari Jaguar", "Atari Jaguar"),
                ["atari_lynx"] = Make("Atari Lynx", "Atari Lynx"),

                // --- SNK / Bandai / misc ---
                ["snk_neogeo_aes"] = Make("Neo Geo", "Neo Geo AES"),
                ["snk_neogeopocket"] = Make("Neo Geo Pocket", "Neo Geo Pocket"),
                ["snk_neogeopocket_color"] = Make("Neo Geo Pocket Color", "Neo Geo Pocket Color"),
                ["bandai_wonderswan"] = Make("WonderSwan", "WonderSwan"),
                ["bandai_wonderswan_color"] = Make("WonderSwan Color", "WonderSwan Color"),
                ["3do"] = Make("3DO", "3DO Interactive Multiplayer"),
                ["commodore_amiga"] = Make("Amiga", "Amiga"),
                ["commodore_64"] = Make("Commodore 64", "Commodore C64/128/MAX"),
                ["sinclair_zxspectrum"] = Make("ZX Spectrum", "Sinclair ZX Spectrum"),

                // --- Arcade ---
                ["arcade"] = Make("Arcade", "Arcade"),
                ["mame_arcade"] = Make("Arcade", "Arcade"),
            };

        /// <summary>
        /// Resolves a platform identity. Prefers the curated specification-id mapping; when that is
        /// unavailable it falls back to the raw Playnite platform name so nothing is ever blank.
        /// </summary>
        /// <param name="specificationId">Playnite platform specification id (may be null/empty).</param>
        /// <param name="playniteName">Playnite platform display name used as a graceful fallback.</param>
        public static PlatformIdentity Resolve(string specificationId, string playniteName)
        {
            if (!string.IsNullOrWhiteSpace(specificationId) &&
                Map.TryGetValue(specificationId.Trim(), out var known))
            {
                // Clone so callers can never mutate the shared table.
                return new PlatformIdentity
                {
                    FriendlyName = known.FriendlyName,
                    IgdbPlatformName = known.IgdbPlatformName
                };
            }

            var fallback = string.IsNullOrWhiteSpace(playniteName) ? "PC" : playniteName.Trim();
            return new PlatformIdentity
            {
                FriendlyName = fallback,
                // The Playnite name is usually close enough for an IGDB platform search.
                IgdbPlatformName = fallback
            };
        }

        private static PlatformIdentity Make(string friendly, string igdb)
        {
            return new PlatformIdentity { FriendlyName = friendly, IgdbPlatformName = igdb };
        }
    }
}

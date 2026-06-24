using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PlayPresence.Services.Matching
{
    /// <summary>
    /// Canonical region values resolved from ROM naming conventions
    /// (No-Intro, Redump, GoodTools, TOSEC and common scene styles).
    /// </summary>
    public enum GameRegion
    {
        Unknown = 0,
        World,
        Usa,
        Europe,
        Japan,
        Asia,
        Korea,
        China,
        Brazil,
        Australia,
        Germany,
        France,
        Spain,
        Italy,
        Netherlands,
        Sweden,
        Russia
    }

    /// <summary>
    /// Structured result produced from a raw game title or ROM file name.
    /// </summary>
    public sealed class NormalizedName
    {
        /// <summary>The cleaned, human-readable title suitable for an IGDB search.</summary>
        public string CleanTitle { get; internal set; }

        /// <summary>The original input, untouched.</summary>
        public string Original { get; internal set; }

        public GameRegion Region { get; internal set; }

        /// <summary>Disc number when the title represents a multi-disc release (1-based), otherwise null.</summary>
        public int? DiscNumber { get; internal set; }

        /// <summary>Revision token, e.g. "Rev A" or "Rev 1", when present.</summary>
        public string Revision { get; internal set; }

        public bool IsBeta { get; internal set; }
        public bool IsPrototype { get; internal set; }
        public bool IsDemo { get; internal set; }
        public bool IsHack { get; internal set; }
        public bool IsTranslation { get; internal set; }
        public bool IsUnlicensed { get; internal set; }

        /// <summary>ISO-ish language tokens detected in the name (e.g. "En", "Fr").</summary>
        public IReadOnlyList<string> Languages { get; internal set; }

        /// <summary>All tag groups that were stripped from the title (for diagnostics).</summary>
        public IReadOnlyList<string> RawTags { get; internal set; }

        public override string ToString()
        {
            return $"{CleanTitle} [region={Region}, disc={DiscNumber?.ToString() ?? "-"}, rev={Revision ?? "-"}]";
        }
    }

    /// <summary>
    /// Converts arbitrary game titles and ROM file names into a clean, search-friendly
    /// title plus structured metadata. The normalizer is deliberately dependency-free so it
    /// can be unit-tested in isolation and reused anywhere in the plugin.
    /// </summary>
    public static class RomNameNormalizer
    {
        // Common ROM / disc image / archive extensions that must be removed before parsing.
        private static readonly HashSet<string> KnownExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cue","bin","iso","chd","img","mdf","mds","ccd","sub","nrg","gdi","cdi",
            "zip","7z","rar","gz","tar",
            "nes","fds","unf","sfc","smc","fig","swc","gb","gbc","gba","nds","dsi","3ds","cia","cci",
            "n64","z64","v64","rom","ndd","gcm","gcz","rvz","wbfs","wad","wux","nsp","xci",
            "md","gen","smd","bin","sms","gg","sg","pce","sgx","ws","wsc","ngp","ngc",
            "a26","a52","a78","lnx","jag","j64","32x","cdi","col","int","vec","min","vb",
            "pbp","cso","ciso","cxi","app","xex","iso2",
            "d64","t64","tap","prg","crt","g64","nib","x64",
            "adf","ipf","dsk","st","msa","dim","fdi",
            "vpk","mra","zip64","dol","elf"
        };

        // Articles that No-Intro / Redump move to the end of the title ("Legend of Zelda, The").
        private static readonly string[] TrailingArticles =
        {
            "The", "A", "An", "Le", "La", "Les", "Les", "El", "Los", "Las", "Der", "Die", "Das", "Il", "Lo", "Un", "Une"
        };

        // A plain struct instead of a ValueTuple so this compiles on .NET Framework 4.6.2
        // without requiring the System.ValueTuple package.
        private struct RegionToken
        {
            public readonly string Token;
            public readonly GameRegion Region;
            public RegionToken(string token, GameRegion region) { Token = token; Region = region; }
        }

        // Region resolution table. Order matters: longer / more specific tokens first.
        private static readonly RegionToken[] RegionTable =
        {
            new RegionToken("world", GameRegion.World),
            new RegionToken("usa", GameRegion.Usa),
            new RegionToken("ntsc-u", GameRegion.Usa),
            new RegionToken("ntsc-us", GameRegion.Usa),
            new RegionToken("us", GameRegion.Usa),
            new RegionToken("u", GameRegion.Usa),
            new RegionToken("europe", GameRegion.Europe),
            new RegionToken("eur", GameRegion.Europe),
            new RegionToken("pal", GameRegion.Europe),
            new RegionToken("e", GameRegion.Europe),
            new RegionToken("japan", GameRegion.Japan),
            new RegionToken("jpn", GameRegion.Japan),
            new RegionToken("jap", GameRegion.Japan),
            new RegionToken("ntsc-j", GameRegion.Japan),
            new RegionToken("jp", GameRegion.Japan),
            new RegionToken("j", GameRegion.Japan),
            new RegionToken("asia", GameRegion.Asia),
            new RegionToken("korea", GameRegion.Korea),
            new RegionToken("kor", GameRegion.Korea),
            new RegionToken("k", GameRegion.Korea),
            new RegionToken("china", GameRegion.China),
            new RegionToken("chn", GameRegion.China),
            new RegionToken("c", GameRegion.China),
            new RegionToken("brazil", GameRegion.Brazil),
            new RegionToken("bra", GameRegion.Brazil),
            new RegionToken("b", GameRegion.Brazil),
            new RegionToken("australia", GameRegion.Australia),
            new RegionToken("aus", GameRegion.Australia),
            new RegionToken("a", GameRegion.Australia),
            new RegionToken("germany", GameRegion.Germany),
            new RegionToken("ger", GameRegion.Germany),
            new RegionToken("g", GameRegion.Germany),
            new RegionToken("france", GameRegion.France),
            new RegionToken("fra", GameRegion.France),
            new RegionToken("f", GameRegion.France),
            new RegionToken("spain", GameRegion.Spain),
            new RegionToken("spa", GameRegion.Spain),
            new RegionToken("s", GameRegion.Spain),
            new RegionToken("italy", GameRegion.Italy),
            new RegionToken("ita", GameRegion.Italy),
            new RegionToken("i", GameRegion.Italy),
            new RegionToken("netherlands", GameRegion.Netherlands),
            new RegionToken("ned", GameRegion.Netherlands),
            new RegionToken("nl", GameRegion.Netherlands),
            new RegionToken("sweden", GameRegion.Sweden),
            new RegionToken("swe", GameRegion.Sweden),
            new RegionToken("russia", GameRegion.Russia),
            new RegionToken("rus", GameRegion.Russia)
        };

        private static readonly HashSet<string> LanguageTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "en","fr","de","es","it","ja","jp","pt","nl","sv","no","da","fi","pl","ru","ko","zh","cs","hu","el","tr","ar","he"
        };

        // Matches any (...) or [...] tag group.
        private static readonly Regex TagGroup = new Regex(@"[\(\[]([^\(\)\[\]]*)[\)\]]", RegexOptions.Compiled);
        private static readonly Regex WhitespaceRun = new Regex(@"\s{2,}", RegexOptions.Compiled);
        private static readonly Regex DiscPattern = new Regex(@"^(?:disc|disk|cd|dvd|side)\s*([0-9a-d]+)(?:\s*of\s*[0-9]+)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RevPattern = new Regex(@"^(?:rev|revision)\s*\.?\s*([0-9]+|[a-z])$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex VersionPattern = new Regex(@"^(?:v|ver|version)\s*\.?\s*([0-9][0-9\.]*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex TrailingDisc = new Regex(@"(?:^|\s)(?:disc|disk|cd|dvd|side)\s*([0-9]+|[a-d])\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Normalize a raw game name or ROM file name into a clean title plus structured tags.
        /// </summary>
        public static NormalizedName Normalize(string input)
        {
            var result = new NormalizedName
            {
                Original = input ?? string.Empty,
                Region = GameRegion.Unknown,
                Languages = new List<string>(),
                RawTags = new List<string>()
            };

            if (string.IsNullOrWhiteSpace(input))
            {
                result.CleanTitle = string.Empty;
                return result;
            }

            var working = input.Trim();

            // 1) Drop any directory component (handle both separators).
            var lastSlash = working.LastIndexOfAny(new[] { '/', '\\' });
            if (lastSlash >= 0 && lastSlash < working.Length - 1)
            {
                working = working.Substring(lastSlash + 1);
            }

            // 2) Strip a known file extension (only if it is actually a known ROM/disc/archive type).
            var lastDot = working.LastIndexOf('.');
            if (lastDot > 0 && lastDot < working.Length - 1)
            {
                var ext = working.Substring(lastDot + 1);
                if (KnownExtensions.Contains(ext))
                {
                    working = working.Substring(0, lastDot);
                }
            }

            // 3) Scene-style names frequently use '.' or '_' as word separators. Only convert dots
            //    to spaces when the name has no real spaces (avoids breaking "Mr. Driller").
            if (!working.Contains(' '))
            {
                working = working.Replace('.', ' ');
            }
            working = working.Replace('_', ' ');

            // 4) Extract and classify every (...) / [...] tag group.
            var tags = new List<string>();
            var languages = new List<string>();
            working = TagGroup.Replace(working, m =>
            {
                var content = m.Groups[1].Value.Trim();
                if (content.Length > 0)
                {
                    tags.Add(content);
                    ClassifyTag(content, result, languages);
                }
                return " ";
            });

            // 5) Collapse separators and trim leftover punctuation produced by tag removal.
            working = working.Replace('–', '-');
            working = WhitespaceRun.Replace(working, " ").Trim();
            working = working.Trim(' ', '-', '_', '.', ',');
            working = WhitespaceRun.Replace(working, " ").Trim();

            // 6) Strip a trailing un-bracketed disc marker, e.g. "Resident Evil Disc 1".
            working = StripTrailingDiscMarker(working, result);

            // 7) Restore article order: "Legend of Zelda, The" -> "The Legend of Zelda".
            working = MoveTrailingArticleToFront(working);

            result.CleanTitle = working;
            result.Languages = languages;
            result.RawTags = tags;
            return result;
        }

        private static void ClassifyTag(string tag, NormalizedName result, List<string> languages)
        {
            var lower = tag.ToLowerInvariant();

            // GoodTools dump-quality / status codes -> strip silently, but flag the meaningful ones.
            switch (lower)
            {
                case "!":   // verified good dump
                    return;
                case "unl":
                case "unlicensed":
                    result.IsUnlicensed = true;
                    return;
                case "pd":
                case "public domain":
                    return;
            }

            if (lower == "hack" || Regex.IsMatch(lower, @"^h[0-9]*$"))
            {
                result.IsHack = true;
                return;
            }
            if (lower.StartsWith("t+") || lower.StartsWith("t-") || lower == "translation" || lower.StartsWith("tra "))
            {
                result.IsTranslation = true;
                return;
            }
            if (lower.Contains("beta")) { result.IsBeta = true; return; }
            if (lower.Contains("proto")) { result.IsPrototype = true; return; }
            if (lower.Contains("demo") || lower.Contains("sample") || lower.Contains("kiosk") || lower.Contains("trial"))
            {
                result.IsDemo = true;
                return;
            }

            // Disc / disk / CD numbering.
            var disc = DiscPattern.Match(lower);
            if (disc.Success)
            {
                var token = disc.Groups[1].Value;
                if (int.TryParse(token, out var discNo))
                {
                    result.DiscNumber = discNo;
                }
                else if (token.Length == 1 && token[0] >= 'a' && token[0] <= 'd')
                {
                    result.DiscNumber = token[0] - 'a' + 1;
                }
                return;
            }

            // Revision / version.
            var rev = RevPattern.Match(tag);
            if (rev.Success)
            {
                result.Revision = "Rev " + rev.Groups[1].Value.ToUpperInvariant();
                return;
            }
            var ver = VersionPattern.Match(tag);
            if (ver.Success)
            {
                result.Revision = "v" + ver.Groups[1].Value;
                return;
            }

            // Comma / plus separated lists: either multi-region ("USA, Europe") or
            // multi-language ("En,Fr,De"), occasionally mixed. Resolve the primary region
            // (first resolvable token) and collect any language tokens.
            if (lower.Contains(",") || lower.Contains("+"))
            {
                var parts = lower.Split(new[] { ',', '+' }, StringSplitOptions.RemoveEmptyEntries)
                                 .Select(p => p.Trim())
                                 .Where(p => p.Length > 0)
                                 .ToList();

                var handled = false;
                foreach (var part in parts)
                {
                    if (result.Region == GameRegion.Unknown)
                    {
                        var partRegion = ResolveRegion(part);
                        if (partRegion != GameRegion.Unknown)
                        {
                            result.Region = partRegion;
                            handled = true;
                            continue;
                        }
                    }
                    if (LanguageTokens.Contains(part))
                    {
                        languages.Add(Capitalize(part));
                        handled = true;
                    }
                }

                if (handled)
                {
                    return;
                }
            }

            // Region resolution (only set the first/strongest region found).
            if (result.Region == GameRegion.Unknown)
            {
                var region = ResolveRegion(lower);
                if (region != GameRegion.Unknown)
                {
                    result.Region = region;
                    return;
                }
            }

            // Standalone language token (e.g. "(En)") when not already a region.
            if (LanguageTokens.Contains(lower))
            {
                languages.Add(Capitalize(lower));
            }
        }

        private static GameRegion ResolveRegion(string token)
        {
            token = token.Trim();
            foreach (var entry in RegionTable)
            {
                if (string.Equals(token, entry.Token, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Region;
                }
            }
            return GameRegion.Unknown;
        }

        private static string StripTrailingDiscMarker(string title, NormalizedName result)
        {
            if (string.IsNullOrEmpty(title))
            {
                return title;
            }

            var match = TrailingDisc.Match(title);
            if (!match.Success)
            {
                return title;
            }

            if (!result.DiscNumber.HasValue)
            {
                var token = match.Groups[1].Value;
                if (int.TryParse(token, out var discNo))
                {
                    result.DiscNumber = discNo;
                }
                else if (token.Length == 1)
                {
                    var lower = char.ToLowerInvariant(token[0]);
                    if (lower >= 'a' && lower <= 'd')
                    {
                        result.DiscNumber = lower - 'a' + 1;
                    }
                }
            }

            return title.Substring(0, match.Index).Trim();
        }

        private static string MoveTrailingArticleToFront(string title)
        {
            if (string.IsNullOrEmpty(title))
            {
                return title;
            }

            var commaIndex = title.LastIndexOf(',');
            if (commaIndex <= 0 || commaIndex >= title.Length - 1)
            {
                return title;
            }

            var head = title.Substring(0, commaIndex).Trim();
            var tail = title.Substring(commaIndex + 1).Trim();

            foreach (var article in TrailingArticles)
            {
                if (string.Equals(tail, article, StringComparison.OrdinalIgnoreCase))
                {
                    return article + " " + head;
                }
            }
            return title;
        }

        private static string Capitalize(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }
            return char.ToUpperInvariant(value[0]) + value.Substring(1).ToLowerInvariant();
        }
    }
}

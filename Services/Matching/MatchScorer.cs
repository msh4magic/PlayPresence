using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PlayPresence.Services.Matching
{
    /// <summary>
    /// A lightweight candidate description used by <see cref="MatchScorer"/>.
    /// The IGDB layer maps its responses onto this type so the scorer stays dependency-free.
    /// </summary>
    public sealed class MatchCandidate
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public IReadOnlyList<string> AlternativeNames { get; set; } = new List<string>();
        public int? ReleaseYear { get; set; }
        public double IgdbPopularity { get; set; }
    }

    /// <summary>Result of scoring a single candidate against a query.</summary>
    public sealed class ScoredMatch
    {
        public MatchCandidate Candidate { get; set; }
        public double Score { get; set; }
        public string MatchedOn { get; set; }
    }

    /// <summary>
    /// Computes a 0..1 confidence score between a normalized query title and IGDB candidates,
    /// combining token-set similarity, edit-distance similarity, containment and light popularity
    /// and release-year signals. Pure and deterministic for straightforward unit testing.
    /// </summary>
    public static class MatchScorer
    {
        /// <summary>Default minimum score required to accept an automatic match.</summary>
        public const double DefaultAcceptThreshold = 0.55;

        public static ScoredMatch SelectBest(
            string queryTitle,
            int? queryYear,
            IEnumerable<MatchCandidate> candidates,
            double acceptThreshold = DefaultAcceptThreshold)
        {
            if (candidates == null)
            {
                return null;
            }

            ScoredMatch best = null;
            foreach (var candidate in candidates)
            {
                var scored = Score(queryTitle, queryYear, candidate);
                if (best == null || scored.Score > best.Score)
                {
                    best = scored;
                }
            }

            if (best != null && best.Score >= acceptThreshold)
            {
                return best;
            }
            return null;
        }

        public static ScoredMatch Score(string queryTitle, int? queryYear, MatchCandidate candidate)
        {
            var query = Canonicalize(queryTitle);

            var bestComponent = 0.0;
            var matchedName = candidate.Name;

            foreach (var name in EnumerateNames(candidate))
            {
                var component = StringSimilarity(query, Canonicalize(name));
                if (component > bestComponent)
                {
                    bestComponent = component;
                    matchedName = name;
                }
            }

            var score = bestComponent;

            // Release-year agreement is a strong corroborating signal when we have it.
            if (queryYear.HasValue && candidate.ReleaseYear.HasValue)
            {
                var delta = Math.Abs(queryYear.Value - candidate.ReleaseYear.Value);
                if (delta == 0) score += 0.06;
                else if (delta == 1) score += 0.02;
                else if (delta > 3) score -= 0.04;
            }

            // Very small tie-breaker so that, between near-identical titles, the more
            // popular/canonical IGDB entry wins. Never enough to override a better title match.
            score += Math.Min(0.03, candidate.IgdbPopularity * 0.0001);

            if (score > 1.0) score = 1.0;
            if (score < 0.0) score = 0.0;

            return new ScoredMatch { Candidate = candidate, Score = score, MatchedOn = matchedName };
        }

        private static IEnumerable<string> EnumerateNames(MatchCandidate candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate.Name))
            {
                yield return candidate.Name;
            }
            if (candidate.AlternativeNames != null)
            {
                foreach (var alt in candidate.AlternativeNames)
                {
                    if (!string.IsNullOrWhiteSpace(alt))
                    {
                        yield return alt;
                    }
                }
            }
        }

        /// <summary>Blended similarity: edit-distance ratio, token-set Jaccard and containment.</summary>
        public static double StringSimilarity(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            {
                return 0.0;
            }
            if (a == b)
            {
                return 1.0;
            }

            var editSim = 1.0 - ((double)Levenshtein(a, b) / Math.Max(a.Length, b.Length));
            var jaccard = TokenJaccard(a, b);

            var containment = 0.0;
            if (a.Length >= 3 && b.Length >= 3)
            {
                if (a.Contains(b) || b.Contains(a))
                {
                    containment = 0.15;
                }
            }

            var blended = (0.55 * editSim) + (0.45 * jaccard) + containment;
            return blended > 1.0 ? 1.0 : blended;
        }

        private static double TokenJaccard(string a, string b)
        {
            var setA = new HashSet<string>(a.Split(' ').Where(t => t.Length > 0));
            var setB = new HashSet<string>(b.Split(' ').Where(t => t.Length > 0));
            if (setA.Count == 0 || setB.Count == 0)
            {
                return 0.0;
            }

            var intersection = setA.Count(setB.Contains);
            var union = setA.Count + setB.Count - intersection;
            return union == 0 ? 0.0 : (double)intersection / union;
        }

        /// <summary>Iterative Levenshtein distance using two rolling rows (O(n) memory).</summary>
        public static int Levenshtein(string a, string b)
        {
            if (a == b) return 0;
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;

            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];

            for (var j = 0; j <= b.Length; j++)
            {
                previous[j] = j;
            }

            for (var i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(
                        Math.Min(current[j - 1] + 1, previous[j] + 1),
                        previous[j - 1] + cost);
                }

                var swap = previous;
                previous = current;
                current = swap;
            }

            return previous[b.Length];
        }

        /// <summary>
        /// Lowercases, removes punctuation, normalizes roman/arabic numeral spacing,
        /// strips a leading article and collapses whitespace for fair comparison.
        /// </summary>
        public static string Canonicalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(value.Length);
            foreach (var ch in value.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(ch);
                }
                else if (char.IsWhiteSpace(ch) || ch == '-' || ch == ':' || ch == '/')
                {
                    sb.Append(' ');
                }
                // all other punctuation dropped
            }

            var collapsed = sb.ToString();
            while (collapsed.Contains("  "))
            {
                collapsed = collapsed.Replace("  ", " ");
            }
            collapsed = collapsed.Trim();

            if (collapsed.StartsWith("the ", StringComparison.Ordinal))
            {
                collapsed = collapsed.Substring(4);
            }

            return collapsed;
        }
    }
}

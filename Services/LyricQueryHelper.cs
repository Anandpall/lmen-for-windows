using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LmenV2.Services
{
    /// <summary>
    /// Title and artist query normalization ported from BitChord (ref/BitChord/LyricsQuery.kt).
    /// Cleans credits, upload wrappers, and YouTube packaging while deliberately preserving
    /// recording versions (e.g. Remix, Acoustic, Live, Sped Up).
    /// </summary>
    public static class LyricQueryHelper
    {
        private static readonly Regex WhitespaceRegex = new Regex(@"\s+", RegexOptions.Compiled);

        private static readonly List<Regex> CreditRegexes = new List<Regex>
        {
            // Bracketed credits: (feat. X), [ft. X], (with X).
            new Regex(@"\s*[(\[]\s*(feat|ft|featuring|with)\b[^)\]]*[)\]]", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            // Unbracketed trailing credit running to end of title
            new Regex(@"\s+(feat|ft|featuring)\.?\s+.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            // Upload & packaging labels: Official Music Video, 4K, Lyrics Video, Visualizer, etc.
            new Regex(@"\s*[(\[]\s*(official\s*)?(music\s*)?(video|audio|visuali[sz]er|lyrics?\s*video|lyrics?|m/?v|hd|hq|4k|full\s*song)\s*[)\]]", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            // Bare [Official]
            new Regex(@"\s*[(\[]\s*official\s*[)\]]", RegexOptions.IgnoreCase | RegexOptions.Compiled)
        };

        public static string CleanTitleForSearch(string rawTitle)
        {
            if (string.IsNullOrWhiteSpace(rawTitle)) return string.Empty;

            string name = rawTitle;
            foreach (var pattern in CreditRegexes)
            {
                name = pattern.Replace(name, " ");
            }

            name = WhitespaceRegex.Replace(name, " ").Trim();
            name = name.TrimEnd(',', '-', '–', '—').Trim();

            return string.IsNullOrWhiteSpace(name) ? rawTitle.Trim() : name;
        }

        public static string CleanArtistForSearch(string rawArtist)
        {
            if (string.IsNullOrWhiteSpace(rawArtist)) return string.Empty;

            string name = rawArtist.Trim();
            if (name.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - " - Topic".Length).Trim();
            }

            return string.IsNullOrWhiteSpace(name) ? rawArtist.Trim() : name;
        }
    }
}

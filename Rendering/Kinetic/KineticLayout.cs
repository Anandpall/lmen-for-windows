using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using LmenV2.Models;

namespace LmenV2.Rendering.Kinetic
{
    public static class KineticLayout
    {
        private static readonly Regex EnergeticRegex = new Regex(
            @"rock|metal|punk|hip.?hop|rap|electronic|dance|edm|house|techno|drum|dubstep|k-pop|anime|pop",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CalmRegex = new Regex(
            @"ballad|acoustic|folk|classical|jazz|ambient|soul|r&b|singer|soundtrack|easy|lo-?fi|ghazal|devotional|bollywood",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TinyWords = new Regex(
            @"^(a|an|the|to|of|in|on|at|by|for|from|with|into|onto|and|or|but|so|as|if|is|am|are|was|be|been|i|i'm|it|it's|me|my|we|you're|that|this|just|all|up|out|oh|ooh|ah|yeah|ka|ki|ke|ko|se|hai|hain|ne|na|bhi|toh)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// FNV-1a hash for deterministic line seed.
        /// </summary>
        public static uint HashString(string s)
        {
            if (string.IsNullOrEmpty(s)) return 2166136261u;
            uint h = 2166136261u;
            foreach (char c in s)
            {
                h ^= (uint)c;
                h *= 16777619u;
            }
            return h;
        }

        /// <summary>
        /// Fast seeded deterministic PRNG returning uniform double [0, 1).
        /// </summary>
        public static Func<double> CreateRng(uint seed)
        {
            uint a = seed == 0 ? 1u : seed;
            return () =>
            {
                a += 0x6D2B79F5u;
                uint t = a;
                t = (t ^ (t >> 15)) * (t | 1u);
                t ^= t + (t ^ (t >> 7)) * (t | 61u);
                return ((t ^ (t >> 14)) & 0xFFFFFFFFu) / 4294967296.0;
            };
        }

        /// <summary>
        /// Approximate rendered width of a string in "em" for bold uppercase type.
        /// </summary>
        public static double VisualLength(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0.5;
            double n = 0;
            string upper = s.ToUpperInvariant();
            foreach (char ch in upper)
            {
                if (ch >= 0x3040 && ch <= 0x9FFF || ch >= 0xAC00 && ch <= 0xD7AF) n += 1.0; // CJK / Hangul
                else if (ch >= 0x0900 && ch <= 0x097F) n += 0.62; // Devanagari
                else if (ch == 'M' || ch == 'W' || ch == '@') n += 0.98;
                else if (ch == 'I' || ch == '.' || ch == ',' || ch == '\'' || ch == '!' || ch == '|' || ch == ':' || ch == ';') n += 0.30;
                else if (ch == 'J') n += 0.56;
                else if (ch == ' ') n += 0.26;
                else if (char.IsDigit(ch)) n += 0.66;
                else n += 0.72;
            }
            return Math.Max(n, 0.5);
        }

        /// <summary>
        /// Maximum words that share one row. Keeping this low keeps the type large and
        /// stops rows from being squeezed to fit several words at once.
        /// </summary>
        public const int MaxWordsPerRow = 2;

        /// <summary>
        /// Chance that a row is allowed to hold a third word, for occasional rhythm
        /// variation. Deliberately tiny so three-up rows stay rare.
        /// </summary>
        public const double TripleWordRowChance = 0.01;

        /// <summary>
        /// Group lyric words into short rows of at most two words (rarely three).
        /// Rows are split in reading order; width is no longer used to pack more words
        /// onto a line, so the layout never has to shrink or squish the type.
        /// </summary>
        public static List<List<LyricWord>> GroupRows(List<LyricWord> words, int maxRows)
        {
            var result = new List<List<LyricWord>>();
            if (words == null || words.Count == 0) return result;

            // Deterministic per-line seed so a line always keeps the same rhythm.
            uint seed = (uint)words.Count * 2654435761u;
            foreach (var w in words)
            {
                seed ^= HashString(w.Text);
                seed *= 16777619u;
            }
            var rng = CreateRng(seed ^ (uint)maxRows);

            int index = 0;
            while (index < words.Count)
            {
                int remaining = words.Count - index;
                int take = Math.Min(MaxWordsPerRow, remaining);

                if (remaining >= MaxWordsPerRow + 1 && rng() < TripleWordRowChance)
                {
                    take = MaxWordsPerRow + 1;
                }

                var row = new List<LyricWord>(take);
                for (int k = 0; k < take; k++)
                {
                    row.Add(words[index++]);
                }
                result.Add(row);
            }

            return result;
        }

        /// <summary>
        /// Determine which word indices in a line should receive vibrant album-accent highlights.
        /// Ignores tiny stop words; picks 1 to 3 words deterministically using seed.
        /// </summary>
        public static HashSet<int> GetHighlightIndices(LyricLine line, uint seed)
        {
            var highlights = new HashSet<int>();
            if (line == null || line.Words == null || line.Words.Count == 0) return highlights;

            var r = CreateRng(seed ^ 0x51ED27u);
            var candidates = new List<int>();

            for (int i = 0; i < line.Words.Count; i++)
            {
                string cleanWord = line.Words[i].Text.Trim();
                if (!TinyWords.IsMatch(cleanWord))
                {
                    candidates.Add(i);
                }
            }

            if (candidates.Count == 0)
            {
                for (int i = 0; i < line.Words.Count; i++) candidates.Add(i);
            }

            // Shuffle candidates
            for (int i = candidates.Count - 1; i > 0; i--)
            {
                int j = (int)Math.Floor(r() * (i + 1));
                int temp = candidates[i];
                candidates[i] = candidates[j];
                candidates[j] = temp;
            }

            int count = Math.Max(1, Math.Min(3, (int)Math.Round(line.Words.Count * (0.20 + r() * 0.15))));
            count = Math.Min(count, candidates.Count);

            for (int i = 0; i < count; i++)
            {
                highlights.Add(candidates[i]);
            }

            return highlights;
        }

        /// <summary>
        /// Selects the optimal kinetic template and camera tilt for a line.
        /// </summary>
        public static KineticScene ChooseScene(LyricLine line, uint songSeed, string? genre, KineticTemplate forced)
        {
            if (forced != KineticTemplate.Auto)
            {
                var forcedScene = new KineticScene
                {
                    Template = forced,
                    TiltDeg = 0.0
                };
                return forcedScene;
            }

            uint lineSeed = songSeed ^ HashString(line.Text);
            var r = CreateRng(lineSeed);

            int n = line.Words?.Count ?? 1;
            double duration = Math.Max(0.5, (line.EndTime - line.StartTime).TotalSeconds);
            double wps = n / duration;

            bool energetic = !string.IsNullOrEmpty(genre)
                ? EnergeticRegex.IsMatch(genre) && !CalmRegex.IsMatch(genre)
                : wps > 2.5;
            bool calm = !string.IsNullOrEmpty(genre) ? CalmRegex.IsMatch(genre) : wps < 1.4;

            KineticTemplate template;

            if (n <= 2)
            {
                template = PickWeighted(r, new[]
                {
                    (KineticTemplate.Zoom, 3.0),
                    (KineticTemplate.Letters, 2.0),
                    (KineticTemplate.Sticker, 1.6),
                    (KineticTemplate.Fisheye, 1.5),
                    (KineticTemplate.Stack, 1.0)
                });
            }
            else if (n > 11 || wps > 3.8)
            {
                template = PickWeighted(r, new[]
                {
                    (KineticTemplate.Flow, 3.0),
                    (KineticTemplate.Slide, 2.0),
                    (KineticTemplate.Fisheye, 1.8)
                });
            }
            else
            {
                template = PickWeighted(r, new[]
                {
                    (KineticTemplate.Stack, calm ? 3.0 : 2.0),
                    (KineticTemplate.Slide, energetic ? 3.0 : 2.0),
                    (KineticTemplate.Fisheye, calm ? 2.2 : 1.6),
                    (KineticTemplate.Graph, n <= 8 ? (calm ? 2.5 : 1.5) : 0.5),
                    (KineticTemplate.Scatter, energetic ? 2.0 : 1.0),
                    (KineticTemplate.Zoom, energetic ? 2.0 : 1.0),
                    (KineticTemplate.Sticker, energetic ? 1.7 : 0.9),
                    (KineticTemplate.Letters, 1.5),
                    (KineticTemplate.Flow, calm ? 2.0 : 0.8)
                });
            }

            // Cotodama subtle camera tilt (Fisheye & Graph stay level)
            double tilt = (template == KineticTemplate.Graph || template == KineticTemplate.Fisheye || r() > 0.32) ? 0.0 : (r() - 0.5) * 6.5;

            return new KineticScene
            {
                Template = template,
                TiltDeg = tilt
            };
        }

        private static T PickWeighted<T>(Func<double> r, (T Value, double Weight)[] items)
        {
            double total = 0;
            foreach (var item in items) total += item.Weight;
            double x = r() * total;

            foreach (var item in items)
            {
                x -= item.Weight;
                if (x <= 0) return item.Value;
            }
            return items[0].Value;
        }

        /// <summary>
        /// Splits a word into individual visible letters / graphemes.
        /// </summary>
        public static List<string> SplitLetters(string word)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(word)) return list;

            var si = new StringInfo(word);
            for (int i = 0; i < si.LengthInTextElements; i++)
            {
                list.Add(si.SubstringByTextElements(i, 1));
            }
            return list;
        }
    }
}

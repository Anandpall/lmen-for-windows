using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LmenV2.Models;

namespace LmenV2.Services
{
    public enum LyricsTransformStatus
    {
        Translated,
        SameLanguage,
        AlreadyRomanized,
        Unavailable
    }

    /// <summary>
    /// The outcome of one translate or romanize request, mirroring BitChord's
    /// LyricsTranslation.Result / RomanizationResult pair.
    /// </summary>
    public sealed class LyricsTransformResult
    {
        public static readonly LyricsTransformResult Unavailable = new LyricsTransformResult { Status = LyricsTransformStatus.Unavailable };
        public static readonly LyricsTransformResult AlreadyRomanized = new LyricsTransformResult { Status = LyricsTransformStatus.AlreadyRomanized };

        public LyricsTransformStatus Status { get; init; }
        public List<LyricLine>? Lines { get; init; }
        public string SourceLanguage { get; init; } = string.Empty;
        public bool FromCache { get; init; }
    }

    public sealed class TranslationLanguage
    {
        public string Code { get; }
        public string FallbackName { get; }

        public TranslationLanguage(string code, string fallbackName)
        {
            Code = code;
            FallbackName = fallbackName;
        }
    }

    /// <summary>
    /// Every language Google Translate offers, in its own picker order. Codes are
    /// kept exactly as Google spells them: zh-CN and zh-TW are different scripts
    /// of the same language, and mni-Mtei names the script it is written in.
    /// </summary>
    public static class LyricsTranslationLanguages
    {
        public static readonly IReadOnlyList<TranslationLanguage> All = new List<TranslationLanguage>
        {
            new TranslationLanguage("af", "Afrikaans"),
            new TranslationLanguage("sq", "Albanian"),
            new TranslationLanguage("am", "Amharic"),
            new TranslationLanguage("ar", "Arabic"),
            new TranslationLanguage("hy", "Armenian"),
            new TranslationLanguage("as", "Assamese"),
            new TranslationLanguage("ay", "Aymara"),
            new TranslationLanguage("az", "Azerbaijani"),
            new TranslationLanguage("bm", "Bambara"),
            new TranslationLanguage("eu", "Basque"),
            new TranslationLanguage("be", "Belarusian"),
            new TranslationLanguage("bn", "Bengali"),
            new TranslationLanguage("bho", "Bhojpuri"),
            new TranslationLanguage("bs", "Bosnian"),
            new TranslationLanguage("bg", "Bulgarian"),
            new TranslationLanguage("ca", "Catalan"),
            new TranslationLanguage("ceb", "Cebuano"),
            new TranslationLanguage("ny", "Chichewa"),
            new TranslationLanguage("zh-CN", "Chinese (Simplified)"),
            new TranslationLanguage("zh-TW", "Chinese (Traditional)"),
            new TranslationLanguage("co", "Corsican"),
            new TranslationLanguage("hr", "Croatian"),
            new TranslationLanguage("cs", "Czech"),
            new TranslationLanguage("da", "Danish"),
            new TranslationLanguage("dv", "Dhivehi"),
            new TranslationLanguage("doi", "Dogri"),
            new TranslationLanguage("nl", "Dutch"),
            new TranslationLanguage("en", "English"),
            new TranslationLanguage("eo", "Esperanto"),
            new TranslationLanguage("et", "Estonian"),
            new TranslationLanguage("ee", "Ewe"),
            new TranslationLanguage("tl", "Filipino"),
            new TranslationLanguage("fi", "Finnish"),
            new TranslationLanguage("fr", "French"),
            new TranslationLanguage("fy", "Frisian"),
            new TranslationLanguage("gl", "Galician"),
            new TranslationLanguage("ka", "Georgian"),
            new TranslationLanguage("de", "German"),
            new TranslationLanguage("el", "Greek"),
            new TranslationLanguage("gn", "Guarani"),
            new TranslationLanguage("gu", "Gujarati"),
            new TranslationLanguage("ht", "Haitian Creole"),
            new TranslationLanguage("ha", "Hausa"),
            new TranslationLanguage("haw", "Hawaiian"),
            new TranslationLanguage("iw", "Hebrew"),
            new TranslationLanguage("hi", "Hindi"),
            new TranslationLanguage("hmn", "Hmong"),
            new TranslationLanguage("hu", "Hungarian"),
            new TranslationLanguage("is", "Icelandic"),
            new TranslationLanguage("ig", "Igbo"),
            new TranslationLanguage("ilo", "Ilocano"),
            new TranslationLanguage("id", "Indonesian"),
            new TranslationLanguage("ga", "Irish"),
            new TranslationLanguage("it", "Italian"),
            new TranslationLanguage("ja", "Japanese"),
            new TranslationLanguage("jw", "Javanese"),
            new TranslationLanguage("kn", "Kannada"),
            new TranslationLanguage("kk", "Kazakh"),
            new TranslationLanguage("km", "Khmer"),
            new TranslationLanguage("rw", "Kinyarwanda"),
            new TranslationLanguage("gom", "Konkani"),
            new TranslationLanguage("ko", "Korean"),
            new TranslationLanguage("kri", "Krio"),
            new TranslationLanguage("ku", "Kurdish (Kurmanji)"),
            new TranslationLanguage("ckb", "Kurdish (Sorani)"),
            new TranslationLanguage("ky", "Kyrgyz"),
            new TranslationLanguage("lo", "Lao"),
            new TranslationLanguage("la", "Latin"),
            new TranslationLanguage("lv", "Latvian"),
            new TranslationLanguage("ln", "Lingala"),
            new TranslationLanguage("lt", "Lithuanian"),
            new TranslationLanguage("lg", "Luganda"),
            new TranslationLanguage("lb", "Luxembourgish"),
            new TranslationLanguage("mk", "Macedonian"),
            new TranslationLanguage("mai", "Maithili"),
            new TranslationLanguage("mg", "Malagasy"),
            new TranslationLanguage("ms", "Malay"),
            new TranslationLanguage("ml", "Malayalam"),
            new TranslationLanguage("mt", "Maltese"),
            new TranslationLanguage("mi", "Maori"),
            new TranslationLanguage("mr", "Marathi"),
            new TranslationLanguage("mni-Mtei", "Meiteilon (Manipuri)"),
            new TranslationLanguage("lus", "Mizo"),
            new TranslationLanguage("mn", "Mongolian"),
            new TranslationLanguage("my", "Myanmar (Burmese)"),
            new TranslationLanguage("ne", "Nepali"),
            new TranslationLanguage("no", "Norwegian"),
            new TranslationLanguage("or", "Odia (Oriya)"),
            new TranslationLanguage("om", "Oromo"),
            new TranslationLanguage("ps", "Pashto"),
            new TranslationLanguage("fa", "Persian"),
            new TranslationLanguage("pl", "Polish"),
            new TranslationLanguage("pt", "Portuguese"),
            new TranslationLanguage("pa", "Punjabi"),
            new TranslationLanguage("qu", "Quechua"),
            new TranslationLanguage("ro", "Romanian"),
            new TranslationLanguage("ru", "Russian"),
            new TranslationLanguage("sm", "Samoan"),
            new TranslationLanguage("sa", "Sanskrit"),
            new TranslationLanguage("gd", "Scots Gaelic"),
            new TranslationLanguage("nso", "Sepedi"),
            new TranslationLanguage("sr", "Serbian"),
            new TranslationLanguage("st", "Sesotho"),
            new TranslationLanguage("sn", "Shona"),
            new TranslationLanguage("sd", "Sindhi"),
            new TranslationLanguage("si", "Sinhala"),
            new TranslationLanguage("sk", "Slovak"),
            new TranslationLanguage("sl", "Slovenian"),
            new TranslationLanguage("so", "Somali"),
            new TranslationLanguage("es", "Spanish"),
            new TranslationLanguage("su", "Sundanese"),
            new TranslationLanguage("sw", "Swahili"),
            new TranslationLanguage("sv", "Swedish"),
            new TranslationLanguage("tg", "Tajik"),
            new TranslationLanguage("ta", "Tamil"),
            new TranslationLanguage("tt", "Tatar"),
            new TranslationLanguage("te", "Telugu"),
            new TranslationLanguage("th", "Thai"),
            new TranslationLanguage("ti", "Tigrinya"),
            new TranslationLanguage("ts", "Tsonga"),
            new TranslationLanguage("tr", "Turkish"),
            new TranslationLanguage("tk", "Turkmen"),
            new TranslationLanguage("ak", "Twi"),
            new TranslationLanguage("uk", "Ukrainian"),
            new TranslationLanguage("ur", "Urdu"),
            new TranslationLanguage("ug", "Uyghur"),
            new TranslationLanguage("uz", "Uzbek"),
            new TranslationLanguage("vi", "Vietnamese"),
            new TranslationLanguage("cy", "Welsh"),
            new TranslationLanguage("xh", "Xhosa"),
            new TranslationLanguage("yi", "Yiddish"),
            new TranslationLanguage("yo", "Yoruba"),
            new TranslationLanguage("zu", "Zulu"),
        };

        /// <summary>
        /// What to call a language on screen, in the reader's own language where
        /// the platform knows it, falling back to the curated English name, and
        /// to the code itself when even that is missing.
        /// </summary>
        public static string DisplayName(string code)
        {
            var entry = All.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
            if (code.IndexOf('-') < 0)
            {
                try
                {
                    var info = CultureInfo.GetCultureInfo(code);
                    string platform = info.DisplayName;
                    if (!string.IsNullOrWhiteSpace(platform) && !string.Equals(platform, code, StringComparison.OrdinalIgnoreCase))
                    {
                        return Titlecase(platform);
                    }
                }
                catch { }
            }
            return entry?.FallbackName ?? code;
        }

        private static string Titlecase(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return char.ToUpper(text[0], CultureInfo.CurrentCulture) + text.Substring(1);
        }
    }

    /// <summary>
    /// Lightweight, on-demand lyric translation and romanization, ported from
    /// BitChord's LyricsTranslation.kt. Requests are batched against Google
    /// Translate and the compact result is kept both in a small memory LRU and
    /// in a bounded gzip cache under %LOCALAPPDATA%\LmenV2\LyricsTranslation.
    /// </summary>
    public static class LyricsTranslationService
    {
        private const string Endpoint = "https://translate.googleapis.com/translate_a/single";
        private const string InputToolsEndpoint = "https://inputtools.google.com/request";
        // Version 3 invalidates answers cached before the Latin-script retry
        // existed; otherwise a song that once came back untranslated would never
        // take the repaired path after the app is updated.
        private const int CacheVersion = 3;
        private const string CacheDirectoryName = "lyrics_translation_v3";
        private const long MaxCacheBytes = 2L * 1024L * 1024L;
        private const int MaxBatchChars = 3500;
        private const int MaxParallelRequests = 2;

        // Input Tools changes ASCII digits to the destination script, so match
        // the private-use wrapper rather than assuming the counter stays ASCII.
        private static readonly Regex MarkerRegex = new Regex("\uE000[^\uE001]*\uE001", RegexOptions.Compiled);
        private static readonly Regex WhitespaceRegex = new Regex("\\s+", RegexOptions.Compiled);

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        private static readonly HttpClient Client = CreateClient();
        private static readonly SemaphoreSlim DiskMutex = new SemaphoreSlim(1, 1);
        private static readonly MemoryCache Memory = new MemoryCache(12);
        private static readonly string? CacheRoot = CreateCacheRoot();

        public static async Task<LyricsTransformResult> TranslateAsync(string trackKey, List<LyricLine> lines, string targetLanguageTag, CancellationToken ct = default)
        {
            // Sent as given rather than reduced to a base language: zh-CN and
            // zh-TW are the same language in two scripts, and canonicalising
            // either to "zh" hands back Simplified whichever one was asked for.
            // The narrowing is only done in SameLanguage, a different question.
            string target = (targetLanguageTag ?? string.Empty).Trim();
            if (target.Length == 0 || lines == null || lines.Count == 0) return LyricsTransformResult.Unavailable;

            var slots = Flatten(lines);
            if (slots.Count == 0) return LyricsTransformResult.Unavailable;

            string cacheKey = ComputeCacheKey(trackKey, target, slots);
            var cached = Memory.Get(cacheKey);
            if (cached == null)
            {
                cached = await ReadCacheAsync(cacheKey, ct).ConfigureAwait(false);
                if (cached != null) Memory.Put(cacheKey, cached);
            }

            if (cached != null && cached.Version == CacheVersion && cached.Texts.Count == slots.Count)
            {
                return SameLanguage(cached.SourceLanguage, target)
                    ? new LyricsTransformResult { Status = LyricsTransformStatus.SameLanguage, SourceLanguage = cached.SourceLanguage }
                    : new LyricsTransformResult
                    {
                        Status = LyricsTransformStatus.Translated,
                        Lines = Rebuild(lines, slots, cached.Texts),
                        SourceLanguage = cached.SourceLanguage,
                        FromCache = true
                    };
            }

            var batches = SplitBatches(slots);
            var answers = new List<BatchAnswer?>();
            foreach (var group in Chunk(batches, MaxParallelRequests))
            {
                var groupTasks = group.Select(batch => RequestBatchAsync(batch, target, "auto", ct)).ToArray();
                answers.AddRange(await Task.WhenAll(groupTasks).ConfigureAwait(false));
            }
            if (answers.Any(a => a == null)) return LyricsTransformResult.Unavailable;
            var complete = answers.Where(a => a != null).Select(a => a!).ToList();

            string source = complete
                .GroupBy(a => CanonicalLanguage(a.SourceLanguage))
                .OrderByDescending(g => g.Sum(a => a.SourceWeight))
                .Select(g => g.Key)
                .FirstOrDefault() ?? string.Empty;
            if (source.Length == 0) return LyricsTransformResult.Unavailable;

            var translated = complete.SelectMany(a => a.Translations).ToList();
            if (translated.Count != slots.Count) return LyricsTransformResult.Unavailable;

            // Google's ordinary auto-detection understands many Latin-script
            // Hindi/Urdu/Punjabi lyrics, but its NMT occasionally returns whole
            // phrases unchanged. If a sizeable part of a Latin-script source
            // survived untouched, use Google's Input Tools to restore the
            // detected language's native script and translate that. Keep the
            // first answer unless the retry actually transforms more of the
            // song, so names and genuinely bilingual lyrics do not get worse
            // merely because they contain Latin text.
            if (!SameLanguage(source, target) &&
                PredominantlyLatin(slots) &&
                UnchangedWeight(slots, translated) * 3 >= slots.Sum(s => s.Text.Length))
            {
                var retried = await RetryRomanizedTranslationAsync(batches, source, target, ct).ConfigureAwait(false);
                if (retried != null &&
                    retried.Count == slots.Count &&
                    UnchangedWeight(slots, retried) < UnchangedWeight(slots, translated))
                {
                    translated = retried;
                }
            }

            var entry = new CachedTranslation
            {
                Version = CacheVersion,
                SourceLanguage = source,
                TargetLanguage = target,
                Texts = translated
            };
            Memory.Put(cacheKey, entry);
            await WriteCacheAsync(cacheKey, entry, ct).ConfigureAwait(false);

            return SameLanguage(source, target)
                ? new LyricsTransformResult { Status = LyricsTransformStatus.SameLanguage, SourceLanguage = source }
                : new LyricsTransformResult
                {
                    Status = LyricsTransformStatus.Translated,
                    Lines = Rebuild(lines, slots, translated),
                    SourceLanguage = source,
                    FromCache = false
                };
        }

        public static async Task<LyricsTransformResult> RomanizeAsync(string trackKey, List<LyricLine> lines, string targetLanguageTag, CancellationToken ct = default)
        {
            if (lines == null || lines.Count == 0) return LyricsTransformResult.Unavailable;
            var slots = Flatten(lines);
            if (slots.Count == 0) return LyricsTransformResult.Unavailable;
            if (!HasNonLatinLetters(slots)) return LyricsTransformResult.AlreadyRomanized;

            // Romanization always ends in Latin script. The translation
            // destination is still sent as `tl` because the web endpoint
            // requires it, but it is deliberately excluded from this cache key:
            // changing "translate to" cannot change how a language is said.
            string cacheKey = ComputeCacheKey(trackKey, "romanize", slots);
            var cached = Memory.Get(cacheKey);
            if (cached == null)
            {
                cached = await ReadCacheAsync(cacheKey, ct).ConfigureAwait(false);
                if (cached != null) Memory.Put(cacheKey, cached);
            }

            if (cached != null && cached.Version == CacheVersion && cached.Texts.Count == slots.Count)
            {
                return new LyricsTransformResult
                {
                    Status = LyricsTransformStatus.Translated,
                    Lines = Rebuild(lines, slots, cached.Texts),
                    SourceLanguage = cached.SourceLanguage,
                    FromCache = true
                };
            }

            string target = (targetLanguageTag ?? string.Empty).Trim();
            if (target.Length == 0) target = "en";

            var batches = SplitBatches(slots);
            var answers = new List<BatchAnswer?>();
            foreach (var group in Chunk(batches, MaxParallelRequests))
            {
                var groupTasks = group.Select(batch => RequestRomanizationBatchAsync(batch, target, ct)).ToArray();
                answers.AddRange(await Task.WhenAll(groupTasks).ConfigureAwait(false));
            }
            if (answers.Any(a => a == null)) return LyricsTransformResult.Unavailable;
            var complete = answers.Where(a => a != null).Select(a => a!).ToList();

            string source = complete
                .GroupBy(a => CanonicalLanguage(a.SourceLanguage))
                .OrderByDescending(g => g.Sum(a => a.SourceWeight))
                .Select(g => g.Key)
                .FirstOrDefault() ?? string.Empty;
            var romanized = complete.SelectMany(a => a.Translations).ToList();
            if (source.Length == 0 || romanized.Count != slots.Count) return LyricsTransformResult.Unavailable;
            if (UnchangedWeight(slots, romanized) == slots.Sum(s => s.Text.Length))
            {
                // Non-Latin input reached this point, so an unchanged response
                // is an unsupported/failed romanization rather than an
                // already-Latin lyric; the fast check above handles that case.
                return LyricsTransformResult.Unavailable;
            }

            var entry = new CachedTranslation
            {
                Version = CacheVersion,
                SourceLanguage = source,
                TargetLanguage = "Latn",
                Texts = romanized
            };
            Memory.Put(cacheKey, entry);
            await WriteCacheAsync(cacheKey, entry, ct).ConfigureAwait(false);

            return new LyricsTransformResult
            {
                Status = LyricsTransformStatus.Translated,
                Lines = Rebuild(lines, slots, romanized),
                SourceLanguage = source,
                FromCache = false
            };
        }

        // -------------------------------------------------------------
        // Line flattening and rebuilding
        // -------------------------------------------------------------

        private sealed class TextSlot
        {
            public int LineIndex { get; init; }
            public bool Background { get; init; }
            public string Text { get; init; } = string.Empty;
            public bool SectionHeader { get; init; }
        }

        private sealed class Batch
        {
            public List<TextSlot> Slots { get; init; } = new List<TextSlot>();
            public string Payload { get; init; } = string.Empty;

            public Batch WithPayload(string payload) => new Batch { Slots = Slots, Payload = payload };
        }

        private sealed class BatchAnswer
        {
            public List<string> Translations { get; init; } = new List<string>();
            public string SourceLanguage { get; init; } = string.Empty;
            public int SourceWeight { get; init; }
        }

        private static List<TextSlot> Flatten(List<LyricLine> lines)
        {
            var slots = new List<TextSlot>();
            for (int index = 0; index < lines.Count; index++)
            {
                var line = lines[index];
                if (!string.IsNullOrWhiteSpace(line.Text))
                {
                    bool header = LyricsProviders.Genius.IsSectionHeader(line.Text);
                    slots.Add(new TextSlot
                    {
                        LineIndex = index,
                        Background = false,
                        Text = header ? line.Text.TrimStart('[').TrimEnd(']').Trim() : line.Text,
                        SectionHeader = header
                    });
                }

                var background = line.BackgroundLine;
                string? backgroundText = background != null && !string.IsNullOrWhiteSpace(background.Text)
                    ? background.Text
                    : (!string.IsNullOrWhiteSpace(line.BackgroundText) ? line.BackgroundText : null);
                if (!string.IsNullOrWhiteSpace(backgroundText))
                {
                    slots.Add(new TextSlot { LineIndex = index, Background = true, Text = backgroundText!, SectionHeader = false });
                }
            }
            return slots;
        }

        private static List<Batch> SplitBatches(List<TextSlot> slots)
        {
            var result = new List<Batch>();
            var current = new List<TextSlot>();
            int length = 0;

            void Flush()
            {
                if (current.Count == 0) return;
                result.Add(new Batch { Slots = new List<TextSlot>(current), Payload = BuildPayload(current) });
                current.Clear();
                length = 0;
            }

            foreach (var slot in slots)
            {
                int added = slot.Text.Length + (current.Count == 0 ? 0 : 8);
                if (current.Count > 0 && length + added > MaxBatchChars) Flush();
                current.Add(slot);
                length += added;
            }
            Flush();
            return result;
        }

        private static string BuildPayload(List<TextSlot> slots)
        {
            var builder = new StringBuilder();
            for (int index = 0; index < slots.Count; index++)
            {
                if (index > 0) builder.Append('\n').Append(Marker(index)).Append('\n');
                builder.Append(slots[index].Text);
            }
            return builder.ToString();
        }

        private static string Marker(int index) => "\uE000" + index.ToString("D4", CultureInfo.InvariantCulture) + "\uE001";

        /// <summary>
        /// Puts the transformed text back where the original sat. Timed lines
        /// keep the source vocal bounds as a single word whose syllable sweep
        /// projects the original non-uniform character timing onto the new text,
        /// preserving holds, pauses and the glow envelope.
        /// </summary>
        private static List<LyricLine> Rebuild(List<LyricLine> original, List<TextSlot> slots, List<string> transformed)
        {
            var byLine = new Dictionary<int, List<(TextSlot Slot, string Text)>>();
            for (int i = 0; i < slots.Count; i++)
            {
                if (!byLine.TryGetValue(slots[i].LineIndex, out var list))
                {
                    list = new List<(TextSlot, string)>();
                    byLine[slots[i].LineIndex] = list;
                }
                list.Add((slots[i], transformed[i]));
            }

            var result = new List<LyricLine>(original.Count);
            for (int index = 0; index < original.Count; index++)
            {
                var line = original[index];
                if (!byLine.TryGetValue(index, out var entries) || entries.Count == 0)
                {
                    result.Add(line);
                    continue;
                }

                var lead = entries.FirstOrDefault(e => !e.Slot.Background);
                var backing = entries.FirstOrDefault(e => e.Slot.Background);
                var leadSlot = lead.Slot;
                bool hasLead = leadSlot != null;
                string leadText = hasLead
                    ? (leadSlot!.SectionHeader ? $"[{lead.Text}]" : lead.Text)
                    : line.Text;

                LyricLine? newBackground = null;
                if (line.BackgroundLine != null)
                {
                    string backingText = backing.Slot != null ? backing.Text : line.BackgroundLine.Text;
                    newBackground = new LyricLine
                    {
                        Index = line.BackgroundLine.Index,
                        StartTime = line.BackgroundLine.StartTime,
                        EndTime = line.BackgroundLine.EndTime,
                        Text = backingText,
                        Alignment = line.BackgroundLine.Alignment,
                        SungUntil = line.BackgroundLine.SungUntil,
                        IsGap = line.BackgroundLine.IsGap,
                        Words = RetimeWords(line.BackgroundLine, backingText)
                    };
                }

                result.Add(new LyricLine
                {
                    Index = line.Index,
                    StartTime = line.StartTime,
                    EndTime = line.EndTime,
                    Text = leadText,
                    BackgroundText = newBackground?.Text ?? line.BackgroundText,
                    BackgroundLine = newBackground,
                    Alignment = line.Alignment,
                    SungUntil = line.SungUntil,
                    IsGap = line.IsGap,
                    Words = RetimeWords(line, leadText)
                });
            }
            return result;
        }

        private static List<LyricWord> RetimeWords(LyricLine source, string transformed)
        {
            if (source.Words == null || source.Words.Count == 0) return new List<LyricWord>();
            if (string.IsNullOrEmpty(transformed)) return new List<LyricWord>();

            long startMs = (long)source.Words[0].StartTime.TotalMilliseconds;
            long endMs = (long)source.Words[^1].EndTime.TotalMilliseconds;

            return new List<LyricWord>
            {
                new LyricWord
                {
                    Index = 0,
                    Text = transformed,
                    StartTime = TimeSpan.FromMilliseconds(startMs),
                    EndTime = TimeSpan.FromMilliseconds(Math.Max(startMs, endMs)),
                    Syllables = BuildProjectionSyllables(source, transformed.Length)
                }
            };
        }

        /// <summary>
        /// Reproduces the source line's non-uniform reveal as syllable break-
        /// points over the transformed text: each source word (or its own
        /// syllables) maps to its character share, and each inter-word pause
        /// fills the whitespace between them.
        /// </summary>
        private static List<LyricSyllable> BuildProjectionSyllables(LyricLine source, int length)
        {
            string sourceText = source.Text ?? string.Empty;
            int sourceLength = sourceText.Length;
            if (sourceLength <= 0 || length <= 0) return new List<LyricSyllable>();

            var parts = new List<LyricSyllable>();
            int offset = 0;
            var words = source.Words ?? new List<LyricWord>();

            for (int i = 0; i < words.Count; i++)
            {
                var word = words[i];
                if (string.IsNullOrEmpty(word.Text)) continue;

                int start = sourceText.IndexOf(word.Text, Math.Min(offset, sourceLength), StringComparison.Ordinal);
                if (start < 0) start = offset;
                int end = Math.Min(sourceLength, start + word.Text.Length);

                long wordStartMs = (long)word.StartTime.TotalMilliseconds;
                long wordEndMs = (long)Math.Max(word.StartTime.TotalMilliseconds, word.EndTime.TotalMilliseconds);

                if (word.Syllables != null && word.Syllables.Count >= 2)
                {
                    foreach (var syllable in word.Syllables.Where(s => s.EndChar > s.StartChar).OrderBy(s => s.StartChar))
                    {
                        parts.Add(new LyricSyllable(
                            syllable.StartMs,
                            Math.Max(syllable.StartMs, syllable.EndMs),
                            ProjectChar(start + syllable.StartChar, sourceLength, length),
                            ProjectChar(start + syllable.EndChar, sourceLength, length)));
                    }
                }
                else
                {
                    parts.Add(new LyricSyllable(
                        wordStartMs,
                        Math.Max(wordStartMs, wordEndMs),
                        ProjectChar(start, sourceLength, length),
                        ProjectChar(end, sourceLength, length)));
                }

                if (i + 1 < words.Count)
                {
                    var next = words[i + 1];
                    long nextStartMs = (long)next.StartTime.TotalMilliseconds;
                    if (nextStartMs > wordEndMs && !string.IsNullOrEmpty(next.Text))
                    {
                        int nextStart = sourceText.IndexOf(next.Text, Math.Min(end, sourceLength), StringComparison.Ordinal);
                        if (nextStart < 0) nextStart = end;
                        parts.Add(new LyricSyllable(
                            wordEndMs,
                            nextStartMs,
                            ProjectChar(end, sourceLength, length),
                            ProjectChar(nextStart, sourceLength, length)));
                    }
                }

                offset = Math.Max(offset, end);
            }

            return LyricSyllable.Normalized(parts, length);
        }

        private static int ProjectChar(int charIndex, int sourceLength, int targetLength)
        {
            if (sourceLength <= 0) return 0;
            double fraction = (double)Math.Clamp(charIndex, 0, sourceLength) / sourceLength;
            return (int)Math.Round(fraction * targetLength);
        }

        // -------------------------------------------------------------
        // HTTP
        // -------------------------------------------------------------

        private static HttpClient CreateClient()
        {
            var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "BitChord/1.7.1");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            return client;
        }

        private static async Task<string?> PostFormAsync(string url, Dictionary<string, string> fields, CancellationToken ct)
        {
            try
            {
                using var content = new FormUrlEncodedContent(fields);
                using var response = await Client.PostAsync(url, content, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;
                return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        private static async Task<BatchAnswer?> RequestBatchAsync(Batch batch, string target, string sourceLanguage, CancellationToken ct)
        {
            string? response = await PostFormAsync(Endpoint, new Dictionary<string, string>
            {
                ["client"] = "dict-chrome-ex",
                ["sl"] = sourceLanguage,
                ["tl"] = target,
                ["dt"] = "t",
                ["q"] = batch.Payload
            }, ct).ConfigureAwait(false);
            if (response == null) return null;

            try
            {
                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 3) return null;

                var builder = new StringBuilder();
                foreach (var segment in root[0].EnumerateArray())
                {
                    if (segment.ValueKind == JsonValueKind.Array &&
                        segment.GetArrayLength() > 0 &&
                        segment[0].ValueKind == JsonValueKind.String)
                    {
                        builder.Append(segment[0].GetString());
                    }
                }

                string source = root[2].ValueKind == JsonValueKind.String ? root[2].GetString() ?? string.Empty : string.Empty;
                var parts = MarkerRegex.Split(builder.ToString()).Select(p => p.Trim()).ToList();
                if (parts.Count != batch.Slots.Count || parts.Any(string.IsNullOrWhiteSpace)) return null;
                return new BatchAnswer { Translations = parts, SourceLanguage = source, SourceWeight = batch.Payload.Length };
            }
            catch
            {
                return null;
            }
        }

        private static async Task<BatchAnswer?> RequestRomanizationBatchAsync(Batch batch, string target, CancellationToken ct)
        {
            string? response = await PostFormAsync(Endpoint, new Dictionary<string, string>
            {
                ["client"] = "dict-chrome-ex",
                ["sl"] = "auto",
                ["tl"] = target,
                ["dt"] = "rm",
                ["q"] = batch.Payload
            }, ct).ConfigureAwait(false);
            if (response == null) return null;

            try
            {
                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 3) return null;

                var builder = new StringBuilder();
                foreach (var segment in root[0].EnumerateArray())
                {
                    if (segment.ValueKind == JsonValueKind.Array &&
                        segment.GetArrayLength() > 3 &&
                        segment[3].ValueKind == JsonValueKind.String)
                    {
                        builder.Append(segment[3].GetString());
                    }
                }

                string source = root[2].ValueKind == JsonValueKind.String ? root[2].GetString() ?? string.Empty : string.Empty;
                var parts = MarkerRegex.Split(builder.ToString()).Select(p => p.Trim()).ToList();
                if (source.Length == 0 || parts.Count != batch.Slots.Count || parts.Any(string.IsNullOrWhiteSpace)) return null;
                return new BatchAnswer { Translations = parts, SourceLanguage = source, SourceWeight = batch.Payload.Length };
            }
            catch
            {
                return null;
            }
        }

        private static async Task<List<string>?> RetryRomanizedTranslationAsync(List<Batch> batches, string sourceLanguage, string target, CancellationToken ct)
        {
            string source = CanonicalLanguage(sourceLanguage);
            if (source.Length == 0 || source == "en") return null;

            var translated = new List<string>();
            foreach (var group in Chunk(batches, MaxParallelRequests))
            {
                var groupTasks = group.Select(async batch =>
                {
                    string? nativePayload = await RequestNativeScriptAsync(batch.Payload, source, ct).ConfigureAwait(false);
                    if (nativePayload == null) return (List<string>?)null;
                    var answer = await RequestBatchAsync(batch.WithPayload(nativePayload), target, source, ct).ConfigureAwait(false);
                    return answer?.Translations;
                }).ToArray();

                var results = await Task.WhenAll(groupTasks).ConfigureAwait(false);
                if (results.Any(r => r == null)) return null;
                foreach (var r in results) translated.AddRange(r!);
            }
            return translated;
        }

        private static async Task<string?> RequestNativeScriptAsync(string text, string sourceLanguage, CancellationToken ct)
        {
            string? response = await PostFormAsync(InputToolsEndpoint, new Dictionary<string, string>
            {
                ["text"] = text,
                ["itc"] = sourceLanguage + "-t-i0-und",
                ["num"] = "1",
                ["cp"] = "0",
                ["cs"] = "1",
                ["ie"] = "utf-8",
                ["oe"] = "utf-8"
            }, ct).ConfigureAwait(false);
            if (response == null) return null;

            try
            {
                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2) return null;
                if (root[0].ValueKind != JsonValueKind.String || root[0].GetString() != "SUCCESS") return null;
                var result = root[1];
                if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0) return null;
                var first = result[0];
                if (first.ValueKind != JsonValueKind.Array || first.GetArrayLength() < 2) return null;
                var candidates = first[1];
                if (candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0) return null;
                var candidate = candidates[0];
                if (candidate.ValueKind != JsonValueKind.String) return null;
                string? value = candidate.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            catch
            {
                return null;
            }
        }

        // -------------------------------------------------------------
        // Language helpers
        // -------------------------------------------------------------

        private static string CanonicalLanguage(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return string.Empty;
            string normalized = tag.Replace('_', '-');
            int separator = normalized.IndexOf('-');
            string language = (separator >= 0 ? normalized.Substring(0, separator) : normalized).ToLowerInvariant();
            return language switch
            {
                "iw" => "he",
                "in" => "id",
                "ji" => "yi",
                _ => language
            };
        }

        private static bool SameLanguage(string first, string second) =>
            CanonicalLanguage(first) == CanonicalLanguage(second);

        private static bool PredominantlyLatin(List<TextSlot> slots)
        {
            int latin = 0;
            int other = 0;
            foreach (var slot in slots)
            {
                foreach (char c in slot.Text)
                {
                    if (!char.IsLetter(c)) continue;
                    if (IsLatinLetter(c)) latin++;
                    else other++;
                }
            }
            return latin > 0 && latin >= other * 4;
        }

        private static bool HasNonLatinLetters(List<TextSlot> slots) =>
            slots.Any(slot => slot.Text.Any(c => char.IsLetter(c) && !IsLatinLetter(c)));

        /// <summary>
        /// Character-range approximation of the Unicode LATIN script, covering
        /// the ranges lyrics realistically use.
        /// </summary>
        private static bool IsLatinLetter(char c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                   (c >= '\u00C0' && c <= '\u00D6') || (c >= '\u00D8' && c <= '\u00F6') ||
                   (c >= '\u00F8' && c <= '\u02FF') ||
                   (c >= '\u1D00' && c <= '\u1DBF') || (c >= '\u1E00' && c <= '\u1EFF') ||
                   (c >= '\u2C60' && c <= '\u2C7F') || (c >= '\uA720' && c <= '\uA7FF') ||
                   (c >= '\uAB30' && c <= '\uAB6F') || (c >= '\uFB00' && c <= '\uFB06') ||
                   (c >= '\uFF21' && c <= '\uFF3A') || (c >= '\uFF41' && c <= '\uFF5A');
        }

        private static int UnchangedWeight(List<TextSlot> slots, List<string> transformed) =>
            slots.Zip(transformed, (slot, text) => Comparable(slot.Text) == Comparable(text) ? slot.Text.Length : 0).Sum();

        private static string Comparable(string text) => WhitespaceRegex.Replace(text.Trim().ToLowerInvariant(), " ");

        private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
        {
            for (int i = 0; i < source.Count; i += size)
            {
                yield return source.GetRange(i, Math.Min(size, source.Count - i));
            }
        }

        // -------------------------------------------------------------
        // Caching
        // -------------------------------------------------------------

        private sealed class CachedTranslation
        {
            public int Version { get; set; } = CacheVersion;
            public string SourceLanguage { get; set; } = string.Empty;
            public string TargetLanguage { get; set; } = string.Empty;
            public List<string> Texts { get; set; } = new List<string>();
        }

        private sealed class MemoryCache
        {
            private readonly int _capacity;
            private readonly Dictionary<string, LinkedListNode<(string Key, CachedTranslation Value)>> _map = new Dictionary<string, LinkedListNode<(string, CachedTranslation)>>();
            private readonly LinkedList<(string Key, CachedTranslation Value)> _order = new LinkedList<(string, CachedTranslation)>();
            private readonly object _gate = new object();

            public MemoryCache(int capacity) => _capacity = capacity;

            public CachedTranslation? Get(string key)
            {
                lock (_gate)
                {
                    if (!_map.TryGetValue(key, out var node)) return null;
                    _order.Remove(node);
                    _order.AddFirst(node);
                    return node.Value.Value;
                }
            }

            public void Put(string key, CachedTranslation value)
            {
                lock (_gate)
                {
                    if (_map.TryGetValue(key, out var existing))
                    {
                        _order.Remove(existing);
                        _map.Remove(key);
                    }
                    var node = _order.AddFirst((key, value));
                    _map[key] = node;
                    while (_map.Count > _capacity && _order.Last != null)
                    {
                        var last = _order.Last;
                        _order.RemoveLast();
                        _map.Remove(last.Value.Key);
                    }
                }
            }
        }

        private static string ComputeCacheKey(string trackKey, string target, List<TextSlot> slots)
        {
            var source = new StringBuilder();
            source.Append(trackKey).Append('\u0000').Append(target);
            foreach (var slot in slots) source.Append('\u0000').Append(slot.Text);
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(source.ToString()));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static string? CreateCacheRoot()
        {
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string folder = Path.Combine(local, "LmenV2", "LyricsTranslation");
                Directory.CreateDirectory(folder);
                return folder;
            }
            catch
            {
                return null;
            }
        }

        private static async Task<CachedTranslation?> ReadCacheAsync(string key, CancellationToken ct)
        {
            string? root = CacheRoot;
            if (root == null) return null;

            await DiskMutex.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                string file = Path.Combine(root, CacheDirectoryName, key + ".json.gz");
                if (!File.Exists(file)) return null;
                try
                {
                    await using var stream = File.OpenRead(file);
                    await using var gzip = new GZipStream(stream, CompressionMode.Decompress);
                    var value = await JsonSerializer.DeserializeAsync<CachedTranslation>(gzip, JsonOptions, ct).ConfigureAwait(false);
                    try { File.SetLastWriteTimeUtc(file, DateTime.UtcNow); } catch { }
                    return value != null && value.Version == CacheVersion ? value : null;
                }
                catch
                {
                    return null;
                }
            }
            finally
            {
                DiskMutex.Release();
            }
        }

        private static async Task WriteCacheAsync(string key, CachedTranslation value, CancellationToken ct)
        {
            string? root = CacheRoot;
            if (root == null) return;

            await DiskMutex.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                string directory = Path.Combine(root, CacheDirectoryName);
                if (!Directory.Exists(directory) && !TryCreateDirectory(directory)) return;

                string destination = Path.Combine(directory, key + ".json.gz");
                string temporary = Path.Combine(directory, key + ".tmp");
                try
                {
                    await using (var stream = File.Create(temporary))
                    await using (var gzip = new GZipStream(stream, CompressionLevel.Optimal))
                    {
                        await JsonSerializer.SerializeAsync(gzip, value, JsonOptions, ct).ConfigureAwait(false);
                    }
                    File.Move(temporary, destination, overwrite: true);
                    TrimCache(directory);
                }
                catch
                {
                    try { File.Delete(temporary); } catch { }
                }
            }
            finally
            {
                DiskMutex.Release();
            }
        }

        private static bool TryCreateDirectory(string path)
        {
            try { Directory.CreateDirectory(path); return true; }
            catch { return false; }
        }

        private static void TrimCache(string directory)
        {
            try
            {
                var files = new DirectoryInfo(directory)
                    .GetFiles("*.gz")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();
                long kept = 0;
                foreach (var file in files)
                {
                    kept += file.Length;
                    if (kept > MaxCacheBytes)
                    {
                        try { file.Delete(); } catch { }
                    }
                }
            }
            catch { }
        }
    }
}

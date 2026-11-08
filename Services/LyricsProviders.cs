using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
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
    public enum LyricSourceId
    {
        LrcRed,
        BiniLyrics,
        BetterLyrics,
        BetterLyricsPortato,
        PaxSenix,
        PaxSenixSpotify,
        PaxSenixMusixmatch,
        LyricsPlus,
        SimpMusic,
        Unison,
        Megalobiz,
        KuGou,
        LrcLib,
        Musixmatch,
        Genius
    }

    public class LyricProviderResult
    {
        public LyricSourceId Source { get; set; }
        public string SourceName { get; set; } = string.Empty;
        public List<LyricLine> Lines { get; set; } = new List<LyricLine>();
        public string? PlainLyrics { get; set; }

        /// <summary>
        /// The answer carries no usable timing: either an explicit plain-text
        /// payload, or lines that all sit at zero. Such an answer is only used
        /// when nothing timed came back from any source.
        /// </summary>
        public bool IsPlain { get; set; }

        public bool IsWordSynced => Lines != null && Lines.Any(l => l.HasWordSync);
        public bool IsSynced => Lines != null && Lines.Count > 0;
        public bool IsTimed => Lines != null && Lines.Any(l => l.HasWordSync || l.StartTime > TimeSpan.Zero);
    }

    /// <summary>
    /// What a source says about itself: its label, description, whether it can
    /// return per-word timings, and whether it is kept off the list. Ported from
    /// BitChord's LyricsSource enum, including the default priority order.
    /// </summary>
    public sealed class LyricSourceInfo
    {
        public LyricSourceId Id { get; }
        public string Label { get; }
        public string Detail { get; }
        public bool WordSynced { get; }
        public bool Hidden { get; }

        public LyricSourceInfo(LyricSourceId id, string label, string detail, bool wordSynced, bool hidden = false)
        {
            Id = id;
            Label = label;
            Detail = detail;
            WordSynced = wordSynced;
            Hidden = hidden;
        }
    }

    public static class LyricSources
    {
        /// <summary>Declaration order is the default priority; hidden sources are never asked.</summary>
        public static readonly IReadOnlyList<LyricSourceInfo> All = new List<LyricSourceInfo>
        {
            new LyricSourceInfo(LyricSourceId.LrcRed, "lrc.red", "Apple timings by recording, syllable by syllable", true),
            new LyricSourceInfo(LyricSourceId.BiniLyrics, "BiniLyrics", "The same Apple timings, matched on the recording itself", true),
            new LyricSourceInfo(LyricSourceId.BetterLyrics, "BetterLyrics", "Apple Music timings, word by word", true),
            new LyricSourceInfo(LyricSourceId.BetterLyricsPortato, "BetterLyrics Portato", "QQ Music karaoke timings through BetterLyrics", true),
            new LyricSourceInfo(LyricSourceId.PaxSenix, "PaxSenix", "Apple Music timings through the original keyless provider", true),
            new LyricSourceInfo(LyricSourceId.PaxSenixSpotify, "PaxSenix: Spotify", "Spotify lyrics with PaxSenix fallback; API key required", false),
            new LyricSourceInfo(LyricSourceId.PaxSenixMusixmatch, "PaxSenix: Musixmatch", "Musixmatch timings with PaxSenix fallback; API key required", true),
            new LyricSourceInfo(LyricSourceId.LyricsPlus, "LyricsPlus", "Syllable by syllable, on community mirrors", true),
            new LyricSourceInfo(LyricSourceId.SimpMusic, "SimpMusic", "Matched on the video, so never the wrong edit", true, hidden: true),
            new LyricSourceInfo(LyricSourceId.Unison, "Unison", "Contributed by listeners, so it has what nobody licensed", true),
            new LyricSourceInfo(LyricSourceId.Megalobiz, "Megalobiz", "Community-made, whole-line LRC", false, hidden: true),
            new LyricSourceInfo(LyricSourceId.KuGou, "KuGou", "Whole lines, strong outside the English catalogue", false),
            new LyricSourceInfo(LyricSourceId.LrcLib, "LRCLIB", "Whole lines only, and always up", false),
            new LyricSourceInfo(LyricSourceId.Musixmatch, "Musixmatch", "Whole lines, from the biggest lyrics database there is", false),
            new LyricSourceInfo(LyricSourceId.Genius, "Genius", "Plain text fallback, massive web catalogue", false)
        };

        private static readonly Dictionary<LyricSourceId, LyricSourceInfo> ById =
            All.ToDictionary(info => info.Id);

        public static LyricSourceInfo Get(LyricSourceId id) => ById[id];

        /// <summary>Every source a listener can see and choose, in default priority.</summary>
        public static readonly IReadOnlyList<LyricSourceInfo> Offered =
            All.Where(info => !info.Hidden).ToList();
    }

    /// <summary>
    /// Individual provider implementations ported directly from BitChord.
    /// </summary>
    public static class LyricsProviders
    {
        private const string UserAgent = "BitChord (https://github.com/bitchord)";

        // -------------------------------------------------------------
        // 1. LrcRed: Apple Music TTML filed by ISRC (https://lrc.red)
        // -------------------------------------------------------------
        public static class LrcRed
        {
            private const string BaseUrl = "https://lrc.red/";
            private static readonly Regex IsrcRegex = new Regex(@"^[A-Z]{2}[A-Z0-9]{3}\d{7}$", RegexOptions.Compiled);
            private static readonly string[] VersionWords = new[]
            {
                "live", "remix", "acoustic", "version", "instrumental",
                "edit", "mix", "sped", "slowed", "orchestral", "club"
            };

            public static async Task<List<LyricLine>?> GetLyricsAsync(
                string title, string artist, long durationMs, string? isrc,
                Func<string, Task<string?>> documentFetcher,
                HttpClient http, CancellationToken ct)
            {
                if (!string.IsNullOrWhiteSpace(isrc))
                {
                    string cleanIsrc = isrc.Trim().ToUpperInvariant();
                    if (IsrcRegex.IsMatch(cleanIsrc))
                    {
                        string docUrl = $"{BaseUrl}s/{cleanIsrc}.ttml";
                        string? ttml = await documentFetcher(docUrl);
                        if (!string.IsNullOrWhiteSpace(ttml))
                        {
                            var lines = TtmlLyrics.Parse(ttml);
                            if (lines.Count > 0) return lines;
                        }
                    }
                }

                // Fallback to lrc.red search
                string searchUrl = $"{BaseUrl}search.json?q={Uri.EscapeDataString($"{title} {artist}".Trim())}";
                string? searchBody = await http.GetStringOrNullAsync(searchUrl, ct);
                if (string.IsNullOrWhiteSpace(searchBody)) return null;

                try
                {
                    using var doc = JsonDocument.Parse(searchBody);
                    if (doc.RootElement.TryGetProperty("hits", out var hitsProp) && hitsProp.ValueKind == JsonValueKind.Array)
                    {
                        var bestHit = PickBestHit(hitsProp, title, artist, durationMs);
                        if (bestHit != null && !string.IsNullOrWhiteSpace(bestHit.Isrc))
                        {
                            string docUrl = $"{BaseUrl}s/{bestHit.Isrc.Trim().ToUpperInvariant()}.ttml";
                            string? ttml = await documentFetcher(docUrl);
                            if (!string.IsNullOrWhiteSpace(ttml))
                            {
                                var lines = TtmlLyrics.Parse(ttml);
                                if (lines.Count > 0) return lines;
                            }
                        }
                    }
                }
                catch { }

                return null;
            }

            private class HitCandidate
            {
                public string? TrackName { get; set; }
                public string? ArtistName { get; set; }
                public double? Duration { get; set; }
                public string? Isrc { get; set; }
            }

            private static HitCandidate? PickBestHit(JsonElement hitsArray, string title, string artist, long durationMs)
            {
                var candidates = new List<HitCandidate>();
                foreach (var item in hitsArray.EnumerateArray())
                {
                    candidates.Add(new HitCandidate
                    {
                        TrackName = item.TryGetProperty("trackName", out var tn) ? tn.GetString() : null,
                        ArtistName = item.TryGetProperty("artistName", out var an) ? an.GetString() : null,
                        Duration = item.TryGetProperty("duration", out var dur) && dur.TryGetDouble(out double d) ? d : null,
                        Isrc = item.TryGetProperty("isrc", out var isrc) ? isrc.GetString() : null
                    });
                }

                double targetSec = durationMs / 1000.0;
                string cleanTargetTitle = NormalizeString(title);
                string cleanTargetArtist = NormalizeString(artist);

                var filtered = candidates.Where(c =>
                {
                    if (string.IsNullOrWhiteSpace(c.TrackName) || string.IsNullOrWhiteSpace(c.Isrc)) return false;
                    string cTitle = NormalizeString(c.TrackName);
                    if (!cTitle.Contains(cleanTargetTitle) && !cleanTargetTitle.Contains(cTitle)) return false;

                    // Version word matching
                    bool targetHasVersion = VersionWords.Any(w => cleanTargetTitle.Contains(w));
                    bool candHasVersion = VersionWords.Any(w => cTitle.Contains(w));
                    if (targetHasVersion != candHasVersion) return false;

                    // Duration tolerance check
                    if (c.Duration.HasValue && targetSec > 0 && Math.Abs(c.Duration.Value - targetSec) > 3.5)
                    {
                        return false;
                    }

                    return true;
                }).ToList();

                return filtered.OrderBy(c => c.Duration.HasValue && targetSec > 0 ? Math.Abs(c.Duration.Value - targetSec) : 0).FirstOrDefault()
                       ?? candidates.FirstOrDefault();
            }

            private static string NormalizeString(string s) =>
                new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        }

        // -------------------------------------------------------------
        // 2. BiniLyrics: Apple Music TTML with ISRC identify (https://lyrics-api.binimum.org)
        // -------------------------------------------------------------
        public static class BiniLyrics
        {
            private const string BaseUrl = "https://lyrics-api.binimum.org/";

            public class Hit
            {
                public string? TrackName { get; set; }
                public string? ArtistName { get; set; }
                public string? Isrc { get; set; }
                public string? LyricsUrl { get; set; }
            }

            public static async Task<Hit?> IdentifyAsync(string title, string artist, long durationMs, string? album, HttpClient http, CancellationToken ct)
            {
                var sb = new StringBuilder(BaseUrl);
                sb.Append("?track=").Append(Uri.EscapeDataString(title));
                sb.Append("&artist=").Append(Uri.EscapeDataString(artist));
                if (!string.IsNullOrWhiteSpace(album))
                {
                    sb.Append("&album=").Append(Uri.EscapeDataString(album));
                }
                long sec = durationMs / 1000;
                if (sec > 0) sb.Append("&duration=").Append(sec);

                string? body = await http.GetStringOrNullAsync(sb.ToString(), ct);
                if (string.IsNullOrWhiteSpace(body)) return null;

                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in results.EnumerateArray())
                        {
                            return new Hit
                            {
                                TrackName = item.TryGetProperty("track_name", out var tn) ? tn.GetString() : null,
                                ArtistName = item.TryGetProperty("artist_name", out var an) ? an.GetString() : null,
                                Isrc = item.TryGetProperty("isrc", out var isrc) ? isrc.GetString() : null,
                                LyricsUrl = item.TryGetProperty("lyricsUrl", out var lu) ? lu.GetString() : null
                            };
                        }
                    }
                }
                catch { }

                return null;
            }

            public static async Task<List<LyricLine>?> GetLyricsForHitAsync(Hit hit, Func<string, Task<string?>> documentFetcher, CancellationToken ct)
            {
                if (string.IsNullOrWhiteSpace(hit.LyricsUrl)) return null;
                string? ttml = await documentFetcher(hit.LyricsUrl);
                if (string.IsNullOrWhiteSpace(ttml)) return null;

                var lines = TtmlLyrics.Parse(ttml);
                return lines.Count > 0 ? lines : null;
            }
        }

        // -------------------------------------------------------------
        // 3. BetterLyrics: Apple Music TTML and QQ Music Portato
        // -------------------------------------------------------------
        public static class BetterLyrics
        {
            private const string BaseUrl = "https://lyrics-api.boidu.dev/getLyrics";
            private const string PortatoUrl = "https://lyrics-api.boidu.dev/qq/getLyrics";

            public static Task<List<LyricLine>?> GetLyricsAsync(string title, string artist, long durationMs, string? album, HttpClient http, CancellationToken ct) =>
                FetchAsync(BaseUrl, title, artist, durationMs, album, http, ct);

            public static Task<List<LyricLine>?> GetPortatoLyricsAsync(string title, string artist, long durationMs, string? album, HttpClient http, CancellationToken ct) =>
                FetchAsync(PortatoUrl, title, artist, durationMs, album, http, ct);

            private static async Task<List<LyricLine>?> FetchAsync(string endpoint, string title, string artist, long durationMs, string? album, HttpClient http, CancellationToken ct)
            {
                var sb = new StringBuilder(endpoint);
                sb.Append("?s=").Append(Uri.EscapeDataString(title));
                sb.Append("&a=").Append(Uri.EscapeDataString(artist));
                long sec = durationMs / 1000;
                if (sec > 0) sb.Append("&d=").Append(sec);
                if (!string.IsNullOrWhiteSpace(album)) sb.Append("&al=").Append(Uri.EscapeDataString(album));

                string? body = await http.GetStringOrNullAsync(sb.ToString(), ct);
                if (string.IsNullOrWhiteSpace(body)) return null;

                return ProviderLyrics.Parse(body);
            }
        }

        // -------------------------------------------------------------
        // 4. LyricsPlus: Multi-Mirror YouLy+ Aggregator
        // -------------------------------------------------------------
        public static class LyricsPlus
        {
            private static readonly string[] Mirrors = new[]
            {
                "https://lyricsplus.prjktla.my.id",
                "https://lyricsplus.atomix.one",
                "https://lyricsplus.binimum.org",
                "https://lyricsplus.prjktla.workers.dev",
                "https://lyricsplus-seven.vercel.app",
                "https://lyrics-plus-backend.vercel.app"
            };

            private static string? _lastGoodMirror;

            public static async Task<List<LyricLine>?> GetLyricsAsync(
                string title, string artist, long durationMs, string? album, string? isrc,
                HttpClient http, CancellationToken ct)
            {
                var hosts = new List<string>();
                if (!string.IsNullOrEmpty(_lastGoodMirror)) hosts.Add(_lastGoodMirror);
                foreach (var m in Mirrors) if (m != _lastGoodMirror) hosts.Add(m);

                using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var tasks = hosts.Select(async host =>
                {
                    try
                    {
                        var sb = new StringBuilder(host);
                        sb.Append("/v2/lyrics/get?title=").Append(Uri.EscapeDataString(title));
                        sb.Append("&artist=").Append(Uri.EscapeDataString(artist));
                        long sec = durationMs / 1000;
                        if (sec > 0) sb.Append("&duration=").Append(sec);
                        if (!string.IsNullOrWhiteSpace(album)) sb.Append("&album=").Append(Uri.EscapeDataString(album));
                        if (!string.IsNullOrWhiteSpace(isrc)) sb.Append("&isrc=").Append(Uri.EscapeDataString(isrc));

                        string? body = await http.GetStringOrNullAsync(sb.ToString(), raceCts.Token);
                        if (!string.IsNullOrWhiteSpace(body))
                        {
                            var parsed = ProviderLyrics.Parse(body);
                            if (parsed != null && parsed.Count > 0)
                            {
                                return (host, parsed);
                            }
                        }
                    }
                    catch { }
                    return (host, (List<LyricLine>?)null);
                }).ToList();

                while (tasks.Count > 0)
                {
                    var finished = await Task.WhenAny(tasks);
                    tasks.Remove(finished);
                    var (host, result) = await finished;
                    if (result != null && result.Count > 0)
                    {
                        _lastGoodMirror = host;
                        raceCts.Cancel();
                        return result;
                    }
                }

                return null;
            }
        }

        // -------------------------------------------------------------
        // 5. PaxSenix: Public Apple lyrics plus optional authenticated
        //    Spotify and Musixmatch routes (API key required).
        // -------------------------------------------------------------
        public static class PaxSenix
        {
            private const string Api = "https://api.paxsenix.org";
            private const string PublicProxy = "https://lyrics.paxsenix.org";
            private const string AppleSearch = "https://amp-api.music.apple.com/v1/catalog/us/search";
            private const int MinimumMatchScore = 10;

            private static readonly HttpClient AuthClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            private static readonly SemaphoreSlim TokenMutex = new SemaphoreSlim(1, 1);
            private static string? _cachedAppleToken;
            private static volatile string _apiKey = string.Empty;

            private static readonly Regex AppleIndexScript = new Regex(@"/assets/index~[^""]+\.js", RegexOptions.Compiled);
            private static readonly Regex AppleToken = new Regex(@"eyJ[A-Za-z0-9_-]+\.eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", RegexOptions.Compiled);

            private static readonly string[] IdKeys = { "id", "trackId", "track_id", "realId" };
            private static readonly string[] TitleKeys = { "name", "title", "trackName", "track_name" };
            private static readonly string[] ArtistKeys = { "artistName", "artist_name" };
            private static readonly string[] DurationKeys = { "durationInMillis", "durationMs", "duration_ms", "duration" };

            public static void SetApiKey(string value) => _apiKey = NormalizeApiKey(value);

            public static string NormalizeApiKey(string value)
            {
                string trimmed = (value ?? string.Empty).Trim();
                return trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? trimmed.Substring(7).Trim()
                    : trimmed;
            }

            /// <summary>The original keyless Apple Music provider used by the existing source id.</summary>
            public static async Task<List<LyricLine>?> GetLyricsAsync(string title, string artist, long durationMs, string? album, HttpClient http, CancellationToken ct)
            {
                string? id = await SearchPublicAppleTrackIdAsync(title, artist, durationMs, http, ct);
                if (id == null) return null;
                string url = $"{PublicProxy}/apple-music/lyrics?id={Uri.EscapeDataString(id)}&ttml=true";
                string? body = await http.GetStringOrNullAsync(url, ct);
                return string.IsNullOrWhiteSpace(body) ? null : ParseResponse(body);
            }

            public static async Task<List<LyricLine>?> GetSpotifyLyricsAsync(string title, string artist, long durationMs, HttpClient http, CancellationToken ct)
            {
                string? id = await SearchTrackIdAsync("spotify/search", title, artist, durationMs, ct);
                if (id != null)
                {
                    string? body = await ApiBodyAsync($"{Api}/lyrics/spotify?id={Uri.EscapeDataString(id)}", ct);
                    var parsed = string.IsNullOrWhiteSpace(body) ? null : ParseResponse(body);
                    if (parsed != null && parsed.Count > 0) return parsed;
                }
                return await GenericAuthenticatedLyricsAsync(title, artist, durationMs, ct);
            }

            public static async Task<List<LyricLine>?> GetMusixmatchLyricsAsync(string title, string artist, long durationMs, HttpClient http, CancellationToken ct)
            {
                string url = $"{Api}/lyrics/musixmatch?t={Uri.EscapeDataString(title)}&a={Uri.EscapeDataString(artist)}&d={(durationMs / 1000).ToString(CultureInfo.InvariantCulture)}";
                string? body = await ApiBodyAsync(url, ct);
                var parsed = string.IsNullOrWhiteSpace(body) ? null : ParseResponse(body);
                if (parsed != null && parsed.Count > 0) return parsed;
                return await GenericAuthenticatedLyricsAsync(title, artist, durationMs, ct);
            }

            private static async Task<string?> SearchPublicAppleTrackIdAsync(string title, string artist, long durationMs, HttpClient http, CancellationToken ct)
            {
                string? token = await AppleTokenAsync(http, ct);
                if (token == null) return null;
                string url = $"{AppleSearch}?term={Uri.EscapeDataString($"{title} {artist}")}&types=songs&limit=10&l=en-US";
                string? body = await http.GetStringOrNullAsync(url, ct, DefaultUserAgent, "application/json",
                    bearer: token, origin: "https://music.apple.com", referer: "https://music.apple.com/");
                if (string.IsNullOrWhiteSpace(body)) return null;
                using var doc = JsonDocument.Parse(body);
                return BestCandidate(doc.RootElement, title, artist, durationMs)?.Id;
            }

            private static async Task<string?> AppleTokenAsync(HttpClient http, CancellationToken ct)
            {
                if (_cachedAppleToken != null) return _cachedAppleToken;
                await TokenMutex.WaitAsync(ct);
                try
                {
                    if (_cachedAppleToken != null) return _cachedAppleToken;
                    _cachedAppleToken = await ScrapeAppleTokenAsync(http, ct);
                    return _cachedAppleToken;
                }
                finally
                {
                    TokenMutex.Release();
                }
            }

            private static async Task<string?> ScrapeAppleTokenAsync(HttpClient http, CancellationToken ct)
            {
                string? page = await http.GetStringOrNullAsync("https://music.apple.com/us/new", ct);
                if (string.IsNullOrWhiteSpace(page)) return null;
                var scriptMatch = AppleIndexScript.Match(page);
                if (!scriptMatch.Success) return null;
                string? script = await http.GetStringOrNullAsync($"https://music.apple.com{scriptMatch.Value}", ct);
                if (string.IsNullOrWhiteSpace(script)) return null;
                var tokenMatch = AppleToken.Match(script);
                return tokenMatch.Success ? tokenMatch.Value : null;
            }

            private static async Task<string?> SearchTrackIdAsync(string path, string title, string artist, long durationMs, CancellationToken ct)
            {
                string? body = await ApiBodyAsync($"{Api}/{path}?q={Uri.EscapeDataString($"{title} {artist}")}", ct);
                if (string.IsNullOrWhiteSpace(body)) return null;
                using var doc = JsonDocument.Parse(body);
                return BestCandidate(doc.RootElement, title, artist, durationMs)?.Id;
            }

            private static async Task<List<LyricLine>?> GenericAuthenticatedLyricsAsync(string title, string artist, long durationMs, CancellationToken ct)
            {
                string url = $"{Api}/lyrics/lrcget?q={Uri.EscapeDataString($"{title} {artist}")}";
                string? body = await ApiBodyAsync(url, ct);
                return string.IsNullOrWhiteSpace(body) ? null : ParseLrcGet(body, title, artist, durationMs);
            }

            private static async Task<string?> ApiBodyAsync(string url, CancellationToken ct)
            {
                string key = _apiKey;
                if (string.IsNullOrWhiteSpace(key)) return null;
                return await AuthClient.GetStringOrNullAsync(url, ct, DefaultUserAgent, "application/json, text/plain, */*", bearer: key);
            }

            // ---------------------------------------------------------
            // Response parsing
            // ---------------------------------------------------------

            private static List<LyricLine>? ParseResponse(string raw) =>
                ParseTimedApple(raw) ?? ProviderLyrics.Parse(raw);

            /// <summary>
            /// The general endpoint returns search candidates, not lyric lines. Select one
            /// recording before parsing its lyrics so timestamps do not restart mid-document.
            /// </summary>
            public static List<LyricLine>? ParseLrcGet(string raw, string title, string artist, long durationMs)
            {
                JsonDocument doc;
                try { doc = JsonDocument.Parse(raw); }
                catch { return null; }
                using (doc)
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                        !doc.RootElement.TryGetProperty("lyrics", out var payload) ||
                        payload.ValueKind != JsonValueKind.Array ||
                        payload.GetArrayLength() == 0)
                    {
                        return ParseResponse(raw);
                    }

                    List<LyricLine>? best = null;
                    int bestMeta = int.MinValue;
                    long bestDistance = long.MaxValue;
                    int bestTimed = -1;
                    int bestWord = -1;

                    foreach (var document in payload.EnumerateArray())
                    {
                        var lines = ParseResponse(document.GetRawText());
                        if (lines == null || lines.Count == 0) continue;
                        int meta = LyricCandidateScore(document, title, artist, durationMs);
                        long distance = DurationDistance(lines, durationMs);
                        int timed = lines.Count(l => l.StartTime > TimeSpan.Zero);
                        int word = lines.Count(l => l.HasWordSync);

                        bool better = meta > bestMeta ||
                            (meta == bestMeta && distance < bestDistance) ||
                            (meta == bestMeta && distance == bestDistance && timed > bestTimed) ||
                            (meta == bestMeta && distance == bestDistance && timed == bestTimed && word > bestWord);
                        if (better)
                        {
                            best = lines;
                            bestMeta = meta;
                            bestDistance = distance;
                            bestTimed = timed;
                            bestWord = word;
                        }
                    }

                    return best ?? ParseResponse(raw);
                }
            }

            /// <summary>Preserves word timestamps when PaxSenix returns its structured Apple payload.</summary>
            public static List<LyricLine>? ParseTimedApple(string raw)
            {
                JsonDocument doc;
                try { doc = JsonDocument.Parse(raw); }
                catch { return null; }
                using (doc)
                {
                    if (!TryFindTimedContent(doc.RootElement, out var content)) return null;

                    var rows = content.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();
                    var lines = new List<LyricLine>();

                    for (int index = 0; index < rows.Count; index++)
                    {
                        var row = rows[index];
                        long? startProp = LongProp(row, "timestamp");
                        if (startProp == null) continue;
                        long start = startProp.Value;
                        if (!row.TryGetProperty("text", out var wordRows) || wordRows.ValueKind != JsonValueKind.Array) continue;

                        var wordElements = wordRows.EnumerateArray().ToList();
                        var texts = wordElements
                            .Where(e => e.ValueKind == JsonValueKind.Object)
                            .Select(e => StringProp(e, "text"))
                            .Where(t => !string.IsNullOrWhiteSpace(t))
                            .Select(t => t!.Trim())
                            .ToList();
                        if (texts.Count == 0) continue;

                        long? nextLine = index + 1 < rows.Count ? LongProp(rows[index + 1], "timestamp") : null;

                        var runs = new List<TimedRun>();
                        for (int wi = 0; wi < wordElements.Count; wi++)
                        {
                            var word = wordElements[wi];
                            if (word.ValueKind != JsonValueKind.Object) continue;
                            string? text = StringProp(word, "text")?.Trim();
                            if (string.IsNullOrEmpty(text)) continue;
                            long? wStart = LongProp(word, "timestamp");
                            if (wStart == null) continue;

                            long? nextStamp = null;
                            if (wi + 1 < wordElements.Count && wordElements[wi + 1].ValueKind == JsonValueKind.Object)
                            {
                                nextStamp = LongProp(wordElements[wi + 1], "timestamp");
                            }
                            long wEnd = LongProp(word, "endtime") ?? nextStamp ?? nextLine ?? (wStart.Value + 800);

                            bool joinsNext = BoolProp(word, "part") == true;
                            runs.Add(new TimedRun(wStart.Value, wEnd, joinsNext ? text : text + " "));
                        }

                        var timed = SyllableRuns.WordsFromRuns(runs, spacingIsExplicit: true);
                        bool untimed = runs.Count != texts.Count;

                        var line = new LyricLine
                        {
                            StartTime = TimeSpan.FromMilliseconds(Math.Min(start, timed.Count > 0 ? (long)timed[0].StartTime.TotalMilliseconds : start)),
                            EndTime = TimeSpan.FromMilliseconds(nextLine ?? (timed.Count > 0 ? (long)timed[^1].EndTime.TotalMilliseconds : start)),
                            Text = untimed ? string.Join(" ", texts) : string.Join(" ", timed.Select(w => w.Text)),
                            Words = untimed ? new List<LyricWord>() : timed,
                            SungUntil = nextLine.HasValue ? TimeSpan.FromMilliseconds(nextLine.Value) : (TimeSpan?)null
                        };
                        lines.Add(line);
                    }

                    var gapped = LyricGaps.WithInstrumentalGaps(lines);
                    return gapped.Any(l => !string.IsNullOrWhiteSpace(l.Text)) ? gapped : null;
                }
            }

            private static bool TryFindTimedContent(JsonElement element, out JsonElement content)
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    if (element.TryGetProperty("content", out var candidate) &&
                        candidate.ValueKind == JsonValueKind.Array &&
                        candidate.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("timestamp", out _)))
                    {
                        content = candidate;
                        return true;
                    }
                    foreach (var prop in element.EnumerateObject())
                    {
                        if (TryFindTimedContent(prop.Value, out content)) return true;
                    }
                }
                else if (element.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in element.EnumerateArray())
                    {
                        if (TryFindTimedContent(item, out content)) return true;
                    }
                }
                content = default;
                return false;
            }

            private static long DurationDistance(List<LyricLine> lines, long durationMs)
            {
                if (durationMs <= 0) return 0;
                if (lines.Count == 0) return long.MaxValue;
                long last = 0;
                foreach (var line in lines)
                {
                    long value = (long)line.StartTime.TotalMilliseconds;
                    if (line.SungUntil.HasValue) value = Math.Max(value, (long)line.SungUntil.Value.TotalMilliseconds);
                    if (line.Words.Count > 0) value = Math.Max(value, (long)line.Words.Max(w => w.EndTime).TotalMilliseconds);
                    last = Math.Max(last, value);
                }
                return Math.Abs(last - durationMs);
            }

            private static int LyricCandidateScore(JsonElement document, string title, string artist, long durationMs)
            {
                if (document.ValueKind != JsonValueKind.Object) return 0;
                JsonElement details = document.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object
                    ? attrs
                    : document;
                var candidate = new Candidate(
                    FirstString(document, IdKeys) ?? FirstString(details, IdKeys) ?? string.Empty,
                    FirstString(details, TitleKeys) ?? string.Empty,
                    FirstString(details, ArtistKeys) ?? ArtistNames(details) ?? string.Empty,
                    ToDurationMs(FirstLong(details, DurationKeys)));
                return CandidateScore(candidate, title, artist, durationMs);
            }

            private static Candidate? BestCandidate(JsonElement root, string title, string artist, long durationMs)
            {
                var candidates = new List<Candidate>();
                CollectCandidates(root, candidates);
                Candidate? best = null;
                int bestScore = int.MinValue;
                foreach (var candidate in candidates)
                {
                    int score = CandidateScore(candidate, title, artist, durationMs);
                    if (score > bestScore)
                    {
                        best = candidate;
                        bestScore = score;
                    }
                }
                return bestScore >= MinimumMatchScore ? best : null;
            }

            private static void CollectCandidates(JsonElement element, List<Candidate> into)
            {
                if (element.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in element.EnumerateArray()) CollectCandidates(item, into);
                }
                else if (element.ValueKind == JsonValueKind.Object)
                {
                    var candidate = ToCandidate(element);
                    if (candidate != null) into.Add(candidate);
                    foreach (var prop in element.EnumerateObject()) CollectCandidates(prop.Value, into);
                }
            }

            private static Candidate? ToCandidate(JsonElement obj)
            {
                JsonElement details = obj.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object
                    ? attrs
                    : obj;
                string? id = FirstString(obj, IdKeys) ?? FirstString(details, IdKeys);
                if (id == null) return null;
                string? title = FirstString(details, TitleKeys);
                if (title == null) return null;
                string artist = FirstString(details, ArtistKeys) ?? ArtistNames(details) ?? string.Empty;
                return new Candidate(id, title, artist, ToDurationMs(FirstLong(details, DurationKeys)));
            }

            private static string? ArtistNames(JsonElement details)
            {
                JsonElement artists;
                if (!details.TryGetProperty("artists", out artists) && !details.TryGetProperty("artist", out artists)) return null;

                switch (artists.ValueKind)
                {
                    case JsonValueKind.String:
                        return artists.GetString();
                    case JsonValueKind.Object:
                        return FirstString(artists, new[] { "name", "artistName", "title" });
                    case JsonValueKind.Array:
                        var names = new List<string>();
                        foreach (var item in artists.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String)
                            {
                                var s = item.GetString();
                                if (!string.IsNullOrEmpty(s)) names.Add(s);
                            }
                            else if (item.ValueKind == JsonValueKind.Object)
                            {
                                var name = FirstString(item, new[] { "name", "artistName", "title" });
                                if (name != null) names.Add(name);
                            }
                        }
                        string joined = string.Join(", ", names);
                        return joined.Length > 0 ? joined : null;
                }
                return null;
            }

            private static string? FirstString(JsonElement obj, string[] keys)
            {
                foreach (var key in keys)
                {
                    string? value = StringProp(obj, key)?.Trim();
                    if (!string.IsNullOrEmpty(value)) return value;
                }
                return null;
            }

            private static long? FirstLong(JsonElement obj, string[] keys)
            {
                foreach (var key in keys)
                {
                    var value = LongProp(obj, key);
                    if (value.HasValue) return value;
                }
                return null;
            }

            private static string? StringProp(JsonElement obj, string key)
            {
                if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var prop)) return null;
                return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
            }

            private static long? LongProp(JsonElement obj, string key)
            {
                if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var prop)) return null;
                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out long value)) return value;
                if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)) return parsed;
                return null;
            }

            private static bool? BoolProp(JsonElement obj, string key)
            {
                if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var prop)) return null;
                if (prop.ValueKind == JsonValueKind.True) return true;
                if (prop.ValueKind == JsonValueKind.False) return false;
                return null;
            }

            private static long ToDurationMs(long? value)
            {
                if (value == null || value <= 0) return 0;
                return value < 10_000 ? value.Value * 1000 : value.Value;
            }

            private static int CandidateScore(Candidate candidate, string wantedTitle, string wantedArtist, long wantedDuration)
            {
                int score = TextScore(candidate.Title, wantedTitle, 20, 10) + TextScore(candidate.Artist, wantedArtist, 15, 5);
                if (wantedDuration > 0 && candidate.DurationMs > 0)
                {
                    long diff = Math.Abs(candidate.DurationMs - wantedDuration);
                    if (diff < 3_000) score += 10;
                    else if (diff < 10_000) score += 5;
                }
                return score;
            }

            private static int TextScore(string candidate, string wanted, int exact, int partial)
            {
                if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(wanted)) return 0;
                if (candidate.Equals(wanted, StringComparison.OrdinalIgnoreCase)) return exact;
                if (candidate.Contains(wanted, StringComparison.OrdinalIgnoreCase) || wanted.Contains(candidate, StringComparison.OrdinalIgnoreCase)) return partial;
                return 0;
            }

            private sealed class Candidate
            {
                public string Id { get; }
                public string Title { get; }
                public string Artist { get; }
                public long DurationMs { get; }

                public Candidate(string id, string title, string artist, long durationMs)
                {
                    Id = id;
                    Title = title;
                    Artist = artist;
                    DurationMs = durationMs;
                }
            }
        }

        // -------------------------------------------------------------
        // 6. Unison: Community-submitted lyrics database
        // -------------------------------------------------------------
        public static class Unison
        {
            private const string BaseUrl = "https://unison.boidu.dev/lyrics";

            public static async Task<LyricProviderResult?> GetLyricsAsync(string title, string artist, long durationMs, string? album, HttpClient http, CancellationToken ct)
            {
                var sb = new StringBuilder(BaseUrl);
                sb.Append("?song=").Append(Uri.EscapeDataString(title));
                sb.Append("&artist=").Append(Uri.EscapeDataString(artist));
                if (!string.IsNullOrWhiteSpace(album)) sb.Append("&album=").Append(Uri.EscapeDataString(album));
                long seconds = durationMs / 1000;
                if (seconds > 0) sb.Append("&duration=").Append(seconds);

                string? body = await http.GetStringOrNullAsync(sb.ToString(), ct);
                if (string.IsNullOrWhiteSpace(body)) return null;

                try
                {
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return null;
                    if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True) return null;
                    if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;

                    string? text = data.TryGetProperty("lyrics", out var lyricsProp) ? lyricsProp.GetString() : null;
                    if (string.IsNullOrWhiteSpace(text)) return null;
                    string? format = data.TryGetProperty("format", out var formatProp) ? formatProp.GetString() : null;
                    string? syncType = data.TryGetProperty("syncType", out var syncProp) ? syncProp.GetString() : null;

                    var result = new LyricProviderResult { Source = LyricSourceId.Unison, SourceName = "Unison" };

                    if (string.Equals(format, "ttml", StringComparison.OrdinalIgnoreCase))
                    {
                        var ttml = TtmlLyrics.Parse(text);
                        if (ttml.Count == 0) return null;
                        result.Lines = ttml;
                        return result;
                    }

                    if (string.Equals(syncType, "plain", StringComparison.OrdinalIgnoreCase))
                    {
                        var plain = PlainLines(text);
                        if (plain.Count == 0) return null;
                        result.Lines = plain;
                        result.IsPlain = true;
                        result.PlainLyrics = string.Join("\n", plain.Select(l => l.Text));
                        return result;
                    }

                    var enhanced = EnhancedLrc.Parse(text);
                    if (enhanced.Count > 0)
                    {
                        result.Lines = enhanced;
                        return result;
                    }

                    var lrc = LrcLibParser.ParseLrc(text);
                    if (lrc.Count == 0) return null;
                    result.Lines = lrc;
                    return result;
                }
                catch
                {
                    return null;
                }
            }
        }

        // -------------------------------------------------------------
        // 7. SimpMusic: Community database keyed on the YouTube video id.
        //    Never asked without a video id — hidden, like in BitChord.
        // -------------------------------------------------------------
        public static class SimpMusic
        {
            private const string BaseUrl = "https://api-lyrics.simpmusic.org/v1/";
            private const int DurationToleranceSeconds = 10;

            public static async Task<List<LyricLine>?> GetLyricsAsync(string? videoId, long durationMs, HttpClient http, CancellationToken ct)
            {
                if (string.IsNullOrWhiteSpace(videoId)) return null;
                string? body = await http.GetStringOrNullAsync(BaseUrl + Uri.EscapeDataString(videoId), ct);
                if (string.IsNullOrWhiteSpace(body)) return null;

                try
                {
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return null;
                    if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True) return null;
                    if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;

                    int seconds = (int)(durationMs / 1000);
                    JsonElement? best = null;
                    double bestDistance = double.MaxValue;
                    foreach (var track in data.EnumerateArray())
                    {
                        double duration = track.TryGetProperty("duration", out var durProp) && durProp.TryGetDouble(out double d) ? d : 0;
                        if (seconds > 0 && Math.Abs(duration - seconds) > DurationToleranceSeconds) continue;
                        double distance = Math.Abs(duration - seconds);
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            best = track;
                        }
                    }

                    if (best == null) return null;
                    var chosen = best.Value;

                    string? richSync = chosen.TryGetProperty("richSyncLyrics", out var rich) ? rich.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(richSync))
                    {
                        var richLines = EnhancedLrc.Parse(richSync);
                        if (richLines.Count > 0) return richLines;
                    }

                    string? synced = chosen.TryGetProperty("syncedLyrics", out var sync) ? sync.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(synced))
                    {
                        var lines = LrcLibParser.ParseLrc(synced);
                        if (lines.Count > 0) return lines;
                    }

                    return null;
                }
                catch
                {
                    return null;
                }
            }
        }

        // -------------------------------------------------------------
        // 8. Musixmatch: Rich word timing, with line-synced fallback,
        //    from Musixmatch's web API.
        // -------------------------------------------------------------
        public static class Musixmatch
        {
            private const string BaseUrl = "https://apic.musixmatch.com/ws/1.1";
            private const string AppId = "mobile-app-v1.0";
            private const string SearchPage = "https://www.musixmatch.com/search";
            private const string BrowserAgent =
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

            // Used only if the public page serving the rotating key is briefly unavailable.
            private const string FallbackSigningSecret = "f09016176ba43a1cfd1031fbd6b3d26c";

            private static readonly SemaphoreSlim CredentialMutex = new SemaphoreSlim(1, 1);
            private static string? _cachedSecret;
            private static string? _cachedToken;
            private static readonly string ProcessGuid = Guid.NewGuid().ToString();

            private static readonly Regex AppScriptRegex = new Regex(
                @"src=[""']([^""']*/_next/static/chunks/pages/_app-[^""']+\.js)[""']",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
            private static readonly Regex EncodedSecretRegex = new Regex(
                @"from\(\s*[""']([^""']+)[""']\s*\.split",
                RegexOptions.Compiled);

            public static async Task<List<LyricLine>?> GetLyricsAsync(string title, string artist, long durationMs, HttpClient http, CancellationToken ct)
            {
                int seconds = (int)(durationMs / 1000);
                var track = await BestTrackAsync(title, artist, seconds, http, ct);
                if (track == null) return null;

                // Prefer the separate rich-sync tier so tracks that have syllable
                // data do not get flattened on ingestion.
                if (track.HasRichSync != 0)
                {
                    string? richBody = await FetchRichSyncAsync(track.TrackId, http, ct);
                    if (!string.IsNullOrWhiteSpace(richBody))
                    {
                        var rich = ParseRichSyncBody(richBody);
                        if (rich.Any(l => l.HasWordSync)) return rich;
                    }
                }

                string? subtitle = track.HasSubtitles != 0 ? await FetchSubtitleAsync(track.TrackId, http, ct) : null;
                string lrc = subtitle != null ? SubtitleToLrc(subtitle) : string.Empty;
                if (string.IsNullOrWhiteSpace(lrc)) return null;
                var lines = LrcLibParser.ParseLrc(lrc);
                return lines.Count > 0 ? lines : null;
            }

            private static async Task<TrackEntry?> BestTrackAsync(string title, string artist, int seconds, HttpClient http, CancellationToken ct)
            {
                var tracks = await SearchTrackAsync(title, artist, http, ct);
                if (tracks == null || tracks.Count == 0) return null;
                TrackEntry? best = null;
                double bestScore = double.MinValue;
                foreach (var track in tracks)
                {
                    double score = Score(track, title, artist, seconds);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = track;
                    }
                }
                return best;
            }

            private static double Score(TrackEntry track, string title, string artist, int seconds)
            {
                double score = 0;
                string name = track.TrackName.Trim().ToLowerInvariant();
                string targetTitle = title.Trim().ToLowerInvariant();
                if (name == targetTitle) score += 80.0;
                else if (name.Contains(targetTitle) || targetTitle.Contains(name)) score += 40.0;

                if (track.ArtistName.Trim().ToLowerInvariant().Contains(artist.Trim().ToLowerInvariant())) score += 40.0;

                if (track.TrackLength.HasValue)
                {
                    int diff = Math.Abs(track.TrackLength.Value - seconds);
                    if (diff <= 2) score += 30.0;
                    else if (diff <= 5) score += 15.0;
                    else if (diff <= 10) score += 5.0;
                    else score -= 20.0;
                }
                return score;
            }

            private static async Task<List<TrackEntry>?> SearchTrackAsync(string title, string artist, HttpClient http, CancellationToken ct)
            {
                string? response = await SignedGetAsync(token =>
                    $"{BaseUrl}/track.search?app_id={AppId}&format=json" +
                    $"&q_track={Uri.EscapeDataString(title)}&q_artist={Uri.EscapeDataString(artist)}" +
                    $"&f_has_lyrics=1&s_track_rating=desc&quorum_factor=1&page_size=10&page=1" +
                    $"&usertoken={Uri.EscapeDataString(token)}", http, ct);
                if (string.IsNullOrWhiteSpace(response)) return null;

                try
                {
                    using var doc = JsonDocument.Parse(response);
                    var tracks = new List<TrackEntry>();
                    if (doc.RootElement.TryGetProperty("message", out var message) &&
                        message.TryGetProperty("body", out var body) &&
                        body.ValueKind == JsonValueKind.Object &&
                        body.TryGetProperty("track_list", out var list) &&
                        list.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var wrapper in list.EnumerateArray())
                        {
                            if (!wrapper.TryGetProperty("track", out var track) || track.ValueKind != JsonValueKind.Object) continue;
                            tracks.Add(new TrackEntry
                            {
                                TrackId = track.TryGetProperty("track_id", out var id) && id.TryGetInt64(out long tid) ? tid : 0,
                                TrackName = track.TryGetProperty("track_name", out var tn) ? tn.GetString() ?? "" : "",
                                ArtistName = track.TryGetProperty("artist_name", out var an) ? an.GetString() ?? "" : "",
                                TrackLength = track.TryGetProperty("track_length", out var tl) && tl.TryGetInt32(out int len) ? len : (int?)null,
                                HasSubtitles = track.TryGetProperty("has_subtitles", out var hs) && hs.TryGetInt32(out int hsi) ? hsi : 0,
                                HasRichSync = track.TryGetProperty("has_richsync", out var hr) && hr.TryGetInt32(out int hri) ? hri : 0
                            });
                        }
                    }
                    return tracks;
                }
                catch
                {
                    return null;
                }
            }

            private static async Task<string?> FetchSubtitleAsync(long trackId, HttpClient http, CancellationToken ct)
            {
                string? response = await SignedGetAsync(token =>
                    $"{BaseUrl}/track.subtitle.get?app_id={AppId}&format=json" +
                    $"&track_id={trackId.ToString(CultureInfo.InvariantCulture)}" +
                    $"&subtitle_format=mxm&usertoken={Uri.EscapeDataString(token)}", http, ct);
                if (string.IsNullOrWhiteSpace(response)) return null;

                try
                {
                    using var doc = JsonDocument.Parse(response);
                    if (doc.RootElement.TryGetProperty("message", out var message) &&
                        message.TryGetProperty("body", out var body) &&
                        body.ValueKind == JsonValueKind.Object &&
                        body.TryGetProperty("subtitle", out var subtitle) &&
                        subtitle.ValueKind == JsonValueKind.Object &&
                        subtitle.TryGetProperty("subtitle_body", out var text))
                    {
                        return text.GetString();
                    }
                }
                catch { }
                return null;
            }

            private static async Task<string?> FetchRichSyncAsync(long trackId, HttpClient http, CancellationToken ct)
            {
                string? response = await SignedGetAsync(token =>
                    $"{BaseUrl}/track.richsync.get?app_id={AppId}&format=json" +
                    $"&track_id={trackId.ToString(CultureInfo.InvariantCulture)}" +
                    $"&usertoken={Uri.EscapeDataString(token)}", http, ct);
                if (string.IsNullOrWhiteSpace(response)) return null;

                try
                {
                    using var doc = JsonDocument.Parse(response);
                    if (doc.RootElement.TryGetProperty("message", out var message) &&
                        message.TryGetProperty("body", out var body) &&
                        body.ValueKind == JsonValueKind.Object &&
                        body.TryGetProperty("richsync", out var richsync) &&
                        richsync.ValueKind == JsonValueKind.Object &&
                        richsync.TryGetProperty("richsync_body", out var text))
                    {
                        return text.GetString();
                    }
                }
                catch { }
                return null;
            }

            /// <summary>
            /// Decodes rich-sync offsets without flattening them through line LRC.
            /// Source order is retained because punctuation and syllable fragments may share a stamp.
            /// </summary>
            public static List<LyricLine> ParseRichSyncBody(string body)
            {
                List<RichSyncEntry>? entries;
                try
                {
                    entries = JsonSerializer.Deserialize<List<RichSyncEntry>>(body);
                }
                catch
                {
                    return new List<LyricLine>();
                }
                if (entries == null) return new List<LyricLine>();

                var result = new List<LyricLine>();
                foreach (var entry in entries)
                {
                    long lineStart = SecondsToMs(entry.StartSeconds);
                    long lineEnd = Math.Max(lineStart, SecondsToMs(entry.EndSeconds));
                    long previousStart = lineStart;

                    var runs = new List<TimedRun>();
                    for (int i = 0; i < entry.Fragments.Count; i++)
                    {
                        var fragment = entry.Fragments[i];
                        if (string.IsNullOrEmpty(fragment.Text)) continue;

                        long start = Math.Max(lineStart, Math.Max(previousStart, SecondsToMs(entry.StartSeconds + fragment.OffsetSeconds)));
                        long next = i + 1 < entry.Fragments.Count
                            ? SecondsToMs(entry.StartSeconds + entry.Fragments[i + 1].OffsetSeconds)
                            : lineEnd;
                        previousStart = start;
                        runs.Add(new TimedRun(start, Math.Max(start, Math.Min(lineEnd, next)), fragment.Text));
                    }

                    var words = SyllableRuns.WordsFromRuns(runs, spacingIsExplicit: true);
                    string text = entry.Text.Trim();
                    if (text.Length == 0) text = string.Join(" ", words.Select(w => w.Text));
                    if (text.Length == 0) continue;

                    result.Add(new LyricLine
                    {
                        StartTime = TimeSpan.FromMilliseconds(Math.Min(lineStart, words.Count > 0 ? (long)words[0].StartTime.TotalMilliseconds : lineStart)),
                        EndTime = TimeSpan.FromMilliseconds(lineEnd),
                        Text = text,
                        Words = words,
                        SungUntil = lineEnd > lineStart ? TimeSpan.FromMilliseconds(lineEnd) : (TimeSpan?)null
                    });
                }

                result.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
                return LyricGaps.WithInstrumentalGaps(result);
            }

            /// <summary>Musixmatch's `mxm` subtitle JSON, converted only for the line-sync fallback.</summary>
            private static string SubtitleToLrc(string subtitleBody)
            {
                try
                {
                    using var doc = JsonDocument.Parse(subtitleBody);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) return string.Empty;
                    var sb = new StringBuilder();
                    foreach (var line in doc.RootElement.EnumerateArray())
                    {
                        string? text = line.TryGetProperty("text", out var textProp) ? textProp.GetString() : null;
                        if (string.IsNullOrWhiteSpace(text)) continue;
                        if (!line.TryGetProperty("time", out var time) || time.ValueKind != JsonValueKind.Object) continue;
                        if (!time.TryGetProperty("total", out var total) || !total.TryGetDouble(out double totalSeconds)) continue;

                        long totalMs = SecondsToMs(totalSeconds);
                        long minutes = totalMs / 1000 / 60;
                        long seconds = (totalMs / 1000) % 60;
                        long millis = totalMs % 1000;
                        sb.Append('[')
                            .Append(minutes.ToString("00", CultureInfo.InvariantCulture))
                            .Append(':')
                            .Append(seconds.ToString("00", CultureInfo.InvariantCulture))
                            .Append('.')
                            .Append(millis.ToString("000", CultureInfo.InvariantCulture))
                            .Append(']')
                            .Append(text)
                            .Append('\n');
                    }
                    return sb.ToString().Trim();
                }
                catch
                {
                    return string.Empty;
                }
            }

            private static long SecondsToMs(double seconds) => (long)(seconds * 1000.0);

            /// <summary>Signs and issues [buildUrl]; an auth failure refreshes both moving credentials once.</summary>
            private static async Task<string?> SignedGetAsync(Func<string, string> buildUrl, HttpClient http, CancellationToken ct)
            {
                string? secret = await GetSecretAsync(http, ct);
                string? token = secret != null ? await GetTokenAsync(secret, http, ct) : null;
                if (secret == null || token == null) return null;

                string first = await ApiGetAsync(Sign(buildUrl(token), secret), http, ct) ?? string.Empty;
                if (!string.IsNullOrEmpty(first) && !LooksUnauthorized(first)) return first;

                _cachedToken = null;
                _cachedSecret = null;
                string? freshSecret = await GetSecretAsync(http, ct);
                string? freshToken = freshSecret != null ? await GetTokenAsync(freshSecret, http, ct) : null;
                if (freshSecret == null || freshToken == null) return null;
                return await ApiGetAsync(Sign(buildUrl(freshToken), freshSecret), http, ct);
            }

            /// <summary>Musixmatch reports an expired token inside a successful HTTP response.</summary>
            private static bool LooksUnauthorized(string body)
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    return doc.RootElement.TryGetProperty("message", out var message) &&
                           message.TryGetProperty("header", out var header) &&
                           header.TryGetProperty("status_code", out var status) &&
                           status.TryGetInt32(out int code) && (code == 401 || code == 402);
                }
                catch
                {
                    return false;
                }
            }

            private static async Task<string?> GetTokenAsync(string secret, HttpClient http, CancellationToken ct)
            {
                if (_cachedToken != null) return _cachedToken;
                await CredentialMutex.WaitAsync(ct);
                try
                {
                    if (_cachedToken != null) return _cachedToken;
                    string? url = $"{BaseUrl}/token.get?app_id={AppId}&guid={Uri.EscapeDataString(ProcessGuid)}&format=json";
                    string? body = await ApiGetAsync(Sign(url, secret), http, ct);
                    if (string.IsNullOrWhiteSpace(body)) return null;

                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("message", out var message) &&
                            message.TryGetProperty("body", out var bodyElement) &&
                            bodyElement.ValueKind == JsonValueKind.Object &&
                            bodyElement.TryGetProperty("user_token", out var userToken))
                        {
                            _cachedToken = userToken.GetString();
                        }
                    }
                    catch { }
                    return _cachedToken;
                }
                finally
                {
                    CredentialMutex.Release();
                }
            }

            /// <summary>Reads the rotating signing key from the JavaScript linked by the current web client.</summary>
            private static async Task<string?> GetSecretAsync(HttpClient http, CancellationToken ct)
            {
                if (_cachedSecret != null) return _cachedSecret;
                try
                {
                    string? page = await http.GetStringOrNullAsync(SearchPage, ct, BrowserAgent, "text/html,application/xhtml+xml", cookie: "mxm_bab=AB");
                    if (string.IsNullOrWhiteSpace(page)) throw new InvalidOperationException("search page unavailable");

                    var scriptMatch = AppScriptRegex.Match(page);
                    if (!scriptMatch.Success) throw new InvalidOperationException("application script not found");
                    string? scriptUrl = new Uri(new Uri(SearchPage), scriptMatch.Groups[1].Value).ToString();

                    string? javascript = await http.GetStringOrNullAsync(scriptUrl, ct, BrowserAgent, "*/*", cookie: "mxm_bab=AB");
                    if (string.IsNullOrWhiteSpace(javascript)) throw new InvalidOperationException("application script unavailable");

                    var encodedMatch = EncodedSecretRegex.Match(javascript);
                    if (!encodedMatch.Success) throw new InvalidOperationException("signing key not found");

                    char[] reversed = encodedMatch.Groups[1].Value.Reverse().ToArray();
                    string secret = Encoding.UTF8.GetString(Convert.FromBase64String(new string(reversed)));
                    if (string.IsNullOrEmpty(secret.Trim())) throw new InvalidOperationException("empty signing key");
                    _cachedSecret = secret;
                }
                catch
                {
                    _cachedSecret = FallbackSigningSecret;
                }
                return _cachedSecret;
            }

            /// <summary>Signs `&lt;url&gt;&lt;UTC yyyyMMdd&gt;` with HMAC-SHA256, matching the current web client.</summary>
            private static string Sign(string url, string secret)
            {
                string normalized = url.Replace("%20", "+").Replace(" ", "+");
                string date = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
                byte[] raw = hmac.ComputeHash(Encoding.UTF8.GetBytes(normalized + date));
                string signature = Convert.ToBase64String(raw);
                return normalized + "&signature=" + Uri.EscapeDataString(signature) + "&signature_protocol=sha256";
            }

            private static Task<string?> ApiGetAsync(string url, HttpClient http, CancellationToken ct) =>
                http.GetStringOrNullAsync(url, ct, BrowserAgent, "application/json, text/plain, */*");

            private sealed class TrackEntry
            {
                public long TrackId { get; set; }
                public string TrackName { get; set; } = string.Empty;
                public string ArtistName { get; set; } = string.Empty;
                public int? TrackLength { get; set; }
                public int HasSubtitles { get; set; }
                public int HasRichSync { get; set; }
            }

            private sealed class RichSyncEntry
            {
                [System.Text.Json.Serialization.JsonPropertyName("ts")]
                public double StartSeconds { get; set; }

                [System.Text.Json.Serialization.JsonPropertyName("te")]
                public double EndSeconds { get; set; }

                [System.Text.Json.Serialization.JsonPropertyName("l")]
                public List<RichSyncFragment> Fragments { get; set; } = new List<RichSyncFragment>();

                [System.Text.Json.Serialization.JsonPropertyName("x")]
                public string Text { get; set; } = string.Empty;
            }

            private sealed class RichSyncFragment
            {
                [System.Text.Json.Serialization.JsonPropertyName("c")]
                public string Text { get; set; } = string.Empty;

                [System.Text.Json.Serialization.JsonPropertyName("o")]
                public double OffsetSeconds { get; set; }
            }
        }

        // -------------------------------------------------------------
        // 9. Megalobiz: Line-synced community LRC scraped from Megalobiz.
        //    Hidden in BitChord; ported for parity, never asked by default.
        // -------------------------------------------------------------
        public static class Megalobiz
        {
            private const string BaseUrl = "https://www.megalobiz.com";

            private static readonly Regex LrcLink = new Regex(
                @"href=[""'](/lrc/maker/download/[^""']+)[""']",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
            private static readonly Regex LrcBody = new Regex(
                @"id=[""']lrc_[^""']*_details[""'][^>]*>(.*?)</span>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

            public static async Task<List<LyricLine>?> GetLyricsAsync(string title, string artist, HttpClient http, CancellationToken ct)
            {
                string search = $"{BaseUrl}/searchall?qry={Uri.EscapeDataString($"{artist} {title}".Trim())}";
                string? results = await http.GetStringOrNullAsync(search, ct);
                if (string.IsNullOrWhiteSpace(results)) return null;

                var linkMatch = LrcLink.Match(results);
                if (!linkMatch.Success) return null;
                string path = linkMatch.Groups[1].Value.Replace("&amp;", "&");

                string? page = await http.GetStringOrNullAsync(BaseUrl + path, ct);
                if (string.IsNullOrWhiteSpace(page)) return null;

                var bodyMatch = LrcBody.Match(page);
                if (!bodyMatch.Success) return null;

                string lrc = Regex.Replace(bodyMatch.Groups[1].Value, @"(?i)<br\s*/?>", "\n");
                lrc = Regex.Replace(lrc, @"<[^>]+>", string.Empty);
                lrc = WebUtility.HtmlDecode(lrc);

                var lines = LrcLibParser.ParseLrc(lrc);
                return lines.Any(l => !string.IsNullOrWhiteSpace(l.Text)) ? lines : null;
            }
        }

        // -------------------------------------------------------------
        // 10. Genius: Web scraper for plain-text lyrics. A last resort
        //     fallback, started lazily when nothing timed came back.
        // -------------------------------------------------------------
        public static class Genius
        {
            /// <summary>
            /// Deliberately not a browser. Genius sits behind Cloudflare, which now
            /// challenges a browser-claiming User-Agent whose TLS fingerprint and
            /// sec-ch-ua headers do not back the claim; a plain one is answered 200.
            /// </summary>
            private const string UserAgent = "BitChord";

            private static readonly Regex TitleSeparator = new Regex(@"\s*[-–—:]\s*", RegexOptions.Compiled);
            private static readonly Regex DecorativeChars = new Regex("[♪♫★☆【】《》「」~_]", RegexOptions.Compiled);
            private static readonly Regex ProducerTags = new Regex(@"(?i)\b(?:prod(?:uced)?\.?(?:\s+by)?)\s+.*$", RegexOptions.Compiled);
            private static readonly Regex Noise = new Regex(
                @"\s*[(\[]\s*(?:from|feat\.?|ft\.?|featuring|with|prod\.?|produced by|official|lyrical|video|audio|remix|music video|visualizer|mv|hd|4k|hq|full song)[^)\]]*[)\]]|" +
                @"\s*\b(?:official\s+(?:music\s+)?(?:video|audio)|lyrical(?:\s+video)?|full\s+song|4k\s+video|hd\s+video|music\s+video)\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
            private static readonly Regex BracketedContent = new Regex(@"\s*[\(\[].*?[\)\]]", RegexOptions.Compiled);
            private static readonly Regex NonAlphanumeric = new Regex(@"[^\p{L}\p{N}\s]", RegexOptions.Compiled);
            private static readonly Regex YouMightAlsoLike = new Regex(@"\d*You might also like", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            private static readonly Regex TrailingEmbed = new Regex(@"\d*Embed\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            private static readonly Regex MultipleSpaces = new Regex(@"\s+", RegexOptions.Compiled);

            private static readonly Regex ContainerOpener = new Regex(
                @"<div[^>]*data-lyrics-container\s*=\s*[""']?true[""']?[^>]*>",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
            private static readonly Regex LegacyLyricsOpener = new Regex(
                @"<div[^>]*class\s*=\s*[""'][^""']*\blyrics\b[^""']*[""'][^>]*>",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

            private static readonly Regex ScriptBlock = new Regex(@"<script[\s\S]*?</script>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            private static readonly Regex StyleBlock = new Regex(@"<style[\s\S]*?</style>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            private static readonly Regex ButtonBlock = new Regex(@"<button[\s\S]*?</button>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            private static readonly Regex ExcludedBlock = new Regex(@"<div[^>]*data-exclude-from-selection[^>]*>[\s\S]*?</div>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            private static readonly Regex ChromeBlock = new Regex(
                @"<div[^>]*class=""[^""]*(?:LyricsHeader__Container|SongBioPreview__Container|InreadAd__Container)[^""]*""[^>]*>[\s\S]*?</div>",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
            private static readonly Regex BreakTag = new Regex(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            private static readonly Regex ParagraphTag = new Regex(@"<p([^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            private static readonly Regex AnyTag = new Regex(@"<[^>]+>", RegexOptions.Compiled);

            public static async Task<LyricProviderResult?> GetLyricsAsync(string title, string artist, HttpClient http, CancellationToken ct)
            {
                try
                {
                    var lines = await ScrapeLyricsAsync(title, artist, http, ct);
                    if (lines == null || lines.Count == 0) return null;

                    string text = string.Join("\n", lines.Select(l => l.Text));
                    if (string.IsNullOrWhiteSpace(text)) return null;

                    return new LyricProviderResult
                    {
                        Source = LyricSourceId.Genius,
                        SourceName = "Genius",
                        IsPlain = true,
                        PlainLyrics = text,
                        Lines = lines
                    };
                }
                catch
                {
                    return null;
                }
            }

            private static async Task<List<LyricLine>?> ScrapeLyricsAsync(string title, string artist, HttpClient http, CancellationToken ct)
            {
                string cleanTitle = CleanQuery(title);
                string cleanArtist = CleanQuery(artist);

                // If the title is in "Artist - Title" format, extract both parts.
                string[]? titleParts = null;
                var separatorMatch = TitleSeparator.Match(cleanTitle);
                if (separatorMatch.Success)
                {
                    string left = cleanTitle.Substring(0, separatorMatch.Index).Trim();
                    string right = cleanTitle.Substring(separatorMatch.Index + separatorMatch.Length).Trim();
                    titleParts = new[] { left, right };
                }

                string extractedTitle;
                if (titleParts != null && titleParts[0].Equals(cleanArtist, StringComparison.OrdinalIgnoreCase)) extractedTitle = titleParts[1];
                else if (titleParts != null && titleParts[1].Equals(cleanArtist, StringComparison.OrdinalIgnoreCase)) extractedTitle = titleParts[0];
                else if (titleParts != null && titleParts[0].Length > 0 && titleParts[1].Length > 0) extractedTitle = titleParts[1];
                else extractedTitle = cleanTitle;

                string extractedArtist;
                if (titleParts != null && titleParts[0].Equals(cleanArtist, StringComparison.OrdinalIgnoreCase)) extractedArtist = cleanArtist;
                else if (titleParts != null && titleParts[1].Equals(cleanArtist, StringComparison.OrdinalIgnoreCase)) extractedArtist = cleanArtist;
                else if (titleParts != null && cleanArtist.Length == 0) extractedArtist = titleParts[0];
                else extractedArtist = cleanArtist;

                string titleWithoutBrackets = MultipleSpaces.Replace(
                    NonAlphanumeric.Replace(BracketedContent.Replace(extractedTitle, " "), " "), " ").Trim();

                var attempts = new List<SearchAttempt>();
                if (extractedArtist.Length > 0 && extractedTitle.Length > 0)
                {
                    attempts.Add(new SearchAttempt($"{extractedArtist} {extractedTitle}".Trim(), extractedTitle, extractedArtist));
                }
                if (extractedArtist.Length > 0 && titleWithoutBrackets.Length > 0 && titleWithoutBrackets != extractedTitle)
                {
                    attempts.Add(new SearchAttempt($"{extractedArtist} {titleWithoutBrackets}".Trim(), titleWithoutBrackets, extractedArtist));
                }
                if (cleanTitle != extractedTitle)
                {
                    attempts.Add(new SearchAttempt($"{cleanArtist} {cleanTitle}".Trim(), cleanTitle, cleanArtist));
                    attempts.Add(new SearchAttempt(cleanTitle, extractedTitle, extractedArtist));
                }
                if (titleWithoutBrackets.Length > 0)
                {
                    attempts.Add(new SearchAttempt(titleWithoutBrackets, titleWithoutBrackets, extractedArtist));
                }
                else if (extractedTitle.Length > 0)
                {
                    attempts.Add(new SearchAttempt(extractedTitle, extractedTitle, extractedArtist));
                }

                var seenQueries = new HashSet<string>();
                foreach (var attempt in attempts)
                {
                    if (!seenQueries.Add(attempt.Query)) continue;
                    string? songUrl = await SearchSongUrlAsync(attempt.Query, attempt.Title, attempt.Artist, http, ct);
                    if (songUrl == null) continue;

                    string? html = await http.GetStringOrNullAsync(songUrl, ct, UserAgent,
                        "text/html,application/xhtml+xml,application/xml;q=0.9,application/json,*/*;q=0.8");
                    if (string.IsNullOrWhiteSpace(html)) continue;

                    var lines = ParseHtml(html);
                    if (lines != null && lines.Count > 0) return lines;
                }

                return null;
            }

            private static async Task<string?> SearchSongUrlAsync(string query, string targetTitle, string targetArtist, HttpClient http, CancellationToken ct)
            {
                string url = $"https://genius.com/api/search/multi?q={Uri.EscapeDataString(query)}";
                string? responseBody = await http.GetStringOrNullAsync(url, ct, UserAgent, "text/html,application/xhtml+xml,application/xml;q=0.9,application/json,*/*;q=0.8");
                if (string.IsNullOrWhiteSpace(responseBody)) return null;

                try
                {
                    using var doc = JsonDocument.Parse(responseBody);
                    if (!doc.RootElement.TryGetProperty("response", out var response) ||
                        !response.TryGetProperty("sections", out var sections) ||
                        sections.ValueKind != JsonValueKind.Array) return null;

                    JsonElement? songSection = null;
                    foreach (var section in sections.EnumerateArray())
                    {
                        if (section.ValueKind == JsonValueKind.Object &&
                            section.TryGetProperty("type", out var type) &&
                            type.ValueKind == JsonValueKind.String &&
                            type.GetString() == "song")
                        {
                            songSection = section;
                            break;
                        }
                    }
                    if (songSection == null || !songSection.Value.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array) return null;

                    var candidates = new List<JsonElement>();
                    foreach (var hit in hits.EnumerateArray())
                    {
                        if (hit.ValueKind == JsonValueKind.Object &&
                            hit.TryGetProperty("result", out var result) &&
                            result.ValueKind == JsonValueKind.Object)
                        {
                            candidates.Add(result);
                        }
                    }

                    var best = BestMatch(candidates, targetTitle, targetArtist);
                    if (best == null) return null;
                    return best.Value.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null;
                }
                catch
                {
                    return null;
                }
            }

            private static JsonElement? BestMatch(List<JsonElement> candidates, string targetTitle, string targetArtist)
            {
                if (candidates.Count == 0) return null;
                string normTitle = targetTitle.ToLowerInvariant();
                string normArtist = targetArtist.ToLowerInvariant();

                JsonElement? best = null;
                int bestScore = int.MinValue;

                foreach (var item in candidates)
                {
                    string title = item.TryGetProperty("title", out var titleProp) ? (titleProp.GetString() ?? "").ToLowerInvariant() : "";
                    string artist = item.TryGetProperty("artist_names", out var artistProp) ? (artistProp.GetString() ?? "").ToLowerInvariant() : "";
                    int score = 0;

                    bool titleMatches = normTitle.Length > 0 && (title == normTitle || title.Contains(normTitle) || normTitle.Contains(title));
                    bool artistMatches = normArtist.Length > 0 && (artist == normArtist || artist.Contains(normArtist) || normArtist.Contains(artist));
                    if (!titleMatches && !artistMatches) continue;

                    if (title == normTitle) score += 50;
                    else if (titleMatches) score += 25;

                    if (artistMatches)
                    {
                        if (artist == normArtist) score += 40;
                        else score += 20;
                    }

                    // Penalize translations / instrumentals / reviews unless specifically requested.
                    string path = item.TryGetProperty("path", out var pathProp) ? (pathProp.GetString() ?? "").ToLowerInvariant() : "";
                    if (path.Contains("translation") && !normTitle.Contains("translation")) score -= 30;
                    if (path.Contains("türkçe") || path.Contains("polskie-tlumaczenie")) score -= 40;
                    if (path.Contains("tracklist") || path.Contains("album-art")) score -= 50;

                    if (score <= 0) continue;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = item;
                    }
                }

                return best;
            }

            public static List<LyricLine>? ParseHtml(string html)
            {
                try
                {
                    var containers = ExtractDivContainers(html, ContainerOpener);
                    if (containers.Count == 0) containers = ExtractDivContainers(html, LegacyLyricsOpener);
                    if (containers.Count == 0) return null;

                    var builder = new StringBuilder();
                    foreach (var raw in containers)
                    {
                        string cleaned = ScriptBlock.Replace(raw, string.Empty);
                        cleaned = StyleBlock.Replace(cleaned, string.Empty);
                        cleaned = ButtonBlock.Replace(cleaned, string.Empty);
                        cleaned = ExcludedBlock.Replace(cleaned, string.Empty);
                        cleaned = ChromeBlock.Replace(cleaned, string.Empty);
                        cleaned = BreakTag.Replace(cleaned, "\n");
                        cleaned = ParagraphTag.Replace(cleaned, "<p$1>\n");
                        cleaned = AnyTag.Replace(cleaned, string.Empty);
                        cleaned = WebUtility.HtmlDecode(cleaned);
                        if (!string.IsNullOrWhiteSpace(cleaned)) builder.Append(cleaned).Append('\n');
                    }

                    string rawLyrics = builder.ToString();
                    if (string.IsNullOrWhiteSpace(rawLyrics)) return null;
                    string stripped = StripArtifacts(rawLyrics);
                    var lines = TextToLyricLines(stripped);
                    return lines.Count > 0 ? lines : null;
                }
                catch
                {
                    return null;
                }
            }

            private static List<string> ExtractDivContainers(string html, Regex opener)
            {
                var result = new List<string>();
                int searchFrom = 0;

                while (searchFrom < html.Length)
                {
                    var match = opener.Match(html, searchFrom);
                    if (!match.Success) break;

                    int bodyStart = match.Index + match.Length;
                    int depth = 1;
                    int i = bodyStart;
                    int bodyEnd = -1;

                    while (i < html.Length && depth > 0)
                    {
                        int nextOpen = html.IndexOf("<div", i, StringComparison.OrdinalIgnoreCase);
                        int nextClose = html.IndexOf("</div>", i, StringComparison.OrdinalIgnoreCase);

                        if (nextClose < 0)
                        {
                            break;
                        }

                        bool openFirst = nextOpen >= 0 && nextOpen < nextClose;
                        if (openFirst)
                        {
                            int after = nextOpen + 4;
                            if (after < html.Length && (html[after] == '>' || char.IsWhiteSpace(html[after])))
                            {
                                // A nested div opens; only count it when it is a real tag.
                                depth++;
                            }
                            i = nextOpen + 4;
                        }
                        else
                        {
                            depth--;
                            if (depth == 0)
                            {
                                bodyEnd = nextClose;
                                break;
                            }
                            i = nextClose + 6;
                        }
                    }

                    if (bodyEnd < 0) break;
                    result.Add(html.Substring(bodyStart, bodyEnd - bodyStart));
                    searchFrom = bodyEnd + 6;
                }

                return result;
            }

            /// <summary>
            /// Cleans common web scraper artifacts found on Genius:
            /// "You might also like" insertions, trailing "Embed", and
            /// non-breaking or zero-width unicode spaces.
            /// </summary>
            public static string StripArtifacts(string raw)
            {
                string value = raw
                    .Replace('\u00A0', ' ')
                    .Replace('\u200B', ' ')
                    .Replace('\uFEFF', ' ');
                value = YouMightAlsoLike.Replace(value, string.Empty).Trim();
                value = TrailingEmbed.Replace(value, string.Empty).Trim();
                return value;
            }

            /// <summary>Converts clean multi-line lyrics text into structured lines, gaps kept as blank lines.</summary>
            public static List<LyricLine> TextToLyricLines(string text)
            {
                var result = new List<LyricLine>();
                bool lastWasGap = false;

                using var reader = new StringReader(text);
                string? rawLine;
                while ((rawLine = reader.ReadLine()) != null)
                {
                    string line = TrailingEmbed.Replace(rawLine.Trim(), string.Empty).Trim();
                    if (line.Length == 0)
                    {
                        if (!lastWasGap && result.Count > 0)
                        {
                            result.Add(new LyricLine { Text = string.Empty, IsGap = true });
                            lastWasGap = true;
                        }
                    }
                    else
                    {
                        result.Add(new LyricLine { Text = line });
                        lastWasGap = false;
                    }
                }

                while (result.Count > 0 && result[0].IsGap) result.RemoveAt(0);
                while (result.Count > 0 && result[^1].IsGap) result.RemoveAt(result.Count - 1);
                return result;
            }

            /// <summary>Whether a line is one of Genius's section markers — `[Chorus]`, `[Verse 2: Artist]`.</summary>
            public static bool IsSectionHeader(string text)
            {
                string trimmed = (text ?? string.Empty).Trim();
                return trimmed.StartsWith("[") && trimmed.EndsWith("]") && trimmed.Length >= 3 && trimmed.Length <= 60;
            }

            private static string CleanQuery(string text)
            {
                string cleaned = DecorativeChars.Replace(text, " ");
                cleaned = Noise.Replace(cleaned, " ");
                cleaned = ProducerTags.Replace(cleaned, " ");
                int pipeIndex = cleaned.IndexOf(" | ", StringComparison.Ordinal);
                if (pipeIndex >= 0) cleaned = cleaned.Substring(0, pipeIndex);
                cleaned = MultipleSpaces.Replace(cleaned, " ").Trim();
                return cleaned.Length > 0 ? cleaned : text.Trim();
            }

            private sealed class SearchAttempt
            {
                public string Query { get; }
                public string Title { get; }
                public string Artist { get; }

                public SearchAttempt(string query, string title, string artist)
                {
                    Query = query;
                    Title = title;
                    Artist = artist;
                }
            }
        }

        // -------------------------------------------------------------
        // Shared plain-text helper
        // -------------------------------------------------------------
        internal static List<LyricLine> PlainLines(string text)
        {
            var lines = new List<LyricLine>();
            using var reader = new StringReader(text);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                lines.Add(new LyricLine { Text = trimmed });
            }
            return lines;
        }

        // -------------------------------------------------------------
        // 6. KuGou: Asian, Bollywood, Hindi & Obscure tracks
        // -------------------------------------------------------------
        public static class KuGou
        {
            public static async Task<List<LyricLine>?> GetLyricsAsync(string title, string artist, long durationMs, string? album, HttpClient http, CancellationToken ct)
            {
                try
                {
                    string keyword = $"{title} {artist}".Trim();
                    int targetSec = (int)(durationMs / 1000);

                    // Step 1: Search song to find candidate audio hashes
                    string songUrl = $"https://mobileservice.kugou.com/api/v3/search/song?version=9108&plat=0&pagesize=8&showtype=0&keyword={Uri.EscapeDataString(keyword)}";
                    string? songBody = await http.GetStringOrNullAsync(songUrl, ct);
                    if (string.IsNullOrWhiteSpace(songBody)) return null;

                    string? bestHash = null;
                    using (var doc = JsonDocument.Parse(songBody))
                    {
                        if (doc.RootElement.TryGetProperty("data", out var data) &&
                            data.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in info.EnumerateArray())
                            {
                                int duration = item.TryGetProperty("duration", out var durProp) ? durProp.GetInt32() : 0;
                                string? hash = item.TryGetProperty("hash", out var hProp) ? hProp.GetString() : null;
                                if (!string.IsNullOrEmpty(hash))
                                {
                                    if (targetSec <= 0 || Math.Abs(duration - targetSec) <= 8)
                                    {
                                        bestHash = hash;
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    // Step 2: Search lyrics candidates
                    string lyricsSearchUrl = bestHash != null
                        ? $"https://lyrics.kugou.com/search?ver=1&man=yes&client=pc&hash={bestHash}"
                        : $"https://lyrics.kugou.com/search?ver=1&man=yes&client=pc&keyword={Uri.EscapeDataString(keyword)}&duration={targetSec}";

                    string? lyricsSearchBody = await http.GetStringOrNullAsync(lyricsSearchUrl, ct);
                    if (string.IsNullOrWhiteSpace(lyricsSearchBody)) return null;

                    string? candidateId = null;
                    string? accessKey = null;

                    using (var doc = JsonDocument.Parse(lyricsSearchBody))
                    {
                        if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var cand in candidates.EnumerateArray())
                            {
                                candidateId = cand.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                                accessKey = cand.TryGetProperty("accesskey", out var akProp) ? akProp.GetString() : null;
                                if (!string.IsNullOrEmpty(candidateId) && !string.IsNullOrEmpty(accessKey)) break;
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(candidateId) || string.IsNullOrEmpty(accessKey)) return null;

                    // Step 3: Download lyrics file
                    string downloadUrl = $"https://lyrics.kugou.com/download?ver=1&client=pc&id={candidateId}&accesskey={accessKey}&fmt=lrc&charset=utf8";
                    string? downloadBody = await http.GetStringOrNullAsync(downloadUrl, ct);
                    if (string.IsNullOrWhiteSpace(downloadBody)) return null;

                    using (var doc = JsonDocument.Parse(downloadBody))
                    {
                        if (doc.RootElement.TryGetProperty("content", out var contentProp))
                        {
                            string? base64 = contentProp.GetString();
                            if (!string.IsNullOrEmpty(base64))
                            {
                                byte[] bytes = Convert.FromBase64String(base64);
                                string lrc = Encoding.UTF8.GetString(bytes);
                                return LrcLibParser.ParseLrc(lrc);
                            }
                        }
                    }
                }
                catch { }

                return null;
            }
        }

        // -------------------------------------------------------------
        // 7. LRCLIB: Line-synced and word-synced LRC (https://lrclib.net)
        // -------------------------------------------------------------
        public static class LrcLib
        {
            private const string BaseUrl = "https://lrclib.net/api";

            public static async Task<LyricProviderResult?> GetLyricsAsync(string title, string artist, long durationMs, HttpClient http, CancellationToken ct)
            {
                int seconds = (int)(durationMs / 1000);

                // Exact match
                string exactUrl = $"{BaseUrl}/get?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
                if (seconds > 0) exactUrl += $"&duration={seconds}";

                string? exactBody = await http.GetStringOrNullAsync(exactUrl, ct);
                var result = ParseLrclibJson(exactBody);
                if (result != null) return result;

                // Fuzzy search
                string searchUrl = $"{BaseUrl}/search?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
                string? searchBody = await http.GetStringOrNullAsync(searchUrl, ct);
                return ParseLrclibSearchJson(searchBody, seconds);
            }

            private static LyricProviderResult? ParseLrclibJson(string? json)
            {
                if (string.IsNullOrWhiteSpace(json)) return null;
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    string? synced = doc.RootElement.TryGetProperty("syncedLyrics", out var sp) ? sp.GetString() : null;
                    string? plain = doc.RootElement.TryGetProperty("plainLyrics", out var pp) ? pp.GetString() : null;

                    if (!string.IsNullOrWhiteSpace(synced))
                    {
                        var lines = EnhancedLrc.LooksLike(synced)
                            ? EnhancedLrc.Parse(synced)
                            : LrcLibParser.ParseLrc(synced);

                        if (lines.Count > 0)
                        {
                            return new LyricProviderResult
                            {
                                Source = LyricSourceId.LrcLib,
                                SourceName = "LRCLIB",
                                Lines = lines,
                                PlainLyrics = plain
                            };
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(plain))
                    {
                        return new LyricProviderResult
                        {
                            Source = LyricSourceId.LrcLib,
                            SourceName = "LRCLIB",
                            PlainLyrics = plain
                        };
                    }
                }
                catch { }
                return null;
            }

            private static LyricProviderResult? ParseLrclibSearchJson(string? json, int targetSeconds)
            {
                if (string.IsNullOrWhiteSpace(json)) return null;
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

                    JsonElement? bestElement = null;
                    double bestDiff = double.MaxValue;

                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (item.TryGetProperty("syncedLyrics", out var sp) && !string.IsNullOrWhiteSpace(sp.GetString()))
                        {
                            double dur = item.TryGetProperty("duration", out var dp) && dp.TryGetDouble(out double d) ? d : 0.0;
                            double diff = targetSeconds > 0 ? Math.Abs(dur - targetSeconds) : 0;
                            if (diff < bestDiff)
                            {
                                bestDiff = diff;
                                bestElement = item;
                            }
                        }
                    }

                    if (bestElement.HasValue)
                    {
                        return ParseLrclibJson(bestElement.Value.GetRawText());
                    }
                }
                catch { }
                return null;
            }
        }

        // -------------------------------------------------------------
        // HTTP Helper Extensions
        // -------------------------------------------------------------
        internal const string DefaultUserAgent = UserAgent;

        internal static Task<string?> GetStringOrNullAsync(this HttpClient client, string url, CancellationToken ct) =>
            client.GetStringOrNullAsync(url, ct, UserAgent, "application/json, text/plain, */*");

        internal static async Task<string?> GetStringOrNullAsync(
            this HttpClient client,
            string url,
            CancellationToken ct,
            string userAgent,
            string accept,
            string? bearer = null,
            string? origin = null,
            string? referer = null,
            string? cookie = null)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                request.Headers.TryAddWithoutValidation("Accept", accept);
                if (!string.IsNullOrEmpty(bearer)) request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
                if (!string.IsNullOrEmpty(origin)) request.Headers.TryAddWithoutValidation("Origin", origin);
                if (!string.IsNullOrEmpty(referer)) request.Headers.TryAddWithoutValidation("Referer", referer);
                if (!string.IsNullOrEmpty(cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync();
                }
            }
            catch { }
            return null;
        }
    }
}

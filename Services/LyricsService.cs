using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LmenV2.Models;

namespace LmenV2.Services
{
    /// <summary>
    /// Master lyrics engine adopting BitChord's multi-source concurrent racing architecture
    /// with recording identification (ISRC), shared document caching, word-to-word syllable
    /// syncing, background vocals, and robust fallbacks.
    /// </summary>
    public sealed class LyricsService
    {
        private static readonly Lazy<LyricsService> _instance =
            new Lazy<LyricsService>(() => new LyricsService());
        public static LyricsService Instance => _instance.Value;

        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(7)
        };

        private readonly ConcurrentDictionary<string, TrackLyrics> _memoryCache =
            new ConcurrentDictionary<string, TrackLyrics>(StringComparer.OrdinalIgnoreCase);

        // Recording ISRC cache (LRU, max 100 items)
        private readonly ConcurrentDictionary<string, string> _isrcCache =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private readonly string _cacheDirectory;

        public LyricsService()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _cacheDirectory = Path.Combine(appData, "LmenV2", "Lyrics");
            try
            {
                if (!Directory.Exists(_cacheDirectory))
                {
                    Directory.CreateDirectory(_cacheDirectory);
                }
            }
            catch { }

            if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
            {
                _httpClient.DefaultRequestHeaders.Add("User-Agent", "BitChord (https://github.com/bitchord)");
            }
        }

        /// <summary>
        /// Retrieves synced/word-timed lyrics for the given track using BitChord's concurrent racing pipeline.
        /// </summary>
        public async Task<TrackLyrics?> GetLyricsAsync(
            string rawTitle,
            string rawArtist,
            string rawAlbum,
            TimeSpan duration,
            CancellationToken ct = default,
            string? isrc = null)
        {
            if (string.IsNullOrWhiteSpace(rawTitle) || rawTitle == "Ready to Play" || rawTitle == "Unknown Track")
            {
                return null;
            }

            // 1. BitChord Title & Artist cleaning: removes packaging while preserving recording versions
            string cleanTitle = LyricQueryHelper.CleanTitleForSearch(rawTitle);
            string cleanArtist = LyricQueryHelper.CleanArtistForSearch(rawArtist);
            string songKey = $"{cleanArtist.ToLowerInvariant()} - {cleanTitle.ToLowerInvariant()}";

            // 2. Check in-memory cache
            if (_memoryCache.TryGetValue(songKey, out var cachedLyrics))
            {
                return cachedLyrics;
            }

            // 3. Check local disk cache
            var diskLyrics = LoadFromDiskCache(songKey);
            if (diskLyrics != null)
            {
                _memoryCache[songKey] = diskLyrics;
                return diskLyrics;
            }

            long durationMs = (long)duration.TotalMilliseconds;

            // 4. BitChord Recording Identification Pre-Flight:
            // Checks if recording ISRC is known in cache. If not, queries BiniLyrics with 2500ms timeout.
            string? recordingIsrc = isrc;
            LyricsProviders.BiniLyrics.Hit? biniHit = null;

            if (string.IsNullOrWhiteSpace(recordingIsrc))
            {
                _isrcCache.TryGetValue(songKey, out recordingIsrc);
            }

            if (string.IsNullOrWhiteSpace(recordingIsrc))
            {
                try
                {
                    using var idCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2500));
                    using var linkedIdCts = CancellationTokenSource.CreateLinkedTokenSource(ct, idCts.Token);
                    biniHit = await LyricsProviders.BiniLyrics.IdentifyAsync(cleanTitle, cleanArtist, durationMs, rawAlbum, _httpClient, linkedIdCts.Token);
                    if (biniHit != null && !string.IsNullOrWhiteSpace(biniHit.Isrc))
                    {
                        recordingIsrc = biniHit.Isrc.Trim();
                        _isrcCache[songKey] = recordingIsrc;
                    }
                }
                catch { }
            }

            // 5. Shared Documents Downloader:
            // Deduplicates in-flight fetches so LrcRed and BiniLyrics sharing a TTML URL only download once.
            var sharedDocs = new ConcurrentDictionary<string, Task<string?>>(StringComparer.OrdinalIgnoreCase);
            Task<string?> SharedFetchDocumentAsync(string url)
            {
                return sharedDocs.GetOrPut(url, async u =>
                {
                    try
                    {
                        return await _httpClient.GetStringOrNullAsync(u, ct);
                    }
                    catch { return null; }
                });
            }

            // 6. Concurrent Multi-Source Racing (BitChord priority order)
            LyricsProviders.PaxSenix.SetApiKey(SettingsService.Instance.PaxSenixApiKey);

            using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var cancelToken = raceCts.Token;

            // Define provider tasks
            var providerTasks = new List<(LyricSourceId id, string name, Task<LyricProviderResult?> task)>();

            // LrcRed (Apple Music TTML via ISRC or search)
            providerTasks.Add((
                LyricSourceId.LrcRed,
                "Apple Music (lrc.red)",
                Task.Run(async () =>
                {
                    var lines = await LyricsProviders.LrcRed.GetLyricsAsync(cleanTitle, cleanArtist, durationMs, recordingIsrc, SharedFetchDocumentAsync, _httpClient, cancelToken);
                    return lines != null && lines.Count > 0 ? new LyricProviderResult { Source = LyricSourceId.LrcRed, SourceName = "Apple Music (lrc.red)", Lines = lines } : null;
                }, cancelToken)
            ));

            // BiniLyrics (Apple Music TTML)
            providerTasks.Add((
                LyricSourceId.BiniLyrics,
                "Apple Music (Bini)",
                Task.Run(async () =>
                {
                    List<LyricLine>? lines = null;
                    if (biniHit != null)
                    {
                        lines = await LyricsProviders.BiniLyrics.GetLyricsForHitAsync(biniHit, SharedFetchDocumentAsync, cancelToken);
                    }
                    if (lines == null || lines.Count == 0)
                    {
                        var hit = await LyricsProviders.BiniLyrics.IdentifyAsync(cleanTitle, cleanArtist, durationMs, rawAlbum, _httpClient, cancelToken);
                        if (hit != null)
                        {
                            if (!string.IsNullOrWhiteSpace(hit.Isrc)) _isrcCache[songKey] = hit.Isrc;
                            lines = await LyricsProviders.BiniLyrics.GetLyricsForHitAsync(hit, SharedFetchDocumentAsync, cancelToken);
                        }
                    }
                    return lines != null && lines.Count > 0 ? new LyricProviderResult { Source = LyricSourceId.BiniLyrics, SourceName = "Apple Music (Bini)", Lines = lines } : null;
                }, cancelToken)
            ));

            // BetterLyrics (Apple Music TTML)
            providerTasks.Add((
                LyricSourceId.BetterLyrics,
                "Apple Music (BetterLyrics)",
                Task.Run(async () =>
                {
                    var lines = await LyricsProviders.BetterLyrics.GetLyricsAsync(cleanTitle, cleanArtist, durationMs, rawAlbum, _httpClient, cancelToken);
                    return lines != null && lines.Count > 0 ? new LyricProviderResult { Source = LyricSourceId.BetterLyrics, SourceName = "Apple Music (BetterLyrics)", Lines = lines } : null;
                }, cancelToken)
            ));

            // BetterLyrics Portato (QQ Music QRC karaoke)
            providerTasks.Add((
                LyricSourceId.BetterLyricsPortato,
                "QQ Music (Portato)",
                Task.Run(async () =>
                {
                    var lines = await LyricsProviders.BetterLyrics.GetPortatoLyricsAsync(cleanTitle, cleanArtist, durationMs, rawAlbum, _httpClient, cancelToken);
                    return lines != null && lines.Count > 0 ? new LyricProviderResult { Source = LyricSourceId.BetterLyricsPortato, SourceName = "QQ Music (Portato)", Lines = lines } : null;
                }, cancelToken)
            ));

            // PaxSenix (Apple Music TTML, keyless public route)
            providerTasks.Add((
                LyricSourceId.PaxSenix,
                "Apple Music (PaxSenix)",
                Task.Run(async () =>
                {
                    var lines = await LyricsProviders.PaxSenix.GetLyricsAsync(cleanTitle, cleanArtist, durationMs, rawAlbum, _httpClient, cancelToken);
                    return lines != null && lines.Count > 0 ? new LyricProviderResult { Source = LyricSourceId.PaxSenix, SourceName = "Apple Music (PaxSenix)", Lines = lines } : null;
                }, cancelToken)
            ));

            // PaxSenix: Spotify (API key required; falls back to PaxSenix's general search)
            providerTasks.Add((
                LyricSourceId.PaxSenixSpotify,
                "Spotify (PaxSenix)",
                Task.Run(async () =>
                {
                    var lines = await LyricsProviders.PaxSenix.GetSpotifyLyricsAsync(cleanTitle, cleanArtist, durationMs, _httpClient, cancelToken);
                    return lines != null && lines.Count > 0 ? new LyricProviderResult { Source = LyricSourceId.PaxSenixSpotify, SourceName = "Spotify (PaxSenix)", Lines = lines } : null;
                }, cancelToken)
            ));

            // PaxSenix: Musixmatch (API key required; falls back to PaxSenix's general search)
            providerTasks.Add((
                LyricSourceId.PaxSenixMusixmatch,
                "Musixmatch (PaxSenix)",
                Task.Run(async () =>
                {
                    var lines = await LyricsProviders.PaxSenix.GetMusixmatchLyricsAsync(cleanTitle, cleanArtist, durationMs, _httpClient, cancelToken);
                    return lines != null && lines.Count > 0 ? new LyricProviderResult { Source = LyricSourceId.PaxSenixMusixmatch, SourceName = "Musixmatch (PaxSenix)", Lines = lines } : null;
                }, cancelToken)
            ));

            // LyricsPlus (Multi-mirror YouLy+ aggregator)
            providerTasks.Add((
                LyricSourceId.LyricsPlus,
                "LyricsPlus",
                Task.Run(async () =>
                {
                    var lines = await LyricsProviders.LyricsPlus.GetLyricsAsync(cleanTitle, cleanArtist, durationMs, rawAlbum, recordingIsrc, _httpClient, cancelToken);
                    return lines != null && lines.Count > 0 ? new LyricProviderResult { Source = LyricSourceId.LyricsPlus, SourceName = "LyricsPlus", Lines = lines } : null;
                }, cancelToken)
            ));

            // Unison (community-submitted; plain, LRC or TTML)
            providerTasks.Add((
                LyricSourceId.Unison,
                "Unison",
                Task.Run(async () =>
                {
                    return await LyricsProviders.Unison.GetLyricsAsync(cleanTitle, cleanArtist, durationMs, rawAlbum, _httpClient, cancelToken);
                }, cancelToken)
            ));

            // KuGou (Asian, Bollywood, Hindi & obscure catalogue)
            providerTasks.Add((
                LyricSourceId.KuGou,
                "KuGou",
                Task.Run(async () =>
                {
                    var lines = await LyricsProviders.KuGou.GetLyricsAsync(cleanTitle, cleanArtist, durationMs, rawAlbum, _httpClient, cancelToken);
                    return lines != null && lines.Count > 0 ? new LyricProviderResult { Source = LyricSourceId.KuGou, SourceName = "KuGou", Lines = lines } : null;
                }, cancelToken)
            ));

            // LRCLIB (Enhanced & Line LRC fallback)
            providerTasks.Add((
                LyricSourceId.LrcLib,
                "LRCLIB",
                Task.Run(async () =>
                {
                    return await LyricsProviders.LrcLib.GetLyricsAsync(cleanTitle, cleanArtist, durationMs, _httpClient, cancelToken);
                }, cancelToken)
            ));

            // Musixmatch (richer word timing when available, line-synced fallback)
            providerTasks.Add((
                LyricSourceId.Musixmatch,
                "Musixmatch",
                Task.Run(async () =>
                {
                    var lines = await LyricsProviders.Musixmatch.GetLyricsAsync(cleanTitle, cleanArtist, durationMs, _httpClient, cancelToken);
                    return lines != null && lines.Count > 0 ? new LyricProviderResult { Source = LyricSourceId.Musixmatch, SourceName = "Musixmatch", Lines = lines } : null;
                }, cancelToken)
            ));

            // 7. True Concurrent Racing Resolution:
            // Process tasks as they finish via Task.WhenAny so fast providers (e.g. LRCLIB 200ms)
            // aren't blocked waiting for slower providers.
            LyricProviderResult? winningResult = null;
            LyricProviderResult? fallbackLineSynced = null;
            LyricProviderResult? fallbackPlain = null;

            try
            {
                var pendingTasks = providerTasks.Select(p => p.task).ToList();
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                using var linkedTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancelToken, timeoutCts.Token);
                var overallTimeout = Task.Delay(TimeSpan.FromSeconds(6), linkedTimeoutCts.Token);

                while (pendingTasks.Count > 0)
                {
                    var completedTask = await Task.WhenAny(pendingTasks.Concat(new[] { (Task)overallTimeout }));
                    if (completedTask == overallTimeout)
                    {
                        break;
                    }

                    var typedTask = (Task<LyricProviderResult?>)completedTask;
                    pendingTasks.Remove(typedTask);

                    try
                    {
                        var res = await typedTask;
                        if (res == null) continue;
                        if (!res.IsSynced && string.IsNullOrWhiteSpace(res.PlainLyrics)) continue;

                        if (res.IsWordSynced)
                        {
                            winningResult = res;
                            try { raceCts.Cancel(); } catch { }
                            break;
                        }

                        if (res.IsTimed)
                        {
                            fallbackLineSynced ??= res;
                        }
                        else
                        {
                            fallbackPlain ??= res;
                        }
                    }
                    catch { }

                    // If we already have a timed result and only a few tasks remain, finish promptly
                    if (fallbackLineSynced != null && pendingTasks.Count <= 3)
                    {
                        break;
                    }
                }

                if (winningResult == null && fallbackLineSynced == null)
                {
                    try
                    {
                        var genius = await LyricsProviders.Genius.GetLyricsAsync(cleanTitle, cleanArtist, _httpClient, ct);
                        if (genius != null) fallbackPlain ??= genius;
                    }
                    catch { }
                }
            }
            finally
            {
                try { raceCts.Cancel(); } catch { }
            }

            winningResult ??= fallbackLineSynced ?? fallbackPlain;
            if (winningResult == null) return null;

            var trackLyrics = FinalizeResult(songKey, rawTitle, rawArtist, winningResult, recordingIsrc);
            if (trackLyrics == null) return null;

            _memoryCache[songKey] = trackLyrics;
            SaveToDiskCache(songKey, trackLyrics);
            return trackLyrics;
        }

        /// <summary>
        /// Fetches lyrics from one specific source only — the manual provider
        /// picker's fetch path. The result is not written to the track caches so
        /// a hand-picked answer cannot poison the automatic lookup.
        /// </summary>
        public async Task<TrackLyrics?> GetLyricsFromSourceAsync(
            string rawTitle,
            string rawArtist,
            string rawAlbum,
            TimeSpan duration,
            LyricSourceId source,
            CancellationToken ct = default,
            string? isrc = null)
        {
            if (string.IsNullOrWhiteSpace(rawTitle) || rawTitle == "Ready to Play" || rawTitle == "Unknown Track") return null;

            string cleanTitle = LyricQueryHelper.CleanTitleForSearch(rawTitle);
            string cleanArtist = LyricQueryHelper.CleanArtistForSearch(rawArtist);
            string songKey = $"{cleanArtist.ToLowerInvariant()} - {cleanTitle.ToLowerInvariant()}";
            long durationMs = (long)duration.TotalMilliseconds;

            string? recordingIsrc = isrc;
            if (string.IsNullOrWhiteSpace(recordingIsrc)) _isrcCache.TryGetValue(songKey, out recordingIsrc);

            bool needsIsrc = source == LyricSourceId.LrcRed || source == LyricSourceId.BiniLyrics || source == LyricSourceId.LyricsPlus;
            if (needsIsrc && string.IsNullOrWhiteSpace(recordingIsrc))
            {
                try
                {
                    using var idCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2500));
                    using var linkedIdCts = CancellationTokenSource.CreateLinkedTokenSource(ct, idCts.Token);
                    var hit = await LyricsProviders.BiniLyrics.IdentifyAsync(cleanTitle, cleanArtist, durationMs, rawAlbum, _httpClient, linkedIdCts.Token);
                    if (hit != null && !string.IsNullOrWhiteSpace(hit.Isrc))
                    {
                        recordingIsrc = hit.Isrc.Trim();
                        _isrcCache[songKey] = recordingIsrc;
                    }
                }
                catch { }
            }

            LyricsProviders.PaxSenix.SetApiKey(SettingsService.Instance.PaxSenixApiKey);

            var sharedDocs = new ConcurrentDictionary<string, Task<string?>>(StringComparer.OrdinalIgnoreCase);
            Task<string?> SharedFetchDocumentAsync(string url)
            {
                return sharedDocs.GetOrPut(url, async u =>
                {
                    try { return await _httpClient.GetStringOrNullAsync(u, ct); }
                    catch { return null; }
                });
            }

            LyricProviderResult? result = null;
            try
            {
                switch (source)
                {
                    case LyricSourceId.LrcRed:
                    {
                        var lines = await LyricsProviders.LrcRed.GetLyricsAsync(cleanTitle, cleanArtist, durationMs, recordingIsrc, SharedFetchDocumentAsync, _httpClient, ct);
                        if (lines != null && lines.Count > 0)
                            result = new LyricProviderResult { Source = source, SourceName = "Apple Music (lrc.red)", Lines = lines };
                        break;
                    }
                    case LyricSourceId.BiniLyrics:
                    {
                        var hit = await LyricsProviders.BiniLyrics.IdentifyAsync(cleanTitle, cleanArtist, durationMs, rawAlbum, _httpClient, ct);
                        if (hit != null)
                        {
                            if (!string.IsNullOrWhiteSpace(hit.Isrc)) { recordingIsrc = hit.Isrc.Trim(); _isrcCache[songKey] = recordingIsrc; }
                            var lines = await LyricsProviders.BiniLyrics.GetLyricsForHitAsync(hit, SharedFetchDocumentAsync, ct);
                            if (lines != null && lines.Count > 0)
                                result = new LyricProviderResult { Source = source, SourceName = "Apple Music (Bini)", Lines = lines };
                        }
                        break;
                    }
                    case LyricSourceId.BetterLyrics:
                    {
                        var lines = await LyricsProviders.BetterLyrics.GetLyricsAsync(cleanTitle, cleanArtist, durationMs, rawAlbum, _httpClient, ct);
                        if (lines != null && lines.Count > 0)
                            result = new LyricProviderResult { Source = source, SourceName = "Apple Music (BetterLyrics)", Lines = lines };
                        break;
                    }
                    case LyricSourceId.BetterLyricsPortato:
                    {
                        var lines = await LyricsProviders.BetterLyrics.GetPortatoLyricsAsync(cleanTitle, cleanArtist, durationMs, rawAlbum, _httpClient, ct);
                        if (lines != null && lines.Count > 0)
                            result = new LyricProviderResult { Source = source, SourceName = "QQ Music (Portato)", Lines = lines };
                        break;
                    }
                    case LyricSourceId.PaxSenix:
                    {
                        var lines = await LyricsProviders.PaxSenix.GetLyricsAsync(cleanTitle, cleanArtist, durationMs, rawAlbum, _httpClient, ct);
                        if (lines != null && lines.Count > 0)
                            result = new LyricProviderResult { Source = source, SourceName = "Apple Music (PaxSenix)", Lines = lines };
                        break;
                    }
                    case LyricSourceId.PaxSenixSpotify:
                    {
                        var lines = await LyricsProviders.PaxSenix.GetSpotifyLyricsAsync(cleanTitle, cleanArtist, durationMs, _httpClient, ct);
                        if (lines != null && lines.Count > 0)
                            result = new LyricProviderResult { Source = source, SourceName = "Spotify (PaxSenix)", Lines = lines };
                        break;
                    }
                    case LyricSourceId.PaxSenixMusixmatch:
                    {
                        var lines = await LyricsProviders.PaxSenix.GetMusixmatchLyricsAsync(cleanTitle, cleanArtist, durationMs, _httpClient, ct);
                        if (lines != null && lines.Count > 0)
                            result = new LyricProviderResult { Source = source, SourceName = "Musixmatch (PaxSenix)", Lines = lines };
                        break;
                    }
                    case LyricSourceId.LyricsPlus:
                    {
                        var lines = await LyricsProviders.LyricsPlus.GetLyricsAsync(cleanTitle, cleanArtist, durationMs, rawAlbum, recordingIsrc, _httpClient, ct);
                        if (lines != null && lines.Count > 0)
                            result = new LyricProviderResult { Source = source, SourceName = "LyricsPlus", Lines = lines };
                        break;
                    }
                    case LyricSourceId.Unison:
                        result = await LyricsProviders.Unison.GetLyricsAsync(cleanTitle, cleanArtist, durationMs, rawAlbum, _httpClient, ct);
                        break;
                    case LyricSourceId.KuGou:
                    {
                        var lines = await LyricsProviders.KuGou.GetLyricsAsync(cleanTitle, cleanArtist, durationMs, rawAlbum, _httpClient, ct);
                        if (lines != null && lines.Count > 0)
                            result = new LyricProviderResult { Source = source, SourceName = "KuGou", Lines = lines };
                        break;
                    }
                    case LyricSourceId.LrcLib:
                        result = await LyricsProviders.LrcLib.GetLyricsAsync(cleanTitle, cleanArtist, durationMs, _httpClient, ct);
                        break;
                    case LyricSourceId.Musixmatch:
                    {
                        var lines = await LyricsProviders.Musixmatch.GetLyricsAsync(cleanTitle, cleanArtist, durationMs, _httpClient, ct);
                        if (lines != null && lines.Count > 0)
                            result = new LyricProviderResult { Source = source, SourceName = "Musixmatch", Lines = lines };
                        break;
                    }
                    case LyricSourceId.Genius:
                        result = await LyricsProviders.Genius.GetLyricsAsync(cleanTitle, cleanArtist, _httpClient, ct);
                        break;
                }
            }
            catch
            {
                return null;
            }

            if (result == null) return null;
            return FinalizeResult(songKey, rawTitle, rawArtist, result, recordingIsrc);
        }

        /// <summary>
        /// Shared last pass for every lookup: the answering vocal is split off the
        /// lead for timed lines, plain answers keep their text, and the result is
        /// packaged with the source that provided it.
        /// </summary>
        private static TrackLyrics? FinalizeResult(string songKey, string rawTitle, string rawArtist, LyricProviderResult winningResult, string? recordingIsrc)
        {
            if (!winningResult.IsSynced && string.IsNullOrWhiteSpace(winningResult.PlainLyrics)) return null;

            bool isPlainAnswer = winningResult.IsPlain || !winningResult.IsTimed;
            var processedLines = winningResult.Lines != null && winningResult.Lines.Count > 0
                ? (isPlainAnswer ? winningResult.Lines : BackgroundVocals.WithBackgroundVocals(winningResult.Lines))
                : new List<LyricLine>();

            bool hasWordSync = !isPlainAnswer && processedLines.Any(l => l.HasWordSync);
            bool isSynced = !isPlainAnswer && processedLines.Count > 0;

            string plainLyrics = winningResult.PlainLyrics
                ?? (isPlainAnswer ? string.Join("\n", processedLines.Select(l => l.Text)) : string.Empty);

            return new TrackLyrics
            {
                SongKey = songKey,
                TrackName = rawTitle,
                ArtistName = rawArtist,
                Provider = winningResult.SourceName,
                Isrc = recordingIsrc,
                IsSynced = isSynced,
                HasWordSync = hasWordSync,
                Lines = processedLines,
                PlainLyrics = plainLyrics
            };
        }

        // -------------------------------------------------------------
        // Disk Cache Storage
        // -------------------------------------------------------------
        private string GetCacheFilePath(string songKey)
        {
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(songKey));
            string hex = Convert.ToHexString(hash).ToLowerInvariant();
            return Path.Combine(_cacheDirectory, $"{hex}.json");
        }

        private TrackLyrics? LoadFromDiskCache(string songKey)
        {
            try
            {
                string path = GetCacheFilePath(songKey);
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    return JsonSerializer.Deserialize<TrackLyrics>(json);
                }
            }
            catch { }
            return null;
        }

        private void SaveToDiskCache(string songKey, TrackLyrics lyrics)
        {
            try
            {
                string path = GetCacheFilePath(songKey);
                string json = JsonSerializer.Serialize(lyrics, new JsonSerializerOptions { WriteIndented = false });
                File.WriteAllText(path, json);
            }
            catch { }
        }

        // Backward-compatible passthroughs
        public static List<LyricLine> ParseLrcString(string lrcContent) => LrcLibParser.ParseLrc(lrcContent);
        public static TrackLyrics? ParseTtmlLyrics(string ttml, string songKey, string title, string artist)
        {
            var lines = TtmlLyrics.Parse(ttml);
            if (lines.Count == 0) return null;
            return new TrackLyrics
            {
                SongKey = songKey,
                TrackName = title,
                ArtistName = artist,
                Provider = "Apple Music (TTML)",
                IsSynced = true,
                HasWordSync = lines.Any(l => l.HasWordSync),
                Lines = lines
            };
        }
    }

    internal static class ConcurrentDictExtensions
    {
        public static TValue GetOrPut<TKey, TValue>(this ConcurrentDictionary<TKey, TValue> dict, TKey key, Func<TKey, TValue> valueFactory)
            where TKey : notnull
        {
            return dict.GetOrAdd(key, valueFactory);
        }
    }
}

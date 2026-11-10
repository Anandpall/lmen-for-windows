using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LmenV2.Services
{
    public class MotionArtworkResult
    {
        public string Url { get; set; } = string.Empty;
        public string? FallbackUrl { get; set; }
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public string Source { get; set; } = "Unknown";
        public string? LocalFilePath { get; set; }

        public bool Matches(string wantTitle, string wantArtist, string? wantAlbum = null)
        {
            string cleanWantTitle = MotionArtworkService.NormalizeForCanvasMatch(wantTitle);
            string cleanWantArtist = MotionArtworkService.NormalizeForCanvasMatch(wantArtist);
            string cleanWantAlbum = !string.IsNullOrWhiteSpace(wantAlbum) ? MotionArtworkService.NormalizeForCanvasMatch(wantAlbum) : string.Empty;

            if (!string.IsNullOrWhiteSpace(cleanWantTitle))
            {
                string normTitle = !string.IsNullOrWhiteSpace(Title) ? MotionArtworkService.NormalizeForCanvasMatch(Title) : string.Empty;
                string normAlbum = !string.IsNullOrWhiteSpace(Album) ? MotionArtworkService.NormalizeForCanvasMatch(Album) : string.Empty;

                bool titleMatches = !string.IsNullOrWhiteSpace(normTitle) &&
                    (normTitle == cleanWantTitle || normTitle.Contains(cleanWantTitle) || cleanWantTitle.Contains(normTitle));

                bool albumMatches = (!string.IsNullOrWhiteSpace(cleanWantAlbum) && !string.IsNullOrWhiteSpace(normAlbum) &&
                                     (normAlbum == cleanWantAlbum || normAlbum.Contains(cleanWantAlbum) || cleanWantAlbum.Contains(normAlbum))) ||
                                    (!string.IsNullOrWhiteSpace(cleanWantAlbum) && !string.IsNullOrWhiteSpace(normTitle) &&
                                     (normTitle == cleanWantAlbum || normTitle.Contains(cleanWantAlbum) || cleanWantAlbum.Contains(normTitle))) ||
                                    (!string.IsNullOrWhiteSpace(normAlbum) &&
                                     (normAlbum == cleanWantTitle || normAlbum.Contains(cleanWantTitle) || cleanWantTitle.Contains(normAlbum)));

                if (!titleMatches && !albumMatches)
                    return false;
            }

            var wantedArtists = MotionArtworkService.SplitCanvasArtists(wantArtist);
            var ourArtists = MotionArtworkService.SplitCanvasArtists(Artist ?? string.Empty);
            if (wantedArtists.Count > 0 && ourArtists.Count > 0)
            {
                bool artistMatch = wantedArtists.Any(w => ourArtists.Any(c => c == w || c.Contains(w) || w.Contains(c))) ||
                                   ourArtists.Any(c => wantedArtists.Any(w => w == c || w.Contains(c) || c.Contains(w)));
                if (!artistMatch)
                    return false;
            }

            return true;
        }
    }

    /// <summary>
    /// MotionArtworkService: Discovers and caches high-definition looping motion artwork (animated album covers / canvas)
    /// across Apple Music (motion square videos / editorialVideo resolved to direct MP4), Tidal (1280x1280 60fps video covers),
    /// the community canvas library, and Spotify Canvas. Ported and enhanced from BitChord's motion canvas architecture.
    /// </summary>
    public sealed class MotionArtworkService
    {
        private static readonly Lazy<MotionArtworkService> _instance =
            new Lazy<MotionArtworkService>(() => new MotionArtworkService());
        public static MotionArtworkService Instance => _instance.Value;

        public event Action<string, string>? VideoCached; // (string videoUrl, string localFilePath)

        private static readonly HttpClient _httpClient = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(10)
        })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

        private readonly ConcurrentDictionary<string, MotionArtworkResult?> _memoryCache =
            new ConcurrentDictionary<string, MotionArtworkResult?>(StringComparer.OrdinalIgnoreCase);

        private readonly string _cacheDirectory;
        private readonly SemaphoreSlim _lookupGate = new SemaphoreSlim(1, 1);

        private MotionArtworkService()
        {
            _cacheDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Lmen",
                "CanvasCache"
            );

            try
            {
                if (!Directory.Exists(_cacheDirectory))
                {
                    Directory.CreateDirectory(_cacheDirectory);
                }
            }
            catch { }
        }

        // =========================================================
        // String Normalization & Artist Matching (Exact Port of BitChord)
        // =========================================================

        private static readonly Regex ArtistSeparatorsRegex = new Regex(
            @"(?:\s*,\s*|\s*&\s*|\s+×\s+|\s+x\s+|\bfeat\.?\b|\bft\.?\b|\bfeaturing\b|\bwith\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string NormalizeForCanvasMatch(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;
            string normalized = input.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (char c in normalized)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category != UnicodeCategory.NonSpacingMark)
                {
                    if (char.IsLetterOrDigit(c))
                        sb.Append(char.ToLowerInvariant(c));
                    else
                        sb.Append(' ');
                }
            }
            return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        }

        public static List<string> SplitCanvasArtists(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
            return ArtistSeparatorsRegex.Split(raw)
                .Select(NormalizeForCanvasMatch)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct()
                .ToList();
        }

        public static bool CanvasMatches(string gotName, List<string> gotArtists, string wantName, string wantArtist)
        {
            string nGot = NormalizeForCanvasMatch(gotName);
            string nWant = NormalizeForCanvasMatch(wantName);
            if (string.IsNullOrWhiteSpace(nGot) || string.IsNullOrWhiteSpace(nWant)) return false;

            if (nGot != nWant && !nGot.Contains(nWant) && !nWant.Contains(nGot))
                return false;

            var wanted = SplitCanvasArtists(wantArtist);
            var credited = gotArtists.Select(NormalizeForCanvasMatch).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (wanted.Count == 0 || credited.Count == 0) return true;
            return wanted.Any(want => credited.Any(c => c == want || c.Contains(want) || want.Contains(c))) ||
                   credited.Any(c => wanted.Any(want => want == c || want.Contains(c) || c.Contains(want)));
        }

        // =========================================================
        // Primary Public API
        // =========================================================

        public async Task<MotionArtworkResult?> GetMotionArtworkAsync(
            string title,
            string artist,
            string albumTitle = "",
            CancellationToken ct = default)
        {
            if (!SettingsService.Instance.EnableMotionArtwork)
                return null;

            if (string.IsNullOrWhiteSpace(title) ||
                title.Equals("Ready to Play", StringComparison.OrdinalIgnoreCase) ||
                title.Equals("Unknown Track", StringComparison.OrdinalIgnoreCase) ||
                title.Equals("No Track Playing", StringComparison.OrdinalIgnoreCase))
                return null;

            string cleanTitle = HighResArtworkService.CleanTitle(title, out string? extractedArtist);
            string cleanArtist = HighResArtworkService.CleanArtist(string.IsNullOrWhiteSpace(artist) ? extractedArtist ?? "" : artist);
            string cleanAlbum = (albumTitle ?? "").Trim();

            if (string.IsNullOrWhiteSpace(cleanTitle) || string.IsNullOrWhiteSpace(cleanArtist))
                return null;

            string cacheKey = $"{cleanArtist}|{cleanTitle}|{cleanAlbum}".ToLowerInvariant();
            if (_memoryCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            await _lookupGate.WaitAsync(ct);
            try
            {
                if (_memoryCache.TryGetValue(cacheKey, out cached))
                    return cached;

                MotionArtworkResult? result = null;
                bool prioritizeSpotify = SettingsService.Instance.PrioritizeSpotifyCanvas;
                string spDc = SettingsService.Instance.SpotifySpDcCookie;
                if (string.IsNullOrWhiteSpace(spDc))
                {
                    try
                    {
                        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\JavaSoft\Prefs\com.music.bitchord.desktop");
                        if (key != null)
                        {
                            var regVal = key.GetValue("spotify_spdc_token") as string;
                            if (!string.IsNullOrWhiteSpace(regVal))
                            {
                                spDc = regVal.Trim();
                            }
                        }
                    }
                    catch { }
                }

                // Optional: Prioritize Spotify Canvas if requested and cookie is available
                if (prioritizeSpotify && !string.IsNullOrWhiteSpace(spDc))
                {
                    try
                    {
                        result = await SpotifyProvider.SearchAsync(cleanTitle, cleanArtist, cleanAlbum, spDc, ct);
                        if (result != null && !result.Matches(cleanTitle, cleanArtist, cleanAlbum))
                            result = null;
                    }
                    catch { }
                }

                // 1. Try Community Curated Canvas Index first (instant in-memory lookup for curated 60fps canvases)
                if (result == null)
                {
                    try
                    {
                        result = await CommunityProvider.SearchAsync(cleanTitle, cleanArtist, cleanAlbum, ct);
                        if (result != null && !result.Matches(cleanTitle, cleanArtist, cleanAlbum))
                            result = null;
                    }
                    catch { }
                }

                // 2. Try Apple Music Motion Catalog (Square 1x1 master motion videos resolved to direct MP4)
                if (result == null)
                {
                    try
                    {
                        result = await AppleMusicProvider.SearchAsync(cleanTitle, cleanArtist, cleanAlbum, ct);
                        if (result != null && !result.Matches(cleanTitle, cleanArtist, cleanAlbum))
                            result = null;
                    }
                    catch { }
                }

                // 3. Try Tidal Video Covers (1280x1280 60fps CDN MP4s)
                if (result == null)
                {
                    try
                    {
                        result = await TidalProvider.SearchAsync(cleanTitle, cleanArtist, cleanAlbum, ct);
                        if (result != null && !result.Matches(cleanTitle, cleanArtist, cleanAlbum))
                            result = null;
                    }
                    catch { }
                }

                // 4. Try Spotify Canvas (as fallback if not prioritized)
                if (result == null && !prioritizeSpotify && !string.IsNullOrWhiteSpace(spDc))
                {
                    try
                    {
                        result = await SpotifyProvider.SearchAsync(cleanTitle, cleanArtist, cleanAlbum, spDc, ct);
                        if (result != null && !result.Matches(cleanTitle, cleanArtist, cleanAlbum))
                            result = null;
                    }
                    catch { }
                }

                if (result != null && !string.IsNullOrWhiteSpace(result.Url))
                {
                    App.Log($"[MotionArtwork] Found motion artwork from {result.Source}: {result.Url}");
                    string? existing = GetExistingLocalCachedVideo(result.Url);
                    if (existing != null)
                    {
                        result.LocalFilePath = existing;
                        App.Log($"[MotionArtwork] Using existing disk cache: {existing}");
                    }
                    else
                    {
                        // Download to local cache so WPF MediaElement plays the file directly from disk
                        // with 100% native hardware acceleration and zero network/streaming failures.
                        try
                        {
                            result.LocalFilePath = await EnsureLocalCachedVideoAsync(result.Url, ct);
                            App.Log($"[MotionArtwork] Downloaded and cached to: {result.LocalFilePath}");
                        }
                        catch (Exception ex)
                        {
                            App.Log($"[MotionArtwork] Cache download error: {ex.Message}");
                        }
                    }
                }

                _memoryCache[cacheKey] = result;
                return result;
            }
            finally
            {
                _lookupGate.Release();
            }
        }

        // =========================================================
        // Disk Cache Helper
        // =========================================================

        public string? GetExistingLocalCachedVideo(string url)
        {
            try
            {
                using var sha256 = SHA256.Create();
                byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(url));
                string hex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                string targetPath = Path.Combine(_cacheDirectory, $"{hex}.mp4");

                if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 1024)
                    return targetPath;
            }
            catch { }
            return null;
        }

        private async Task<string?> EnsureLocalCachedVideoAsync(string url, CancellationToken ct)
        {
            try
            {
                string downloadUrl = url;
                if (downloadUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
                {
                    string? resolved = await AppleMusicProvider.ResolveMp4FromM3u8Async(downloadUrl, ct);
                    if (!string.IsNullOrEmpty(resolved) && !resolved.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = resolved;
                    }
                }

                using var sha256 = SHA256.Create();
                // Check cache by both original URL and resolved URL
                byte[] hashOrig = sha256.ComputeHash(Encoding.UTF8.GetBytes(url));
                string hexOrig = BitConverter.ToString(hashOrig).Replace("-", "").ToLowerInvariant();
                string targetPath = Path.Combine(_cacheDirectory, $"{hexOrig}.mp4");

                if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 1024)
                {
                    VideoCached?.Invoke(url, targetPath);
                    return targetPath;
                }

                if (downloadUrl != url)
                {
                    byte[] hashResolved = sha256.ComputeHash(Encoding.UTF8.GetBytes(downloadUrl));
                    string hexResolved = BitConverter.ToString(hashResolved).Replace("-", "").ToLowerInvariant();
                    string resolvedPath = Path.Combine(_cacheDirectory, $"{hexResolved}.mp4");
                    if (File.Exists(resolvedPath) && new FileInfo(resolvedPath).Length > 1024)
                    {
                        VideoCached?.Invoke(url, resolvedPath);
                        return resolvedPath;
                    }
                }

                // If still an unresolved m3u8 playlist after attempt, we cannot cache it as a single mp4
                if (downloadUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
                    return null;

                using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
                request.Headers.UserAgent.ParseAdd(UserAgent);
                request.Headers.Add("Accept", "*/*");

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                    return null;

                string tempPath = targetPath + ".tmp";
                await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await response.Content.CopyToAsync(fs, ct);
                }

                var tempInfo = new FileInfo(tempPath);
                if (tempInfo.Length > 1024)
                {
                    if (File.Exists(targetPath))
                        File.Delete(targetPath);
                    File.Move(tempPath, targetPath);

                    VideoCached?.Invoke(url, targetPath);
                    return targetPath;
                }
                else
                {
                    try { File.Delete(tempPath); } catch { }
                    return null;
                }
            }
            catch
            {
                return null;
            }
        }

        // ====================================================================
        // PROVIDER 1: Apple Music (AMP Catalog Web Scraper + Editorial Video)
        // ====================================================================
        private static class AppleMusicProvider
        {
            private const string AmpCatalogUrl = "https://amp-api.music.apple.com/v1/catalog";
            private const string WebPlayerUrl = "https://music.apple.com/us/browse";
            private static readonly string Storefront = GetStorefront();

            private static string? _cachedToken;
            private static long _tokenExpiresAtMs;
            private static long _retryTokenAfterMs;
            private static readonly SemaphoreSlim _tokenLock = new SemaphoreSlim(1, 1);

            private static string GetStorefront()
            {
                try
                {
                    string country = RegionInfo.CurrentRegion.TwoLetterISORegionName;
                    if (country.Length == 2) return country.ToLowerInvariant();
                }
                catch { }
                return "us";
            }

            public static async Task<string?> ResolveMp4FromM3u8Async(string m3u8Url, CancellationToken ct)
            {
                if (string.IsNullOrWhiteSpace(m3u8Url)) return null;
                if (m3u8Url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || m3u8Url.Contains(".mp4?"))
                    return m3u8Url;

                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, m3u8Url);
                    req.Headers.UserAgent.ParseAdd(UserAgent);
                    using var resp = await _httpClient.SendAsync(req, ct);
                    if (!resp.IsSuccessStatusCode) return m3u8Url;

                    string m3u8Text = await resp.Content.ReadAsStringAsync(ct);

                    // Case A: Playlist directly contains #EXT-X-MAP:URI="...mp4"
                    var directMapMatch = Regex.Match(m3u8Text, @"#EXT-X-MAP:URI=""([^""]+)""");
                    if (directMapMatch.Success)
                    {
                        string rel = directMapMatch.Groups[1].Value;
                        var baseUri = new Uri(m3u8Url);
                        return new Uri(baseUri, rel).ToString();
                    }

                    // Case B: Master playlist with variant streams
                    // Prioritize avc1 (H.264) codec which works natively on 100% of Windows installations without requiring external HEVC codecs
                    var streamMatches = Regex.Matches(m3u8Text, @"#EXT-X-STREAM-INF:([^\r\n]+)[\r\n]+([^\r\n]+)");
                    if (streamMatches.Count > 0)
                    {
                        string? bestVariantUrl = null;
                        int bestScore = -1;

                        foreach (Match match in streamMatches)
                        {
                            string info = match.Groups[1].Value;
                            string streamUriStr = match.Groups[2].Value.Trim();
                            if (string.IsNullOrWhiteSpace(streamUriStr)) continue;

                            int score = 0;
                            if (info.Contains("avc1", StringComparison.OrdinalIgnoreCase))
                                score += 10000;

                            var resMatch = Regex.Match(info, @"RESOLUTION=(\d+)x(\d+)");
                            if (resMatch.Success && int.TryParse(resMatch.Groups[1].Value, out int width))
                            {
                                score += Math.Min(width, 1080);
                            }

                            if (score > bestScore)
                            {
                                bestScore = score;
                                var baseUri = new Uri(m3u8Url);
                                bestVariantUrl = new Uri(baseUri, streamUriStr).ToString();
                            }
                        }

                        if (!string.IsNullOrEmpty(bestVariantUrl))
                        {
                            using var vReq = new HttpRequestMessage(HttpMethod.Get, bestVariantUrl);
                            vReq.Headers.UserAgent.ParseAdd(UserAgent);
                            using var vResp = await _httpClient.SendAsync(vReq, ct);
                            if (vResp.IsSuccessStatusCode)
                            {
                                string vText = await vResp.Content.ReadAsStringAsync(ct);
                                var mapMatch = Regex.Match(vText, @"#EXT-X-MAP:URI=""([^""]+)""");
                                if (mapMatch.Success)
                                {
                                    string rel = mapMatch.Groups[1].Value;
                                    var vBaseUri = new Uri(bestVariantUrl);
                                    return new Uri(vBaseUri, rel).ToString();
                                }

                                var mp4Line = vText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                    .FirstOrDefault(line => !line.StartsWith("#") && (line.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || line.Contains(".mp4?")));
                                if (!string.IsNullOrEmpty(mp4Line))
                                {
                                    var vBaseUri = new Uri(bestVariantUrl);
                                    return new Uri(vBaseUri, mp4Line.Trim()).ToString();
                                }
                            }
                        }
                    }
                }
                catch { }

                return m3u8Url;
            }

            public static async Task<MotionArtworkResult?> SearchAsync(string title, string artist, string? album, CancellationToken ct)
            {
                string? token = await GetTokenAsync(ct);
                if (string.IsNullOrEmpty(token)) return null;

                // Build term
                var sb = new StringBuilder();
                if (!title.Contains(artist, StringComparison.OrdinalIgnoreCase))
                    sb.Append(artist).Append(' ');
                sb.Append(title);
                if (!string.IsNullOrWhiteSpace(album) && !title.Contains(album, StringComparison.OrdinalIgnoreCase))
                    sb.Append(' ').Append(album);

                string term = Uri.EscapeDataString(sb.ToString().Trim());
                string url = $"{AmpCatalogUrl}/{Storefront}/search?term={term}&types=songs&limit=10&extend=editorialVideo&include=albums";

                string? json = await GetJsonAsync(url, token, ct);
                if (string.IsNullOrEmpty(json)) return null;

                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("results", out var results) ||
                    !results.TryGetProperty("songs", out var songs) ||
                    !songs.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var ranked = new List<(int score, JsonElement song)>();
                foreach (var song in data.EnumerateArray())
                {
                    int? score = ScoreSong(song, title, artist, album);
                    if (score.HasValue && score.Value >= 12)
                    {
                        ranked.Add((score.Value, song.Clone()));
                    }
                }

                ranked.Sort((a, b) => b.score.CompareTo(a.score));

                foreach (var (_, song) in ranked)
                {
                    if (!song.TryGetProperty("attributes", out var attrs)) continue;

                    string? songName = attrs.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? songArtist = attrs.TryGetProperty("artistName", out var a) ? a.GetString() : null;
                    string? albumName = attrs.TryGetProperty("albumName", out var al) ? al.GetString() : null;

                    if (attrs.TryGetProperty("editorialVideo", out var editorialVideo))
                    {
                        var urls = ExtractMotionUrls(editorialVideo);
                        if (urls != null)
                        {
                            string resolvedPrimary = await ResolveMp4FromM3u8Async(urls.Value.primary, ct) ?? urls.Value.primary;
                            string? resolvedAlternate = !string.IsNullOrEmpty(urls.Value.alternate)
                                ? await ResolveMp4FromM3u8Async(urls.Value.alternate, ct)
                                : null;

                            return new MotionArtworkResult
                            {
                                Url = resolvedPrimary,
                                FallbackUrl = resolvedAlternate,
                                Title = songName,
                                Artist = songArtist,
                                Album = albumName,
                                Source = "AppleMusic"
                            };
                        }
                    }

                    // Check album relationship or URL for album ID
                    string? albumId = GetAlbumId(song);
                    if (!string.IsNullOrEmpty(albumId))
                    {
                        var albumResult = await FetchAlbumAsync(albumId, token, songName, songArtist, ct);
                        if (albumResult != null) return albumResult;
                    }

                    // If albumId lookup returned null or 404, but we have a non-compilation albumName from song:
                    if (!string.IsNullOrWhiteSpace(albumName) && !IsCompilation(albumName))
                    {
                        var albumResult = await SearchAlbumAsync(albumName, songArtist ?? artist, token, ct);
                        if (albumResult != null)
                        {
                            albumResult.Title = songName ?? albumResult.Title;
                            return albumResult;
                        }
                    }
                }

                // If song search had no motion video, search album directly
                if (!string.IsNullOrWhiteSpace(album))
                {
                    var albumResult = await SearchAlbumAsync(album, artist, token, ct);
                    if (albumResult != null) return albumResult;
                }
                else
                {
                    // Fallback album search using song title (for eponymous releases, singles, or album titles matching track)
                    var albumResult = await SearchAlbumAsync(title, artist, token, ct);
                    if (albumResult != null) return albumResult;
                }

                return null;
            }

            private static async Task<MotionArtworkResult?> SearchAlbumAsync(string album, string artist, string token, CancellationToken ct)
            {
                string term = album.Contains(artist, StringComparison.OrdinalIgnoreCase) ? album : $"{artist} {album}";
                string url = $"{AmpCatalogUrl}/{Storefront}/search?term={Uri.EscapeDataString(term)}&types=albums&limit=10&extend=editorialVideo";

                string? json = await GetJsonAsync(url, token, ct);
                if (string.IsNullOrEmpty(json)) return null;

                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("results", out var results) ||
                    !results.TryGetProperty("albums", out var albums) ||
                    !albums.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                foreach (var record in data.EnumerateArray())
                {
                    if (!record.TryGetProperty("attributes", out var attrs)) continue;
                    string? name = attrs.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name != null && IsCompilation(name)) continue;

                    string? recArtist = attrs.TryGetProperty("artistName", out var a) ? a.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(recArtist))
                    {
                        var wanted = SplitCanvasArtists(artist);
                        var credited = SplitCanvasArtists(recArtist);
                        if (wanted.Count > 0 && credited.Count > 0)
                        {
                            bool artistMatches = wanted.Any(w => credited.Any(c => c == w || c.Contains(w) || w.Contains(c))) ||
                                                 credited.Any(c => wanted.Any(w => w == c || w.Contains(c) || c.Contains(w)));
                            if (!artistMatches) continue;
                        }
                    }

                    if (attrs.TryGetProperty("editorialVideo", out var video))
                    {
                        var urls = ExtractMotionUrls(video);
                        if (urls != null)
                        {
                            string resolvedPrimary = await ResolveMp4FromM3u8Async(urls.Value.primary, ct) ?? urls.Value.primary;
                            string? resolvedAlternate = !string.IsNullOrEmpty(urls.Value.alternate)
                                ? await ResolveMp4FromM3u8Async(urls.Value.alternate, ct)
                                : null;

                            return new MotionArtworkResult
                            {
                                Url = resolvedPrimary,
                                FallbackUrl = resolvedAlternate,
                                Title = name,
                                Artist = recArtist,
                                Album = name,
                                Source = "AppleMusic"
                            };
                        }
                    }
                }

                return null;
            }

            private static async Task<MotionArtworkResult?> FetchAlbumAsync(string albumId, string token, string? songTitle, string? songArtist, CancellationToken ct)
            {
                string url = $"{AmpCatalogUrl}/{Storefront}/albums/{albumId}?extend=editorialVideo";
                string? json = await GetJsonAsync(url, token, ct);
                if (string.IsNullOrEmpty(json)) return null;

                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array ||
                    data.GetArrayLength() == 0)
                {
                    return null;
                }

                var album = data[0];
                if (!album.TryGetProperty("attributes", out var attrs)) return null;
                string albumName = attrs.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (IsCompilation(albumName)) return null;

                if (attrs.TryGetProperty("editorialVideo", out var video))
                {
                    var urls = ExtractMotionUrls(video);
                    if (urls != null)
                    {
                        string resolvedPrimary = await ResolveMp4FromM3u8Async(urls.Value.primary, ct) ?? urls.Value.primary;
                        string? resolvedAlternate = !string.IsNullOrEmpty(urls.Value.alternate)
                            ? await ResolveMp4FromM3u8Async(urls.Value.alternate, ct)
                            : null;

                        return new MotionArtworkResult
                        {
                            Url = resolvedPrimary,
                            FallbackUrl = resolvedAlternate,
                            Title = songTitle,
                            Artist = songArtist ?? (attrs.TryGetProperty("artistName", out var a) ? a.GetString() : null),
                            Album = albumName,
                            Source = "AppleMusic"
                        };
                    }
                }

                return null;
            }

            private static (string primary, string? alternate)? ExtractMotionUrls(JsonElement video)
            {
                string? Link(string key)
                {
                    if (video.TryGetProperty(key, out var asset) && asset.ValueKind == JsonValueKind.Object)
                    {
                        if (asset.TryGetProperty("video", out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
                        if (asset.TryGetProperty("videoUrl", out var vu) && vu.ValueKind == JsonValueKind.String) return vu.GetString();
                        if (asset.TryGetProperty("hlsUrl", out var hu) && hu.ValueKind == JsonValueKind.String) return hu.GetString();
                        if (asset.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String) return u.GetString();
                    }
                    return null;
                }

                string? square = Link("motionDetailSquare") ?? Link("motionSquareVideo1x1");
                string? raw = Link("motionDetailRaw");
                string? tall = Link("motionDetailTall") ?? Link("motionTallVideo3x4");

                string? primary = square ?? raw ?? tall;
                if (string.IsNullOrEmpty(primary)) return null;

                string? alternate = new[] { square, raw, tall }.FirstOrDefault(u => !string.IsNullOrEmpty(u) && u != primary);
                return (primary, alternate);
            }

            private static string? GetAlbumId(JsonElement song)
            {
                if (song.TryGetProperty("relationships", out var rels) &&
                    rels.TryGetProperty("albums", out var albums) &&
                    albums.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.Array &&
                    data.GetArrayLength() > 0)
                {
                    var first = data[0];
                    if (first.TryGetProperty("id", out var idElem))
                    {
                        string? id = idElem.GetString();
                        if (id != null && !id.StartsWith("pl.", StringComparison.OrdinalIgnoreCase))
                            return id;
                    }
                }

                if (song.TryGetProperty("attributes", out var attrs) &&
                    attrs.TryGetProperty("url", out var urlElem))
                {
                    string? url = urlElem.GetString();
                    if (!string.IsNullOrEmpty(url) && url.Contains("/album/"))
                    {
                        int albumIdx = url.IndexOf("/album/", StringComparison.Ordinal);
                        string sub = url.Substring(albumIdx + 7);
                        int qIdx = sub.IndexOf('?');
                        if (qIdx >= 0) sub = sub.Substring(0, qIdx);
                        int lastSlash = sub.LastIndexOf('/');
                        if (lastSlash >= 0) sub = sub.Substring(lastSlash + 1);
                        if (!string.IsNullOrWhiteSpace(sub) && sub.All(char.IsDigit))
                            return sub;
                    }
                }

                return null;
            }

            private static int? ScoreSong(JsonElement song, string title, string artist, string? album)
            {
                if (!song.TryGetProperty("attributes", out var attrs)) return null;
                string hitName = attrs.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string hitArtist = attrs.TryGetProperty("artistName", out var a) ? a.GetString() ?? "" : "";
                string hitAlbum = attrs.TryGetProperty("albumName", out var al) ? al.GetString() ?? "" : "";

                if (IsCompilation(hitName) || IsCompilation(hitAlbum)) return null;

                var wanted = SplitCanvasArtists(artist);
                var credited = SplitCanvasArtists(hitArtist);
                if (wanted.Count > 0 && credited.Count > 0)
                {
                    if (!wanted.Any(w => credited.Any(c => c == w || c.Contains(w) || w.Contains(c))) &&
                        !credited.Any(c => wanted.Any(w => w == c || w.Contains(c) || c.Contains(w))))
                        return null;
                }

                int score = 10;
                string wantTitle = NormalizeForCanvasMatch(title);
                string hitTitle = NormalizeForCanvasMatch(hitName);

                if (hitTitle == wantTitle)
                    score += 15;
                else if (hitTitle.Contains(wantTitle) || wantTitle.Contains(hitTitle))
                    score += 7;
                else
                    score -= 10;

                if (!string.IsNullOrWhiteSpace(album) && !string.IsNullOrWhiteSpace(hitAlbum))
                {
                    string wantAlbum = NormalizeForCanvasMatch(album);
                    string gotAlbum = NormalizeForCanvasMatch(hitAlbum);
                    if (gotAlbum == wantAlbum)
                        score += 20;
                    else if (gotAlbum.Contains(wantAlbum) || wantAlbum.Contains(gotAlbum))
                        score += 10;
                }

                string[] editionWords = { "deluxe", "expanded", "remastered", "remix", "version", "edit", "mix", "bonus" };
                foreach (var word in editionWords)
                {
                    bool inWanted = title.Contains(word, StringComparison.OrdinalIgnoreCase);
                    bool inHit = hitName.Contains(word, StringComparison.OrdinalIgnoreCase);
                    if (inWanted && inHit) score += 5;
                    else if (inHit) score -= 3;
                }

                return score;
            }

            private static bool IsCompilation(string name)
            {
                string lower = name.ToLowerInvariant();
                string[] markers = { "playlist", "set list", "essentials", "dj mix", "mixed", "apple music", "today's hits", "session" };
                return markers.Any(m => lower.Contains(m));
            }

            private static async Task<string?> GetTokenAsync(CancellationToken ct)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (_cachedToken != null && now < _tokenExpiresAtMs - 60000)
                    return _cachedToken;

                string tokenFile = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Lmen",
                    "apple_token.json"
                );

                if (_cachedToken == null)
                {
                    try
                    {
                        if (File.Exists(tokenFile))
                        {
                            string saved = File.ReadAllText(tokenFile);
                            using var doc = JsonDocument.Parse(saved);
                            if (doc.RootElement.TryGetProperty("token", out var tProp) &&
                                doc.RootElement.TryGetProperty("expiresAt", out var expProp) &&
                                expProp.TryGetInt64(out long savedExp) &&
                                savedExp > now + 60000)
                            {
                                _cachedToken = tProp.GetString();
                                _tokenExpiresAtMs = savedExp;
                                return _cachedToken;
                            }
                        }
                    }
                    catch { }
                }

                if (now < _retryTokenAfterMs)
                    return null;

                await _tokenLock.WaitAsync(ct);
                try
                {
                    now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (_cachedToken != null && now < _tokenExpiresAtMs - 60000)
                        return _cachedToken;

                    using var req = new HttpRequestMessage(HttpMethod.Get, WebPlayerUrl);
                    req.Headers.UserAgent.ParseAdd(UserAgent);
                    using var resp = await _httpClient.SendAsync(req, ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        _retryTokenAfterMs = now + 1800000;
                        return null;
                    }

                    string html = await resp.Content.ReadAsStringAsync(ct);
                    var scriptMatches = Regex.Matches(html, @"/assets/index(?:-legacy)?[~-][A-Za-z0-9_-]+\.js");
                    var scriptPaths = scriptMatches.Select(m => m.Value).Distinct().ToList();

                    foreach (var path in scriptPaths)
                    {
                        using var scriptReq = new HttpRequestMessage(HttpMethod.Get, "https://music.apple.com" + path);
                        scriptReq.Headers.UserAgent.ParseAdd(UserAgent);
                        using var scriptResp = await _httpClient.SendAsync(scriptReq, ct);
                        if (!scriptResp.IsSuccessStatusCode) continue;

                        string js = await scriptResp.Content.ReadAsStringAsync(ct);
                        var jwtMatches = Regex.Matches(js, @"ey[A-Za-z0-9_-]+\.ey[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+");

                        string? fallbackJwt = null;
                        long fallbackExp = 0;

                        foreach (Match m in jwtMatches)
                        {
                            string jwt = m.Value;
                            long? exp = GetJwtExpiry(jwt);
                            if (exp.HasValue && exp.Value > now)
                            {
                                if (IsWebPlayerToken(jwt))
                                {
                                    _cachedToken = jwt;
                                    _tokenExpiresAtMs = exp.Value;
                                    SaveTokenToDisk(tokenFile, jwt, exp.Value);
                                    return jwt;
                                }
                                else if (fallbackJwt == null)
                                {
                                    fallbackJwt = jwt;
                                    fallbackExp = exp.Value;
                                }
                            }
                        }

                        if (fallbackJwt != null)
                        {
                            _cachedToken = fallbackJwt;
                            _tokenExpiresAtMs = fallbackExp;
                            SaveTokenToDisk(tokenFile, fallbackJwt, fallbackExp);
                            return fallbackJwt;
                        }
                    }

                    _retryTokenAfterMs = now + 1800000;
                    return null;
                }
                catch
                {
                    _retryTokenAfterMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 300000;
                    return null;
                }
                finally
                {
                    _tokenLock.Release();
                }
            }

            private static void SaveTokenToDisk(string path, string token, long expiresAt)
            {
                try
                {
                    string dir = Path.GetDirectoryName(path)!;
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    string json = $"{{\"token\":\"{token}\",\"expiresAt\":{expiresAt}}}";
                    File.WriteAllText(path, json);
                }
                catch { }
            }

            private static bool IsWebPlayerToken(string jwt)
            {
                try
                {
                    string[] parts = jwt.Split('.');
                    if (parts.Length < 2) return false;
                    string body = parts[1];
                    int pad = (4 - body.Length % 4) % 4;
                    body += new string('=', pad);
                    byte[] data = Convert.FromBase64String(body.Replace('-', '+').Replace('_', '/'));
                    string payloadJson = Encoding.UTF8.GetString(data);
                    return payloadJson.Contains("apple.com", StringComparison.OrdinalIgnoreCase) ||
                           payloadJson.Contains("AMPWebPlay", StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            }

            private static long? GetJwtExpiry(string jwt)
            {
                try
                {
                    string[] parts = jwt.Split('.');
                    if (parts.Length < 2) return null;
                    string body = parts[1];
                    int pad = (4 - body.Length % 4) % 4;
                    body += new string('=', pad);
                    byte[] data = Convert.FromBase64String(body.Replace('-', '+').Replace('_', '/'));
                    using var doc = JsonDocument.Parse(data);
                    if (doc.RootElement.TryGetProperty("exp", out var expElem) && expElem.TryGetInt64(out long exp))
                    {
                        return exp * 1000;
                    }
                }
                catch { }
                return null;
            }

            private static async Task<string?> GetJsonAsync(string url, string bearer, CancellationToken ct)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd(UserAgent);
                req.Headers.Add("Authorization", "Bearer " + bearer);
                req.Headers.Add("Origin", "https://music.apple.com");
                req.Headers.Add("Referer", "https://music.apple.com/");

                using var resp = await _httpClient.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode)
                    return await resp.Content.ReadAsStringAsync(ct);
                return null;
            }
        }

        // ====================================================================
        // PROVIDER 2: Tidal (Public Embed Search + 1280x1280 Video Covers)
        // ====================================================================
        private static class TidalProvider
        {
            private const string SearchUrl = "https://api.tidal.com/v1/search";
            private const string EmbedToken = "vNVdglQOjFJJGG2U";

            public static async Task<MotionArtworkResult?> SearchAsync(string title, string artist, string? album, CancellationToken ct)
            {
                var result = await SearchTracksInternalAsync($"{artist} {title}", title, artist, ct);
                if (result != null) return result;

                if (!string.IsNullOrWhiteSpace(album))
                {
                    result = await SearchTracksInternalAsync($"{album} {artist} {title}", title, artist, ct);
                    if (result != null) return result;

                    result = await SearchAlbumAsync(album, artist, ct);
                    if (result != null) return result;
                }
                else
                {
                    // Fallback album search using song title
                    result = await SearchAlbumAsync(title, artist, ct);
                    if (result != null) return result;
                }

                return null;
            }

            private static async Task<MotionArtworkResult?> SearchTracksInternalAsync(string term, string title, string artist, CancellationToken ct)
            {
                string url = $"{SearchUrl}?query={Uri.EscapeDataString(term)}&limit=10&types=TRACKS&countryCode=US";

                string? json = await GetJsonAsync(url, ct);
                if (string.IsNullOrEmpty(json)) return null;

                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("tracks", out var tracks) ||
                    !tracks.TryGetProperty("items", out var items) ||
                    items.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                foreach (var track in items.EnumerateArray())
                {
                    string? trackTitle = track.TryGetProperty("title", out var t) ? t.GetString() : null;
                    if (string.IsNullOrEmpty(trackTitle)) continue;

                    var artistsList = new List<string>();
                    if (track.TryGetProperty("artists", out var arts) && arts.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var a in arts.EnumerateArray())
                        {
                            if (a.TryGetProperty("name", out var n) && n.GetString() is string s)
                                artistsList.Add(s);
                        }
                    }

                    if (!CanvasMatches(trackTitle, artistsList, title, artist)) continue;

                    if (track.TryGetProperty("album", out var albumObj))
                    {
                        if (albumObj.TryGetProperty("videoCover", out var vcElem) &&
                            vcElem.ValueKind == JsonValueKind.String)
                        {
                            string? vcId = vcElem.GetString();
                            string? videoUrl = BuildCoverUrl(vcId);
                            if (!string.IsNullOrEmpty(videoUrl))
                            {
                                return new MotionArtworkResult
                                {
                                    Url = videoUrl,
                                    Title = trackTitle,
                                    Artist = string.Join(", ", artistsList),
                                    Album = albumObj.TryGetProperty("title", out var at) ? at.GetString() : null,
                                    Source = "Tidal"
                                };
                            }
                        }

                        // If track has no inline videoCover, but has album title, try searching that album directly
                        if (albumObj.TryGetProperty("title", out var albTitleElem) &&
                            albTitleElem.GetString() is string albTitle &&
                            !string.IsNullOrWhiteSpace(albTitle))
                        {
                            var albumRes = await SearchAlbumAsync(albTitle, artist, ct);
                            if (albumRes != null)
                            {
                                albumRes.Title = trackTitle;
                                return albumRes;
                            }
                        }
                    }
                }

                return null;
            }

            private static async Task<MotionArtworkResult?> SearchAlbumAsync(string album, string artist, CancellationToken ct)
            {
                string url = $"{SearchUrl}?query={Uri.EscapeDataString($"{album} {artist}")}&limit=10&types=ALBUMS&countryCode=US";
                string? json = await GetJsonAsync(url, ct);
                if (string.IsNullOrEmpty(json)) return null;

                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("albums", out var albums) ||
                    !albums.TryGetProperty("items", out var items) ||
                    items.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                foreach (var record in items.EnumerateArray())
                {
                    string? recordTitle = record.TryGetProperty("title", out var t) ? t.GetString() : null;
                    if (string.IsNullOrEmpty(recordTitle)) continue;

                    var artistsList = new List<string>();
                    if (record.TryGetProperty("artists", out var arts) && arts.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var a in arts.EnumerateArray())
                        {
                            if (a.TryGetProperty("name", out var n) && n.GetString() is string s)
                                artistsList.Add(s);
                        }
                    }

                    if (!CanvasMatches(recordTitle, artistsList, album, artist)) continue;

                    if (record.TryGetProperty("videoCover", out var vcElem) &&
                        vcElem.ValueKind == JsonValueKind.String)
                    {
                        string? vcId = vcElem.GetString();
                        string? videoUrl = BuildCoverUrl(vcId);
                        if (!string.IsNullOrEmpty(videoUrl))
                        {
                            return new MotionArtworkResult
                            {
                                Url = videoUrl,
                                Title = recordTitle,
                                Artist = string.Join(", ", artistsList),
                                Album = recordTitle,
                                Source = "Tidal"
                            };
                        }
                    }
                }

                return null;
            }

            private static string? BuildCoverUrl(string? id)
            {
                if (string.IsNullOrWhiteSpace(id)) return null;
                string[] parts = id.Split('-');
                if (parts.Length != 5) return null;
                return $"https://resources.tidal.com/videos/{string.Join("/", parts)}/1280x1280.mp4";
            }

            private static async Task<string?> GetJsonAsync(string url, CancellationToken ct)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd(UserAgent);
                req.Headers.Add("X-Tidal-Token", EmbedToken);

                using var resp = await _httpClient.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode)
                    return await resp.Content.ReadAsStringAsync(ct);
                return null;
            }
        }

        // ====================================================================
        // PROVIDER 3: Community Canvas Library (Vivi Music Index)
        // ====================================================================
        private static class CommunityProvider
        {
            private const string ManifestUrl = "https://vivimusicanvas.mkmdevilmi.workers.dev/canvas.json";
            private static List<CommunityEntry> _entries = new List<CommunityEntry>();
            private static long _fetchedAtMs = 0;
            private const long TtlMs = 1800000; // 30 minutes
            private static readonly SemaphoreSlim _manifestLock = new SemaphoreSlim(1, 1);

            private class CommunityEntry
            {
                public string Song { get; set; } = "";
                public string Artist { get; set; } = "";
                public string Album { get; set; } = "";
                public string Url { get; set; } = "";
            }

            public static async Task<MotionArtworkResult?> SearchAsync(string title, string artist, string? album, CancellationToken ct)
            {
                var index = await GetManifestAsync(ct);
                if (index.Count == 0) return null;

                string wantTitle = NormalizeForCanvasMatch(title);
                string wantArtist = NormalizeForCanvasMatch(artist);
                string? wantAlbum = !string.IsNullOrWhiteSpace(album) ? NormalizeForCanvasMatch(album) : null;

                var hit = index.FirstOrDefault(e =>
                {
                    string song = NormalizeForCanvasMatch(e.Song);
                    string credited = NormalizeForCanvasMatch(e.Artist);
                    string listed = NormalizeForCanvasMatch(e.Album);

                    if (string.IsNullOrWhiteSpace(song) || string.IsNullOrWhiteSpace(credited)) return false;
                    bool titleMatch = wantTitle == song || wantTitle.Contains(song) || song.Contains(wantTitle);
                    bool artistMatch = wantArtist == credited || wantArtist.Contains(credited) || credited.Contains(wantArtist);
                    return titleMatch && artistMatch;
                });

                if (hit == null && !string.IsNullOrWhiteSpace(wantAlbum))
                {
                    // Fallback search by album
                    hit = index.FirstOrDefault(e =>
                    {
                        string listed = NormalizeForCanvasMatch(e.Album);
                        string credited = NormalizeForCanvasMatch(e.Artist);
                        return !string.IsNullOrWhiteSpace(listed) && listed == wantAlbum &&
                               !string.IsNullOrWhiteSpace(credited) &&
                               (wantArtist == credited || wantArtist.Contains(credited) || credited.Contains(wantArtist));
                    });
                }

                if (hit != null)
                {
                    string resolvedUrl = hit.Url;
                    if (resolvedUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
                    {
                        string? directMp4 = await AppleMusicProvider.ResolveMp4FromM3u8Async(resolvedUrl, ct);
                        if (!string.IsNullOrEmpty(directMp4) && !directMp4.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
                        {
                            resolvedUrl = directMp4;
                        }
                    }

                    return new MotionArtworkResult
                    {
                        Url = resolvedUrl,
                        Title = hit.Song,
                        Artist = hit.Artist,
                        Album = string.IsNullOrWhiteSpace(hit.Album) ? null : hit.Album,
                        Source = "Community"
                    };
                }

                return null;
            }

            private static async Task<List<CommunityEntry>> GetManifestAsync(CancellationToken ct)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (_entries.Count > 0 && now - _fetchedAtMs < TtlMs)
                    return _entries;

                await _manifestLock.WaitAsync(ct);
                try
                {
                    now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (_entries.Count > 0 && now - _fetchedAtMs < TtlMs)
                        return _entries;

                    using var req = new HttpRequestMessage(HttpMethod.Get, ManifestUrl);
                    req.Headers.UserAgent.ParseAdd(UserAgent);

                    using var resp = await _httpClient.SendAsync(req, ct);
                    if (!resp.IsSuccessStatusCode)
                        return _entries;

                    string json = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                        return _entries;

                    var list = new List<CommunityEntry>();
                    foreach (var item in items.EnumerateArray())
                    {
                        string? song = item.TryGetProperty("song", out var s) ? s.GetString() : null;
                        string? artist = item.TryGetProperty("artist", out var a) ? a.GetString() : null;
                        string? url = item.TryGetProperty("url", out var u) ? u.GetString() : null;
                        string album = item.TryGetProperty("album", out var al) ? al.GetString() ?? "" : "";

                        if (!string.IsNullOrEmpty(song) && !string.IsNullOrEmpty(artist) && !string.IsNullOrEmpty(url))
                        {
                            list.Add(new CommunityEntry
                            {
                                Song = song,
                                Artist = artist,
                                Album = album,
                                Url = url
                            });
                        }
                    }

                    if (list.Count > 0)
                    {
                        _entries = list;
                        _fetchedAtMs = now;
                    }

                    return _entries;
                }
                catch
                {
                    return _entries;
                }
                finally
                {
                    _manifestLock.Release();
                }
            }
        }

        // ====================================================================
        // PROVIDER 4: Spotify Canvas (Web Token / sp_dc + canvaz-cache protobuf)
        // ====================================================================
        private static class SpotifyProvider
        {
            private const string SearchUrl = "https://api.spotify.com/v1/search";
            private const string CanvasUrl = "https://spclient.wg.spotify.com/canvaz-cache/v0/canvases";
            private const string TokenUrl = "https://open.spotify.com/api/token";
            private const string SpotifyAppUa = "Spotify/9.0.34.593 iOS/18.4 (iPhone15,3)";
            private static readonly Regex CanvasUrlRegex = new Regex(@"https://[^""'\s\x00-\x1F]+\.cnvs\.mp4", RegexOptions.Compiled);

            private static string? _cachedAccessToken;
            private static long _tokenExpiresAtMs;
            private static long _retryTokenAfterMs;
            private static readonly SemaphoreSlim _tokenLock = new SemaphoreSlim(1, 1);

            public static async Task<MotionArtworkResult?> SearchAsync(string title, string artist, string? album, string spDcCookie, CancellationToken ct)
            {
                if (string.IsNullOrWhiteSpace(spDcCookie)) return null;

                string? token = await GetAccessTokenAsync(spDcCookie.Trim(), ct);
                if (string.IsNullOrEmpty(token)) return null;

                // 1. Search track
                string query = string.IsNullOrWhiteSpace(album) ? $"{artist} {title}" : $"{artist} {title} {album}";
                string url = $"{SearchUrl}?q={Uri.EscapeDataString(query)}&type=track&limit=10";

                string? json = await GetJsonAsync(url, token, ct);
                if (string.IsNullOrEmpty(json)) return null;

                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("tracks", out var tracks) ||
                    !tracks.TryGetProperty("items", out var items) ||
                    items.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                foreach (var track in items.EnumerateArray())
                {
                    string? trackTitle = track.TryGetProperty("name", out var t) ? t.GetString() : null;
                    if (string.IsNullOrEmpty(trackTitle)) continue;

                    var artistsList = new List<string>();
                    if (track.TryGetProperty("artists", out var arts) && arts.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var a in arts.EnumerateArray())
                        {
                            if (a.TryGetProperty("name", out var n) && n.GetString() is string s)
                                artistsList.Add(s);
                        }
                    }

                    if (!CanvasMatches(trackTitle, artistsList, title, artist)) continue;

                    string? trackUri = track.TryGetProperty("uri", out var u) ? u.GetString() : null;
                    if (string.IsNullOrEmpty(trackUri)) continue;

                    string? canvasUrl = await FetchCanvasUrlAsync(trackUri, token, ct);
                    if (!string.IsNullOrEmpty(canvasUrl))
                    {
                        string? trackAlbum = null;
                        if (track.TryGetProperty("album", out var alb) && alb.TryGetProperty("name", out var an))
                            trackAlbum = an.GetString();

                        return new MotionArtworkResult
                        {
                            Url = canvasUrl,
                            Title = trackTitle,
                            Artist = string.Join(", ", artistsList),
                            Album = trackAlbum,
                            Source = "Spotify"
                        };
                    }
                }

                return null;
            }

            private static async Task<string?> FetchCanvasUrlAsync(string trackUri, string token, CancellationToken ct)
            {
                try
                {
                    byte[] requestBody = EncodeCanvasRequest(trackUri);
                    using var req = new HttpRequestMessage(HttpMethod.Post, CanvasUrl);
                    req.Headers.UserAgent.ParseAdd(SpotifyAppUa);
                    req.Headers.Add("Authorization", "Bearer " + token);
                    req.Headers.Add("Accept", "application/protobuf");
                    req.Headers.Add("Accept-Language", "en");
                    req.Content = new ByteArrayContent(requestBody);
                    req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/protobuf");

                    using var resp = await _httpClient.SendAsync(req, ct);
                    if (!resp.IsSuccessStatusCode) return null;

                    byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                    string rawStr = Encoding.Latin1.GetString(bytes);
                    var match = CanvasUrlRegex.Match(rawStr);
                    if (match.Success)
                    {
                        return match.Value;
                    }
                }
                catch { }

                return null;
            }

            private static byte[] EncodeCanvasRequest(string trackUri)
            {
                // CanvasRequest { repeated Track tracks = 1; Track { string track_uri = 1; } }
                using var trackMs = new MemoryStream();
                WriteLengthDelimited(trackMs, 1, Encoding.UTF8.GetBytes(trackUri));
                byte[] trackBytes = trackMs.ToArray();

                using var rootMs = new MemoryStream();
                WriteLengthDelimited(rootMs, 1, trackBytes);
                return rootMs.ToArray();
            }

            private static void WriteLengthDelimited(Stream stream, int field, byte[] payload)
            {
                WriteVarint(stream, (field << 3) | 2);
                WriteVarint(stream, payload.Length);
                stream.Write(payload, 0, payload.Length);
            }

            private static void WriteVarint(Stream stream, long value)
            {
                long remaining = value;
                while (true)
                {
                    if ((remaining & ~0x7FL) == 0)
                    {
                        stream.WriteByte((byte)remaining);
                        return;
                    }
                    stream.WriteByte((byte)((remaining & 0x7F) | 0x80));
                    remaining = (long)((ulong)remaining >> 7);
                }
            }

            private static string? GenerateTotp()
            {
                try
                {
                    string secretStr = ",7/*F(\"rLJ2oxaKL^f+E1xvP@N";
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    long step = now / 30;

                    var rList = new List<int>();
                    for (int t = 0; t < secretStr.Length; t++)
                    {
                        int c = (int)secretStr[t];
                        int xor = c ^ ((t % 33) + 9);
                        rList.Add(xor);
                    }
                    string rJoined = string.Join("", rList);
                    byte[] keyBytes = Encoding.UTF8.GetBytes(rJoined);

                    using var hmac = new HMACSHA1(keyBytes);
                    byte[] msg = new byte[8];
                    long val = step;
                    for (int i = 7; i >= 0; i--)
                    {
                        msg[i] = (byte)(val & 0xFF);
                        val >>= 8;
                    }
                    byte[] hash = hmac.ComputeHash(msg);
                    int offset = hash[hash.Length - 1] & 0x0F;
                    int binary = ((hash[offset] & 0x7F) << 24) |
                                 ((hash[offset + 1] & 0xFF) << 16) |
                                 ((hash[offset + 2] & 0xFF) << 8) |
                                 (hash[offset + 3] & 0xFF);
                    return (binary % 1000000).ToString("D6");
                }
                catch
                {
                    return null;
                }
            }

            private static async Task<string?> GetAccessTokenAsync(string spDcCookie, CancellationToken ct)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (_cachedAccessToken != null && now < _tokenExpiresAtMs - 30000)
                    return _cachedAccessToken;

                if (now < _retryTokenAfterMs)
                    return null;

                await _tokenLock.WaitAsync(ct);
                try
                {
                    now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (_cachedAccessToken != null && now < _tokenExpiresAtMs - 30000)
                        return _cachedAccessToken;

                    string? totp = GenerateTotp();
                    string url = $"{TokenUrl}?reason=init&productType=web-player";
                    if (!string.IsNullOrEmpty(totp))
                    {
                        url += $"&totp={totp}&totpServer={totp}&totpVer=61";
                    }

                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.UserAgent.ParseAdd(UserAgent);
                    req.Headers.Add("Cookie", $"sp_dc={spDcCookie}");
                    req.Headers.Add("Referer", "https://open.spotify.com/");
                    req.Headers.Add("App-Platform", "WebPlayer");

                    using var resp = await _httpClient.SendAsync(req, ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        _retryTokenAfterMs = now + 300000;
                        return null;
                    }

                    string json = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("accessToken", out var tokElem) &&
                        tokElem.GetString() is string token && !string.IsNullOrWhiteSpace(token))
                    {
                        _cachedAccessToken = token;
                        if (doc.RootElement.TryGetProperty("accessTokenExpirationTimestampMs", out var expElem) &&
                            expElem.TryGetInt64(out long expMs))
                        {
                            _tokenExpiresAtMs = expMs;
                        }
                        else
                        {
                            _tokenExpiresAtMs = now + 3600000;
                        }
                        return token;
                    }

                    _retryTokenAfterMs = now + 300000;
                    return null;
                }
                catch
                {
                    _retryTokenAfterMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 300000;
                    return null;
                }
                finally
                {
                    _tokenLock.Release();
                }
            }

            private static async Task<string?> GetJsonAsync(string url, string token, CancellationToken ct)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd(UserAgent);
                req.Headers.Add("Authorization", "Bearer " + token);
                using var resp = await _httpClient.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode)
                    return await resp.Content.ReadAsStringAsync(ct);
                return null;
            }
        }
    }
}

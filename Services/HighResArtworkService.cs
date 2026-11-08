using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace LmenV2.Services
{
    /// <summary>
    /// Fetches pristine, studio-quality album artwork (1200x1200px to 1400x1400px).
    /// Emulates the YouTube Music TV app and searches YouTube Music (WEB_REMIX / TVHTML5)
    /// and Apple Music/iTunes master CDN to replace low-res GSMTC thumbnails with crystal-clear masters.
    /// </summary>
    public sealed class HighResArtworkService
    {
        private static readonly Lazy<HighResArtworkService> _instance =
            new Lazy<HighResArtworkService>(() => new HighResArtworkService());
        public static HighResArtworkService Instance => _instance.Value;

        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(6)
        };

        // In-memory artwork cache by sanitized artist+title key
        private readonly ConcurrentDictionary<string, BitmapSource> _cache = new(StringComparer.OrdinalIgnoreCase);

        // Regex patterns to clean noise from YouTube track titles
        private static readonly Regex FluffRegex = new Regex(
            @"(?i)\s*[\(\[](?:Official\s*(?:Music\s*)?Video|Official\s*Audio|Lyric\s*Video|Audio|4K|Visualizer|Official\s*Visualizer|HD|HQ|Official|Full\s*Track|Video)[\)\]]",
            RegexOptions.Compiled);

        private static readonly Regex FeatParenRegex = new Regex(
            @"(?i)\s*[\(\[](?:feat\.|ft\.|featuring).*?[\)\]]",
            RegexOptions.Compiled);

        private static readonly Regex FeatSuffixRegex = new Regex(
            @"(?i)\s*(?:ft\.|feat\.|featuring).*$",
            RegexOptions.Compiled);

        private static readonly Regex RemasterRegex = new Regex(
            @"(?i)\s*-\s*\d{4}\s*Remaster.*$",
            RegexOptions.Compiled);

        private static readonly Regex SingleSuffixRegex = new Regex(
            @"(?i)\s*-\s*(?:Single|EP|Album)$",
            RegexOptions.Compiled);

        private static readonly Regex Lh3UrlRegex = new Regex(
            @"https://lh3\.googleusercontent\.com/[a-zA-Z0-9_\-]+",
            RegexOptions.Compiled);

        private static readonly Regex YtVideoIdRegex = new Regex(
            @"https://i\.ytimg\.com/vi/([a-zA-Z0-9_\-]{11})",
            RegexOptions.Compiled);

        static HighResArtworkService()
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
        }

        private HighResArtworkService() { }

        /// <summary>
        /// Retrieves high-resolution album artwork asynchronously.
        /// Returns cached or downloaded 1200px-1400px BitmapSource, or null if not found.
        /// </summary>
        public async Task<BitmapSource?> GetHighResArtworkAsync(
            string title,
            string artist,
            string albumTitle = "",
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(title) || title.Equals("Ready to Play", StringComparison.OrdinalIgnoreCase))
                return null;

            string cleanTitle = CleanTitle(title, out string? extractedArtist);
            string cleanArtist = CleanArtist(string.IsNullOrWhiteSpace(artist) ? extractedArtist ?? "" : artist);

            string cacheKey = $"{cleanArtist}|{cleanTitle}";
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            // Run iTunes studio master lookup and YouTube Music lookup concurrently for lowest latency
            Task<BitmapSource?> itunesTask = QueryItunesArtworkAsync(cleanTitle, cleanArtist, albumTitle, ct);
            Task<BitmapSource?> ytmTask = QueryYouTubeMusicArtworkAsync(cleanTitle, cleanArtist, ct);

            // 1. Prefer studio master 1400x1400 from Apple/iTunes if found
            try
            {
                var itunesBitmap = await itunesTask;
                if (itunesBitmap != null)
                {
                    _cache[cacheKey] = itunesBitmap;
                    return itunesBitmap;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HighResArtworkService] iTunes lookup error: {ex.Message}");
            }

            // 2. YouTube Music TV master (1200x1200px googleusercontent)
            try
            {
                var ytmBitmap = await ytmTask;
                if (ytmBitmap != null)
                {
                    _cache[cacheKey] = ytmBitmap;
                    return ytmBitmap;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HighResArtworkService] YouTube Music lookup error: {ex.Message}");
            }

            // 3. YouTube TV HTML5 MaxRes thumbnail fallback (1920x1080 / 1280x720)
            try
            {
                var ytTvBitmap = await QueryYouTubeTvArtworkAsync(cleanTitle, cleanArtist, ct);
                if (ytTvBitmap != null)
                {
                    _cache[cacheKey] = ytTvBitmap;
                    return ytTvBitmap;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HighResArtworkService] YouTube TV lookup error: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Queries YouTube Music WEB_REMIX API. Emulates YouTube Music TV client
        /// to retrieve high-res googleusercontent.com artwork (=w1200-h1200-l90-rj).
        /// </summary>
        private async Task<BitmapSource?> QueryYouTubeMusicArtworkAsync(string title, string artist, CancellationToken ct)
        {
            string query = string.IsNullOrWhiteSpace(artist) ? title : $"{artist} {title}";
            var requestBody = new
            {
                context = new
                {
                    client = new
                    {
                        clientName = "WEB_REMIX",
                        clientVersion = "1.20230405.01.00"
                    }
                },
                query = query
            };

            string jsonPayload = JsonSerializer.Serialize(requestBody);
            using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            using var resp = await _httpClient.PostAsync("https://music.youtube.com/youtubei/v1/search", content, ct);
            if (!resp.IsSuccessStatusCode) return null;

            string respJson = await resp.Content.ReadAsStringAsync(ct);

            // Look for Googleusercontent album cover
            var matchLh3 = Lh3UrlRegex.Match(respJson);
            if (matchLh3.Success)
            {
                // Upgrade to TV high-res master (1200x1200px)
                string highResUrl = $"{matchLh3.Value}=w1200-h1200-l90-rj";
                var bmp = await DownloadFrozenBitmapAsync(highResUrl, ct);
                if (bmp != null) return bmp;
            }

            // Look for YouTube video ID fallback
            var matchYt = YtVideoIdRegex.Match(respJson);
            if (matchYt.Success && matchYt.Groups.Count > 1)
            {
                string videoId = matchYt.Groups[1].Value;
                string maxResUrl = $"https://i.ytimg.com/vi/{videoId}/maxresdefault.jpg";
                var bmp = await DownloadFrozenBitmapAsync(maxResUrl, ct);
                if (bmp != null) return bmp;
            }

            return null;
        }

        /// <summary>
        /// Queries Apple Music / iTunes Store for studio master artwork up to 1400x1400.
        /// </summary>
        private async Task<BitmapSource?> QueryItunesArtworkAsync(string title, string artist, string album, CancellationToken ct)
        {
            string query = string.IsNullOrWhiteSpace(artist) ? title : $"{artist} {title}";
            string url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(query)}&media=music&limit=2";

            using var resp = await _httpClient.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;

            string json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
                return null;

            foreach (var item in results.EnumerateArray())
            {
                if (item.TryGetProperty("artworkUrl100", out var artElem))
                {
                    string? artUrl = artElem.GetString();
                    if (!string.IsNullOrEmpty(artUrl))
                    {
                        // Replace 100x100bb with studio master 1400x1400bb
                        string masterUrl = artUrl.Replace("100x100bb", "1400x1400bb");
                        var bmp = await DownloadFrozenBitmapAsync(masterUrl, ct);
                        if (bmp != null) return bmp;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Queries YouTube with TVHTML5 client for TV app thumbnails.
        /// </summary>
        private async Task<BitmapSource?> QueryYouTubeTvArtworkAsync(string title, string artist, CancellationToken ct)
        {
            string query = string.IsNullOrWhiteSpace(artist) ? title : $"{artist} {title}";
            var requestBody = new
            {
                context = new
                {
                    client = new
                    {
                        clientName = "TVHTML5",
                        clientVersion = "7.20230405.08.01"
                    }
                },
                query = query
            };

            string jsonPayload = JsonSerializer.Serialize(requestBody);
            using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            using var resp = await _httpClient.PostAsync("https://www.youtube.com/youtubei/v1/search", content, ct);
            if (!resp.IsSuccessStatusCode) return null;

            string respJson = await resp.Content.ReadAsStringAsync(ct);

            var matchYt = YtVideoIdRegex.Match(respJson);
            if (matchYt.Success && matchYt.Groups.Count > 1)
            {
                string videoId = matchYt.Groups[1].Value;
                // Try 1080p/720p MaxRes first
                string maxResUrl = $"https://i.ytimg.com/vi/{videoId}/maxresdefault.jpg";
                var bmp = await DownloadFrozenBitmapAsync(maxResUrl, ct);
                if (bmp != null) return bmp;

                // Fallback to HQ default
                string hqUrl = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";
                return await DownloadFrozenBitmapAsync(hqUrl, ct);
            }

            return null;
        }

        /// <summary>
        /// Downloads image bytes and creates an immutable, thread-safe frozen BitmapImage.
        /// </summary>
        private static async Task<BitmapSource?> DownloadFrozenBitmapAsync(string url, CancellationToken ct)
        {
            try
            {
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode) return null;

                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                if (bytes == null || bytes.Length < 1024) return null;

                var mem = new MemoryStream(bytes);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = mem;
                bmp.EndInit();
                bmp.Freeze();

                return bmp;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Cleans common clutter from track titles (e.g. "Song (Official Video) [4K]").
        /// Also extracts artist if title is formatted as "Artist - Title".
        /// </summary>
        public static string CleanTitle(string rawTitle, out string? extractedArtist)
        {
            extractedArtist = null;
            if (string.IsNullOrWhiteSpace(rawTitle)) return "";

            string s = rawTitle.Trim();

            // Check if artist is prepended e.g. "Artist - Song Title"
            int dashIdx = s.IndexOf(" - ", StringComparison.Ordinal);
            if (dashIdx > 0 && dashIdx < s.Length - 3)
            {
                string possibleArtist = s.Substring(0, dashIdx).Trim();
                string possibleTitle = s.Substring(dashIdx + 3).Trim();
                if (!string.IsNullOrWhiteSpace(possibleArtist) && !string.IsNullOrWhiteSpace(possibleTitle))
                {
                    extractedArtist = possibleArtist;
                    s = possibleTitle;
                }
            }

            s = FluffRegex.Replace(s, "");
            s = FeatParenRegex.Replace(s, "");
            s = FeatSuffixRegex.Replace(s, "");
            s = RemasterRegex.Replace(s, "");
            s = SingleSuffixRegex.Replace(s, "");

            return s.Trim(' ', '-', '—', '"', '\'');
        }

        /// <summary>
        /// Cleans artist name of junk like " - Topic" (YouTube auto-generated channel suffix).
        /// </summary>
        public static string CleanArtist(string rawArtist)
        {
            if (string.IsNullOrWhiteSpace(rawArtist)) return "";
            string s = rawArtist.Trim();
            if (s.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(0, s.Length - 8).Trim();
            }
            if (s.EndsWith("VEVO", StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(0, s.Length - 4).Trim();
            }
            return s;
        }
    }
}

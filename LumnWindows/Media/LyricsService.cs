using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace LumnWindows.Media;

public class LyricsLine
{
    public TimeSpan Time { get; set; }
    public string Text { get; set; } = string.Empty;
}

public class LyricsResult
{
    public string PlainLyrics { get; set; } = string.Empty;
    public List<LyricsLine> SyncedLines { get; set; } = new();
    public bool HasLyrics => !string.IsNullOrWhiteSpace(PlainLyrics) || SyncedLines.Count > 0;
}

public class LyricsService
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private static readonly Dictionary<string, LyricsResult> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<LyricsResult> FetchLyricsAsync(string title, string artist)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return new LyricsResult { PlainLyrics = "No song is currently playing." };
        }

        string cleanTitle = CleanTrackTitle(title);
        string cleanArtist = CleanArtistName(artist);
        string cacheKey = $"{cleanArtist} - {cleanTitle}";

        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        try
        {
            // 1. Try exact match on LRCLIB
            string url = $"https://lrclib.net/api/get?artist_name={Uri.EscapeDataString(cleanArtist)}&track_name={Uri.EscapeDataString(cleanTitle)}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "LumnWindows/3.0");

            var response = await _http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync();
                var result = ParseLrclibJson(json);
                if (result.HasLyrics)
                {
                    _cache[cacheKey] = result;
                    return result;
                }
            }

            // 2. Fallback to LRCLIB search query
            string searchUrl = $"https://lrclib.net/api/search?q={Uri.EscapeDataString(cleanTitle + " " + cleanArtist)}";
            var searchRequest = new HttpRequestMessage(HttpMethod.Get, searchUrl);
            searchRequest.Headers.Add("User-Agent", "LumnWindows/3.0");

            var searchResponse = await _http.SendAsync(searchRequest);
            if (searchResponse.IsSuccessStatusCode)
            {
                string searchJson = await searchResponse.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(searchJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                {
                    var firstItem = doc.RootElement[0];
                    string plain = firstItem.TryGetProperty("plainLyrics", out var pl) ? pl.GetString() ?? "" : "";
                    string synced = firstItem.TryGetProperty("syncedLyrics", out var sl) ? sl.GetString() ?? "" : "";

                    var result = ParseLyricsStrings(plain, synced);
                    if (result.HasLyrics)
                    {
                        _cache[cacheKey] = result;
                        return result;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LyricsService] Error: {ex.Message}");
        }

        var fallback = new LyricsResult
        {
            PlainLyrics = $"Lyrics not found for \"{cleanTitle}\".\n\nEnjoy the ambient music glow!"
        };
        _cache[cacheKey] = fallback;
        return fallback;
    }

    private static LyricsResult ParseLrclibJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string plain = root.TryGetProperty("plainLyrics", out var pl) ? pl.GetString() ?? "" : "";
            string synced = root.TryGetProperty("syncedLyrics", out var sl) ? sl.GetString() ?? "" : "";
            return ParseLyricsStrings(plain, synced);
        }
        catch
        {
            return new LyricsResult();
        }
    }

    private static LyricsResult ParseLyricsStrings(string plain, string synced)
    {
        var result = new LyricsResult { PlainLyrics = plain };

        if (!string.IsNullOrWhiteSpace(synced))
        {
            var lines = synced.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.StartsWith("[") && line.IndexOf(']') > 1)
                {
                    int endBracket = line.IndexOf(']');
                    string timeStr = line.Substring(1, endBracket - 1);
                    string text = line.Substring(endBracket + 1).Trim();

                    if (TryParseTimestamp(timeStr, out var ts))
                    {
                        result.SyncedLines.Add(new LyricsLine { Time = ts, Text = text });
                    }
                }
            }
        }

        return result;
    }

    private static bool TryParseTimestamp(string timeStr, out TimeSpan ts)
    {
        ts = TimeSpan.Zero;
        try
        {
            var parts = timeStr.Split(':');
            if (parts.Length == 2)
            {
                int min = int.Parse(parts[0]);
                double sec = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                ts = TimeSpan.FromMinutes(min) + TimeSpan.FromSeconds(sec);
                return true;
            }
        }
        catch { }
        return false;
    }

    private static string CleanTrackTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        // Strip (Official Music Video), [Lyric Video], etc.
        int paren = title.IndexOf('(');
        if (paren > 0) title = title.Substring(0, paren);
        int bracket = title.IndexOf('[');
        if (bracket > 0) title = title.Substring(0, bracket);
        int dash = title.IndexOf(" - ");
        if (dash > 0) title = title.Substring(0, dash);
        return title.Trim();
    }

    private static string CleanArtistName(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return "";
        int feat = artist.IndexOf("feat.", StringComparison.OrdinalIgnoreCase);
        if (feat > 0) artist = artist.Substring(0, feat);
        int ft = artist.IndexOf("ft.", StringComparison.OrdinalIgnoreCase);
        if (ft > 0) artist = artist.Substring(0, ft);
        return artist.Trim();
    }
}

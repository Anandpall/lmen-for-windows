using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Windows.Security.Credentials;

namespace LumnWindows.Media;

public class SpotifyTrackInfo
{
    public string TrackId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;          // primary artist
    public string[] AllArtists { get; set; } = Array.Empty<string>();    // all featured artists
    public string Album { get; set; } = string.Empty;
    public string AlbumArtUrl { get; set; } = string.Empty;     // 640x640 image URL
    public int DurationMs { get; set; }
    public int ProgressMs { get; set; }
    public bool IsPlaying { get; set; }
    public bool IsPodcast { get; set; }
    public string? PodcastShow { get; set; }

    // Audio features (fetched separately)
    public float Energy { get; set; }        // 0.0–1.0
    public float Valence { get; set; }       // 0.0–1.0
    public float Tempo { get; set; }         // BPM
    public float Danceability { get; set; }  // 0.0–1.0
    public float Loudness { get; set; }      // dB, typically -60 to 0
}

public class LyricLine
{
    public int StartTimeMs { get; set; }
    public string Words { get; set; } = string.Empty;
}

public class SpotifyService : IDisposable
{
    // ===== CONFIGURATION =====
    public const string DefaultRedirectUri = "http://127.0.0.1:5678/callback";
    private const string TokenVaultResource = "LumnWindows_Spotify";
    private const string TokenVaultUsername = "tokens";

    // ===== STATE =====
    private string _clientId = string.Empty;
    private string? _accessToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private string? _refreshToken;
    private readonly HttpClient _http = new();
    private CancellationTokenSource _cts = new();
    private System.Threading.Timer? _pollTimer;

    // ===== EVENTS =====
    public event Action<SpotifyTrackInfo>? TrackChanged;
    public event Action<SpotifyTrackInfo>? PlaybackStateChanged; // fires on poll
    public event Action<List<LyricLine>>? LyricsLoaded;
    public event Action? AuthRequired;
    public event Action<string>? StatusMessageChanged;

    private SpotifyTrackInfo? _lastTrack;

    public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken) || !string.IsNullOrEmpty(_refreshToken);
    public string ClientId => _clientId;

    // =========================================================
    // AUTH
    // =========================================================

    public async Task InitializeAsync(string? clientId = null)
    {
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            _clientId = clientId.Trim();
        }

        // Try to load saved tokens from Windows Credential Manager
        TryLoadSavedTokens();

        if (!string.IsNullOrEmpty(_refreshToken) && !string.IsNullOrEmpty(_clientId))
        {
            await RefreshAccessTokenAsync();
            StartPolling();
        }
        else
        {
            AuthRequired?.Invoke();
        }
    }

    public void SetClientId(string clientId)
    {
        _clientId = clientId.Trim();
    }

    public async Task StartAuthFlowAsync(string? clientId = null)
    {
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            _clientId = clientId.Trim();
        }

        if (string.IsNullOrWhiteSpace(_clientId))
        {
            StatusMessageChanged?.Invoke("Please enter a valid Spotify Client ID.");
            AuthRequired?.Invoke();
            return;
        }

        StatusMessageChanged?.Invoke("Opening browser to authorize Spotify...");

        // 1. Generate PKCE verifier + challenge
        string codeVerifier = GenerateCodeVerifier();
        string codeChallenge = GenerateCodeChallenge(codeVerifier);
        string state = Guid.NewGuid().ToString("N");

        // 2. Build auth URL
        string authUrl = "https://accounts.spotify.com/authorize?" +
            $"client_id={Uri.EscapeDataString(_clientId)}" +
            $"&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(DefaultRedirectUri)}" +
            $"&code_challenge_method=S256" +
            $"&code_challenge={codeChallenge}" +
            $"&state={state}" +
            $"&scope=user-read-playback-state%20user-modify-playback-state%20user-read-currently-playing";

        // 3. Open user browser
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = authUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open browser: {ex.Message}");
        }

        // 4. Start local HTTP listener for callback
        string code;
        try
        {
            code = await ListenForCallbackAsync(state);
        }
        catch (Exception ex)
        {
            StatusMessageChanged?.Invoke($"Authorization cancelled or failed: {ex.Message}");
            return;
        }

        // 5. Exchange code for tokens
        StatusMessageChanged?.Invoke("Connecting to Spotify account...");
        await ExchangeCodeForTokensAsync(code, codeVerifier);

        // 6. Save tokens
        SaveTokens();

        // 7. Start polling
        StatusMessageChanged?.Invoke("Connected to Spotify!");
        StartPolling();
    }

    private async Task<string> ListenForCallbackAsync(string expectedState)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:5678/");
        listener.Prefixes.Add("http://localhost:5678/");
        listener.Start();

        var context = await listener.GetContextAsync();
        var query = HttpUtility.ParseQueryString(context.Request.Url!.Query);

        // Friendly success page
        string html = "<html><body style='font-family:Segoe UI,sans-serif;text-align:center;padding:60px;background:#121212;color:#fff'>" +
            "<h2 style='color:#1DB954;'>Connected to Spotify</h2>" +
            "<p style='color:#b3b3b3;'>LumnWindows is now connected. You can close this tab and return to the app.</p>" +
            "</body></html>";
        byte[] responseBytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = responseBytes.Length;
        await context.Response.OutputStream.WriteAsync(responseBytes);
        context.Response.Close();
        listener.Stop();

        string? returnedState = query["state"];
        if (returnedState != expectedState)
            throw new InvalidOperationException("OAuth state mismatch — potential CSRF");

        return query["code"] ?? throw new InvalidOperationException("No code returned in callback");
    }

    private async Task ExchangeCodeForTokensAsync(string code, string codeVerifier)
    {
        var body = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = DefaultRedirectUri,
            ["client_id"] = _clientId,
            ["code_verifier"] = codeVerifier
        };

        var resp = await _http.PostAsync(
            "https://accounts.spotify.com/api/token",
            new FormUrlEncodedContent(body));
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        _accessToken = root.GetProperty("access_token").GetString();
        _refreshToken = root.GetProperty("refresh_token").GetString();
        _tokenExpiry = DateTime.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32() - 60);
    }

    private async Task RefreshAccessTokenAsync()
    {
        if (string.IsNullOrEmpty(_refreshToken) || string.IsNullOrEmpty(_clientId)) return;

        var body = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = _refreshToken,
            ["client_id"] = _clientId
        };

        try
        {
            var resp = await _http.PostAsync(
                "https://accounts.spotify.com/api/token",
                new FormUrlEncodedContent(body));
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            _accessToken = root.GetProperty("access_token").GetString();
            if (root.TryGetProperty("refresh_token", out var newRefresh))
                _refreshToken = newRefresh.GetString();
            _tokenExpiry = DateTime.UtcNow.AddSeconds(
                root.GetProperty("expires_in").GetInt32() - 60);
        }
        catch
        {
            // Refresh token expired or revoked
            _refreshToken = null;
            AuthRequired?.Invoke();
        }
    }

    private async Task EnsureTokenValidAsync()
    {
        if (DateTime.UtcNow >= _tokenExpiry)
            await RefreshAccessTokenAsync();
    }

    // =========================================================
    // POLLING — CURRENT PLAYBACK
    // =========================================================

    private void StartPolling()
    {
        _pollTimer?.Dispose();
        _pollTimer = new System.Threading.Timer(
            async _ => await PollPlaybackAsync(),
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1));
    }

    private async Task PollPlaybackAsync()
    {
        try
        {
            await EnsureTokenValidAsync();
            if (string.IsNullOrEmpty(_accessToken)) return;

            var req = new HttpRequestMessage(HttpMethod.Get,
                "https://api.spotify.com/v1/me/player?additional_types=track,episode");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

            var resp = await _http.SendAsync(req);

            // 204 = nothing playing / player idle
            if (resp.StatusCode == HttpStatusCode.NoContent)
            {
                if (_lastTrack != null && _lastTrack.IsPlaying)
                {
                    _lastTrack.IsPlaying = false;
                    PlaybackStateChanged?.Invoke(_lastTrack);
                }
                return;
            }

            if (!resp.IsSuccessStatusCode) return;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            var track = ParsePlaybackState(root);
            if (track == null) return;

            PlaybackStateChanged?.Invoke(track);

            // Fire TrackChanged only when the track actually changes
            if (track.TrackId != _lastTrack?.TrackId)
            {
                _lastTrack = track;
                TrackChanged?.Invoke(track);

                // Fetch audio features & timed lyrics in parallel on track change
                _ = Task.WhenAll(
                    FetchAndAttachAudioFeaturesAsync(track),
                    FetchLyricsAsync(track.TrackId)
                );
            }
            else
            {
                _lastTrack.ProgressMs = track.ProgressMs;
                _lastTrack.IsPlaying = track.IsPlaying;
            }
        }
        catch { /* transient network errors — retry next poll */ }
    }

    private static SpotifyTrackInfo? ParsePlaybackState(JsonElement root)
    {
        try
        {
            bool isPodcast = root.TryGetProperty("currently_playing_type", out var typeEl)
                && typeEl.GetString() == "episode";

            if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
                return null;

            string trackId = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            string title = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
            bool isPlaying = root.TryGetProperty("is_playing", out var playEl) && playEl.GetBoolean();
            int durationMs = item.TryGetProperty("duration_ms", out var durEl) ? durEl.GetInt32() : 0;
            int progressMs = root.TryGetProperty("progress_ms", out var progEl) ? progEl.GetInt32() : 0;

            string artist = "";
            string[] allArtists = Array.Empty<string>();
            string album = "";
            string albumArtUrl = "";
            string? podcastShow = null;

            if (isPodcast)
            {
                podcastShow = item.TryGetProperty("show", out var show) && show.TryGetProperty("name", out var sName)
                    ? sName.GetString() : null;
                artist = podcastShow ?? "";
                album = artist;

                if (item.TryGetProperty("images", out var epImages) && epImages.GetArrayLength() > 0)
                    albumArtUrl = epImages[0].GetProperty("url").GetString() ?? "";
            }
            else
            {
                if (item.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
                {
                    allArtists = artists.EnumerateArray()
                        .Select(a => a.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "")
                        .Where(n => !string.IsNullOrEmpty(n))
                        .ToArray();
                    artist = allArtists.Length > 0 ? allArtists[0] : "";
                }

                if (item.TryGetProperty("album", out var albumObj))
                {
                    album = albumObj.TryGetProperty("name", out var albName) ? albName.GetString() ?? "" : "";

                    if (albumObj.TryGetProperty("images", out var images) && images.GetArrayLength() > 0)
                    {
                        // Prefer high resolution 640x640
                        albumArtUrl = images[0].GetProperty("url").GetString() ?? "";
                    }
                }
            }

            return new SpotifyTrackInfo
            {
                TrackId = trackId,
                Title = title,
                Artist = artist,
                AllArtists = allArtists,
                Album = album,
                AlbumArtUrl = albumArtUrl,
                DurationMs = durationMs,
                ProgressMs = progressMs,
                IsPlaying = isPlaying,
                IsPodcast = isPodcast,
                PodcastShow = podcastShow
            };
        }
        catch
        {
            return null;
        }
    }

    // =========================================================
    // AUDIO FEATURES
    // =========================================================

    private async Task FetchAndAttachAudioFeaturesAsync(SpotifyTrackInfo track)
    {
        if (track.IsPodcast || string.IsNullOrEmpty(track.TrackId)) return;

        try
        {
            await EnsureTokenValidAsync();
            var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.spotify.com/v1/audio-features/{track.TrackId}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            track.Energy = root.TryGetProperty("energy", out var en) ? en.GetSingle() : 0.5f;
            track.Valence = root.TryGetProperty("valence", out var vl) ? vl.GetSingle() : 0.5f;
            track.Tempo = root.TryGetProperty("tempo", out var tp) ? tp.GetSingle() : 120.0f;
            track.Danceability = root.TryGetProperty("danceability", out var dc) ? dc.GetSingle() : 0.5f;
            track.Loudness = root.TryGetProperty("loudness", out var ld) ? ld.GetSingle() : -10.0f;

            TrackChanged?.Invoke(track);
        }
        catch { }
    }

    // =========================================================
    // TIMED LYRICS
    // =========================================================

    private async Task FetchLyricsAsync(string trackId)
    {
        var lyrics = await TryFetchSpotifyLyricsAsync(trackId);

        if (lyrics == null || lyrics.Count == 0)
            lyrics = await TryFetchLrcLibLyricsAsync(trackId);

        if (lyrics != null && lyrics.Count > 0)
            LyricsLoaded?.Invoke(lyrics);
    }

    private async Task<List<LyricLine>?> TryFetchSpotifyLyricsAsync(string trackId)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://spclient.wg.spotify.com/color-lyrics/v2/track/{trackId}?format=json&market=from_token");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            req.Headers.Add("App-Platform", "WebPlayer");

            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var lines = doc.RootElement
                .GetProperty("lyrics")
                .GetProperty("lines")
                .EnumerateArray()
                .Select(l => new LyricLine
                {
                    StartTimeMs = int.Parse(l.GetProperty("startTimeMs").GetString() ?? "0"),
                    Words = l.GetProperty("words").GetString() ?? ""
                })
                .ToList();

            return lines;
        }
        catch { return null; }
    }

    private async Task<List<LyricLine>?> TryFetchLrcLibLyricsAsync(string trackId)
    {
        if (_lastTrack == null || string.IsNullOrWhiteSpace(_lastTrack.Title)) return null;
        try
        {
            string url = $"https://lrclib.net/api/search?q={Uri.EscapeDataString(_lastTrack.Title + " " + _lastTrack.Artist)}";
            var resp = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(resp);
            var results = doc.RootElement.EnumerateArray().FirstOrDefault();

            if (results.ValueKind == JsonValueKind.Undefined) return null;

            string? syncedLyrics = results.TryGetProperty("syncedLyrics", out var sl)
                ? sl.GetString() : null;
            if (syncedLyrics == null) return null;

            var lines = new List<LyricLine>();
            foreach (var line in syncedLyrics.Split('\n'))
            {
                var match = Regex.Match(line, @"\[(\d+):(\d+\.\d+)\](.*)");
                if (!match.Success) continue;
                int minutes = int.Parse(match.Groups[1].Value);
                double seconds = double.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                int ms = (int)((minutes * 60 + seconds) * 1000);
                lines.Add(new LyricLine { StartTimeMs = ms, Words = match.Groups[3].Value.Trim() });
            }
            return lines;
        }
        catch { return null; }
    }

    // =========================================================
    // PLAYBACK CONTROLS
    // =========================================================

    public async Task PlayPauseAsync()
    {
        await EnsureTokenValidAsync();
        bool currentlyPlaying = _lastTrack?.IsPlaying ?? false;
        string endpoint = currentlyPlaying
            ? "https://api.spotify.com/v1/me/player/pause"
            : "https://api.spotify.com/v1/me/player/play";

        var req = new HttpRequestMessage(HttpMethod.Put, endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        req.Content = new StringContent("", Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(req);
        HandlePlaybackResponse(resp);
    }

    public async Task NextTrackAsync()
    {
        await EnsureTokenValidAsync();
        var req = new HttpRequestMessage(HttpMethod.Post,
            "https://api.spotify.com/v1/me/player/next");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        req.Content = new StringContent("");
        var resp = await _http.SendAsync(req);
        HandlePlaybackResponse(resp);
    }

    public async Task PreviousTrackAsync()
    {
        await EnsureTokenValidAsync();
        var req = new HttpRequestMessage(HttpMethod.Post,
            "https://api.spotify.com/v1/me/player/previous");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        req.Content = new StringContent("");
        var resp = await _http.SendAsync(req);
        HandlePlaybackResponse(resp);
    }

    public async Task SeekAsync(int positionMs)
    {
        await EnsureTokenValidAsync();
        var req = new HttpRequestMessage(HttpMethod.Put,
            $"https://api.spotify.com/v1/me/player/seek?position_ms={positionMs}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        req.Content = new StringContent("");
        var resp = await _http.SendAsync(req);
        HandlePlaybackResponse(resp);
    }

    public async Task SkipForward10Async()
    {
        int newPos = Math.Min(
            (_lastTrack?.ProgressMs ?? 0) + 10000,
            _lastTrack?.DurationMs ?? int.MaxValue);
        await SeekAsync(newPos);
    }

    public async Task SkipBack10Async()
    {
        int newPos = Math.Max((_lastTrack?.ProgressMs ?? 0) - 10000, 0);
        await SeekAsync(newPos);
    }

    private void HandlePlaybackResponse(HttpResponseMessage resp)
    {
        if (resp.StatusCode == HttpStatusCode.Forbidden)
        {
            StatusMessageChanged?.Invoke("⚠ Playback controls require Spotify Premium");
        }
        else if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            StatusMessageChanged?.Invoke("No active Spotify player found. Start playing in Spotify app.");
        }
    }

    // =========================================================
    // TOKEN PERSISTENCE
    // =========================================================

    private void SaveTokens()
    {
        if (_accessToken == null || _refreshToken == null) return;
        try
        {
            var vault = new PasswordVault();
            try { vault.Remove(vault.Retrieve(TokenVaultResource, TokenVaultUsername)); } catch { }
            var payload = JsonSerializer.Serialize(new
            {
                client_id = _clientId,
                access_token = _accessToken,
                refresh_token = _refreshToken,
                expiry = _tokenExpiry.ToBinary()
            });
            vault.Add(new PasswordCredential(TokenVaultResource, TokenVaultUsername, payload));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PasswordVault save failed: {ex.Message}");
        }
    }

    private void TryLoadSavedTokens()
    {
        try
        {
            var vault = new PasswordVault();
            var cred = vault.Retrieve(TokenVaultResource, TokenVaultUsername);
            cred.RetrievePassword();
            using var doc = JsonDocument.Parse(cred.Password);
            _accessToken = doc.RootElement.GetProperty("access_token").GetString();
            _refreshToken = doc.RootElement.GetProperty("refresh_token").GetString();
            if (doc.RootElement.TryGetProperty("client_id", out var cid))
            {
                string savedCid = cid.GetString() ?? "";
                if (!string.IsNullOrEmpty(savedCid) && string.IsNullOrEmpty(_clientId))
                {
                    _clientId = savedCid;
                }
            }
            _tokenExpiry = DateTime.FromBinary(doc.RootElement.GetProperty("expiry").GetInt64());
        }
        catch { }
    }

    // =========================================================
    // PKCE HELPERS
    // =========================================================

    private static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string GenerateCodeChallenge(string verifier)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Convert.ToBase64String(bytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public void Dispose()
    {
        _pollTimer?.Dispose();
        _cts.Cancel();
        _http.Dispose();
    }
}

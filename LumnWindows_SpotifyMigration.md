# LumnWindows — Change Request: Spotify API Migration + Bug Fixes

---

## Overview

This document outlines three things to be done:

1. **Migrate from YouTube Music / WebView2 to the Spotify Web API + Spotify Desktop App**
2. **Fix broken playback controls** (play/pause, next, previous, skip ±10s)
3. **Fix the album color extraction bug** (colors not auto-updating on track change)

---

## CHANGE 1 — Switch to Spotify API

### Why

The Spotify Web API gives us structured, reliable data that WebView2 DOM scraping never can:

- ✅ **Timed lyrics** (via Spotify's internal lyrics endpoint or MusiXMatch fallback)
- ✅ **Artist name, album name, album art URL** — clean, always present
- ✅ **Track duration, playback position** (for a real progress bar)
- ✅ **Is playing / shuffle / repeat state**
- ✅ **Audio features** per track: energy, valence, tempo, danceability, loudness
- ✅ **Queue / upcoming tracks**
- ✅ **Podcast support** (episode title, show name)

### How It Works

Spotify exposes two APIs we need:

| API | What it does |
|---|---|
| **Spotify Web API** (`api.spotify.com`) | REST — get current track, player state, audio features, search |
| **Spotify Web Playback SDK** | Browser-based playback (not needed — user plays from Spotify desktop app) |

We do **not** need to play music ourselves. The user plays music in the **Spotify Desktop App** as normal. We connect to the API purely to read what's playing and send playback commands.

### Auth Flow — PKCE (no client secret needed)

Use the **Authorization Code with PKCE** flow. This is the correct flow for desktop apps — no backend server needed.

```
Step 1: On first launch, open the user's browser to Spotify's auth URL
        https://accounts.spotify.com/authorize?
          client_id=YOUR_CLIENT_ID
          &response_type=code
          &redirect_uri=http://localhost:5678/callback
          &code_challenge_method=S256
          &code_challenge=GENERATED_CHALLENGE
          &scope=user-read-playback-state user-modify-playback-state user-read-currently-playing

Step 2: Start a local HTTP listener on localhost:5678
        Catch the redirect from Spotify with the auth code

Step 3: Exchange code for access_token + refresh_token
        POST https://accounts.spotify.com/api/token

Step 4: Store refresh_token in Windows Credential Manager (not plain text)
        Use it to silently refresh the access_token every 60 minutes
```

### Required OAuth Scopes

```
user-read-playback-state       — read current track, position, is_playing
user-modify-playback-state     — play, pause, skip, seek, volume
user-read-currently-playing    — current track metadata
streaming                      — (optional, only if you want Spotify SDK playback)
```

### Spotify App Registration

The agent needs to tell the user:

> Register a free app at https://developer.spotify.com/dashboard
> - App name: LumnWindows
> - Redirect URI: `http://localhost:5678/callback`
> - Copy the **Client ID** (no secret needed for PKCE)
> - Paste it into LumnWindows settings on first launch

---

### `SpotifyService.cs` — Full Implementation

Replace `YtmBridge.cs` and `ChromeYtmService.cs` entirely with this.

```csharp
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Windows.Security.Credentials; // For PasswordVault

public class SpotifyTrackInfo
{
    public string TrackId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";          // primary artist
    public string[] AllArtists { get; set; } = [];    // all featured artists
    public string Album { get; set; } = "";
    public string AlbumArtUrl { get; set; } = "";     // 640x640 image URL
    public int DurationMs { get; set; }
    public int ProgressMs { get; set; }
    public bool IsPlaying { get; set; }
    public bool IsPodcast { get; set; }
    public string? PodcastShow { get; set; }
    // Audio features (fetched separately)
    public float Energy { get; set; }        // 0.0–1.0
    public float Valence { get; set; }       // 0.0–1.0 (musical positiveness)
    public float Tempo { get; set; }         // BPM
    public float Danceability { get; set; }  // 0.0–1.0
    public float Loudness { get; set; }      // dB, typically -60 to 0
}

public class LyricLine
{
    public int StartTimeMs { get; set; }
    public string Words { get; set; } = "";
}

public class SpotifyService : IDisposable
{
    // ===== CONFIGURATION =====
    private const string ClientId = "YOUR_CLIENT_ID_HERE"; // filled from settings
    private const string RedirectUri = "http://localhost:5678/callback";
    private const string TokenVaultResource = "LumnWindows_Spotify";
    private const string TokenVaultUsername = "tokens";

    // ===== STATE =====
    private string? _accessToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private string? _refreshToken;
    private readonly HttpClient _http = new();
    private CancellationTokenSource _cts = new();
    private System.Threading.Timer? _pollTimer;

    // ===== EVENTS =====
    public event Action<SpotifyTrackInfo>? TrackChanged;
    public event Action<SpotifyTrackInfo>? PlaybackStateChanged; // fires every poll
    public event Action<List<LyricLine>>? LyricsLoaded;
    public event Action? AuthRequired;

    private SpotifyTrackInfo? _lastTrack;

    // =========================================================
    // AUTH
    // =========================================================

    public async Task InitializeAsync(string? clientId = null)
    {
        if (clientId != null && clientId != ClientId)
        {
            // Save to settings — agent should wire this up
        }

        // Try to load saved tokens from Windows Credential Manager
        TryLoadSavedTokens();

        if (_refreshToken != null)
        {
            await RefreshAccessTokenAsync();
            StartPolling();
        }
        else
        {
            AuthRequired?.Invoke();
        }
    }

    public async Task StartAuthFlowAsync()
    {
        // 1. Generate PKCE verifier + challenge
        string codeVerifier = GenerateCodeVerifier();
        string codeChallenge = GenerateCodeChallenge(codeVerifier);
        string state = Guid.NewGuid().ToString("N");

        // 2. Build auth URL
        string authUrl = "https://accounts.spotify.com/authorize?" +
            $"client_id={ClientId}" +
            $"&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            $"&code_challenge_method=S256" +
            $"&code_challenge={codeChallenge}" +
            $"&state={state}" +
            $"&scope=user-read-playback-state%20user-modify-playback-state%20user-read-currently-playing";

        // 3. Open browser
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = authUrl,
            UseShellExecute = true
        });

        // 4. Start local HTTP listener for callback
        string code = await ListenForCallbackAsync(state);

        // 5. Exchange code for tokens
        await ExchangeCodeForTokensAsync(code, codeVerifier);

        // 6. Save tokens
        SaveTokens();

        // 7. Start polling
        StartPolling();
    }

    private async Task<string> ListenForCallbackAsync(string expectedState)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:5678/");
        listener.Start();

        var context = await listener.GetContextAsync();
        var query = HttpUtility.ParseQueryString(context.Request.Url!.Query);

        // Send a nice success page to the browser
        string html = "<html><body style='font-family:sans-serif;text-align:center;padding:60px'>" +
            "<h2>✅ Connected to Spotify</h2><p>You can close this tab.</p></body></html>";
        byte[] responseBytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentLength64 = responseBytes.Length;
        await context.Response.OutputStream.WriteAsync(responseBytes);
        context.Response.Close();
        listener.Stop();

        string? returnedState = query["state"];
        if (returnedState != expectedState)
            throw new InvalidOperationException("OAuth state mismatch — possible CSRF");

        return query["code"] ?? throw new InvalidOperationException("No code in callback");
    }

    private async Task ExchangeCodeForTokensAsync(string code, string codeVerifier)
    {
        var body = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = ClientId,
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
        if (_refreshToken == null) return;

        var body = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = _refreshToken,
            ["client_id"] = ClientId
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
            // Refresh token expired — need re-auth
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
        // Poll every 1 second for playback state
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

            var req = new HttpRequestMessage(HttpMethod.Get,
                "https://api.spotify.com/v1/me/player?additional_types=track,episode");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

            var resp = await _http.SendAsync(req);

            // 204 = nothing playing
            if (resp.StatusCode == System.Net.HttpStatusCode.NoContent) return;
            if (!resp.IsSuccessStatusCode) return;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            var track = ParsePlaybackState(root);
            PlaybackStateChanged?.Invoke(track);

            // Fire TrackChanged only when the track actually changes
            if (track.TrackId != _lastTrack?.TrackId)
            {
                _lastTrack = track;
                TrackChanged?.Invoke(track);

                // Fetch audio features + lyrics in parallel on track change
                _ = Task.WhenAll(
                    FetchAndAttachAudioFeaturesAsync(track),
                    FetchLyricsAsync(track.TrackId)
                );
            }
        }
        catch { /* network error — retry next poll */ }
    }

    private static SpotifyTrackInfo ParsePlaybackState(JsonElement root)
    {
        bool isPodcast = root.TryGetProperty("currently_playing_type", out var typeEl)
            && typeEl.GetString() == "episode";

        var item = root.GetProperty("item");

        string trackId = item.GetProperty("id").GetString() ?? "";
        string title = item.GetProperty("name").GetString() ?? "";
        bool isPlaying = root.GetProperty("is_playing").GetBoolean();
        int durationMs = item.GetProperty("duration_ms").GetInt32();
        int progressMs = root.TryGetProperty("progress_ms", out var prog)
            ? prog.GetInt32() : 0;

        string artist = "";
        string[] allArtists = [];
        string album = "";
        string albumArtUrl = "";
        string? podcastShow = null;

        if (isPodcast)
        {
            // Episode
            podcastShow = item.TryGetProperty("show", out var show)
                ? show.GetProperty("name").GetString() : null;
            artist = podcastShow ?? "";
            album = artist;

            // Podcast thumbnail
            if (item.TryGetProperty("images", out var epImages) &&
                epImages.GetArrayLength() > 0)
                albumArtUrl = epImages[0].GetProperty("url").GetString() ?? "";
        }
        else
        {
            // Track
            var artists = item.GetProperty("artists");
            allArtists = artists.EnumerateArray()
                .Select(a => a.GetProperty("name").GetString() ?? "")
                .ToArray();
            artist = allArtists.Length > 0 ? allArtists[0] : "";

            var albumObj = item.GetProperty("album");
            album = albumObj.GetProperty("name").GetString() ?? "";

            // Prefer 640x640 image
            if (albumObj.TryGetProperty("images", out var images) &&
                images.GetArrayLength() > 0)
                albumArtUrl = images[0].GetProperty("url").GetString() ?? "";
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

    // =========================================================
    // AUDIO FEATURES (energy, tempo, valence, etc.)
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

            track.Energy = root.GetProperty("energy").GetSingle();
            track.Valence = root.GetProperty("valence").GetSingle();
            track.Tempo = root.GetProperty("tempo").GetSingle();
            track.Danceability = root.GetProperty("danceability").GetSingle();
            track.Loudness = root.GetProperty("loudness").GetSingle();

            // Re-fire TrackChanged with the now-enriched track data
            TrackChanged?.Invoke(track);
        }
        catch { }
    }

    // =========================================================
    // TIMED LYRICS
    // =========================================================

    // Spotify has an internal lyrics API (used in the desktop/web app).
    // It is unofficial but widely used. If it stops working, fall back
    // to lrclib.net which is a free open lyrics API with timestamps.

    private async Task FetchLyricsAsync(string trackId)
    {
        // Strategy 1: Spotify internal lyrics endpoint
        var lyrics = await TryFetchSpotifyLyricsAsync(trackId);

        // Strategy 2: lrclib.net fallback
        if (lyrics == null || lyrics.Count == 0)
            lyrics = await TryFetchLrcLibLyricsAsync(trackId);

        if (lyrics != null && lyrics.Count > 0)
            LyricsLoaded?.Invoke(lyrics);
    }

    private async Task<List<LyricLine>?> TryFetchSpotifyLyricsAsync(string trackId)
    {
        // Spotify internal lyrics endpoint — requires the access token from Web API auth.
        // As of 2025 this endpoint is still active and returns synced lyrics for most tracks.
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
        // Use last known track title + artist to search lrclib
        if (_lastTrack == null) return null;
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

            // Parse LRC format: [mm:ss.xx] lyric line
            var lines = new List<LyricLine>();
            foreach (var line in syncedLyrics.Split('\n'))
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    line, @"\[(\d+):(\d+\.\d+)\](.*)");
                if (!match.Success) continue;
                int minutes = int.Parse(match.Groups[1].Value);
                double seconds = double.Parse(match.Groups[2].Value,
                    System.Globalization.CultureInfo.InvariantCulture);
                int ms = (int)((minutes * 60 + seconds) * 1000);
                lines.Add(new LyricLine { StartTimeMs = ms, Words = match.Groups[3].Value.Trim() });
            }
            return lines;
        }
        catch { return null; }
    }

    // =========================================================
    // PLAYBACK CONTROLS — These all call the Spotify Web API
    // =========================================================

    // IMPORTANT: These commands require Spotify Premium.
    // They send commands to whatever active Spotify device is playing.
    // The user just needs the Spotify desktop app open and playing.

    public async Task PlayPauseAsync()
    {
        await EnsureTokenValidAsync();
        bool currentlyPlaying = _lastTrack?.IsPlaying ?? false;
        string endpoint = currentlyPlaying
            ? "https://api.spotify.com/v1/me/player/pause"
            : "https://api.spotify.com/v1/me/player/play";
        string method = currentlyPlaying ? "PUT" : "PUT";

        var req = new HttpRequestMessage(new HttpMethod("PUT"), endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        req.Content = new StringContent("", Encoding.UTF8, "application/json");
        await _http.SendAsync(req);
    }

    public async Task NextTrackAsync()
    {
        await EnsureTokenValidAsync();
        var req = new HttpRequestMessage(HttpMethod.Post,
            "https://api.spotify.com/v1/me/player/next");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        req.Content = new StringContent("");
        await _http.SendAsync(req);
    }

    public async Task PreviousTrackAsync()
    {
        await EnsureTokenValidAsync();
        var req = new HttpRequestMessage(HttpMethod.Post,
            "https://api.spotify.com/v1/me/player/previous");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        req.Content = new StringContent("");
        await _http.SendAsync(req);
    }

    public async Task SeekAsync(int positionMs)
    {
        await EnsureTokenValidAsync();
        var req = new HttpRequestMessage(HttpMethod.Put,
            $"https://api.spotify.com/v1/me/player/seek?position_ms={positionMs}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        req.Content = new StringContent("");
        await _http.SendAsync(req);
    }

    // Skip forward 10 seconds
    public async Task SkipForward10Async()
    {
        int newPos = Math.Min(
            (_lastTrack?.ProgressMs ?? 0) + 10000,
            _lastTrack?.DurationMs ?? int.MaxValue);
        await SeekAsync(newPos);
    }

    // Skip backward 10 seconds
    public async Task SkipBack10Async()
    {
        int newPos = Math.Max((_lastTrack?.ProgressMs ?? 0) - 10000, 0);
        await SeekAsync(newPos);
    }

    // =========================================================
    // TOKEN PERSISTENCE — Windows Credential Manager
    // =========================================================

    private void SaveTokens()
    {
        if (_accessToken == null || _refreshToken == null) return;
        var vault = new PasswordVault();
        try { vault.Remove(vault.Retrieve(TokenVaultResource, TokenVaultUsername)); } catch { }
        var payload = JsonSerializer.Serialize(new
        {
            access_token = _accessToken,
            refresh_token = _refreshToken,
            expiry = _tokenExpiry.ToBinary()
        });
        vault.Add(new PasswordCredential(TokenVaultResource, TokenVaultUsername, payload));
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
```

---

## BUG FIX 1 — Playback Buttons Not Working

### Root Cause

The old implementation used `WebView2.ExecuteScriptAsync()` to click DOM buttons on the YouTube Music page. This is inherently fragile:
- DOM selectors break whenever YTM updates its HTML
- JS execution is async and timing-dependent
- The WebView2 page must be focused and fully loaded
- Clicking hidden elements silently fails

### Fix

With Spotify API, **all playback controls become direct HTTP calls** to `api.spotify.com`. These are in `SpotifyService.cs` above. Wire them up like this:

```csharp
// In PlayerViewModel.cs — replace ALL old command implementations:

[RelayCommand]
private async Task PlayPause() => await _spotify.PlayPauseAsync();

[RelayCommand]
private async Task Next() => await _spotify.NextTrackAsync();

[RelayCommand]
private async Task Previous() => await _spotify.PreviousTrackAsync();

[RelayCommand]
private async Task SkipForward() => await _spotify.SkipForward10Async();

[RelayCommand]
private async Task SkipBack() => await _spotify.SkipBack10Async();
```

Button XAML — make sure each button is bound to the right command:

```xml
<!-- Replay 10 seconds back -->
<Button Command="{Binding SkipBackCommand}" ToolTip="Back 10s">
    <TextBlock Text="⏮10"/>
</Button>

<!-- Previous track -->
<Button Command="{Binding PreviousCommand}" ToolTip="Previous">
    <Path Data="M6 6h2v12H6zm3.5 6 8.5 6V6z" Fill="White"/>
</Button>

<!-- Play / Pause -->
<Button Command="{Binding PlayPauseCommand}">
    <ContentControl Content="{Binding IsPlaying,
        Converter={StaticResource PlayPauseIconConverter}}"/>
</Button>

<!-- Next track -->
<Button Command="{Binding NextCommand}" ToolTip="Next">
    <Path Data="M6 18l8.5-6L6 6v12zM16 6v12h2V6h-2z" Fill="White"/>
</Button>

<!-- Skip 10 seconds forward -->
<Button Command="{Binding SkipForwardCommand}" ToolTip="Forward 10s">
    <TextBlock Text="10⏭"/>
</Button>
```

Also make sure **no button has `IsEnabled="False"`** or is covered by another element — use the WPF inspector (Snoop) to verify hit-testing is working if a button still fails.

---

## BUG FIX 2 — Album Colors Not Updating Automatically

### Root Cause

There are two separate bugs here:

**Bug A — Color extraction only runs once at startup, not on track change.**
The `AlbumArtColorExtractor` is called at app start but not re-called when `TrackChanged` fires. The event subscription is missing or wired up after the first track has already loaded.

**Bug B — The extracted palette is not invalidating / replacing the previous one properly.**
When `UpdatePalette()` is called on the renderer, `_paletteBlend` is reset to `0f` but if `_targetPalette` is null or empty (because the download failed silently), it falls back to the old palette. The bug manifests as: switching to Custom Colors forces a reset of internal state, which clears the stale reference and makes the auto-mode "work again" on the next track.

### Fix — Re-extract colors on every TrackChanged event

```csharp
// In App.xaml.cs (or wherever SpotifyService is wired up):

_spotify.TrackChanged += async track =>
{
    // Update player UI
    Application.Current.Dispatcher.Invoke(() =>
    {
        _playerViewModel.TrackTitle = track.Title;
        _playerViewModel.TrackArtist = track.Artist;
        _playerViewModel.AlbumArtUrl = track.AlbumArtUrl;
        _playerViewModel.IsPlaying = track.IsPlaying;
    });

    // *** FIX: Always re-extract colors on EVERY track change ***
    if (!string.IsNullOrEmpty(track.AlbumArtUrl) && _settings.ColorMode == "AlbumArt")
    {
        var colors = await AlbumArtColorExtractor.ExtractFromUrlAsync(track.AlbumArtUrl);

        if (colors != null && colors.Count > 0)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                // Update the live palette dots in the UI
                _playerViewModel.PaletteColor1 = colors.Count > 0 ? colors[0] : Colors.Transparent;
                _playerViewModel.PaletteColor2 = colors.Count > 1 ? colors[1] : Colors.Transparent;
                _playerViewModel.PaletteColor3 = colors.Count > 2 ? colors[2] : Colors.Transparent;

                // Push to renderer — THIS is where the old code was missing
                _overlayRenderer.UpdatePalette(colors);
            });
        }
    }
};
```

### Fix — Make color extraction more robust

```csharp
// In AlbumArtColorExtractor.cs:
// Add a retry + null guard so silent failures don't leave the old palette active

public static async Task<List<Color>?> ExtractFromUrlAsync(string url, int retries = 2)
{
    for (int attempt = 0; attempt <= retries; attempt++)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var bytes = await _http.GetByteArrayAsync(url, cts.Token);
            if (bytes.Length == 0) continue;

            using var ms = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(ms,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            var colors = ExtractDominantColors(decoder.Frames[0], colorCount: 3);

            // Validate — reject if all colors are near-black (failed extraction)
            bool valid = colors.Any(c => c.R + c.G + c.B > 80);
            if (valid) return colors;
        }
        catch
        {
            if (attempt < retries)
                await Task.Delay(500); // brief pause before retry
        }
    }

    return null; // Caller should keep previous palette if null
}
```

### Fix — Don't replace palette with null

```csharp
// In EdgeGlowRenderer.cs — guard UpdatePalette:
public void UpdatePalette(List<Color>? newPalette)
{
    // *** FIX: Never replace a valid palette with null or empty ***
    if (newPalette == null || newPalette.Count == 0) return;

    _targetPalette = newPalette;
    _paletteBlend = 0f; // triggers cross-fade
}
```

---

## WHAT TO REMOVE

Delete or fully replace these files — they are no longer needed:

- `Media/YtmBridge.cs` — replaced by `SpotifyService.cs`
- `Media/ChromeYtmService.cs` — replaced by `SpotifyService.cs`
- The entire WebView2 control and its NuGet package — Spotify desktop app handles playback
- The Chrome launch / CDP port logic in `App.xaml.cs`

---

## UPDATED PLAYER WINDOW (no WebView2)

Without the embedded browser, `PlayerWindow.xaml` becomes a clean, compact media player:

```
┌─────────────────────────────────────┐
│  ●  ●  ●                            │  ← traffic lights
│                                     │
│         ┌─────────────┐             │
│         │  Album Art  │             │  ← 280×280 rounded image
│         └─────────────┘             │
│                                     │
│         Track Title                 │  ← bound to SpotifyTrackInfo.Title
│         Artist · Album              │  ← Artist + Album on one line
│                                     │
│    ━━━━━━━━━━━━━━━━━━━━━━━━━━━━    │  ← progress bar (driven by ProgressMs poll)
│    0:42                      3:21   │  ← elapsed / total
│                                     │
│  ⏮10   ⏮   ▶/⏸   ⏭   10⏭       │  ← playback controls
│                                     │
│  🎤 Lyrics                          │  ← opens lyrics panel (timed, synced)
│                                     │
│  🎨  〰  💡  Settings  🖥  📺  📱  │  ← toolbar
└─────────────────────────────────────┘
```

The lyrics panel (shown when "🎤 Lyrics" is clicked) is a scrollable list of `LyricLine` items. The currently active line — determined by comparing `ProgressMs` to `StartTimeMs` — is highlighted in white. All other lines are dimmed (`#888888`). Auto-scroll keeps the active line centered.

---

## UPDATED DATA FLOW

```
[Spotify Desktop App — user plays music normally]
        ↓ audio output → Windows audio device
[WASAPI Loopback — captures whatever plays through speakers]
        ↓ float[] samples every ~10 ms
[SpectralAnalyser — FFT 2048, hop 512]
        ↓ SpectralFrame (6 bands + onset)
[EdgeGlowRenderer — 60 fps]
        ↓ animated edge glow on screen border

[SpotifyService — polls api.spotify.com every 1 second]
        ↓ SpotifyTrackInfo on every poll
        ├── PlaybackStateChanged → progress bar, play/pause icon
        └── TrackChanged (new song detected)
                ├── PlayerViewModel → title, artist, album, album art image
                ├── AlbumArtColorExtractor.ExtractFromUrlAsync()
                │       ↓ k-means on 640×640 thumbnail
                │       └── EdgeGlowRenderer.UpdatePalette() ← ALWAYS called
                └── FetchLyricsAsync()
                        ├── Try Spotify spclient lyrics endpoint
                        └── Fallback: lrclib.net
                        └── LyricsLoaded → LyricsPanel updates line list

[Playback buttons → SpotifyService]
        PlayPause()    → PUT /v1/me/player/play or /pause
        Next()         → POST /v1/me/player/next
        Previous()     → POST /v1/me/player/previous
        SkipForward10()→ PUT /v1/me/player/seek?position_ms=currentPos+10000
        SkipBack10()   → PUT /v1/me/player/seek?position_ms=currentPos-10000
```

---

## FIRST-RUN SETUP EXPERIENCE

On first launch (no saved tokens):

1. Show a setup screen in the player window:
   ```
   ┌──────────────────────────────────┐
   │  Connect Spotify                 │
   │                                  │
   │  Paste your Client ID:           │
   │  [________________________]      │
   │                                  │
   │  [ Connect to Spotify → ]        │
   │                                  │
   │  Register at:                    │
   │  developer.spotify.com/dashboard │
   │  Redirect URI: localhost:5678    │
   └──────────────────────────────────┘
   ```
2. User clicks "Connect to Spotify" → browser opens to Spotify auth page
3. User grants permission → browser redirects to localhost:5678
4. App catches the token, saves to Windows Credential Manager
5. Player starts showing the current track immediately

On subsequent launches: silently load saved tokens and start polling with no visible auth step.

---

## SPOTIFY PREMIUM NOTE

The playback control API endpoints (`/play`, `/pause`, `/next`, `/previous`, `/seek`) **require Spotify Premium**. Read-only endpoints (currently playing, audio features, lyrics) work with free accounts.

Add a clear note in the UI if a 403 response is received from a playback control endpoint:

```csharp
if (resp.StatusCode == HttpStatusCode.Forbidden)
{
    // Show in the player window status bar:
    // "⚠ Playback controls require Spotify Premium"
    _playerViewModel.StatusMessage = "⚠ Playback controls require Spotify Premium";
}
```

---

## AGENT CHECKLIST

### Remove
- [ ] Delete `YtmBridge.cs`
- [ ] Delete `ChromeYtmService.cs`
- [ ] Remove `Microsoft.Web.WebView2` NuGet package
- [ ] Remove WebView2 XAML control from `PlayerWindow.xaml`
- [ ] Remove all Chrome/CDP launch logic from `App.xaml.cs`

### Add
- [ ] Add `SpotifyService.cs` (full implementation above)
- [ ] Add `SpotifyTrackInfo` and `LyricLine` model classes
- [ ] Add `LyricsPanel.xaml` — scrollable lyric lines, active line highlighted
- [ ] Register Spotify app at developer.spotify.com and store Client ID in settings
- [ ] Add first-run setup screen to `PlayerWindow.xaml`
- [ ] Implement PKCE auth flow with local HTTP listener
- [ ] Save/load tokens via Windows Credential Manager (`PasswordVault`)

### Fix playback controls
- [ ] Replace all `ExecuteScriptAsync` button clicks with `SpotifyService` method calls
- [ ] Verify XAML `Command=` bindings are correct for all 5 buttons
- [ ] Add 403 Premium warning to status bar

### Fix album colors
- [ ] Subscribe to `SpotifyService.TrackChanged` and always call `AlbumArtColorExtractor.ExtractFromUrlAsync()`
- [ ] Guard `UpdatePalette()` to reject null/empty palette (never clear a valid palette)
- [ ] Add retry logic (2 retries, 500 ms apart) to `ExtractFromUrlAsync()`
- [ ] Verify `_overlayRenderer.UpdatePalette()` is actually called after extraction completes

### New features enabled by Spotify API
- [ ] **Progress bar** — bind to `ProgressMs` / `DurationMs`, update every poll
- [ ] **Timed lyrics panel** — auto-scroll, highlight active line by `ProgressMs`
- [ ] **Audio features display** — optionally show energy/tempo/valence in settings
- [ ] **All artists** — show featured artists ("feat. X, Y") below main artist name
- [ ] **Album name** — show below artist
- [ ] **Podcast support** — show episode title + show name when a podcast is playing

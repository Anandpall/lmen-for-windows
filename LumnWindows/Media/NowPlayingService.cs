using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace LumnWindows.Media;

public class NowPlayingInfo
{
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string AlbumTitle { get; set; } = string.Empty;
    public BitmapSource? AlbumArt { get; set; }
    public bool IsPlaying { get; set; }
    public string SourceApp { get; set; } = string.Empty;
    public TimeSpan Position { get; set; } = TimeSpan.Zero;
    public TimeSpan Duration { get; set; } = TimeSpan.Zero;
}

public class NowPlayingService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _activeSession;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private CancellationTokenSource? _retryCts;
    private System.Threading.Timer? _progressTimer;

    public NowPlayingInfo CurrentInfo { get; private set; } = new();

    public event Action<NowPlayingInfo>? MediaStateChanged;
    public event Action<TimeSpan, TimeSpan>? TimelineChanged;

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_manager != null)
            {
                _manager.CurrentSessionChanged += OnSessionChanged;
                _manager.SessionsChanged += OnSessionsChanged;
                await RefreshActiveSessionAsync();
            }

            // Start 500ms timeline sync timer
            _progressTimer = new System.Threading.Timer(OnTimerTick, null, 500, 500);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error starting NowPlayingService: {ex.Message}");
        }
    }

    private void OnTimerTick(object? state)
    {
        if (_activeSession == null || !CurrentInfo.IsPlaying) return;

        try
        {
            var timeline = _activeSession.GetTimelineProperties();
            if (timeline != null && timeline.EndTime > TimeSpan.Zero)
            {
                CurrentInfo.Position = timeline.Position;
                CurrentInfo.Duration = timeline.EndTime;
            }
            else if (CurrentInfo.Duration > TimeSpan.Zero)
            {
                CurrentInfo.Position += TimeSpan.FromMilliseconds(500);
                if (CurrentInfo.Position > CurrentInfo.Duration)
                    CurrentInfo.Position = CurrentInfo.Duration;
            }

            TimelineChanged?.Invoke(CurrentInfo.Position, CurrentInfo.Duration);
        }
        catch { }
    }

    private async void OnSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        await RefreshActiveSessionAsync();
    }

    private async void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        await RefreshActiveSessionAsync();
    }

    public async Task RefreshActiveSessionAsync()
    {
        if (_manager == null) return;

        await _lock.WaitAsync();
        try
        {
            var sessions = _manager.GetSessions().ToList();

            // Prioritize Spotify Desktop, then actively playing sessions, then any other music app
            GlobalSystemMediaTransportControlsSession? bestSession = null;

            // 1. Actively playing Spotify
            bestSession = sessions.FirstOrDefault(s =>
                IsSpotify(s) &&
                s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);

            // 2. Any actively playing media player
            bestSession ??= sessions.FirstOrDefault(s =>
                s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);

            // 3. Any Spotify session (even if paused)
            bestSession ??= sessions.FirstOrDefault(IsSpotify);

            // 4. Default current session from Windows
            bestSession ??= _manager.GetCurrentSession();

            // 5. Any session available
            bestSession ??= sessions.FirstOrDefault();

            if (_activeSession != bestSession)
            {
                if (_activeSession != null)
                {
                    _activeSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                    _activeSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                    _activeSession.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
                }

                _activeSession = bestSession;

                if (_activeSession != null)
                {
                    _activeSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
                    _activeSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
                    _activeSession.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
                }
            }

            await FetchMediaInfoWithRetryAsync(_activeSession);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error refreshing session: {ex.Message}");
        }
        finally
        {
            _lock.Release();
        }
    }

    private static bool IsSpotify(GlobalSystemMediaTransportControlsSession s)
    {
        string id = s.SourceAppUserModelId ?? "";
        return id.Contains("spotify", StringComparison.OrdinalIgnoreCase);
    }

    private async void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        await FetchMediaInfoWithRetryAsync(sender);
    }

    private async void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        await FetchMediaInfoWithRetryAsync(sender);
    }

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        try
        {
            var timeline = sender.GetTimelineProperties();
            if (timeline != null)
            {
                CurrentInfo.Position = timeline.Position;
                CurrentInfo.Duration = timeline.EndTime;
                TimelineChanged?.Invoke(CurrentInfo.Position, CurrentInfo.Duration);
            }
        }
        catch { }
    }

    private async Task FetchMediaInfoWithRetryAsync(GlobalSystemMediaTransportControlsSession? session)
    {
        _retryCts?.Cancel();
        _retryCts = new CancellationTokenSource();
        var token = _retryCts.Token;

        bool hasThumbnail = await FetchMediaInfoInternalAsync(session);

        if (!hasThumbnail && session != null)
        {
            try
            {
                await Task.Delay(300, token);
                if (!token.IsCancellationRequested)
                {
                    hasThumbnail = await FetchMediaInfoInternalAsync(session);
                }

                if (!hasThumbnail && !token.IsCancellationRequested)
                {
                    await Task.Delay(600, token);
                    if (!token.IsCancellationRequested)
                    {
                        await FetchMediaInfoInternalAsync(session);
                    }
                }
            }
            catch (TaskCanceledException) { }
        }
    }

    private async Task<bool> FetchMediaInfoInternalAsync(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session == null)
        {
            CurrentInfo = new NowPlayingInfo { IsPlaying = false };
            MediaStateChanged?.Invoke(CurrentInfo);
            return false;
        }

        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            bool isPlaying = playbackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var props = await session.TryGetMediaPropertiesAsync();
            var timeline = session.GetTimelineProperties();

            TimeSpan pos = timeline?.Position ?? TimeSpan.Zero;
            TimeSpan dur = timeline?.EndTime ?? TimeSpan.Zero;

            if (props == null)
            {
                CurrentInfo = new NowPlayingInfo
                {
                    IsPlaying = isPlaying,
                    SourceApp = session.SourceAppUserModelId ?? "",
                    Position = pos,
                    Duration = dur
                };
                MediaStateChanged?.Invoke(CurrentInfo);
                return false;
            }

            BitmapSource? albumArt = null;
            if (props.Thumbnail != null)
            {
                albumArt = await LoadThumbnailAsync(props.Thumbnail);
            }

            string sourceName = session.SourceAppUserModelId ?? "";
            if (sourceName.Contains("spotify", StringComparison.OrdinalIgnoreCase))
                sourceName = "Spotify Desktop";
            else if (sourceName.Contains("chrome", StringComparison.OrdinalIgnoreCase))
                sourceName = "Chrome / YouTube Music";
            else if (sourceName.Contains("msedge", StringComparison.OrdinalIgnoreCase))
                sourceName = "Edge";

            var updated = new NowPlayingInfo
            {
                Title = props.Title ?? string.Empty,
                Artist = props.Artist ?? props.AlbumArtist ?? string.Empty,
                AlbumTitle = props.AlbumTitle ?? string.Empty,
                AlbumArt = albumArt ?? CurrentInfo.AlbumArt,
                IsPlaying = isPlaying,
                SourceApp = sourceName,
                Position = pos,
                Duration = dur
            };

            CurrentInfo = updated;
            MediaStateChanged?.Invoke(updated);
            TimelineChanged?.Invoke(pos, dur);
            return albumArt != null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error fetching media info: {ex.Message}");
            return false;
        }
    }

    // =========================================================
    // NATIVE WINDOWS PLAYBACK CONTROLS (Works on Spotify Free!)
    // =========================================================

    public async Task<bool> PlayPauseAsync()
    {
        if (_activeSession == null) return false;
        try
        {
            return await _activeSession.TryTogglePlayPauseAsync();
        }
        catch { return false; }
    }

    public async Task<bool> NextTrackAsync()
    {
        if (_activeSession == null) return false;
        try
        {
            return await _activeSession.TrySkipNextAsync();
        }
        catch { return false; }
    }

    public async Task<bool> PreviousTrackAsync()
    {
        if (_activeSession == null) return false;
        try
        {
            return await _activeSession.TrySkipPreviousAsync();
        }
        catch { return false; }
    }

    public async Task<bool> SeekAsync(TimeSpan targetPosition)
    {
        if (_activeSession == null) return false;
        try
        {
            return await _activeSession.TryChangePlaybackPositionAsync((long)targetPosition.Ticks);
        }
        catch { return false; }
    }

    public async Task<bool> SkipForward10Async()
    {
        var target = CurrentInfo.Position + TimeSpan.FromSeconds(10);
        if (CurrentInfo.Duration > TimeSpan.Zero && target > CurrentInfo.Duration)
            target = CurrentInfo.Duration;
        return await SeekAsync(target);
    }

    public async Task<bool> SkipBack10Async()
    {
        var target = CurrentInfo.Position - TimeSpan.FromSeconds(10);
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        return await SeekAsync(target);
    }

    private static async Task<BitmapSource?> LoadThumbnailAsync(IRandomAccessStreamReference thumbnailRef)
    {
        try
        {
            using var stream = await thumbnailRef.OpenReadAsync();
            if (stream == null || stream.Size == 0) return null;

            // First attempt: Standard WPF BitmapImage
            try
            {
                using var memoryStream = new MemoryStream();
                using (var netStream = stream.AsStreamForRead())
                {
                    await netStream.CopyToAsync(memoryStream);
                }
                memoryStream.Position = 0;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = memoryStream;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                // Second attempt: WinRT BitmapDecoder
                stream.Seek(0);
                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                var pixelData = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    new BitmapTransform(),
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb);

                byte[] pixels = pixelData.DetachPixelData();
                int width = (int)decoder.PixelWidth;
                int height = (int)decoder.PixelHeight;

                BitmapSource? result = null;
                if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var wb = new WriteableBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
                        wb.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
                        wb.Freeze();
                        result = wb;
                    });
                }
                else
                {
                    var wb = new WriteableBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
                    wb.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
                    wb.Freeze();
                    result = wb;
                }

                return result;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load thumbnail: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        _progressTimer?.Dispose();
        _retryCts?.Cancel();
        _retryCts?.Dispose();
        _lock.Dispose();
    }
}

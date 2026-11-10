using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;
using LmenV2.Models;

namespace LmenV2.Services
{
    public class MediaSessionService : IDisposable
    {
        private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;
        private System.Windows.Threading.DispatcherTimer? _sessionPollTimer;

        // Fast track skipping fires several MediaPropertiesChanged events in a row. Each one
        // kicks off an async refresh that awaits artwork, so the responses can come back out
        // of order and an older track's colours would land on top of the newest one. Only the
        // most recent request is allowed to publish.
        private int _refreshVersion;

        public event Action<MediaTrackInfo>? TrackUpdated;
        public event Action<TimeSpan, TimeSpan>? TimelineUpdated;
        public event Action<bool>? PlaybackStateChanged;

        public MediaTrackInfo CurrentTrack { get; private set; } = new MediaTrackInfo();

        public async Task InitializeAsync()
        {
            try
            {
                _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                if (_sessionManager != null)
                {
                    _sessionManager.CurrentSessionChanged += OnCurrentSessionChanged;
                    _sessionManager.SessionsChanged += OnSessionsChanged;
                    UpdateCurrentSession(_sessionManager.GetCurrentSession());
                }

                // Periodic check for players (BitChord, web browsers) that don't reliably signal CurrentSessionChanged
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    _sessionPollTimer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromSeconds(2.0)
                    };
                    _sessionPollTimer.Tick += (s, e) => PollActiveSession();
                    _sessionPollTimer.Start();
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MediaSessionService] Initialization error: {ex.Message}");
            }
        }

        private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        {
            UpdateCurrentSession(sender.GetCurrentSession());
        }

        private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
        {
            UpdateCurrentSession(sender.GetCurrentSession());
        }

        private void PollActiveSession()
        {
            if (_sessionManager == null) return;
            try
            {
                var cur = _sessionManager.GetCurrentSession();
                if (cur == null)
                {
                    var sessions = _sessionManager.GetSessions();
                    cur = sessions.FirstOrDefault(s => s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                       ?? sessions.FirstOrDefault();
                }

                if (!ReferenceEquals(cur, _currentSession))
                {
                    UpdateCurrentSession(cur);
                }
            }
            catch { }
        }

        private void UpdateCurrentSession(GlobalSystemMediaTransportControlsSession? session)
        {
            if (session == null && _sessionManager != null)
            {
                try
                {
                    var sessions = _sessionManager.GetSessions();
                    session = sessions.FirstOrDefault(s => s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                           ?? sessions.FirstOrDefault();
                }
                catch { }
            }

            if (_currentSession != null)
            {
                try
                {
                    _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                    _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                    _currentSession.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
                }
                catch { }
            }

            _currentSession = session;

            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
                _currentSession.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
            }

            _ = RefreshTrackInfoAsync();
        }

        private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        {
            _ = RefreshTrackInfoAsync();
        }

        private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            RefreshPlaybackState();
        }

        private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
        {
            RefreshTimelineState();
        }

        private void RefreshTimelineState()
        {
            if (_currentSession == null) return;
            try
            {
                var timeline = _currentSession.GetTimelineProperties();
                if (timeline != null)
                {
                    CurrentTrack.Position = timeline.Position;
                    CurrentTrack.Duration = timeline.EndTime > timeline.StartTime 
                        ? (timeline.EndTime - timeline.StartTime) 
                        : TimeSpan.Zero;

                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        TimelineUpdated?.Invoke(CurrentTrack.Position, CurrentTrack.Duration);
                    });
                }
            }
            catch { }
        }

        public async Task RefreshTrackInfoAsync()
        {
            int version = System.Threading.Interlocked.Increment(ref _refreshVersion);

            if (_currentSession == null)
            {
                SetDefaultTrack();
                return;
            }

            try
            {
                var session = _currentSession;
                var mediaProps = await session.TryGetMediaPropertiesAsync();
                var playbackInfo = session.GetPlaybackInfo();
                var timeline = session.GetTimelineProperties();
                TimeSpan position = timeline?.Position ?? TimeSpan.Zero;
                TimeSpan duration = timeline != null && timeline.EndTime > timeline.StartTime 
                    ? (timeline.EndTime - timeline.StartTime) 
                    : TimeSpan.Zero;

                if (mediaProps == null)
                {
                    SetDefaultTrack();
                    return;
                }

                BitmapImage? bitmap = null;
                if (mediaProps.Thumbnail != null)
                {
                    try
                    {
                        using var stream = await mediaProps.Thumbnail.OpenReadAsync();
                        using var netStream = stream.AsStreamForRead();
                        using var memStream = new MemoryStream();
                        await netStream.CopyToAsync(memStream);
                        memStream.Position = 0;

                        bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = memStream;
                        bitmap.EndInit();
                        bitmap.Freeze();
                        App.Log($"[MediaSessionService] Thumbnail loaded successfully ({bitmap.PixelWidth}x{bitmap.PixelHeight})");
                    }
                    catch (Exception ex)
                    {
                        App.Log($"[MediaSessionService] Thumbnail load failed: {ex.Message}");
                    }
                }
                else
                {
                    App.Log("[MediaSessionService] GSMTC mediaProps.Thumbnail is null (waiting for HighResArtworkService fallback)");
                }

                // The session may have been switched or cleared while we were awaiting.
                if (version != System.Threading.Volatile.Read(ref _refreshVersion)) return;
                if (!ReferenceEquals(session, _currentSession)) return;

                var palette = ColorExtractionService.ExtractPalette(bitmap);

                bool isPlaying = playbackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var controls = playbackInfo?.Controls;

                var newTrack = new MediaTrackInfo
                {
                    Title = string.IsNullOrWhiteSpace(mediaProps.Title) ? "Unknown Track" : mediaProps.Title,
                    Artist = string.IsNullOrWhiteSpace(mediaProps.Artist) ? "Unknown Artist" : mediaProps.Artist,
                    AlbumTitle = mediaProps.AlbumTitle ?? string.Empty,
                    Thumbnail = bitmap,
                    IsPlaying = isPlaying,
                    CanPlay = controls?.IsPlayEnabled ?? true,
                    CanPause = controls?.IsPauseEnabled ?? true,
                    CanSkipNext = controls?.IsNextEnabled ?? true,
                    CanSkipPrevious = controls?.IsPreviousEnabled ?? true,
                    Position = position,
                    Duration = duration,
                    DominantColor = palette.DominantColor,
                    AccentColor = palette.AccentColor,
                    AmbientColor = palette.AmbientColor,
                    DarkVignetteColor = palette.DarkVignetteColor
                };

                CurrentTrack = newTrack;
                App.Log($"[MediaSessionService] Track updated: '{newTrack.Title}' - '{newTrack.Artist}' (Playing: {newTrack.IsPlaying})");

                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    TrackUpdated?.Invoke(CurrentTrack);
                });

                // Asynchronously fetch studio-quality / TV master artwork from YouTube Music / Apple Music
                if (!string.IsNullOrWhiteSpace(newTrack.Title) && newTrack.Title != "Ready to Play" && newTrack.Title != "Unknown Track")
                {
                    _ = FetchHighResArtworkAsync(version, newTrack.Title, newTrack.Artist, newTrack.AlbumTitle);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MediaSessionService] Refresh error: {ex.Message}");
            }
        }

        private async Task FetchHighResArtworkAsync(int version, string title, string artist, string albumTitle)
        {
            try
            {
                var highRes = await HighResArtworkService.Instance.GetHighResArtworkAsync(title, artist, albumTitle);
                if (highRes == null) return;

                // Ensure user didn't switch tracks while high-res artwork was downloading
                if (version != System.Threading.Volatile.Read(ref _refreshVersion)) return;

                // Re-extract palette from the studio master for richest color vibrancy
                var hdPalette = ColorExtractionService.ExtractPalette(highRes);

                CurrentTrack.Thumbnail = highRes;
                CurrentTrack.DominantColor = hdPalette.DominantColor;
                CurrentTrack.AccentColor = hdPalette.AccentColor;
                CurrentTrack.AmbientColor = hdPalette.AmbientColor;
                CurrentTrack.DarkVignetteColor = hdPalette.DarkVignetteColor;

                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (version == System.Threading.Volatile.Read(ref _refreshVersion))
                    {
                        TrackUpdated?.Invoke(CurrentTrack);
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MediaSessionService] HighRes fetch failed: {ex.Message}");
            }
        }

        private void RefreshPlaybackState()
        {
            if (_currentSession == null) return;

            try
            {
                var playbackInfo = _currentSession.GetPlaybackInfo();
                if (playbackInfo != null)
                {
                    bool isPlaying = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    if (CurrentTrack.IsPlaying != isPlaying)
                    {
                        CurrentTrack.IsPlaying = isPlaying;

                        Application.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            PlaybackStateChanged?.Invoke(isPlaying);
                        });
                    }
                }
            }
            catch { }
        }

        private void SetDefaultTrack()
        {
            var palette = ColorExtractionService.GetDefaultPalette();
            CurrentTrack = new MediaTrackInfo
            {
                Title = "Ready to Play",
                Artist = "Start music in Spotify, Chrome, or Media Player",
                AlbumTitle = string.Empty,
                Thumbnail = null,
                IsPlaying = false,
                DominantColor = palette.DominantColor,
                AccentColor = palette.AccentColor,
                AmbientColor = palette.AmbientColor,
                DarkVignetteColor = palette.DarkVignetteColor
            };

            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                TrackUpdated?.Invoke(CurrentTrack);
            });
        }

        public async Task<bool> TogglePlayPauseAsync()
        {
            if (_currentSession == null) return false;
            try
            {
                var info = _currentSession.GetPlaybackInfo();
                if (info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    return await _currentSession.TryPauseAsync();
                }
                else
                {
                    return await _currentSession.TryPlayAsync();
                }
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> PauseAsync()
        {
            if (_currentSession == null) return false;
            try
            {
                return await _currentSession.TryPauseAsync();
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> PlayAsync()
        {
            if (_currentSession == null) return false;
            try
            {
                return await _currentSession.TryPlayAsync();
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> SkipNextAsync()
        {
            if (_currentSession == null) return false;
            try
            {
                return await _currentSession.TrySkipNextAsync();
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> SkipPreviousAsync()
        {
            if (_currentSession == null) return false;
            try
            {
                return await _currentSession.TrySkipPreviousAsync();
            }
            catch
            {
                return false;
            }
        }

        private readonly System.Threading.SemaphoreSlim _seekLock = new System.Threading.SemaphoreSlim(1, 1);

        public async Task<bool> ChangePlaybackPositionAsync(TimeSpan position)
        {
            if (_currentSession == null) return false;
            if (!await _seekLock.WaitAsync(1000)) return false;

            try
            {
                if (position < TimeSpan.Zero) position = TimeSpan.Zero;
                if (CurrentTrack.Duration > TimeSpan.Zero && position > CurrentTrack.Duration)
                {
                    position = CurrentTrack.Duration;
                }

                long ticks = position.Ticks;
                return await _currentSession.TryChangePlaybackPositionAsync(ticks);
            }
            catch
            {
                return false;
            }
            finally
            {
                _seekLock.Release();
            }
        }

        public async Task<bool> ToggleShuffleAsync()
        {
            if (_currentSession == null) return false;
            try
            {
                var pb = _currentSession.GetPlaybackInfo();
                bool isShuffle = pb?.IsShuffleActive ?? false;
                return await _currentSession.TryChangeShuffleActiveAsync(!isShuffle);
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> ToggleRepeatAsync()
        {
            if (_currentSession == null) return false;
            try
            {
                var pb = _currentSession.GetPlaybackInfo();
                var currentMode = pb?.AutoRepeatMode ?? Windows.Media.MediaPlaybackAutoRepeatMode.None;
                var nextMode = currentMode switch
                {
                    Windows.Media.MediaPlaybackAutoRepeatMode.None => Windows.Media.MediaPlaybackAutoRepeatMode.List,
                    Windows.Media.MediaPlaybackAutoRepeatMode.List => Windows.Media.MediaPlaybackAutoRepeatMode.Track,
                    _ => Windows.Media.MediaPlaybackAutoRepeatMode.None
                };
                return await _currentSession.TryChangeAutoRepeatModeAsync(nextMode);
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            _sessionPollTimer?.Stop();

            if (_sessionManager != null)
            {
                _sessionManager.CurrentSessionChanged -= OnCurrentSessionChanged;
                _sessionManager.SessionsChanged -= OnSessionsChanged;
            }

            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                _currentSession.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
            }

            _seekLock.Dispose();
        }
    }
}

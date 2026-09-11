using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LumnWindows.Media;
using LumnWindows.Settings;

namespace LumnWindows.Player;

public partial class PlayerWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly SettingsPopover _settingsPopover;
    private readonly NowPlayingService _nowPlaying;
    private readonly LyricsService _lyricsService = new();

    private bool _isLyricsOpen = false;
    private bool _isDraggingProgress = false;
    private string _lastTrackTitle = string.Empty;
    private string _lastTrackArtist = string.Empty;
    private List<LyricsLine> _currentLyrics = new();
    private TextBlock? _activeLyricBlock;

    public event Action<AppSettings>? SettingsChanged;

    public PlayerWindow(
        AppSettings settings,
        SettingsService settingsService,
        SettingsPopover settingsPopover,
        NowPlayingService nowPlaying)
    {
        _settings = settings;
        _settingsService = settingsService;
        _settingsPopover = settingsPopover;
        _nowPlaying = nowPlaying;

        InitializeComponent();

        InlineBrightnessSlider.Value = _settings.GlowBrightness;
        UpdateBeatSyncVisual(_settings.BeatSyncEnabled);

        _nowPlaying.MediaStateChanged += OnMediaStateChanged;
        _nowPlaying.TimelineChanged += OnTimelineChanged;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_nowPlaying.CurrentInfo.Title))
        {
            OnMediaStateChanged(_nowPlaying.CurrentInfo);
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Minimize/Hide to system tray instead of exiting application
        e.Cancel = true;
        Hide();
    }

    // =========================================================
    // WINDOW DRAG & WINDOWS CAPTION CONTROLS
    // =========================================================

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d)
        {
            if (FindVisualParent<Button>(d) != null ||
                FindVisualParent<Slider>(d) != null ||
                FindVisualParent<ScrollBar>(d) != null ||
                FindVisualParent<TextBox>(d) != null)
            {
                return;
            }
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch { }
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T parent) return parent;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        // Close hides the window to tray; ambient glow continues running
        Hide();
    }

    // =========================================================
    // MEDIA STATE SYNC (Windows SMTC - Spotify Desktop & all players)
    // =========================================================

    private void OnMediaStateChanged(NowPlayingInfo info)
    {
        Dispatcher.Invoke(() =>
        {
            bool hasTrack = !string.IsNullOrWhiteSpace(info.Title);
            TrackTitleText.Text = hasTrack ? info.Title : "Waiting for Music...";

            string defaultArtist = "Play any song in Spotify Desktop, YouTube, or browser";
            TrackArtistText.Text = string.IsNullOrWhiteSpace(info.Artist) ? defaultArtist : info.Artist;

            string sourceName = string.IsNullOrWhiteSpace(info.SourceApp) ? "Spotify & Windows Media" : info.SourceApp;
            StatusLabel.Text = sourceName;
            StatusIndicatorText.Text = hasTrack ? $" • {sourceName}" : " • Ready";

            PlayPauseBtn.Content = info.IsPlaying ? "⏸" : "▶";

            if (info.AlbumArt != null)
            {
                AlbumArtImage.Source = info.AlbumArt;
            }

            // If track changed, fetch synced lyrics
            if (hasTrack && (_lastTrackTitle != info.Title || _lastTrackArtist != info.Artist))
            {
                _lastTrackTitle = info.Title;
                _lastTrackArtist = info.Artist;
                _ = FetchLyricsAsync(info.Title, info.Artist);
            }
        });
    }

    private void OnTimelineChanged(TimeSpan pos, TimeSpan dur)
    {
        Dispatcher.Invoke(() =>
        {
            if (!_isDraggingProgress)
            {
                if (dur > TimeSpan.Zero)
                {
                    TrackProgressSlider.Maximum = dur.TotalSeconds;
                    TrackProgressSlider.Value = Math.Clamp(pos.TotalSeconds, 0, dur.TotalSeconds);
                    DurationTimeText.Text = FormatTime(dur);
                }
                ProgressTimeText.Text = FormatTime(pos);
            }

            UpdateActiveLyricLine(pos);
        });
    }

    private static string FormatTime(TimeSpan time)
    {
        int totalSeconds = Math.Max(0, (int)time.TotalSeconds);
        int minutes = totalSeconds / 60;
        int seconds = totalSeconds % 60;
        return $"{minutes}:{seconds:D2}";
    }

    // =========================================================
    // PLAYBACK CONTROLS (Native Windows - Works on Spotify Free!)
    // =========================================================

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        await _nowPlaying.PlayPauseAsync();
    }

    private async void PreviousTrack_Click(object sender, RoutedEventArgs e)
    {
        await _nowPlaying.PreviousTrackAsync();
    }

    private async void NextTrack_Click(object sender, RoutedEventArgs e)
    {
        await _nowPlaying.NextTrackAsync();
    }

    private async void SkipBack10_Click(object sender, RoutedEventArgs e)
    {
        await _nowPlaying.SkipBack10Async();
    }

    private async void SkipForward10_Click(object sender, RoutedEventArgs e)
    {
        await _nowPlaying.SkipForward10Async();
    }

    // =========================================================
    // PROGRESS BAR & SEEK
    // =========================================================

    private void ProgressSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingProgress = true;
    }

    private async void ProgressSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingProgress = false;
        double targetSec = TrackProgressSlider.Value;
        await _nowPlaying.SeekAsync(TimeSpan.FromSeconds(targetSec));
    }

    private void ProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isDraggingProgress)
        {
            ProgressTimeText.Text = FormatTime(TimeSpan.FromSeconds(e.NewValue));
        }
    }

    // =========================================================
    // SYNCED LYRICS (Powered by LRCLIB - Free & Open)
    // =========================================================

    private void LyricsToggle_Click(object sender, RoutedEventArgs e)
    {
        _isLyricsOpen = !_isLyricsOpen;
        if (_isLyricsOpen)
        {
            ArtworkBorder.Visibility = Visibility.Collapsed;
            LyricsPanel.Visibility = Visibility.Visible;
            LyricsToggleBtn.Background = new SolidColorBrush(Color.FromRgb(50, 50, 50));
        }
        else
        {
            LyricsPanel.Visibility = Visibility.Collapsed;
            ArtworkBorder.Visibility = Visibility.Visible;
            LyricsToggleBtn.Background = new SolidColorBrush(Color.FromRgb(37, 37, 37));
        }
    }

    private async Task FetchLyricsAsync(string title, string artist)
    {
        try
        {
            Dispatcher.Invoke(() =>
            {
                LyricsStatusText.Text = "Searching...";
            });

            var result = await _lyricsService.FetchLyricsAsync(title, artist);

            Dispatcher.Invoke(() =>
            {
                _currentLyrics = result.SyncedLines ?? new List<LyricsLine>();
                LyricsLinesContainer.Children.Clear();
                _activeLyricBlock = null;

                if (_currentLyrics.Count > 0)
                {
                    LyricsStatusText.Text = $"{_currentLyrics.Count} synced lines";

                    foreach (var line in _currentLyrics)
                    {
                        var tb = new TextBlock
                        {
                            Text = line.Text,
                            FontSize = 14,
                            FontWeight = FontWeights.Normal,
                            Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                            TextAlignment = TextAlignment.Center,
                            Margin = new Thickness(0, 6, 0, 6),
                            Tag = line.Time,
                            Cursor = Cursors.Hand,
                            TextWrapping = TextWrapping.Wrap,
                            MaxWidth = 440
                        };

                        // Clicking a lyric line jumps directly to that timestamp
                        tb.MouseLeftButtonDown += async (s, ev) =>
                        {
                            if (s is TextBlock b && b.Tag is TimeSpan time)
                            {
                                await _nowPlaying.SeekAsync(time);
                            }
                        };

                        LyricsLinesContainer.Children.Add(tb);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(result.PlainLyrics))
                {
                    LyricsStatusText.Text = "Plain lyrics";
                    var tb = new TextBlock
                    {
                        Text = result.PlainLyrics,
                        Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                        FontSize = 14,
                        TextAlignment = TextAlignment.Center,
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 440,
                        Margin = new Thickness(0, 16, 0, 16)
                    };
                    LyricsLinesContainer.Children.Add(tb);
                }
                else
                {
                    LyricsStatusText.Text = "No lyrics found";
                    var tb = new TextBlock
                    {
                        Text = "No synced lyrics available for this song.",
                        Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)),
                        FontSize = 14,
                        TextAlignment = TextAlignment.Center,
                        Margin = new Thickness(0, 40, 0, 0)
                    };
                    LyricsLinesContainer.Children.Add(tb);
                }
            });
        }
        catch
        {
            Dispatcher.Invoke(() =>
            {
                LyricsStatusText.Text = "Unavailable";
            });
        }
    }

    private void UpdateActiveLyricLine(TimeSpan currentPos)
    {
        if (_currentLyrics == null || _currentLyrics.Count == 0 || LyricsLinesContainer.Children.Count == 0)
            return;

        int activeIdx = -1;
        for (int i = 0; i < _currentLyrics.Count; i++)
        {
            if (_currentLyrics[i].Time <= currentPos)
            {
                activeIdx = i;
            }
            else
            {
                break;
            }
        }

        if (activeIdx < 0 || activeIdx >= LyricsLinesContainer.Children.Count)
            return;

        if (LyricsLinesContainer.Children[activeIdx] is TextBlock currentBlock)
        {
            if (_activeLyricBlock != currentBlock)
            {
                // Reset previous active line
                if (_activeLyricBlock != null)
                {
                    _activeLyricBlock.Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 100));
                    _activeLyricBlock.FontSize = 14;
                    _activeLyricBlock.FontWeight = FontWeights.Normal;
                }

                // Highlight new active line
                _activeLyricBlock = currentBlock;
                _activeLyricBlock.Foreground = new SolidColorBrush(Color.FromRgb(255, 255, 255));
                _activeLyricBlock.FontSize = 16;
                _activeLyricBlock.FontWeight = FontWeights.Bold;

                // Smoothly bring active line into view
                currentBlock.BringIntoView();
            }
        }
    }

    // =========================================================
    // BOTTOM TOOLBAR CONTROLS
    // =========================================================

    private void Palette_Click(object sender, RoutedEventArgs e)
    {
        _settings.OverrideAlbumColor = true;
        _settingsPopover.ShowNear(GetToolbarAnchor());
    }

    private void Waveform_Click(object sender, RoutedEventArgs e)
    {
        _settings.BeatSyncEnabled = !_settings.BeatSyncEnabled;
        UpdateBeatSyncVisual(_settings.BeatSyncEnabled);
        _settingsService.Save(_settings);
        SettingsChanged?.Invoke(_settings);
    }

    private void UpdateBeatSyncVisual(bool enabled)
    {
        WaveformIcon.Foreground = enabled
            ? new SolidColorBrush(Color.FromRgb(0, 229, 255))
            : new SolidColorBrush(Color.FromRgb(100, 100, 100));
    }

    private void Bulb_Click(object sender, RoutedEventArgs e)
    {
        InlineBrightnessPanel.Visibility = InlineBrightnessPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void InlineBrightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _settings.GlowBrightness = e.NewValue;
        _settings.MasterOpacity = e.NewValue / 100.0;
        SettingsChanged?.Invoke(_settings);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        _settingsPopover.ShowNear(GetToolbarAnchor());
    }

    private void Display_Click(object sender, RoutedEventArgs e)
    {
        _settingsPopover.ShowNear(GetToolbarAnchor());
    }

    private Point GetToolbarAnchor()
    {
        Point pt = SettingsBtn.PointToScreen(new Point(0, 0));
        return new Point(pt.X + SettingsBtn.ActualWidth / 2, pt.Y);
    }

    public NowPlayingService NowPlayingService => _nowPlaying;
}



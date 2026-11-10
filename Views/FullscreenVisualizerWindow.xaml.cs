using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Documents;
using LmenV2.Models;
using LmenV2.Rendering;
using LmenV2.Services;

namespace LmenV2.Views
{
    public partial class FullscreenVisualizerWindow : Window
    {
        private readonly MediaSessionService _mediaService;
        private readonly AudioReactiveService _audioService;
        private readonly SettingsService _settings = SettingsService.Instance;

        private bool _audioReactivityEnabled = true;

        // Same screen-edge beam as the desktop overlay, so both modes share one look.
        private readonly EdgeBeam _beam;

        private double _currentSpeedPx = 80.0;
        private double _breathPhase = 0.0;
        private double _beatExcitement = 0.0;

        // Playback progress & glitch-free timeline seeking
        private bool _isPlaying = false;
        private TimeSpan _currentTrackPosition = TimeSpan.Zero;
        private TimeSpan _currentTrackDuration = TimeSpan.Zero;
        private bool _isUserSeeking = false;
        private bool _wasPlayingBeforeSeek = false;
        private int _seekSequence = 0;
        private TimeSpan _seekTargetTime = TimeSpan.Zero;
        private DateTime _ignoreStaleTimelineUntil = DateTime.MinValue;

        /// <summary>
        /// BitChord manual-sync offset: the lyric clock runs ahead of (or behind)
        /// the transport by the configured offset, and lyric-initiated seeks
        /// compensate in the opposite direction.
        /// </summary>
        private TimeSpan LyricClockPosition => _currentTrackPosition - TimeSpan.FromMilliseconds(SettingsService.Instance.LyricsOffsetMs);
        private static TimeSpan ToTrackPosition(TimeSpan lyricTime) => lyricTime + TimeSpan.FromMilliseconds(SettingsService.Instance.LyricsOffsetMs);

        // TV ambient background drifting motion (continuous multi-harmonic false-motion aesthetic)
        private double _backdropMotionTime = 0.0;
        private bool _activeBackdropIsA = true;
        private ImageSource? _lastBackdropSource;
        private BitmapSource? _currentBlurredBackdrop;

        private Stopwatch _renderTimer = new Stopwatch();
        private TimeSpan _lastFrameTime = TimeSpan.Zero;
        private int _lastDisplayedPosSec = -1;
        private int _lastDisplayedDurSec = -1;

        // Performance optimizations: frame throttling & deadbands
        private int _renderFrameCount = 0;
        private double _lastArtShadowDir = double.MinValue;
        private double _lastArtShadowBlur = double.MinValue;
        private double _lastArtShadowDepth = double.MinValue;
        private double _lastArtShadowOpacity = double.MinValue;
        private Color _lastArtShadowColor = Colors.Transparent;

        private SolidColorBrush? _cardBorderBrush;
        private double _lastCardShadowBlur = double.MinValue;
        private double _lastCardShadowOpacity = double.MinValue;
        private Color _lastCardShadowColor = Colors.Transparent;

        private double _lastGlowShadowBlur = double.MinValue;
        private double _lastGlowShadowOpacity = double.MinValue;
        private Color _lastGlowShadowColor = Colors.Transparent;

        private double _lastPlayShadowBlur = double.MinValue;
        private double _lastPlayShadowOpacity = double.MinValue;
        private Color _lastPlayShadowColor = Colors.Transparent;

        private IntPtr _cachedTaskbarHwnd = IntPtr.Zero;

        // Custom color selection & screen eyedropper
        private bool _isColorDropperActive = false;
        private Color? _customColorOverride = null;
        private string? _currentSongKey = null;
        private string? _customColorSongKey = null;

        // Window Mode: Fullscreen vs Restored (Windowed)
        private bool _isRestoredMode = false;
        private Rect _savedRestoredBounds = new Rect(0, 0, 980, 680);

        // System Audio Volume Control
        private readonly SystemVolumeService _volumeService = SystemVolumeService.Instance;
        private bool _isUpdatingVolumeFromService = false;
        private bool _isVolumeExpanded = false;
        private bool _isDraggingVolumeSlider = false;
        private System.Windows.Threading.DispatcherTimer? _volumeCollapseTimer;
        private double _expandedVolumeWidth = 236;
        private const double CollapsedVolumeWidth = 36;

        // Timed Synced Lyrics Engine (Fullscreen Karaoke Stage)
        private bool _isLyricsMode = false;
        private TrackLyrics? _currentLyrics;
        private System.Threading.CancellationTokenSource? _lyricsCts;
        private int _activeLineIndex = -1;
        private double _targetScrollOffset = 0.0;
        private double _currentScrollOffset = 0.0;
        private bool _isUserScrolledAway = false;
        private System.Windows.Threading.DispatcherTimer? _userScrollResumeTimer;
        private readonly List<LyricLineItem> _lyricItems = new List<LyricLineItem>();

        // BitChord lyrics tools: source picker, translate / romanize, manual sync offset
        private LyricSourceId? _currentProviderSourceId;
        private System.Threading.CancellationTokenSource? _sourceFetchCts;
        private bool _isFetchingFromSource;
        private TrackLyrics? _preTransformLyrics;
        private bool _translationActive;
        private bool _romanizationActive;
        private bool _translateBusy;
        private string _lastTrackTitle = "";
        private string _lastTrackArtist = "";
        private string _lastTrackAlbum = "";

        // Motion Artwork Engine (Animated Album Covers / Canvas Video)
        private System.Threading.CancellationTokenSource? _motionCts;
        private MotionArtworkResult? _currentMotionResult;
        private bool _isMotionAvailable = false;
        private bool _isFullscreenMotionActive = false;
        private bool _hasAttemptedFallback = false;
        private System.Windows.Threading.DispatcherTimer? _motionHintTimer;
        private System.Windows.Threading.DispatcherTimer? _albumVideoFrameTimer;
        private System.Windows.Threading.DispatcherTimer? _fullscreenVideoFrameTimer;
        private System.Windows.Threading.DispatcherTimer? _motionBarFadeTimer;

        // Display Style: Stage (BitChord) vs Fisheye Lens (Verci)
        public enum LyricDisplayStyle
        {
            Stage,
            Fisheye
        }
        private LyricDisplayStyle _currentDisplayStyle = LyricDisplayStyle.Stage;
        private readonly List<FisheyeWordItem> _allFisheyeWords = new List<FisheyeWordItem>();
        private readonly List<FisheyeWordVisualNode> _fisheyeNodes = new List<FisheyeWordVisualNode>();
        private double _smoothedFisheyeIndex = 0.0;

        private class FisheyeWordVisualNode
        {
            public Border Container { get; set; } = null!;
            public Border PillBorder { get; set; } = null!;
            public TextBlock WordTextBlock { get; set; } = null!;
            public Border SymbolBadge { get; set; } = null!;
            public TextBlock SymbolTextBlock { get; set; } = null!;
            public ScaleTransform ScaleTransform { get; set; } = null!;
            public RotateTransform RotateTransform { get; set; } = null!;
            public TranslateTransform TranslateTransform { get; set; } = null!;
            public FisheyeWordItem? BoundWord { get; set; }
        }

        // High-precision Monotonic Clock Reconciler (BitChord protection against GSMTC transport lag)
        private readonly LyricClockReconciler _lyricClock = new LyricClockReconciler();

        // Cinematic Depth-of-Field Blur Effects (Pre-frozen immutable DirectX shader resources)
        private readonly System.Windows.Media.Effects.BlurEffect _blurDist1 = new System.Windows.Media.Effects.BlurEffect { Radius = 0.8, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance };
        private readonly System.Windows.Media.Effects.BlurEffect _blurDist2 = new System.Windows.Media.Effects.BlurEffect { Radius = 1.4, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance };
        private readonly System.Windows.Media.Effects.BlurEffect _blurDistFar = new System.Windows.Media.Effects.BlurEffect { Radius = 2.2, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance };
        private System.Windows.Media.Effects.DropShadowEffect? _heldWordBloomEffect;

        // High-contrast, vibrant karaoke brushes (recolored to album palette)
        private SolidColorBrush _activeWordBrush = new SolidColorBrush(Color.FromRgb(255, 60, 100));
        private SolidColorBrush _sungWordBrush = new SolidColorBrush(Color.FromRgb(255, 60, 100));
        private SolidColorBrush _unsungWordBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
        private SolidColorBrush _inactiveLineBrush = new SolidColorBrush(Color.FromArgb(95, 255, 255, 255));
        private SolidColorBrush _pastLineBrush = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255));

        // BitChord Cubic Bezier Easing: LYRIC_EASING = CubicBezier(0.41, 0, 0.12, 0.99)
        // One curve for scrolling, scaling, dimming, and blur
        private static double EvaluateLyricBezier(double t)
        {
            t = Math.Clamp(t, 0.0, 1.0);
            // Cubic Bezier with P0=(0,0), P1=(0.41, 0.0), P2=(0.12, 0.99), P3=(1,1)
            // Solve x(u) = t using Newton-Raphson, then compute y(u)
            double u = t;
            for (int i = 0; i < 5; i++)
            {
                double u2 = u * u;
                double u3 = u2 * u;
                double oneMinusU = 1.0 - u;
                double oneMinusU2 = oneMinusU * oneMinusU;

                // x(u) = 3*(1-u)^2 * u * 0.41 + 3*(1-u) * u^2 * 0.12 + u^3
                double xu = 3.0 * oneMinusU2 * u * 0.41 + 3.0 * oneMinusU * u2 * 0.12 + u3;
                double dx = 3.0 * oneMinusU2 * 0.41 + 6.0 * oneMinusU * u * (0.12 - 0.41) + 3.0 * u2 * (1.0 - 0.12);
                if (Math.Abs(dx) < 1e-6) break;
                u -= (xu - t) / dx;
                u = Math.Clamp(u, 0.0, 1.0);
            }

            double ou = 1.0 - u;
            // y(u) = 3*(1-u)^2 * u * 0.0 + 3*(1-u) * u^2 * 0.99 + u^3
            return 3.0 * ou * (u * u) * 0.99 + (u * u * u);
        }

        private class LyricWordView
        {
            public LyricWord Word { get; set; } = null!;
            public Grid ContainerGrid { get; set; } = null!;
            public TextBlock DimTextBlock { get; set; } = null!;
            public TextBlock LitTextBlock { get; set; } = null!;
            public TranslateTransform LiftTransform { get; set; } = null!;
        }

        private class LyricLineItem
        {
            public LyricLine Line { get; set; } = null!;
            public Button Button { get; set; } = null!;
            public TextBlock TextBlock { get; set; } = null!;
            public TranslateTransform StaggerTransform { get; set; } = null!;
            public ScaleTransform ScaleTransform { get; set; } = null!;
            public List<LyricWordView> WordViews { get; set; } = new List<LyricWordView>();
            public Border? GapContainer { get; set; }
            public List<System.Windows.Shapes.Ellipse>? GapDots { get; set; }

            public void SetLineStyle(double fontSize, FontWeight fontWeight, double lineHeight, System.Windows.Media.Effects.Effect? effect, double opacity, double scale = 1.0)
            {
                TextBlock.FontSize = fontSize;
                TextBlock.FontWeight = fontWeight;
                TextBlock.LineHeight = lineHeight;
                TextBlock.Effect = effect;
                TextBlock.Opacity = opacity;

                if (ScaleTransform != null)
                {
                    ScaleTransform.ScaleX = scale;
                    ScaleTransform.ScaleY = scale;
                }

                for (int i = 0; i < WordViews.Count; i++)
                {
                    var wv = WordViews[i];
                    wv.DimTextBlock.FontSize = fontSize;
                    wv.DimTextBlock.FontWeight = fontWeight;
                    wv.DimTextBlock.LineHeight = lineHeight;
                    wv.LitTextBlock.FontSize = fontSize;
                    wv.LitTextBlock.FontWeight = fontWeight;
                    wv.LitTextBlock.LineHeight = lineHeight;
                }
            }
        }

        // BitChord ScrollRun Handover State: tracks dynamic bezier glide and trailing row fan-out
        private int _scrollRunId = 0;
        private double _scrollRunStartOffset = 0.0;
        private double _scrollRunTargetOffset = 0.0;
        private double _scrollRunDurationMs = 400.0;
        private double _scrollRunElapsedMs = 0.0;
        private bool _isScrollRunActive = false;
        private int _scrollRunFocusIndex = -1;
        private const int StaggerSteps = 3;
        private const double StaggerFraction = 0.06;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern uint GetPixel(IntPtr hdc, int nXPos, int nYPos);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Win32Point lpPoint);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out Win32Rect lpRect);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct Win32Point
        {
            public int X;
            public int Y;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct Win32Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        // Taskbar Auto-Reveal in Fullscreen Mode
        private IntPtr _windowHandle = IntPtr.Zero;
        private bool _isTaskbarRevealed = false;

        public FullscreenVisualizerWindow(MediaSessionService mediaService, AudioReactiveService audioService)
        {
            InitializeComponent();
            _mediaService = mediaService;
            _audioService = audioService;

            _blurDist1.Freeze();
            _blurDist2.Freeze();
            _blurDistFar.Freeze();

            _beam = new EdgeBeam(BeamHost);

            _mediaService.TrackUpdated += OnTrackUpdated;
            _mediaService.TimelineUpdated += OnTimelineUpdated;
            _mediaService.PlaybackStateChanged += OnPlaybackStateChanged;
            _settings.SettingsChanged += OnSettingsChanged;
            _volumeService.VolumeChanged += OnSystemVolumeChanged;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _windowHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = Math.Max(1, SystemParameters.PrimaryScreenHeight - 1);

            double rw = Math.Min(1020, SystemParameters.PrimaryScreenWidth * 0.7);
            double rh = Math.Min(720, SystemParameters.PrimaryScreenHeight * 0.75);
            double rx = (SystemParameters.PrimaryScreenWidth - rw) / 2;
            double ry = (SystemParameters.PrimaryScreenHeight - rh) / 2;
            _savedRestoredBounds = new Rect(rx, ry, rw, rh);

            InitSliderValues();
            InitFisheyeVisualPool();
            ApplyGeometry();
            UpdateWindowClip();
            UpdateColors(_mediaService.CurrentTrack);
            UpdateVolumeUI(_volumeService.MasterVolume, _volumeService.IsMuted);

            // Adopt the current palette without easing so the visualizer does not fade in
            // from the default crimson on every launch.
            _beam.EaseColor(1.0);

            _renderTimer.Start();
            _lastFrameTime = _renderTimer.Elapsed;
            CompositionTarget.Rendering += OnRenderFrame;
            MotionArtworkService.Instance.VideoCached += OnMotionVideoCached;
            IsVisibleChanged += FullscreenVisualizerWindow_IsVisibleChanged;

            if (_mediaService.CurrentTrack != null && !string.IsNullOrWhiteSpace(_mediaService.CurrentTrack.Title))
            {
                OnTrackUpdated(_mediaService.CurrentTrack);
            }
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyGeometry();
            UpdateWindowClip();
            if (!_isUserSeeking)
            {
                UpdatePlaybackTimeUI(_currentTrackPosition, _currentTrackDuration);
            }
        }

        private void UpdateWindowClip()
        {
            if (_isRestoredMode)
            {
                double w = ActualWidth > 0 ? ActualWidth : Width;
                double h = ActualHeight > 0 ? ActualHeight : Height;
                if (w > 0 && h > 0)
                {
                    var geom = new RectangleGeometry(new Rect(0, 0, w, h), 22, 22);
                    geom.Freeze();
                    MainRootBorder.Clip = geom;
                    MainRootGrid.Clip = geom;
                }
            }
            else
            {
                MainRootBorder.Clip = null;
                MainRootGrid.Clip = null;
            }
        }

        private void ApplyGeometry()
        {
            if (_beam == null) return;

            double w = BeamHost.ActualWidth > 1.0 ? BeamHost.ActualWidth : ActualWidth;
            double h = BeamHost.ActualHeight > 1.0 ? BeamHost.ActualHeight : ActualHeight;
            if (w <= 1.0) w = SystemParameters.PrimaryScreenWidth;
            if (h <= 1.0) h = SystemParameters.PrimaryScreenHeight;

            _beam.SetGeometry(w, h, _settings.GlowSpread);
        }

        private bool _suppressSettingsUpdate;

        private void OnSettingsChanged()
        {
            Dispatcher.Invoke(() =>
            {
                if (_suppressSettingsUpdate) return;
                _suppressSettingsUpdate = true;
                try
                {
                    if (BrightnessSlider != null) BrightnessSlider.Value = _settings.GlowBrightness * 100.0;
                    if (SpreadSlider != null) SpreadSlider.Value = _settings.GlowSpread;
                    if (BackdropSlider != null) BackdropSlider.Value = _settings.BackdropBrightness * 100.0;
                    if (SpeedSlider != null) SpeedSlider.Value = _settings.RotationSpeed * 100.0;
                    if (ReactivitySlider != null) ReactivitySlider.Value = _settings.AudioReactivity * 100.0;

                    if (BrightnessValText != null) BrightnessValText.Text = $"{(int)(_settings.GlowBrightness * 100)}%";
                    if (SpreadValText != null) SpreadValText.Text = $"{(int)_settings.GlowSpread} px";
                    if (BackdropValText != null) BackdropValText.Text = $"{(int)(_settings.BackdropBrightness * 100)}%";
                    if (SpeedValText != null) SpeedValText.Text = $"{_settings.RotationSpeed:0.0}x";
                    if (ReactivityValText != null) ReactivityValText.Text = $"{_settings.AudioReactivity:0.0}x";

                    if (TvBackdropHost != null) TvBackdropHost.Opacity = _settings.BackdropBrightness;
                    ApplyGeometry();
                }
                finally
                {
                    _suppressSettingsUpdate = false;
                }
            });
        }

        private void InitSliderValues()
        {
            _suppressSettingsUpdate = true;
            try
            {
                BrightnessSlider.Value = _settings.GlowBrightness * 100.0;
                SpreadSlider.Value = _settings.GlowSpread;
                BackdropSlider.Value = _settings.BackdropBrightness * 100.0;
                SpeedSlider.Value = _settings.RotationSpeed * 100.0;
                ReactivitySlider.Value = _settings.AudioReactivity * 100.0;

                BrightnessValText.Text = $"{(int)BrightnessSlider.Value}%";
                SpreadValText.Text = $"{(int)SpreadSlider.Value} px";
                BackdropValText.Text = $"{(int)BackdropSlider.Value}%";
                SpeedValText.Text = $"{_settings.RotationSpeed:0.0}x";
                ReactivityValText.Text = $"{_settings.AudioReactivity:0.0}x";

                TvBackdropHost.Opacity = _settings.BackdropBrightness;
            }
            finally
            {
                _suppressSettingsUpdate = false;
            }
        }

        private void OnRenderFrame(object? sender, EventArgs e)
        {
            if (!IsVisible || WindowState == WindowState.Minimized) return;

            var now = _renderTimer.Elapsed;
            double dt = (now - _lastFrameTime).TotalSeconds;
            _lastFrameTime = now;

            if (dt <= 0.0 || dt > 0.1) dt = 0.016;

            // 1. ROTATION SPEED DIRECTLY DRIVEN BY MUSIC
            float rawEnergy = _audioReactivityEnabled ? _audioService.CurrentEnergy : 0f;
            double speedMultiplier = _settings.RotationSpeed;

            // Track dynamic kick & bass excitement for the "excited jump" effect
            if (rawEnergy > _beatExcitement)
            {
                // Fast attack: jump up immediately when kicks and bass hit!
                _beatExcitement = (_beatExcitement * 0.25) + (rawEnergy * 0.75);
            }
            else
            {
                // Smooth decay over ~350ms (for that "brief second" jump)
                _beatExcitement = Math.Max(0.0, _beatExcitement - (dt * 2.8));
            }

            if (speedMultiplier > 0.0)
            {
                // Baseline speed when quiet: slow, calm, majestic drift (~80 px/sec)
                double baseSpeed = 80.0 * speedMultiplier;

                // Kick acceleration: surge speed on kicks and bass
                double kickSurge = Math.Pow(rawEnergy, 1.25) * 2200.0 * speedMultiplier * _settings.AudioReactivity;
                double targetSpeed = baseSpeed + kickSurge;

                // Asymmetric acceleration & deceleration:
                if (targetSpeed > _currentSpeedPx)
                {
                    _currentSpeedPx = (_currentSpeedPx * 0.60) + (targetSpeed * 0.40);
                }
                else
                {
                    _currentSpeedPx = (_currentSpeedPx * 0.94) + (targetSpeed * 0.06);
                }

                _beam.AdvancePhase(_currentSpeedPx * dt);
            }

            // 2. BREATHING: calm 14s background cycle, energized by kicks and bass!
            _breathPhase += GlowProfile.BreathOmega * dt;
            if (_breathPhase > Math.PI * 2.0) _breathPhase -= Math.PI * 2.0;
            double baseBreath = GlowProfile.Breathing(_breathPhase, _settings.BreathingAmount);

            double kickBreathBoost = _beatExcitement * 0.65 * _settings.AudioReactivity;
            double breath = Math.Clamp(baseBreath + kickBreathBoost, 0.0, 1.0);

            double userBrightness = _settings.GlowBrightness;
            double energy = Math.Min(1.0, rawEnergy * _settings.AudioReactivity);

            // Increased glow brightness with dynamic beat excitement flare for intense luminescence
            double beatBrightnessBoost = _beatExcitement * 0.35 * _settings.AudioReactivity;
            double effectiveBrightness = (userBrightness * 1.20) + beatBrightnessBoost;
            double brightness = speedMultiplier > 0.0 ? effectiveBrightness : 0.0;

            _beam.Render(brightness, breath, energy);
            _beam.EaseColor(dt);

            // 2.5 REAL-TIME LIGHT PROXIMITY & SPECULAR REFLECTION:
            // Sub-sampled to every 2nd frame with deadband thresholds to eliminate continuous DropShadow invalidation
            _renderFrameCount++;
            if ((_renderFrameCount & 1) == 0)
            {
                Color currentLightColor = _beam.CurrentColor;
                double winW = ActualWidth > 0 ? ActualWidth : 1920.0;
                double winH = ActualHeight > 0 ? ActualHeight : 1080.0;

                // (a) Album Art Dynamic Directional Ambient Glow & Shadow:
                if (ArtDropShadow != null)
                {
                    double artCenterX = winW * 0.5;
                    double artCenterY = winH * 0.44;
                    double artLightIllum = _beam.GetIlluminationAtPoint(artCenterX, artCenterY, reachRadius: winW * 0.65);
                    double lightAngle = _beam.GetLightAngleFromPoint(artCenterX, artCenterY);

                    double targetDir = lightAngle + 180.0;
                    double targetBlur = 24.0 + (artLightIllum * 52.0);
                    double targetDepth = 4.0 + (artLightIllum * 16.0);
                    double targetOpacity = 0.28 + (artLightIllum * 0.55);

                    if (_lastArtShadowColor != currentLightColor)
                    {
                        _lastArtShadowColor = currentLightColor;
                        ArtDropShadow.Color = currentLightColor;
                    }
                    if (Math.Abs(targetDir - _lastArtShadowDir) >= 2.0)
                    {
                        _lastArtShadowDir = targetDir;
                        ArtDropShadow.Direction = targetDir;
                    }
                    if (Math.Abs(targetBlur - _lastArtShadowBlur) >= 1.5)
                    {
                        _lastArtShadowBlur = targetBlur;
                        ArtDropShadow.BlurRadius = targetBlur;
                    }
                    if (Math.Abs(targetDepth - _lastArtShadowDepth) >= 1.0)
                    {
                        _lastArtShadowDepth = targetDepth;
                        ArtDropShadow.ShadowDepth = targetDepth;
                    }
                    if (Math.Abs(targetOpacity - _lastArtShadowOpacity) >= 0.02)
                    {
                        _lastArtShadowOpacity = targetOpacity;
                        ArtDropShadow.Opacity = targetOpacity;
                    }
                }

                // (b) Live Glow Controls Card Specular Reflection:
                // Only evaluated if the card is actively opened!
                if (QuickGlowControlsCard != null && QuickGlowControlsCard.Visibility == Visibility.Visible)
                {
                    double cardCenterX = winW - 200.0;
                    double cardCenterY = winH * 0.50;
                    double cardLightIllum = _beam.GetIlluminationAtPoint(cardCenterX, cardCenterY, reachRadius: 650.0);

                    byte rimAlpha = (byte)Math.Clamp(30 + (cardLightIllum * 160.0), 30, 220);
                    if (_cardBorderBrush == null)
                    {
                        _cardBorderBrush = new SolidColorBrush();
                        QuickGlowControlsCard.BorderBrush = _cardBorderBrush;
                    }
                    _cardBorderBrush.Color = Color.FromArgb(rimAlpha, currentLightColor.R, currentLightColor.G, currentLightColor.B);

                    if (QuickGlowControlsCard.Effect is DropShadowEffect cardShadow)
                    {
                        double targetCardOpacity = 0.30 + (cardLightIllum * 0.45);
                        double targetCardBlur = 24.0 + (cardLightIllum * 26.0);

                        if (_lastCardShadowColor != currentLightColor)
                        {
                            _lastCardShadowColor = currentLightColor;
                            cardShadow.Color = currentLightColor;
                        }
                        if (Math.Abs(targetCardOpacity - _lastCardShadowOpacity) >= 0.02)
                        {
                            _lastCardShadowOpacity = targetCardOpacity;
                            cardShadow.Opacity = targetCardOpacity;
                        }
                        if (Math.Abs(targetCardBlur - _lastCardShadowBlur) >= 1.5)
                        {
                            _lastCardShadowBlur = targetCardBlur;
                            cardShadow.BlurRadius = targetCardBlur;
                        }
                    }
                }

                // (c) Reactive Glow Pill Button:
                if (ReactiveGlowShadow != null)
                {
                    double glowBtnCenterX = winW * 0.5;
                    double glowBtnCenterY = winH * 0.77;
                    double btnLightIllum = _beam.GetIlluminationAtPoint(glowBtnCenterX, glowBtnCenterY, reachRadius: 600.0);

                    double targetGlowBlur = 12.0 + (btnLightIllum * 24.0);
                    double targetGlowOpacity = _audioReactivityEnabled ? (0.22 + (btnLightIllum * 0.68)) : 0.0;

                    if (_lastGlowShadowColor != currentLightColor)
                    {
                        _lastGlowShadowColor = currentLightColor;
                        ReactiveGlowShadow.Color = currentLightColor;
                    }
                    if (Math.Abs(targetGlowBlur - _lastGlowShadowBlur) >= 1.5)
                    {
                        _lastGlowShadowBlur = targetGlowBlur;
                        ReactiveGlowShadow.BlurRadius = targetGlowBlur;
                    }
                    if (Math.Abs(targetGlowOpacity - _lastGlowShadowOpacity) >= 0.02)
                    {
                        _lastGlowShadowOpacity = targetGlowOpacity;
                        ReactiveGlowShadow.Opacity = targetGlowOpacity;
                    }
                }

                // (d) Hero Play / Pause Button:
                if (PlayPauseShadow != null)
                {
                    double playCenterX = winW * 0.5;
                    double playCenterY = winH * 0.69;
                    double playLightIllum = _beam.GetIlluminationAtPoint(playCenterX, playCenterY, reachRadius: 620.0);

                    double targetPlayBlur = 14.0 + (playLightIllum * 20.0);
                    double targetPlayOpacity = 0.20 + (playLightIllum * 0.55);

                    if (_lastPlayShadowColor != currentLightColor)
                    {
                        _lastPlayShadowColor = currentLightColor;
                        PlayPauseShadow.Color = currentLightColor;
                    }
                    if (Math.Abs(targetPlayBlur - _lastPlayShadowBlur) >= 1.5)
                    {
                        _lastPlayShadowBlur = targetPlayBlur;
                        PlayPauseShadow.BlurRadius = targetPlayBlur;
                    }
                    if (Math.Abs(targetPlayOpacity - _lastPlayShadowOpacity) >= 0.02)
                    {
                        _lastPlayShadowOpacity = targetPlayOpacity;
                        PlayPauseShadow.Opacity = targetPlayOpacity;
                    }
                }
            }

            // 3. TV AMBIENT FLOATING BACKDROP: Organic random sway motion via pure 2D vertex translation
            _backdropMotionTime += dt;
            if (_backdropMotionTime > 10000.0) _backdropMotionTime = 0.0;

            double swayX = Math.Sin(_backdropMotionTime * 0.38) * 38.0 
                         + Math.Sin(_backdropMotionTime * 0.17 + 1.5) * 18.0;

            double swayY = Math.Cos(_backdropMotionTime * 0.31 + 0.8) * 28.0 
                         + Math.Sin(_backdropMotionTime * 0.14 + 2.2) * 14.0;

            BackdropTranslateTransform.X = swayX;
            BackdropTranslateTransform.Y = swayY;

            // 4. BUTTERY-SMOOTH PROGRESS BAR ADVANCEMENT
            if (_isPlaying && _currentTrackDuration > TimeSpan.Zero && !_isUserSeeking)
            {
                _currentTrackPosition += TimeSpan.FromSeconds(dt);
                if (_currentTrackPosition > _currentTrackDuration)
                {
                    _currentTrackPosition = _currentTrackDuration;
                }
                UpdatePlaybackTimeUI(_currentTrackPosition, _currentTrackDuration);
            }

            // 5. TIMED LYRICS SYNC (Verci Fisheye Lens vs Stage View)
            if (_isLyricsMode && _currentLyrics != null && _currentLyrics.IsSynced)
            {
                if (_currentDisplayStyle == LyricDisplayStyle.Fisheye)
                {
                    UpdateFisheyeLyricsFrame(dt);
                }
                else if (_lyricItems.Count > 0)
                {
                    UpdateLyricsFrame(dt);
                }
            }

            // 6. ACCESSIBLE TASKBAR ON HOVER (WHEN IN FULLSCREEN MODE)
            UpdateTaskbarVisibilityOnHover();
        }

        private void OnTrackUpdated(MediaTrackInfo track)
        {
            Dispatcher.Invoke(() => UpdateColors(track));
        }

        private void OnTimelineUpdated(TimeSpan position, TimeSpan duration)
        {
            Dispatcher.Invoke(() =>
            {
                if (DateTime.UtcNow < _ignoreStaleTimelineUntil)
                {
                    if (Math.Abs((position - _currentTrackPosition).TotalSeconds) <= 2.5)
                    {
                        _currentTrackPosition = _lyricClock.Reconcile(_currentTrackPosition, position, _isPlaying);
                        _currentTrackDuration = duration;
                        _ignoreStaleTimelineUntil = DateTime.MinValue;
                    }
                }
                else
                {
                    _currentTrackDuration = duration;
                    _currentTrackPosition = _lyricClock.Reconcile(_currentTrackPosition, position, _isPlaying);
                }

                if (!_isUserSeeking)
                {
                    UpdatePlaybackTimeUI(_currentTrackPosition, _currentTrackDuration);
                }
            });
        }

        private void SetPlayPauseIconState(bool isPlaying)
        {
            string icon = isPlaying ? "\uE769" : "\uE768";
            if (PlayPauseIcon != null) PlayPauseIcon.Text = icon;
            if (MotionPlayPauseIcon != null) MotionPlayPauseIcon.Text = icon;
        }

        private void OnPlaybackStateChanged(bool isPlaying)
        {
            Dispatcher.Invoke(() =>
            {
                _isPlaying = isPlaying;
                SetPlayPauseIconState(isPlaying);
                if (_isMotionAvailable)
                {
                    if (isPlaying)
                    {
                        if (AlbumCoverVideo.Source != null && !_isFullscreenMotionActive)
                        {
                            AlbumCoverVideo.Play();
                            if (TvMotionVideoBackdrop != null && TvMotionVideoBackdrop.Source != null)
                                TvMotionVideoBackdrop.Play();
                        }
                        if (FullscreenMotionVideo.Source != null && _isFullscreenMotionActive)
                        {
                            FullscreenMotionVideo.Play();
                            if (MotionVideoBackdrop != null && MotionVideoBackdrop.Source != null)
                                MotionVideoBackdrop.Play();
                        }
                    }
                    else
                    {
                        AlbumCoverVideo?.Pause();
                        TvMotionVideoBackdrop?.Pause();
                        FullscreenMotionVideo?.Pause();
                        MotionVideoBackdrop?.Pause();
                    }
                }
            });
        }

        private void UpdateColors(MediaTrackInfo? track)
        {
            if (track == null)
            {
                TrackTitleText.Text = "Ready to Play";
                ArtistNameText.Text = "Play music to begin";
                if (LyricsSongTitleText != null) LyricsSongTitleText.Text = "Ready to Play";
                if (LyricsArtistText != null) LyricsArtistText.Text = "Play music to begin";
                if (MotionSongTitleText != null) MotionSongTitleText.Text = "Ready to Play";
                if (MotionArtistText != null) MotionArtistText.Text = "Play music to begin";
                AlbumCoverImage.Source = null;
                ResetMotionArtwork();
                SetPlayPauseIconState(false);
                _currentLyrics = null;
                UpdateLyricsButtonAvailability(hasLyrics: false, isInstrumental: false);
                return;
            }

            TrackTitleText.Text = track.Title;
            ArtistNameText.Text = track.Artist;
            if (LyricsSongTitleText != null) LyricsSongTitleText.Text = track.Title;
            if (LyricsArtistText != null) LyricsArtistText.Text = track.Artist;
            if (MotionSongTitleText != null) MotionSongTitleText.Text = track.Title;
            if (MotionArtistText != null) MotionArtistText.Text = track.Artist;
            if (AlbumCoverImage != null)
            {
                AlbumCoverImage.BeginAnimation(UIElement.OpacityProperty, null);
                AlbumCoverImage.Opacity = 1.0;
                AlbumCoverImage.Source = track.Thumbnail;
            }
            if (_isFullscreenMotionActive)
            {
                if (FullscreenCoverImage != null)
                {
                    FullscreenCoverImage.BeginAnimation(UIElement.OpacityProperty, null);
                    FullscreenCoverImage.Opacity = 1.0;
                    FullscreenCoverImage.Source = track.Thumbnail;
                }
                UpdateMotionBlurredBackdrop();
            }

            // Detect if this is a new song (or initial load):
            string trackTitle = track.Title?.Trim() ?? string.Empty;
            string trackArtist = track.Artist?.Trim() ?? string.Empty;
            string newSongKey = !string.IsNullOrEmpty(trackTitle) ? $"{trackTitle} - {trackArtist}" : string.Empty;
            bool isNewSong = !string.IsNullOrEmpty(newSongKey) && (_currentSongKey == null || newSongKey != _currentSongKey);

            if (isNewSong)
            {
                _lyricClock.Reset(track.Position, track.IsPlaying);

                // When next song comes, automatically revert back to automatic color choosing!
                if (_currentSongKey != null)
                {
                    _customColorOverride = null;
                    UpdateColorOverrideUI();
                }

                // Always pre-fetch lyrics so button availability (enabled / greyed out) is immediately updated!
                _ = LoadLyricsForCurrentSongAsync(track);

                // Asynchronously fetch motion animated album cover from Apple Music / Tidal / Community!
                _ = LoadMotionArtworkForCurrentSongAsync(trackTitle, trackArtist, track.AlbumTitle);
            }
            else if (string.IsNullOrEmpty(newSongKey))
            {
                _currentLyrics = null;
                UpdateLyricsButtonAvailability(hasLyrics: false, isInstrumental: false);
                ResetMotionArtwork();
            }

            _currentSongKey = newSongKey;

            // TV Ambient Blurred Background Cross-Fade
            if (track.Thumbnail != null && !ReferenceEquals(track.Thumbnail, _lastBackdropSource))
            {
                _lastBackdropSource = track.Thumbnail;
                var blurred = BackdropBlur.CreateBlurredBackdrop(track.Thumbnail);
                CrossFadeBackdrop(blurred);
            }
            else if (track.Thumbnail == null && _lastBackdropSource != null)
            {
                _lastBackdropSource = null;
                CrossFadeBackdrop(null);
            }

            SetPlayPauseIconState(track.IsPlaying);

            // Synchronize motion video playback state with music playback state
            if (_isMotionAvailable)
            {
                if (track.IsPlaying)
                {
                    if (AlbumCoverVideo.Source != null && !_isFullscreenMotionActive)
                    {
                        AlbumCoverVideo.Play();
                        if (TvMotionVideoBackdrop != null && TvMotionVideoBackdrop.Source != null)
                            TvMotionVideoBackdrop.Play();
                    }
                    if (FullscreenMotionVideo.Source != null && _isFullscreenMotionActive)
                    {
                        FullscreenMotionVideo.Play();
                        if (MotionVideoBackdrop != null && MotionVideoBackdrop.Source != null)
                            MotionVideoBackdrop.Play();
                    }
                }
                else
                {
                    AlbumCoverVideo?.Pause();
                    TvMotionVideoBackdrop?.Pause();
                    FullscreenMotionVideo?.Pause();
                    MotionVideoBackdrop?.Pause();
                }
            }

            // Snap-back protection:
            // When user seeks, GSMTC sends stale position events for up to 2 seconds before the player's
            // timeline reflects the jump. If the incoming position is stale, do not overwrite our local position!
            if (DateTime.UtcNow < _ignoreStaleTimelineUntil)
            {
                if (Math.Abs((track.Position - _currentTrackPosition).TotalSeconds) > 2.5)
                {
                    // Stale update: ignore position, but record playing status and duration
                    _isPlaying = track.IsPlaying;
                    _currentTrackDuration = track.Duration;
                }
                else
                {
                    // Player has caught up to our seek!
                    _currentTrackPosition = _lyricClock.Reconcile(_currentTrackPosition, track.Position, track.IsPlaying);
                    _currentTrackDuration = track.Duration;
                    _isPlaying = track.IsPlaying;
                    _ignoreStaleTimelineUntil = DateTime.MinValue; // reset suppression
                }
            }
            else
            {
                _isPlaying = track.IsPlaying;
                _currentTrackDuration = track.Duration;
                _currentTrackPosition = _lyricClock.Reconcile(_currentTrackPosition, track.Position, _isPlaying);
            }

            if (!_isUserSeeking)
            {
                UpdatePlaybackTimeUI(_currentTrackPosition, _currentTrackDuration);
            }

            Color effectiveColor = _customColorOverride ?? track.DominantColor;
            ApplyGlowColor(effectiveColor);
        }

        private void ApplyGlowColor(Color dom)
        {
            _beam.SetTargetColor(dom);

            var duration = TimeSpan.FromMilliseconds(500);

            // TV Vignette Stops: darker perimeter so the main screen-edge glow shines with massive contrast
            Color darkVig = _customColorOverride.HasValue
                ? Color.FromArgb(255, (byte)(dom.R * 0.08), (byte)(dom.G * 0.08), (byte)(dom.B * 0.08))
                : (_mediaService.CurrentTrack?.DarkVignetteColor ?? Color.FromArgb(255, 15, 5, 8));

            Color vigCenter = Color.FromArgb(60, 0, 0, 0);
            Color vigMid = Color.FromArgb(130, (byte)(darkVig.R * 0.3), (byte)(darkVig.G * 0.3), (byte)(darkVig.B * 0.3));
            Color vigOuter = Color.FromArgb(220, darkVig.R, darkVig.G, darkVig.B);
            AnimateGradientStopColor(BackdropVignetteCenterStop, vigCenter, duration);
            AnimateGradientStopColor(BackdropVignetteMidStop, vigMid, duration);
            AnimateGradientStopColor(BackdropVignetteOuterStop, vigOuter, duration);

            // Background Tint
            var bgBrush = MainRootBorder.Background as SolidColorBrush;
            if (bgBrush == null || bgBrush.IsFrozen)
            {
                bgBrush = new SolidColorBrush(darkVig);
                MainRootBorder.Background = bgBrush;
            }
            else
            {
                AnimateBrushColor(bgBrush, darkVig, duration);
            }

            UpdateLyricBrushes(dom);
        }

        private void UpdatePlaybackTimeUI(TimeSpan pos, TimeSpan dur)
        {
            if (dur > TimeSpan.Zero)
            {
                double totalWidth = TimelineTrackContainer.ActualWidth > 0 ? TimelineTrackContainer.ActualWidth : 450.0;
                double ratio = Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0.0, 1.0);
                double progressWidth = ratio * totalWidth;

                ProgressBarScale.ScaleX = Math.Max(0.001, progressWidth);
                KnobTranslate.X = progressWidth - 5.0;

                int curSec = (int)pos.TotalSeconds;
                int durSec = (int)dur.TotalSeconds;
                if (curSec != _lastDisplayedPosSec || durSec != _lastDisplayedDurSec)
                {
                    _lastDisplayedPosSec = curSec;
                    _lastDisplayedDurSec = durSec;
                    string posStr = FormatTime(pos);
                    var remaining = dur - pos;
                    if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
                    string remStr = "-" + FormatTime(remaining);

                    PositionText.Text = posStr;
                    DurationText.Text = remStr;

                    if (MotionPositionText != null) MotionPositionText.Text = posStr;
                    if (MotionDurationText != null) MotionDurationText.Text = remStr;
                }

                // Update Motion Artwork Floating Bar Scrubber
                if (MotionTimelineTrackContainer != null && MotionProgressBarScale != null && MotionKnobTranslate != null)
                {
                    double motionWidth = MotionTimelineTrackContainer.ActualWidth > 0 ? MotionTimelineTrackContainer.ActualWidth : 360.0;
                    double motionProgress = ratio * motionWidth;
                    MotionProgressBarScale.ScaleX = Math.Max(0.001, motionProgress);
                    MotionKnobTranslate.X = motionProgress - 5.0;
                }
            }
            else
            {
                ProgressBarScale.ScaleX = 0.001;
                KnobTranslate.X = -5.0;
                if (_lastDisplayedPosSec != 0)
                {
                    _lastDisplayedPosSec = 0;
                    _lastDisplayedDurSec = 0;
                    PositionText.Text = "0:00";
                    DurationText.Text = "-0:00";
                }

                if (MotionProgressBarScale != null) MotionProgressBarScale.ScaleX = 0.001;
                if (MotionKnobTranslate != null) MotionKnobTranslate.X = -5.0;
                if (MotionPositionText != null) MotionPositionText.Text = "0:00";
                if (MotionDurationText != null) MotionDurationText.Text = "-0:00";
            }
        }

        private static string FormatTime(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            if (t.TotalHours >= 1)
            {
                return $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";
            }
            return $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
        }

        private void CrossFadeBackdrop(BitmapSource? newBlurred)
        {
            _currentBlurredBackdrop = newBlurred;

            var fadeDuration = TimeSpan.FromMilliseconds(750);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };

            var fadeInAnim = new DoubleAnimation(0.0, 1.0, fadeDuration) { EasingFunction = ease };
            var fadeOutAnim = new DoubleAnimation(1.0, 0.0, fadeDuration) { EasingFunction = ease };

            if (_activeBackdropIsA)
            {
                BackdropImageB.Source = newBlurred;
                BackdropImageB.BeginAnimation(UIElement.OpacityProperty, fadeInAnim);
                BackdropImageA.BeginAnimation(UIElement.OpacityProperty, fadeOutAnim);
                _activeBackdropIsA = false;
            }
            else
            {
                BackdropImageA.Source = newBlurred;
                BackdropImageA.BeginAnimation(UIElement.OpacityProperty, fadeInAnim);
                BackdropImageB.BeginAnimation(UIElement.OpacityProperty, fadeOutAnim);
                _activeBackdropIsA = true;
            }

            if (MotionBlurredBackdrop != null)
            {
                MotionBlurredBackdrop.Source = newBlurred;
            }
        }

        private static void AnimateBrushColor(SolidColorBrush brush, Color toColor, TimeSpan duration)
        {
            var anim = new ColorAnimation
            {
                To = toColor,
                Duration = duration,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }

        private static void AnimateGradientStopColor(GradientStop stop, Color toColor, TimeSpan duration)
        {
            var anim = new ColorAnimation
            {
                To = toColor,
                Duration = duration,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            stop.BeginAnimation(GradientStop.ColorProperty, anim);
        }

        // =========================================================
        // TIMELINE INTERACTIVE SEEKING HANDLERS (GLITCH-FREE)
        // =========================================================

        private void Timeline_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_currentTrackDuration <= TimeSpan.Zero) return;

            _isUserSeeking = true;
            _wasPlayingBeforeSeek = _isPlaying;
            int seq = System.Threading.Interlocked.Increment(ref _seekSequence);

            // Halt music immediately as soon as user touches or drags the timeline
            if (_wasPlayingBeforeSeek)
            {
                _ = _mediaService.PauseAsync();
                _isPlaying = false;
                SetPlayPauseIconState(false);
            }

            TimelineTrackContainer.CaptureMouse();

            double totalWidth = TimelineTrackContainer.ActualWidth > 0 ? TimelineTrackContainer.ActualWidth : 450.0;
            double mouseX = Math.Clamp(e.GetPosition(TimelineTrackContainer).X, 0, totalWidth);
            double ratio = mouseX / totalWidth;

            _seekTargetTime = TimeSpan.FromSeconds(ratio * _currentTrackDuration.TotalSeconds);
            _currentTrackPosition = _seekTargetTime;

            ProgressBarScale.ScaleX = Math.Max(0.001, mouseX);
            KnobTranslate.X = mouseX - 5.0;
            _lastDisplayedPosSec = (int)_seekTargetTime.TotalSeconds;
            PositionText.Text = FormatTime(_seekTargetTime);
            var remaining = _currentTrackDuration - _seekTargetTime;
            DurationText.Text = "-" + FormatTime(remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining);
        }

        private void Timeline_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_isUserSeeking || _currentTrackDuration <= TimeSpan.Zero) return;

            double totalWidth = TimelineTrackContainer.ActualWidth > 0 ? TimelineTrackContainer.ActualWidth : 450.0;
            double mouseX = Math.Clamp(e.GetPosition(TimelineTrackContainer).X, 0, totalWidth);
            double ratio = mouseX / totalWidth;

            _seekTargetTime = TimeSpan.FromSeconds(ratio * _currentTrackDuration.TotalSeconds);
            _currentTrackPosition = _seekTargetTime;

            ProgressBarScale.ScaleX = Math.Max(0.001, mouseX);
            KnobTranslate.X = mouseX - 5.0;
            _lastDisplayedPosSec = (int)_seekTargetTime.TotalSeconds;
            PositionText.Text = FormatTime(_seekTargetTime);
            var remaining = _currentTrackDuration - _seekTargetTime;
            DurationText.Text = "-" + FormatTime(remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining);
        }

        private async void Timeline_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isUserSeeking) return;

            _isUserSeeking = false;
            TimelineTrackContainer.ReleaseMouseCapture();

            if (_currentTrackDuration > TimeSpan.Zero)
            {
                int seq = _seekSequence;

                double totalWidth = TimelineTrackContainer.ActualWidth > 0 ? TimelineTrackContainer.ActualWidth : 450.0;
                double mouseX = Math.Clamp(e.GetPosition(TimelineTrackContainer).X, 0, totalWidth);
                double ratio = mouseX / totalWidth;

                _seekTargetTime = TimeSpan.FromSeconds(ratio * _currentTrackDuration.TotalSeconds);
                _currentTrackPosition = _seekTargetTime;
                _lyricClock.Reset(_seekTargetTime, _isPlaying);

                // Protect local position from stale GSMTC echo events for 3.0 seconds
                _ignoreStaleTimelineUntil = DateTime.UtcNow.AddSeconds(3.0);

                // 1. Send seek to media player while it is silent/paused
                await _mediaService.ChangePlaybackPositionAsync(_seekTargetTime);

                // 2. Wait ~800ms after seeking for YouTube Music / Spotify to decode & buffer in silence
                await System.Threading.Tasks.Task.Delay(800);

                // 3. Resume the music cleanly if this seek is still the latest one and it was playing before
                if (seq == _seekSequence && _wasPlayingBeforeSeek)
                {
                    await _mediaService.PlayAsync();
                    _isPlaying = true;
                    SetPlayPauseIconState(true);
                }
            }
        }

        // =========================================================
        // LIVE SLIDERS HANDLERS
        // =========================================================

        private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSettingsUpdate) return;
            if (BrightnessValText != null)
            {
                _suppressSettingsUpdate = true;
                try
                {
                    _settings.GlowBrightness = e.NewValue / 100.0;
                    BrightnessValText.Text = $"{(int)e.NewValue}%";
                }
                finally
                {
                    _suppressSettingsUpdate = false;
                }
            }
        }

        private void SpreadSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSettingsUpdate) return;
            if (SpreadValText != null)
            {
                _suppressSettingsUpdate = true;
                try
                {
                    _settings.GlowSpread = e.NewValue;
                    SpreadValText.Text = $"{(int)e.NewValue} px";
                    ApplyGeometry();
                }
                finally
                {
                    _suppressSettingsUpdate = false;
                }
            }
        }

        private void BackdropSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSettingsUpdate) return;
            if (BackdropValText != null && TvBackdropHost != null)
            {
                _suppressSettingsUpdate = true;
                try
                {
                    double opacity = e.NewValue / 100.0;
                    _settings.BackdropBrightness = opacity;
                    TvBackdropHost.Opacity = opacity;
                    if (MotionBlurredBackdrop != null) MotionBlurredBackdrop.Opacity = opacity;
                    if (MotionVideoBackdrop != null && MotionVideoBackdrop.Source != null && MotionVideoBackdrop.Opacity > 0.01)
                    {
                        MotionVideoBackdrop.Opacity = opacity;
                    }
                    if (TvMotionVideoBackdrop != null && TvMotionVideoBackdrop.Source != null && TvMotionVideoBackdrop.Opacity > 0.01)
                    {
                        TvMotionVideoBackdrop.Opacity = opacity;
                    }
                    BackdropValText.Text = $"{(int)e.NewValue}%";
                }
                finally
                {
                    _suppressSettingsUpdate = false;
                }
            }
        }

        private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSettingsUpdate) return;
            if (SpeedValText != null)
            {
                _suppressSettingsUpdate = true;
                try
                {
                    _settings.RotationSpeed = e.NewValue / 100.0;
                    SpeedValText.Text = $"{_settings.RotationSpeed:0.0}x";
                }
                finally
                {
                    _suppressSettingsUpdate = false;
                }
            }
        }

        private void ReactivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSettingsUpdate) return;
            if (ReactivityValText != null)
            {
                _suppressSettingsUpdate = true;
                try
                {
                    _settings.AudioReactivity = e.NewValue / 100.0;
                    ReactivityValText.Text = $"{_settings.AudioReactivity:0.0}x";
                }
                finally
                {
                    _suppressSettingsUpdate = false;
                }
            }
        }

        private void ToggleGlowControls_Click(object sender, RoutedEventArgs e)
        {
            if (QuickGlowControlsCard.Visibility != Visibility.Visible)
            {
                InitSliderValues();
                QuickGlowControlsCard.Visibility = Visibility.Visible;
            }
            else
            {
                QuickGlowControlsCard.Visibility = Visibility.Collapsed;
            }
        }

        private void CloseQuickControls_Click(object sender, RoutedEventArgs e)
        {
            QuickGlowControlsCard.Visibility = Visibility.Collapsed;
        }

        private async void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            await _mediaService.TogglePlayPauseAsync();
        }

        private async void Next_Click(object sender, RoutedEventArgs e)
        {
            await _mediaService.SkipNextAsync();
        }

        private async void Prev_Click(object sender, RoutedEventArgs e)
        {
            await _mediaService.SkipPreviousAsync();
        }

        private async void Shuffle_Click(object sender, RoutedEventArgs e)
        {
            await _mediaService.ToggleShuffleAsync();
        }

        private async void Repeat_Click(object sender, RoutedEventArgs e)
        {
            await _mediaService.ToggleRepeatAsync();
        }

        private void ToggleAudioPulse_Click(object sender, RoutedEventArgs e)
        {
            _audioReactivityEnabled = !_audioReactivityEnabled;
            ReactiveGlowButton.Opacity = _audioReactivityEnabled ? 1.0 : 0.55;
            if (ReactiveGlowText != null)
            {
                ReactiveGlowText.Foreground = _audioReactivityEnabled 
                    ? new SolidColorBrush(Color.FromArgb(160, 255, 255, 255))
                    : new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
            }
            if (ReactiveGlowIcon != null)
            {
                ReactiveGlowIcon.Fill = _audioReactivityEnabled 
                    ? System.Windows.Media.Brushes.White 
                    : new SolidColorBrush(Color.FromArgb(140, 255, 255, 255));
            }
            if (ReactiveGlowShadow != null)
            {
                ReactiveGlowShadow.Opacity = _audioReactivityEnabled ? 0.45 : 0.0;
            }
            ReactiveGlowButton.ToolTip = _audioReactivityEnabled 
                ? "Reactive Glow: Active (Click to pause reactivity)" 
                : "Reactive Glow: Paused (Click to enable reactivity)";
        }

        private void ReactiveGlowButton_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (ReactiveGlowText != null)
            {
                ReactiveGlowText.Margin = new Thickness(7, 0, 4, 0);
                var widthAnim = new DoubleAnimation
                {
                    To = 86.0,
                    Duration = TimeSpan.FromMilliseconds(200),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                var opacityAnim = new DoubleAnimation
                {
                    To = 1.0,
                    Duration = TimeSpan.FromMilliseconds(200),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                ReactiveGlowText.BeginAnimation(WidthProperty, widthAnim);
                ReactiveGlowText.BeginAnimation(OpacityProperty, opacityAnim);
            }
        }

        private void ReactiveGlowButton_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (ReactiveGlowText != null)
            {
                var widthAnim = new DoubleAnimation
                {
                    To = 0.0,
                    Duration = TimeSpan.FromMilliseconds(180),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                };
                var opacityAnim = new DoubleAnimation
                {
                    To = 0.0,
                    Duration = TimeSpan.FromMilliseconds(150),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                };
                widthAnim.Completed += (s, ev) =>
                {
                    if (ReactiveGlowText != null && !ReactiveGlowButton.IsMouseOver)
                    {
                        ReactiveGlowText.Margin = new Thickness(0);
                    }
                };
                ReactiveGlowText.BeginAnimation(WidthProperty, widthAnim);
                ReactiveGlowText.BeginAnimation(OpacityProperty, opacityAnim);
            }
        }

        private void GearButton_Click(object sender, RoutedEventArgs e)
        {
            if (TopGearButton.ContextMenu != null)
            {
                TopGearButton.ContextMenu.PlacementTarget = TopGearButton;
                TopGearButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                TopGearButton.ContextMenu.VerticalOffset = 8;
                TopGearButton.ContextMenu.IsOpen = true;
            }
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            var settingsWin = new SettingsWindow();
            settingsWin.Owner = this;
            settingsWin.Topmost = true;
            settingsWin.ShowDialog();
        }

        private void SwitchToOverlay_Click(object sender, RoutedEventArgs e)
        {
            RestoreTaskbarToTopmost();
            if (_isFullscreenMotionActive) ExitFullscreenMotionMode();
            try { AlbumCoverVideo?.Pause(); } catch { }
            ((App)Application.Current).ShowDesktopOverlay();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            RestoreTaskbarToTopmost();
            if (_isFullscreenMotionActive) ExitFullscreenMotionMode();
            try { AlbumCoverVideo?.Pause(); } catch { }
            ((App)Application.Current).ShowDesktopOverlay();
        }

        // =========================================================
        // CUSTOM GLOW COLOR OVERRIDE & SCREEN COLOR DROPPER
        // =========================================================

        public void ApplyCustomColor(Color color)
        {
            _customColorOverride = color;
            _customColorSongKey = _currentSongKey;

            ApplyGlowColor(color);
            UpdateColorOverrideUI();
        }

        public void ResetToAutoColor()
        {
            _customColorOverride = null;
            _customColorSongKey = null;

            Color dom = _mediaService.CurrentTrack?.DominantColor ?? Color.FromRgb(220, 20, 60);
            ApplyGlowColor(dom);
            UpdateColorOverrideUI();
        }

        private void UpdateColorOverrideUI()
        {
            if (ColorModeBadgeText == null || ColorModeBadge == null) return;

            if (_customColorOverride.HasValue)
            {
                Color c = _customColorOverride.Value;
                ColorModeBadgeText.Text = $"Custom (#{c.R:X2}{c.G:X2}{c.B:X2})";
                ColorModeBadge.Background = new SolidColorBrush(Color.FromArgb(90, c.R, c.G, c.B));
                ColorModeBadgeText.Foreground = System.Windows.Media.Brushes.White;

                // De-activate auto checkmark & glowing ring
                if (ColorAutoCheckmark != null) ColorAutoCheckmark.Visibility = Visibility.Collapsed;
                if (ColorAutoButton != null)
                {
                    ColorAutoButton.BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
                    ColorAutoButton.BorderThickness = new Thickness(1);
                    if (ColorAutoButton.Effect is DropShadowEffect autoEffect) autoEffect.Opacity = 0.0;
                }
            }
            else
            {
                ColorModeBadgeText.Text = "Auto (Album Art)";
                ColorModeBadge.Background = new SolidColorBrush(Color.FromArgb(37, 255, 255, 255));
                ColorModeBadgeText.Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255));

                // Activate auto checkmark & glowing ring (as shown in screenshot 2)
                if (ColorAutoCheckmark != null) ColorAutoCheckmark.Visibility = Visibility.Visible;
                if (ColorAutoButton != null)
                {
                    ColorAutoButton.BorderBrush = System.Windows.Media.Brushes.White;
                    ColorAutoButton.BorderThickness = new Thickness(2);
                    if (ColorAutoButton.Effect is DropShadowEffect autoEffect)
                    {
                        autoEffect.Color = Colors.White;
                        autoEffect.BlurRadius = 8;
                        autoEffect.Opacity = 0.85;
                    }
                }
            }
        }

        private void StartColorDropper_Click(object sender, RoutedEventArgs e)
        {
            StartColorDropper();
        }

        private void StartColorDropper()
        {
            _isColorDropperActive = true;
            ColorDropperOverlay.Visibility = Visibility.Visible;
            ColorDropperOverlay.CaptureMouse();

            Color initialColor = GetScreenPixelColor();
            LoupeColorCircle.Background = new SolidColorBrush(initialColor);
            LoupeHexText.Text = $"#{initialColor.R:X2}{initialColor.G:X2}{initialColor.B:X2}";

            if (GetCursorPos(out Win32Point pt))
            {
                Point wpfPt = PointFromScreen(new Point(pt.X, pt.Y));
                PositionLoupe(wpfPt);
            }
        }

        private void StopColorDropper()
        {
            if (!_isColorDropperActive) return;
            _isColorDropperActive = false;
            ColorDropperOverlay.ReleaseMouseCapture();
            ColorDropperOverlay.Visibility = Visibility.Collapsed;
        }

        private void PositionLoupe(Point pt)
        {
            double loupeW = 168.0;
            double loupeH = 54.0;
            double x = pt.X + 18.0;
            double y = pt.Y - 60.0;

            if (x + loupeW > ActualWidth - 12.0)
            {
                x = pt.X - loupeW - 18.0;
            }
            if (y < 12.0)
            {
                y = pt.Y + 24.0;
            }
            if (y + loupeH > ActualHeight - 12.0)
            {
                y = ActualHeight - loupeH - 12.0;
            }

            Canvas.SetLeft(DropperLoupe, Math.Max(10.0, x));
            Canvas.SetTop(DropperLoupe, Math.Max(10.0, y));
        }

        private void DropperOverlay_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_isColorDropperActive) return;

            Point pt = e.GetPosition(ColorDropperOverlay);
            PositionLoupe(pt);

            Color color = GetScreenPixelColor();
            LoupeColorCircle.Background = new SolidColorBrush(color);
            LoupeHexText.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        private void DropperOverlay_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!_isColorDropperActive) return;

            Color pickedColor = GetScreenPixelColor();
            ApplyCustomColor(pickedColor);
            StopColorDropper();
        }

        private void DropperOverlay_MouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            StopColorDropper();
        }

        private Color GetScreenPixelColor()
        {
            try
            {
                if (GetCursorPos(out Win32Point pt))
                {
                    IntPtr hdc = GetDC(IntPtr.Zero);
                    if (hdc != IntPtr.Zero)
                    {
                        uint pixel = GetPixel(hdc, pt.X, pt.Y);
                        ReleaseDC(IntPtr.Zero, hdc);

                        // In GDI COLORREF, format is 0x00bbggrr
                        if ((pixel & 0xFF000000) == 0)
                        {
                            byte r = (byte)(pixel & 0xFF);
                            byte g = (byte)((pixel >> 8) & 0xFF);
                            byte b = (byte)((pixel >> 16) & 0xFF);
                            return Color.FromRgb(r, g, b);
                        }
                    }
                }
            }
            catch { }
            return Colors.White;
        }

        private void ColorSwatch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string hex)
            {
                try
                {
                    var col = (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
                    ApplyCustomColor(col);
                }
                catch { }
            }
        }

        private void ColorAuto_Click(object sender, RoutedEventArgs e)
        {
            ResetToAutoColor();
        }

        private void MoreColors_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.ColorDialog
            {
                FullOpen = true,
                AnyColor = true
            };
            if (_customColorOverride.HasValue)
            {
                var c = _customColorOverride.Value;
                dlg.Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B);
            }
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                var col = Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B);
                ApplyCustomColor(col);
            }
        }

        private async void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (_isColorDropperActive)
                {
                    StopColorDropper();
                    e.Handled = true;
                    return;
                }
                if (QuickGlowControlsCard.Visibility == Visibility.Visible)
                {
                    QuickGlowControlsCard.Visibility = Visibility.Collapsed;
                    e.Handled = true;
                    return;
                }
                if (_isFullscreenMotionActive)
                {
                    ExitFullscreenMotionMode();
                    e.Handled = true;
                    return;
                }
                if (_isLyricsMode)
                {
                    SetLyricsMode(false);
                    e.Handled = true;
                    return;
                }
                RestoreTaskbarToTopmost();
                SwitchToOverlay_Click(sender, e);
            }
            else if (_isFullscreenMotionActive && (e.Key == Key.M || e.Key == Key.F))
            {
                ExitFullscreenMotionMode();
                e.Handled = true;
            }
            else if ((e.Key == Key.M || e.Key == Key.F) && !_isLyricsMode && _isMotionAvailable)
            {
                EnterFullscreenMotionMode();
                e.Handled = true;
            }
            else if (e.Key == Key.F11)
            {
                SetRestoredMode(!_isRestoredMode);
                e.Handled = true;
            }
            else if (e.Key == Key.L)
            {
                if (_isLyricsMode)
                {
                    SetLyricsMode(false);
                    e.Handled = true;
                }
                else if (_currentLyrics != null && _currentLyrics.HasLyrics)
                {
                    SetLyricsMode(true);
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.S && _isLyricsMode)
            {
                SetLyricDisplayStyle(_currentDisplayStyle == LyricDisplayStyle.Fisheye ? LyricDisplayStyle.Stage : LyricDisplayStyle.Fisheye);
                e.Handled = true;
            }
            else if (e.Key == Key.Space)
            {
                await _mediaService.TogglePlayPauseAsync();
            }
            else if (e.Key == Key.Right)
            {
                await _mediaService.SkipNextAsync();
            }
            else if (e.Key == Key.Left)
            {
                await _mediaService.SkipPreviousAsync();
            }
            else if (e.Key == Key.Up)
            {
                _volumeService.StepVolume(0.05f);
                ExpandVolumeControl(autoCollapse: true);
                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                _volumeService.StepVolume(-0.05f);
                ExpandVolumeControl(autoCollapse: true);
                e.Handled = true;
            }
            else if (e.Key == Key.M)
            {
                _volumeService.ToggleMute();
                ExpandVolumeControl(autoCollapse: true);
                e.Handled = true;
            }
        }

        // =========================================================
        // FULLSCREEN TASKBAR HOVER & ACCESS MANAGEMENT
        // =========================================================

        private void TaskbarHoverEdge_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_isRestoredMode)
            {
                RevealTaskbar();
            }
        }

        private void TaskbarHoverEdge_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_isRestoredMode && !_isTaskbarRevealed)
            {
                RevealTaskbar();
            }
        }

        private void TaskbarHoverEdge_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!_isRestoredMode)
            {
                RevealTaskbar();
                IntPtr taskbarHwnd = FindWindow("Shell_TrayWnd", null);
                if (taskbarHwnd != IntPtr.Zero)
                {
                    SetForegroundWindow(taskbarHwnd);
                }
            }
        }

        private void UpdateTaskbarVisibilityOnHover()
        {
            if (_isRestoredMode || WindowState == WindowState.Minimized || !IsVisible)
            {
                if (_isTaskbarRevealed)
                {
                    ResetTaskbarZOrder();
                }
                return;
            }

            if (!GetCursorPos(out Win32Point pt)) return;

            double screenH = ActualHeight > 100 ? ActualHeight : SystemParameters.PrimaryScreenHeight;
            double screenW = ActualWidth > 100 ? ActualWidth : SystemParameters.PrimaryScreenWidth;

            // Is the cursor near the bottom edge of the screen (bottom 5px)?
            bool isAtBottomEdge = pt.Y >= (screenH - 5) && pt.X >= 0 && pt.X <= screenW;

            if (!isAtBottomEdge && !_isTaskbarRevealed)
            {
                return;
            }

            if (_cachedTaskbarHwnd == IntPtr.Zero)
            {
                _cachedTaskbarHwnd = FindWindow("Shell_TrayWnd", null);
            }
            if (_cachedTaskbarHwnd == IntPtr.Zero) return;

            GetWindowRect(_cachedTaskbarHwnd, out Win32Rect tbRect);

            // True if mouse is anywhere over the taskbar bounds
            bool isCursorOverTaskbar = pt.X >= tbRect.Left && pt.X <= tbRect.Right && pt.Y >= tbRect.Top && pt.Y <= tbRect.Bottom;

            if (isAtBottomEdge || isCursorOverTaskbar)
            {
                if (!_isTaskbarRevealed)
                {
                    RevealTaskbar();
                }
            }
            else if (_isTaskbarRevealed)
            {
                // Cursor moved back up into the visualizer content (above taskbar)
                int threshold = tbRect.Top > 0 ? (tbRect.Top - 12) : (int)(screenH - 60);
                if (pt.Y < threshold)
                {
                    IntPtr fg = GetForegroundWindow();
                    IntPtr secTaskbar = FindWindow("Shell_SecondaryTrayWnd", null);

                    // If user is actively focused on taskbar or Start menu, keep it visible
                    if (fg == _windowHandle || (fg != _cachedTaskbarHwnd && fg != secTaskbar))
                    {
                        ResetTaskbarZOrder();
                    }
                }
            }
        }

        public void RevealTaskbar()
        {
            IntPtr taskbarHwnd = FindWindow("Shell_TrayWnd", null);
            if (taskbarHwnd != IntPtr.Zero)
            {
                SetWindowPos(taskbarHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            }
            IntPtr secTaskbar = FindWindow("Shell_SecondaryTrayWnd", null);
            if (secTaskbar != IntPtr.Zero)
            {
                SetWindowPos(secTaskbar, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            }
            _isTaskbarRevealed = true;
        }

        public void RestoreTaskbarToTopmost()
        {
            try
            {
                IntPtr taskbarHwnd = FindWindow("Shell_TrayWnd", null);
                if (taskbarHwnd != IntPtr.Zero)
                {
                    SetWindowPos(taskbarHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                }
                IntPtr secTaskbar = FindWindow("Shell_SecondaryTrayWnd", null);
                if (secTaskbar != IntPtr.Zero)
                {
                    SetWindowPos(secTaskbar, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                }
                _isTaskbarRevealed = true;
            }
            catch { }
        }

        public void ResetTaskbarZOrder()
        {
            if (_isRestoredMode)
            {
                RestoreTaskbarToTopmost();
                return;
            }

            try
            {
                IntPtr taskbarHwnd = FindWindow("Shell_TrayWnd", null);
                if (taskbarHwnd != IntPtr.Zero)
                {
                    SetWindowPos(taskbarHwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
                IntPtr secTaskbar = FindWindow("Shell_SecondaryTrayWnd", null);
                if (secTaskbar != IntPtr.Zero)
                {
                    SetWindowPos(secTaskbar, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
                _isTaskbarRevealed = false;
            }
            catch { }
        }

        // =========================================================
        // WINDOW STATE & RESTORE / FULLSCREEN MANAGEMENT
        // =========================================================

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            RestoreTaskbarToTopmost();
            WindowState = WindowState.Minimized;
        }

        private void ToggleRestore_Click(object sender, RoutedEventArgs e)
        {
            SetRestoredMode(!_isRestoredMode);
        }

        public void SetRestoredMode(bool restored)
        {
            _isRestoredMode = restored;

            if (_isRestoredMode)
            {
                RestoreTaskbarToTopmost();

                // 1. Calculate restored position and size
                double targetW = _savedRestoredBounds.Width > 400 ? _savedRestoredBounds.Width : 980;
                double targetH = _savedRestoredBounds.Height > 400 ? _savedRestoredBounds.Height : 680;
                double targetL = _savedRestoredBounds.Left;
                double targetT = _savedRestoredBounds.Top;

                if (targetL <= 0 || targetT <= 0 || targetL + targetW > SystemParameters.PrimaryScreenWidth || targetT + targetH > SystemParameters.PrimaryScreenHeight)
                {
                    targetL = Math.Max(20, (SystemParameters.PrimaryScreenWidth - targetW) / 2);
                    targetT = Math.Max(20, (SystemParameters.PrimaryScreenHeight - targetH) / 2);
                }

                WindowState = WindowState.Normal;
                Left = targetL;
                Top = targetT;
                Width = targetW;
                Height = targetH;

                // 2. Liquid Glass border with rounded squircle corners
                MainRootBorder.CornerRadius = new CornerRadius(22);
                MainRootBorder.BorderThickness = new Thickness(1);
                MainRootBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));

                // 3. Compact elegant centerpiece layout for restored window
                AlbumArtContainer.Width = 320;
                AlbumArtContainer.Height = 320;
                AlbumArtContainer.CornerRadius = new CornerRadius(22);
                CenterContentStack.Width = 380;
                CenterContentStack.Margin = new Thickness(0, 16, 0, 12);
                _expandedVolumeWidth = 206;
                if (VolumeSlider != null) VolumeSlider.Width = 110;
                if (VolumeControlContainer != null)
                {
                    VolumeControlContainer.BeginAnimation(WidthProperty, null);
                    VolumeControlContainer.Width = _isVolumeExpanded ? _expandedVolumeWidth : CollapsedVolumeWidth;
                }

                // 4. Update Icons and ToolTips
                RestoreIcon.Text = "\uE922"; // ChromeMaximize (single square)
                RestoreButton.ToolTip = "Maximize (Fullscreen)";
                if (MenuRestoreToggle != null)
                {
                    MenuRestoreToggle.Header = "Maximize (Fullscreen)";
                }
            }
            else
            {
                // 1. Save restored bounds for next time
                _savedRestoredBounds = new Rect(Left, Top, Width, Height);

                // 2. Expand to true Fullscreen
                WindowState = WindowState.Normal;
                Left = 0;
                Top = 0;
                Width = SystemParameters.PrimaryScreenWidth;
                Height = Math.Max(1, SystemParameters.PrimaryScreenHeight - 1);

                // 3. Borderless edge-to-edge
                MainRootBorder.CornerRadius = new CornerRadius(0);
                MainRootBorder.BorderThickness = new Thickness(0);

                // 4. Large hero centerpiece layout
                AlbumArtContainer.Width = 450;
                AlbumArtContainer.Height = 450;
                AlbumArtContainer.CornerRadius = new CornerRadius(28);
                CenterContentStack.Width = 450;
                CenterContentStack.Margin = new Thickness(0, 0, 0, 12);
                _expandedVolumeWidth = 236;
                if (VolumeSlider != null) VolumeSlider.Width = 140;
                if (VolumeControlContainer != null)
                {
                    VolumeControlContainer.BeginAnimation(WidthProperty, null);
                    VolumeControlContainer.Width = _isVolumeExpanded ? _expandedVolumeWidth : CollapsedVolumeWidth;
                }

                // 5. Update Icons and ToolTips
                RestoreIcon.Text = "\uE923"; // ChromeRestore (two overlapping squares)
                RestoreButton.ToolTip = "Restore Down (Windowed Mode)";
                if (MenuRestoreToggle != null)
                {
                    MenuRestoreToggle.Header = "Restore (Windowed)";
                }
            }

            if (_isLyricsMode)
            {
                UpdateLyricsModeUI();
                _activeLineIndex = -1;
            }

            // Adapt reactive screen-edge beam to new geometry
            ApplyGeometry();
            UpdateWindowClip();

            // Re-sync timeline scrubber width to new container width
            if (!_isUserSeeking)
            {
                UpdatePlaybackTimeUI(_currentTrackPosition, _currentTrackDuration);
            }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_isFullscreenMotionActive)
            {
                if (e.ClickCount == 2)
                {
                    ExitFullscreenMotionMode();
                    e.Handled = true;
                }
                return;
            }

            // Double click toggles between Fullscreen and Restored
            if (e.ClickCount == 2)
            {
                if (e.OriginalSource is DependencyObject dep)
                {
                    if (FindVisualParent<Button>(dep) == null &&
                        FindVisualParent<Slider>(dep) == null &&
                        FindVisualParent<System.Windows.Controls.Primitives.Thumb>(dep) == null &&
                        e.Source != TimelineTrackContainer &&
                        e.Source != VolumeControlContainer &&
                        (VolumeControlContainer == null || !VolumeControlContainer.IsAncestorOf(dep)) &&
                        e.Source != AlbumArtContainer &&
                        (AlbumArtContainer == null || !AlbumArtContainer.IsAncestorOf(dep)))
                    {
                        ToggleRestore_Click(sender, e);
                        return;
                    }
                }
            }

            // In restored mode, click-drag on background moves the window
            if (_isRestoredMode && e.ButtonState == MouseButtonState.Pressed)
            {
                if (e.OriginalSource is DependencyObject dep)
                {
                    if (FindVisualParent<Button>(dep) == null &&
                        FindVisualParent<Slider>(dep) == null &&
                        FindVisualParent<System.Windows.Controls.Primitives.Thumb>(dep) == null &&
                        e.Source != TimelineTrackContainer &&
                        e.Source != VolumeControlContainer &&
                        (VolumeControlContainer == null || !VolumeControlContainer.IsAncestorOf(dep)))
                    {
                        DragMove();
                    }
                }
            }
        }

        private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
        {
            var current = child;
            while (current != null)
            {
                if (current is T match) return match;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        // =========================================================
        // VOLUME CONTROL EVENT HANDLERS
        // =========================================================

        private void OnSystemVolumeChanged(float vol, bool isMuted)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateVolumeUI(vol, isMuted);
                if (IsActive)
                {
                    ExpandVolumeControl(autoCollapse: true);
                }
            });
        }

        private void UpdateVolumeUI(float vol, bool isMuted)
        {
            _isUpdatingVolumeFromService = true;
            try
            {
                double percent = Math.Clamp(Math.Round(vol * 100.0), 0.0, 100.0);
                if (VolumeSlider != null && Math.Abs(VolumeSlider.Value - percent) > 0.4)
                {
                    VolumeSlider.Value = percent;
                }
                if (QuickVolumeSlider != null && Math.Abs(QuickVolumeSlider.Value - percent) > 0.4)
                {
                    QuickVolumeSlider.Value = percent;
                }

                if (VolumePercentText != null)
                {
                    VolumePercentText.Text = isMuted ? "Mute" : $"{(int)percent}%";
                }
                if (QuickVolumeValText != null)
                {
                    QuickVolumeValText.Text = isMuted ? "Mute" : $"{(int)percent}%";
                }

                if (VolumeIcon != null)
                {
                    if (isMuted || percent <= 0.0)
                    {
                        VolumeIcon.Text = "\uE74F"; // Mute
                    }
                    else if (percent < 33.0)
                    {
                        VolumeIcon.Text = "\uE992"; // Low volume
                    }
                    else if (percent < 66.0)
                    {
                        VolumeIcon.Text = "\uE994"; // Mid volume
                    }
                    else
                    {
                        VolumeIcon.Text = "\uE767"; // High volume
                    }
                }
            }
            finally
            {
                _isUpdatingVolumeFromService = false;
            }
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingVolumeFromService) return;
            float newVol = (float)(e.NewValue / 100.0);
            _volumeService.MasterVolume = newVol;
            if (newVol > 0f && _volumeService.IsMuted)
            {
                _volumeService.IsMuted = false;
            }
            UpdateVolumeUI(newVol, _volumeService.IsMuted);
        }

        private void QuickVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingVolumeFromService) return;
            float newVol = (float)(e.NewValue / 100.0);
            _volumeService.MasterVolume = newVol;
            if (newVol > 0f && _volumeService.IsMuted)
            {
                _volumeService.IsMuted = false;
            }
            UpdateVolumeUI(newVol, _volumeService.IsMuted);
        }

        private void VolumeMuteButton_Click(object sender, RoutedEventArgs e)
        {
            _volumeService.ToggleMute();
            UpdateVolumeUI(_volumeService.MasterVolume, _volumeService.IsMuted);
        }

        private void VolumeControl_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            float step = e.Delta > 0 ? 0.03f : -0.03f;
            _volumeService.StepVolume(step);
            ExpandVolumeControl(autoCollapse: true);
            e.Handled = true;
        }

        // =========================================================
        // EXPANDABLE HOVER VOLUME CONTROL
        // =========================================================

        private void ExpandVolumeControl(bool autoCollapse = false)
        {
            _volumeCollapseTimer?.Stop();

            if (!_isVolumeExpanded)
            {
                _isVolumeExpanded = true;
                if (VolumeSliderPanel != null)
                {
                    VolumeSliderPanel.Visibility = Visibility.Visible;
                    var opacityAnim = new DoubleAnimation
                    {
                        To = 1.0,
                        Duration = TimeSpan.FromMilliseconds(200),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    VolumeSliderPanel.BeginAnimation(OpacityProperty, opacityAnim);
                }

                if (VolumeControlContainer != null)
                {
                    var widthAnim = new DoubleAnimation
                    {
                        To = _expandedVolumeWidth,
                        Duration = TimeSpan.FromMilliseconds(240),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    VolumeControlContainer.BeginAnimation(WidthProperty, widthAnim);
                }
            }

            if (autoCollapse)
            {
                StartVolumeCollapseTimer(1800);
            }
        }

        private void CollapseVolumeControl(bool immediate = false)
        {
            if (!_isVolumeExpanded) return;
            if (_isDraggingVolumeSlider) return;
            if (VolumeControlContainer != null && VolumeControlContainer.IsMouseOver) return;

            _volumeCollapseTimer?.Stop();
            _isVolumeExpanded = false;

            if (immediate)
            {
                if (VolumeControlContainer != null)
                {
                    VolumeControlContainer.BeginAnimation(WidthProperty, null);
                    VolumeControlContainer.Width = CollapsedVolumeWidth;
                }
                if (VolumeSliderPanel != null)
                {
                    VolumeSliderPanel.BeginAnimation(OpacityProperty, null);
                    VolumeSliderPanel.Opacity = 0.0;
                    VolumeSliderPanel.Visibility = Visibility.Collapsed;
                }
                return;
            }

            if (VolumeSliderPanel != null)
            {
                var opacityAnim = new DoubleAnimation
                {
                    To = 0.0,
                    Duration = TimeSpan.FromMilliseconds(160),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                };
                VolumeSliderPanel.BeginAnimation(OpacityProperty, opacityAnim);
            }

            if (VolumeControlContainer != null)
            {
                var widthAnim = new DoubleAnimation
                {
                    To = CollapsedVolumeWidth,
                    Duration = TimeSpan.FromMilliseconds(250),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                };
                widthAnim.Completed += (s, e) =>
                {
                    if (!_isVolumeExpanded && VolumeSliderPanel != null)
                    {
                        VolumeSliderPanel.Visibility = Visibility.Collapsed;
                    }
                };
                VolumeControlContainer.BeginAnimation(WidthProperty, widthAnim);
            }
        }

        private void StartVolumeCollapseTimer(int milliseconds = 1800)
        {
            if (_volumeCollapseTimer == null)
            {
                _volumeCollapseTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(milliseconds)
                };
                _volumeCollapseTimer.Tick += (s, e) =>
                {
                    _volumeCollapseTimer.Stop();
                    CollapseVolumeControl();
                };
            }
            else
            {
                _volumeCollapseTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
            }
            _volumeCollapseTimer.Stop();
            _volumeCollapseTimer.Start();
        }

        private void VolumeControl_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            ExpandVolumeControl(autoCollapse: false);
        }

        private void VolumeControl_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_isDraggingVolumeSlider)
            {
                StartVolumeCollapseTimer(250);
            }
        }

        private void VolumeSlider_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _isDraggingVolumeSlider = true;
            _volumeCollapseTimer?.Stop();
        }

        private void VolumeSlider_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _isDraggingVolumeSlider = false;
            if (VolumeControlContainer != null && !VolumeControlContainer.IsMouseOver)
            {
                StartVolumeCollapseTimer(600);
            }
        }

        private void VolumeSlider_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _isDraggingVolumeSlider = false;
            if (VolumeControlContainer != null && !VolumeControlContainer.IsMouseOver)
            {
                StartVolumeCollapseTimer(600);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            RestoreTaskbarToTopmost();
            _lyricsCts?.Cancel();
            _motionCts?.Cancel();
            _motionHintTimer?.Stop();
            _albumVideoFrameTimer?.Stop();
            _albumVideoFrameTimer = null;
            _fullscreenVideoFrameTimer?.Stop();
            _fullscreenVideoFrameTimer = null;
            try { AlbumCoverVideo?.Stop(); } catch { }
            try { FullscreenMotionVideo?.Stop(); } catch { }
            _userScrollResumeTimer?.Stop();
            _volumeCollapseTimer?.Stop();
            CompositionTarget.Rendering -= OnRenderFrame;
            _mediaService.TrackUpdated -= OnTrackUpdated;
            _mediaService.TimelineUpdated -= OnTimelineUpdated;
            _mediaService.PlaybackStateChanged -= OnPlaybackStateChanged;
            _settings.SettingsChanged -= OnSettingsChanged;
            _volumeService.VolumeChanged -= OnSystemVolumeChanged;
            MotionArtworkService.Instance.VideoCached -= OnMotionVideoCached;
            IsVisibleChanged -= FullscreenVisualizerWindow_IsVisibleChanged;
            base.OnClosed(e);
        }

        // =========================================================
        // FULLSCREEN TIMED LYRICS WITH AUDIO-REACTIVE ON-BEAT EFFECTS
        // =========================================================

        private void ToggleLyrics_Click(object sender, RoutedEventArgs e)
        {
            if (_isLyricsMode)
            {
                SetLyricsMode(false);
            }
            else if (_currentLyrics != null && _currentLyrics.HasLyrics)
            {
                SetLyricsMode(true);
            }
        }

        public void SetLyricsMode(bool enabled)
        {
            if (enabled && (_currentLyrics == null || !_currentLyrics.HasLyrics))
            {
                return;
            }

            _isLyricsMode = enabled;
            UpdateLyricsModeUI();
        }

        public void UpdateLyricsButtonAvailability(bool hasLyrics, bool isInstrumental, bool isChecking = false)
        {
            if (LyricsButton != null)
            {
                if (isChecking)
                {
                    if (!_isLyricsMode)
                    {
                        LyricsButton.IsEnabled = false;
                        LyricsButton.Opacity = 0.50;
                        LyricsButton.Cursor = System.Windows.Input.Cursors.Wait;
                        LyricsButton.ToolTip = "Checking for timed lyrics...";
                        if (LyricsButtonIcon != null)
                        {
                            LyricsButtonIcon.Foreground = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255));
                        }
                    }
                }
                else
                {
                    if (_isLyricsMode)
                    {
                        LyricsButton.IsEnabled = true;
                        LyricsButton.Opacity = 1.0;
                        LyricsButton.Cursor = System.Windows.Input.Cursors.Hand;
                        LyricsButton.ToolTip = "Close Lyrics Mode (L)";
                        if (LyricsButtonIcon != null)
                        {
                            LyricsButtonIcon.Foreground = new SolidColorBrush(Color.FromRgb(0, 230, 118));
                        }
                    }
                    else
                    {
                        LyricsButton.IsEnabled = hasLyrics;
                        LyricsButton.Opacity = hasLyrics ? 1.0 : 0.30;
                        LyricsButton.Cursor = hasLyrics ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.No;
                        LyricsButton.ToolTip = !hasLyrics
                            ? "Lyrics not available for this song"
                            : (isInstrumental ? "Instrumental Track (L)" : "Timed Lyrics Mode (L)");

                        if (LyricsButtonIcon != null)
                        {
                            LyricsButtonIcon.Foreground = hasLyrics
                                ? new SolidColorBrush(Color.FromArgb(216, 255, 255, 255))
                                : new SolidColorBrush(Color.FromArgb(80, 255, 255, 255));
                        }
                    }
                }
            }

            if (MenuLyricsToggle != null)
            {
                if (isChecking)
                {
                    MenuLyricsToggle.IsEnabled = false;
                    MenuLyricsToggle.Header = "Checking Lyrics...";
                }
                else
                {
                    MenuLyricsToggle.IsEnabled = hasLyrics || _isLyricsMode;
                    MenuLyricsToggle.Header = !hasLyrics
                        ? (_isLyricsMode ? "Close Lyrics (L)" : "Timed Lyrics (Unavailable)")
                        : (isInstrumental ? "Instrumental Track (L)" : (_isLyricsMode ? "Close Lyrics (L)" : "Timed Lyrics (L)"));
                }
            }
        }

        private void UpdateLyricsModeUI()
        {
            bool hasLyrics = _currentLyrics != null && _currentLyrics.HasLyrics;
            bool isInst = _currentLyrics != null && _currentLyrics.IsInstrumental;
            UpdateLyricsButtonAvailability(hasLyrics, isInst);

            if (FullscreenLyricsStage == null || VisualizerContentGrid == null) return;

            if (_isLyricsMode)
            {
                VisualizerContentGrid.Visibility = Visibility.Collapsed;
                FullscreenLyricsStage.Visibility = Visibility.Visible;
                SetLyricDisplayStyle(_currentDisplayStyle);

                if (_mediaService.CurrentTrack != null)
                {
                    LyricsSongTitleText.Text = _mediaService.CurrentTrack.Title ?? "Ready to Play";
                    LyricsArtistText.Text = _mediaService.CurrentTrack.Artist ?? "Play music to begin";
                }

                if (_currentLyrics == null)
                {
                    _ = LoadLyricsForCurrentSongAsync(_mediaService.CurrentTrack);
                }
            }
            else
            {
                FullscreenLyricsStage.Visibility = Visibility.Collapsed;
                VisualizerContentGrid.Visibility = Visibility.Visible;
            }
        }

        private void ExitLyrics_Click(object sender, RoutedEventArgs e)
        {
            SetLyricsMode(false);
        }

        private void LyricsStyleToggle_Click(object sender, RoutedEventArgs e)
        {
            SetLyricDisplayStyle(_currentDisplayStyle == LyricDisplayStyle.Fisheye ? LyricDisplayStyle.Stage : LyricDisplayStyle.Fisheye);
        }

        private void SetLyricDisplayStyle(LyricDisplayStyle style)
        {
            _currentDisplayStyle = style;

            if (FisheyeLyricsCanvas == null || LyricsScrollViewer == null) return;

            if (style == LyricDisplayStyle.Fisheye)
            {
                FisheyeLyricsCanvas.Visibility = Visibility.Visible;
                LyricsScrollViewer.Visibility = Visibility.Collapsed;
                if (SyncLyricsPillButton != null) SyncLyricsPillButton.Visibility = Visibility.Collapsed;
                if (LyricsStyleIcon != null) LyricsStyleIcon.Text = "🐟";
                if (LyricsStyleText != null) LyricsStyleText.Text = "Fisheye Lens";
                if (LyricsStyleToggleButton != null) LyricsStyleToggleButton.ToolTip = "Switch to Stage View (S)";
            }
            else
            {
                FisheyeLyricsCanvas.Visibility = Visibility.Collapsed;
                LyricsScrollViewer.Visibility = Visibility.Visible;
                if (LyricsStyleIcon != null) LyricsStyleIcon.Text = "📜";
                if (LyricsStyleText != null) LyricsStyleText.Text = "Stage View";
                if (LyricsStyleToggleButton != null) LyricsStyleToggleButton.ToolTip = "Switch to Fisheye Lens (S)";
                _isUserScrolledAway = false;
                _activeLineIndex = -1;
            }
        }

        private void InitFisheyeVisualPool()
        {
            if (FisheyeLyricsCanvas == null) return;
            FisheyeLyricsCanvas.Children.Clear();
            _fisheyeNodes.Clear();

            const int poolSize = 13;
            for (int i = 0; i < poolSize; i++)
            {
                var scale = new ScaleTransform(1.0, 1.0);
                var rotate = new RotateTransform(0.0);
                var translate = new TranslateTransform(0.0, 0.0);
                var transformGroup = new TransformGroup();
                transformGroup.Children.Add(scale);
                transformGroup.Children.Add(rotate);
                transformGroup.Children.Add(translate);

                var wordText = new TextBlock
                {
                    Text = "",
                    FontSize = 48,
                    FontWeight = FontWeights.Bold,
                    Foreground = System.Windows.Media.Brushes.White,
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center
                };

                var symbolText = new TextBlock
                {
                    Text = "",
                    FontSize = 26,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center
                };

                var symbolBadge = new Border
                {
                    Margin = new Thickness(14, 0, 0, 0),
                    Padding = new Thickness(6, 2, 6, 2),
                    CornerRadius = new CornerRadius(14),
                    Background = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Visibility = Visibility.Collapsed,
                    Child = symbolText
                };

                var stack = new StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                stack.Children.Add(wordText);
                stack.Children.Add(symbolBadge);

                var pill = new Border
                {
                    CornerRadius = new CornerRadius(32),
                    Padding = new Thickness(32, 10, 32, 10),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = stack
                };

                var container = new Border
                {
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = transformGroup,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = pill,
                    Visibility = Visibility.Collapsed,
                    Cursor = System.Windows.Input.Cursors.Hand
                };

                var node = new FisheyeWordVisualNode
                {
                    Container = container,
                    PillBorder = pill,
                    WordTextBlock = wordText,
                    SymbolBadge = symbolBadge,
                    SymbolTextBlock = symbolText,
                    ScaleTransform = scale,
                    RotateTransform = rotate,
                    TranslateTransform = translate
                };

                container.MouseLeftButtonUp += (s, e) =>
                {
                    if (node.BoundWord != null && !node.BoundWord.IsGap && _currentTrackDuration > TimeSpan.Zero)
                    {
                        _lyricClock.Reset(ToTrackPosition(node.BoundWord.StartTime), _isPlaying);
                        _ = _mediaService.ChangePlaybackPositionAsync(ToTrackPosition(node.BoundWord.StartTime));
                    }
                };

                FisheyeLyricsCanvas.Children.Add(container);
                _fisheyeNodes.Add(node);
            }
        }

        private void BuildFisheyeWords(List<LyricLine> lines)
        {
            _allFisheyeWords.Clear();
            _smoothedFisheyeIndex = 0.0;
            if (lines == null || lines.Count == 0) return;

            int globalIdx = 0;
            for (int lineIdx = 0; lineIdx < lines.Count; lineIdx++)
            {
                var line = lines[lineIdx];

                // Check for instrumental gap / interlude before this line
                if (lineIdx > 0)
                {
                    var prevLine = lines[lineIdx - 1];
                    var gapDuration = line.StartTime - prevLine.EndTime;
                    if (gapDuration > TimeSpan.FromSeconds(3.0))
                    {
                        _allFisheyeWords.Add(new FisheyeWordItem
                        {
                            GlobalIndex = globalIdx++,
                            LineIndex = lineIdx,
                            WordIndex = -1,
                            Text = "♪  ♪  ♪",
                            Symbol = "🎵",
                            StartTime = prevLine.EndTime,
                            EndTime = line.StartTime,
                            IsGap = true
                        });
                    }
                }

                if (line.Words != null && line.Words.Count > 0)
                {
                    for (int wIdx = 0; wIdx < line.Words.Count; wIdx++)
                    {
                        var w = line.Words[wIdx];
                        _allFisheyeWords.Add(new FisheyeWordItem
                        {
                            GlobalIndex = globalIdx++,
                            LineIndex = lineIdx,
                            WordIndex = wIdx,
                            Text = w.Text,
                            Symbol = LyricSymbolMapper.TryGetSymbol(w.Text),
                            StartTime = w.StartTime,
                            EndTime = w.EndTime,
                            IsGap = false
                        });
                    }
                }
                else
                {
                    // Synthesize per-word timing from line timing if words aren't individually timestamped
                    var rawTokens = line.Text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (rawTokens.Length > 0)
                    {
                        var lineDur = (line.EndTime - line.StartTime).TotalSeconds;
                        var durPerWord = lineDur > 0 ? (lineDur / rawTokens.Length) : 0.5;

                        for (int wIdx = 0; wIdx < rawTokens.Length; wIdx++)
                        {
                            var token = rawTokens[wIdx];
                            var wStart = line.StartTime + TimeSpan.FromSeconds(wIdx * durPerWord);
                            var wEnd = line.StartTime + TimeSpan.FromSeconds((wIdx + 1) * durPerWord);

                            _allFisheyeWords.Add(new FisheyeWordItem
                            {
                                GlobalIndex = globalIdx++,
                                LineIndex = lineIdx,
                                WordIndex = wIdx,
                                Text = token,
                                Symbol = LyricSymbolMapper.TryGetSymbol(token),
                                StartTime = wStart,
                                EndTime = wEnd,
                                IsGap = false
                            });
                        }
                    }
                }
            }
        }

        private void BuildFisheyeWordsFromPlain(string plainLyrics)
        {
            _allFisheyeWords.Clear();
            _smoothedFisheyeIndex = 0.0;
            if (string.IsNullOrWhiteSpace(plainLyrics)) return;

            using var reader = new System.IO.StringReader(plainLyrics);
            string? line;
            int globalIdx = 0;
            int lineIdx = 0;
            var currentTime = TimeSpan.FromSeconds(2.0);

            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                var rawTokens = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var lineDuration = TimeSpan.FromSeconds(Math.Max(2.5, rawTokens.Length * 0.45));
                var durPerWord = (lineDuration.TotalSeconds / Math.Max(1, rawTokens.Length));

                for (int wIdx = 0; wIdx < rawTokens.Length; wIdx++)
                {
                    var token = rawTokens[wIdx];
                    var wStart = currentTime + TimeSpan.FromSeconds(wIdx * durPerWord);
                    var wEnd = currentTime + TimeSpan.FromSeconds((wIdx + 1) * durPerWord);

                    _allFisheyeWords.Add(new FisheyeWordItem
                    {
                        GlobalIndex = globalIdx++,
                        LineIndex = lineIdx,
                        WordIndex = wIdx,
                        Text = token,
                        Symbol = LyricSymbolMapper.TryGetSymbol(token),
                        StartTime = wStart,
                        EndTime = wEnd,
                        IsGap = false
                    });
                }

                currentTime += lineDuration + TimeSpan.FromSeconds(0.8);
                lineIdx++;
            }
        }

        private void UpdateFisheyeLyricsFrame(double dt)
        {
            if (_allFisheyeWords.Count == 0 || FisheyeLyricsCanvas == null || _fisheyeNodes.Count == 0) return;

            var position = LyricClockPosition;

            // 1. Locate current word and exact fractional progress
            double targetIndex = 0.0;
            int activeWordIdx = -1;

            for (int i = 0; i < _allFisheyeWords.Count; i++)
            {
                var word = _allFisheyeWords[i];
                if (position >= word.StartTime && position < word.EndTime)
                {
                    activeWordIdx = i;
                    double wordDur = (word.EndTime - word.StartTime).TotalSeconds;
                    double progress = wordDur > 0.001
                        ? Math.Clamp((position - word.StartTime).TotalSeconds / wordDur, 0.0, 1.0)
                        : 0.0;
                    targetIndex = i + (progress * 0.7);
                    break;
                }
            }

            if (activeWordIdx == -1)
            {
                if (position < _allFisheyeWords[0].StartTime)
                {
                    targetIndex = 0.0;
                }
                else if (position >= _allFisheyeWords[_allFisheyeWords.Count - 1].EndTime)
                {
                    targetIndex = _allFisheyeWords.Count - 1;
                }
                else
                {
                    // Between words (inter-word gap)
                    for (int i = 0; i < _allFisheyeWords.Count - 1; i++)
                    {
                        var curr = _allFisheyeWords[i];
                        var next = _allFisheyeWords[i + 1];
                        if (position >= curr.EndTime && position < next.StartTime)
                        {
                            double gapDur = (next.StartTime - curr.EndTime).TotalSeconds;
                            double gapProg = gapDur > 0.001
                                ? Math.Clamp((position - curr.EndTime).TotalSeconds / gapDur, 0.0, 1.0)
                                : 0.0;
                            targetIndex = i + 0.7 + (gapProg * 0.3);
                            break;
                        }
                    }
                }
            }

            // 2. Exponential inertia for organic fluid camera glide
            double glideSpeed = 9.5;
            _smoothedFisheyeIndex += (targetIndex - _smoothedFisheyeIndex) * Math.Min(1.0, dt * glideSpeed);

            double canvasWidth = FisheyeLyricsCanvas.ActualWidth > 10.0 ? FisheyeLyricsCanvas.ActualWidth : ActualWidth;
            double canvasHeight = FisheyeLyricsCanvas.ActualHeight > 10.0 ? FisheyeLyricsCanvas.ActualHeight : ActualHeight;
            if (canvasWidth <= 10.0) canvasWidth = 1200.0;
            if (canvasHeight <= 10.0) canvasHeight = 800.0;

            double centerX = canvasWidth / 2.0;
            double centerY = canvasHeight / 2.0;

            int centerWordIndex = (int)Math.Round(_smoothedFisheyeIndex);
            int halfPool = _fisheyeNodes.Count / 2;

            for (int k = 0; k < _fisheyeNodes.Count; k++)
            {
                var node = _fisheyeNodes[k];
                int offset = k - halfPool;
                int wordIdx = centerWordIndex + offset;

                if (wordIdx < 0 || wordIdx >= _allFisheyeWords.Count)
                {
                    node.Container.Visibility = Visibility.Collapsed;
                    node.BoundWord = null;
                    continue;
                }

                var item = _allFisheyeWords[wordIdx];
                if (!ReferenceEquals(node.BoundWord, item))
                {
                    node.BoundWord = item;
                    node.WordTextBlock.Text = item.Text;

                    // Emoji / Symbol Badge beside word (Verci signature)
                    if (!string.IsNullOrEmpty(item.Symbol))
                    {
                        node.SymbolTextBlock.Text = item.Symbol;
                        node.SymbolBadge.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        node.SymbolBadge.Visibility = Visibility.Collapsed;
                    }
                }
                node.Container.Visibility = Visibility.Visible;

                // Distance from exact smoothed focal point
                double d = wordIdx - _smoothedFisheyeIndex;

                // A. Convex Fisheye Vertical Compression:
                // Non-linear arctan curve compresses receding words towards the glass poles
                double maxH = canvasHeight * 0.44;
                double wordSpacing = _isRestoredMode ? 74.0 : 88.0;
                double yOffset = maxH * (2.0 / Math.PI) * Math.Atan((d * wordSpacing) / (maxH * 0.68));
                double y = centerY + yOffset;

                // B. Convex Magnification & Gaussian Bulge:
                // Apex (d=0) is closest to glass (magnified ~1.30x), tapering gracefully to ~0.38x
                double gaussian = Math.Exp(-(d * d) / 1.85);
                double scale = 0.38 + (0.90 * gaussian);
                node.ScaleTransform.ScaleX = scale;
                node.ScaleTransform.ScaleY = scale;

                // C. Spherical Barrel Tilt:
                // Slight rotation tangent to convex spherical curvature
                double barrelAngle = -3.2 * Math.Clamp(d, -1.8, 1.8);
                node.RotateTransform.Angle = barrelAngle;

                // D. Depth Falloff & Atmosphere:
                double opacity = Math.Clamp(0.06 + (0.94 * Math.Exp(-(d * d) / 2.5)), 0.0, 1.0);
                node.Container.Opacity = opacity;

                // E. Verci Active Highlight Pill:
                double absDist = Math.Abs(d);
                if (absDist < 0.5)
                {
                    // Inside the active sung focal window:
                    double activeFactor = Math.Clamp(1.0 - (absDist * 2.0), 0.0, 1.0);
                    byte alpha = (byte)(235 * activeFactor);

                    var activeCol = _activeWordBrush.Color;
                    var pillBrush = new SolidColorBrush(Color.FromArgb(alpha, activeCol.R, activeCol.G, activeCol.B));
                    pillBrush.Freeze();
                    node.PillBorder.Background = pillBrush;
                    node.PillBorder.BorderBrush = new SolidColorBrush(Color.FromArgb((byte)(160 * activeFactor), 255, 255, 255));
                    node.PillBorder.BorderThickness = new Thickness(1.5);
                    node.WordTextBlock.Foreground = System.Windows.Media.Brushes.White;
                    node.WordTextBlock.FontWeight = FontWeights.Bold;

                    if (node.PillBorder.Effect == null)
                    {
                        node.PillBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect
                        {
                            Color = activeCol,
                            BlurRadius = 26,
                            ShadowDepth = 3,
                            Opacity = 0.65
                        };
                    }
                    System.Windows.Controls.Panel.SetZIndex(node.Container, 100);
                }
                else
                {
                    // Receding/inactive words:
                    node.PillBorder.Background = System.Windows.Media.Brushes.Transparent;
                    node.PillBorder.BorderBrush = System.Windows.Media.Brushes.Transparent;
                    node.PillBorder.BorderThickness = new Thickness(0);
                    node.PillBorder.Effect = null;

                    // Words already sung get soft album tint, future words get crisp white
                    if (d < 0)
                    {
                        node.WordTextBlock.Foreground = _sungWordBrush;
                    }
                    else
                    {
                        node.WordTextBlock.Foreground = _inactiveLineBrush;
                    }
                    node.WordTextBlock.FontWeight = FontWeights.SemiBold;
                    System.Windows.Controls.Panel.SetZIndex(node.Container, 50 - (int)(absDist * 5));
                }

                // F. Center the container at (centerX, y)
                double w = node.Container.ActualWidth > 0 ? node.Container.ActualWidth : 220.0;
                double h = node.Container.ActualHeight > 0 ? node.Container.ActualHeight : 60.0;
                Canvas.SetLeft(node.Container, centerX - (w / 2.0));
                Canvas.SetTop(node.Container, y - (h / 2.0));
            }
        }

        private async Task LoadLyricsForCurrentSongAsync(MediaTrackInfo? track)
        {
            _lyricsCts?.Cancel();
            _lyricsCts = new System.Threading.CancellationTokenSource();
            var ct = _lyricsCts.Token;

            _sourceFetchCts?.Cancel();
            _currentProviderSourceId = null;
            ResetTransformState();

            _lyricItems.Clear();
            _allFisheyeWords.Clear();
            _smoothedFisheyeIndex = 0.0;
            LyricsLinesStack.Children.Clear();
            _activeLineIndex = -1;
            _currentScrollOffset = 0;
            _targetScrollOffset = 0;
            LyricsScrollViewer.ScrollToVerticalOffset(0);

            if (track == null || string.IsNullOrWhiteSpace(track.Title) || track.Title == "Ready to Play" || track.Title == "Unknown Track")
            {
                _currentLyrics = null;
                UpdateLyricsButtonAvailability(hasLyrics: false, isInstrumental: false);
                LyricsStatusOverlay.Visibility = Visibility.Visible;
                LyricsStatusIcon.Text = "🎵";
                LyricsStatusText.Text = "Play music to see timed lyrics";
                if (LyricsSongTitleText != null) LyricsSongTitleText.Text = "Ready to Play";
                if (LyricsArtistText != null) LyricsArtistText.Text = "Play music to begin";
                return;
            }

            if (LyricsSongTitleText != null) LyricsSongTitleText.Text = track.Title;
            if (LyricsArtistText != null) LyricsArtistText.Text = string.IsNullOrWhiteSpace(track.Artist) ? "Unknown Artist" : track.Artist;

            _lastTrackTitle = track.Title;
            _lastTrackArtist = string.IsNullOrWhiteSpace(track.Artist) ? "Unknown Artist" : track.Artist;
            _lastTrackAlbum = track.AlbumTitle ?? "";

            UpdateLyricsButtonAvailability(hasLyrics: false, isInstrumental: false, isChecking: true);
            LyricsStatusOverlay.Visibility = Visibility.Visible;
            LyricsStatusIcon.Text = "⏳";
            LyricsStatusText.Text = $"Finding lyrics for {track.Title}...";

            var lyrics = await LyricsService.Instance.GetLyricsAsync(track.Title, track.Artist, track.AlbumTitle ?? "", track.Duration, ct);

            if (ct.IsCancellationRequested) return;

            _currentLyrics = lyrics;
            _currentProviderSourceId = MapProviderToSourceId(lyrics?.Provider);

            bool hasLyrics = lyrics != null && lyrics.HasLyrics;
            bool isInstrumental = lyrics != null && lyrics.IsInstrumental;
            UpdateLyricsButtonAvailability(hasLyrics, isInstrumental);

            if (!hasLyrics || lyrics == null)
            {
                LyricsStatusOverlay.Visibility = Visibility.Visible;
                LyricsStatusIcon.Text = "📜";
                LyricsStatusText.Text = "Synced lyrics not found for this track";
                return;
            }

            if (lyrics.IsInstrumental)
            {
                LyricsStatusOverlay.Visibility = Visibility.Visible;
                LyricsStatusIcon.Text = "♪";
                LyricsStatusText.Text = "Instrumental ♪";
                return;
            }

            if (lyrics.IsSynced && lyrics.Lines.Count > 0)
            {
                LyricsStatusOverlay.Visibility = Visibility.Collapsed;
                if (!string.IsNullOrEmpty(lyrics.Provider) && LyricsArtistText != null)
                {
                    string art = string.IsNullOrWhiteSpace(track.Artist) ? "Unknown Artist" : track.Artist;
                    LyricsArtistText.Text = $"{art}  •  {lyrics.Provider}";
                }
                BuildLyricLineViews(lyrics.Lines);
                BuildFisheyeWords(lyrics.Lines);
            }
            else if (!string.IsNullOrWhiteSpace(lyrics.PlainLyrics))
            {
                // Fallback to plain lyrics display
                LyricsStatusOverlay.Visibility = Visibility.Collapsed;
                BuildPlainLyricsView(lyrics.PlainLyrics);
                BuildFisheyeWordsFromPlain(lyrics.PlainLyrics);
            }
        }

        // -------------------------------------------------------------
        // BitChord Lyrics Tools: Source Picker, Translate, Romanize, Offset
        // -------------------------------------------------------------

        private static string FormatOffset(int milliseconds)
        {
            if (milliseconds == 0) return "0.0s";
            double seconds = milliseconds / 1000.0;
            return (seconds > 0 ? "+" : "−") + Math.Abs(seconds).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s";
        }

        private static LyricSourceId? MapProviderToSourceId(string? provider)
        {
            switch (provider)
            {
                case "Apple Music (lrc.red)": return LyricSourceId.LrcRed;
                case "Apple Music (Bini)": return LyricSourceId.BiniLyrics;
                case "Apple Music (BetterLyrics)": return LyricSourceId.BetterLyrics;
                case "QQ Music (Portato)": return LyricSourceId.BetterLyricsPortato;
                case "Apple Music (PaxSenix)": return LyricSourceId.PaxSenix;
                case "Spotify (PaxSenix)": return LyricSourceId.PaxSenixSpotify;
                case "Musixmatch (PaxSenix)": return LyricSourceId.PaxSenixMusixmatch;
                case "LyricsPlus": return LyricSourceId.LyricsPlus;
                case "Unison": return LyricSourceId.Unison;
                case "KuGou": return LyricSourceId.KuGou;
                case "LRCLIB": return LyricSourceId.LrcLib;
                case "Musixmatch": return LyricSourceId.Musixmatch;
                case "Genius": return LyricSourceId.Genius;
                default: return null;
            }
        }

        private void SetLyricsStatus(string? text)
        {
            if (LyricsTranslationStatusText == null) return;
            if (string.IsNullOrEmpty(text))
            {
                LyricsTranslationStatusText.Text = "";
                LyricsTranslationStatusText.Visibility = Visibility.Collapsed;
            }
            else
            {
                LyricsTranslationStatusText.Text = text;
                LyricsTranslationStatusText.Visibility = Visibility.Visible;
            }
        }

        private void ResetTransformState()
        {
            _preTransformLyrics = null;
            _translationActive = false;
            _romanizationActive = false;
            if (LyricsTranslateText != null) LyricsTranslateText.Text = "Translate";
            if (LyricsRomanizeText != null) LyricsRomanizeText.Text = "Romanize";
            SetToolButtonActive(LyricsTranslateButton, false);
            SetToolButtonActive(LyricsRomanizeButton, false);
            SetLyricsStatus(null);
        }

        /// <summary>
        /// Tints a header tool button to indicate its transform is active;
        /// clearing the local values reverts it to the liquid-glass style defaults.
        /// </summary>
        private static void SetToolButtonActive(Button? button, bool active)
        {
            if (button == null) return;
            if (active)
            {
                button.Background = new SolidColorBrush(Color.FromArgb(0x46, 0xFF, 0x5C, 0x8F));
                button.BorderBrush = new SolidColorBrush(Color.FromArgb(0x9E, 0xFF, 0x8C, 0xB0));
                button.Foreground = System.Windows.Media.Brushes.White;
            }
            else
            {
                button.ClearValue(Button.BackgroundProperty);
                button.ClearValue(Button.BorderBrushProperty);
                button.ClearValue(Button.ForegroundProperty);
            }
        }

        /// <summary>
        /// ~540ms cross-fade replacing BitChord's particle transition: the stage
        /// dips out, the views are rebuilt, and it eases back in.
        /// </summary>
        private void FadeSwapLyrics(Action rebuild)
        {
            var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            fadeOut.Completed += (s, e) =>
            {
                rebuild();
                var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(360))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                };
                LyricsScrollViewer.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                FisheyeLyricsCanvas.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            };
            LyricsScrollViewer.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            FisheyeLyricsCanvas.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        private void ApplyLyricsToStage(TrackLyrics lyrics, string artist)
        {
            _currentLyrics = lyrics;
            UpdateLyricsButtonAvailability(lyrics.HasLyrics, lyrics.IsInstrumental);

            string displayArtist = string.IsNullOrWhiteSpace(artist) ? "Unknown Artist" : artist;

            if (!lyrics.HasLyrics)
            {
                LyricsStatusOverlay.Visibility = Visibility.Visible;
                LyricsStatusIcon.Text = "📜";
                LyricsStatusText.Text = "Synced lyrics not found for this track";
                return;
            }

            if (lyrics.IsInstrumental)
            {
                LyricsStatusOverlay.Visibility = Visibility.Visible;
                LyricsStatusIcon.Text = "♪";
                LyricsStatusText.Text = "Instrumental ♪";
                return;
            }

            string artistLine = string.IsNullOrEmpty(lyrics.Provider) ? displayArtist : $"{displayArtist}  •  {lyrics.Provider}";

            if (lyrics.IsSynced && lyrics.Lines.Count > 0)
            {
                LyricsStatusOverlay.Visibility = Visibility.Collapsed;
                if (LyricsArtistText != null) LyricsArtistText.Text = artistLine;
                BuildLyricLineViews(lyrics.Lines);
                BuildFisheyeWords(lyrics.Lines);
            }
            else if (!string.IsNullOrWhiteSpace(lyrics.PlainLyrics))
            {
                LyricsStatusOverlay.Visibility = Visibility.Collapsed;
                if (LyricsArtistText != null) LyricsArtistText.Text = artistLine;
                BuildPlainLyricsView(lyrics.PlainLyrics);
                BuildFisheyeWordsFromPlain(lyrics.PlainLyrics);
            }
        }

        private static List<LyricLine> GetTransformSourceLines(TrackLyrics lyrics)
        {
            if (lyrics.IsSynced && lyrics.Lines.Count > 0) return lyrics.Lines;

            var lines = new List<LyricLine>();
            if (!string.IsNullOrWhiteSpace(lyrics.PlainLyrics))
            {
                using var reader = new System.IO.StringReader(lyrics.PlainLyrics);
                string? line;
                int index = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0) continue;
                    lines.Add(new LyricLine { Index = index++, Text = trimmed });
                }
            }
            return lines;
        }

        private static TrackLyrics CloneWithLines(TrackLyrics source, List<LyricLine> lines, string? plainOverride)
        {
            return new TrackLyrics
            {
                SongKey = source.SongKey,
                TrackName = source.TrackName,
                ArtistName = source.ArtistName,
                Provider = source.Provider,
                Isrc = source.Isrc,
                IsSynced = source.IsSynced,
                HasWordSync = lines.Any(l => l.HasWordSync),
                IsInstrumental = false,
                Lines = lines,
                PlainLyrics = plainOverride ?? source.PlainLyrics
            };
        }

        private async void LyricsTranslate_Click(object sender, RoutedEventArgs e)
        {
            if (_translateBusy) return;
            if (_currentLyrics == null || !_currentLyrics.HasLyrics)
            {
                SetLyricsStatus("No lyrics to translate");
                return;
            }

            if (_translationActive)
            {
                var original = _preTransformLyrics;
                ResetTransformState();
                if (original != null) FadeSwapLyrics(() => ApplyLyricsToStage(original, _lastTrackArtist));
                return;
            }

            var snapshot = _currentLyrics;
            var sourceLines = GetTransformSourceLines(snapshot);
            if (sourceLines.Count == 0)
            {
                SetLyricsStatus("No lyric text to translate");
                return;
            }

            _translateBusy = true;
            if (LyricsTranslateText != null) LyricsTranslateText.Text = "Translating...";
            SetLyricsStatus("Translating lyrics...");

            var result = await LyricsTranslationService.TranslateAsync(snapshot.SongKey, sourceLines, SettingsService.Instance.LyricsTranslationLanguage);

            _translateBusy = false;
            if (LyricsTranslateText != null) LyricsTranslateText.Text = "Translate";
            if (!ReferenceEquals(_currentLyrics, snapshot)) return;

            switch (result.Status)
            {
                case LyricsTransformStatus.Translated:
                {
                    _preTransformLyrics = snapshot;
                    _translationActive = true;
                    _romanizationActive = false;
                    SetToolButtonActive(LyricsTranslateButton, true);
                    SetToolButtonActive(LyricsRomanizeButton, false);
                    if (LyricsTranslateText != null) LyricsTranslateText.Text = "Original";
                    var updated = snapshot.IsSynced
                        ? CloneWithLines(snapshot, result.Lines!, null)
                        : CloneWithLines(snapshot, result.Lines!, string.Join("\n", result.Lines!.Select(l => l.Text)));
                    FadeSwapLyrics(() => ApplyLyricsToStage(updated, _lastTrackArtist));
                    string suffix = result.FromCache ? " (cached)" : "";
                    SetLyricsStatus($"Translated · from {LyricsTranslationLanguages.DisplayName(result.SourceLanguage)}{suffix}");
                    break;
                }
                case LyricsTransformStatus.SameLanguage:
                    SetLyricsStatus($"Already in {LyricsTranslationLanguages.DisplayName(result.SourceLanguage)}");
                    break;
                default:
                    SetLyricsStatus("Translation unavailable right now");
                    break;
            }
        }

        private async void LyricsRomanize_Click(object sender, RoutedEventArgs e)
        {
            if (_translateBusy) return;
            if (_currentLyrics == null || !_currentLyrics.HasLyrics)
            {
                SetLyricsStatus("No lyrics to romanize");
                return;
            }

            if (_romanizationActive)
            {
                var original = _preTransformLyrics;
                ResetTransformState();
                if (original != null) FadeSwapLyrics(() => ApplyLyricsToStage(original, _lastTrackArtist));
                return;
            }

            var snapshot = _currentLyrics;
            var sourceLines = GetTransformSourceLines(snapshot);
            if (sourceLines.Count == 0)
            {
                SetLyricsStatus("No lyric text to romanize");
                return;
            }

            _translateBusy = true;
            if (LyricsRomanizeText != null) LyricsRomanizeText.Text = "Romanizing...";
            SetLyricsStatus("Romanizing lyrics...");

            var result = await LyricsTranslationService.RomanizeAsync(snapshot.SongKey, sourceLines, SettingsService.Instance.LyricsTranslationLanguage);

            _translateBusy = false;
            if (LyricsRomanizeText != null) LyricsRomanizeText.Text = "Romanize";
            if (!ReferenceEquals(_currentLyrics, snapshot)) return;

            switch (result.Status)
            {
                case LyricsTransformStatus.Translated:
                {
                    _preTransformLyrics = snapshot;
                    _romanizationActive = true;
                    _translationActive = false;
                    SetToolButtonActive(LyricsRomanizeButton, true);
                    SetToolButtonActive(LyricsTranslateButton, false);
                    if (LyricsRomanizeText != null) LyricsRomanizeText.Text = "Original";
                    var updated = snapshot.IsSynced
                        ? CloneWithLines(snapshot, result.Lines!, null)
                        : CloneWithLines(snapshot, result.Lines!, string.Join("\n", result.Lines!.Select(l => l.Text)));
                    FadeSwapLyrics(() => ApplyLyricsToStage(updated, _lastTrackArtist));
                    SetLyricsStatus($"Romanized · from {LyricsTranslationLanguages.DisplayName(result.SourceLanguage)}");
                    break;
                }
                case LyricsTransformStatus.AlreadyRomanized:
                    SetLyricsStatus("Already in the Latin alphabet");
                    break;
                default:
                    SetLyricsStatus("Romanization unavailable right now");
                    break;
            }
        }

        private void LyricsOffsetButton_Click(object sender, RoutedEventArgs e)
        {
            bool opening = LyricsOffsetOverlay.Visibility != Visibility.Visible;
            LyricsProviderOverlay.Visibility = Visibility.Collapsed;
            LyricsOffsetValueText.Text = FormatOffset(SettingsService.Instance.LyricsOffsetMs);
            LyricsOffsetOverlay.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        }

        private void LyricsOffsetOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            LyricsOffsetOverlay.Visibility = Visibility.Collapsed;
        }

        private void LyricsOverlayPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        private void LyricsOffsetStep_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string tag && int.TryParse(tag, out int delta))
            {
                SettingsService.Instance.LyricsOffsetMs += delta;
                LyricsOffsetValueText.Text = FormatOffset(SettingsService.Instance.LyricsOffsetMs);
            }
        }

        private void LyricsOffsetReset_Click(object sender, RoutedEventArgs e)
        {
            SettingsService.Instance.LyricsOffsetMs = 0;
            LyricsOffsetValueText.Text = FormatOffset(0);
        }

        private void LyricsProviderButton_Click(object sender, RoutedEventArgs e)
        {
            bool opening = LyricsProviderOverlay.Visibility != Visibility.Visible;
            LyricsOffsetOverlay.Visibility = Visibility.Collapsed;

            if (!opening)
            {
                LyricsProviderOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            PopulateProviderList();
            LyricsProviderStatusText.Text = _currentLyrics != null && !string.IsNullOrEmpty(_currentLyrics.Provider)
                ? $"Currently showing: {_currentLyrics.Provider}"
                : "No lyrics loaded yet";
            LyricsProviderStatusText.Visibility = Visibility.Visible;
            LyricsProviderOverlay.Visibility = Visibility.Visible;
        }

        private void LyricsProviderOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            LyricsProviderOverlay.Visibility = Visibility.Collapsed;
        }

        private void PopulateProviderList()
        {
            LyricsProviderListPanel.Children.Clear();
            foreach (var info in LyricSources.Offered)
            {
                var button = new Button
                {
                    Style = (Style)FindResource("FrostedPillButton"),
                    Padding = new Thickness(14, 9, 14, 9),
                    Margin = new Thickness(0, 0, 0, 6),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
                    Tag = info
                };

                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                left.Children.Add(new TextBlock
                {
                    Text = info.Label,
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF))
                });
                left.Children.Add(new TextBlock
                {
                    Text = info.Detail,
                    FontSize = 10.5,
                    Foreground = new SolidColorBrush(Color.FromArgb(0x78, 0xFF, 0xFF, 0xFF)),
                    TextWrapping = TextWrapping.Wrap
                });
                Grid.SetColumn(left, 0);
                row.Children.Add(left);

                var right = new StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                };
                if (info.WordSynced)
                {
                    right.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xE7, 0xC1, 0x7A)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(6, 2, 6, 2),
                        Margin = new Thickness(0, 0, 6, 0),
                        Child = new TextBlock
                        {
                            Text = "WORD",
                            FontSize = 8.5,
                            FontWeight = FontWeights.Bold,
                            Foreground = new SolidColorBrush(Color.FromArgb(0xD0, 0xE7, 0xC1, 0x7A))
                        }
                    });
                }
                if (_currentProviderSourceId == info.Id)
                {
                    right.Children.Add(new TextBlock
                    {
                        Text = "✓",
                        FontSize = 13,
                        FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(Color.FromArgb(0xE0, 0x7C, 0xFF, 0xB0))
                    });
                }
                Grid.SetColumn(right, 1);
                row.Children.Add(right);

                button.Content = row;
                button.Click += ProviderRow_Click;
                LyricsProviderListPanel.Children.Add(button);
            }
        }

        private void SetProviderListEnabled(bool enabled)
        {
            LyricsProviderListPanel.IsHitTestVisible = enabled;
            LyricsProviderListPanel.Opacity = enabled ? 1.0 : 0.55;
        }

        private async void ProviderRow_Click(object sender, RoutedEventArgs e)
        {
            if (_isFetchingFromSource) return;
            if (!(sender is Button button && button.Tag is LyricSourceInfo info)) return;
            if (string.IsNullOrWhiteSpace(_lastTrackTitle)) return;

            _isFetchingFromSource = true;
            SetProviderListEnabled(false);
            LyricsProviderStatusText.Text = $"Fetching from {info.Label}...";
            LyricsProviderStatusText.Visibility = Visibility.Visible;

            _sourceFetchCts?.Cancel();
            _sourceFetchCts = new System.Threading.CancellationTokenSource();
            var ct = _sourceFetchCts.Token;

            TrackLyrics? result = null;
            try
            {
                result = await LyricsService.Instance.GetLyricsFromSourceAsync(_lastTrackTitle, _lastTrackArtist, _lastTrackAlbum, _currentTrackDuration, info.Id, ct);
            }
            catch { }

            _isFetchingFromSource = false;
            SetProviderListEnabled(true);

            if (ct.IsCancellationRequested) return;

            if (result == null || !result.HasLyrics)
            {
                LyricsProviderStatusText.Text = $"Nothing found on {info.Label}";
                return;
            }

            _currentProviderSourceId = info.Id;
            ResetTransformState();
            SetLyricsStatus($"Source · {info.Label}");
            FadeSwapLyrics(() => ApplyLyricsToStage(result, _lastTrackArtist));
            LyricsProviderOverlay.Visibility = Visibility.Collapsed;
        }

        private static double ComputeScrollLead(List<LyricLineItem> items, TimeSpan now)
        {
            const double leadMinMs = 350.0;
            const double leadMaxMs = 500.0;
            if (items == null || items.Count == 0) return leadMinMs;

            int current = -1;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Line.StartTime <= now) current = i;
                else break;
            }

            if (current < 0) return leadMinMs;
            if (current + 1 >= items.Count) return leadMinMs;

            double gapMs = (items[current + 1].Line.StartTime - items[current].Line.EndTime).TotalMilliseconds;
            return Math.Clamp(gapMs, leadMinMs, leadMaxMs);
        }

        private static LinearGradientBrush CreateSweepMask(double progress)
        {
            // BitChord WIPE_FEATHER = 30.dp (~0.22 width feather)
            double feather = 0.22;
            double start = Math.Clamp(progress - feather * 0.5, 0.0, 1.0);
            double end = Math.Clamp(progress + feather * 0.5, 0.0, 1.0);

            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0)
            };

            if (start > 0.0)
            {
                brush.GradientStops.Add(new GradientStop(Color.FromArgb(255, 255, 255, 255), 0.0));
            }
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(255, 255, 255, 255), start));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), end));
            if (end < 1.0)
            {
                brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1.0));
            }

            brush.Freeze();
            return brush;
        }

        private void BuildLyricLineViews(List<LyricLine> lines)
        {
            LyricsLinesStack.Children.Clear();
            _lyricItems.Clear();

            var lineBtnStyle = (Style)FindResource("LyricLineButtonStyle");
            // BitChord Typography: fontSize = 34sp, lineHeight = 41sp, fontWeight = ExtraBold
            double initialFontSize = _isRestoredMode ? 24 : 34;
            double initialLineHeight = _isRestoredMode ? 30 : 41;

            // BitChord Duet Lane Detection: songs with duet lines keep 44px lane clear on opposing side
            bool hasDuet = false;
            for (int d = 0; d < lines.Count; d++)
            {
                if (lines[d].Alignment == LyricAlignment.End)
                {
                    hasDuet = true;
                    break;
                }
            }

            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];

                bool isAlignEnd = hasDuet && line.Alignment == LyricAlignment.End;
                // BitChord aligns to Start (Left) by default, and End (Right) only for duet answering vocal
                var horizAlign = isAlignEnd
                    ? System.Windows.HorizontalAlignment.Right
                    : System.Windows.HorizontalAlignment.Left;
                var textAlign = isAlignEnd
                    ? TextAlignment.Right
                    : TextAlignment.Left;

                // DUET_LANE = 44px
                var lineMargin = hasDuet
                    ? (isAlignEnd ? new Thickness(44, 4, 0, 4) : new Thickness(0, 4, 44, 4))
                    : new Thickness(0, 4, 0, 4);

                var staggerTransform = new TranslateTransform(0, 0);
                var scaleTransform = new ScaleTransform(0.98, 0.98); // INACTIVE_SCALE = 0.98f
                var transformGroup = new TransformGroup();
                transformGroup.Children.Add(scaleTransform);
                transformGroup.Children.Add(staggerTransform);

                var btn = new Button
                {
                    Style = lineBtnStyle,
                    Tag = line,
                    HorizontalAlignment = horizAlign,
                    Margin = lineMargin,
                    RenderTransformOrigin = new Point(isAlignEnd ? 1.0 : 0.0, 0.5),
                    RenderTransform = transformGroup
                };
                btn.Click += (s, e) =>
                {
                    if (s is Button b && b.Tag is LyricLine l)
                    {
                        _lyricClock.Reset(ToTrackPosition(l.StartTime), _isPlaying);
                        _ = _mediaService.ChangePlaybackPositionAsync(ToTrackPosition(l.StartTime));
                        _isUserScrolledAway = false;
                        if (SyncLyricsPillButton != null) SyncLyricsPillButton.Visibility = Visibility.Collapsed;
                    }
                };

                var txt = new TextBlock
                {
                    FontSize = initialFontSize,
                    FontWeight = FontWeights.SemiBold,
                    LineHeight = initialLineHeight,
                    TextAlignment = textAlign,
                    TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = horizAlign
                };

                var wordViews = new List<LyricWordView>();
                Border? gapBorder = null;
                List<System.Windows.Shapes.Ellipse>? gapDotsList = null;

                if (line.IsGap)
                {
                    // BitChord 3-Dot Instrumental Interlude:
                    // Height 40px, GAP_DOT_SIZE = 13px, GAP_DOT_GAP = 5px, progressive left-to-right filling dots
                    gapDotsList = new List<System.Windows.Shapes.Ellipse>();
                    var dotsPanel = new StackPanel
                    {
                        Orientation = System.Windows.Controls.Orientation.Horizontal,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    for (int dotIdx = 0; dotIdx < 3; dotIdx++)
                    {
                        var ellipse = new System.Windows.Shapes.Ellipse
                        {
                            Width = 13,
                            Height = 13,
                            Fill = System.Windows.Media.Brushes.White,
                            Opacity = 0.25, // GAP_DOT_REST = 0.25f
                            Margin = new Thickness(dotIdx > 0 ? 5 : 0, 0, 0, 0)
                        };
                        gapDotsList.Add(ellipse);
                        dotsPanel.Children.Add(ellipse);
                    }

                    gapBorder = new Border
                    {
                        Height = 0, // Swells open to 40px when active
                        Opacity = 0.0,
                        ClipToBounds = true,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = dotsPanel
                    };

                    btn.Content = gapBorder;
                }
                else if (line.Words != null && line.Words.Count > 0)
                {
                    for (int w = 0; w < line.Words.Count; w++)
                    {
                        var word = line.Words[w];
                        string wordText = word.Text;

                        var wordGrid = new Grid { IsHitTestVisible = false };
                        var dimBlock = new TextBlock
                        {
                            Text = wordText,
                            FontSize = initialFontSize,
                            FontWeight = FontWeights.SemiBold,
                            LineHeight = initialLineHeight,
                            Foreground = _unsungWordBrush,
                            IsHitTestVisible = false
                        };
                        var liftTransform = new TranslateTransform(0, 0);
                        var litBlock = new TextBlock
                        {
                            Text = wordText,
                            FontSize = initialFontSize,
                            FontWeight = FontWeights.SemiBold,
                            LineHeight = initialLineHeight,
                            Foreground = _sungWordBrush,
                            Visibility = Visibility.Collapsed,
                            RenderTransform = liftTransform,
                            IsHitTestVisible = false
                        };
                        wordGrid.Children.Add(dimBlock);
                        wordGrid.Children.Add(litBlock);

                        var container = new InlineUIContainer(wordGrid)
                        {
                            BaselineAlignment = BaselineAlignment.Baseline
                        };
                        txt.Inlines.Add(container);

                        if (w < line.Words.Count - 1)
                        {
                            txt.Inlines.Add(new Run(" ") { Foreground = System.Windows.Media.Brushes.Transparent });
                        }

                        wordViews.Add(new LyricWordView
                        {
                            Word = word,
                            ContainerGrid = wordGrid,
                            DimTextBlock = dimBlock,
                            LitTextBlock = litBlock,
                            LiftTransform = liftTransform
                        });
                    }
                }
                else
                {
                    // Line-synced lyric fallback
                    string lineText = line.Text;
                    var wordGrid = new Grid { IsHitTestVisible = false };
                    var dimBlock = new TextBlock
                    {
                        Text = lineText,
                        FontSize = initialFontSize,
                        FontWeight = FontWeights.SemiBold,
                        LineHeight = initialLineHeight,
                        Foreground = _unsungWordBrush,
                        IsHitTestVisible = false
                    };
                    var liftTransform = new TranslateTransform(0, 0);
                    var litBlock = new TextBlock
                    {
                        Text = lineText,
                        FontSize = initialFontSize,
                        FontWeight = FontWeights.SemiBold,
                        LineHeight = initialLineHeight,
                        Foreground = _sungWordBrush,
                        Visibility = Visibility.Collapsed,
                        RenderTransform = liftTransform,
                        IsHitTestVisible = false
                    };
                    wordGrid.Children.Add(dimBlock);
                    wordGrid.Children.Add(litBlock);

                    var container = new InlineUIContainer(wordGrid)
                    {
                        BaselineAlignment = BaselineAlignment.Baseline
                    };
                    txt.Inlines.Add(container);

                    var syntheticWord = new LyricWord
                    {
                        Text = lineText,
                        StartTime = line.StartTime,
                        EndTime = line.EndTime
                    };
                    wordViews.Add(new LyricWordView
                    {
                        Word = syntheticWord,
                        ContainerGrid = wordGrid,
                        DimTextBlock = dimBlock,
                        LitTextBlock = litBlock,
                        LiftTransform = liftTransform
                    });
                }

                if (!line.IsGap)
                {
                    UIElement buttonContent = txt;

                    if (line.BackgroundLine != null && !string.IsNullOrWhiteSpace(line.BackgroundLine.Text))
                    {
                        var bgLine = line.BackgroundLine;
                        var txtBg = new TextBlock
                        {
                            FontSize = initialFontSize * 0.82,
                            FontWeight = FontWeights.Medium,
                            FontStyle = FontStyles.Italic,
                            LineHeight = initialLineHeight * 0.82,
                            TextAlignment = textAlign,
                            TextWrapping = TextWrapping.Wrap,
                            HorizontalAlignment = horizAlign,
                            Margin = new Thickness(0, 4, 0, 0),
                            Foreground = _pastLineBrush
                        };

                        if (bgLine.Words != null && bgLine.Words.Count > 0)
                        {
                            for (int bw = 0; bw < bgLine.Words.Count; bw++)
                            {
                                var bgWord = bgLine.Words[bw];
                                var bgWordGrid = new Grid { IsHitTestVisible = false };
                                var bgDimBlock = new TextBlock
                                {
                                    Text = bgWord.Text,
                                    FontSize = initialFontSize * 0.82,
                                    FontWeight = FontWeights.Medium,
                                    FontStyle = FontStyles.Italic,
                                    Foreground = _pastLineBrush,
                                    IsHitTestVisible = false
                                };
                                var bgLift = new TranslateTransform(0, 0);
                                var bgLitBlock = new TextBlock
                                {
                                    Text = bgWord.Text,
                                    FontSize = initialFontSize * 0.82,
                                    FontWeight = FontWeights.Medium,
                                    FontStyle = FontStyles.Italic,
                                    Foreground = _sungWordBrush,
                                    Visibility = Visibility.Collapsed,
                                    RenderTransform = bgLift,
                                    IsHitTestVisible = false
                                };
                                bgWordGrid.Children.Add(bgDimBlock);
                                bgWordGrid.Children.Add(bgLitBlock);
                                txtBg.Inlines.Add(new InlineUIContainer(bgWordGrid) { BaselineAlignment = BaselineAlignment.Baseline });
                                if (bw < bgLine.Words.Count - 1)
                                {
                                    txtBg.Inlines.Add(new Run(" ") { Foreground = System.Windows.Media.Brushes.Transparent });
                                }
                                wordViews.Add(new LyricWordView
                                {
                                    Word = bgWord,
                                    ContainerGrid = bgWordGrid,
                                    DimTextBlock = bgDimBlock,
                                    LitTextBlock = bgLitBlock,
                                    LiftTransform = bgLift
                                });
                            }
                        }
                        else
                        {
                            txtBg.Text = bgLine.Text;
                        }

                        var stack = new StackPanel
                        {
                            Orientation = System.Windows.Controls.Orientation.Vertical,
                            HorizontalAlignment = horizAlign
                        };
                        stack.Children.Add(txt);
                        stack.Children.Add(txtBg);
                        buttonContent = stack;
                    }

                    btn.Content = buttonContent;
                }

                LyricsLinesStack.Children.Add(btn);

                _lyricItems.Add(new LyricLineItem
                {
                    Line = line,
                    Button = btn,
                    TextBlock = txt,
                    StaggerTransform = staggerTransform,
                    ScaleTransform = scaleTransform,
                    WordViews = wordViews,
                    GapContainer = gapBorder,
                    GapDots = gapDotsList
                });
            }
        }

        private void BuildPlainLyricsView(string plainLyrics)
        {
            LyricsLinesStack.Children.Clear();
            _lyricItems.Clear();

            double fontSize = _isRestoredMode ? 20 : 26;
            double lineHeight = _isRestoredMode ? 32 : 40;

            using var reader = new System.IO.StringReader(plainLyrics);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var txt = new TextBlock
                {
                    Text = line,
                    FontSize = fontSize,
                    FontWeight = FontWeights.Medium,
                    Foreground = _inactiveLineBrush,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = lineHeight,
                    Margin = new Thickness(14, 6, 14, 6),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center
                };
                LyricsLinesStack.Children.Add(txt);
            }
        }

        private void UpdateLyricsFrame(double dt)
        {
            if (_lyricItems.Count == 0) return;

            var position = LyricClockPosition;

            // 1. BitChord Dynamic scrollLead Calculation:
            // Measured run-up bounded between 350ms and 500ms
            double dynamicLeadMs = ComputeScrollLead(_lyricItems, position);
            TimeSpan leadTime = TimeSpan.FromMilliseconds(dynamicLeadMs);
            int newActiveIndex = -1;

            for (int i = 0; i < _lyricItems.Count; i++)
            {
                var item = _lyricItems[i];
                if (position >= item.Line.StartTime && position < item.Line.EndTime)
                {
                    newActiveIndex = i;
                    break;
                }
            }

            if (newActiveIndex == -1)
            {
                for (int i = 0; i < _lyricItems.Count; i++)
                {
                    var item = _lyricItems[i];
                    if (position >= item.Line.StartTime - leadTime && position < item.Line.StartTime)
                    {
                        newActiveIndex = i;
                        break;
                    }
                    else if (position < item.Line.StartTime)
                    {
                        newActiveIndex = Math.Max(0, i - 1);
                        break;
                    }
                }
            }

            if (newActiveIndex == -1 && _lyricItems.Count > 0 && position >= _lyricItems[_lyricItems.Count - 1].Line.StartTime)
            {
                newActiveIndex = _lyricItems.Count - 1;
            }

            // BitChord Typography Metrics:
            // Synced: fontSize = 34sp (active 34, inactive 34 with 0.98 scale), lineHeight = 41sp, fontWeight = ExtraBold
            // BitChord LINE_FALLOFF_ALPHA = [1.0, 0.80, 0.70, 0.58, 0.46]
            // BitChord LINE_FALLOFF_BLUR = [0.0, 1.0, 1.0, 1.7, 2.4]
            // Inactive scale = 0.98, Active scale = 1.0
            double activeFontSize = _isRestoredMode ? 26 : 34;
            double activeLineHeight = _isRestoredMode ? 32 : 41;
            double inactiveFontSize = _isRestoredMode ? 26 : 34;
            double inactiveLineHeight = _isRestoredMode ? 32 : 41;

            // 2. Focus Transition, Depth-of-Field Styling, and Scroll Trigger
            if (newActiveIndex != _activeLineIndex)
            {
                for (int i = 0; i < _lyricItems.Count; i++)
                {
                    var item = _lyricItems[i];
                    int dist = Math.Abs(i - newActiveIndex);

                    if (item.Line.IsGap && item.GapContainer != null)
                    {
                        // Instrumental gap row swell / collapse (BitChord: 40px height + 16px spacing)
                        bool isGapActive = (i == newActiveIndex);
                        item.GapContainer.Height = isGapActive ? 40.0 : 0.0;
                        item.GapContainer.Opacity = isGapActive ? 1.0 : 0.0;
                    }
                    else if (dist == 0)
                    {
                        // Playing Line: BitChord scale 1.0, alpha 1.0, 0 blur, ExtraBold
                        item.SetLineStyle(activeFontSize, FontWeights.ExtraBold, activeLineHeight, null, 1.0, scale: 1.0);
                    }
                    else if (dist == 1)
                    {
                        // Step 1: BitChord LINE_FALLOFF_ALPHA[1] = 0.80, LINE_FALLOFF_BLUR[1] = 1.0px, scale 0.98
                        double op = 0.80;
                        item.SetLineStyle(inactiveFontSize, FontWeights.ExtraBold, inactiveLineHeight, _blurDist1, op, scale: 0.98);
                    }
                    else if (dist == 2)
                    {
                        // Step 2: BitChord LINE_FALLOFF_ALPHA[2] = 0.70, LINE_FALLOFF_BLUR[2] = 1.0px, scale 0.98
                        double op = 0.70;
                        item.SetLineStyle(inactiveFontSize, FontWeights.ExtraBold, inactiveLineHeight, _blurDist1, op, scale: 0.98);
                    }
                    else if (dist == 3)
                    {
                        // Step 3: BitChord LINE_FALLOFF_ALPHA[3] = 0.58, LINE_FALLOFF_BLUR[3] = 1.7px, scale 0.98
                        double op = 0.58;
                        item.SetLineStyle(inactiveFontSize, FontWeights.ExtraBold, inactiveLineHeight, _blurDist2, op, scale: 0.98);
                    }
                    else
                    {
                        // Step 4+: BitChord LINE_FALLOFF_ALPHA[4] = 0.46, LINE_FALLOFF_BLUR[4] = 2.4px, scale 0.98
                        double op = 0.46;
                        item.SetLineStyle(inactiveFontSize, FontWeights.ExtraBold, inactiveLineHeight, _blurDistFar, op, scale: 0.98);
                    }

                    // Reset past and upcoming word highlight states
                    if (i < newActiveIndex)
                    {
                        for (int w = 0; w < item.WordViews.Count; w++)
                        {
                            var wv = item.WordViews[w];
                            wv.LitTextBlock.Visibility = Visibility.Visible;
                            wv.LitTextBlock.OpacityMask = null;
                            wv.LitTextBlock.Foreground = _sungWordBrush;
                            wv.LitTextBlock.Effect = null;
                            wv.LiftTransform.Y = 0;
                        }
                    }
                    else if (i > newActiveIndex)
                    {
                        for (int w = 0; w < item.WordViews.Count; w++)
                        {
                            var wv = item.WordViews[w];
                            wv.LitTextBlock.Visibility = Visibility.Collapsed;
                            wv.LitTextBlock.OpacityMask = null;
                            wv.LitTextBlock.Effect = null;
                            wv.LiftTransform.Y = 0;
                        }
                    }
                }

                // Smooth scroll target: center active line at 40% of viewport
                if (newActiveIndex >= 0 && newActiveIndex < _lyricItems.Count)
                {
                    try
                    {
                        var activeBtn = _lyricItems[newActiveIndex].Button;
                        var pt = activeBtn.TranslatePoint(new Point(0, 0), LyricsLinesStack);
                        double viewportHeight = LyricsScrollViewer.ActualHeight > 0 ? LyricsScrollViewer.ActualHeight : 700.0;
                        double newTarget = Math.Max(0.0, pt.Y - (viewportHeight * 0.40));

                        // Initiate BitChord ScrollRun Handover animation
                        _scrollRunId++;
                        _scrollRunStartOffset = _currentScrollOffset;
                        _scrollRunTargetOffset = newTarget;
                        _scrollRunDurationMs = dynamicLeadMs; // dynamic span between 350ms and 500ms
                        _scrollRunElapsedMs = 0.0;
                        _isScrollRunActive = true;
                        _scrollRunFocusIndex = newActiveIndex;
                        _targetScrollOffset = newTarget;
                    }
                    catch { }
                }

                _activeLineIndex = newActiveIndex;
            }

            // 3. Instrumental 3-Dot Fill Animation & Progressive Left-to-Right Lighting
            if (_activeLineIndex >= 0 && _activeLineIndex < _lyricItems.Count)
            {
                var activeItem = _lyricItems[_activeLineIndex];
                if (activeItem.Line.IsGap && activeItem.GapDots != null)
                {
                    double gapSpan = Math.Max(0.1, (activeItem.Line.EndTime - activeItem.Line.StartTime).TotalSeconds);
                    double through = Math.Clamp((position - activeItem.Line.StartTime).TotalSeconds / gapSpan, 0.0, 1.0);

                    for (int d = 0; d < activeItem.GapDots.Count; d++)
                    {
                        // BitChord progressive dot fill: lit = (through * 3 - dot).coerceIn(0f, 1f)
                        double lit = Math.Clamp((through * 3.0) - d, 0.0, 1.0);
                        activeItem.GapDots[d].Opacity = 0.25 + (0.75 * lit);
                    }
                }
            }

            // 4. Continuous Sub-Pixel Wavefront Sweeping, Headroom Buoyancy & Held-Note Bloom
            if (_activeLineIndex >= 0 && _activeLineIndex < _lyricItems.Count)
            {
                var activeItem = _lyricItems[_activeLineIndex];
                if (!activeItem.Line.IsGap)
                {
                    for (int w = 0; w < activeItem.WordViews.Count; w++)
                    {
                        var wv = activeItem.WordViews[w];
                        var word = wv.Word;

                        if (position >= word.EndTime)
                        {
                            // Fully sung: settle to baseline with solid sung color
                            if (wv.LitTextBlock.Visibility != Visibility.Visible || wv.LitTextBlock.OpacityMask != null)
                            {
                                wv.LitTextBlock.Visibility = Visibility.Visible;
                                wv.LitTextBlock.OpacityMask = null;
                                wv.LitTextBlock.Foreground = _sungWordBrush;
                                wv.LitTextBlock.Effect = null;
                                wv.LiftTransform.Y = 0;
                            }
                        }
                        else if (position >= word.StartTime)
                        {
                            // Actively being sung right now!
                            double wordDuration = (word.EndTime - word.StartTime).TotalSeconds;
                            double progress = wordDuration > 0.001
                                ? Math.Clamp((position - word.StartTime).TotalSeconds / wordDuration, 0.0, 1.0)
                                : 1.0;

                            if (word.Syllables != null && word.Syllables.Count > 0 && word.Text.Length > 0)
                            {
                                progress = Math.Clamp(word.CharsSungAt(position) / word.Text.Length, 0.0, 1.0);
                            }

                            wv.LitTextBlock.Visibility = Visibility.Visible;
                            wv.LitTextBlock.Foreground = _activeWordBrush;
                            wv.LitTextBlock.OpacityMask = CreateSweepMask(progress);

                            // Kinetic word rise: smooth sine buoyant lift (2.4px)
                            wv.LiftTransform.Y = -Math.Sin(Math.PI * progress) * 2.4;

                            // BitChord Held-note letter bloom for carried notes (>= 750ms)
                            if (wordDuration >= 0.75 && progress >= 0.15 && progress <= 0.95)
                            {
                                wv.LitTextBlock.Effect = _heldWordBloomEffect;
                            }
                            else
                            {
                                wv.LitTextBlock.Effect = null;
                            }
                        }
                        else
                        {
                            // Unsung word ahead
                            if (wv.LitTextBlock.Visibility != Visibility.Collapsed)
                            {
                                wv.LitTextBlock.Visibility = Visibility.Collapsed;
                                wv.LitTextBlock.OpacityMask = null;
                                wv.LitTextBlock.Effect = null;
                                wv.LiftTransform.Y = 0;
                            }
                        }
                    }
                }
            }

            // 5. BitChord Cubic Bezier Eased Handover & Staggered Trailing Row Fan-out
            if (!_isUserScrolledAway)
            {
                if (_isScrollRunActive)
                {
                    _scrollRunElapsedMs += dt * 1000.0;
                    double t = Math.Clamp(_scrollRunElapsedMs / _scrollRunDurationMs, 0.0, 1.0);
                    double easedT = EvaluateLyricBezier(t);

                    _currentScrollOffset = _scrollRunStartOffset + (_scrollRunTargetOffset - _scrollRunStartOffset) * easedT;
                    LyricsScrollViewer.ScrollToVerticalOffset(_currentScrollOffset);

                    // Trailing rows fan-out calculation:
                    // Each row after focus line sets off slightly later (STAGGER_STEPS = 3, STAGGER_FRACTION = 0.06)
                    double delta = _scrollRunTargetOffset - _scrollRunStartOffset;
                    for (int r = 0; r < _lyricItems.Count; r++)
                    {
                        var rowItem = _lyricItems[r];
                        int behind = r - _scrollRunFocusIndex;
                        if (behind > 0 && behind <= StaggerSteps && Math.Abs(delta) > 1.0)
                        {
                            double staggerDelayMs = behind * StaggerFraction * _scrollRunDurationMs;
                            double delayedT = Math.Clamp((_scrollRunElapsedMs - staggerDelayMs) / _scrollRunDurationMs, 0.0, 1.0);
                            double delayedEased = EvaluateLyricBezier(delayedT);
                            rowItem.StaggerTransform.Y = delta * (easedT - delayedEased);
                        }
                        else
                        {
                            rowItem.StaggerTransform.Y = 0.0;
                        }
                    }

                    if (t >= 1.0)
                    {
                        _isScrollRunActive = false;
                        for (int r = 0; r < _lyricItems.Count; r++)
                        {
                            _lyricItems[r].StaggerTransform.Y = 0.0;
                        }
                    }
                }
                else if (Math.Abs(_currentScrollOffset - _targetScrollOffset) > 0.4)
                {
                    double scrollDt = Math.Min(dt, 0.05);
                    _currentScrollOffset += (_targetScrollOffset - _currentScrollOffset) * Math.Min(1.0, scrollDt * 8.5);
                    LyricsScrollViewer.ScrollToVerticalOffset(_currentScrollOffset);
                }
            }
        }

        private static Color EnsureVibrantLegibility(Color c)
        {
            float r = c.R / 255f;
            float g = c.G / 255f;
            float b = c.B / 255f;

            float max = Math.Max(r, Math.Max(g, b));
            float min = Math.Min(r, Math.Min(g, b));
            float delta = max - min;

            float h = 0f;
            if (delta > 0.0001f)
            {
                if (Math.Abs(max - r) < 0.0001f) h = ((g - b) / delta) % 6f;
                else if (Math.Abs(max - g) < 0.0001f) h = ((b - r) / delta) + 2f;
                else h = ((r - g) / delta) + 4f;
                h *= 60f;
                if (h < 0) h += 360f;
            }

            float s = max > 0.0001f ? (delta / max) : 0f;
            float v = max;

            // Very dark / black covers fallback to vibrant rose
            if (v < 0.18f && s < 0.18f)
            {
                return Color.FromRgb(255, 64, 129);
            }

            // Ensure rich, saturated, luminous color against dark glass
            s = Math.Clamp(Math.Max(s, 0.45f), 0f, 1f);
            v = Math.Clamp(Math.Max(v, 0.88f), 0f, 1f);

            float cVal = v * s;
            float x = cVal * (1 - Math.Abs(((h / 60f) % 2) - 1));
            float m = v - cVal;

            float r1 = 0, g1 = 0, b1 = 0;
            if (h >= 0 && h < 60) { r1 = cVal; g1 = x; b1 = 0; }
            else if (h >= 60 && h < 120) { r1 = x; g1 = cVal; b1 = 0; }
            else if (h >= 120 && h < 180) { r1 = 0; g1 = cVal; b1 = x; }
            else if (h >= 180 && h < 240) { r1 = 0; g1 = x; b1 = cVal; }
            else if (h >= 240 && h < 300) { r1 = x; g1 = 0; b1 = cVal; }
            else { r1 = cVal; g1 = 0; b1 = x; }

            byte finalR = (byte)Math.Clamp((r1 + m) * 255f, 0, 255);
            byte finalG = (byte)Math.Clamp((g1 + m) * 255f, 0, 255);
            byte finalB = (byte)Math.Clamp((b1 + m) * 255f, 0, 255);

            return Color.FromRgb(finalR, finalG, finalB);
        }

        private void UpdateLyricBrushes(Color baseColor)
        {
            Color vibrant = EnsureVibrantLegibility(baseColor);

            // Sung words: rich vibrant album color
            _sungWordBrush = new SolidColorBrush(vibrant);
            _sungWordBrush.Freeze();

            // Word currently being sung: luminous bright highlight (50% blend with white)
            byte activeR = (byte)((vibrant.R + 255) / 2);
            byte activeG = (byte)((vibrant.G + 255) / 2);
            byte activeB = (byte)((vibrant.B + 255) / 2);
            _activeWordBrush = new SolidColorBrush(Color.FromRgb(activeR, activeG, activeB));
            _activeWordBrush.Freeze();

            // BitChord Held-note bloom effect for sustained words:
            // GLOW_RADIUS = 6.dp, GLOW_ALPHA = 0.62f
            _heldWordBloomEffect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = vibrant,
                BlurRadius = 6,
                ShadowDepth = 0,
                Opacity = 0.62
            };
            _heldWordBloomEffect.Freeze();

            // Words not yet reached: clean semi-translucent white
            _unsungWordBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
            _unsungWordBrush.Freeze();

            // Upcoming lines: soft white
            _inactiveLineBrush = new SolidColorBrush(Color.FromArgb(95, 255, 255, 255));
            _inactiveLineBrush.Freeze();

            // Past lines: subtle faded white
            _pastLineBrush = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255));
            _pastLineBrush.Freeze();

            // Invalidate active line index to immediately refresh colors on next frame
            if (_isLyricsMode && _lyricItems.Count > 0)
            {
                _activeLineIndex = -1;
            }
        }

        private void LyricsScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            _isUserScrolledAway = true;
            if (SyncLyricsPillButton != null)
            {
                SyncLyricsPillButton.Visibility = Visibility.Visible;
            }

            if (_userScrollResumeTimer == null)
            {
                _userScrollResumeTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(3.5)
                };
                _userScrollResumeTimer.Tick += (s, ev) =>
                {
                    _userScrollResumeTimer.Stop();
                    _isUserScrolledAway = false;
                    if (SyncLyricsPillButton != null)
                    {
                        SyncLyricsPillButton.Visibility = Visibility.Collapsed;
                    }
                };
            }
            _userScrollResumeTimer.Stop();
            _userScrollResumeTimer.Start();
        }

        private void LyricsScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_isUserScrolledAway)
            {
                _currentScrollOffset = e.VerticalOffset;
            }
        }

        private void SyncLyricsPillButton_Click(object sender, RoutedEventArgs e)
        {
            _userScrollResumeTimer?.Stop();
            _isUserScrolledAway = false;
            if (SyncLyricsPillButton != null)
            {
                SyncLyricsPillButton.Visibility = Visibility.Collapsed;
            }
        }

        // =========================================================
        // MOTION ARTWORK ENGINE (Animated Album Covers & Fullscreen Canvas)
        // =========================================================

        private async Task LoadMotionArtworkForCurrentSongAsync(string title, string artist, string? albumTitle)
        {
            ResetMotionArtwork();

            if (string.IsNullOrWhiteSpace(title) ||
                title.Equals("Ready to Play", StringComparison.OrdinalIgnoreCase) ||
                title.Equals("Unknown Track", StringComparison.OrdinalIgnoreCase) ||
                title.Equals("No Track Playing", StringComparison.OrdinalIgnoreCase))
                return;

            _motionCts = new System.Threading.CancellationTokenSource();
            var ct = _motionCts.Token;

            try
            {
                App.Log($"[MotionArtwork] Searching motion canvas for: '{title}' - '{artist}'");
                var motion = await MotionArtworkService.Instance.GetMotionArtworkAsync(title, artist, albumTitle ?? "", ct);
                if (ct.IsCancellationRequested || motion == null || string.IsNullOrWhiteSpace(motion.Url))
                {
                    App.Log($"[MotionArtwork] No motion video found for: {title} - {artist}");
                    return;
                }

                _currentMotionResult = motion;
                _hasAttemptedFallback = false;

                // Prefer local cached file on disk for instant, zero-latency looping
                string targetUri = !string.IsNullOrEmpty(motion.LocalFilePath) && System.IO.File.Exists(motion.LocalFilePath)
                    ? motion.LocalFilePath
                    : motion.Url;

                var uri = targetUri.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? new Uri(targetUri, UriKind.Absolute)
                    : new Uri(System.IO.Path.GetFullPath(targetUri), UriKind.Absolute);

                await Dispatcher.InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested) return;

                    App.Log($"[MotionArtwork] Mounting motion video for '{title}': {targetUri} (Source: {motion.Source})");

                    if (_isFullscreenMotionActive)
                    {
                        if (FullscreenMotionVideo != null)
                        {
                            FullscreenMotionVideo.Source = uri;
                            FullscreenMotionVideo.Stop();
                            if (_isPlaying) FullscreenMotionVideo.Play();
                        }
                        if (MotionVideoBackdrop != null)
                        {
                            MotionVideoBackdrop.Source = uri;
                            MotionVideoBackdrop.Stop();
                            if (_isPlaying) MotionVideoBackdrop.Play();
                        }
                    }
                    else
                    {
                        if (AlbumCoverVideo != null)
                        {
                            AlbumCoverVideo.Source = uri;
                            AlbumCoverVideo.Stop();
                            if (_isPlaying) AlbumCoverVideo.Play();
                        }
                        if (TvMotionVideoBackdrop != null)
                        {
                            TvMotionVideoBackdrop.Source = uri;
                            TvMotionVideoBackdrop.Stop();
                            if (_isPlaying) TvMotionVideoBackdrop.Play();
                        }
                    }

                    UpdateMotionButtonUI();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.Log($"[MotionArtwork] Failed loading motion cover: {ex}");
            }
        }

        private void OnMotionVideoCached(string videoUrl, string localFilePath)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (_currentMotionResult != null && _currentMotionResult.Url == videoUrl)
                {
                    _currentMotionResult.LocalFilePath = localFilePath;
                    var newUri = new Uri(System.IO.Path.GetFullPath(localFilePath), UriKind.Absolute);

                    // Seamlessly transition running video to local zero-latency file
                    if (_isFullscreenMotionActive)
                    {
                        if (FullscreenMotionVideo != null && FullscreenMotionVideo.Source != null && FullscreenMotionVideo.Source.OriginalString != localFilePath)
                        {
                            FullscreenMotionVideo.Source = newUri;
                            FullscreenMotionVideo.Stop();
                            if (_isPlaying) FullscreenMotionVideo.Play();
                        }
                        if (MotionVideoBackdrop != null && MotionVideoBackdrop.Source != null && MotionVideoBackdrop.Source.OriginalString != localFilePath)
                        {
                            MotionVideoBackdrop.Source = newUri;
                            MotionVideoBackdrop.Stop();
                            if (_isPlaying) MotionVideoBackdrop.Play();
                        }
                    }
                    else
                    {
                        if (AlbumCoverVideo != null && AlbumCoverVideo.Source != null && AlbumCoverVideo.Source.OriginalString != localFilePath)
                        {
                            AlbumCoverVideo.Source = newUri;
                            AlbumCoverVideo.Stop();
                            if (_isPlaying) AlbumCoverVideo.Play();
                        }
                        if (TvMotionVideoBackdrop != null && TvMotionVideoBackdrop.Source != null && TvMotionVideoBackdrop.Source.OriginalString != localFilePath)
                        {
                            TvMotionVideoBackdrop.Source = newUri;
                            TvMotionVideoBackdrop.Stop();
                            if (_isPlaying) TvMotionVideoBackdrop.Play();
                        }
                    }
                }
            });
        }

        private void ResetMotionArtwork()
        {
            try
            {
                _motionCts?.Cancel();
                _motionCts?.Dispose();
            }
            catch { }
            _motionCts = null;

            _albumVideoFrameTimer?.Stop();
            _albumVideoFrameTimer = null;
            _fullscreenVideoFrameTimer?.Stop();
            _fullscreenVideoFrameTimer = null;

            _isMotionAvailable = false;
            _currentMotionResult = null;
            _hasAttemptedFallback = false;

            if (AlbumCoverVideo != null)
            {
                AlbumCoverVideo.Stop();
                AlbumCoverVideo.Source = null;
            }

            if (TvMotionVideoBackdrop != null)
            {
                TvMotionVideoBackdrop.BeginAnimation(UIElement.OpacityProperty, null);
                TvMotionVideoBackdrop.Opacity = 0.0;
                TvMotionVideoBackdrop.Stop();
                TvMotionVideoBackdrop.Source = null;
            }

            if (AlbumCoverImage != null)
            {
                AlbumCoverImage.BeginAnimation(UIElement.OpacityProperty, null);
                AlbumCoverImage.Opacity = 1.0;
            }

            if (MotionBadge != null)
            {
                MotionBadge.Visibility = Visibility.Collapsed;
            }

            if (FullscreenMotionVideo != null)
            {
                FullscreenMotionVideo.Stop();
                FullscreenMotionVideo.Source = null;
            }

            if (MotionVideoBackdrop != null)
            {
                MotionVideoBackdrop.BeginAnimation(UIElement.OpacityProperty, null);
                MotionVideoBackdrop.Opacity = 0.0;
                MotionVideoBackdrop.Stop();
                MotionVideoBackdrop.Source = null;
            }

            if (FullscreenCoverImage != null)
            {
                FullscreenCoverImage.BeginAnimation(UIElement.OpacityProperty, null);
                FullscreenCoverImage.Opacity = 1.0;
            }

            UpdateMotionButtonUI();
        }

        private void MotionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isFullscreenMotionActive)
            {
                ExitFullscreenMotionMode();
            }
            else
            {
                EnterFullscreenMotionMode();
            }
        }

        private void UpdateMotionButtonUI()
        {
            if (MotionButton == null) return;

            if (_isMotionAvailable)
            {
                MotionButton.Opacity = 1.0;
                if (MotionButtonIcon != null)
                {
                    MotionButtonIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 68, 255, 212)); // Glowing neon cyan/mint
                }
                MotionButton.ToolTip = "Fullscreen Animated Album Cover (Active - M / Double-click)";
            }
            else
            {
                MotionButton.Opacity = 0.85;
                if (MotionButtonIcon != null)
                {
                    MotionButtonIcon.Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255));
                }
                MotionButton.ToolTip = "Fullscreen Animated Album Cover / Canvas (M / Double-click)";
            }
        }

        private void UpdateMotionBlurredBackdrop()
        {
            if (MotionBlurredBackdrop == null) return;

            if (_currentBlurredBackdrop != null)
            {
                MotionBlurredBackdrop.Source = _currentBlurredBackdrop;
                return;
            }

            // Otherwise generate from current track thumbnail or album cover image
            var thumb = AlbumCoverImage?.Source as BitmapSource ?? _mediaService.CurrentTrack?.Thumbnail;
            if (thumb != null)
            {
                _currentBlurredBackdrop = BackdropBlur.CreateBlurredBackdrop(thumb);
                MotionBlurredBackdrop.Source = _currentBlurredBackdrop ?? thumb;
            }
            else
            {
                MotionBlurredBackdrop.Source = null;
            }
        }

        private void EnterFullscreenMotionMode()
        {
            _isFullscreenMotionActive = true;

            // Immediately set the fullscreen static cover image & blurred backdrop BEFORE stage is made visible
            if (FullscreenCoverImage != null)
            {
                FullscreenCoverImage.BeginAnimation(UIElement.OpacityProperty, null);
                FullscreenCoverImage.Source = AlbumCoverImage?.Source ?? _mediaService.CurrentTrack?.Thumbnail ?? _lastBackdropSource;
                FullscreenCoverImage.Opacity = 1.0;
            }

            UpdateMotionBlurredBackdrop();

            if (MotionSongTitleText != null) MotionSongTitleText.Text = TrackTitleText.Text;
            if (MotionArtistText != null) MotionArtistText.Text = ArtistNameText.Text;

            FullscreenMotionStage.Visibility = Visibility.Visible;
            VisualizerContentGrid.Visibility = Visibility.Collapsed;
            FullscreenLyricsStage.Visibility = Visibility.Collapsed;

            // Resolve URI for motion artwork
            Uri? uriToPlay = null;
            if (_currentMotionResult != null)
            {
                string targetUri = !string.IsNullOrEmpty(_currentMotionResult.LocalFilePath) && System.IO.File.Exists(_currentMotionResult.LocalFilePath)
                    ? _currentMotionResult.LocalFilePath
                    : _currentMotionResult.Url;

                uriToPlay = targetUri.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? new Uri(targetUri, UriKind.Absolute)
                    : new Uri(System.IO.Path.GetFullPath(targetUri), UriKind.Absolute);
            }
            else if (AlbumCoverVideo?.Source != null)
            {
                uriToPlay = AlbumCoverVideo.Source;
            }

            // Stop small video and release D3D surface so fullscreen video has full hardware decoder
            if (AlbumCoverVideo != null)
            {
                _albumVideoFrameTimer?.Stop();
                _albumVideoFrameTimer = null;
                AlbumCoverVideo.Stop();
                AlbumCoverVideo.Source = null;
            }
            if (TvMotionVideoBackdrop != null)
            {
                TvMotionVideoBackdrop.Stop();
                TvMotionVideoBackdrop.Source = null;
                TvMotionVideoBackdrop.Opacity = 0.0;
            }

            if (uriToPlay != null)
            {
                if (FullscreenMotionVideo != null)
                {
                    FullscreenMotionVideo.Source = uriToPlay;
                    FullscreenMotionVideo.Stop();
                    if (_isPlaying) FullscreenMotionVideo.Play();
                }
                if (MotionVideoBackdrop != null)
                {
                    MotionVideoBackdrop.Source = uriToPlay;
                    MotionVideoBackdrop.Stop();
                    if (_isPlaying) MotionVideoBackdrop.Play();
                }
            }

            // Fade in hint pill and schedule smooth fade out
            if (MotionHintPill != null)
            {
                MotionHintPill.Opacity = 1.0;
                MotionHintPill.Visibility = Visibility.Visible;

                _motionHintTimer?.Stop();
                _motionHintTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(3)
                };
                _motionHintTimer.Tick += (s, e) =>
                {
                    _motionHintTimer?.Stop();
                    var fade = new System.Windows.Media.Animation.DoubleAnimation(0.0, TimeSpan.FromMilliseconds(500));
                    MotionHintPill.BeginAnimation(UIElement.OpacityProperty, fade);
                };
                _motionHintTimer.Start();
            }

            // Initialize and show Motion Player Bar
            if (MotionPlayerBar != null)
            {
                MotionPlayerBar.BeginAnimation(UIElement.OpacityProperty, null);
                MotionPlayerBar.Opacity = 1.0;
                MotionPlayerBar.Visibility = Visibility.Visible;
                SetPlayPauseIconState(_isPlaying);
                UpdatePlaybackTimeUI(_currentTrackPosition, _currentTrackDuration);
                ResetMotionBarFadeTimer();
            }
        }

        private void ExitFullscreenMotionMode()
        {
            if (!_isFullscreenMotionActive) return;

            _isFullscreenMotionActive = false;
            _motionHintTimer?.Stop();
            _fullscreenVideoFrameTimer?.Stop();
            _fullscreenVideoFrameTimer = null;
            _motionBarFadeTimer?.Stop();

            if (MotionPlayerBar != null)
            {
                MotionPlayerBar.BeginAnimation(UIElement.OpacityProperty, null);
            }

            // Stop fullscreen video and release D3D hardware decoder
            if (FullscreenMotionVideo != null)
            {
                FullscreenMotionVideo.Stop();
                FullscreenMotionVideo.Source = null;
            }

            if (MotionVideoBackdrop != null)
            {
                MotionVideoBackdrop.Stop();
                MotionVideoBackdrop.Source = null;
                MotionVideoBackdrop.Opacity = 0.0;
            }

            if (FullscreenCoverImage != null)
            {
                FullscreenCoverImage.BeginAnimation(UIElement.OpacityProperty, null);
                FullscreenCoverImage.Source = null;
                FullscreenCoverImage.Opacity = 1.0;
            }

            // Ensure small video starts hidden behind 100% opaque cover image
            if (AlbumCoverImage != null)
            {
                AlbumCoverImage.BeginAnimation(UIElement.OpacityProperty, null);
                AlbumCoverImage.Opacity = 1.0;
            }

            FullscreenMotionStage.Visibility = Visibility.Collapsed;
            VisualizerContentGrid.Visibility = Visibility.Visible;

            // Re-mount to small album cover video if motion artwork is available
            if (_isMotionAvailable && _currentMotionResult != null)
            {
                string targetUri = !string.IsNullOrEmpty(_currentMotionResult.LocalFilePath) && System.IO.File.Exists(_currentMotionResult.LocalFilePath)
                    ? _currentMotionResult.LocalFilePath
                    : _currentMotionResult.Url;

                var uri = targetUri.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? new Uri(targetUri, UriKind.Absolute)
                    : new Uri(System.IO.Path.GetFullPath(targetUri), UriKind.Absolute);

                if (AlbumCoverVideo != null)
                {
                    AlbumCoverVideo.Source = uri;
                    AlbumCoverVideo.Stop();
                    if (_isPlaying) AlbumCoverVideo.Play();
                }
                if (TvMotionVideoBackdrop != null)
                {
                    TvMotionVideoBackdrop.Source = uri;
                    TvMotionVideoBackdrop.Stop();
                    if (_isPlaying) TvMotionVideoBackdrop.Play();
                }
            }
        }

        private void FullscreenVisualizerWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible)
            {
                if (_isMotionAvailable && _isPlaying)
                {
                    if (_isFullscreenMotionActive)
                    {
                        if (FullscreenMotionVideo != null && FullscreenMotionVideo.Source != null)
                            FullscreenMotionVideo.Play();
                        if (MotionVideoBackdrop != null && MotionVideoBackdrop.Source != null)
                            MotionVideoBackdrop.Play();
                    }
                    else
                    {
                        if (AlbumCoverVideo != null && AlbumCoverVideo.Source != null)
                            AlbumCoverVideo.Play();
                        if (TvMotionVideoBackdrop != null && TvMotionVideoBackdrop.Source != null)
                            TvMotionVideoBackdrop.Play();
                    }
                }
            }
            else
            {
                AlbumCoverVideo?.Pause();
                TvMotionVideoBackdrop?.Pause();
                FullscreenMotionVideo?.Pause();
                MotionVideoBackdrop?.Pause();
            }
        }

        private void AlbumArtContainer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                e.Handled = true;
                EnterFullscreenMotionMode();
            }
        }

        private void AlbumArtContainer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                e.Handled = true;
                EnterFullscreenMotionMode();
            }
        }

        private void MotionBadge_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            EnterFullscreenMotionMode();
        }

        private void FullscreenMotionStage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (MotionPlayerBar != null && (e.Source == MotionPlayerBar || MotionPlayerBar.IsAncestorOf(e.OriginalSource as DependencyObject)))
            {
                return;
            }
            if (e.ClickCount == 2)
            {
                e.Handled = true;
                ExitFullscreenMotionMode();
            }
        }

        private void FullscreenMotionStage_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (MotionPlayerBar != null && (e.Source == MotionPlayerBar || MotionPlayerBar.IsAncestorOf(e.OriginalSource as DependencyObject)))
            {
                return;
            }
            if (e.ClickCount == 2)
            {
                e.Handled = true;
                ExitFullscreenMotionMode();
            }
        }

        private void FullscreenMotionStage_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_isFullscreenMotionActive) return;
            ShowMotionControls();
        }

        private void ShowMotionControls()
        {
            if (MotionPlayerBar == null) return;

            if (MotionPlayerBar.Visibility != Visibility.Visible || MotionPlayerBar.Opacity < 0.99)
            {
                MotionPlayerBar.Visibility = Visibility.Visible;
                var anim = new DoubleAnimation
                {
                    To = 1.0,
                    Duration = TimeSpan.FromMilliseconds(220),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                MotionPlayerBar.BeginAnimation(UIElement.OpacityProperty, anim);
            }

            ResetMotionBarFadeTimer();
        }

        private void ResetMotionBarFadeTimer()
        {
            if (!_isFullscreenMotionActive) return;

            _motionBarFadeTimer?.Stop();
            _motionBarFadeTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3.5)
            };
            _motionBarFadeTimer.Tick += (s, e) =>
            {
                _motionBarFadeTimer?.Stop();
                if (!_isFullscreenMotionActive) return;

                // If user is currently dragging/seeking or hovering directly over the bar, don't fade out!
                if (_isUserSeeking || (MotionPlayerBar != null && MotionPlayerBar.IsMouseOver))
                {
                    ResetMotionBarFadeTimer();
                    return;
                }

                if (MotionPlayerBar != null)
                {
                    var fadeOut = new DoubleAnimation
                    {
                        To = 0.0,
                        Duration = TimeSpan.FromMilliseconds(550),
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
                    };
                    MotionPlayerBar.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                }
            };
            _motionBarFadeTimer.Start();
        }

        private void MotionPlayerBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ResetMotionBarFadeTimer();
        }

        private void MotionPlayerBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            ResetMotionBarFadeTimer();
            if (e.ClickCount == 2)
            {
                e.Handled = true;
            }
        }

        private void MotionTimeline_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_currentTrackDuration <= TimeSpan.Zero) return;
            ResetMotionBarFadeTimer();

            _isUserSeeking = true;
            _wasPlayingBeforeSeek = _isPlaying;
            int seq = System.Threading.Interlocked.Increment(ref _seekSequence);

            if (_wasPlayingBeforeSeek)
            {
                _ = _mediaService.PauseAsync();
                _isPlaying = false;
                SetPlayPauseIconState(false);
            }

            MotionTimelineTrackContainer.CaptureMouse();

            double totalWidth = MotionTimelineTrackContainer.ActualWidth > 0 ? MotionTimelineTrackContainer.ActualWidth : 360.0;
            double mouseX = Math.Clamp(e.GetPosition(MotionTimelineTrackContainer).X, 0, totalWidth);
            double ratio = mouseX / totalWidth;

            _seekTargetTime = TimeSpan.FromSeconds(ratio * _currentTrackDuration.TotalSeconds);
            _currentTrackPosition = _seekTargetTime;

            MotionProgressBarScale.ScaleX = Math.Max(0.001, mouseX);
            MotionKnobTranslate.X = mouseX - 5.0;

            double mainWidth = TimelineTrackContainer.ActualWidth > 0 ? TimelineTrackContainer.ActualWidth : 450.0;
            double mainProgress = ratio * mainWidth;
            ProgressBarScale.ScaleX = Math.Max(0.001, mainProgress);
            KnobTranslate.X = mainProgress - 5.0;

            _lastDisplayedPosSec = (int)_seekTargetTime.TotalSeconds;
            string posStr = FormatTime(_seekTargetTime);
            var remaining = _currentTrackDuration - _seekTargetTime;
            string remStr = "-" + FormatTime(remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining);

            PositionText.Text = posStr;
            DurationText.Text = remStr;
            if (MotionPositionText != null) MotionPositionText.Text = posStr;
            if (MotionDurationText != null) MotionDurationText.Text = remStr;

            e.Handled = true;
        }

        private void MotionTimeline_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            ResetMotionBarFadeTimer();
            if (!_isUserSeeking || _currentTrackDuration <= TimeSpan.Zero) return;

            double totalWidth = MotionTimelineTrackContainer.ActualWidth > 0 ? MotionTimelineTrackContainer.ActualWidth : 360.0;
            double mouseX = Math.Clamp(e.GetPosition(MotionTimelineTrackContainer).X, 0, totalWidth);
            double ratio = mouseX / totalWidth;

            _seekTargetTime = TimeSpan.FromSeconds(ratio * _currentTrackDuration.TotalSeconds);
            _currentTrackPosition = _seekTargetTime;

            MotionProgressBarScale.ScaleX = Math.Max(0.001, mouseX);
            MotionKnobTranslate.X = mouseX - 5.0;

            double mainWidth = TimelineTrackContainer.ActualWidth > 0 ? TimelineTrackContainer.ActualWidth : 450.0;
            double mainProgress = ratio * mainWidth;
            ProgressBarScale.ScaleX = Math.Max(0.001, mainProgress);
            KnobTranslate.X = mainProgress - 5.0;

            _lastDisplayedPosSec = (int)_seekTargetTime.TotalSeconds;
            string posStr = FormatTime(_seekTargetTime);
            var remaining = _currentTrackDuration - _seekTargetTime;
            string remStr = "-" + FormatTime(remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining);

            PositionText.Text = posStr;
            DurationText.Text = remStr;
            if (MotionPositionText != null) MotionPositionText.Text = posStr;
            if (MotionDurationText != null) MotionDurationText.Text = remStr;

            e.Handled = true;
        }

        private async void MotionTimeline_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isUserSeeking) return;
            ResetMotionBarFadeTimer();

            _isUserSeeking = false;
            MotionTimelineTrackContainer.ReleaseMouseCapture();

            if (_currentTrackDuration > TimeSpan.Zero)
            {
                int seq = _seekSequence;

                double totalWidth = MotionTimelineTrackContainer.ActualWidth > 0 ? MotionTimelineTrackContainer.ActualWidth : 360.0;
                double mouseX = Math.Clamp(e.GetPosition(MotionTimelineTrackContainer).X, 0, totalWidth);
                double ratio = mouseX / totalWidth;

                _seekTargetTime = TimeSpan.FromSeconds(ratio * _currentTrackDuration.TotalSeconds);
                _currentTrackPosition = _seekTargetTime;
                _lyricClock.Reset(_seekTargetTime, _isPlaying);

                _ignoreStaleTimelineUntil = DateTime.UtcNow.AddSeconds(3.0);

                await _mediaService.ChangePlaybackPositionAsync(_seekTargetTime);
                await System.Threading.Tasks.Task.Delay(800);

                if (seq == _seekSequence && _wasPlayingBeforeSeek)
                {
                    await _mediaService.PlayAsync();
                    _isPlaying = true;
                    SetPlayPauseIconState(true);
                }
            }

            e.Handled = true;
        }

        private void ExitMotion_Click(object sender, RoutedEventArgs e)
        {
            ExitFullscreenMotionMode();
        }

        private void AlbumCoverVideo_MediaOpened(object sender, RoutedEventArgs e)
        {
            _isMotionAvailable = true;
            App.Log("[MotionArtwork] AlbumCoverVideo MediaOpened successfully!");
            if (MotionBadge != null) MotionBadge.Visibility = Visibility.Visible;
            UpdateMotionButtonUI();

            AlbumCoverVideo.Play();
            if (TvMotionVideoBackdrop != null && TvMotionVideoBackdrop.Source != null)
            {
                TvMotionVideoBackdrop.Play();
            }

            _albumVideoFrameTimer?.Stop();
            int ticks = 0;
            _albumVideoFrameTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(20)
            };
            _albumVideoFrameTimer.Tick += (s, ev) =>
            {
                ticks++;
                bool frameReady = AlbumCoverVideo.Position > TimeSpan.FromMilliseconds(25);
                // When frame is verified rendered, fade out the top static cover image to reveal video underneath
                if (frameReady || ticks >= 50)
                {
                    _albumVideoFrameTimer?.Stop();
                    _albumVideoFrameTimer = null;

                    if (AlbumCoverImage != null)
                    {
                        var fade = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(350))
                        {
                            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                        };
                        AlbumCoverImage.BeginAnimation(UIElement.OpacityProperty, fade);
                    }

                    if (TvMotionVideoBackdrop != null && TvMotionVideoBackdrop.Source != null)
                    {
                        var videoFade = new System.Windows.Media.Animation.DoubleAnimation(0.0, _settings.BackdropBrightness, TimeSpan.FromMilliseconds(450))
                        {
                            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                        };
                        TvMotionVideoBackdrop.BeginAnimation(UIElement.OpacityProperty, videoFade);
                    }

                    if (!_isPlaying)
                    {
                        AlbumCoverVideo.Pause();
                        TvMotionVideoBackdrop?.Pause();
                    }
                }
            };
            _albumVideoFrameTimer.Start();
        }

        private void AlbumCoverVideo_MediaEnded(object sender, RoutedEventArgs e)
        {
            // Reset position to zero and continue playback without tearing down the DirectShow graph
            AlbumCoverVideo.Position = TimeSpan.Zero;
            AlbumCoverVideo.Play();
        }

        private void TvMotionVideoBackdrop_MediaEnded(object sender, RoutedEventArgs e)
        {
            if (TvMotionVideoBackdrop != null)
            {
                TvMotionVideoBackdrop.Position = TimeSpan.Zero;
                TvMotionVideoBackdrop.Play();
            }
        }

        private void AlbumCoverVideo_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            _albumVideoFrameTimer?.Stop();
            _albumVideoFrameTimer = null;

            if (TvMotionVideoBackdrop != null)
            {
                TvMotionVideoBackdrop.BeginAnimation(UIElement.OpacityProperty, null);
                TvMotionVideoBackdrop.Opacity = 0.0;
                TvMotionVideoBackdrop.Stop();
                TvMotionVideoBackdrop.Source = null;
            }

            App.Log($"[MotionArtwork] AlbumCoverVideo MediaFailed: {e.ErrorException?.Message}");
            if (_currentMotionResult?.FallbackUrl != null && !_hasAttemptedFallback)
            {
                _hasAttemptedFallback = true;
                App.Log($"[MotionArtwork] Retrying with FallbackUrl: {_currentMotionResult.FallbackUrl}");
                var uri = _currentMotionResult.FallbackUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? new Uri(_currentMotionResult.FallbackUrl, UriKind.Absolute)
                    : new Uri(System.IO.Path.GetFullPath(_currentMotionResult.FallbackUrl), UriKind.Absolute);
                AlbumCoverVideo.Source = uri;
                AlbumCoverVideo.Stop();
                AlbumCoverVideo.Play();
                if (TvMotionVideoBackdrop != null)
                {
                    TvMotionVideoBackdrop.Source = uri;
                    TvMotionVideoBackdrop.Stop();
                    TvMotionVideoBackdrop.Play();
                }
                return;
            }

            _isMotionAvailable = false;
            if (AlbumCoverImage != null)
            {
                AlbumCoverImage.BeginAnimation(UIElement.OpacityProperty, null);
                AlbumCoverImage.Opacity = 1.0;
            }
            if (MotionBadge != null) MotionBadge.Visibility = Visibility.Collapsed;
            UpdateMotionButtonUI();
        }

        private void FullscreenMotionVideo_MediaOpened(object sender, RoutedEventArgs e)
        {
            _isMotionAvailable = true;
            App.Log("[MotionArtwork] FullscreenMotionVideo MediaOpened successfully!");

            // Always start play to decode initial frames
            FullscreenMotionVideo.Play();
            if (MotionVideoBackdrop != null && MotionVideoBackdrop.Source != null)
            {
                MotionVideoBackdrop.Play();
            }

            _fullscreenVideoFrameTimer?.Stop();
            int ticks = 0;
            _fullscreenVideoFrameTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(20)
            };
            _fullscreenVideoFrameTimer.Tick += (s, ev) =>
            {
                ticks++;
                bool frameReady = FullscreenMotionVideo.Position > TimeSpan.FromMilliseconds(25);
                // When frame is verified rendered, fade out the top static cover image to reveal video underneath
                if (frameReady || ticks >= 50)
                {
                    _fullscreenVideoFrameTimer?.Stop();
                    _fullscreenVideoFrameTimer = null;

                    if (FullscreenCoverImage != null)
                    {
                        var fade = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(350))
                        {
                            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                        };
                        FullscreenCoverImage.BeginAnimation(UIElement.OpacityProperty, fade);
                    }

                    if (MotionVideoBackdrop != null && MotionVideoBackdrop.Source != null)
                    {
                        var videoFade = new System.Windows.Media.Animation.DoubleAnimation(0.0, _settings.BackdropBrightness, TimeSpan.FromMilliseconds(450))
                        {
                            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                        };
                        MotionVideoBackdrop.BeginAnimation(UIElement.OpacityProperty, videoFade);
                    }

                    if (!_isPlaying)
                    {
                        FullscreenMotionVideo.Pause();
                        MotionVideoBackdrop?.Pause();
                    }
                }
            };
            _fullscreenVideoFrameTimer.Start();
        }

        private void FullscreenMotionVideo_MediaEnded(object sender, RoutedEventArgs e)
        {
            // Reset position to zero and continue playback without tearing down the DirectShow graph
            FullscreenMotionVideo.Position = TimeSpan.Zero;
            FullscreenMotionVideo.Play();
        }

        private void MotionVideoBackdrop_MediaEnded(object sender, RoutedEventArgs e)
        {
            if (MotionVideoBackdrop != null)
            {
                MotionVideoBackdrop.Position = TimeSpan.Zero;
                MotionVideoBackdrop.Play();
            }
        }

        private void FullscreenMotionVideo_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            _fullscreenVideoFrameTimer?.Stop();
            _fullscreenVideoFrameTimer = null;

            if (MotionVideoBackdrop != null)
            {
                MotionVideoBackdrop.BeginAnimation(UIElement.OpacityProperty, null);
                MotionVideoBackdrop.Opacity = 0.0;
                MotionVideoBackdrop.Stop();
                MotionVideoBackdrop.Source = null;
            }

            App.Log($"[MotionArtwork] FullscreenMotionVideo MediaFailed: {e.ErrorException?.Message}");
            if (_currentMotionResult?.FallbackUrl != null && !_hasAttemptedFallback)
            {
                _hasAttemptedFallback = true;
                App.Log($"[MotionArtwork] Retrying FullscreenMotionVideo with FallbackUrl: {_currentMotionResult.FallbackUrl}");
                var uri = _currentMotionResult.FallbackUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? new Uri(_currentMotionResult.FallbackUrl, UriKind.Absolute)
                    : new Uri(System.IO.Path.GetFullPath(_currentMotionResult.FallbackUrl), UriKind.Absolute);
                FullscreenMotionVideo.Source = uri;
                FullscreenMotionVideo.Stop();
                FullscreenMotionVideo.Play();
                if (MotionVideoBackdrop != null)
                {
                    MotionVideoBackdrop.Source = uri;
                    MotionVideoBackdrop.Stop();
                    MotionVideoBackdrop.Play();
                }
            }
            else
            {
                if (FullscreenCoverImage != null)
                {
                    FullscreenCoverImage.BeginAnimation(UIElement.OpacityProperty, null);
                    FullscreenCoverImage.Opacity = 1.0;
                }
            }
        }
    }
}

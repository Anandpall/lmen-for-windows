using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using LumnWindows.Audio;
using LumnWindows.Helpers;
using LumnWindows.Media;
using LumnWindows.Settings;

namespace LumnWindows.Overlay;

public partial class OverlayWindow : Window
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastTime = 0.0;
    private SpectralFrame _frame;

    private List<Color> _palette = AlbumArtColorExtractor.GetDefaultPalette();
    private List<Color> _targetPalette = AlbumArtColorExtractor.GetDefaultPalette();
    private float _paletteBlend = 1.0f;
    private double _revolutionAngle = 0.0;

    private AppSettings _settings;
    private string _lastSelectedDisplay = "All Displays";
    public bool IsMusicPlaying { get; set; } = true;

    public OverlayWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        Loaded += OnLoaded;
        Closing += (s, e) => CompositionTarget.Rendering -= OnRendering;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateWindowBounds();
        SystemEvents.DisplaySettingsChanged += (s, ev) => UpdateWindowBounds();
        CompositionTarget.Rendering += OnRendering;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        Win32Helper.EnableClickThrough(hwnd);
    }

    public void UpdateWindowBounds()
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                if (_settings.SelectedDisplay != "All Displays")
                {
                    var screens = System.Windows.Forms.Screen.AllScreens;
                    for (int i = 0; i < screens.Length; i++)
                    {
                        var s = screens[i];
                        string name1 = $"Display {i + 1}" + (s.Primary ? " (Primary)" : "");
                        if (_settings.SelectedDisplay.StartsWith(name1, StringComparison.OrdinalIgnoreCase))
                        {
                            Left = s.Bounds.Left;
                            Top = s.Bounds.Top;
                            Width = s.Bounds.Width;
                            Height = s.Bounds.Height;
                            return;
                        }
                    }
                }
            }
            catch { }

            // Default: All Displays
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;
        });
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_settings.IsEnabled || Width <= 0 || Height <= 0)
        {
            RootGrid.Visibility = Visibility.Collapsed;
            return;
        }

        if (_settings.AnimationMode == "Off")
        {
            RootGrid.Visibility = Visibility.Collapsed;
            return;
        }

        RootGrid.Visibility = Visibility.Visible;

        double now = _clock.Elapsed.TotalSeconds;
        double dt = now - _lastTime;
        _lastTime = now;
        if (dt <= 0.0 || dt > 0.1) dt = 0.016;

        // Smooth palette cross-fade
        if (_paletteBlend < 1.0f)
        {
            _paletteBlend = Math.Min(1.0f, _paletteBlend + (float)(dt * 2.5));
            _palette = AlbumArtColorExtractor.BlendPalettes(_palette, _targetPalette, _paletteBlend);
        }

        // --- SMOOTH PERIMETER ROTATION ---
        // Noticeable, fluid revolution speed (~5.5s per loop at 1.0x)
        bool shouldRotate = _settings.AnimationMode != "Static" && _settings.RevolveEnabled;
        if (shouldRotate)
        {
            double speed = Math.Max(0.12, _settings.PerimeterRotationSpeed * 0.18);
            if (_settings.AnimationMode == "Music Sync")
            {
                // Speed accelerates dynamically with rhythm and mid-range beats
                speed *= (0.85 + _frame.Mid * 0.90 + _frame.SubBass * 0.45);
            }
            _revolutionAngle = (_revolutionAngle + dt * speed) % 1.0;
        }

        // --- CONTINUOUS LINEAR PERIMETER GRADIENTS ---
        // Top Edge (Left -> Right)
        TopEdge.Fill = CreateEdgeBrush(0.00, 0.25, isHorizontal: true, isReversed: false);

        // Right Edge (Top -> Bottom)
        RightEdge.Fill = CreateEdgeBrush(0.25, 0.50, isHorizontal: false, isReversed: false);

        // Bottom Edge (Right -> Left)
        BottomEdge.Fill = CreateEdgeBrush(0.50, 0.75, isHorizontal: true, isReversed: true);

        // Left Edge (Bottom -> Top)
        LeftEdge.Fill = CreateEdgeBrush(0.75, 1.00, isHorizontal: false, isReversed: true);

        // --- THICKNESS & INWARD REACH ---
        double userThickness = Math.Max(2.0, _settings.Thickness);
        double edgeThickness = 28.0 + (userThickness * 1.5); // from 31px to 148px

        if (_settings.AnimationMode == "Music Sync")
        {
            // Kick drum physically pulses the thickness inward
            edgeThickness += _frame.SubBass * 42.0 * _settings.BeatSensitivity
                           + _frame.Bass * 18.0 * _settings.BeatSensitivity;
        }

        TopEdge.Height = edgeThickness;
        BottomEdge.Height = edgeThickness;
        LeftEdge.Width = edgeThickness;
        RightEdge.Width = edgeThickness;

        // --- GAUSSIAN BLUR RADIUS ---
        double blurRadius = Math.Max(25.0, 48.0 * _settings.GlowAmount);
        if (_settings.AnimationMode == "Music Sync")
        {
            blurRadius += _frame.SubBass * 28.0 * _settings.BeatSensitivity;
        }

        TopBlur.Radius = blurRadius;
        BottomBlur.Radius = blurRadius;
        LeftBlur.Radius = blurRadius;
        RightBlur.Radius = blurRadius;

        // --- MASTER OPACITY & FLARES ---
        double opacity = Math.Clamp((_settings.GlowBrightness / 100.0) * _settings.MasterOpacity, 0.0, 1.0);

        if (_settings.AnimationMode == "Music Sync")
        {
            // Beat attacks trigger instant bright flares
            opacity += _frame.BeatOnset * 0.38 * _settings.BeatSensitivity
                     + _frame.UpperMid * 0.12;
        }
        else if (_settings.AnimationMode == "Breathing")
        {
            double breath = (Math.Sin(now * 2.0) + 1.0) * 0.5;
            opacity *= (0.35 + 0.65 * breath);
        }

        // Idle state when audio is quiet
        bool isIdle = !IsMusicPlaying || _frame.Fullness < 0.005f;
        if (isIdle && _settings.AnimationMode != "Static")
        {
            if (!_settings.IdleGlow)
            {
                RootGrid.Opacity = 0;
                return;
            }
            double breathe = (Math.Sin(now * 1.4) + 1.0) * 0.5;
            opacity = Math.Clamp(opacity * (0.35 + 0.25 * breathe), 0.15, 0.70);
        }
        else
        {
            opacity = Math.Clamp(opacity, 0.05, 1.0);
        }

        RootGrid.Opacity = opacity;
    }

    private LinearGradientBrush CreateEdgeBrush(double startFrac, double endFrac, bool isHorizontal, bool isReversed)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = isHorizontal
                ? (isReversed ? new Point(1, 0) : new Point(0, 0))
                : (isReversed ? new Point(0, 1) : new Point(0, 0)),
            EndPoint = isHorizontal
                ? (isReversed ? new Point(0, 0) : new Point(1, 0))
                : (isReversed ? new Point(0, 0) : new Point(0, 1)),
            MappingMode = BrushMappingMode.RelativeToBoundingBox
        };

        const int numStops = 10;
        for (int i = 0; i <= numStops; i++)
        {
            double t = (double)i / numStops;
            double perimPos = startFrac + (endFrac - startFrac) * t;
            double sampledPos = (perimPos + _revolutionAngle) % 1.0;
            if (sampledPos < 0) sampledPos += 1.0;

            Color c = SamplePalette(sampledPos);

            if (_settings.AnimationMode == "Music Sync" && _frame.LowMid > 0.15f)
            {
                c = AlbumArtColorExtractor.ShiftWarmth(c, _frame.LowMid * 0.25f);
            }

            brush.GradientStops.Add(new GradientStop(c, t));
        }

        brush.Freeze();
        return brush;
    }

    private Color SamplePalette(double pos)
    {
        if (_settings.OverrideAlbumColor)
        {
            Color p = ParseHex(_settings.PrimaryColorHex, Color.FromRgb(229, 57, 53));
            Color s = ParseHex(_settings.SecondaryColorHex, Color.FromRgb(0, 229, 255));

            p = AdjustBrightness(p, _settings.PrimaryBrightness / 100.0);
            s = AdjustBrightness(s, _settings.SecondaryBrightness / 100.0);

            double wave = 0.5 + 0.5 * Math.Sin(pos * 2.0 * Math.PI);
            return AlbumArtColorExtractor.Lerp(p, s, (float)wave);
        }

        if (_settings.GradientMode == "Rainbow")
        {
            return ColorFromHsv((pos * 360.0) % 360.0, 0.95, 1.0);
        }

        var pal = _palette;
        if (pal == null || pal.Count == 0) pal = AlbumArtColorExtractor.GetDefaultPalette();
        if (pal.Count == 1) return pal[0];

        double scaled = (pos % 1.0) * pal.Count;
        if (scaled < 0) scaled += pal.Count;

        int i0 = (int)scaled % pal.Count;
        int i1 = (i0 + 1) % pal.Count;
        double t = scaled - Math.Floor(scaled);

        return AlbumArtColorExtractor.Lerp(pal[i0], pal[i1], (float)t);
    }

    private static Color AdjustBrightness(Color c, double factor)
    {
        byte r = (byte)Math.Clamp((int)(c.R * factor), 0, 255);
        byte g = (byte)Math.Clamp((int)(c.G * factor), 0, 255);
        byte b = (byte)Math.Clamp((int)(c.B * factor), 0, 255);
        return Color.FromRgb(r, g, b);
    }

    private static Color ParseHex(string hex, Color fallback)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            hex = hex.TrimStart('#');
            if (hex.Length == 6)
            {
                byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                return Color.FromRgb(r, g, b);
            }
        }
        catch { }
        return fallback;
    }

    private static Color ColorFromHsv(double hue, double saturation, double value)
    {
        int hi = Convert.ToInt32(Math.Floor(hue / 60)) % 6;
        double f = hue / 60 - Math.Floor(hue / 60);

        value *= 255;
        byte v = (byte)Math.Clamp((int)value, 0, 255);
        byte p = (byte)Math.Clamp((int)(value * (1 - saturation)), 0, 255);
        byte q = (byte)Math.Clamp((int)(value * (1 - f * saturation)), 0, 255);
        byte t = (byte)Math.Clamp((int)(value * (1 - (1 - f) * saturation)), 0, 255);

        return hi switch
        {
            0 => Color.FromRgb(v, t, p),
            1 => Color.FromRgb(q, v, p),
            2 => Color.FromRgb(p, v, t),
            3 => Color.FromRgb(p, q, v),
            4 => Color.FromRgb(t, p, v),
            _ => Color.FromRgb(v, p, q),
        };
    }

    public void UpdateSpectralFrame(SpectralFrame frame)
    {
        _frame = frame;
    }

    public void UpdatePalette(List<Color> colors)
    {
        if (colors == null || colors.Count == 0) return;
        Dispatcher.Invoke(() =>
        {
            _targetPalette = new List<Color>(colors);
            _paletteBlend = 0.0f;
        });
    }

    public void UpdatePlaybackState(bool isPlaying)
    {
        IsMusicPlaying = isPlaying;
    }

    public void UpdateSettings(AppSettings settings)
    {
        _settings = settings;
        if (_lastSelectedDisplay != settings.SelectedDisplay)
        {
            _lastSelectedDisplay = settings.SelectedDisplay;
            UpdateWindowBounds();
        }
    }
}

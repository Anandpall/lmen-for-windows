using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using LumnWindows.Audio;
using LumnWindows.Media;
using LumnWindows.Settings;

namespace LumnWindows.Overlay;

public class BorderGlowRenderer
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastTime = 0.0;
    private SpectralFrame _frame;

    private List<Color> _palette = AlbumArtColorExtractor.GetDefaultPalette();
    private List<Color> _targetPalette = AlbumArtColorExtractor.GetDefaultPalette();
    private float _paletteBlend = 1.0f;
    private double _revolutionAngle = 0.0;

    private AppSettings _settings = new();
    public bool IsMusicPlaying { get; set; } = true;

    public void UpdateSpectralFrame(SpectralFrame frame) => _frame = frame;

    public void UpdatePalette(List<Color> newPalette)
    {
        if (newPalette == null || newPalette.Count == 0) return;
        _targetPalette = new List<Color>(newPalette);
        _paletteBlend = 0.0f;
    }

    public void UpdateSettings(AppSettings s)
    {
        _settings = s;
    }

    public void Render(DrawingContext dc, double screenW, double screenH)
    {
        if (!_settings.IsEnabled || screenW <= 0 || screenH <= 0) return;
        if (_settings.AnimationMode == "Off") return;

        double now = _clock.Elapsed.TotalSeconds;
        double dt = now - _lastTime;
        _lastTime = now;
        if (dt <= 0.0 || dt > 0.1) dt = 0.016;

        // Smooth palette cross-fade over ~1.5s
        if (_paletteBlend < 1.0f)
        {
            _paletteBlend = Math.Min(1.0f, _paletteBlend + (float)(dt * 2.0));
            _palette = AlbumArtColorExtractor.BlendPalettes(_palette, _targetPalette, _paletteBlend);
        }

        // Perimeter Rotation (smooth ~10s loop)
        bool shouldRotate = _settings.AnimationMode != "Static" && _settings.RevolveEnabled;
        if (shouldRotate)
        {
            double speed = _settings.PerimeterRotationSpeed * 0.10;
            if (_settings.AnimationMode == "Music Sync")
            {
                speed *= (0.80 + _frame.Mid * 0.8 + _frame.SubBass * 0.3);
            }
            _revolutionAngle = (_revolutionAngle + dt * speed) % 1.0;
        }

        // Responsive Thickness & Glow depth from sliders
        double userThickness = Math.Max(2.0, _settings.Thickness);
        double baseReach = 45.0 + (userThickness * 2.0); // 49px at min up to 205px at max

        // Audio reactivity: SubBass pulses physical inward reach
        double dynamicReach = baseReach;
        if (_settings.AnimationMode == "Music Sync")
        {
            dynamicReach += _frame.SubBass * baseReach * 0.65 * _settings.BeatSensitivity
                          + _frame.Bass * baseReach * 0.25 * _settings.BeatSensitivity;
        }

        // Master Opacity from Brightness slider (0 - 100)
        double opacity = Math.Clamp((_settings.GlowBrightness / 100.0) * _settings.MasterOpacity, 0.0, 1.0);

        if (_settings.AnimationMode == "Music Sync")
        {
            // Beat onset creates instant flare
            opacity += _frame.BeatOnset * 0.35 * _settings.BeatSensitivity
                     + _frame.UpperMid * 0.10;
        }
        else if (_settings.AnimationMode == "Breathing")
        {
            double breath = (Math.Sin(now * 2.0) + 1.0) * 0.5;
            opacity *= (0.35 + 0.65 * breath);
        }

        // Idle gentle breathing when audio is silent
        bool isIdle = !IsMusicPlaying || _frame.Fullness < 0.005f;
        if (isIdle && _settings.AnimationMode != "Static")
        {
            if (!_settings.IdleGlow) return;
            double breathe = (Math.Sin(now * 1.4) + 1.0) * 0.5;
            opacity = Math.Clamp(opacity * (0.30 + 0.25 * breathe), 0.12, 0.65);
        }
        else
        {
            opacity = Math.Clamp(opacity, 0.05, 1.0);
        }

        double diffusion = Math.Max(0.8, _settings.GlowAmount);

        // --- ULTRA-SOFT DUAL-PASS GAUSSIAN AMBIENT EMITTER AURA ---
        // Pass 1: Wide Dreamy Atmosphere (deep, whisper-soft haze)
        RenderEdgeEmitters(dc, screenW, screenH, dynamicReach * 1.8 * diffusion, opacity * 0.28, 65.0);

        // Pass 2: Core Velvet Glow (rich, vibrant, smooth body)
        RenderEdgeEmitters(dc, screenW, screenH, dynamicReach * 1.0 * diffusion, opacity * 0.65, 48.0);

        // --- 4 CORNER SOFT BLOSSOMS ---
        RenderCornerBlossoms(dc, screenW, screenH, dynamicReach * 1.6 * diffusion, opacity);
    }

    private void RenderEdgeEmitters(DrawingContext dc, double W, double H, double reach, double passOpacity, double step)
    {
        if (passOpacity <= 0.001 || reach <= 1.0) return;

        // TOP EDGE
        int numX = Math.Max(2, (int)Math.Ceiling(W / step));
        double dx = W / numX;
        for (int i = 0; i <= numX; i++)
        {
            double x = i * dx;
            double frac = (double)i / numX;
            double perim = (frac * 0.25 + _revolutionAngle) % 1.0;
            Color c = SamplePalette(perim);
            DrawRadialEmitter(dc, new Point(x, 0), dx * 2.2, reach, c, passOpacity);
        }

        // BOTTOM EDGE
        for (int i = 0; i <= numX; i++)
        {
            double x = i * dx;
            double frac = (double)i / numX;
            double perim = (0.50 + (1.0 - frac) * 0.25 + _revolutionAngle) % 1.0;
            Color c = SamplePalette(perim);
            DrawRadialEmitter(dc, new Point(x, H), dx * 2.2, reach, c, passOpacity);
        }

        // LEFT & RIGHT EDGES
        int numY = Math.Max(2, (int)Math.Ceiling(H / step));
        double dy = H / numY;

        // LEFT EDGE
        for (int i = 0; i <= numY; i++)
        {
            double y = i * dy;
            double frac = (double)i / numY;
            double perim = (0.75 + (1.0 - frac) * 0.25 + _revolutionAngle) % 1.0;
            Color c = SamplePalette(perim);
            DrawRadialEmitter(dc, new Point(0, y), reach, dy * 2.2, c, passOpacity);
        }

        // RIGHT EDGE
        for (int i = 0; i <= numY; i++)
        {
            double y = i * dy;
            double frac = (double)i / numY;
            double perim = (0.25 + frac * 0.25 + _revolutionAngle) % 1.0;
            Color c = SamplePalette(perim);
            DrawRadialEmitter(dc, new Point(W, y), reach, dy * 2.2, c, passOpacity);
        }
    }

    private static void DrawRadialEmitter(DrawingContext dc, Point center, double radiusX, double radiusY, Color color, double opacity)
    {
        var brush = new RadialGradientBrush
        {
            Center = center,
            GradientOrigin = center,
            RadiusX = radiusX,
            RadiusY = radiusY,
            MappingMode = BrushMappingMode.Absolute
        };

        // Multi-stop Gaussian falloff curve:
        // High opacity at the origin, dissolving into absolute transparency with zero hard boundaries
        byte a0 = (byte)Math.Clamp((int)(255 * opacity), 0, 255);
        byte a1 = (byte)Math.Clamp((int)(160 * opacity), 0, 255);
        byte a2 = (byte)Math.Clamp((int)(65  * opacity), 0, 255);
        byte a3 = (byte)Math.Clamp((int)(18  * opacity), 0, 255);

        brush.GradientStops.Add(new GradientStop(Color.FromArgb(a0, color.R, color.G, color.B), 0.00));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(a1, color.R, color.G, color.B), 0.24));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(a2, color.R, color.G, color.B), 0.52));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(a3, color.R, color.G, color.B), 0.78));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0,  color.R, color.G, color.B), 1.00));
        brush.Freeze();

        dc.DrawRectangle(brush, null, new Rect(center.X - radiusX, center.Y - radiusY, radiusX * 2, radiusY * 2));
    }

    private void RenderCornerBlossoms(DrawingContext dc, double W, double H, double radius, double opacity)
    {
        double cornerOpacity = opacity * 0.70;
        if (cornerOpacity <= 0.001 || radius <= 1.0) return;

        DrawCornerBlossom(dc, new Point(0, 0), radius, cornerOpacity, SamplePalette((_revolutionAngle) % 1.0));
        DrawCornerBlossom(dc, new Point(W, 0), radius, cornerOpacity, SamplePalette((0.25 + _revolutionAngle) % 1.0));
        DrawCornerBlossom(dc, new Point(W, H), radius, cornerOpacity, SamplePalette((0.50 + _revolutionAngle) % 1.0));
        DrawCornerBlossom(dc, new Point(0, H), radius, cornerOpacity, SamplePalette((0.75 + _revolutionAngle) % 1.0));
    }

    private static void DrawCornerBlossom(DrawingContext dc, Point center, double radius, double opacity, Color color)
    {
        var brush = new RadialGradientBrush
        {
            Center = center,
            GradientOrigin = center,
            RadiusX = radius,
            RadiusY = radius,
            MappingMode = BrushMappingMode.Absolute
        };

        byte a0 = (byte)Math.Clamp((int)(255 * opacity), 0, 255);
        byte a1 = (byte)Math.Clamp((int)(150 * opacity), 0, 255);
        byte a2 = (byte)Math.Clamp((int)(55  * opacity), 0, 255);

        brush.GradientStops.Add(new GradientStop(Color.FromArgb(a0, color.R, color.G, color.B), 0.00));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(a1, color.R, color.G, color.B), 0.30));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(a2, color.R, color.G, color.B), 0.65));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0,  color.R, color.G, color.B), 1.00));
        brush.Freeze();

        dc.DrawRectangle(brush, null, new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2));
    }

    private Color SamplePalette(double pos)
    {
        if (_settings.OverrideAlbumColor)
        {
            Color p = ParseHex(_settings.PrimaryColorHex, Color.FromRgb(229, 57, 53));
            Color s = ParseHex(_settings.SecondaryColorHex, Color.FromRgb(30, 136, 229));

            p = AdjustBrightness(p, _settings.PrimaryBrightness / 100.0);
            s = AdjustBrightness(s, _settings.SecondaryBrightness / 100.0);

            double wave = 0.5 + 0.5 * Math.Sin(pos * 2.0 * Math.PI);
            return AlbumArtColorExtractor.Lerp(p, s, (float)wave);
        }

        if (_settings.GradientMode == "Rainbow")
        {
            return ColorFromHsv((pos * 360.0) % 360.0, 0.90, 1.0);
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
}

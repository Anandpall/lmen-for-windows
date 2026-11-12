using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace LmenV2.Rendering.Kinetic
{
    /// <summary>
    /// Kinetic typography animation templates from Cotodama / LyricalStudio.
    /// </summary>
    public enum KineticTemplate
    {
        Auto,
        Stack,
        Slide,
        Zoom,
        Letters,
        Scatter,
        Graph,
        Flow,
        Sticker,
        Fisheye
    }

    /// <summary>
    /// Three vivid album-derived accent hues and a dark base tint.
    /// </summary>
    public sealed class KineticPalette
    {
        public Color[] Accents { get; set; } = new Color[3];
        public Color BaseColor { get; set; } = Color.FromRgb(11, 11, 18);

        public static readonly Color[] AppleSystemColors = new[]
        {
            Color.FromRgb(255, 55, 95),   // pink
            Color.FromRgb(255, 159, 10),  // orange
            Color.FromRgb(255, 214, 10),  // yellow
            Color.FromRgb(48, 209, 88),   // green
            Color.FromRgb(100, 210, 255), // cyan
            Color.FromRgb(10, 132, 255),  // blue
            Color.FromRgb(94, 92, 230),   // indigo
            Color.FromRgb(191, 90, 242)   // purple
        };

        public static KineticPalette CreateFallback(int seed)
        {
            int n = AppleSystemColors.Length;
            uint s = (uint)Math.Abs(seed);
            return new KineticPalette
            {
                Accents = new[]
                {
                    AppleSystemColors[s % n],
                    AppleSystemColors[(s + 3) % n],
                    AppleSystemColors[(s + 5) % n]
                },
                BaseColor = Color.FromRgb(11, 11, 18)
            };
        }

        public static KineticPalette FromDominantAndAccent(Color dominant, Color accent, int seed)
        {
            var p = CreateFallback(seed);
            p.Accents[0] = MakeVivid(dominant);
            p.Accents[1] = MakeVivid(accent);
            // Third accent: rotate hue ~60 degrees
            p.Accents[2] = RotateHue(p.Accents[0], 60.0);
            p.BaseColor = Color.FromRgb(
                (byte)Math.Clamp(dominant.R * 0.12, 8, 22),
                (byte)Math.Clamp(dominant.G * 0.12, 8, 22),
                (byte)Math.Clamp(dominant.B * 0.12, 12, 26)
            );
            return p;
        }

        private static Color MakeVivid(Color c)
        {
            RgbToHsl(c, out double h, out double s, out double l);
            s = Math.Clamp(s * 1.35, 0.70, 1.0);
            l = Math.Clamp(l, 0.58, 0.76);
            return HslToRgb(h, s, l);
        }

        private static Color RotateHue(Color c, double degrees)
        {
            RgbToHsl(c, out double h, out double s, out double l);
            h = (h + degrees / 360.0) % 1.0;
            if (h < 0.0) h += 1.0;
            return HslToRgb(h, s, l);
        }

        public static void RgbToHsl(Color c, out double h, out double s, out double l)
        {
            double r = c.R / 255.0;
            double g = c.G / 255.0;
            double b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            l = (max + min) / 2.0;

            if (Math.Abs(max - min) < 0.0001)
            {
                h = 0;
                s = 0;
                return;
            }

            double d = max - min;
            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);

            if (Math.Abs(max - r) < 0.0001)
                h = (g - b) / d + (g < b ? 6.0 : 0.0);
            else if (Math.Abs(max - g) < 0.0001)
                h = (b - r) / d + 2.0;
            else
                h = (r - g) / d + 4.0;

            h /= 6.0;
        }

        public static Color HslToRgb(double h, double s, double l)
        {
            double r, g, b;
            if (Math.Abs(s) < 0.0001)
            {
                r = g = b = l;
            }
            else
            {
                double q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
                double p = 2.0 * l - q;
                r = HueToRgb(p, q, h + 1.0 / 3.0);
                g = HueToRgb(p, q, h);
                b = HueToRgb(p, q, h - 1.0 / 3.0);
            }
            return Color.FromRgb(
                (byte)Math.Clamp(Math.Round(r * 255), 0, 255),
                (byte)Math.Clamp(Math.Round(g * 255), 0, 255),
                (byte)Math.Clamp(Math.Round(b * 255), 0, 255)
            );
        }

        private static double HueToRgb(double p, double q, double t)
        {
            if (t < 0.0) t += 1.0;
            if (t > 1.0) t -= 1.0;
            if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
            if (t < 1.0 / 2.0) return q;
            if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
            return p;
        }
    }

    /// <summary>
    /// Kinetic Scene attributes for one lyric line: layout template and camera tilt.
    /// </summary>
    public sealed class KineticScene
    {
        public KineticTemplate Template { get; set; }
        public double TiltDeg { get; set; }
    }

    /// <summary>
    /// Second-order spring physics simulation (Apple-style spring: fast, no wobble).
    /// </summary>
    public struct KineticSpring
    {
        public double Value;
        public double Velocity;
        public double Target;
        public double Stiffness; // e.g. 150
        public double Damping;   // e.g. 24.5

        public static KineticSpring Create(double initial, double stiffness = 150.0, double damping = 24.5)
        {
            return new KineticSpring
            {
                Value = initial,
                Velocity = 0.0,
                Target = initial,
                Stiffness = stiffness,
                Damping = damping
            };
        }

        public void Update(double dt)
        {
            if (dt <= 0.0) return;
            // Cap time step to avoid numerical instability
            dt = Math.Min(dt, 0.033);

            double force = -Stiffness * (Value - Target);
            double dampForce = -Damping * Velocity;
            double acceleration = force + dampForce;

            Velocity += acceleration * dt;
            Value += Velocity * dt;

            // Settle check
            if (Math.Abs(Value - Target) < 0.0005 && Math.Abs(Velocity) < 0.001)
            {
                Value = Target;
                Velocity = 0.0;
            }
        }
    }
}

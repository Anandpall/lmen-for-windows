using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LmenV2.Services
{
    public class ExtractedPalette
    {
        public Color DominantColor { get; set; } = Color.FromRgb(225, 25, 65);
        public Color AccentColor { get; set; } = Color.FromRgb(255, 45, 80);
        public Color SecondaryColor { get; set; } = Color.FromRgb(255, 60, 150);
        public Color AmbientColor { get; set; } = Color.FromRgb(85, 12, 28);
        public Color DarkVignetteColor { get; set; } = Color.FromRgb(14, 5, 8);
    }

    public static class ColorExtractionService
    {
        public static ExtractedPalette ExtractPalette(BitmapSource? bitmap)
        {
            if (bitmap == null)
            {
                return GetDefaultPalette();
            }

            try
            {
                // Resize image to small thumbnail (48x48) for ultra-fast processing
                var smallBmp = new TransformedBitmap(bitmap, new ScaleTransform(
                    48.0 / Math.Max(1, bitmap.PixelWidth),
                    48.0 / Math.Max(1, bitmap.PixelHeight)));

                var formattedBmp = new FormatConvertedBitmap(smallBmp, PixelFormats.Bgra32, null, 0);

                int width = formattedBmp.PixelWidth;
                int height = formattedBmp.PixelHeight;
                int stride = width * 4;
                byte[] pixels = new byte[height * stride];
                formattedBmp.CopyPixels(pixels, stride, 0);

                // Dictionaries to track pixel counts: chromatic (colored) vs all pixels
                var chromaticColorCounts = new Dictionary<int, int>();
                var allColorCounts = new Dictionary<int, int>();

                for (int i = 0; i < pixels.Length; i += 4)
                {
                    byte b = pixels[i];
                    byte g = pixels[i + 1];
                    byte r = pixels[i + 2];
                    byte a = pixels[i + 3];

                    if (a < 128) continue;

                    // Quantize to 5 bits per channel (32 levels) to group similar color shades
                    int qr = (r >> 3) << 3;
                    int qg = (g >> 3) << 3;
                    int qb = (b >> 3) << 3;
                    int key = (qr << 16) | (qg << 8) | qb;

                    allColorCounts[key] = allColorCounts.GetValueOrDefault(key) + 1;

                    RgbToHsv((byte)qr, (byte)qg, (byte)qb, out double h, out double s, out double v);

                    // Chromatic filter: exclude pure blacks (letterbox) and pure whites/grays
                    // Must have meaningful saturation (s >= 0.18) and brightness (v >= 0.15)
                    if (s >= 0.18 && v >= 0.15 && !(s < 0.12 && v > 0.90))
                    {
                        chromaticColorCounts[key] = chromaticColorCounts.GetValueOrDefault(key) + 1;
                    }
                }

                int dominantKey;
                // If there are chromatic colors, pick the absolute most frequent (dominant) one
                if (chromaticColorCounts.Count > 0)
                {
                    dominantKey = chromaticColorCounts.OrderByDescending(kv => kv.Value).First().Key;
                }
                else if (allColorCounts.Count > 0)
                {
                    // Fallback for black & white / grayscale artwork
                    dominantKey = allColorCounts.OrderByDescending(kv => kv.Value).First().Key;
                }
                else
                {
                    return GetDefaultPalette();
                }

                byte dr = (byte)((dominantKey >> 16) & 0xFF);
                byte dg = (byte)((dominantKey >> 8) & 0xFF);
                byte db = (byte)(dominantKey & 0xFF);

                RgbToHsv(dr, dg, db, out double dh, out double ds, out double dv);

                // Enhance vibrance for luminous ambient display:
                // Ensure dominant color is sufficiently vibrant and bright to glow nicely
                double glowSat = Math.Max(0.55, Math.Min(1.0, ds * 1.25));
                double glowVal = Math.Max(0.70, Math.Min(1.0, dv * 1.20));
                HsvToRgb(dh, glowSat, glowVal, out byte gr, out byte gg, out byte gb);
                Color dominantGlowColor = Color.FromRgb(gr, gg, gb);

                // Accent color: vivid punch
                double accSat = Math.Max(0.75, Math.Min(1.0, ds * 1.35));
                double accVal = Math.Max(0.85, Math.Min(1.0, dv * 1.30));
                HsvToRgb(dh, accSat, accVal, out byte ar, out byte ag, out byte ab);
                Color accentGlowColor = Color.FromRgb(ar, ag, ab);

                // Ambient backdrop tone
                HsvToRgb(dh, Math.Min(1.0, glowSat * 1.1), 0.35, out byte amr, out byte amg, out byte amb);
                Color ambientColor = Color.FromRgb(amr, amg, amb);

                // Dark vignette screen tint
                HsvToRgb(dh, 0.45, 0.07, out byte dkr, out byte dkg, out byte dkb);
                Color darkVignette = Color.FromRgb(dkr, dkg, dkb);

                // Secondary artwork hue: a second, visibly distinct chromatic color from the
                // cover. Used by the kinetic lyric stage so text + sticker slabs can pull two
                // real album tones (e.g. coral text on magenta stickers) instead of one hue.
                Color secondaryColor;
                Color? secondaryCandidate = null;
                foreach (var kv in chromaticColorCounts.OrderByDescending(kv => kv.Value))
                {
                    int key = kv.Key;
                    if (key == dominantKey) continue;
                    byte cr = (byte)((key >> 16) & 0xFF);
                    byte cg = (byte)((key >> 8) & 0xFF);
                    byte cb = (byte)(key & 0xFF);

                    RgbToHsv(cr, cg, cb, out double ch, out double cs, out double cv);
                    double hueDelta = Math.Abs(ch - dh);
                    if (hueDelta > 180.0) hueDelta = 360.0 - hueDelta;

                    if (hueDelta >= 24.0 && cs >= 0.25 && cv >= 0.22)
                    {
                        secondaryCandidate = Color.FromRgb(cr, cg, cb);
                        break;
                    }
                }

                if (secondaryCandidate.HasValue)
                {
                    RgbToHsv(secondaryCandidate.Value.R, secondaryCandidate.Value.G, secondaryCandidate.Value.B,
                        out double sh, out double ss, out double sv);
                    double secSat = Math.Max(0.72, Math.Min(1.0, ss * 1.30));
                    double secVal = Math.Max(0.85, Math.Min(1.0, sv * 1.25));
                    HsvToRgb(sh, secSat, secVal, out byte sr, out byte sg, out byte sb);
                    secondaryColor = Color.FromRgb(sr, sg, sb);
                }
                else
                {
                    // Monochrome artwork: rotate the dominant hue into a harmonious second tone
                    HsvToRgb((dh + 40.0) % 360.0, Math.Max(0.80, glowSat), Math.Max(0.90, glowVal),
                        out byte sr, out byte sg, out byte sb);
                    secondaryColor = Color.FromRgb(sr, sg, sb);
                }

                return new ExtractedPalette
                {
                    DominantColor = dominantGlowColor,
                    AccentColor = accentGlowColor,
                    SecondaryColor = secondaryColor,
                    AmbientColor = ambientColor,
                    DarkVignetteColor = darkVignette
                };
            }
            catch
            {
                return GetDefaultPalette();
            }
        }

        public static ExtractedPalette GetDefaultPalette()
        {
            return new ExtractedPalette
            {
                DominantColor = Color.FromRgb(235, 25, 60),      // Vibrant Red / Crimson
                AccentColor = Color.FromRgb(255, 45, 80),        // Neon glow
                SecondaryColor = Color.FromRgb(255, 60, 150),    // Album secondary tone
                AmbientColor = Color.FromRgb(85, 12, 28),        // Deep backdrop
                DarkVignetteColor = Color.FromRgb(14, 5, 8)      // Deep vignette
            };
        }

        public static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
        {
            double dr = r / 255.0;
            double dg = g / 255.0;
            double db = b / 255.0;

            double max = Math.Max(dr, Math.Max(dg, db));
            double min = Math.Min(dr, Math.Min(dg, db));
            double delta = max - min;

            v = max;
            s = max == 0 ? 0 : delta / max;

            if (delta == 0)
            {
                h = 0;
            }
            else if (max == dr)
            {
                h = 60 * (((dg - db) / delta) % 6);
                if (h < 0) h += 360;
            }
            else if (max == dg)
            {
                h = 60 * (((db - dr) / delta) + 2);
            }
            else
            {
                h = 60 * (((dr - dg) / delta) + 4);
            }
        }

        public static void HsvToRgb(double h, double s, double v, out byte r, out byte g, out byte b)
        {
            int hi = (int)Math.Floor(h / 60) % 6;
            double f = (h / 60) - Math.Floor(h / 60);

            double p = v * (1 - s);
            double q = v * (1 - f * s);
            double t = v * (1 - (1 - f) * s);

            double dr = 0, dg = 0, db = 0;
            switch (hi)
            {
                case 0: dr = v; dg = t; db = p; break;
                case 1: dr = q; dg = v; db = p; break;
                case 2: dr = p; dg = v; db = t; break;
                case 3: dr = p; dg = q; db = v; break;
                case 4: dr = t; dg = p; db = v; break;
                case 5: dr = v; dg = p; db = q; break;
            }

            r = (byte)Math.Clamp((int)(dr * 255), 0, 255);
            g = (byte)Math.Clamp((int)(dg * 255), 0, 255);
            b = (byte)Math.Clamp((int)(db * 255), 0, 255);
        }
    }
}

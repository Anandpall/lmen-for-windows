using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LumnWindows.Media;

public static class AlbumArtColorExtractor
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static async Task<List<Color>?> ExtractFromUrlAsync(string thumbnailUrl, int retries = 2, int colorCount = 3)
    {
        if (string.IsNullOrWhiteSpace(thumbnailUrl)) return null;

        for (int attempt = 0; attempt <= retries; attempt++)
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8));
                byte[] bytes = await _http.GetByteArrayAsync(thumbnailUrl, cts.Token);
                using var ms = new MemoryStream(bytes);

                var decoder = BitmapDecoder.Create(ms,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);

                if (decoder.Frames.Count == 0) continue;
                var frame = decoder.Frames[0];
                var colors = ExtractColorsInternal(frame, colorCount);

                if (colors != null && colors.Count > 0 && colors.Any(c => c.R + c.G + c.B > 80))
                {
                    return colors;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ColorExtractor] Attempt {attempt} failed: {ex.Message}");
                if (attempt < retries)
                {
                    await Task.Delay(500);
                }
            }
        }

        return null;
    }

    public static async Task<List<Color>> ExtractFromUrl(string thumbnailUrl, int colorCount = 3)
    {
        var result = await ExtractFromUrlAsync(thumbnailUrl, 2, colorCount);
        return result ?? GetDefaultPalette();
    }

    public static List<Color> ExtractDominantColors(BitmapSource bitmap, int colorCount = 3)
    {
        return ExtractColorsInternal(bitmap, colorCount) ?? GetDefaultPalette();
    }

    private static List<Color>? ExtractColorsInternal(BitmapSource bitmap, int colorCount = 3)
    {
        try
        {
            int targetSize = 32;
            var scaled = new TransformedBitmap(bitmap,
                new ScaleTransform((double)targetSize / bitmap.PixelWidth, (double)targetSize / bitmap.PixelHeight));

            var formatConverted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            int w = formatConverted.PixelWidth;
            int h = formatConverted.PixelHeight;
            byte[] pixels = new byte[w * h * 4];
            formatConverted.CopyPixels(pixels, w * 4, 0);

            var colors = new List<(int R, int G, int B)>();
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int b = pixels[i];
                int g = pixels[i + 1];
                int r = pixels[i + 2];
                int max = Math.Max(r, Math.Max(g, b));
                int min = Math.Min(r, Math.Min(g, b));

                // Filter out non-chromatic colors (greys, near-black, near-white)
                if (max - min > 35 && max > 50 && max < 245)
                {
                    colors.Add((r, g, b));
                }
            }

            if (colors.Count == 0) return null;

            int k = Math.Min(colorCount, colors.Count);
            return KMeans(colors, k);
        }
        catch
        {
            return null;
        }
    }

    private static List<Color> KMeans(List<(int R, int G, int B)> pixels, int k)
    {
        var rng = new Random(42);
        var centroids = pixels.OrderBy(_ => rng.Next()).Take(k)
            .Select(p => ((double)p.R, (double)p.G, (double)p.B)).ToList();

        for (int iter = 0; iter < 8; iter++)
        {
            var clusters = Enumerable.Range(0, k)
                .Select(_ => new List<(int R, int G, int B)>()).ToList();

            foreach (var p in pixels)
            {
                int nearest = 0;
                double minDist = double.MaxValue;
                for (int c = 0; c < centroids.Count; c++)
                {
                    double dist = Math.Pow(p.R - centroids[c].Item1, 2)
                                + Math.Pow(p.G - centroids[c].Item2, 2)
                                + Math.Pow(p.B - centroids[c].Item3, 2);
                    if (dist < minDist) { minDist = dist; nearest = c; }
                }
                clusters[nearest].Add(p);
            }

            for (int c = 0; c < centroids.Count; c++)
            {
                if (clusters[c].Count > 0)
                {
                    centroids[c] = (
                        clusters[c].Average(p => p.R),
                        clusters[c].Average(p => p.G),
                        clusters[c].Average(p => p.B));
                }
            }
        }

        // Sort by saturation (most vivid first)
        return centroids
            .Select(c => Color.FromRgb(
                (byte)Math.Clamp((int)c.Item1, 0, 255),
                (byte)Math.Clamp((int)c.Item2, 0, 255),
                (byte)Math.Clamp((int)c.Item3, 0, 255)))
            .OrderByDescending(c =>
            {
                double max = Math.Max(c.R, Math.Max(c.G, c.B)) / 255.0;
                double min = Math.Min(c.R, Math.Min(c.G, c.B)) / 255.0;
                return max == 0 ? 0 : (max - min) / max;
            })
            .ToList();
    }

    public static Color ShiftWarmth(Color c, float amount)
    {
        return Color.FromRgb(
            (byte)Math.Min(255, c.R + (int)(amount * 40)),
            c.G,
            (byte)Math.Max(0, c.B - (int)(amount * 20)));
    }

    public static List<Color> BlendPalettes(List<Color> a, List<Color> b, float t)
    {
        if (b == null || b.Count == 0) return a;
        if (a == null || a.Count == 0) return b;

        int count = Math.Max(a.Count, b.Count);
        var result = new List<Color>();
        for (int i = 0; i < count; i++)
        {
            var ca = a[i % a.Count];
            var cb = b[i % b.Count];
            result.Add(Lerp(ca, cb, t));
        }
        return result;
    }

    public static Color Lerp(Color from, Color to, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        byte a = (byte)(from.A + (to.A - from.A) * t);
        byte r = (byte)(from.R + (to.R - from.R) * t);
        byte g = (byte)(from.G + (to.G - from.G) * t);
        byte b = (byte)(from.B + (to.B - from.B) * t);
        return Color.FromArgb(a, r, g, b);
    }

    public static List<Color> GetDefaultPalette()
    {
        return new List<Color>
        {
            Color.FromRgb(0, 200, 255),   // Cyan
            Color.FromRgb(180, 0, 255),   // Purple
            Color.FromRgb(255, 0, 120)    // Pink
        };
    }
}

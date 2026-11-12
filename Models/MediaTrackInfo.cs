using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LmenV2.Models
{
    public class MediaTrackInfo
    {
        public string Title { get; set; } = "No Track Playing";
        public string Artist { get; set; } = "Waiting for music...";
        public string AlbumTitle { get; set; } = string.Empty;
        public BitmapSource? Thumbnail { get; set; }
        public bool IsPlaying { get; set; } = false;
        public bool CanPlay { get; set; } = true;
        public bool CanPause { get; set; } = true;
        public bool CanSkipNext { get; set; } = true;
        public bool CanSkipPrevious { get; set; } = true;

        public System.TimeSpan Position { get; set; } = System.TimeSpan.Zero;
        public System.TimeSpan Duration { get; set; } = System.TimeSpan.Zero;

        public Color DominantColor { get; set; } = Color.FromRgb(220, 20, 60);      // Crimson / Ruby default
        public Color AccentColor { get; set; } = Color.FromRgb(255, 50, 80);        // Vivid Neon
        public Color SecondaryColor { get; set; } = Color.FromRgb(255, 60, 150);    // Second artwork tone
        public Color AmbientColor { get; set; } = Color.FromRgb(80, 10, 25);        // Deep aura
        public Color DarkVignetteColor { get; set; } = Color.FromRgb(15, 5, 8);     // Screen background
    }
}

namespace LumnWindows.Settings;

public class AppSettings
{
    // Glow / overlay geometry
    public double GlowDepth { get; set; } = 120.0;           // px inward bleed (80 - 240)
    public double Thickness { get; set; } = 28.0;            // visual base thickness (2 - 80)
    public double GlowAmount { get; set; } = 2.0;            // bloom spread multiplier (1.0 - 4.0)
    public double MasterOpacity { get; set; } = 0.85;        // 0.0 - 1.0
    public double GlowBrightness { get; set; } = 80.0;       // 0 - 100

    // Backward compatibility aliases
    public double EdgeThickness
    {
        get => Thickness;
        set => Thickness = value;
    }
    public double AuraDiffusion
    {
        get => GlowAmount;
        set => GlowAmount = value;
    }

    // Revolution
    public bool RevolveEnabled { get; set; } = true;
    public double PerimeterRotationSpeed { get; set; } = 1.0; // speed multiplier (0.1 - 4.0x)

    // Animation & Beat Sync
    public string AnimationMode { get; set; } = "Music Sync"; // "Music Sync" | "Static" | "Breathing" | "Off"
    public bool BeatSyncEnabled
    {
        get => AnimationMode == "Music Sync";
        set => AnimationMode = value ? "Music Sync" : "Static";
    }
    public double BeatSensitivity { get; set; } = 1.0;        // 0.5 - 3.0x
    public double DecaySpeed { get; set; } = 0.5;

    // Color & Palette
    public string GradientMode { get; set; } = "2 Colors";    // "2 Colors" | "3 Colors" | "Rainbow"
    public string ColorMode
    {
        get => OverrideAlbumColor ? "Static" : (GradientMode == "Rainbow" ? "Rainbow" : "AlbumArt");
        set
        {
            if (value == "Rainbow") GradientMode = "Rainbow";
            else if (value == "Static") OverrideAlbumColor = true;
            else OverrideAlbumColor = false;
        }
    }
    public bool OverrideAlbumColor { get; set; } = false;
    public string PrimaryColorHex { get; set; } = "#E53935";
    public string SecondaryColorHex { get; set; } = "#1E88E5";
    public double PrimaryBrightness { get; set; } = 80.0;     // 0 - 100
    public double SecondaryBrightness { get; set; } = 80.0;   // 0 - 100
    public int PrimaryPercent { get; set; } = 60;
    public int SecondaryPercent { get; set; } = 40;
    public string StaticColorHex
    {
        get => PrimaryColorHex;
        set => PrimaryColorHex = value;
    }

    // Idle
    public bool IdleGlow { get; set; } = true;
    public string IdleColorHex { get; set; } = "#FF4081";

    // Multi-monitor
    public string SelectedDisplay { get; set; } = "All Displays";

    // System & Shell
    public bool ShowInTaskbar { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    public bool LockScreenPlayer { get; set; } = false;
    public bool IsEnabled { get; set; } = true;

    // Spotify Integration
    public string SpotifyClientId { get; set; } = "";
}

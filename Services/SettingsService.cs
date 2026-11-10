using System;
using System.IO;
using System.Text.Json;

namespace LmenV2.Services
{
    public class SettingsModel
    {
        public double GlowBrightness { get; set; } = 0.85;      // 0.2 to 1.5
        public double GlowSpread { get; set; } = 125.0;          // 50 to 260 px
        public double RotationSpeed { get; set; } = 1.0;        // 0.0 (off) to 2.5 (fast)
        public double AudioReactivity { get; set; } = 1.0;      // 0.0 (off) to 2.0 (hyper)
        public double BreathingAmount { get; set; } = 0.7;      // 0.0 (steady) to 1.0 (deep)
        public double BackdropBrightness { get; set; } = 0.70;  // 0.0 (off) to 1.0 (vivid)
        public bool EnableLyricsBeatEffects { get; set; } = true;
        public string PaxSenixApiKey { get; set; } = string.Empty;
        public int LyricsOffsetMs { get; set; } = 0;             // manual lyric sync offset
        public string LyricsTranslationLanguage { get; set; } = "en";
        public bool EnableMotionArtwork { get; set; } = true;
        public bool PrioritizeSpotifyCanvas { get; set; } = false;
        public string SpotifySpDcCookie { get; set; } = string.Empty;
    }

    public class SettingsService
    {
        private static SettingsService? _instance;
        public static SettingsService Instance => _instance ??= new SettingsService();

        private readonly string _settingsFilePath;
        private SettingsModel _model;

        public event Action? SettingsChanged;

        private SettingsService()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string folder = Path.Combine(appData, "LmenV2");
            Directory.CreateDirectory(folder);
            _settingsFilePath = Path.Combine(folder, "settings.json");

            _model = LoadSettings();
        }

        public double BackdropBrightness
        {
            get => _model.BackdropBrightness;
            set
            {
                _model.BackdropBrightness = Math.Clamp(value, 0.0, 1.0);
                NotifyAndSave();
            }
        }

        public double GlowBrightness
        {
            get => _model.GlowBrightness;
            set
            {
                _model.GlowBrightness = Math.Clamp(value, 0.1, 1.8);
                NotifyAndSave();
            }
        }

        public double GlowSpread
        {
            get => _model.GlowSpread;
            set
            {
                _model.GlowSpread = Math.Clamp(value, 40.0, 300.0);
                NotifyAndSave();
            }
        }

        public double RotationSpeed
        {
            get => _model.RotationSpeed;
            set
            {
                _model.RotationSpeed = Math.Clamp(value, 0.0, 3.0);
                NotifyAndSave();
            }
        }

        public double AudioReactivity
        {
            get => _model.AudioReactivity;
            set
            {
                _model.AudioReactivity = Math.Clamp(value, 0.0, 2.5);
                NotifyAndSave();
            }
        }

        public double BreathingAmount
        {
            get => _model.BreathingAmount;
            set
            {
                _model.BreathingAmount = Math.Clamp(value, 0.0, 1.0);
                NotifyAndSave();
            }
        }

        public bool EnableLyricsBeatEffects
        {
            get => _model.EnableLyricsBeatEffects;
            set
            {
                _model.EnableLyricsBeatEffects = value;
                NotifyAndSave();
            }
        }

        public string PaxSenixApiKey
        {
            get => _model.PaxSenixApiKey;
            set
            {
                _model.PaxSenixApiKey = value?.Trim() ?? string.Empty;
                NotifyAndSave();
            }
        }

        public int LyricsOffsetMs
        {
            get => _model.LyricsOffsetMs;
            set
            {
                _model.LyricsOffsetMs = Math.Clamp(value, -10000, 10000);
                NotifyAndSave();
            }
        }

        public string LyricsTranslationLanguage
        {
            get => _model.LyricsTranslationLanguage;
            set
            {
                _model.LyricsTranslationLanguage = string.IsNullOrWhiteSpace(value) ? "en" : value;
                NotifyAndSave();
            }
        }

        public bool EnableMotionArtwork
        {
            get => _model.EnableMotionArtwork;
            set
            {
                _model.EnableMotionArtwork = value;
                NotifyAndSave();
            }
        }

        public bool PrioritizeSpotifyCanvas
        {
            get => _model.PrioritizeSpotifyCanvas;
            set
            {
                _model.PrioritizeSpotifyCanvas = value;
                NotifyAndSave();
            }
        }

        public string SpotifySpDcCookie
        {
            get => _model.SpotifySpDcCookie;
            set
            {
                _model.SpotifySpDcCookie = value?.Trim() ?? string.Empty;
                NotifyAndSave();
            }
        }

        private void NotifyAndSave()
        {
            SettingsChanged?.Invoke();
            SaveSettings();
        }

        private SettingsModel LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    string json = File.ReadAllText(_settingsFilePath);
                    var loaded = JsonSerializer.Deserialize<SettingsModel>(json);
                    if (loaded != null) return loaded;
                }
            }
            catch { }
            return new SettingsModel();
        }

        private void SaveSettings()
        {
            try
            {
                string json = JsonSerializer.Serialize(_model, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsFilePath, json);
            }
            catch { }
        }
    }
}

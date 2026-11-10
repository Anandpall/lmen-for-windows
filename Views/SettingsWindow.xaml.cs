using System;
using System.Windows;
using System.Windows.Controls;
using LmenV2.Services;

namespace LmenV2.Views
{
    public partial class SettingsWindow : Window
    {
        private readonly SettingsService _settings = SettingsService.Instance;

        // True while the sliders are being seeded from saved settings, so the
        // ValueChanged handlers do not echo the seed values back into SettingsService.
        private bool _loading;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        public SettingsWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Topmost = true;
            Activate();
            Focus();
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                SetForegroundWindow(handle);
            }

            // Pushing values into the sliders raises ValueChanged, which would write the
            // value straight back into SettingsService (and trigger a save + a SettingsChanged
            // storm) before the user touched anything. Suppress that while we sync the UI.
            _loading = true;
            try
            {
                BrightnessSlider.Value = _settings.GlowBrightness * 100.0;
                BackdropSlider.Value = _settings.BackdropBrightness * 100.0;
                SpreadSlider.Value = _settings.GlowSpread;
                SpeedSlider.Value = _settings.RotationSpeed * 100.0;
                ReactivitySlider.Value = _settings.AudioReactivity * 100.0;
                BreathingSlider.Value = _settings.BreathingAmount * 100.0;

                if (SettingsVolumeSlider != null)
                {
                    SettingsVolumeSlider.Value = (int)(SystemVolumeService.Instance.MasterVolume * 100.0f);
                }

                if (LyricsBeatEffectsCheckBox != null)
                {
                    LyricsBeatEffectsCheckBox.IsChecked = _settings.EnableLyricsBeatEffects;
                }

                if (LyricsLanguageComboBox != null)
                {
                    LyricsLanguageComboBox.Items.Clear();
                    ComboBoxItem? selected = null;
                    foreach (var language in LyricsTranslationLanguages.All)
                    {
                        var item = new ComboBoxItem
                        {
                            Content = LyricsTranslationLanguages.DisplayName(language.Code),
                            Tag = language.Code
                        };
                        LyricsLanguageComboBox.Items.Add(item);
                        if (string.Equals(language.Code, _settings.LyricsTranslationLanguage, StringComparison.OrdinalIgnoreCase))
                        {
                            selected = item;
                        }
                    }
                    LyricsLanguageComboBox.SelectedItem = selected ?? (LyricsLanguageComboBox.Items.Count > 0 ? LyricsLanguageComboBox.Items[0] : null);
                }

                if (PaxSenixApiKeyBox != null)
                {
                    PaxSenixApiKeyBox.Text = _settings.PaxSenixApiKey;
                }

                if (EnableMotionArtworkCheckBox != null)
                {
                    EnableMotionArtworkCheckBox.IsChecked = _settings.EnableMotionArtwork;
                }

                if (PrioritizeSpotifyCanvasCheckBox != null)
                {
                    PrioritizeSpotifyCanvasCheckBox.IsChecked = _settings.PrioritizeSpotifyCanvas;
                }

                if (SpotifySpDcBox != null)
                {
                    SpotifySpDcBox.Text = _settings.SpotifySpDcCookie;
                }
            }
            finally
            {
                _loading = false;
            }

            bool isOverlay = Application.Current is App app && app.IsOverlayModeActive;
            FullscreenModeButton.Style = (Style)FindResource(isOverlay ? "SegmentButton" : "SegmentSelectedButton");
            OverlayModeButton.Style = (Style)FindResource(isOverlay ? "SegmentSelectedButton" : "SegmentButton");

            SystemVolumeService.Instance.VolumeChanged += OnSystemVolumeChanged;
            _settings.SettingsChanged += OnSettingsChanged;
            UpdateVolumeLabel(SystemVolumeService.Instance.MasterVolume, SystemVolumeService.Instance.IsMuted);

            UpdateLabels();

            if (ActiveGpuText != null)
            {
                ActiveGpuText.Text = GpuPreferenceService.GetPrimaryGpuName();
            }
        }

        private void UpdateLabels()
        {
            if (BrightnessText != null) BrightnessText.Text = $"{(int)BrightnessSlider.Value}%";
            if (BackdropText != null) BackdropText.Text = $"{(int)BackdropSlider.Value}%";
            if (SpreadText != null) SpreadText.Text = $"{(int)SpreadSlider.Value} px";
            if (SpeedText != null) SpeedText.Text = $"{_settings.RotationSpeed:0.0}x";
            if (ReactivityText != null) ReactivityText.Text = $"{_settings.AudioReactivity:0.0}x";
            if (BreathingText != null) BreathingText.Text = $"{(int)BreathingSlider.Value}%";
        }

        private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loading) return;
            _settings.GlowBrightness = e.NewValue / 100.0;
            if (BrightnessText != null) BrightnessText.Text = $"{(int)e.NewValue}%";
        }

        private void BackdropSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loading) return;
            _settings.BackdropBrightness = e.NewValue / 100.0;
            if (BackdropText != null) BackdropText.Text = $"{(int)e.NewValue}%";
        }

        private void SpreadSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loading) return;
            _settings.GlowSpread = e.NewValue;
            if (SpreadText != null) SpreadText.Text = $"{(int)e.NewValue} px";
        }

        private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loading) return;
            _settings.RotationSpeed = e.NewValue / 100.0;
            if (SpeedText != null) SpeedText.Text = $"{_settings.RotationSpeed:0.0}x";
        }

        private void ReactivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loading) return;
            _settings.AudioReactivity = e.NewValue / 100.0;
            if (ReactivityText != null) ReactivityText.Text = $"{_settings.AudioReactivity:0.0}x";
        }

        private void BreathingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loading) return;
            _settings.BreathingAmount = e.NewValue / 100.0;
            if (BreathingText != null) BreathingText.Text = $"{(int)e.NewValue}%";
        }

        private void FullscreenMode_Click(object sender, RoutedEventArgs e)
        {
            ((App)Application.Current).ShowFullscreenVisualizer();
            Close();
        }

        private void OverlayMode_Click(object sender, RoutedEventArgs e)
        {
            ((App)Application.Current).ShowDesktopOverlay();
            Close();
        }

        private void SettingsVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loading) return;
            var volume = (float)(e.NewValue / 100.0);
            SystemVolumeService.Instance.MasterVolume = volume;
            UpdateVolumeLabel(volume, SystemVolumeService.Instance.IsMuted);
        }

        private void OnSystemVolumeChanged(float volume, bool isMuted)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (_loading) return;
                _loading = true;
                try
                {
                    if (SettingsVolumeSlider != null)
                    {
                        SettingsVolumeSlider.Value = (int)(volume * 100.0f);
                    }
                    UpdateVolumeLabel(volume, isMuted);
                }
                finally
                {
                    _loading = false;
                }
            });
        }

        private void UpdateVolumeLabel(float volume, bool isMuted)
        {
            if (SettingsVolumeText != null)
            {
                SettingsVolumeText.Text = isMuted ? "Muted" : $"{(int)(volume * 100.0f)}%";
            }
        }

        private void LyricsBeatEffects_Checked(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _settings.EnableLyricsBeatEffects = true;
        }

        private void LyricsBeatEffects_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _settings.EnableLyricsBeatEffects = false;
        }

        private void LyricsLanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (LyricsLanguageComboBox.SelectedItem is ComboBoxItem item && item.Tag is string code)
            {
                _settings.LyricsTranslationLanguage = code;
            }
        }

        private void PaxSenixApiKeyBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            _settings.PaxSenixApiKey = PaxSenixApiKeyBox.Text;
        }

        private void EnableMotionArtwork_Checked(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _settings.EnableMotionArtwork = true;
        }

        private void EnableMotionArtwork_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _settings.EnableMotionArtwork = false;
        }

        private void PrioritizeSpotifyCanvas_Checked(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _settings.PrioritizeSpotifyCanvas = true;
        }

        private void PrioritizeSpotifyCanvas_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _settings.PrioritizeSpotifyCanvas = false;
        }

        private void SpotifySpDcBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            _settings.SpotifySpDcCookie = SpotifySpDcBox.Text;
        }

        private void OnSettingsChanged()
        {
            if (_loading) return;
            Dispatcher.InvokeAsync(() =>
            {
                if (_loading) return;
                _loading = true;
                try
                {
                    BrightnessSlider.Value = _settings.GlowBrightness * 100.0;
                    BackdropSlider.Value = _settings.BackdropBrightness * 100.0;
                    SpreadSlider.Value = _settings.GlowSpread;
                    SpeedSlider.Value = _settings.RotationSpeed * 100.0;
                    ReactivitySlider.Value = _settings.AudioReactivity * 100.0;
                    BreathingSlider.Value = _settings.BreathingAmount * 100.0;
                    UpdateLabels();
                }
                finally
                {
                    _loading = false;
                }
            });
        }

        protected override void OnClosed(System.EventArgs e)
        {
            _settings.SettingsChanged -= OnSettingsChanged;
            SystemVolumeService.Instance.VolumeChanged -= OnSystemVolumeChanged;
            base.OnClosed(e);
        }

        private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState != System.Windows.Input.MouseButtonState.Pressed) return;
            try { DragMove(); } catch (InvalidOperationException) { }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OpenWindowsGraphicsSettings_Click(object sender, RoutedEventArgs e)
        {
            GpuPreferenceService.OpenWindowsGraphicsSettings();
        }
    }
}

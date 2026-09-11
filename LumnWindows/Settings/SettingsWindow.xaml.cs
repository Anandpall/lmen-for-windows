using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LumnWindows.Audio;
using LumnWindows.Media;

namespace LumnWindows.Settings;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly SpotifyService? _spotifyService;
    private readonly SpectralAnalyser? _spectralAnalyser;
    private readonly Action? _onQuitApp;
    private bool _isInitializing = true;

    public SettingsWindow(
        SettingsService settingsService,
        SpotifyService? spotifyService = null,
        SpectralAnalyser? spectralAnalyser = null,
        Action? onQuitApp = null)
    {
        _settingsService = settingsService;
        _spotifyService = spotifyService;
        _spectralAnalyser = spectralAnalyser;
        _onQuitApp = onQuitApp;

        InitializeComponent();
        LoadSettingsToUi();

        if (_spectralAnalyser != null)
        {
            _spectralAnalyser.FrameReady += OnSpectralFrameReady;
        }

        if (_spotifyService != null)
        {
            _spotifyService.TrackChanged += OnTrackChanged;
            UpdateCdpStatus(_spotifyService.IsAuthenticated);
        }

        Closed += (s, e) =>
        {
            if (_spectralAnalyser != null)
            {
                _spectralAnalyser.FrameReady -= OnSpectralFrameReady;
            }
            if (_spotifyService != null)
            {
                _spotifyService.TrackChanged -= OnTrackChanged;
            }
        };
    }

    private void LoadSettingsToUi()
    {
        _isInitializing = true;
        var s = _settingsService.Current;

        ChkEnabled.IsChecked = s.IsEnabled;
        SldThickness.Value = s.EdgeThickness;
        SldSpread.Value = s.AuraDiffusion;
        SldOpacity.Value = s.MasterOpacity * 100.0;

        ChkRevolve.IsChecked = s.RevolveEnabled;
        SldRotationSpeed.Value = s.PerimeterRotationSpeed;

        ChkBeatSync.IsChecked = s.BeatSyncEnabled;
        SldBeatSensitivity.Value = s.BeatSensitivity;
        SldDecaySpeed.Value = s.DecaySpeed;

        ChkStartWithWindows.IsChecked = s.StartWithWindows;
        TxtStaticHex.Text = s.StaticColorHex;

        foreach (ComboBoxItem item in CmbColorMode.Items)
        {
            if ((string?)item.Tag == s.ColorMode)
            {
                CmbColorMode.SelectedItem = item;
                break;
            }
        }

        UpdateLabels();
        _isInitializing = false;
    }

    private void UpdateLabels()
    {
        if (LblThickness != null) LblThickness.Text = $"{(int)SldThickness.Value} px";
        if (LblSpread != null) LblSpread.Text = $"{SldSpread.Value:F1}x";
        if (LblOpacity != null) LblOpacity.Text = $"{(int)SldOpacity.Value}%";
        if (LblRotationSpeed != null) LblRotationSpeed.Text = $"{SldRotationSpeed.Value:F1}x";
        if (LblBeatSensitivity != null) LblBeatSensitivity.Text = $"{SldBeatSensitivity.Value:F1}x";
        if (LblDecaySpeed != null) LblDecaySpeed.Text = $"{SldDecaySpeed.Value:F2}x";
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing) return;
        UpdateLabels();
        ApplyLiveChanges();
    }

    private void OnLiveSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        ApplyLiveChanges();
    }

    private void OnColorModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PnlStaticColor == null) return;
        var selectedItem = CmbColorMode.SelectedItem as ComboBoxItem;
        string mode = (string?)selectedItem?.Tag ?? "AlbumArt";
        PnlStaticColor.Visibility = mode == "Static" ? Visibility.Visible : Visibility.Collapsed;

        if (!_isInitializing) ApplyLiveChanges();
    }

    private void OnStaticHexChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isInitializing) ApplyLiveChanges();
    }

    private void ApplyLiveChanges()
    {
        var s = _settingsService.Current;
        s.IsEnabled = ChkEnabled.IsChecked ?? true;
        s.EdgeThickness = SldThickness.Value;
        s.AuraDiffusion = SldSpread.Value;
        s.MasterOpacity = SldOpacity.Value / 100.0;

        s.RevolveEnabled = ChkRevolve.IsChecked ?? true;
        s.PerimeterRotationSpeed = SldRotationSpeed.Value;

        s.BeatSyncEnabled = ChkBeatSync.IsChecked ?? true;
        s.BeatSensitivity = SldBeatSensitivity.Value;
        s.DecaySpeed = SldDecaySpeed.Value;

        if (_spectralAnalyser != null)
        {
            _spectralAnalyser.Sensitivity = s.BeatSensitivity;
            _spectralAnalyser.ReleaseCoeff = (float)(0.15f * s.DecaySpeed);
        }

        s.StartWithWindows = ChkStartWithWindows.IsChecked ?? false;

        var selectedItem = CmbColorMode.SelectedItem as ComboBoxItem;
        s.ColorMode = (string?)selectedItem?.Tag ?? "AlbumArt";
        s.StaticColorHex = TxtStaticHex.Text.Trim();

        _settingsService.Save(s);
    }

    private void OnSpectralFrameReady(SpectralFrame frame)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (BarSubBass == null) return;
            BarSubBass.Height  = 4 + Math.Clamp(frame.SubBass  * 44, 0, 44);
            BarBass.Height     = 4 + Math.Clamp(frame.Bass     * 44, 0, 44);
            BarLowMid.Height   = 4 + Math.Clamp(frame.LowMid   * 44, 0, 44);
            BarMid.Height      = 4 + Math.Clamp(frame.Mid      * 44, 0, 44);
            BarUpperMid.Height = 4 + Math.Clamp(frame.UpperMid * 44, 0, 44);
            BarAir.Height      = 4 + Math.Clamp(frame.Air      * 44, 0, 44);

            if (BeatDot != null)
            {
                BeatDot.Opacity = 0.25 + frame.BeatOnset * 0.75;
            }
        }, DispatcherPriority.Render);
    }

    private void OnTrackChanged(SpotifyTrackInfo track)
    {
        Dispatcher.Invoke(() => UpdateTrackUi(track));
    }

    private void OnCdpConnectionChanged(bool connected)
    {
        Dispatcher.Invoke(() => UpdateCdpStatus(connected));
    }

    private async void UpdateTrackUi(SpotifyTrackInfo track)
    {
        if (!string.IsNullOrWhiteSpace(track.Title))
        {
            TxtTrackTitle.Text = track.Title;
            TxtTrackArtist.Text = string.IsNullOrWhiteSpace(track.Artist) ? "Spotify" : track.Artist;

            if (!string.IsNullOrWhiteSpace(track.AlbumArtUrl))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(track.AlbumArtUrl);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    AlbumArtImage.Source = bmp;

                    var palette = await AlbumArtColorExtractor.ExtractFromUrl(track.AlbumArtUrl);
                    UpdatePaletteDots(palette);
                }
                catch { }
            }
        }
        else
        {
            TxtTrackTitle.Text = "Waiting for Spotify...";
            TxtTrackArtist.Text = "Start playback in Spotify Desktop";
            AlbumArtImage.Source = null;
            UpdatePaletteDots(AlbumArtColorExtractor.GetDefaultPalette());
        }
    }

    private void UpdatePaletteDots(List<Color> palette)
    {
        if (DotPalette1 != null && palette.Count > 0)
        {
            DotPalette1.Fill = new SolidColorBrush(palette[0]);
            DotPalette1.ToolTip = $"Primary: #{palette[0].R:X2}{palette[0].G:X2}{palette[0].B:X2}";
        }
        if (DotPalette2 != null && palette.Count > 1)
        {
            DotPalette2.Fill = new SolidColorBrush(palette[1]);
            DotPalette2.ToolTip = $"Secondary: #{palette[1].R:X2}{palette[1].G:X2}{palette[1].B:X2}";
        }
        if (DotPalette3 != null && palette.Count > 2)
        {
            DotPalette3.Fill = new SolidColorBrush(palette[2]);
            DotPalette3.ToolTip = $"Accent: #{palette[2].R:X2}{palette[2].G:X2}{palette[2].B:X2}";
        }
    }

    private void UpdateCdpStatus(bool connected)
    {
        if (TxtCdpStatus == null || DotCdpStatus == null) return;
        if (connected)
        {
            TxtCdpStatus.Text = "Chrome CDP: Connected";
            TxtCdpStatus.Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 136));
            DotCdpStatus.Fill = new SolidColorBrush(Color.FromRgb(0, 255, 136));
        }
        else
        {
            TxtCdpStatus.Text = "Chrome CDP: Disconnected";
            TxtCdpStatus.Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 153));
            DotCdpStatus.Fill = new SolidColorBrush(Color.FromRgb(136, 136, 153));
        }
    }

    private void OnLaunchChromeClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "spotify:",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        ApplyLiveChanges();
        MessageBox.Show(this, "Settings saved successfully!", "Lumn Control Center", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnResetClicked(object sender, RoutedEventArgs e)
    {
        _settingsService.Save(new AppSettings());
        LoadSettingsToUi();
    }

    private void OnQuitClicked(object sender, RoutedEventArgs e)
    {
        var res = MessageBox.Show(this,
            "Are you sure you want to completely exit Lumn and turn off edge lighting?",
            "Quit LumnWindows",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (res == MessageBoxResult.Yes)
        {
            if (_onQuitApp != null)
            {
                _onQuitApp();
            }
            else
            {
                Application.Current.Shutdown();
            }
        }
    }
}

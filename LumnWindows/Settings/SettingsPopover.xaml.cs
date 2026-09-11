using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace LumnWindows.Settings;

public partial class SettingsPopover : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    public event Action<AppSettings>? SettingsChanged;
    private bool _isInitializing = true;
    private readonly DispatcherTimer _saveDebounceTimer;

    // Vibrant Lumn preset colors for quick cycling / selection
    private static readonly string[] PresetColors = new[]
    {
        "#E53935", // Crimson
        "#D81B60", // Rose
        "#8E24AA", // Purple
        "#3949AB", // Indigo
        "#1E88E5", // Blue
        "#00ACC1", // Cyan
        "#00897B", // Teal
        "#43A047", // Green
        "#FB8C00", // Orange
        "#FF5722", // Deep Orange
    };
    private int _primaryColorIndex = 0;
    private int _secondaryColorIndex = 4;

    public SettingsPopover(AppSettings settings, SettingsService settingsService)
    {
        _settings = settings;
        _settingsService = settingsService;
        InitializeComponent();

        _saveDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _saveDebounceTimer.Tick += (s, e) =>
        {
            _saveDebounceTimer.Stop();
            _settingsService.Save(_settings);
        };

        LoadSettingsToUi();
        PopulateDisplays();
        _isInitializing = false;

        Deactivated += (s, e) =>
        {
            Hide();
        };
    }

    private void LoadSettingsToUi()
    {
        // Gradient
        foreach (ComboBoxItem item in GradientCombo.Items)
        {
            if (item.Content?.ToString() == _settings.GradientMode)
            {
                GradientCombo.SelectedItem = item;
                break;
            }
        }
        if (GradientCombo.SelectedItem == null && GradientCombo.Items.Count > 0)
            GradientCombo.SelectedIndex = 0;

        // Animation
        foreach (ComboBoxItem item in AnimationCombo.Items)
        {
            if (item.Content?.ToString() == _settings.AnimationMode)
            {
                AnimationCombo.SelectedItem = item;
                break;
            }
        }
        if (AnimationCombo.SelectedItem == null && AnimationCombo.Items.Count > 0)
            AnimationCombo.SelectedIndex = 0;

        // Sliders
        ThicknessSlider.Value = _settings.Thickness;
        ThicknessText.Text = $"{Math.Round(_settings.Thickness)} px";

        GlowSlider.Value = _settings.GlowAmount;
        GlowText.Text = $"{_settings.GlowAmount:0.0}x";

        RevolveToggle.IsChecked = _settings.RevolveEnabled;
        RotationSpeedSlider.Value = _settings.PerimeterRotationSpeed;
        RotationSpeedText.Text = $"{_settings.PerimeterRotationSpeed:0.0}x";

        // Override album color
        OverrideToggle.IsChecked = _settings.OverrideAlbumColor;
        ColorPickersContainer.Visibility = _settings.OverrideAlbumColor ? Visibility.Visible : Visibility.Collapsed;

        PrimaryLabel.Text = $"Primary {_settings.PrimaryPercent}%";
        SecondaryLabel.Text = $"Secondary {_settings.SecondaryPercent}%";

        UpdateSwatch(PrimarySwatch, _settings.PrimaryColorHex);
        UpdateSwatch(SecondarySwatch, _settings.SecondaryColorHex);

        PrimaryBrightnessSlider.Value = _settings.PrimaryBrightness;
        SecondaryBrightnessSlider.Value = _settings.SecondaryBrightness;

        // Toggles
        TaskbarToggle.IsChecked = _settings.ShowInTaskbar;
        StartWithWindowsToggle.IsChecked = _settings.StartWithWindows;
    }

    private void PopulateDisplays()
    {
        DisplayCombo.Items.Clear();
        DisplayCombo.Items.Add("All Displays");
        DisplayCombo.Items.Add("Display 1 (Primary)");

        if (SystemParameters.VirtualScreenWidth > SystemParameters.PrimaryScreenWidth + 50)
        {
            DisplayCombo.Items.Add("Display 2 (Secondary)");
        }

        int selectedIdx = 0;
        for (int i = 0; i < DisplayCombo.Items.Count; i++)
        {
            if (DisplayCombo.Items[i]?.ToString() == _settings.SelectedDisplay)
            {
                selectedIdx = i;
                break;
            }
        }
        DisplayCombo.SelectedIndex = selectedIdx;
    }

    public void ShowNear(Point anchorBottomCenter)
    {
        double targetLeft = anchorBottomCenter.X - (Width / 2);
        double targetTop = anchorBottomCenter.Y - Height - 12;

        if (targetTop < 20)
        {
            targetTop = anchorBottomCenter.Y + 20;
        }

        targetLeft = Math.Max(10, Math.Min(targetLeft, SystemParameters.VirtualScreenWidth - Width - 20));
        targetTop = Math.Max(10, Math.Min(targetTop, SystemParameters.VirtualScreenHeight - Height - 20));

        Left = targetLeft;
        Top = targetTop;

        Show();
        Activate();
    }

    private void NotifyChanged(bool saveImmediately = false)
    {
        if (_isInitializing) return;
        SettingsChanged?.Invoke(_settings);

        if (saveImmediately)
        {
            _saveDebounceTimer.Stop();
            _settingsService.Save(_settings);
        }
        else
        {
            _saveDebounceTimer.Stop();
            _saveDebounceTimer.Start();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    private void GradientCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GradientCombo.SelectedItem is ComboBoxItem item)
        {
            _settings.GradientMode = item.Content?.ToString() ?? "2 Colors";
            NotifyChanged(true);
        }
    }

    private void AnimationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AnimationCombo.SelectedItem is ComboBoxItem item)
        {
            _settings.AnimationMode = item.Content?.ToString() ?? "Music Sync";
            NotifyChanged(true);
        }
    }

    private void ThicknessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ThicknessText != null)
        {
            ThicknessText.Text = $"{Math.Round(e.NewValue)} px";
            _settings.Thickness = e.NewValue;
            NotifyChanged(false);
        }
    }

    private void GlowSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GlowText != null)
        {
            GlowText.Text = $"{e.NewValue:0.0}x";
            _settings.GlowAmount = e.NewValue;
            NotifyChanged(false);
        }
    }

    private void RevolveToggle_Changed(object sender, RoutedEventArgs e)
    {
        _settings.RevolveEnabled = RevolveToggle.IsChecked == true;
        NotifyChanged(true);
    }

    private void RotationSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RotationSpeedText != null)
        {
            RotationSpeedText.Text = $"{e.NewValue:0.0}x";
            _settings.PerimeterRotationSpeed = e.NewValue;
            NotifyChanged(false);
        }
    }

    private void OverrideToggle_Changed(object sender, RoutedEventArgs e)
    {
        bool isOverride = OverrideToggle.IsChecked == true;
        _settings.OverrideAlbumColor = isOverride;
        if (ColorPickersContainer != null)
        {
            ColorPickersContainer.Visibility = isOverride ? Visibility.Visible : Visibility.Collapsed;
        }
        NotifyChanged(true);
    }

    private void PrimarySwatch_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _primaryColorIndex = (_primaryColorIndex + 1) % PresetColors.Length;
        string hex = PresetColors[_primaryColorIndex];
        _settings.PrimaryColorHex = hex;
        UpdateSwatch(PrimarySwatch, hex);
        NotifyChanged(true);
    }

    private void SecondarySwatch_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _secondaryColorIndex = (_secondaryColorIndex + 1) % PresetColors.Length;
        string hex = PresetColors[_secondaryColorIndex];
        _settings.SecondaryColorHex = hex;
        UpdateSwatch(SecondarySwatch, hex);
        NotifyChanged(true);
    }

    private static void UpdateSwatch(Border swatch, string hex)
    {
        try
        {
            var brush = new BrushConverter().ConvertFromString(hex) as Brush;
            if (brush != null) swatch.Background = brush;
        }
        catch { }
    }

    private void PrimaryBrightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _settings.PrimaryBrightness = e.NewValue;
        NotifyChanged(false);
    }

    private void SecondaryBrightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _settings.SecondaryBrightness = e.NewValue;
        NotifyChanged(false);
    }

    private void DisplayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DisplayCombo.SelectedItem != null)
        {
            _settings.SelectedDisplay = DisplayCombo.SelectedItem.ToString() ?? "All Displays";
            NotifyChanged(true);
        }
    }

    private void TaskbarToggle_Changed(object sender, RoutedEventArgs e)
    {
        _settings.ShowInTaskbar = TaskbarToggle.IsChecked == true;
        NotifyChanged(true);
    }

    private void StartWithWindowsToggle_Changed(object sender, RoutedEventArgs e)
    {
        _settings.StartWithWindows = StartWithWindowsToggle.IsChecked == true;
        NotifyChanged(true);
    }

    private void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("Lumn is up to date (Version 1.0.0).", "Lumn", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }
}

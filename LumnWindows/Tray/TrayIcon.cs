using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;

namespace LumnWindows.Tray;

public class TrayIconManager : IDisposable
{
    private TaskbarIcon? _taskbarIcon;
    private readonly Action _onOpenPlayer;
    private readonly Action _onOpenSettings;
    private readonly Action _onToggleGlow;
    private readonly Action _onExit;

    public TrayIconManager(Action onOpenPlayer, Action onOpenSettings, Action onToggleGlow, Action onExit)
    {
        _onOpenPlayer = onOpenPlayer;
        _onOpenSettings = onOpenSettings;
        _onToggleGlow = onToggleGlow;
        _onExit = onExit;

        Initialize();
    }

    private void Initialize()
    {
        try
        {
            _taskbarIcon = new TaskbarIcon();

            var iconUri = new Uri("pack://application:,,,/Resources/TrayIcon.ico", UriKind.Absolute);
            try
            {
                _taskbarIcon.IconSource = new BitmapImage(iconUri);
            }
            catch
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "TrayIcon.ico");
                if (File.Exists(iconPath))
                {
                    _taskbarIcon.IconSource = new BitmapImage(new Uri(iconPath));
                }
            }

            _taskbarIcon.ToolTipText = "Lumn — Ambient Music Lighting";
            _taskbarIcon.TrayMouseDoubleClick += (s, e) => _onOpenPlayer();

            var menu = new ContextMenu();

            var openPlayerItem = new MenuItem
            {
                Header = "Open Lumn Player",
                FontWeight = FontWeights.Bold
            };
            openPlayerItem.Click += (s, e) => _onOpenPlayer();
            menu.Items.Add(openPlayerItem);

            menu.Items.Add(new Separator());

            var settingsItem = new MenuItem { Header = "Settings..." };
            settingsItem.Click += (s, e) => _onOpenSettings();
            menu.Items.Add(settingsItem);

            var toggleItem = new MenuItem { Header = "Toggle Edge Lighting" };
            toggleItem.Click += (s, e) => _onToggleGlow();
            menu.Items.Add(toggleItem);

            menu.Items.Add(new Separator());

            var exitItem = new MenuItem { Header = "Quit Lumn" };
            exitItem.Click += (s, e) => _onExit();
            menu.Items.Add(exitItem);

            _taskbarIcon.ContextMenu = menu;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to init TaskbarIcon: {ex.Message}");
        }
    }

    public void UpdateToolTip(string trackTitle, string artist)
    {
        if (_taskbarIcon == null) return;

        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (!string.IsNullOrWhiteSpace(trackTitle))
            {
                string text = $"Lumn: {trackTitle} - {artist}";
                if (text.Length > 63) text = text.Substring(0, 60) + "...";
                _taskbarIcon.ToolTipText = text;
            }
            else
            {
                _taskbarIcon.ToolTipText = "Lumn — Ambient Lighting";
            }
        });
    }

    public void Dispose()
    {
        _taskbarIcon?.Dispose();
        _taskbarIcon = null;
    }
}

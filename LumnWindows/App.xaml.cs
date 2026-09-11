using System;
using System.IO;
using System.Threading;
using System.Windows;
using LumnWindows.Audio;
using LumnWindows.Media;
using LumnWindows.Overlay;
using LumnWindows.Player;
using LumnWindows.Settings;
using LumnWindows.Tray;

namespace LumnWindows;

public partial class App : Application
{
    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LumnWindows", "debug.log");

    private const string AppGuid = "LumnWindows_SingleInstance_Mutex_987234";
    private static Mutex? _instanceMutex;
    private static EventWaitHandle? _showGuiEvent;

    private SettingsService? _settingsService;
    private AudioCaptureService? _audio;
    private SpectralAnalyser? _spectralAnalyser;
    private NowPlayingService? _nowPlaying;
    private OverlayWindow? _overlay;
    private PlayerWindow? _playerWindow;
    private SettingsPopover? _settingsPopover;
    private TrayIconManager? _tray;

    public static void Log(string message)
    {
        try
        {
            string dir = Path.GetDirectoryName(LogFile)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, AppGuid, out bool isNewInstance);
        _showGuiEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "LumnWindows_ShowGui_Signal");

        if (!isNewInstance)
        {
            _showGuiEvent.Set();
            Shutdown();
            return;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            while (true)
            {
                _showGuiEvent.WaitOne();
                Dispatcher.Invoke(OnOpenPlayer);
            }
        });

        Log("App OnStartup started (v3 - Lumn Windows Media Edition)");

        AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
        {
            Log($"UnhandledException: {ev.ExceptionObject}");
        };
        DispatcherUnhandledException += (s, ev) =>
        {
            Log($"DispatcherUnhandledException: {ev.Exception}");
            ev.Handled = true;
        };

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            Log("Loading settings...");
            _settingsService = new SettingsService();
            var currentSettings = _settingsService.Current;

            Log("Initializing audio services (low-latency WASAPI loopback)...");
            _audio = new AudioCaptureService();
            _spectralAnalyser = new SpectralAnalyser
            {
                Sensitivity = currentSettings.BeatSensitivity,
                ReleaseCoeff = (float)(0.15f * currentSettings.DecaySpeed)
            };

            Log("Initializing Windows Media SMTC services...");
            _nowPlaying = new NowPlayingService();

            Log("Creating Settings Popover...");
            _settingsPopover = new SettingsPopover(currentSettings, _settingsService);

            Log("Creating OverlayWindow...");
            _overlay = new OverlayWindow(currentSettings);
            _overlay.Show();
            Log("OverlayWindow shown.");

            Log("Creating PlayerWindow...");
            _playerWindow = new PlayerWindow(currentSettings, _settingsService, _settingsPopover, _nowPlaying);
            _playerWindow.Show();
            Log("PlayerWindow shown.");

            // Wire Audio -> 6-band Spectral Analyser -> Overlay
            _audio.SamplesReady += samples =>
            {
                _spectralAnalyser.Feed(samples);
            };

            _spectralAnalyser.FrameReady += frame =>
            {
                _overlay.UpdateSpectralFrame(frame);
            };

            // Wire Windows Media Session (Spotify Free/Premium, YouTube, Browser) -> Overlay & Color Extraction
            _nowPlaying.MediaStateChanged += info =>
            {
                _overlay.UpdatePlaybackState(info.IsPlaying);

                if (info.AlbumArt != null)
                {
                    var palette = AlbumArtColorExtractor.ExtractDominantColors(info.AlbumArt);
                    if (palette != null && palette.Count > 0)
                    {
                        _overlay.UpdatePalette(palette);
                    }
                }

                _tray?.UpdateToolTip(info.Title, info.Artist);
            };

            // Settings synchronization
            Action<AppSettings> applySettings = settings =>
            {
                _overlay.UpdateSettings(settings);
                _spectralAnalyser.Sensitivity = settings.BeatSensitivity;
                _spectralAnalyser.ReleaseCoeff = (float)(0.15f * settings.DecaySpeed);
            };

            _settingsService.SettingsChanged += applySettings;
            _settingsPopover.SettingsChanged += applySettings;
            _playerWindow.SettingsChanged += applySettings;

            Log("Initializing TrayIconManager...");
            _tray = new TrayIconManager(OnOpenPlayer, OnOpenSettings, OnToggleGlow, HandleAppExit);
            Log("TrayIconManager initialized.");

            // Start services
            _audio.Start();
            await _nowPlaying.StartAsync();
            Log("Background services started.");
        }
        catch (Exception ex)
        {
            Log($"Exception during OnStartup: {ex}");
        }
    }

    private void OnOpenPlayer()
    {
        if (_playerWindow == null) return;
        _playerWindow.Show();
        if (_playerWindow.WindowState == WindowState.Minimized)
        {
            _playerWindow.WindowState = WindowState.Normal;
        }
        _playerWindow.Activate();
        _playerWindow.Focus();
    }

    private void OnOpenSettings()
    {
        if (_settingsPopover == null) return;
        if (_playerWindow != null && _playerWindow.IsVisible)
        {
            Point pt = _playerWindow.PointToScreen(new Point(_playerWindow.ActualWidth / 2, _playerWindow.ActualHeight));
            _settingsPopover.ShowNear(pt);
        }
        else
        {
            _settingsPopover.Show();
            _settingsPopover.Activate();
        }
    }

    private void OnToggleGlow()
    {
        if (_settingsService == null || _overlay == null) return;
        var s = _settingsService.Current;
        s.IsEnabled = !s.IsEnabled;
        _settingsService.Save(s);
        _overlay.UpdateSettings(s);
        Log($"Glow toggled: {s.IsEnabled}");
    }

    private void HandleAppExit()
    {
        Log("HandleAppExit called - shutting down cleanly");
        try
        {
            _audio?.Stop();
            _audio?.Dispose();
            _nowPlaying?.Dispose();
            _tray?.Dispose();
            _settingsPopover?.Close();
            _playerWindow?.Close();
            _overlay?.Close();
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
        }
        catch (Exception ex)
        {
            Log($"Error closing app: {ex.Message}");
        }
        finally
        {
            Shutdown();
        }
    }
}

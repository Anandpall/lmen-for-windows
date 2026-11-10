using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using LmenV2.Services;
using LmenV2.Tray;
using LmenV2.Views;

namespace LmenV2
{
    public partial class App : Application
    {
        private static Mutex? _singleInstanceMutex;
        private static EventWaitHandle? _showWindowEvent;
        private MediaSessionService? _mediaService;
        private AudioReactiveService? _audioService;
        private TrayIconManager? _trayManager;

        private FullscreenVisualizerWindow? _fullscreenWindow;
        private DesktopOverlayWindow? _overlayWindow;

        public static void Log(string msg)
        {
            try
            {
                string dir = AppContext.BaseDirectory;
                try
                {
                    string? procPath = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(procPath))
                    {
                        string? pDir = Path.GetDirectoryName(procPath);
                        if (!string.IsNullOrEmpty(pDir) && Directory.Exists(pDir))
                        {
                            dir = pDir;
                        }
                    }
                }
                catch { }

                string logPath = Path.Combine(dir, "app_debug.log");
                File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
            }
            catch { }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            Log("App OnStartup begins");

            // Enforce Discrete / Standalone High-Performance GPU priority (NVIDIA RTX, AMD Radeon, etc.)
            if (GpuPreferenceService.EnsureHighPerformanceGpu(e.Args))
            {
                Shutdown();
                Environment.Exit(0);
                return;
            }

            // Ensure WPF hardware acceleration pipeline is active
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.Default;

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                Log($"[AppDomain UnhandledException] {args.ExceptionObject}");
            };

            DispatcherUnhandledException += (s, args) =>
            {
                Log($"[Dispatcher UnhandledException] {args.Exception}");
                args.Handled = true;
            };

            const string appGuid = "LmenV2_Ambient_Music_Companion_Unique";
            const string eventName = "LmenV2_Show_Window_Event";
            try
            {
                _singleInstanceMutex = new Mutex(true, appGuid, out bool isNewInstance);
                Log($"Mutex created. isNewInstance={isNewInstance}");
                if (!isNewInstance)
                {
                    Log("Another instance is running. Signaling existing instance and exiting.");
                    try
                    {
                        if (EventWaitHandle.TryOpenExisting(eventName, out var existingEvent))
                        {
                            existingEvent.Set();
                            existingEvent.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Failed signaling existing instance: {ex.Message}");
                    }

                    _singleInstanceMutex.Dispose();
                    _singleInstanceMutex = null;
                    Shutdown();
                    return;
                }

                // Setup listener event for secondary launches
                _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
                var listenerThread = new Thread(() =>
                {
                    while (_showWindowEvent.WaitOne())
                    {
                        if (_hasExited) break;
                        Dispatcher.BeginInvoke(() =>
                        {
                            Log("Show window event signaled from another instance.");
                            ShowFullscreenVisualizer();
                            if (_fullscreenWindow != null)
                            {
                                if (_fullscreenWindow.WindowState == WindowState.Minimized)
                                    _fullscreenWindow.WindowState = WindowState.Normal;
                                _fullscreenWindow.Activate();
                                _fullscreenWindow.Focus();
                            }
                        });
                    }
                })
                {
                    IsBackground = true,
                    Name = "LmenV2_SingleInstanceListener"
                };
                listenerThread.Start();
            }
            catch (Exception ex)
            {
                Log($"Mutex exception: {ex.Message}");
            }

            base.OnStartup(e);

            try
            {
                Log("Initializing MediaSessionService...");
                _mediaService = new MediaSessionService();

                Log("Initializing AudioReactiveService...");
                _audioService = new AudioReactiveService();

                Log("Initializing TrayIconManager...");
                _trayManager = new TrayIconManager();

                _trayManager.OpenFullscreenRequested += ShowFullscreenVisualizer;
                _trayManager.OpenOverlayRequested += ShowDesktopOverlay;
                _trayManager.OpenSettingsRequested += ShowSettings;
                _trayManager.ExitRequested += ExitApp;

                Log("Showing FullscreenVisualizerWindow...");
                ShowFullscreenVisualizer();

                Log("Starting AudioReactiveService...");
                _audioService.Start();

                Log("Initializing MediaSessionService async...");
                _ = _mediaService.InitializeAsync();

                Log("Startup completed successfully!");
            }
            catch (Exception ex)
            {
                Log($"[Startup Init Error] {ex}");
            }
        }

        public void ShowFullscreenVisualizer()
        {
            Dispatcher.Invoke(() =>
            {
                Log("ShowFullscreenVisualizer called");
                if (_overlayWindow != null)
                {
                    _overlayWindow.Hide();
                }

                if (_fullscreenWindow == null)
                {
                    _fullscreenWindow = new FullscreenVisualizerWindow(_mediaService!, _audioService!);

                    // The window can disappear without the app exiting (Alt+F4 while it is
                    // focused). Without clearing the reference, the next tray click would
                    // call Show() on a closed Window and throw
                    // "Cannot set Visibility or call Show/Activate after window has closed".
                    _fullscreenWindow.Closed += (s, e) => _fullscreenWindow = null;
                }

                _fullscreenWindow.Show();
                _fullscreenWindow.Activate();
            });
        }

        public void ShowDesktopOverlay()
        {
            Dispatcher.Invoke(() =>
            {
                Log("ShowDesktopOverlay called");
                if (_fullscreenWindow != null)
                {
                    _fullscreenWindow.RestoreTaskbarToTopmost();
                    _fullscreenWindow.Hide();
                }

                if (_overlayWindow == null)
                {
                    _overlayWindow = new DesktopOverlayWindow(_mediaService!, _audioService!);
                    _overlayWindow.Closed += (s, e) => _overlayWindow = null;
                }

                _overlayWindow.Show();
                _overlayWindow.EnsureTaskbarAboveOverlay();
            });
        }

        public bool IsOverlayModeActive =>
            _overlayWindow != null && _overlayWindow.IsVisible &&
            (_fullscreenWindow == null || !_fullscreenWindow.IsVisible);

        public void ShowSettings()
        {
            Dispatcher.Invoke(() =>
            {
                Log("ShowSettings called");
                var settings = new SettingsWindow();
                if (_fullscreenWindow != null && _fullscreenWindow.IsVisible)
                {
                    settings.Owner = _fullscreenWindow;
                }
                else if (_overlayWindow != null && _overlayWindow.IsVisible)
                {
                    settings.Owner = _overlayWindow;
                }
                settings.Topmost = true;
                settings.ShowDialog();
            });
        }

        private volatile bool _hasExited;

        public void ExitApp()
        {
            // ExitApp runs from the tray menu and again from OnExit (Shutdown() re-enters
            // it). Doing the teardown twice means releasing an unowned mutex and disposing
            // objects that are already gone.
            if (_hasExited) return;
            _hasExited = true;

            Log("ExitApp called");

            // Windows first: their OnClosed handlers unsubscribe from the render loop and
            // from service events, which must happen before the services go away.
            try
            {
                _fullscreenWindow?.Close();
                _overlayWindow?.Close();
            }
            catch (Exception ex)
            {
                Log($"[ExitApp window close] {ex.Message}");
            }

            try { _audioService?.Dispose(); } catch (Exception ex) { Log($"[ExitApp audio] {ex.Message}"); }
            try { _mediaService?.Dispose(); } catch (Exception ex) { Log($"[ExitApp media] {ex.Message}"); }
            try { _trayManager?.Dispose(); } catch (Exception ex) { Log($"[ExitApp tray] {ex.Message}"); }
            try { Services.SystemVolumeService.Instance.Dispose(); } catch (Exception ex) { Log($"[ExitApp volume] {ex.Message}"); }

            try
            {
                _showWindowEvent?.Set();
                _showWindowEvent?.Dispose();
            }
            catch { }

            try
            {
                _singleInstanceMutex?.ReleaseMutex();
            }
            catch (Exception ex)
            {
                Log($"[ExitApp mutex] {ex.Message}");
            }
            try { _singleInstanceMutex?.Dispose(); } catch { }

            Log("ExitApp teardown complete - shutting down");
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log("OnExit called");
            ExitApp();
            base.OnExit(e);
        }
    }
}

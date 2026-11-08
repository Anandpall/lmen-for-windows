using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using LmenV2.Helpers;
using LmenV2.Models;
using LmenV2.Rendering;
using LmenV2.Services;

namespace LmenV2.Views
{
    public partial class DesktopOverlayWindow : Window
    {
        private readonly MediaSessionService _mediaService;
        private readonly AudioReactiveService _audioService;
        private readonly SettingsService _settings = SettingsService.Instance;

        // The visual is a stack of dashed perimeter bands owned by EdgeBeam; this window
        // only decides how fast it travels, how bright it is and what colour it is.
        private readonly EdgeBeam _beam;

        private double _currentSpeedPx = 80.0;
        private double _breathPhase = 0.0;
        private double _beatExcitement = 0.0;

        private readonly Stopwatch _renderTimer = new Stopwatch();
        private TimeSpan _lastFrameTime = TimeSpan.Zero;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Win32Point lpPoint);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out Win32Rect lpRect);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct Win32Point
        {
            public int X;
            public int Y;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct Win32Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        public DesktopOverlayWindow(MediaSessionService mediaService, AudioReactiveService audioService)
        {
            InitializeComponent();
            _mediaService = mediaService;
            _audioService = audioService;

            _beam = new EdgeBeam(BeamHost);

            _mediaService.TrackUpdated += OnTrackUpdated;
            _settings.SettingsChanged += OnSettingsChanged;
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible)
                {
                    EnsureTaskbarAboveOverlay();
                }
            };
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = Math.Max(1, SystemParameters.PrimaryScreenHeight - 1);

            // Enable transparent click-through so user can click anything beneath
            Win32Helper.SetClickThrough(this);

            // Immediately ensure taskbar stays accessible above this overlay layer
            EnsureTaskbarAboveOverlay();

            ApplyGeometry();

            // Land on the current palette immediately instead of easing in from the
            // default crimson every time the overlay is opened from the tray.
            _beam.SetTargetColor(_mediaService.CurrentTrack.DominantColor);
            _beam.EaseColor(1.0);

            _renderTimer.Start();
            _lastFrameTime = _renderTimer.Elapsed;
            CompositionTarget.Rendering += OnRenderFrame;
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyGeometry();
        }

        private void OnSettingsChanged()
        {
            Dispatcher.Invoke(ApplyGeometry);
        }

        /// <summary>
        /// Size and GlowSpread are the only inputs the geometry needs and both change
        /// rarely, so the dash arrays are rebuilt here rather than every frame.
        ///
        /// GlowSpread used to scale a single stroke thickness, which is why dragging it
        /// barely changed anything. It now scales the entire octave stack, so it genuinely
        /// widens and narrows the halo.
        /// </summary>
        private void ApplyGeometry()
        {
            double w = BeamHost.ActualWidth > 1.0 ? BeamHost.ActualWidth : ActualWidth;
            double h = BeamHost.ActualHeight > 1.0 ? BeamHost.ActualHeight : ActualHeight;
            if (w <= 1.0) w = SystemParameters.PrimaryScreenWidth;
            if (h <= 1.0) h = SystemParameters.PrimaryScreenHeight;

            _beam.SetGeometry(w, h, _settings.GlowSpread);
        }

        private void OnRenderFrame(object? sender, EventArgs e)
        {
            if (!IsVisible || WindowState == WindowState.Minimized) return;

            // Ensure Windows Taskbar stays immediately accessible and responsive on bottom hover
            UpdateTaskbarHover();

            var now = _renderTimer.Elapsed;
            double dt = (now - _lastFrameTime).TotalSeconds;
            _lastFrameTime = now;

            if (dt <= 0.0 || dt > 0.1) dt = 0.016;

            float rawEnergy = _audioService.CurrentEnergy;
            double speedMultiplier = _settings.RotationSpeed;

            // Track dynamic kick & bass excitement for the "excited jump" effect
            if (rawEnergy > _beatExcitement)
            {
                // Fast attack: jump up immediately when kicks and bass hit!
                _beatExcitement = (_beatExcitement * 0.25) + (rawEnergy * 0.75);
            }
            else
            {
                // Smooth decay over ~350ms (for that "brief second" jump)
                _beatExcitement = Math.Max(0.0, _beatExcitement - (dt * 2.8));
            }

            if (speedMultiplier > 0.0)
            {
                // Baseline speed when quiet: slow, calm, majestic drift (~80 px/sec)
                double baseSpeed = 80.0 * speedMultiplier;

                // Kick acceleration: surge speed on kicks and bass
                double kickSurge = Math.Pow(rawEnergy, 1.25) * 2200.0 * speedMultiplier * _settings.AudioReactivity;
                double targetSpeed = baseSpeed + kickSurge;

                // Asymmetric acceleration & deceleration:
                // When kicks arrive -> instant punchy acceleration! Suddenly picks up!
                // When kicks end -> smooth physical momentum deceleration / drag back to slow drift
                if (targetSpeed > _currentSpeedPx)
                {
                    _currentSpeedPx = (_currentSpeedPx * 0.60) + (targetSpeed * 0.40);
                }
                else
                {
                    _currentSpeedPx = (_currentSpeedPx * 0.94) + (targetSpeed * 0.06);
                }

                _beam.AdvancePhase(_currentSpeedPx * dt);
            }

            // Breathing: calm 14s background cycle, energized by kicks and bass!
            _breathPhase += GlowProfile.BreathOmega * dt;
            if (_breathPhase > Math.PI * 2.0) _breathPhase -= Math.PI * 2.0;
            double baseBreath = GlowProfile.Breathing(_breathPhase, _settings.BreathingAmount);

            // Kick excitement directly pumps the breathing! For a brief second it jumps up and gets excited!
            double kickBreathBoost = _beatExcitement * 0.65 * _settings.AudioReactivity;
            double breath = Math.Clamp(baseBreath + kickBreathBoost, 0.0, 1.0);

            // Audio energy brightens the outer bands
            double energy = Math.Min(1.0, rawEnergy * _settings.AudioReactivity);

            double brightness = speedMultiplier > 0.0 ? _settings.GlowBrightness : 0.0;

            _beam.Render(brightness, breath, energy);
            _beam.EaseColor(dt);
        }

        private void OnTrackUpdated(MediaTrackInfo track)
        {
            Dispatcher.Invoke(() => _beam.SetTargetColor(track.DominantColor));
        }

        public void EnsureTaskbarAboveOverlay()
        {
            try
            {
                IntPtr taskbarHwnd = FindWindow("Shell_TrayWnd", null);
                if (taskbarHwnd != IntPtr.Zero)
                {
                    SetWindowPos(taskbarHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                }
                IntPtr secTaskbar = FindWindow("Shell_SecondaryTrayWnd", null);
                if (secTaskbar != IntPtr.Zero)
                {
                    SetWindowPos(secTaskbar, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                }
            }
            catch { }
        }

        private bool _lastHoverAtBottom = false;

        private void UpdateTaskbarHover()
        {
            try
            {
                if (!GetCursorPos(out Win32Point pt)) return;

                double screenH = ActualHeight > 100 ? ActualHeight : SystemParameters.PrimaryScreenHeight;
                double screenW = ActualWidth > 100 ? ActualWidth : SystemParameters.PrimaryScreenWidth;

                // Check if cursor is within bottom 8px or over taskbar
                bool isAtBottom = (pt.Y >= screenH - 8) && pt.X >= 0 && pt.X <= screenW;

                IntPtr taskbarHwnd = FindWindow("Shell_TrayWnd", null);
                if (taskbarHwnd != IntPtr.Zero)
                {
                    GetWindowRect(taskbarHwnd, out Win32Rect tbRect);
                    bool isOverTaskbar = (pt.X >= tbRect.Left && pt.X <= tbRect.Right && pt.Y >= tbRect.Top && pt.Y <= tbRect.Bottom);
                    if (isOverTaskbar) isAtBottom = true;
                }

                if (isAtBottom && !_lastHoverAtBottom)
                {
                    EnsureTaskbarAboveOverlay();
                }
                _lastHoverAtBottom = isAtBottom;
            }
            catch { }
        }

        protected override void OnClosed(EventArgs e)
        {
            CompositionTarget.Rendering -= OnRenderFrame;
            _mediaService.TrackUpdated -= OnTrackUpdated;
            _settings.SettingsChanged -= OnSettingsChanged;
            base.OnClosed(e);
        }
    }
}


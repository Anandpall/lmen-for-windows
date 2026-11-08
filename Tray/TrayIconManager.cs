using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DrawingColor = System.Drawing.Color;

namespace LmenV2.Tray
{
    public class TrayIconManager : IDisposable
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly ContextMenuStrip _menu;

        // Icon.FromHandle does NOT take ownership of the HICON, so the handle returned by
        // Bitmap.GetHicon has to be destroyed manually or it leaks for the process lifetime.
        private IntPtr _hIcon = IntPtr.Zero;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public event Action? OpenFullscreenRequested;
        public event Action? OpenOverlayRequested;
        public event Action? OpenSettingsRequested;
        public event Action? ExitRequested;

        public TrayIconManager()
        {
            _menu = new ContextMenuStrip();
            _menu.Items.Add("📺 Fullscreen Visualizer", null, (s, e) => OpenFullscreenRequested?.Invoke());
            _menu.Items.Add("🗗 Desktop Edge Overlay", null, (s, e) => OpenOverlayRequested?.Invoke());
            _menu.Items.Add("⚙ Settings", null, (s, e) => OpenSettingsRequested?.Invoke());
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("❌ Exit Lmen v2", null, (s, e) => ExitRequested?.Invoke());

            _notifyIcon = new NotifyIcon
            {
                Icon = CreateGlowIcon(),
                Text = "Lmen v2 - Ambient Music Glow",
                Visible = true,
                ContextMenuStrip = _menu
            };

            _notifyIcon.DoubleClick += (s, e) => OpenFullscreenRequested?.Invoke();
        }

        private Icon CreateGlowIcon()
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(DrawingColor.Transparent);

                // Outer ambient red glow
                using (var glowBrush = new SolidBrush(DrawingColor.FromArgb(120, 255, 30, 80)))
                {
                    g.FillEllipse(glowBrush, 2, 2, 28, 28);
                }

                // Inner bright neon core
                using (var coreBrush = new SolidBrush(DrawingColor.FromArgb(255, 255, 60, 100)))
                {
                    g.FillEllipse(coreBrush, 8, 8, 16, 16);
                }

                // Center highlight
                using (var centerBrush = new SolidBrush(DrawingColor.White))
                {
                    g.FillEllipse(centerBrush, 12, 12, 8, 8);
                }
            }

            _hIcon = bmp.GetHicon();
            return Icon.FromHandle(_hIcon);
        }

        public void Dispose()
        {
            // Visible=false first: removing the icon from the notification area before the
            // handle goes away is what prevents the "ghost tray icon" that lingers on the
            // taskbar until the next explorer refresh.
            try { _notifyIcon.Visible = false; } catch { }

            try { _notifyIcon.Dispose(); } catch { }
            try { _menu.Dispose(); } catch { }

            if (_hIcon != IntPtr.Zero)
            {
                try { DestroyIcon(_hIcon); } catch { }
                _hIcon = IntPtr.Zero;
            }
        }
    }
}

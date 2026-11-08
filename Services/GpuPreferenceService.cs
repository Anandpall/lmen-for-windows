using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace LmenV2.Services
{
    public static class GpuPreferenceService
    {
        private const string UserGpuPrefKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
        private const string VideoClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        /// <summary>
        /// Ensures the application executable is registered with Windows DirectX / D3D
        /// to run on the High-Performance discrete GPU (e.g., NVIDIA GeForce RTX 5060, AMD Radeon discrete)
        /// with first priority over integrated graphics.
        /// Returns true if a restart was initiated, false to continue startup.
        /// </summary>
        public static bool EnsureHighPerformanceGpu(string[]? args)
        {
            try
            {
                // 1. Force driver environment variables
                Environment.SetEnvironmentVariable("SHIM_MCCOMPAT", "0x0000000000000001");
                Environment.SetEnvironmentVariable("__NV_PRIME_RENDER_OFFLOAD", "1");
                Environment.SetEnvironmentVariable("__GLX_VENDOR_LIBRARY_NAME", "nvidia");
                Environment.SetEnvironmentVariable("DXGI_GPU_PREFERENCE", "2");
                Environment.SetEnvironmentVariable("AMD_POWERXPRESS_REQUEST_HIGH_PERFORMANCE", "1");

                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                {
                    exePath = Process.GetCurrentProcess().MainModule?.FileName;
                }

                if (string.IsNullOrWhiteSpace(exePath)) return false;

                string fullPath = Path.GetFullPath(exePath);

                // Register High Performance (GpuPreference=2;) in Windows DirectX configuration
                bool changed = false;
                using (var key = Registry.CurrentUser.CreateSubKey(UserGpuPrefKey, true))
                {
                    if (key != null)
                    {
                        var val = key.GetValue(fullPath) as string;
                        if (val == null || !val.Trim().Equals("GpuPreference=2;", StringComparison.OrdinalIgnoreCase))
                        {
                            key.SetValue(fullPath, "GpuPreference=2;", RegistryValueKind.String);
                            changed = true;
                        }

                        // Also write without canonicalization if different
                        if (!string.Equals(exePath, fullPath, StringComparison.OrdinalIgnoreCase))
                        {
                            key.SetValue(exePath, "GpuPreference=2;", RegistryValueKind.String);
                        }
                    }
                }

                if (changed)
                {
                    App.Log($"[GpuPreferenceService] Registered high-performance GPU preference for: {fullPath}");
                }
            }
            catch (Exception ex)
            {
                App.Log($"[GpuPreferenceService] EnsureHighPerformanceGpu error: {ex.Message}");
            }

            return false;
        }

        public static void OpenWindowsGraphicsSettings()
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:display-advancedgraphics") { UseShellExecute = true });
            }
            catch { }
        }

        /// <summary>
        /// Returns all installed graphics adapters detected on the system.
        /// </summary>
        public static List<string> GetInstalledGpus()
        {
            var gpus = new List<string>();
            try
            {
                using var baseKey = Registry.LocalMachine.OpenSubKey(VideoClassKey);
                if (baseKey != null)
                {
                    foreach (var subKeyName in baseKey.GetSubKeyNames())
                    {
                        if (subKeyName.StartsWith("0", StringComparison.Ordinal))
                        {
                            using var subKey = baseKey.OpenSubKey(subKeyName);
                            if (subKey != null)
                            {
                                var desc = subKey.GetValue("DriverDesc") as string;
                                if (!string.IsNullOrWhiteSpace(desc) && !gpus.Contains(desc))
                                {
                                    gpus.Add(desc);
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            return gpus;
        }

        /// <summary>
        /// Returns the name of the primary discrete GPU (or top graphics adapter).
        /// </summary>
        public static string GetPrimaryGpuName()
        {
            var gpus = GetInstalledGpus();
            if (gpus.Count == 0) return "High-Performance Graphics Adapter";

            // Prioritize dedicated / discrete GPUs
            var discrete = gpus.FirstOrDefault(g =>
                g.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                g.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                g.Contains("RTX", StringComparison.OrdinalIgnoreCase) ||
                g.Contains("GTX", StringComparison.OrdinalIgnoreCase) ||
                g.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase) ||
                g.Contains("Arc", StringComparison.OrdinalIgnoreCase));

            return discrete ?? gpus[0];
        }
    }
}

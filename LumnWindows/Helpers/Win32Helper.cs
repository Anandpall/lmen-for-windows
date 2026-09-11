using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LumnWindows.Helpers;

public static class Win32Helper
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    public static IntPtr GetWindowLong(IntPtr hWnd, int nIndex)
    {
        return IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));
    }

    public static IntPtr SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
    }

    public static void EnableClickThrough(IntPtr hwnd)
    {
        IntPtr extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        long newStyle = extendedStyle.ToInt64() | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        SetWindowLong(hwnd, GWL_EXSTYLE, new IntPtr(newStyle));
    }

    public static void SetStartWithWindows(bool enable)
    {
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string appName = "LumnWindows";

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKey, true);
            if (key == null) return;

            if (enable)
            {
                var processPath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(processPath))
                {
                    key.SetValue(appName, $"\"{processPath}\"");
                }
            }
            else
            {
                if (key.GetValue(appName) != null)
                {
                    key.DeleteValue(appName, false);
                }
            }
        }
        catch
        {
            // Ignore registry permission errors if any
        }
    }
}

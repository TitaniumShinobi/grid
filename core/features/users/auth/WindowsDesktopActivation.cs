using System;
using System.Runtime.InteropServices;

namespace Life.Auth.Desktop;

/// <summary>Raises an existing window only. Call on its UI dispatcher.</summary>
public static class WindowsDesktopActivation
{
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    public static bool TryActivate(IntPtr existingWindow)
    {
        if (!OperatingSystem.IsWindows() || existingWindow == IntPtr.Zero || !IsWindow(existingWindow)) return false;
        if (IsIconic(existingWindow)) ShowWindow(existingWindow, 9); // SW_RESTORE
        return SetForegroundWindow(existingWindow) && GetForegroundWindow() == existingWindow;
    }
}

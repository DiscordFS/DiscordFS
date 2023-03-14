using Vanara.PInvoke;

namespace DiscordFS.Platforms.Windows.Helpers;

public static class Win32WindowHelper
{
    public static void BringToFront(HWND hwnd)
    {
        User32.ShowWindow(hwnd, ShowWindowCommand.SW_SHOW);
        User32.ShowWindow(hwnd, ShowWindowCommand.SW_RESTORE);

        _ = User32.SetForegroundWindow(hwnd);
    }

    public static void MinimizeToTray(HWND hwnd)
    {
        User32.ShowWindow(hwnd, ShowWindowCommand.SW_MINIMIZE);
        User32.ShowWindow(hwnd, ShowWindowCommand.SW_HIDE);
    }
}
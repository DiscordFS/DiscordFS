using DiscordFS.Platforms.Windows.Helpers;
using DiscordFS.Platforms.Windows.TrayIcon;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using WinRT.Interop;

namespace DiscordFS;

public partial class App
{
    private bool _trayReady;

    public App()
    {
        InitializeComponent();

        MainPage = new AppShell();
    }

    protected override Window CreateWindow(IActivationState activationState)
    {
        var window = base.CreateWindow(activationState);
        if (_trayReady)
        {
            return window;
        }

        var tray = new WindowsTrayIcon();
        window.Created += (_, _) =>
        {
            var nativeWindow = window.Handler.PlatformView;
            var handle = WindowNative.GetWindowHandle(nativeWindow);
            var id = Win32Interop.GetWindowIdFromWindow(handle);
            var appWindow = AppWindow.GetFromWindowId(id);
            appWindow.Changed += (s, e) =>
            {
                var presenter = appWindow.Presenter as OverlappedPresenter;

                if (presenter?.State == OverlappedPresenterState.Minimized)
                {
                    Win32WindowHelper.MinimizeToTray(handle);
                }
            };

            tray.LeftClick += () => Win32WindowHelper.BringToFront(handle);
        };

        window.Destroying += (_, _) =>
        {
            tray.Dispose();
        };

        _trayReady = true;
        return window;
    }
}
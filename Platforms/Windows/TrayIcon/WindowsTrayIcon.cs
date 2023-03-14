// Some interop code taken from Mike Marshall's AnyForm

using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using static Vanara.PInvoke.Shell32;

namespace DiscordFS.Platforms.Windows.TrayIcon;

public class WindowsTrayIcon : IDisposable
{
    private static readonly object SyncRoot = new();

    public bool IsTaskbarIconCreated { get; private set; }

    public Action LeftClick { get; set; }

    public Action RightClick { get; set; }

    private readonly object _lockObject = new();

    private NOTIFYICONDATA _iconData;

    public WindowsTrayIcon()
    {
        var messageSink = new TrayWindowMessageSink();

        _iconData = new NOTIFYICONDATA();
        _iconData.cbSize = (uint)Marshal.SizeOf(_iconData);
        _iconData.hwnd = messageSink.MessageWindowHandle;
        _iconData.hBalloonIcon = 0x0;
        _iconData.uCallbackMessage = TrayWindowMessageSink.CallbackMessageId;
        _iconData.uTimeoutOrVersion = 0x0;

        var hIcon = GetTrayIcon();
        _iconData.hIcon = hIcon;

        _iconData.dwState = NIS.NIS_HIDDEN;
        _iconData.dwStateMask = NIS.NIS_HIDDEN;

        _iconData.uFlags = NIF.NIF_MESSAGE
                           | NIF.NIF_ICON
                           | NIF.NIF_TIP;

        _iconData.szInfo = _iconData.szInfoTitle = _iconData.szTip = string.Empty;

        CreateTaskbarIcon();

        messageSink.MouseEventReceived += MessageSinkMouseEventReceived;
        messageSink.TaskbarCreated += MessageSinkTaskbarCreated;
    }

    public void Dispose()
    {
        RemoveTaskbarIcon();
    }

    public static bool WriteIconData(in NOTIFYICONDATA data, NIM command)
    {
        return WriteIconData(data, command, data.uFlags);
    }

    public static bool WriteIconData(NOTIFYICONDATA data, NIM command, NIF flags)
    {
        data.uFlags = flags;

        lock (SyncRoot)
        {
            return Shell_NotifyIcon(command, in data);
        }
    }

    private void CreateTaskbarIcon()
    {
        lock (_lockObject)
        {
            if (IsTaskbarIconCreated)
            {
                return;
            }

            const NIF members = NIF.NIF_MESSAGE
                                | NIF.NIF_ICON
                                | NIF.NIF_TIP;

            var status = WriteIconData(_iconData, NIM.NIM_ADD, members);
            if (!status)
            {
                return;
            }

            SetVersion();
            IsTaskbarIconCreated = true;
        }
    }

    private nint GetTrayIcon()
    {
        const int size = 16;
        var trayIcon = new Icon(Resources.Tray, size, size);
        return trayIcon.Handle;
    }

    private void MessageSinkMouseEventReceived(TrayIconMouseEvent @event)
    {
        switch (@event)
        {
            case TrayIconMouseEvent.IconLeftMouseUp:
                LeftClick?.Invoke();
                break;

            case TrayIconMouseEvent.IconRightMouseUp:
                RightClick?.Invoke();
                break;
        }
    }

    private void MessageSinkTaskbarCreated()
    {
        RemoveTaskbarIcon();
        CreateTaskbarIcon();
    }

    private void RemoveTaskbarIcon()
    {
        lock (_lockObject)
        {
            if (!IsTaskbarIconCreated)
            {
                return;
            }

            WriteIconData(_iconData, NIM.NIM_DELETE, NIF.NIF_MESSAGE);
            IsTaskbarIconCreated = false;
        }
    }

    private void SetVersion()
    {
        _iconData.uTimeoutOrVersion = 0x4; // Vista

        var status = Shell_NotifyIcon(NIM.NIM_SETVERSION, in _iconData);
        if (!status)
        {
            Debug.Fail(message: "Could not set version");
        }
    }
}
// hardcodet.net NotifyIcon for WPF
// Copyright (c) 2009 - 2013 Philipp Sumi
// Contact and Information: http://www.hardcodet.net
//
// This library is free software; you can redistribute it and/or
// modify it under the terms of the Code Project Open License (CPOL);
// either version 1.0 of the License, or (at your option) any later
// version.
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
// OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
// HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
// OTHER DEALINGS IN THE SOFTWARE.
//
// THIS COPYRIGHT NOTICE MAY NOT BE REMOVED FROM THIS FILE


using System.ComponentModel;
using Serilog;
using Vanara.PInvoke;
using static Vanara.PInvoke.User32;
using GC = System.GC;

namespace DiscordFS.Platforms.Windows.TrayIcon;

public class TrayWindowMessageSink : IDisposable
{
    public const int CallbackMessageId = 0x400;

    public bool IsDisposed { get; private set; }

    internal HWND MessageWindowHandle { get; private set; }

    internal string WindowId { get; private set; }

    private bool _isDoubleClick;
    private WindowProc _messageHandler;

    private uint _taskbarRestartMessageId;

    public TrayWindowMessageSink()
    {
        CreateMessageWindow();
    }

    public void Dispose()
    {
        Dispose(disposing: true);

        GC.SuppressFinalize(this);
    }

    public event Action<bool> BalloonToolTipChanged;

    public event Action<bool> ChangeToolTipStateRequest;

    public event Action<TrayIconMouseEvent> MouseEventReceived;

    public event Action TaskbarCreated;

    internal static TrayWindowMessageSink CreateEmpty()
    {
        return new TrayWindowMessageSink
        {
            MessageWindowHandle = HWND.NULL
        };
    }

    private void CreateMessageWindow()
    {
        WindowId = "DiscordFSTaskbarIcon_" + DateTime.Now.Ticks;

        _messageHandler = OnWindowMessageReceived;

        var wc = new WNDCLASS
        {
            style = 0,
            lpfnWndProc = _messageHandler,
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = 0,
            hIcon = 0,
            hCursor = 0,
            hbrBackground = 0,
            lpszMenuName = "",
            lpszClassName = WindowId
        };

        RegisterClass(in wc);

        _taskbarRestartMessageId = RegisterWindowMessage(lpString: "TaskbarCreated");

        MessageWindowHandle = CreateWindowEx(dwExStyle: 0, WindowId, lpWindowName: "", dwStyle: 0, X: 0, Y: 0, nWidth: 1, nHeight: 1,
            hWndParent: 0, hMenu: 0,
            hInstance: 0, lpParam: 0);

        if (MessageWindowHandle == 0)
        {
            throw new Win32Exception(message: "Message window handle was not a valid pointer");
        }
    }

    private void Dispose(bool disposing)
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;

        DestroyWindow(MessageWindowHandle);
        _messageHandler = null;
    }

    private nint OnWindowMessageReceived(HWND hwnd, uint messageId, nint wparam, nint lparam)
    {
        if (messageId == _taskbarRestartMessageId)
        {
            TaskbarCreated?.Invoke();
        }

        ProcessWindowMessage(messageId, wparam, lparam);
        return DefWindowProc(hwnd, messageId, wparam, lparam);
    }


    private void ProcessWindowMessage(uint msg, nint wParam, nint lParam)
    {
        if (msg != CallbackMessageId)
        {
            return;
        }

        switch (lParam.ToInt32())
        {
            case 0x200:
                MouseEventReceived?.Invoke(TrayIconMouseEvent.MouseMove);
                break;

            case 0x201:
                MouseEventReceived?.Invoke(TrayIconMouseEvent.IconLeftMouseDown);
                break;

            case 0x202:
                if (!_isDoubleClick)
                {
                    MouseEventReceived?.Invoke(TrayIconMouseEvent.IconLeftMouseUp);
                }

                _isDoubleClick = false;
                break;

            case 0x203:
                _isDoubleClick = true;
                MouseEventReceived?.Invoke(TrayIconMouseEvent.IconDoubleClick);
                break;

            case 0x204:
                MouseEventReceived?.Invoke(TrayIconMouseEvent.IconRightMouseDown);
                break;

            case 0x205:
                MouseEventReceived?.Invoke(TrayIconMouseEvent.IconRightMouseUp);
                break;

            case 0x206:
                //double click with right mouse button - do not trigger event
                break;

            case 0x207:
                MouseEventReceived?.Invoke(TrayIconMouseEvent.IconMiddleMouseDown);
                break;

            case 520:
                MouseEventReceived?.Invoke(TrayIconMouseEvent.IconMiddleMouseUp);
                break;

            case 0x209:
                //double click with middle mouse button - do not trigger event
                break;

            case 0x402:
                BalloonToolTipChanged?.Invoke(obj: true);
                break;

            case 0x403:
            case 0x404:
                BalloonToolTipChanged?.Invoke(obj: false);
                break;

            case 0x405:
                MouseEventReceived?.Invoke(TrayIconMouseEvent.BalloonToolTipClicked);
                break;

            case 0x406:
                ChangeToolTipStateRequest?.Invoke(obj: true);
                break;

            case 0x407:
                ChangeToolTipStateRequest?.Invoke(obj: false);
                break;

            default:
                // todo: implemnt 123(0x7B) and 1024(0x400)
                // First one is my right click and second is my left click (single click)
                Log.Warning("Unhandled NotifyIcon message ID: " + lParam);
                break;
        }
    }

    ~TrayWindowMessageSink()
    {
        Dispose(disposing: false);
    }
}
using System;
using System.Runtime.Versioning;
using System.Threading;

namespace Zhuoying.Platform.Windows;

/// <summary>
/// 基于专用消息线程 + message-only 窗口的全局热键实现。
/// RegisterHotKey 必须在拥有窗口的线程上调用，故通过 WM_APP 消息转发注册请求。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHotkeyService : IHotkeyService
{
    private const int HotkeyId = 1;
    private const uint WmAppRegister = Win32.WM_APP + 1;

    private readonly ManualResetEventSlim _windowReady = new(false);
    private readonly ManualResetEventSlim _registerDone = new(false);

    private Thread? _thread;
    private IntPtr _hwnd;
    private Win32.WndProc? _wndProc; // 持有引用防止委托被 GC

    private HotkeyModifiers _pendingModifiers;
    private uint _pendingVk;
    private volatile bool _registerOk;
    private Action? _callback;
    private bool _disposed;

    public bool TryRegister(HotkeyModifiers modifiers, uint virtualKey, Action callback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureThread();
        if (!_windowReady.Wait(3000) || _hwnd == IntPtr.Zero)
            return false;

        _pendingModifiers = modifiers;
        _pendingVk = virtualKey;
        _registerDone.Reset();
        Win32.PostMessageW(_hwnd, WmAppRegister, IntPtr.Zero, IntPtr.Zero);
        if (!_registerDone.Wait(3000) || !_registerOk)
            return false;

        _callback = callback;
        return true;
    }

    private void EnsureThread()
    {
        if (_thread != null)
            return;
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "Zhuoying.Hotkey" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void MessageLoop()
    {
        _wndProc = WndProc;
        var className = $"Zhuoying.HotkeyWindow.{Environment.ProcessId}";
        var wc = new Win32.WNDCLASSEX
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = Win32.GetModuleHandleW(null),
            lpszClassName = className,
        };
        if (Win32.RegisterClassExW(ref wc) == 0)
        {
            _windowReady.Set();
            return;
        }

        _hwnd = Win32.CreateWindowExW(0, className, null, 0, 0, 0, 0, 0,
            Win32.HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        _windowReady.Set();
        if (_hwnd == IntPtr.Zero)
            return;

        while (Win32.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessageW(ref msg);
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32.WM_HOTKEY:
                _callback?.Invoke();
                return IntPtr.Zero;

            case WmAppRegister:
                Win32.UnregisterHotKey(hWnd, HotkeyId);
                _registerOk = Win32.RegisterHotKey(
                    hWnd, HotkeyId, (uint)_pendingModifiers | Win32.MOD_NOREPEAT, _pendingVk);
                _registerDone.Set();
                return IntPtr.Zero;

            case Win32.WM_DESTROY:
                Win32.UnregisterHotKey(hWnd, HotkeyId);
                Win32.PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return Win32.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_hwnd != IntPtr.Zero)
            Win32.PostMessageW(_hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(1000);
        _windowReady.Dispose();
        _registerDone.Dispose();
    }
}

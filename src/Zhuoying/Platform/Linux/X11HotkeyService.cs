using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Zhuoying.Platform.Linux;

/// <summary>
/// X11 全局热键（对应 Windows 侧 <c>WindowsHotkeyService</c> 的 RegisterHotKey）。
///
/// 与 Windows 的差异：
/// - XGrabKey 抓的是 **keycode**，配置里存的是 Win32 VK 码，
///   经 <see cref="VirtualKeyToKeysym"/> → XKeysymToKeycode 两级转换；
/// - 修饰键锁（NumLock/CapsLock/ScrollLock）会进 event state，
///   必须把 8 种锁组合全部 grab 一遍，否则开着小键盘锁热键就失灵；
/// - 冲突检测靠 X 错误处理器：XGrabKey 本身不返回错误，
///   BadAccess 是异步送达的，必须 XSync 后再看标志位。
///
/// 线程模型同 Windows 侧：独立后台线程 + 独立 Display 连接
/// （Xlib 连接不可跨线程共享，且此线程要长期阻塞在 poll 上）。
/// </summary>
public sealed unsafe class X11HotkeyService : IHotkeyService
{
    private readonly List<(int Keycode, uint Modifiers, Action Callback)> _registered = [];
    private readonly object _lock = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly CancellationTokenSource _cts = new();

    private IntPtr _display;
    private IntPtr _root;
    private Thread? _thread;
    private bool _disposed;

    /// <summary>X 异步错误标志（XGrabKey 的 BadAccess 靠它回收）。</summary>
    private static volatile int _xError;

    /// <summary>本服务专用的错误处理器：在 <see cref="X11Display"/> 那个"只吞不记"的
    /// 全局处理器之上，额外记下标志位——XGrabKey 的 BadAccess 是异步送达的，
    /// 只能靠它判断热键是否被别的客户端占用。</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnXError(IntPtr display, IntPtr errorEvent)
    {
        _xError = 1;
        return 0; // 吞掉错误，绝不让 Xlib 默认处理器 exit(1)
    }

    public X11HotkeyService()
    {
        _thread = new Thread(EventLoop)
        {
            IsBackground = true,
            Name = "Zhuoying.X11Hotkey",
        };
        _thread.Start();
        // 等连接就绪，让 TryRegister 可以同步返回真实结果（同 Windows 侧语义）
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    public bool TryRegister(HotkeyModifiers modifiers, uint virtualKey, Action callback)
    {
        if (_disposed || _display == IntPtr.Zero)
            return false;

        var keysym = VirtualKeyToKeysym(virtualKey);
        if (keysym == 0)
            return false;

        lock (_lock)
        {
            // 换键前先撤销已注册的（同 Windows 侧：服务只持一个热键）
            UnregisterAll();

            int keycode = X11Interop.XKeysymToKeycode(_display, keysym);
            if (keycode == 0)
                return false;

            var xMods = ToX11Modifiers(modifiers);
            _xError = 0;
            foreach (var lockMask in LockCombinations)
                X11Interop.XGrabKey(_display, keycode, xMods | lockMask, _root,
                    ownerEvents: true, X11Interop.GrabModeAsync, X11Interop.GrabModeAsync);
            // BadAccess（键已被别的客户端抓走）是异步的，必须同步一次才看得到
            X11Interop.XSync(_display, false);

            if (_xError != 0)
            {
                foreach (var lockMask in LockCombinations)
                    X11Interop.XUngrabKey(_display, keycode, xMods | lockMask, _root);
                X11Interop.XSync(_display, false);
                return false;
            }

            _registered.Add((keycode, xMods, callback));
            return true;
        }
    }

    /// <summary>NumLock/CapsLock/ScrollLock 的 8 种组合——都要 grab。</summary>
    private static readonly uint[] LockCombinations =
    [
        0,
        X11Interop.LockMask,
        X11Interop.Mod2Mask,
        X11Interop.Mod5Mask,
        X11Interop.LockMask | X11Interop.Mod2Mask,
        X11Interop.LockMask | X11Interop.Mod5Mask,
        X11Interop.Mod2Mask | X11Interop.Mod5Mask,
        X11Interop.LockMask | X11Interop.Mod2Mask | X11Interop.Mod5Mask,
    ];

    private static uint ToX11Modifiers(HotkeyModifiers m)
    {
        uint r = 0;
        if (m.HasFlag(HotkeyModifiers.Control)) r |= X11Interop.ControlMask;
        if (m.HasFlag(HotkeyModifiers.Shift)) r |= X11Interop.ShiftMask;
        if (m.HasFlag(HotkeyModifiers.Alt)) r |= X11Interop.Mod1Mask;
        if (m.HasFlag(HotkeyModifiers.Win)) r |= X11Interop.Mod4Mask;
        return r;
    }

    private void UnregisterAll()
    {
        foreach (var (keycode, mods, _) in _registered)
            foreach (var lockMask in LockCombinations)
                X11Interop.XUngrabKey(_display, keycode, mods | lockMask, _root);
        _registered.Clear();
        if (_display != IntPtr.Zero)
            X11Interop.XSync(_display, false);
    }

    private void EventLoop()
    {
        _display = X11Interop.XOpenDisplay(null);
        if (_display == IntPtr.Zero)
        {
            _ready.Set();
            return;
        }
        X11Interop.XSetErrorHandler(
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)&OnXError);
        _root = X11Interop.XDefaultRootWindow(_display);
        X11Interop.XSelectInput(_display, _root, X11Interop.KeyPressMask);
        var fd = X11Interop.XConnectionNumber(_display);
        _ready.Set();

        // XEvent 联合体在 64 位机上 192 字节，留足余量
        var evBuf = stackalloc byte[256];
        while (!_cts.IsCancellationRequested)
        {
            // 阻塞等 X socket 可读（不能用 XNextEvent 直接阻塞：Dispose 时叫不醒）
            var pfd = new PollFd { Fd = fd, Events = POLLIN };
            var n = poll(&pfd, 1, 200);
            if (_cts.IsCancellationRequested)
                break;
            if (n <= 0)
                continue;

            lock (_lock)
            {
                while (X11Interop.XPending(_display) > 0)
                {
                    X11Interop.XNextEvent(_display, (IntPtr)evBuf);
                    if (*(int*)evBuf != X11Interop.KeyPress)
                        continue;
                    // XKeyEvent 布局：state 在偏移 80，keycode 在 84（64 位 ABI）
                    var state = *(uint*)(evBuf + 80);
                    var keycode = (int)*(uint*)(evBuf + 84);
                    // 比对时滤掉锁位，只看真正的修饰键
                    var mods = state & ~(X11Interop.LockMask
                                         | X11Interop.Mod2Mask | X11Interop.Mod5Mask);
                    foreach (var (kc, m, cb) in _registered)
                    {
                        if (kc != keycode || m != mods)
                            continue;
                        // 回调可能在非 UI 线程触发，调用方自行调度（接口约定）
                        try { cb(); }
                        catch (Exception) { /* 回调异常不能打死热键线程 */ }
                        break;
                    }
                }
            }
        }

        lock (_lock)
        {
            UnregisterAll();
            X11Interop.XCloseDisplay(_display);
            _display = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Win32 虚拟键码 → X11 keysym。配置文件里存的是 VK 码（跨平台共用一份
    /// settings.json，同一份配置在两个系统上得是同一个键）。
    /// 未覆盖的键返回 0（注册失败，设置界面会提示改键）。
    /// </summary>
    internal static nuint VirtualKeyToKeysym(uint vk) => vk switch
    {
        >= 0x30 and <= 0x39 => vk,                       // '0'-'9' → ASCII 同值
        >= 0x41 and <= 0x5A => vk + 0x20,                // 'A'-'Z' → 小写 keysym
        >= 0x70 and <= 0x87 => 0xFFBE + (vk - 0x70),     // F1-F24
        0x08 => 0xFF08, // Backspace
        0x09 => 0xFF09, // Tab
        0x0D => 0xFF0D, // Return
        0x13 => 0xFF13, // Pause
        0x1B => 0xFF1B, // Escape
        0x20 => 0x0020, // Space
        0x21 => 0xFF55, // PageUp
        0x22 => 0xFF56, // PageDown
        0x23 => 0xFF57, // End
        0x24 => 0xFF50, // Home
        0x25 => 0xFF51, // Left
        0x26 => 0xFF52, // Up
        0x27 => 0xFF53, // Right
        0x28 => 0xFF54, // Down
        0x2C => 0xFF61, // PrintScreen
        0x2D => 0xFF63, // Insert
        0x2E => 0xFFFF, // Delete
        >= 0x60 and <= 0x69 => 0xFFB0 + (vk - 0x60),     // 小键盘 0-9
        0x6A => 0xFFAA, // 小键盘 *
        0x6B => 0xFFAB, // 小键盘 +
        0x6D => 0xFFAD, // 小键盘 -
        0x6E => 0xFFAE, // 小键盘 .
        0x6F => 0xFFAF, // 小键盘 /
        0xBA => 0x003B, // ;
        0xBB => 0x003D, // =
        0xBC => 0x002C, // ,
        0xBD => 0x002D, // -
        0xBE => 0x002E, // .
        0xBF => 0x002F, // /
        0xC0 => 0x0060, // `
        0xDB => 0x005B, // [
        0xDC => 0x005C, // \
        0xDD => 0x005D, // ]
        0xDE => 0x0027, // '
        _ => 0,
    };

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        _cts.Dispose();
        _ready.Dispose();
    }

    // ---------- poll(2) ----------

    private const short POLLIN = 0x001;

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int poll(PollFd* fds, nuint nfds, int timeout);
}

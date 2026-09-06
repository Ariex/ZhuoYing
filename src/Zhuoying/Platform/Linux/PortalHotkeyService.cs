using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Zhuoying.Platform.Linux;

/// <summary>
/// Wayland 全局热键：xdg-desktop-portal 的 GlobalShortcuts 接口。
///
/// Wayland 没有 XGrabKey 的等价物——合成器不允许客户端抢全局按键，
/// 这是安全模型的一部分。portal GlobalShortcuts 是唯一合法通道，代价是：
/// - 绑定时合成器会弹一次确认框（同 ScreenCast 的一次性授权）；
/// - **用户的实际触发键由合成器决定**，我们只能给 preferred_trigger 建议。
///   设置界面里显示的键位因此可能与实际不符，这是平台差异不是 bug。
///
/// **部署约束（实测）**：portal 要求调用方有 app id，否则 CreateSession 直接返回
/// `NotAllowed: An app id is required`。xdg-desktop-portal 是从进程的 systemd
/// scope 名（`app-<appid>-<pid>.scope`）反推 app id 的，所以**从终端直接运行的
/// 进程拿不到 app id，全局热键必然不可用**；必须经 .desktop 启动
/// （应用菜单、XDG autostart，或 `systemd-run --user --scope --unit=app-zhuoying-N`）。
/// 见 docs/LINUX-PORT.md「Wayland 全局热键」。
///
/// 线程模型同 X11 侧：独立线程 + 独立连接，长期泵 GMainContext 收 Activated 信号。
/// </summary>
public sealed unsafe class PortalHotkeyService : IHotkeyService
{
    private const string Iface = "org.freedesktop.portal.GlobalShortcuts";
    private const string ShortcutId = "capture";

    /// <summary>等待中的实例，键为传给 GDBus 的 user_data。</summary>
    private static readonly ConcurrentDictionary<IntPtr, PortalHotkeyService> Instances = new();

    private readonly ManualResetEventSlim _ready = new(false);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();

    private PortalConnection? _connection;
    private string? _sessionHandle;
    private Action? _callback;
    private Thread? _thread;
    private GCHandle _self;
    private uint _activatedSubscription;
    private volatile bool _connected;
    private bool _disposed;

    /// <summary>待绑定的请求（由 TryRegister 交给后台线程执行）。</summary>
    private volatile string? _pendingTrigger;
    private volatile int _bindResult; // 0=进行中 1=成功 -1=失败

    public PortalHotkeyService()
    {
        _thread = new Thread(EventLoop)
        {
            IsBackground = true,
            Name = "Zhuoying.PortalHotkey",
        };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(10));
    }

    public bool TryRegister(HotkeyModifiers modifiers, uint virtualKey, Action callback)
    {
        if (_disposed || !_connected)
            return false;
        var trigger = BuildTrigger(modifiers, virtualKey);
        if (trigger == null)
            return false;

        lock (_lock)
        {
            _callback = callback;
            _bindResult = 0;
            _pendingTrigger = trigger;
        }
        // 绑定要弹确认框，给足时间；由后台线程执行以免阻塞 UI 线程的 GMainContext
        var deadline = Environment.TickCount64 + 120_000;
        while (_bindResult == 0 && Environment.TickCount64 < deadline && !_disposed)
            Thread.Sleep(50);
        return _bindResult == 1;
    }

    private void EventLoop()
    {
        try
        {
            _connection = new PortalConnection();
            _connection.PushContext();
            CreateSession();
            _connected = true;
        }
        catch (Exception)
        {
            // portal 无 GlobalShortcuts 实现（老版本桌面）：热键不可用，
            // 上层会提示"热键被占用/不可用"，其余功能照常
            _ready.Set();
            return;
        }
        _ready.Set();

        while (!_cts.IsCancellationRequested)
        {
            if (_pendingTrigger is { } trigger)
            {
                _pendingTrigger = null;
                // 首次 BindShortcuts 会触发 GNOME 那边
                // org.gnome.Settings.GlobalShortcutsProvider 的 D-Bus 激活
                // （要拉起 gnome-control-center 的服务），实测第一次会等到超时；
                // 服务常驻后同样的调用 0 秒返回。所以超时后重试一次。
                var bound = false;
                for (var attempt = 0; attempt < 2 && !bound; attempt++)
                {
                    try
                    {
                        BindShortcut(trigger);
                        bound = true;
                    }
                    catch (TimeoutException)
                    {
                        // 首轮多半是 provider 还在启动，重试
                    }
                    catch (Exception)
                    {
                        break;
                    }
                }
                _bindResult = bound ? 1 : -1;
            }
            // 非阻塞泵 + 短睡眠：阻塞式迭代收不到取消信号
            if (!_connection!.Iterate(false))
                Thread.Sleep(20);
        }

        if (_activatedSubscription != 0)
            _connection!.Unsubscribe(_activatedSubscription);
        _connection?.PopContext();
        _connection?.Dispose();
        _connection = null;
    }

    private void CreateSession()
    {
        var conn = _connection!;
        var createToken = PortalConnection.NewToken("GsCreate");
        var sessionToken = PortalConnection.NewToken("GsSess");
        var created = conn.CallWithRequest(Iface, "CreateSession",
            $"({{'handle_token': <'{createToken}'>, " +
            $"'session_handle_token': <'{sessionToken}'>}},)",
            createToken, 20);
        if (!created.Ok)
            throw new InvalidOperationException($"GlobalShortcuts CreateSession 失败 code={created.Code}");
        try
        {
            _sessionHandle = ReadSessionHandle(created.Results)
                             ?? throw new InvalidOperationException("portal 未返回 session_handle");
        }
        finally
        {
            GLibInterop.g_variant_unref(created.Results);
        }

        _self = GCHandle.Alloc(this);
        var userData = GCHandle.ToIntPtr(_self);
        Instances[userData] = this;
        _activatedSubscription = conn.SubscribeSignal(Iface, "Activated", null,
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr,
                IntPtr, IntPtr, IntPtr, void>)&OnActivated,
            userData);
    }

    private void BindShortcut(string trigger)
    {
        var conn = _connection!;
        var bindToken = PortalConnection.NewToken("GsBind");
        var bound = conn.CallWithRequest(Iface, "BindShortcuts",
            $"(objectpath '{_sessionHandle}', " +
            $"[('{ShortcutId}', {{'description': <'截屏'>, " +
            $"'preferred_trigger': <'{trigger}'>}})], " +
            $"'', {{'handle_token': <'{bindToken}'>}})",
            bindToken, 120);
        if (!bound.Ok)
            throw new InvalidOperationException($"BindShortcuts 失败 code={bound.Code}");
        GLibInterop.g_variant_unref(bound.Results);
    }

    private static string? ReadSessionHandle(IntPtr results)
    {
        var v = GLibInterop.g_variant_lookup_value(results, "session_handle", IntPtr.Zero);
        if (v == IntPtr.Zero)
            return null;
        try
        {
            return GLibInterop.GetString(v);
        }
        finally
        {
            GLibInterop.g_variant_unref(v);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnActivated(IntPtr connection, IntPtr sender, IntPtr path,
        IntPtr interfaceName, IntPtr signal, IntPtr parameters, IntPtr userData)
    {
        if (!Instances.TryGetValue(userData, out var self))
            return;
        // 参数是 (o session_handle, s shortcut_id, t timestamp, a{sv} options)
        var idVar = GLibInterop.g_variant_get_child_value(parameters, 1);
        var id = GLibInterop.GetString(idVar);
        GLibInterop.g_variant_unref(idVar);
        if (id != ShortcutId)
            return;
        Action? cb;
        lock (self._lock)
            cb = self._callback;
        try { cb?.Invoke(); }
        catch (Exception) { /* 回调异常不能打死热键线程 */ }
    }

    /// <summary>
    /// Win32 虚拟键码 + 修饰键 → portal 的快捷键语法
    /// （XDG shortcuts syntax，如 <c>CTRL+SHIFT+a</c>）。
    /// </summary>
    internal static string? BuildTrigger(HotkeyModifiers modifiers, uint virtualKey)
    {
        var key = KeyName(virtualKey);
        if (key == null)
            return null;
        var parts = new System.Collections.Generic.List<string>(4);
        if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("CTRL");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("SHIFT");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("ALT");
        if (modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("LOGO");
        parts.Add(key);
        return string.Join('+', parts);
    }

    /// <summary>VK → portal 键名。与 X11 侧
    /// <see cref="X11HotkeyService.VirtualKeyToKeysym"/> 覆盖同一批键。</summary>
    private static string? KeyName(uint vk) => vk switch
    {
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),          // '0'-'9'
        >= 0x41 and <= 0x5A => ((char)(vk + 0x20)).ToString(), // 'A'-'Z' → 小写
        >= 0x70 and <= 0x7B => $"F{vk - 0x70 + 1}",            // F1-F12
        0x20 => "space",
        0x2C => "Print",
        0x1B => "Escape",
        0x0D => "Return",
        0x09 => "Tab",
        0x2D => "Insert",
        0x2E => "Delete",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "Prior",
        0x22 => "Next",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        _ => null,
    };

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(3));
        _thread = null;
        if (_self.IsAllocated)
        {
            Instances.TryRemove(GCHandle.ToIntPtr(_self), out _);
            _self.Free();
        }
        _cts.Dispose();
        _ready.Dispose();
    }
}

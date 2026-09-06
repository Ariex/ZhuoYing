using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Zhuoying.Platform.Linux;

/// <summary>portal 的 Request 应答：0 = 成功，1 = 用户取消，2 = 其他失败。</summary>
internal sealed record PortalResponse(uint Code, IntPtr Results)
{
    public bool Ok => Code == 0;
    public bool Cancelled => Code == 1;
}

/// <summary>
/// 与 xdg-desktop-portal 的会话总线连接，封装 portal 特有的
/// **Request 模式**：方法调用只返回一个 <c>/…/request/…</c> 对象路径，
/// 真正的结果稍后通过该路径上的 <c>Response</c> 信号送达。
///
/// 竞态很关键：必须**先订阅信号再发起调用**——反过来做，快速返回的
/// 请求（比如已有 restore_token 的免授权 Start）会在订阅完成前就把信号发完，
/// 于是永远等不到（移植期实测踩过，见 docs/LINUX-PORT.md）。
///
/// 线程模型：GDBus 把信号派发到**订阅时所在线程的 thread-default GMainContext**，
/// 所以订阅、发起、泵事件、退订必须在同一线程且全程持锁。
/// </summary>
internal sealed unsafe class PortalConnection : IDisposable
{
    private const string PortalBus = "org.freedesktop.portal.Desktop";
    private const string PortalPath = "/org/freedesktop/portal/desktop";

    /// <summary>等待中的 Request，键为传给 GDBus 的 user_data。</summary>
    private static readonly ConcurrentDictionary<IntPtr, Slot> Pending = new();
    private static long _tokenCounter;

    private sealed class Slot
    {
        public volatile bool Done;
        public uint Code;
        public IntPtr Results; // 已 ref，由等待方 unref
    }

    private readonly object _lock = new();
    private readonly IntPtr _context;
    private readonly IntPtr _connection;

    /// <summary>本连接唯一名转成 Request 路径里的形式（":1.183" → "1_183"）。</summary>
    private readonly string _senderToken;

    private bool _disposed;

    internal PortalConnection()
    {
        _context = GLibInterop.g_main_context_new();
        _connection = GLibInterop.g_bus_get_sync(
            GLibInterop.G_BUS_TYPE_SESSION, IntPtr.Zero, out var error);
        if (_connection == IntPtr.Zero)
            throw new InvalidOperationException(
                $"连接会话总线失败：{GLibInterop.TakeErrorMessage(error)}");

        var unique = Marshal.PtrToStringUTF8(
            GLibInterop.g_dbus_connection_get_unique_name(_connection)) ?? "";
        _senderToken = unique.TrimStart(':').Replace('.', '_');
    }

    /// <summary>生成本进程内唯一的 handle_token（只含 portal 允许的字符）。</summary>
    internal static string NewToken(string prefix) =>
        $"zy{prefix}{Interlocked.Increment(ref _tokenCounter)}_{Environment.ProcessId}";

    /// <summary>
    /// 调用一个 portal 方法并等待其 Response 信号。
    /// <paramref name="parameters"/> 是 GVariant 文本格式的完整参数元组，
    /// 其中的 handle_token 必须等于 <paramref name="handleToken"/>。
    /// </summary>
    internal PortalResponse CallWithRequest(string interfaceName, string method,
        string parameters, string handleToken, int timeoutSeconds)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var requestPath =
                $"/org/freedesktop/portal/desktop/request/{_senderToken}/{handleToken}";

            var slot = new Slot();
            var handle = GCHandle.Alloc(slot);
            var userData = GCHandle.ToIntPtr(handle);
            Pending[userData] = slot;

            GLibInterop.g_main_context_push_thread_default(_context);
            var subscription = 0u;
            try
            {
                // 先订阅后调用：顺序颠倒会丢掉快速返回的响应
                subscription = GLibInterop.g_dbus_connection_signal_subscribe(
                    _connection, PortalBus, "org.freedesktop.portal.Request",
                    "Response", requestPath, null, GLibInterop.G_DBUS_SIGNAL_FLAGS_NONE,
                    (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr,
                        IntPtr, IntPtr, IntPtr, void>)&OnResponse,
                    userData, IntPtr.Zero);

                Call(interfaceName, method, parameters);
                return Wait(slot, timeoutSeconds, method);
            }
            finally
            {
                if (subscription != 0)
                    GLibInterop.g_dbus_connection_signal_unsubscribe(_connection, subscription);
                GLibInterop.g_main_context_pop_thread_default(_context);
                Pending.TryRemove(userData, out _);
                handle.Free();
            }
        }
    }

    /// <summary>调用一个 portal 方法并直接返回结果（无 Request 模式，如 Close）。</summary>
    internal IntPtr Call(string interfaceName, string method, string parameters)
    {
        var args = GLibInterop.g_variant_new_parsed(parameters, IntPtr.Zero);
        if (args == IntPtr.Zero)
            throw new InvalidOperationException($"GVariant 解析失败：{parameters}");
        GLibInterop.g_variant_ref_sink(args);
        try
        {
            var reply = GLibInterop.g_dbus_connection_call_sync(_connection,
                PortalBus, PortalPath, interfaceName, method, args, IntPtr.Zero,
                GLibInterop.G_DBUS_CALL_FLAGS_NONE, 25_000, IntPtr.Zero, out var error);
            if (reply == IntPtr.Zero)
                throw new InvalidOperationException(
                    $"{interfaceName}.{method} 调用失败：{GLibInterop.TakeErrorMessage(error)}");
            return reply;
        }
        finally
        {
            GLibInterop.g_variant_unref(args);
        }
    }

    /// <summary>泵 GMainContext 直到响应到达或超时。</summary>
    private PortalResponse Wait(Slot slot, int timeoutSeconds, string method)
    {
        var deadline = Environment.TickCount64 + timeoutSeconds * 1000L;
        while (!slot.Done)
        {
            // 非阻塞迭代 + 短睡眠：阻塞式迭代没有超时参数，
            // 授权框迟迟无人点时会永久卡死抓屏线程
            if (!GLibInterop.g_main_context_iteration(_context, false))
                Thread.Sleep(10);
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException(
                    $"portal {method} 等待响应超时（{timeoutSeconds}s）");
        }
        return new PortalResponse(slot.Code, slot.Results);
    }

    /// <summary>
    /// 长期订阅一个信号（不走 Request 模式，如 GlobalShortcuts 的 Activated）。
    /// 调用方必须自己持续 <see cref="Iterate"/> 才收得到，且订阅与泵事件
    /// 必须在同一线程——GDBus 按订阅时的 thread-default context 派发。
    /// </summary>
    internal uint SubscribeSignal(string interfaceName, string member, string? objectPath,
        IntPtr callback, IntPtr userData)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return GLibInterop.g_dbus_connection_signal_subscribe(_connection,
                PortalBus, interfaceName, member, objectPath, null,
                GLibInterop.G_DBUS_SIGNAL_FLAGS_NONE, callback, userData, IntPtr.Zero);
        }
    }

    internal void Unsubscribe(uint subscriptionId)
    {
        lock (_lock)
        {
            if (!_disposed && subscriptionId != 0)
                GLibInterop.g_dbus_connection_signal_unsubscribe(_connection, subscriptionId);
        }
    }

    /// <summary>把本连接的 GMainContext 设为当前线程默认——长期监听信号的
    /// 线程要在订阅前调用一次，之后该线程收到的信号才走这个 context。</summary>
    internal void PushContext() => GLibInterop.g_main_context_push_thread_default(_context);

    internal void PopContext() => GLibInterop.g_main_context_pop_thread_default(_context);

    /// <summary>派发一轮待处理事件；返回是否真的处理了事件。</summary>
    internal bool Iterate(bool mayBlock) =>
        GLibInterop.g_main_context_iteration(_context, mayBlock);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnResponse(IntPtr connection, IntPtr sender, IntPtr path,
        IntPtr interfaceName, IntPtr signal, IntPtr parameters, IntPtr userData)
    {
        if (!Pending.TryGetValue(userData, out var slot) || slot.Done)
            return;
        // parameters 是 (u a{sv})，回调返回后即失效，须自行 ref 保活
        var code = GLibInterop.g_variant_get_child_value(parameters, 0);
        var results = GLibInterop.g_variant_get_child_value(parameters, 1);
        slot.Code = GLibInterop.g_variant_get_uint32(code);
        GLibInterop.g_variant_unref(code);
        slot.Results = results; // 已是新引用，等待方负责 unref
        slot.Done = true;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_connection != IntPtr.Zero)
                GLibInterop.g_object_unref(_connection);
            if (_context != IntPtr.Zero)
                GLibInterop.g_main_context_unref(_context);
        }
    }
}

using System;
using System.Runtime.InteropServices;

namespace Zhuoying.Platform.Linux;

/// <summary>
/// GLib / GIO(GDBus) 的 P/Invoke 集中地——Wayland 下与 xdg-desktop-portal
/// 通话所需（对应 X11 侧的 <see cref="X11Interop"/>）。
///
/// 为什么用 GDBus 而不是 libdbus-1：portal 的参数几乎全是 <c>a{sv}</c> 字典，
/// libdbus 得手写 message iter 逐层编解码，而 GVariant 有
/// <c>g_variant_new_parsed</c>——直接按字符串字面量构造，代码量差一个数量级。
///
/// NativeAOT 约束：全部 DllImport 直调；信号回调用
/// <c>[UnmanagedCallersOnly]</c> 静态函数指针，无委托封送。
/// </summary>
internal static partial class GLibInterop
{
    private const string LibGio = "libgio-2.0.so.0";
    private const string LibGLib = "libglib-2.0.so.0";
    private const string LibGObject = "libgobject-2.0.so.0";

    // ---------- GMainContext（信号靠它派发） ----------

    [LibraryImport(LibGLib, EntryPoint = "g_main_context_new")]
    internal static partial IntPtr g_main_context_new();

    [LibraryImport(LibGLib, EntryPoint = "g_main_context_push_thread_default")]
    internal static partial void g_main_context_push_thread_default(IntPtr context);

    [LibraryImport(LibGLib, EntryPoint = "g_main_context_pop_thread_default")]
    internal static partial void g_main_context_pop_thread_default(IntPtr context);

    [LibraryImport(LibGLib, EntryPoint = "g_main_context_unref")]
    internal static partial void g_main_context_unref(IntPtr context);

    /// <summary>派发一轮待处理事件。<paramref name="mayBlock"/> 为 true 时阻塞至有事件。</summary>
    [LibraryImport(LibGLib, EntryPoint = "g_main_context_iteration")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool g_main_context_iteration(IntPtr context,
        [MarshalAs(UnmanagedType.Bool)] bool mayBlock);

    // ---------- GError / 内存 ----------

    [StructLayout(LayoutKind.Sequential)]
    internal struct GError
    {
        public uint Domain;
        public int Code;
        public IntPtr Message;
    }

    [LibraryImport(LibGLib, EntryPoint = "g_error_free")]
    internal static partial void g_error_free(IntPtr error);

    [LibraryImport(LibGLib, EntryPoint = "g_free")]
    internal static partial void g_free(IntPtr mem);

    [LibraryImport(LibGObject, EntryPoint = "g_object_unref")]
    internal static partial void g_object_unref(IntPtr obj);

    /// <summary>取出并释放 GError 的消息文本。</summary>
    internal static string TakeErrorMessage(IntPtr error)
    {
        if (error == IntPtr.Zero)
            return "";
        var e = Marshal.PtrToStructure<GError>(error);
        var msg = Marshal.PtrToStringUTF8(e.Message) ?? "";
        g_error_free(error);
        return msg;
    }

    // ---------- GVariant ----------

    /// <summary>按 GVariant 文本格式构造值（末参数必须是 NULL 终止的 va_list，
    /// 我们不用占位符，全部拼进字符串，故传 NULL）。</summary>
    [LibraryImport(LibGLib, EntryPoint = "g_variant_new_parsed", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr g_variant_new_parsed(string format, IntPtr terminator);

    [LibraryImport(LibGLib, EntryPoint = "g_variant_ref_sink")]
    internal static partial IntPtr g_variant_ref_sink(IntPtr value);

    [LibraryImport(LibGLib, EntryPoint = "g_variant_ref")]
    internal static partial IntPtr g_variant_ref(IntPtr value);

    [LibraryImport(LibGLib, EntryPoint = "g_variant_unref")]
    internal static partial void g_variant_unref(IntPtr value);

    [LibraryImport(LibGLib, EntryPoint = "g_variant_n_children")]
    internal static partial nuint g_variant_n_children(IntPtr value);

    [LibraryImport(LibGLib, EntryPoint = "g_variant_get_child_value")]
    internal static partial IntPtr g_variant_get_child_value(IntPtr value, nuint index);

    [LibraryImport(LibGLib, EntryPoint = "g_variant_get_uint32")]
    internal static partial uint g_variant_get_uint32(IntPtr value);

    [LibraryImport(LibGLib, EntryPoint = "g_variant_get_int32")]
    internal static partial int g_variant_get_int32(IntPtr value);

    [LibraryImport(LibGLib, EntryPoint = "g_variant_get_string")]
    internal static partial IntPtr g_variant_get_string(IntPtr value, out nuint length);

    /// <summary>从 a{sv} 里取键值（返回已解包的值，需 unref）；无此键返回 Zero。</summary>
    [LibraryImport(LibGLib, EntryPoint = "g_variant_lookup_value", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr g_variant_lookup_value(IntPtr dictionary, string key,
        IntPtr expectedType);

    [LibraryImport(LibGLib, EntryPoint = "g_variant_print", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr g_variant_print(IntPtr value,
        [MarshalAs(UnmanagedType.Bool)] bool typeAnnotate);

    /// <summary>GVariant → 字符串（诊断用）。</summary>
    internal static string Print(IntPtr value)
    {
        if (value == IntPtr.Zero)
            return "(null)";
        var p = g_variant_print(value, false);
        var s = Marshal.PtrToStringUTF8(p) ?? "";
        g_free(p);
        return s;
    }

    /// <summary>读字符串型 GVariant。</summary>
    internal static string? GetString(IntPtr value)
    {
        if (value == IntPtr.Zero)
            return null;
        var p = g_variant_get_string(value, out _);
        return p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
    }

    // ---------- GDBus ----------

    internal const int G_BUS_TYPE_SESSION = 2;
    internal const int G_DBUS_CALL_FLAGS_NONE = 0;
    internal const int G_DBUS_SIGNAL_FLAGS_NONE = 0;

    [LibraryImport(LibGio, EntryPoint = "g_bus_get_sync")]
    internal static partial IntPtr g_bus_get_sync(int busType, IntPtr cancellable,
        out IntPtr error);

    [LibraryImport(LibGio, EntryPoint = "g_dbus_connection_get_unique_name")]
    internal static partial IntPtr g_dbus_connection_get_unique_name(IntPtr connection);

    [LibraryImport(LibGio, EntryPoint = "g_dbus_connection_call_sync",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr g_dbus_connection_call_sync(IntPtr connection,
        string? busName, string objectPath, string interfaceName, string methodName,
        IntPtr parameters, IntPtr replyType, int flags, int timeoutMsec,
        IntPtr cancellable, out IntPtr error);

    /// <summary>信号回调签名（C）：
    /// (connection, sender, path, interface, signal, parameters, userData)。</summary>
    [LibraryImport(LibGio, EntryPoint = "g_dbus_connection_signal_subscribe",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial uint g_dbus_connection_signal_subscribe(IntPtr connection,
        string? sender, string? interfaceName, string? member, string? objectPath,
        string? arg0, int flags, IntPtr callback, IntPtr userData, IntPtr userDataFreeFunc);

    [LibraryImport(LibGio, EntryPoint = "g_dbus_connection_signal_unsubscribe")]
    internal static partial void g_dbus_connection_signal_unsubscribe(IntPtr connection,
        uint subscriptionId);
}

using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;

namespace Zhuoying.Platform.Linux;

/// <summary>portal 交出的一路视频流。</summary>
internal sealed record PortalStream(uint NodeId, PixelRect Bounds);

/// <summary>
/// xdg-desktop-portal 的 ScreenCast 会话。Wayland 下这是**唯一**能拿到
/// 屏幕像素的合法通道：X11 抓屏对合成器内容全黑，
/// <c>org.gnome.Shell.Screenshot</c> 对普通客户端返回 AccessDenied。
///
/// 为什么不用更简单的 <c>portal.Screenshot</c>：它每次调用都弹授权框
/// （实测无人点击就一直挂着），热键截屏流完全不可用。ScreenCast 配
/// <c>persist_mode=2</c> 只在首次弹一次框，之后凭 restore_token 静默恢复。
///
/// 会话是长生命周期的：建立一次，之后所有截屏/录屏共用同一路 PipeWire 流。
/// </summary>
internal sealed class PortalScreenCast : IDisposable
{
    private const string Iface = "org.freedesktop.portal.ScreenCast";

    /// <summary>光标模式：2 = EMBEDDED，光标由合成器直接画进帧
    /// （省掉 X11 侧 XFixes 手工合成那一步）。</summary>
    private const uint CursorModeEmbedded = 2;

    /// <summary>源类型：1 = MONITOR。</summary>
    private const uint SourceTypeMonitor = 1;

    /// <summary>持久模式：2 = 一直有效，返回可复用的 restore_token。</summary>
    private const uint PersistModeExplicit = 2;

    private readonly object _lock = new();
    private PortalConnection? _connection;
    private string? _sessionHandle;
    private List<PortalStream> _streams = [];
    private bool _disposed;

    /// <summary>restore_token 的落盘位置。是本机凭据而非用户配置，
    /// 故不进 settings.json（同 Windows 侧"注册表即持久态"的取舍）。</summary>
    private static string TokenPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Zhuoying", "wayland-restore-token");

    /// <summary>当前会话的所有流；未建立会话时为空。</summary>
    internal IReadOnlyList<PortalStream> Streams
    {
        get { lock (_lock) return _streams; }
    }

    /// <summary>会话是否已就绪。</summary>
    internal bool IsActive
    {
        get { lock (_lock) return _sessionHandle != null && _streams.Count > 0; }
    }

    /// <summary>
    /// 确保会话已建立。首次（无 restore_token）会弹一次授权框并阻塞等待，
    /// 之后静默恢复。<paramref name="timeoutSeconds"/> 覆盖等待授权的时长。
    /// </summary>
    internal void EnsureSession(int timeoutSeconds = 120)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sessionHandle != null && _streams.Count > 0)
                return;

            _connection ??= new PortalConnection();
            var conn = _connection;

            // 1) CreateSession
            var createToken = PortalConnection.NewToken("Create");
            var sessionToken = PortalConnection.NewToken("Sess");
            var created = conn.CallWithRequest(Iface, "CreateSession",
                $"({{'handle_token': <'{createToken}'>, " +
                $"'session_handle_token': <'{sessionToken}'>}},)",
                createToken, 20);
            if (!created.Ok)
                throw new InvalidOperationException($"portal CreateSession 失败 code={created.Code}");
            var session = ReadString(created.Results, "session_handle")
                          ?? throw new InvalidOperationException("portal 未返回 session_handle");
            GLibInterop.g_variant_unref(created.Results);

            // 2) SelectSources（带上已有的 restore_token 以求免授权）
            var restoreToken = ReadSavedToken();
            var selectToken = PortalConnection.NewToken("Select");
            var restorePart = restoreToken is { Length: > 0 }
                ? $", 'restore_token': <'{Escape(restoreToken)}'>"
                : "";
            var selected = conn.CallWithRequest(Iface, "SelectSources",
                $"(objectpath '{session}', " +
                $"{{'handle_token': <'{selectToken}'>, " +
                $"'types': <uint32 {SourceTypeMonitor}>, " +
                $"'multiple': <false>, " +
                $"'cursor_mode': <uint32 {CursorModeEmbedded}>, " +
                $"'persist_mode': <uint32 {PersistModeExplicit}>{restorePart}}})",
                selectToken, 20);
            if (!selected.Ok)
                throw new InvalidOperationException($"portal SelectSources 失败 code={selected.Code}");
            GLibInterop.g_variant_unref(selected.Results);

            // 3) Start —— 无有效 restore_token 时在此弹授权框
            var startToken = PortalConnection.NewToken("Start");
            var started = conn.CallWithRequest(Iface, "Start",
                $"(objectpath '{session}', '', {{'handle_token': <'{startToken}'>}})",
                startToken, timeoutSeconds);
            if (started.Cancelled)
                throw new OperationCanceledException("用户拒绝了屏幕共享授权");
            if (!started.Ok)
                throw new InvalidOperationException($"portal Start 失败 code={started.Code}");

            try
            {
                if (ReadString(started.Results, "restore_token") is { Length: > 0 } tok)
                    SaveToken(tok);
                _streams = ReadStreams(started.Results);
                if (_streams.Count == 0)
                    throw new InvalidOperationException("portal 未返回任何视频流");
                _sessionHandle = session;
            }
            finally
            {
                GLibInterop.g_variant_unref(started.Results);
            }
        }
    }

    /// <summary>丢弃当前会话（流断开/授权被撤销后重建用）。</summary>
    internal void Reset()
    {
        lock (_lock)
        {
            if (_sessionHandle != null && _connection != null)
            {
                try
                {
                    var reply = _connection.Call("org.freedesktop.portal.Session", "Close",
                        $"(objectpath '{_sessionHandle}',)");
                    GLibInterop.g_variant_unref(reply);
                }
                catch (InvalidOperationException)
                {
                    // 会话可能已被合成器关闭，忽略
                }
            }
            _sessionHandle = null;
            _streams = [];
        }
    }

    /// <summary>解析 Start 结果里的 <c>streams: a(ua{sv})</c>。</summary>
    private static List<PortalStream> ReadStreams(IntPtr results)
    {
        var list = new List<PortalStream>();
        var streams = GLibInterop.g_variant_lookup_value(results, "streams", IntPtr.Zero);
        if (streams == IntPtr.Zero)
            return list;
        try
        {
            var count = (int)GLibInterop.g_variant_n_children(streams);
            for (var i = 0; i < count; i++)
            {
                var entry = GLibInterop.g_variant_get_child_value(streams, (nuint)i);
                try
                {
                    var nodeVar = GLibInterop.g_variant_get_child_value(entry, 0);
                    var nodeId = GLibInterop.g_variant_get_uint32(nodeVar);
                    GLibInterop.g_variant_unref(nodeVar);

                    var props = GLibInterop.g_variant_get_child_value(entry, 1);
                    try
                    {
                        // position 可能缺失（合成器不一定给全局坐标），缺省按原点
                        var (px, py) = ReadPair(props, "position");
                        var (sw, sh) = ReadPair(props, "size");
                        list.Add(new PortalStream(nodeId, new PixelRect(px, py, sw, sh)));
                    }
                    finally
                    {
                        GLibInterop.g_variant_unref(props);
                    }
                }
                finally
                {
                    GLibInterop.g_variant_unref(entry);
                }
            }
        }
        finally
        {
            GLibInterop.g_variant_unref(streams);
        }
        return list;
    }

    /// <summary>读 a{sv} 里的 (ii) 值对。</summary>
    private static (int X, int Y) ReadPair(IntPtr dict, string key)
    {
        var v = GLibInterop.g_variant_lookup_value(dict, key, IntPtr.Zero);
        if (v == IntPtr.Zero)
            return (0, 0);
        try
        {
            if (GLibInterop.g_variant_n_children(v) < 2)
                return (0, 0);
            var a = GLibInterop.g_variant_get_child_value(v, 0);
            var b = GLibInterop.g_variant_get_child_value(v, 1);
            var result = (GLibInterop.g_variant_get_int32(a), GLibInterop.g_variant_get_int32(b));
            GLibInterop.g_variant_unref(a);
            GLibInterop.g_variant_unref(b);
            return result;
        }
        finally
        {
            GLibInterop.g_variant_unref(v);
        }
    }

    private static string? ReadString(IntPtr dict, string key)
    {
        var v = GLibInterop.g_variant_lookup_value(dict, key, IntPtr.Zero);
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

    /// <summary>GVariant 文本格式里的单引号字符串转义。</summary>
    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

    private static string? ReadSavedToken()
    {
        try
        {
            return File.Exists(TokenPath) ? File.ReadAllText(TokenPath).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void SaveToken(string token)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TokenPath)!);
            File.WriteAllText(TokenPath, token);
        }
        catch (IOException)
        {
            // 写不进去只是下次要再授权一次，不影响本次
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        Reset();
        _connection?.Dispose();
        _connection = null;
    }
}

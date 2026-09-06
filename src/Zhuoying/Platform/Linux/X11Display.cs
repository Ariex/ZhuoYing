using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Zhuoying.Platform.Linux;

/// <summary>
/// 进程内共享的 X11 Display 连接。
///
/// Xlib 的 Display 不是线程安全的（即便 XInitThreads 也只保证内部锁，
/// 我们还有跨调用的 XGetImage→读像素→XDestroyImage 序列需要原子性），
/// 故所有访问一律在 <see cref="Gate"/> 下进行——对应 Windows 侧
/// DesktopDuplicator.Gate 与 MCP 服务线程互斥的同一考虑。
/// </summary>
internal static unsafe class X11Display
{
    /// <summary>
    /// 全局 X 错误处理器。Xlib 的默认处理器遇到协议错误会直接 <c>exit()</c>——
    /// 整个进程没了，连异常都抛不出来。实测在 Wayland 会话下对 Xwayland 的
    /// root window 调 XGetImage 会返回 <c>BadMatch</c>，若没有这个处理器
    /// 应用当场被 Xlib 终止（不是抓到黑屏那么温和）。
    ///
    /// 装上之后错误被吞掉、调用返回 NULL，由各调用点按空指针正常报错。
    /// 错误处理器是**进程级**的（不是每个 Display 一个），装一次即可。
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnXError(IntPtr display, IntPtr errorEvent) => 0;

    private static int _errorHandlerInstalled;

    /// <summary>幂等地装上全局 X 错误处理器（任何 X 调用之前都该先过这里）。</summary>
    internal static void EnsureErrorHandler()
    {
        if (Interlocked.Exchange(ref _errorHandlerInstalled, 1) != 0)
            return;
        X11Interop.XSetErrorHandler(
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)&OnXError);
    }

    /// <summary>访问 <see cref="Handle"/> 期间必须持有的锁。</summary>
    internal static readonly object Gate = new();

    private static IntPtr _display;
    private static bool _initialized;

    /// <summary>共享连接；无 X 服务器（纯 Wayland 无 Xwayland / 无头）时为 IntPtr.Zero。</summary>
    internal static IntPtr Handle
    {
        get
        {
            lock (Gate)
            {
                if (_initialized)
                    return _display;
                _initialized = true;
                X11Interop.XInitThreads();
                EnsureErrorHandler();
                _display = X11Interop.XOpenDisplay(null);
                return _display;
            }
        }
    }

    /// <summary>在锁内取连接并执行；无连接时返回 default。</summary>
    internal static T With<T>(Func<IntPtr, T> action)
    {
        var d = Handle; // 首次会在 Gate 内初始化
        lock (Gate)
            return d == IntPtr.Zero ? default! : action(d);
    }

    /// <summary>X 资源库里的 Xft.dpi（桌面统一缩放），读不到返回 96。</summary>
    internal static double ReadXftDpi()
    {
        // 环境变量优先：GDK_SCALE / QT_SCALE_FACTOR 是用户显式覆盖，权威性高于资源库
        var envScale = Environment.GetEnvironmentVariable("GDK_SCALE");
        if (double.TryParse(envScale, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var gs) && gs > 0)
            return gs * 96.0;

        return With(d =>
        {
            var rm = X11Interop.XResourceManagerString(d);
            if (rm == IntPtr.Zero)
                return 96.0;
            var s = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(rm);
            if (string.IsNullOrEmpty(s))
                return 96.0;
            foreach (var line in s.Split('\n'))
            {
                if (!line.StartsWith("Xft.dpi:", StringComparison.Ordinal))
                    continue;
                if (double.TryParse(line.AsSpan(8).Trim(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var dpi) && dpi > 0)
                    return dpi;
            }
            return 96.0;
        });
    }
}

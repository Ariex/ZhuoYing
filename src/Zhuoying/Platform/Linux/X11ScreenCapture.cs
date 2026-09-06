using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Zhuoying.Platform.Linux;

/// <summary>
/// X11 抓屏 + 显示器信息（对应 Windows 侧 <c>WindowsScreenCapture</c>）。
///
/// 坐标模型：X11 的 root window 即"虚拟屏"，其坐标就是物理像素、无 DPI 虚拟化，
/// 天然满足项目的"虚拟屏物理像素为唯一真源"约定（比 Windows 还省心）。
///
/// 重要限制：Wayland 会话下应用经 Xwayland 运行，root window 只含 X 客户端，
/// 合成器与原生 Wayland 窗口一律抓不到（实测 1920×1080 里 2073458 像素纯黑）。
/// 该会话下抓屏由 xdg-desktop-portal（PortalScreenCast，待实现） 接管，本类仅用于真 X11 会话
/// 与 Xvfb/Xephyr 自动化测试环境。见 docs/LINUX-PORT.md。
/// </summary>
public sealed class X11ScreenCapture : IScreenCapture
{
    public PixelPoint GetCursorPosition() => X11Display.With(d =>
    {
        var root = X11Interop.XDefaultRootWindow(d);
        return X11Interop.XQueryPointer(d, root, out _, out _,
            out var rx, out var ry, out _, out _, out _)
            ? new PixelPoint(rx, ry)
            : default;
    });

    public MonitorInfo GetMonitorAt(PixelPoint point)
    {
        var all = GetAllMonitors();
        if (all.Count == 0)
            return FallbackMonitor();

        foreach (var m in all)
            if (m.Bounds.Contains(point))
                return m;

        // 不在任何显示器内（显示器间空隙/越界）：取中心最近的一块，
        // 语义对齐 Win32 的 MONITOR_DEFAULTTONEAREST
        var best = all[0];
        var bestDist = double.MaxValue;
        foreach (var m in all)
        {
            var cx = m.Bounds.X + m.Bounds.Width / 2.0;
            var cy = m.Bounds.Y + m.Bounds.Height / 2.0;
            var dist = (cx - point.X) * (cx - point.X) + (cy - point.Y) * (cy - point.Y);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = m;
            }
        }
        return best;
    }

    public IReadOnlyList<MonitorInfo> GetAllMonitors()
    {
        var list = new List<MonitorInfo>();
        var scaling = X11Display.ReadXftDpi() / 96.0;
        if (scaling <= 0)
            scaling = 1.0;
        var workArea = ReadNetWorkArea();

        X11Display.With<object?>(d =>
        {
            var root = X11Interop.XDefaultRootWindow(d);
            var resPtr = X11Interop.XRRGetScreenResourcesCurrent(d, root);
            if (resPtr == IntPtr.Zero)
                return null;
            try
            {
                var res = Marshal.PtrToStructure<X11Interop.XRRScreenResources>(resPtr);
                var primaryOutput = X11Interop.XRRGetOutputPrimary(d, root);

                for (var i = 0; i < res.NCrtc; i++)
                {
                    var crtc = Marshal.ReadIntPtr(res.Crtcs, i * IntPtr.Size);
                    var ciPtr = X11Interop.XRRGetCrtcInfo(d, resPtr, crtc);
                    if (ciPtr == IntPtr.Zero)
                        continue;
                    try
                    {
                        var ci = Marshal.PtrToStructure<X11Interop.XRRCrtcInfo>(ciPtr);
                        // 未接显示器的 CRTC：宽高为 0 或无 output，跳过
                        if (ci.Width == 0 || ci.Height == 0 || ci.NOutput == 0)
                            continue;

                        var bounds = new PixelRect(ci.X, ci.Y, (int)ci.Width, (int)ci.Height);
                        var isPrimary = false;
                        for (var o = 0; o < ci.NOutput && !isPrimary; o++)
                            isPrimary = Marshal.ReadIntPtr(ci.Outputs, o * IntPtr.Size) == primaryOutput;

                        // _NET_WORKAREA 是全局单值（EWMH 未按屏拆分）：与本屏求交，
                        // 无交集（工作区不覆盖此屏）时退回整屏
                        var wa = workArea is { } w && w.Intersects(bounds)
                            ? w.Intersect(bounds)
                            : bounds;
                        list.Add(new MonitorInfo(bounds, wa, scaling, isPrimary));
                    }
                    finally
                    {
                        X11Interop.XRRFreeCrtcInfo(ciPtr);
                    }
                }
            }
            finally
            {
                X11Interop.XRRFreeScreenResources(resPtr);
            }
            return null;
        });

        // XRandR 不可用（Xvfb 默认无 RandR 扩展等）：退回单屏 = 整个 root
        if (list.Count == 0)
            list.Add(FallbackMonitor());
        // 无 primary 标记时把第一块当主屏，避免上层拿不到主显示器
        else if (!list.Exists(m => m.IsPrimary))
            list[0] = list[0] with { IsPrimary = true };

        return list;
    }

    /// <summary>整个 root window 当作单显示器（XRandR 缺失时的兜底）。</summary>
    private static MonitorInfo FallbackMonitor()
    {
        var scaling = X11Display.ReadXftDpi() / 96.0;
        var rect = X11Display.With(d =>
        {
            var screen = X11Interop.XDefaultScreen(d);
            return new PixelRect(0, 0,
                X11Interop.XDisplayWidth(d, screen), X11Interop.XDisplayHeight(d, screen));
        });
        if (rect.Width <= 0 || rect.Height <= 0)
            rect = new PixelRect(0, 0, 1920, 1080);
        return new MonitorInfo(rect, rect, scaling <= 0 ? 1.0 : scaling, true);
    }

    /// <summary>EWMH _NET_WORKAREA（去掉面板/停靠栏后的可用区，全局单值）。</summary>
    private static PixelRect? ReadNetWorkArea() => X11Display.With<PixelRect?>(d =>
    {
        var root = X11Interop.XDefaultRootWindow(d);
        var atom = X11Interop.XInternAtom(d, "_NET_WORKAREA", true);
        if (atom == IntPtr.Zero)
            return null;
        if (X11Interop.XGetWindowProperty(d, root, atom, 0, 4, false, IntPtr.Zero,
                out _, out var format, out var nItems, out _, out var prop) != 0
            || prop == IntPtr.Zero)
            return null;
        try
        {
            // 32 位格式的属性在 Xlib 里按 long 返回（64 位机上每项 8 字节）
            if (format != 32 || nItems < 4)
                return null;
            var v = new long[4];
            for (var i = 0; i < 4; i++)
                v[i] = Marshal.ReadIntPtr(prop, i * IntPtr.Size).ToInt64();
            if (v[2] <= 0 || v[3] <= 0)
                return null;
            return new PixelRect((int)v[0], (int)v[1], (int)v[2], (int)v[3]);
        }
        finally
        {
            X11Interop.XFree(prop);
        }
    });

    /// <summary>最近一次 CaptureRegion 实际使用的后端（诊断/自测用）。</summary>
    public static string LastBackendInfo { get; private set; } = "";

    public unsafe WriteableBitmap CaptureRegion(PixelRect region)
    {
        int w = region.Width, h = region.Height;
        if (w <= 0 || h <= 0)
            throw new ArgumentException($"非法抓取区域 {region}");

        var bitmap = new WriteableBitmap(
            new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var fb = bitmap.Lock();
        CaptureIntoCore(region, (byte*)fb.Address, fb.RowBytes);
        LastBackendInfo = "x11";
        return bitmap;
    }

    public unsafe void CaptureInto(PixelRect region, byte* dst, int dstStride) =>
        CaptureIntoCore(region, dst, dstStride);

    /// <summary>抓取 region 写入 dst（top-down BGRA，dst 指向 region 左上角像素）。</summary>
    internal static unsafe void CaptureIntoCore(PixelRect region, byte* dst, int dstStride)
    {
        var ok = X11Display.With(d =>
        {
            var root = X11Interop.XDefaultRootWindow(d);
            var img = X11Interop.XGetImage(d, root, region.X, region.Y,
                (uint)region.Width, (uint)region.Height, X11Interop.AllPlanes, X11Interop.ZPixmap);
            if (img == IntPtr.Zero)
                return false;
            try
            {
                CopyImage((X11Interop.XImage*)img, region.Width, region.Height, dst, dstStride);
                return true;
            }
            finally
            {
                X11Interop.XDestroyImage(img);
            }
        });
        if (!ok)
            throw new InvalidOperationException(
                $"XGetImage 抓取 {region} 失败（区域越界或无 X 连接）");
    }

    /// <summary>XImage → top-down BGRA。X11 的 24/32 位 ZPixmap 在小端机上
    /// 内存序即 BGRA，与目标一致，逐行直拷即可；alpha 通道不可靠，强制不透明。</summary>
    private static unsafe void CopyImage(X11Interop.XImage* img, int w, int h,
        byte* dst, int dstStride)
    {
        var src = (byte*)img->Data;
        var srcStride = img->BytesPerLine;
        var bpp = img->BitsPerPixel;

        if (bpp is 24 or 32 && img->RedMask == 0x00FF0000 && img->BlueMask == 0x000000FF)
        {
            for (var y = 0; y < h; y++)
            {
                var s = (uint*)(src + (long)y * srcStride);
                var dRow = (uint*)(dst + (long)y * dstStride);
                for (var x = 0; x < w; x++)
                    dRow[x] = s[x] | 0xFF000000u;
            }
            return;
        }

        // 非常规视觉（16 位、BGR 掩码顺序不同等）：按掩码逐像素解包
        var rMask = (uint)img->RedMask;
        var gMask = (uint)img->GreenMask;
        var bMask = (uint)img->BlueMask;
        var (rSh, rMax) = MaskInfo(rMask);
        var (gSh, gMax) = MaskInfo(gMask);
        var (bSh, bMax) = MaskInfo(bMask);
        var bytesPerPixel = Math.Max(1, bpp / 8);

        for (var y = 0; y < h; y++)
        {
            var s = src + (long)y * srcStride;
            var dRow = (uint*)(dst + (long)y * dstStride);
            for (var x = 0; x < w; x++)
            {
                uint px = bytesPerPixel switch
                {
                    4 => *(uint*)(s + x * 4),
                    2 => *(ushort*)(s + x * 2),
                    3 => (uint)(s[x * 3] | (s[x * 3 + 1] << 8) | (s[x * 3 + 2] << 16)),
                    _ => s[x],
                };
                var r = rMax == 0 ? 0u : (px & rMask) >> rSh;
                var g = gMax == 0 ? 0u : (px & gMask) >> gSh;
                var b = bMax == 0 ? 0u : (px & bMask) >> bSh;
                if (rMax is > 0 and < 255) r = r * 255 / rMax;
                if (gMax is > 0 and < 255) g = g * 255 / gMax;
                if (bMax is > 0 and < 255) b = b * 255 / bMax;
                dRow[x] = 0xFF000000u | (r << 16) | (g << 8) | b;
            }
        }
    }

    private static (int Shift, uint Max) MaskInfo(uint mask)
    {
        if (mask == 0)
            return (0, 0);
        var shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        return (shift, mask >> shift);
    }

    public IReadOnlyList<(string Title, PixelRect Rect)> GetVisibleWindowsWithTitles() =>
        GetVisibleWindows();

    public IReadOnlyList<PixelRect> GetVisibleWindowRects()
    {
        var list = new List<PixelRect>();
        foreach (var (_, rect) in GetVisibleWindows())
            list.Add(rect);
        return list;
    }

    /// <summary>
    /// 可见顶层窗口快照（自顶向下 Z 序，物理像素）。
    /// 走 EWMH _NET_CLIENT_LIST_STACKING（自底向上，此处反转），
    /// 矩形加上 _NET_FRAME_EXTENTS 的窗管装饰边——对齐 Windows 侧
    /// DWMWA_EXTENDED_FRAME_BOUNDS 的"用户看到的窗口边界"语义。
    /// </summary>
    internal static List<(string Title, PixelRect Rect)> GetVisibleWindows() =>
        X11Display.With(d =>
        {
            var result = new List<(string, PixelRect)>();
            var root = X11Interop.XDefaultRootWindow(d);
            var stacking = X11Interop.XInternAtom(d, "_NET_CLIENT_LIST_STACKING", true);
            if (stacking == IntPtr.Zero)
                return result;

            if (X11Interop.XGetWindowProperty(d, root, stacking, 0, 1024, false, IntPtr.Zero,
                    out _, out var format, out var nItems, out _, out var prop) != 0
                || prop == IntPtr.Zero)
                return result;

            var windows = new List<IntPtr>();
            try
            {
                if (format == 32)
                    for (var i = 0; i < (int)nItems; i++)
                        windows.Add(Marshal.ReadIntPtr(prop, i * IntPtr.Size));
            }
            finally
            {
                X11Interop.XFree(prop);
            }

            var frameAtom = X11Interop.XInternAtom(d, "_NET_FRAME_EXTENTS", true);
            var nameAtom = X11Interop.XInternAtom(d, "_NET_WM_NAME", true);
            var utf8Atom = X11Interop.XInternAtom(d, "UTF8_STRING", true);

            // _NET_CLIENT_LIST_STACKING 自底向上，反转得自顶向下（列表序即 Z 序）
            for (var i = windows.Count - 1; i >= 0; i--)
            {
                var win = windows[i];
                if (X11Interop.XGetWindowAttributes(d, win, out var attrs) == 0
                    || attrs.MapState != X11Interop.IsViewable)
                    continue;
                if (!X11Interop.XTranslateCoordinates(d, win, root, 0, 0,
                        out var absX, out var absY, out _))
                    continue;

                var rect = new PixelRect(absX, absY, attrs.Width, attrs.Height);
                if (frameAtom != IntPtr.Zero
                    && ReadCardinals(d, win, frameAtom, 4) is { } ext)
                {
                    // [left, right, top, bottom]
                    rect = new PixelRect(
                        rect.X - (int)ext[0], rect.Y - (int)ext[2],
                        rect.Width + (int)ext[0] + (int)ext[1],
                        rect.Height + (int)ext[2] + (int)ext[3]);
                }
                if (rect.Width < 16 || rect.Height < 16)
                    continue;

                result.Add((ReadUtf8Property(d, win, nameAtom, utf8Atom) ?? "", rect));
            }
            return result;
        }) ?? [];

    private static long[]? ReadCardinals(IntPtr d, IntPtr win, IntPtr atom, int count)
    {
        if (X11Interop.XGetWindowProperty(d, win, atom, 0, count, false, IntPtr.Zero,
                out _, out var format, out var nItems, out _, out var prop) != 0
            || prop == IntPtr.Zero)
            return null;
        try
        {
            if (format != 32 || (int)nItems < count)
                return null;
            var v = new long[count];
            for (var i = 0; i < count; i++)
                v[i] = Marshal.ReadIntPtr(prop, i * IntPtr.Size).ToInt64();
            return v;
        }
        finally
        {
            X11Interop.XFree(prop);
        }
    }

    private static string? ReadUtf8Property(IntPtr d, IntPtr win, IntPtr atom, IntPtr type)
    {
        if (atom == IntPtr.Zero)
            return null;
        if (X11Interop.XGetWindowProperty(d, win, atom, 0, 256, false, type,
                out _, out _, out var nItems, out _, out var prop) != 0
            || prop == IntPtr.Zero)
            return null;
        try
        {
            return nItems == 0 ? null : Marshal.PtrToStringUTF8(prop, (int)nItems);
        }
        finally
        {
            X11Interop.XFree(prop);
        }
    }
}

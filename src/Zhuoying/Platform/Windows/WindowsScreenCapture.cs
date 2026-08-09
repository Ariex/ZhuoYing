using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Zhuoying.Platform.Windows;

public sealed class WindowsScreenCapture : IScreenCapture
{
    public PixelPoint GetCursorPosition()
    {
        Win32.GetCursorPos(out var pt);
        return new PixelPoint(pt.X, pt.Y);
    }

    public MonitorInfo GetMonitorAt(PixelPoint point)
    {
        var hMonitor = Win32.MonitorFromPoint(
            new Win32.POINT { X = point.X, Y = point.Y }, Win32.MONITOR_DEFAULTTONEAREST);
        return ReadMonitor(hMonitor);
    }

    public IReadOnlyList<MonitorInfo> GetAllMonitors()
    {
        var list = new List<MonitorInfo>();
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMonitor, IntPtr _, ref Win32.RECT _, IntPtr _) =>
            {
                list.Add(ReadMonitor(hMonitor));
                return true;
            }, IntPtr.Zero);
        return list;
    }

    public IReadOnlyList<PixelRect> GetVisibleWindowRects()
    {
        var list = new List<PixelRect>();
        EnumVisibleWindows((_, rect) => list.Add(rect));
        return list;
    }

    /// <summary>带标题的可见窗口快照（Agent API 用，过滤规则同 GetVisibleWindowRects）。</summary>
    internal static List<(string Title, PixelRect Rect)> GetVisibleWindowsWithTitles()
    {
        var buffer = new char[512];
        var list = new List<(string, PixelRect)>();
        EnumVisibleWindows((hWnd, rect) =>
        {
            var len = Win32.GetWindowTextW(hWnd, buffer, buffer.Length);
            list.Add((new string(buffer, 0, Math.Max(0, len)), rect));
        });
        return list;
    }

    /// <summary>枚举可见顶层窗口（自顶向下 Z 序，DWM 扩展边界物理像素）。</summary>
    private static void EnumVisibleWindows(Action<IntPtr, PixelRect> visit)
    {
        Win32.EnumWindows((hWnd, _) =>
        {
            // EnumWindows 自顶向下：列表序即 Z 序（靠前在上）
            if (!Win32.IsWindowVisible(hWnd) || Win32.IsIconic(hWnd))
                return true;
            // 点击穿透的 overlay（游戏覆盖层/输入法悬浮层等）：鼠标事件永远
            // 不属于它，吸附命中它会框住幽灵全屏窗口，必须排除
            var exStyle = (long)Win32.GetWindowLongPtrW(hWnd, Win32.GWL_EXSTYLE);
            if ((exStyle & Win32.WS_EX_TRANSPARENT) != 0)
                return true;
            // 隐身窗口（UWP 挂起等）：可见标志位仍在但屏幕上没有，必须排除
            if (Win32.DwmGetWindowAttributeInt(
                    hWnd, Win32.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0
                && cloaked != 0)
                return true;
            // 扩展框架边界：去掉不可见的窗口阴影边（物理像素）
            if (Win32.DwmGetWindowAttribute(hWnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS,
                    out var rect, System.Runtime.InteropServices.Marshal.SizeOf<Win32.RECT>()) != 0)
                return true;
            var w = rect.Right - rect.Left;
            var h = rect.Bottom - rect.Top;
            if (w < 16 || h < 16)
                return true;
            visit(hWnd, new PixelRect(rect.Left, rect.Top, w, h));
            return true;
        }, IntPtr.Zero);
    }

    private static MonitorInfo ReadMonitor(IntPtr hMonitor)
    {
        var mi = new Win32.MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        if (!Win32.GetMonitorInfoW(hMonitor, ref mi))
            throw new InvalidOperationException("GetMonitorInfo 失败");

        double scaling = 1.0;
        if (Win32.GetDpiForMonitor(hMonitor, Win32.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0)
            scaling = dpiX / 96.0;

        return new MonitorInfo(
            ToPixelRect(mi.rcMonitor),
            ToPixelRect(mi.rcWork),
            scaling,
            (mi.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0);
    }

    private static PixelRect ToPixelRect(Win32.RECT r) =>
        new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    /// <summary>最近一次 CaptureRegion 实际使用的后端（诊断/自测用）：
    /// "dda" 全 DDA、"bitblt" 全回退、"dda+bitblt(n)" 混合（n 为回退矩形数）。</summary>
    public static string LastBackendInfo { get; private set; } = "";

    public unsafe WriteableBitmap CaptureRegion(PixelRect region)
    {
        int w = region.Width, h = region.Height;
        if (w <= 0 || h <= 0)
            throw new ArgumentException($"非法抓取区域 {region}");

        var bitmap = new WriteableBitmap(
            new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var fb = bitmap.Lock();
        var dst = (byte*)fb.Address;

        // DDA 优先（独占全屏/MPO/HDR 正确），任何层面失败回退 BitBlt：
        // 设备级异常 → 重建实例并整块回退；单输出失败/未覆盖区域 → 按矩形回退
        List<PixelRect> pending;
        try
        {
            pending = DesktopDuplicator.Shared.CaptureInto(region, dst, fb.RowBytes);
        }
        catch
        {
            DesktopDuplicator.Reset();
            pending = [region];
        }
        LastBackendInfo = pending.Count == 0 ? "dda"
            : pending.Count == 1 && pending[0] == region ? "bitblt"
            : $"dda+bitblt({pending.Count})";
        foreach (var r in pending)
            BitBltInto(r, dst + (long)(r.Y - region.Y) * fb.RowBytes + (long)(r.X - region.X) * 4,
                fb.RowBytes);
        return bitmap;
    }

    /// <summary>强制纯 BitBlt 抓取（DDA 像素一致性自测用，产线走 CaptureRegion）。</summary>
    public unsafe WriteableBitmap CaptureRegionBitBlt(PixelRect region)
    {
        int w = region.Width, h = region.Height;
        if (w <= 0 || h <= 0)
            throw new ArgumentException($"非法抓取区域 {region}");
        var bitmap = new WriteableBitmap(
            new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var fb = bitmap.Lock();
        BitBltInto(region, (byte*)fb.Address, fb.RowBytes);
        return bitmap;
    }

    /// <summary>BitBlt 抓取 region 并写入 dst（dst 指向目标位图中 region 左上角像素）。</summary>
    internal static unsafe void BitBltInto(PixelRect region, byte* dst, int dstStride)
    {
        int w = region.Width, h = region.Height;
        var screenDc = Win32.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            throw new InvalidOperationException("GetDC 失败");

        var memDc = IntPtr.Zero;
        var hBitmap = IntPtr.Zero;
        try
        {
            memDc = Win32.CreateCompatibleDC(screenDc);
            var bmi = new Win32.BITMAPINFOHEADER
            {
                biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // top-down，与 WriteableBitmap 行序一致
                biPlanes = 1,
                biBitCount = 32,
                biCompression = Win32.BI_RGB,
            };
            hBitmap = Win32.CreateDIBSection(memDc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
            if (hBitmap == IntPtr.Zero)
                throw new InvalidOperationException("CreateDIBSection 失败");

            var old = Win32.SelectObject(memDc, hBitmap);
            var ok = Win32.BitBlt(memDc, 0, 0, w, h, screenDc, region.X, region.Y,
                Win32.SRCCOPY | Win32.CAPTUREBLT);
            Win32.SelectObject(memDc, old);
            if (!ok)
                throw new InvalidOperationException("BitBlt 抓屏失败");

            var src = (byte*)bits;
            for (var y = 0; y < h; y++)
            {
                var s = (uint*)(src + (long)y * w * 4);
                var d = (uint*)(dst + (long)y * dstStride);
                // GDI 输出的 alpha 通道不可靠，强制置为不透明
                for (var x = 0; x < w; x++)
                    d[x] = s[x] | 0xFF000000u;
            }
        }
        finally
        {
            if (hBitmap != IntPtr.Zero) Win32.DeleteObject(hBitmap);
            if (memDc != IntPtr.Zero) Win32.DeleteDC(memDc);
            Win32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}

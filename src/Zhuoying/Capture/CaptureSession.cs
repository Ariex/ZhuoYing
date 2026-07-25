using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media.Imaging;
using Zhuoying.Platform;

namespace Zhuoying.Capture;

/// <summary>
/// 一次截屏会话：整个虚拟屏幕的冻结帧 + 覆盖全部显示器的单一遮罩窗口 + 选区状态；
/// 负责最终输出（按物理像素从全帧裁剪）。
/// </summary>
public sealed class CaptureSession
{
    private readonly IClipboardImage _clipboard;
    private readonly WriteableBitmap _fullFrame;
    private readonly PixelRect _virtualBounds;
    private readonly IReadOnlyList<MonitorInfo> _monitors;
    private readonly MonitorInfo _cursorMonitor;
    private readonly SelectionController _selection;
    private readonly CaptureOverlayWindow _window;
    private bool _closing;
    private bool _opened;

    /// <summary>会话结束（复制完成或取消），窗口已关闭、资源已释放。</summary>
    public event Action? Finished;

    public CaptureSession(
        IReadOnlyList<MonitorInfo> monitors, MonitorInfo cursorMonitor,
        WriteableBitmap fullFrame, PixelRect virtualBounds, IClipboardImage clipboard)
    {
        _clipboard = clipboard;
        _fullFrame = fullFrame;
        _virtualBounds = virtualBounds;
        _monitors = monitors;
        _cursorMonitor = cursorMonitor;

        // 默认选区 = 鼠标所在屏幕全屏（REQUIREMENTS §4.3）
        _selection = new SelectionController(virtualBounds, cursorMonitor.Bounds);
        _selection.DragStarted += () => _window!.HideToolbar();
        _selection.DragCompleted += _ => UpdateToolbar();

        _window = new CaptureOverlayWindow(fullFrame, virtualBounds, _selection, Copy, CloseAll);
        _window.Opened += (_, _) => { _opened = true; UpdateToolbar(); };
        _window.GeometryChanged += () => { if (_opened) UpdateToolbar(); };
        _window.Closed += (_, _) => CloseAll();
    }

    public void Show()
    {
        _window.Show();
        _window.Activate();
        _window.Focus();
    }

    /// <summary>测试钩子：直接设定选区（虚拟屏幕物理像素）并复制。</summary>
    public void TestCopy(PixelRect physicalRect)
    {
        _selection.SetSelection(physicalRect);
        Copy();
    }

    /// <summary>测试钩子：只设定选区不复制（供视觉验证选区渲染与工具条定位）。</summary>
    public void TestSelect(PixelRect physicalRect) => _selection.SetSelection(physicalRect);

    private void Copy()
    {
        var sel = _selection.Selection;
        if (sel.Width <= 0 || sel.Height <= 0)
            return;
        try
        {
            // 输出 DPI 取选区覆盖面积最大的显示器（跨屏混合 DPI 时无唯一正确答案）
            var dpi = 96.0 * DpiOwner(sel).Scaling;
            var cropped = BitmapUtil.Crop(_fullFrame, ToFrameRect(sel), new Vector(dpi, dpi));
            // 先关遮罩再写剪贴板：进程持有高 DPI 全屏窗口时写入，
            // 剪贴板位图会被打上高 DPI 虚拟化标签，导致部分粘贴目标缩小图像
            CloseAll();
            try
            {
                _clipboard.SetImage(cropped);
            }
            finally
            {
                cropped.Dispose();
            }
        }
        catch (Exception)
        {
            // 输出失败不崩溃；托盘气泡提示留到生命周期完善阶段
            CloseAll();
        }
    }

    private MonitorInfo DpiOwner(PixelRect sel)
    {
        var best = _cursorMonitor;
        long bestArea = 0;
        foreach (var monitor in _monitors)
        {
            var overlap = sel.Intersect(monitor.Bounds);
            var area = (long)overlap.Width * overlap.Height;
            if (area > bestArea)
            {
                bestArea = area;
                best = monitor;
            }
        }
        return best;
    }

    /// <summary>虚拟屏幕坐标 → 全帧位图坐标（平移掉虚拟屏幕原点，可能为负）。</summary>
    private PixelRect ToFrameRect(PixelRect r) =>
        new(r.X - _virtualBounds.X, r.Y - _virtualBounds.Y, r.Width, r.Height);

    private void UpdateToolbar()
    {
        var sel = _selection.Selection;
        if (_closing || sel.Width <= 0 || sel.Height <= 0)
        {
            _window.HideToolbar();
            return;
        }
        _window.ShowToolbarFor(sel);
    }

    private void CloseAll()
    {
        if (_closing)
            return;
        _closing = true;
        _window.Close();
        _fullFrame.Dispose();
        Finished?.Invoke();
    }
}

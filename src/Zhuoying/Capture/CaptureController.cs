using System;
using System.Linq;
using Avalonia;
using Zhuoying.Platform;

namespace Zhuoying.Capture;

/// <summary>
/// 截屏触发入口：一次触发 = 冻结整个虚拟屏幕 → 创建跨屏会话（每显示器一个遮罩窗口）。
/// </summary>
public sealed class CaptureController
{
    private readonly IScreenCapture _screenCapture;
    private readonly IClipboardImage _clipboard;
    private CaptureSession? _session;

    public CaptureController(IScreenCapture screenCapture, IClipboardImage clipboard)
    {
        _screenCapture = screenCapture;
        _clipboard = clipboard;
    }

    public void StartCapture()
    {
        if (_session != null)
            return; // 会话进行中，忽略再次触发

        var monitors = _screenCapture.GetAllMonitors();
        if (monitors.Count == 0)
            return;
        var cursor = _screenCapture.GetCursorPosition();
        var cursorMonitor = monitors.FirstOrDefault(m => m.Bounds.Contains(cursor)) ?? monitors[0];

        // 虚拟屏幕 = 所有显示器包围盒（副屏在主屏左/上时原点为负）
        var virtualBounds = monitors[0].Bounds;
        foreach (var m in monitors.Skip(1))
        {
            var left = Math.Min(virtualBounds.X, m.Bounds.X);
            var top = Math.Min(virtualBounds.Y, m.Bounds.Y);
            var right = Math.Max(virtualBounds.Right, m.Bounds.Right);
            var bottom = Math.Max(virtualBounds.Bottom, m.Bounds.Bottom);
            virtualBounds = new PixelRect(left, top, right - left, bottom - top);
        }

        // 先冻结再显示遮罩，后续操作全部基于冻结帧（REQUIREMENTS §4.1）；
        // 一次 BitBlt 抓整个虚拟屏幕，保证各屏画面同一时刻
        var frame = _screenCapture.CaptureRegion(virtualBounds);

        var session = new CaptureSession(monitors, cursorMonitor, frame, virtualBounds, _clipboard);
        session.Finished += () => _session = null;
        _session = session;
        session.Show();
    }

    /// <summary>测试钩子：在进行中的会话上直接设定选区（虚拟屏幕物理像素）并复制。</summary>
    public void TestCopy(PixelRect physicalRect) => _session?.TestCopy(physicalRect);

    /// <summary>测试钩子：只设定选区不复制。</summary>
    public void TestSelect(PixelRect physicalRect) => _session?.TestSelect(physicalRect);
}

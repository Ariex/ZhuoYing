using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Zhuoying.Platform;

namespace Zhuoying.Capture;

/// <summary>
/// 截屏触发入口：一次触发 = 冻结整个虚拟屏幕 → 创建覆盖所有显示器的截屏会话。
/// </summary>
public sealed class CaptureController
{
    private readonly IScreenCapture _screenCapture;
    private readonly IClipboardImage _clipboard;
    private readonly Func<EditorOptions> _options;
    private CaptureSession? _session;

    /// <summary>一次截屏会话结束（复制/保存/取消后；样式记忆落盘等挂此处）。</summary>
    public event Action? SessionFinished;

    public CaptureController(
        IScreenCapture screenCapture, IClipboardImage clipboard,
        Func<EditorOptions> options)
    {
        _screenCapture = screenCapture;
        _clipboard = clipboard;
        _options = options;
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
        // 同一时刻快照可见窗口（遮罩窗口尚未创建，不会混入）：供选区"窗口吸附"
        var windowRects = _screenCapture.GetVisibleWindowRects();

        var session = new CaptureSession(
            monitors, cursorMonitor, frame, virtualBounds, _clipboard, _options());
        session.SetWindowRects(windowRects);
        session.Finished += () =>
        {
            _session = null;
            SessionFinished?.Invoke();
        };
        _session = session;
        session.Show();
    }

    /// <summary>测试钩子：在进行中的会话上直接设定选区（虚拟屏幕物理像素）并复制。</summary>
    public void TestCopy(PixelRect physicalRect) => _session?.TestCopy(physicalRect);

    /// <summary>测试钩子：只设定选区不复制。</summary>
    public void TestSelect(PixelRect physicalRect) => _session?.TestSelect(physicalRect);

    /// <summary>测试钩子：设定选区并保存到默认目录。</summary>
    public void TestSave(PixelRect physicalRect) => _session?.TestSave(physicalRect);

    /// <summary>测试钩子：添加一个文字标注。</summary>
    public void TestAddText(
        PixelRect bounds, string text, double fontSize, double rotationDeg,
        bool boxEnabled, double strokeThickness, double opacity = 100) =>
        _session?.TestAddText(bounds, text, fontSize, rotationDeg, boxEnabled, strokeThickness, opacity);

    /// <summary>测试钩子：添加一条线/箭头标注。</summary>
    public void TestAddLine(
        IReadOnlyList<PixelPoint> points, Annotations.LineCapKind startCap, Annotations.LineCapKind endCap,
        double startThickness, double endThickness, bool spline, int lineStyleIndex, double opacity) =>
        _session?.TestAddLine(points, startCap, endCap, startThickness, endThickness,
            spline, lineStyleIndex, opacity);

    /// <summary>测试钩子：添加一条橡皮擦除笔迹。</summary>
    public void TestAddEraser(IReadOnlyList<PixelPoint> points, double thickness) =>
        _session?.TestAddEraser(points, thickness);

    /// <summary>测试钩子：添加一个图章。</summary>
    public void TestAddStamp(
        PixelRect bounds, string sourcePath, double rotationDeg, double outlineWidth, double opacity) =>
        _session?.TestAddStamp(bounds, sourcePath, rotationDeg, outlineWidth, opacity);

    /// <summary>测试钩子：添加一条画笔笔迹。</summary>
    public void TestAddPen(
        IReadOnlyList<PixelPoint> points, double thickness, bool highlight, Color? color) =>
        _session?.TestAddPen(points, thickness, highlight, color);

    /// <summary>测试钩子：添加一个区域模糊元素。</summary>
    public void TestAddMosaic(PixelRect bounds, bool blur, double amount, double rotationDeg) =>
        _session?.TestAddMosaic(bounds, blur, amount, rotationDeg);

    /// <summary>测试钩子：添加一个编号徽章。</summary>
    public void TestAddNumber(
        PixelPoint center, int value, Annotations.NumberKind kind,
        double diameter, bool hollow, Color? color) =>
        _session?.TestAddNumber(center, value, kind, diameter, hollow, color);

    /// <summary>测试钩子：添加一个形状标注。</summary>
    public void TestAddShape(
        PixelRect bounds, double radiusPercent, bool filled, double thickness,
        double rotationDeg = 0, int lineStyleIndex = 0, double opacity = 100, bool invert = false) =>
        _session?.TestAddShape(
            bounds, radiusPercent, filled, thickness, rotationDeg, lineStyleIndex, opacity, invert);
}

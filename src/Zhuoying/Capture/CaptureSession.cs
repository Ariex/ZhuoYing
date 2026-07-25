using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Zhuoying.Annotations;
using Zhuoying.Platform;

namespace Zhuoying.Capture;

/// <summary>
/// 一次截屏会话：整个虚拟屏幕的冻结帧 + 覆盖全部显示器的单一遮罩窗口 +
/// 选区状态 + 标注编辑器；输出时把标注合成到按物理像素裁剪的图像上。
/// </summary>
public sealed class CaptureSession
{
    private readonly IClipboardImage _clipboard;
    private readonly WriteableBitmap _fullFrame;
    private readonly PixelRect _virtualBounds;
    private readonly IReadOnlyList<MonitorInfo> _monitors;
    private readonly MonitorInfo _cursorMonitor;
    private readonly SelectionController _selection;
    private readonly AnnotationModel _annotations;
    private readonly EditorState _editor;
    private readonly CaptureOverlayWindow _window;
    private bool _closing;
    private bool _opened;

    /// <summary>会话结束（复制完成或取消），窗口已关闭、资源已释放。</summary>
    public event Action? Finished;

    public CaptureSession(
        IReadOnlyList<MonitorInfo> monitors, MonitorInfo cursorMonitor,
        WriteableBitmap fullFrame, PixelRect virtualBounds,
        IClipboardImage clipboard, IReadOnlyList<Color> presetColors)
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

        _annotations = new AnnotationModel();
        _editor = new EditorState(_annotations, presetColors);

        _window = new CaptureOverlayWindow(
            fullFrame, virtualBounds, _selection, _editor, Copy, CloseAll);
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

    /// <summary>测试钩子：添加一个形状元素并选中（免注入验证标注渲染与输出合成）。</summary>
    public void TestAddShape(
        PixelRect bounds, double radiusPercent, bool filled, double thickness,
        double rotationDeg = 0, int lineStyleIndex = 0, double opacity = 100)
    {
        var el = new ShapeElement
        {
            Bounds = bounds,
            Style = _editor.CurrentStyle with
            {
                CornerRadiusPercent = radiusPercent,
                Filled = filled,
                Thickness = thickness,
                RotationDeg = rotationDeg,
                LineStyleIndex = lineStyleIndex,
                Opacity = opacity,
            },
        };
        _annotations.Elements.Add(el);
        _annotations.Push(new AddElementCommand(_annotations, el));
        _annotations.Selected = el;
    }

    private void Copy()
    {
        var sel = _selection.Selection;
        if (sel.Width <= 0 || sel.Height <= 0)
            return;
        try
        {
            // 输出 DPI 取选区覆盖面积最大的显示器（跨屏混合 DPI 时无唯一正确答案）
            var dpi = 96.0 * DpiOwner(sel).Scaling;
            var output = ComposeOutput(sel, new Vector(dpi, dpi));
            // 先关遮罩再写剪贴板：进程持有高 DPI 全屏窗口时写入，
            // 剪贴板位图会被打上高 DPI 虚拟化标签，导致部分粘贴目标缩小图像
            CloseAll();
            try
            {
                _clipboard.SetImage(output);
            }
            finally
            {
                output.Dispose();
            }
        }
        catch (Exception)
        {
            // 输出失败不崩溃；托盘气泡提示留到生命周期完善阶段
            CloseAll();
        }
    }

    /// <summary>底图裁剪 + 标注合成，输出物理分辨率位图。无标注时直接走像素裁剪。</summary>
    private WriteableBitmap ComposeOutput(PixelRect sel, Vector dpi)
    {
        if (_annotations.Elements.Count == 0)
            return BitmapUtil.Crop(_fullFrame, ToFrameRect(sel), dpi);

        // RenderTargetBitmap 以 96 DPI 创建 → 1 DIP = 1 像素，绘制坐标即物理像素
        using var rtb = new RenderTargetBitmap(new PixelSize(sel.Width, sel.Height), new Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext())
        {
            var src = ToFrameRect(sel);
            ctx.DrawImage(_fullFrame,
                new Rect(src.X, src.Y, src.Width, src.Height),
                new Rect(0, 0, sel.Width, sel.Height));
            foreach (var el in _annotations.Elements)
            {
                el.Render(ctx, r => new Rect(
                    r.X - sel.X, r.Y - sel.Y, r.Width, r.Height), 1.0);
            }
        }

        var result = new WriteableBitmap(
            new PixelSize(sel.Width, sel.Height), dpi, PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var fb = result.Lock();
        rtb.CopyPixels(new PixelRect(0, 0, sel.Width, sel.Height),
            fb.Address, fb.RowBytes * sel.Height, fb.RowBytes);
        return result;
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

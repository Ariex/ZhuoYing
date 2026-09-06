using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
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
    private readonly string _savePath;
    private bool _closing;
    private bool _opened;

    /// <summary>会话结束（复制完成或取消），窗口已关闭、资源已释放。</summary>
    public event Action? Finished;

    public CaptureSession(
        IReadOnlyList<MonitorInfo> monitors, MonitorInfo cursorMonitor,
        WriteableBitmap fullFrame, PixelRect virtualBounds,
        IClipboardImage clipboard, EditorOptions options)
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
        _editor = new EditorState(_annotations, options);
        _editor.SetBackground(fullFrame, virtualBounds.TopLeft); // 区域模糊的采样来源
        _savePath = options.SavePath;

        _window = new CaptureOverlayWindow(
            fullFrame, virtualBounds, _selection, _editor, Copy, Save, SaveAs, Pin, Record, CloseAll);
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

    /// <summary>注入抓屏瞬间的可见窗口快照（选区窗口吸附用）。</summary>
    public void SetWindowRects(IReadOnlyList<PixelRect> rects) => _selection.SetWindowRects(rects);

    /// <summary>测试钩子：直接设定选区（虚拟屏幕物理像素）并复制。</summary>
    public void TestCopy(PixelRect physicalRect)
    {
        _selection.SetSelection(physicalRect);
        Copy();
    }

    /// <summary>测试钩子：只设定选区不复制（供视觉验证选区渲染与工具条定位）。</summary>
    public void TestSelect(PixelRect physicalRect) => _selection.SetSelection(physicalRect);

    /// <summary>测试钩子：设定选区并保存到默认目录。</summary>
    public void TestSave(PixelRect physicalRect)
    {
        _selection.SetSelection(physicalRect);
        Save();
    }

    /// <summary>测试钩子：设定选区并贴图。</summary>
    public void TestPin(PixelRect physicalRect)
    {
        _selection.SetSelection(physicalRect);
        Pin();
    }

    /// <summary>测试钩子：添加一条线/箭头元素（免注入验证线渲染与输出合成）。</summary>
    public void TestAddLine(
        IReadOnlyList<PixelPoint> points, LineCapKind startCap, LineCapKind endCap,
        double startThickness, double endThickness, bool spline, int lineStyleIndex, double opacity)
    {
        var el = new LineElement
        {
            // 两点 + 有末端头视为箭头工具产物（决定样式槽与"弧线"开关的显隐）
            IsArrowTool = points.Count == 2 && endCap != LineCapKind.None,
            Style = new LineStyle
            {
                Color = _editor.CurrentLineStyle.Color,
                StartCap = startCap,
                EndCap = endCap,
                StartThickness = startThickness,
                EndThickness = endThickness,
                Spline = spline,
                LineStyleIndex = lineStyleIndex,
                Opacity = opacity,
            },
        };
        el.Points.AddRange(points);
        _annotations.Elements.Add(el);
        _annotations.Push(new AddElementCommand(_annotations, el));
        _annotations.Selected = el;
    }

    /// <summary>测试钩子：添加一个文字元素并选中（免注入验证文字渲染与输出合成）。</summary>
    public void TestAddText(
        PixelRect bounds, string text, double fontSize, double rotationDeg,
        bool boxEnabled, double strokeThickness, double opacity = 100)
    {
        var el = new TextElement
        {
            Bounds = bounds,
            Text = text,
            Style = _editor.CurrentTextStyle with
            {
                FontSize = fontSize,
                RotationDeg = rotationDeg,
                BoxEnabled = boxEnabled,
                StrokeThickness = strokeThickness,
                Opacity = opacity,
            },
        };
        _annotations.Elements.Add(el);
        _annotations.Push(new AddElementCommand(_annotations, el));
        _annotations.Selected = el;
    }

    /// <summary>测试钩子：添加一条橡皮擦除笔迹。</summary>
    public void TestAddEraser(IReadOnlyList<PixelPoint> points, double thickness)
    {
        var el = new EraserElement { Thickness = thickness };
        el.Points.AddRange(points);
        _annotations.Elements.Add(el);
        _annotations.Push(new AddElementCommand(_annotations, el));
    }

    /// <summary>测试钩子：添加一个图章并选中。</summary>
    public void TestAddStamp(
        PixelRect bounds, string sourcePath, double rotationDeg, double outlineWidth, double opacity)
    {
        var el = new StampElement
        {
            Bounds = bounds,
            SourcePath = sourcePath,
            Style = _editor.CurrentStampStyle with
            {
                RotationDeg = rotationDeg,
                OutlineEnabled = outlineWidth > 0,
                OutlineWidth = Math.Max(1, outlineWidth),
                Opacity = opacity,
            },
        };
        _annotations.Elements.Add(el);
        _annotations.Push(new AddElementCommand(_annotations, el));
        _annotations.Selected = el;
    }

    /// <summary>测试钩子：添加一条画笔笔迹并选中。</summary>
    public void TestAddPen(
        IReadOnlyList<PixelPoint> points, double thickness, bool highlight, Color? color)
    {
        var el = new PenElement
        {
            Frame = _fullFrame,
            FrameOrigin = _virtualBounds.TopLeft,
            Style = _editor.CurrentPenStyle with
            {
                Thickness = thickness,
                Highlight = highlight,
                Color = color ?? _editor.CurrentPenStyle.Color,
            },
        };
        el.Points.AddRange(points);
        _annotations.Elements.Add(el);
        _annotations.Push(new AddElementCommand(_annotations, el));
        _annotations.Selected = el;
    }

    /// <summary>测试钩子：添加一个区域模糊元素并选中（插入底层前缀组）。</summary>
    public void TestAddMosaic(PixelRect bounds, bool blur, double amount, double rotationDeg)
    {
        var el = new MosaicElement
        {
            Bounds = bounds,
            Frame = _fullFrame,
            FrameOrigin = _virtualBounds.TopLeft,
            Seed = 12345, // 测试固定种子，截图可复现
            Style = new MosaicStyle
            {
                Mode = blur ? MosaicMode.Blur : MosaicMode.Pixelate,
                BlockSize = blur ? 10 : amount,
                BlurRadius = blur ? amount : 10,
                RotationDeg = rotationDeg,
            },
        };
        var at = 0;
        while (at < _annotations.Elements.Count && _annotations.Elements[at] is MosaicElement)
            at++;
        _annotations.Elements.Insert(at, el);
        _annotations.Push(new AddElementCommand(_annotations, el, at));
        _annotations.Selected = el;
    }

    /// <summary>测试钩子：添加一个编号徽章并选中（免注入验证编号渲染与四角按钮）。</summary>
    public void TestAddNumber(
        PixelPoint center, int value, NumberKind kind, double diameter, bool hollow, Color? color)
    {
        var el = new NumberElement
        {
            Center = center,
            Value = value,
            Style = _editor.CurrentNumberStyle with
            {
                Kind = kind,
                Diameter = diameter,
                Hollow = hollow,
                Color = color ?? _editor.CurrentNumberStyle.Color,
            },
        };
        _annotations.Elements.Add(el);
        _annotations.Push(new AddElementCommand(_annotations, el));
        _annotations.Selected = el;
    }

    /// <summary>测试钩子：添加一个形状元素并选中（免注入验证标注渲染与输出合成）。</summary>
    public void TestAddShape(
        PixelRect bounds, double radiusPercent, bool filled, double thickness,
        double rotationDeg = 0, int lineStyleIndex = 0, double opacity = 100, bool invert = false)
    {
        var el = new ShapeElement
        {
            Bounds = bounds,
            Frame = _fullFrame,
            FrameOrigin = _virtualBounds.TopLeft,
            Style = _editor.CurrentStyle with
            {
                CornerRadiusPercent = radiusPercent,
                Filled = filled,
                Thickness = thickness,
                RotationDeg = rotationDeg,
                LineStyleIndex = lineStyleIndex,
                Opacity = opacity,
                Color = invert ? InvertPaint.Sentinel : _editor.CurrentStyle.Color,
            },
        };
        _annotations.Elements.Add(el);
        _annotations.Push(new AddElementCommand(_annotations, el));
        _annotations.Selected = el;
    }

    /// <summary>录制（F4 GIF / F5 MP4）：关闭冻结帧会话，对选区开始活屏录制
    ///（24fps 采样；不含标注——录屏是活画面，冻结帧标注无意义）。</summary>
    private void Record(RecordFormat format)
    {
        var sel = _selection.Selection;
        if (sel.Width <= 0 || sel.Height <= 0)
            return;
        var saveDir = _savePath;
        CloseAll();
        try
        {
            RecordingController.Start(sel, 24, saveDir, format);
        }
        catch (Exception ex)
        {
            NotificationToast.Show($"无法开始录制：{ex.Message}");
        }
    }

    /// <summary>贴图（F3）：选区合成后钉成置顶贴图窗（原位、物理像素 1:1），结束会话。</summary>
    private void Pin()
    {
        var sel = _selection.Selection;
        if (sel.Width <= 0 || sel.Height <= 0)
            return;
        try
        {
            // 显示用位图必须 96 DPI（TROUBLESHOOTING §2：非 96 DPI 位图 Avalonia
            // 渲染错误放大）；来源 DPI 单独传给贴图窗，复制/保存时再标记
            var dpi = 96.0 * DpiOwner(sel).Scaling;
            var output = ComposeOutput(sel, new Vector(96, 96));
            CloseAll();
            new PinWindow(output, sel.TopLeft, dpi, _clipboard).Show();
        }
        catch (Exception)
        {
            CloseAll();
        }
    }

    /// <summary>保存：自动落到设定目录（时间戳文件名，重名加序号），完成后结束会话。</summary>
    private void Save()
    {
        var sel = _selection.Selection;
        if (sel.Width <= 0 || sel.Height <= 0)
            return;
        try
        {
            System.IO.Directory.CreateDirectory(_savePath);
            var path = UniquePath(_savePath, $"捉影_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            WritePng(sel, path);
            CloseAll();
            NotificationToast.Show($"已保存：{path}");
        }
        catch (Exception)
        {
            CloseAll();
            NotificationToast.Show($"保存失败：无法写入 {_savePath}");
        }
    }

    /// <summary>另存为：用户指定目录与文件名；取消则回到会话继续编辑。</summary>
    private async void SaveAs()
    {
        var sel = _selection.Selection;
        if (sel.Width <= 0 || sel.Height <= 0)
            return;
        try
        {
            var file = await _window.StorageProvider.SaveFilePickerAsync(
                new Avalonia.Platform.Storage.FilePickerSaveOptions
                {
                    Title = "另存为",
                    SuggestedFileName = $"捉影_{DateTime.Now:yyyyMMdd_HHmmss}",
                    DefaultExtension = "png",
                    FileTypeChoices =
                    [
                        new Avalonia.Platform.Storage.FilePickerFileType("PNG 图片")
                        {
                            Patterns = ["*.png"],
                        },
                    ],
                });
            if (file?.TryGetLocalPath() is not { } path)
                return; // 用户取消：回会话
            WritePng(sel, path);
            CloseAll();
            NotificationToast.Show($"已保存：{path}");
        }
        catch (Exception)
        {
            CloseAll();
            NotificationToast.Show("另存为失败：目标位置无法写入");
        }
    }

    /// <summary>合成输出并写 PNG（pHYs 块携带来源屏幕 DPI）。</summary>
    private void WritePng(PixelRect sel, string path)
    {
        var dpi = 96.0 * DpiOwner(sel).Scaling;
        var output = ComposeOutput(sel, new Vector(dpi, dpi));
        try
        {
            using var ms = new System.IO.MemoryStream();
            output.Save(ms);
            System.IO.File.WriteAllBytes(path,
                Zhuoying.Platform.PngDpiWriter.WithDpi(ms.ToArray(), dpi, dpi));
        }
        finally
        {
            output.Dispose();
        }
    }

    private static string UniquePath(string dir, string fileName)
    {
        var path = System.IO.Path.Combine(dir, fileName);
        var name = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var ext = System.IO.Path.GetExtension(fileName);
        for (var i = 2; System.IO.File.Exists(path); i++)
            path = System.IO.Path.Combine(dir, $"{name}-{i}{ext}");
        return path;
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
            Point ToOut(Point p) => new(p.X - sel.X, p.Y - sel.Y);
            var eraserClip = EraserElement.BuildClip(_annotations.Elements, ToOut, 1.0);
            foreach (var el in _annotations.Elements)
            {
                if (el is EraserElement)
                    continue;
                if (el is PenElement && eraserClip != null)
                {
                    using (ctx.PushGeometryClip(eraserClip))
                        el.Render(ctx, ToOut, 1.0);
                }
                else
                {
                    el.Render(ctx, ToOut, 1.0);
                }
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

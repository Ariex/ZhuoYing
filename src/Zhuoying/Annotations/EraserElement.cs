using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;

namespace Zhuoying.Annotations;

/// <summary>
/// 橡皮元素：**只擦画笔笔迹（含荧光）**，形状/箭头/文字/区域模糊等一概不受影响。
/// 自身不渲染、不可选中/命中；擦除 = 渲染 PenElement 时套
/// "全区 − 全部橡皮带并集"的反向裁剪（BuildClip），对先画后画的所有笔迹
/// 一致生效。仅撤销（Ctrl+Z）可恢复被擦内容。
/// </summary>
public sealed class EraserElement : AnnotationElement
{
    public List<PixelPoint> Points { get; } = [];

    /// <summary>橡皮直径（物理像素）。</summary>
    public double Thickness { get; set; } = 24;

    public override object CaptureState() => (Points.ToArray(), Thickness);

    public override void RestoreState(object state)
    {
        var (points, thickness) = ((PixelPoint[], double))state;
        Points.Clear();
        Points.AddRange(points);
        Thickness = thickness;
    }

    public override void Render(DrawingContext context, Func<Point, Point> toLocal, double scale)
    {
        // 无自身外观：效果由渲染管线对画笔元素做反向裁剪实现
    }

    public override bool HitTest(PixelPoint p, double slop) => false; // 不可选中

    /// <summary>
    /// 汇总全部橡皮 → 画笔渲染用的反向裁剪几何（目标坐标系）；无橡皮返回 null。
    /// AnnotationLayer 与输出合成共用。
    /// </summary>
    public static Geometry? BuildClip(
        IReadOnlyList<AnnotationElement> elements, Func<Point, Point> toLocal, double scale)
    {
        GeometryGroup? bands = null;
        foreach (var el in elements)
        {
            if (el is not EraserElement eraser || eraser.Points.Count == 0)
                continue;
            bands ??= new GeometryGroup { FillRule = FillRule.NonZero };
            bands.Children.Add(StrokeGeometry.BuildBand(
                eraser.Points, toLocal, Math.Max(1, eraser.Thickness) / scale / 2));
        }
        if (bands == null)
            return null;
        // "全区"用超大矩形，避免依赖目标区域尺寸
        var full = new RectangleGeometry(new Rect(-1e6, -1e6, 2e6, 2e6));
        return new CombinedGeometry(GeometryCombineMode.Exclude, full, bands);
    }
}

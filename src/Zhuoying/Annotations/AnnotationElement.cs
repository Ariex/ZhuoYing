using System;
using Avalonia;
using Avalonia.Media;

namespace Zhuoying.Annotations;

/// <summary>
/// 标注元素基类。几何一律为虚拟屏幕物理像素；渲染时由调用方提供
/// 物理点 → 目标坐标系的换算（线性变换，缩放系数单独给出供线宽换算）。
/// </summary>
public abstract class AnnotationElement
{
    /// <param name="toLocal">物理像素点 → 目标坐标系点。</param>
    /// <param name="scale">目标坐标系每单位对应的物理像素数（线宽换算用）。</param>
    public abstract void Render(DrawingContext context, Func<Point, Point> toLocal, double scale);

    /// <summary>命中测试（物理像素）。</summary>
    public abstract bool HitTest(PixelPoint p, double slop);

    /// <summary>捕获完整可变状态快照（撤销/重做用，与 RestoreState 配对）。</summary>
    public abstract object CaptureState();

    public abstract void RestoreState(object state);
}

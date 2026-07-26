using System;
using Avalonia;

namespace Zhuoying.Annotations;

/// <summary>
/// 有矩形包围盒 + 绕中心旋转的元素基类（形状、文字）。
/// 编辑器的移动/8 手柄缩放/旋转手柄逻辑针对本类统一实现。
/// </summary>
public abstract class BoxedElement : AnnotationElement
{
    /// <summary>包围矩形（虚拟屏幕物理像素，已规范化）。</summary>
    public PixelRect Bounds { get; set; }

    /// <summary>绕包围盒中心的旋转角（度）。存储位置由子类决定（通常在样式里随快照进撤销栈）。</summary>
    public abstract double RotationDeg { get; set; }

    /// <summary>包围盒中心（物理像素）。</summary>
    public Point Center => new(Bounds.X + Bounds.Width / 2.0, Bounds.Y + Bounds.Height / 2.0);

    /// <summary>把物理像素点逆旋转到元素未旋转坐标系。</summary>
    public Point ToUnrotated(Point p) => RotatePoint(p, Center, -RotationDeg);

    /// <summary>旋转后四角的轴对齐包围盒（物理像素；采样底图类渲染用）。</summary>
    protected PixelRect RotatedAabb()
    {
        if (RotationDeg == 0)
            return Bounds;
        var c = Center;
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        Span<Point> corners =
        [
            new(Bounds.X, Bounds.Y), new(Bounds.Right, Bounds.Y),
            new(Bounds.Right, Bounds.Bottom), new(Bounds.X, Bounds.Bottom),
        ];
        foreach (var corner in corners)
        {
            var p = RotatePoint(corner, c, RotationDeg);
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }
        return new PixelRect(
            (int)Math.Floor(minX), (int)Math.Floor(minY),
            (int)Math.Ceiling(maxX - minX), (int)Math.Ceiling(maxY - minY));
    }

    /// <summary>绕 center 旋转 deg 度的变换矩阵。</summary>
    public static Matrix RotationMatrix(Point center, double deg)
    {
        if (deg == 0)
            return Matrix.Identity;
        var rad = deg * Math.PI / 180;
        return Matrix.CreateTranslation(-center.X, -center.Y)
               * Matrix.CreateRotation(rad)
               * Matrix.CreateTranslation(center.X, center.Y);
    }

    /// <summary>绕 center 把点旋转 deg 度。</summary>
    public static Point RotatePoint(Point p, Point center, double deg)
    {
        if (deg == 0)
            return p;
        var rad = deg * Math.PI / 180;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var dx = p.X - center.X;
        var dy = p.Y - center.Y;
        return new Point(center.X + dx * cos - dy * sin, center.Y + dx * sin + dy * cos);
    }
}

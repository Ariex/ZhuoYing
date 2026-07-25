using Avalonia.Media;

namespace Zhuoying.Annotations;

/// <summary>
/// 形状样式（不可变，record 便于命令模式快照）。
/// Thickness 为物理像素；CornerRadiusPercent 0–100，
/// 语义同 CSS 百分比圆角（rx=p%×宽/2，ry=p%×高/2），100 = 椭圆。
/// </summary>
public sealed record ShapeStyle
{
    public Color Color { get; init; } = Color.FromRgb(0xFF, 0x3B, 0x30);
    public bool Filled { get; init; }
    public int LineStyleIndex { get; init; }
    public double Thickness { get; init; } = 5;
    public double CornerRadiusPercent { get; init; }

    /// <summary>绕包围盒中心的旋转角（度，-180..180）。随样式快照进撤销栈；新建元素时归零。</summary>
    public double RotationDeg { get; init; }

    /// <summary>透明度 0–100（%），作用于整个元素（描边 + 填充，TOOLS-SPEC §0.2）。</summary>
    public double Opacity { get; init; } = 100;
}

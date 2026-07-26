using Avalonia.Media;

namespace Zhuoying.Annotations;

/// <summary>
/// "反色"颜色哨兵：标注颜色 = 对底图逐像素取反（255−RGB），深浅背景都清晰。
/// 用 alpha=1 的黑作为哨兵值（取色器禁 Alpha，正常途径产生不了），
/// 渲染路径识别后走"采样冻结帧 → 取反 → 按元素几何裁剪"管线。
/// 当前支持：形状描边/填充、画笔（含与荧光同管线）。
/// </summary>
public static class InvertPaint
{
    public static readonly Color Sentinel = Color.FromArgb(1, 0, 0, 0);

    public static bool IsInvert(Color c) => c.A == 1 && c.R == 0 && c.G == 0 && c.B == 0;
}

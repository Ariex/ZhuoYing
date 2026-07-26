using Zhuoying.Annotations;

namespace Zhuoying.Capture;

/// <summary>
/// 各工具"上一次使用的样式"（进程内跨截屏会话记忆）：
/// 每次截屏会话新建 EditorState，样式槽从这里恢复、修改时写回——
/// 用户调过的颜色/粗细/字号等在下一次截屏时保持。
/// 旋转角在新建元素时归零，不受记忆影响；编号序列不记忆（每次会话从 1 起）。
/// </summary>
internal static class StyleMemory
{
    public static ShapeStyle? Shape;
    public static LineStyle? Arrow;
    public static LineStyle? Polyline;
    public static TextStyle? Text;
    public static NumberStyle? Number;
    public static MosaicStyle? Mosaic;
    public static PenStyle? Pen;
    public static StampStyle? Stamp;
    public static string? StampPath;
    public static double? Eraser;
}

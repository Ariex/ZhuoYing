using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Zhuoying.Capture;

/// <summary>
/// 悬浮弹层的统一堆叠方位。
/// 方向不按弹层各自判断（会出现一级向下、二级向上互相覆盖的混乱），而是由
/// **工具条位置**一次性决定：工具条每次停靠/拖动后，检查其下方是否足够容纳
/// 整个菜单链（当前最深三层 ≈ 720 DIP），够则所有层级向下堆叠，否则全部向上。
/// 堆叠以**容器整体**为基准：一级弹层弹在整条工具条的上/下方（不遮工具行），
/// 嵌套弹层弹在其父弹层窗口的上/下方（逐层顺延，不互相覆盖）。
/// 另做水平钳位：弹层右缘超出屏幕时整体左移。
/// </summary>
internal static class PopupPlacement
{
    /// <summary>菜单链高度预算（DIP）：属性行弹层→子菜单→取色器三层。加层时上调。</summary>
    private const double ChainHeightDip = 720;

    /// <summary>true = 截屏会话内所有弹层向上堆叠。</summary>
    public static bool StackUp { get; private set; }

    /// <summary>截屏工具条根元素（EditorToolbar 构造时注册，一级弹层的堆叠基准）。</summary>
    public static Control? ToolbarContainer { get; set; }

    /// <summary>
    /// 工具条停靠或拖动后调用，重新决定统一方向。
    /// leftDip/topDip 为工具条在窗口画布上的目标位置（布局可能尚未提交，
    /// 不能用 PointToScreen 读旧位置）。
    /// </summary>
    public static void UpdateDirection(Window window, double leftDip, double topDip, Size sizeDip)
    {
        var s = window.RenderScaling;
        var center = new PixelPoint(
            window.Position.X + (int)((leftDip + sizeDip.Width / 2) * s),
            window.Position.Y + (int)((topDip + sizeDip.Height / 2) * s));
        var screen = window.Screens.ScreenFromPoint(center);
        if (screen == null)
            return; // 找不到所属屏（异常几何），沿用当前方向
        var bottomY = window.Position.Y + (topDip + sizeDip.Height) * s;
        StackUp = bottomY + ChainHeightDip * s > screen.Bounds.Bottom;
    }

    /// <summary>
    /// 每次打开弹层前调用：套用统一方向（跳过容器整体）+ 水平钳位。
    /// minHeightDip / minWidthDip 为该弹层的最小尺寸估计——内容未挂进视觉树时
    /// 模板控件（Button/CheckBox/ColorView 等）尚无模板，Measure 结果严重偏小。
    /// 截屏会话（含嵌套二级弹层）走统一方向；其他窗口（设置窗口）就地判断。
    /// </summary>
    public static void Adjust(
        Popup popup, Control target, Control content, double minHeightDip, double minWidthDip)
    {
        var topLevel = TopLevel.GetTopLevel(target);
        content.Measure(Size.Infinity);
        var scaling = topLevel?.RenderScaling ?? 1.0;

        // 堆叠容器：一级 = 整条工具条；嵌套 = 父弹层窗口；设置窗口 = 无（贴按钮）
        Control? container = topLevel switch
        {
            PopupRoot root => root,
            CaptureOverlayWindow => ToolbarContainer,
            _ => null,
        };

        bool up;
        if (topLevel is CaptureOverlayWindow or PopupRoot)
        {
            up = StackUp;
        }
        else
        {
            up = false;
            if (topLevel != null && TryPointToScreen(
                    target, new Point(0, target.Bounds.Height), out var below))
            {
                var screen = topLevel.Screens?.ScreenFromPoint(below);
                var estimated = Math.Max(content.DesiredSize.Height, minHeightDip) + 4;
                up = screen != null
                     && below.Y + estimated * scaling > screen.Bounds.Bottom;
            }
        }

        popup.Placement = up ? PlacementMode.Top : PlacementMode.Bottom;
        var offset = up ? -2.0 : 2.0;
        // 从"贴按钮"平移到"贴容器边缘"：向上=弹层底对齐容器顶，向下=弹层顶对齐容器底
        if (container != null
            && TryPointToScreen(target, default, out var buttonTopLeft)
            && TryPointToScreen(container, default, out var containerTopLeft))
        {
            if (up)
            {
                offset -= (buttonTopLeft.Y - containerTopLeft.Y) / scaling;
            }
            else
            {
                var buttonBottom = buttonTopLeft.Y + target.Bounds.Height * scaling;
                var containerBottom = containerTopLeft.Y + container.Bounds.Height * scaling;
                offset += (containerBottom - buttonBottom) / scaling;
            }
        }
        popup.VerticalOffset = offset;

        // 水平规则：弹层比其容器（工具条/父弹层）宽 → 与容器右缘对齐、向左扩展；
        // 否则与按钮左缘对齐、向右扩展。最后按屏幕边缘兜底钳位。
        popup.HorizontalOffset = 0;
        if (topLevel != null && TryPointToScreen(target, default, out var anchor))
        {
            var width = Math.Max(content.DesiredSize.Width, minWidthDip) * scaling;
            double left = anchor.X;
            if (container != null
                && TryPointToScreen(container, default, out var containerAnchor))
            {
                var containerWidth = container.Bounds.Width * scaling;
                if (width > containerWidth)
                    left = containerAnchor.X + containerWidth - width;
            }
            var screen = topLevel.Screens?.ScreenFromPoint(anchor);
            if (screen != null)
            {
                if (left + width > screen.Bounds.Right)
                    left = screen.Bounds.Right - width;
                if (left < screen.Bounds.X)
                    left = screen.Bounds.X;
            }
            popup.HorizontalOffset = (left - anchor.X) / scaling;
        }
    }

    private static bool TryPointToScreen(Visual visual, Point point, out PixelPoint result)
    {
        try
        {
            result = visual.PointToScreen(point);
            return true;
        }
        catch (InvalidOperationException)
        {
            result = default; // 尚未挂到视觉树
            return false;
        }
    }
}

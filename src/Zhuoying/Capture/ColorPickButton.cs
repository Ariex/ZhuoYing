using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Zhuoying.Capture;

/// <summary>
/// 自定义取色按钮：彩虹格图标，仅点击弹出（不悬浮触发），弹层内嵌官方 ColorView
///（禁用 Alpha——透明度在样式体系里是独立属性）。外点击关闭（light dismiss）。
/// 选色实时回调 onPick；opened/onClosed 供工具栏接连续修改会话（拖色谱合并为一条撤销）。
/// 嵌套在其它悬浮弹层内使用时，外层看门狗应检查 <see cref="AnyOpen"/> 防止误关。
/// </summary>
public sealed class ColorPickButton : Panel
{
    private static int s_openCount;

    /// <summary>当前是否有取色弹层打开（外层弹层看门狗用）。</summary>
    public static bool AnyOpen => s_openCount > 0;

    private bool _syncing;

    public ColorPickButton(
        Func<Color> get, Action<Color> onPick,
        Action? onOpened = null, Action? onClosed = null, double buttonSize = 26)
    {
        var button = new Button
        {
            Width = buttonSize,
            Height = buttonSize,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(5),
            Content = RainbowIcon(buttonSize - 10),
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        ToolTip.SetTip(button, "自定义颜色");

        var colorView = new ColorView
        {
            IsAlphaEnabled = false,
            IsAlphaVisible = false,
            Width = 300,
        };
        var content = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            BoxShadow = BoxShadows.Parse("0 2 8 0 #33000000"),
            Cursor = new Cursor(StandardCursorType.Arrow),
            Child = colorView,
        };
        var popup = new Popup
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 2,
            IsLightDismissEnabled = true, // 点击弹层外关闭；色谱拖拽越界不受影响
            Child = content,
        };

        colorView.ColorChanged += (_, e) =>
        {
            if (!_syncing && popup.IsOpen)
                onPick(e.NewColor);
        };
        var previewHooked = false;
        popup.Opened += (_, _) =>
        {
            s_openCount++;
            onOpened?.Invoke();
            // 底部颜色预览条 = "选中并关闭"：模板应用后挂一次点击处理
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (previewHooked)
                    return;
                var previewer = colorView.GetVisualDescendants()
                    .OfType<ColorPreviewer>().FirstOrDefault();
                if (previewer == null)
                    return;
                previewHooked = true;
                previewer.Cursor = new Cursor(StandardCursorType.Hand);
                ToolTip.SetTip(previewer, "确认并关闭");
                previewer.PointerReleased += (_, _) => popup.IsOpen = false;
            }, Avalonia.Threading.DispatcherPriority.Background);
        };
        popup.Closed += (_, _) =>
        {
            s_openCount = Math.Max(0, s_openCount - 1);
            onClosed?.Invoke();
        };
        button.Click += (_, _) =>
        {
            if (popup.IsOpen)
            {
                popup.IsOpen = false;
                return;
            }
            _syncing = true;
            colorView.Color = get();
            _syncing = false;
            // ColorView 未打开时无模板、测不出尺寸，给足最小估计
            PopupPlacement.Adjust(popup, button, content, minHeightDip: 500, minWidthDip: 320);
            popup.IsOpen = true;
        };

        Children.Add(button);
        Children.Add(popup);
    }

    /// <summary>彩虹格图标：锥形渐变小方块 + 白点。</summary>
    private static Control RainbowIcon(double size) => new Border
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(3),
        BorderThickness = new Thickness(1),
        BorderBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)),
        Background = new ConicGradientBrush
        {
            Center = RelativePoint.Center,
            GradientStops =
            {
                new GradientStop(Colors.Red, 0),
                new GradientStop(Colors.Yellow, 0.17),
                new GradientStop(Colors.Lime, 0.33),
                new GradientStop(Colors.Cyan, 0.5),
                new GradientStop(Colors.Blue, 0.67),
                new GradientStop(Colors.Magenta, 0.83),
                new GradientStop(Colors.Red, 1),
            },
        },
    };
}

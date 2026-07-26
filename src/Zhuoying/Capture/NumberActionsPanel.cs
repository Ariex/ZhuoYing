using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Zhuoying.Annotations;

namespace Zhuoying.Capture;

/// <summary>
/// 选中编号徽章时叠加在其四角的操作按钮（真实控件，非画布绘制，天然支持 ToolTip）：
/// 左上 ▲▼ 增减编号值（不影响其他编号，允许重复）、右上红色 ✕ 删除（其余编号不变）、
/// 右下 ↺ 重置本序列（同标号类型的徽章从当前最小值起重排连续）。
/// </summary>
public sealed class NumberActionsPanel : Canvas
{
    /// <summary>选中虚线框相对徽章圆的外扩量（DIP），与 EditorLayer 的选中框一致。</summary>
    public const double OutlineMargin = 4;

    private const double Gap = 3; // 按钮与虚线框角的间距（DIP）

    private readonly AnnotationModel _model;
    private readonly EditorState _state;
    private readonly PixelPoint _origin;
    private readonly StackPanel _valueGroup;
    private readonly Button _deleteButton;
    private readonly Button _resetButton;

    public NumberActionsPanel(EditorState state, PixelPoint origin)
    {
        _state = state;
        _model = state.Model;
        _origin = origin;
        Background = null; // 空白区域不拦截命中，仅按钮可点

        _valueGroup = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                MiniButton("▲", "编号 +1", () => ChangeValue(1)),
                MiniButton("▼", "编号 −1", () => ChangeValue(-1)),
            },
        };
        _deleteButton = MiniButton("✕", "删除此编号（其余编号不变）", DeleteSelected,
            Color.FromRgb(0xE5, 0x39, 0x35), bold: true);
        _resetButton = MiniButton("↺", "重置本序列编号（从最小值起连续）", ResetSequence);

        Children.Add(_valueGroup);
        Children.Add(_deleteButton);
        Children.Add(_resetButton);
        IsVisible = false;
        _model.Changed += Refresh;
    }

    /// <summary>按当前选中徽章重摆按钮位置（选中变化/移动/DPI 几何变化时调用）。</summary>
    public void Refresh()
    {
        if (_model.Selected is not NumberElement el)
        {
            IsVisible = false;
            return;
        }
        IsVisible = true;
        var s = (VisualRoot as TopLevel)?.RenderScaling ?? 1.0;
        var cx = (el.Center.X - _origin.X) / s;
        var cy = (el.Center.Y - _origin.Y) / s;
        var half = el.Style.Diameter / s / 2 + OutlineMargin;

        _valueGroup.Measure(Size.Infinity);
        _deleteButton.Measure(Size.Infinity);
        _resetButton.Measure(Size.Infinity);

        // ▲ 与右上的 ✕ 同一行对齐，▼ 沿虚线框左边缘向下排
        Place(_valueGroup,
            cx - half - Gap - _valueGroup.DesiredSize.Width,
            cy - half - Gap - _deleteButton.DesiredSize.Height);
        Place(_deleteButton,
            cx + half + Gap,
            cy - half - Gap - _deleteButton.DesiredSize.Height);
        Place(_resetButton, cx + half + Gap, cy + half + Gap);
    }

    /// <summary>放置并钳入窗口范围（贴屏边的徽章按钮不跑出画面）。</summary>
    private void Place(Control control, double x, double y)
    {
        // 面板从隐藏转可见的首帧尚未排布（Bounds 为 0），此时不钳位，
        // 排布完成后 OnSizeChanged 会再精确重摆
        if (Bounds.Width > 0 && Bounds.Height > 0)
        {
            var size = control.DesiredSize;
            x = Math.Clamp(x, 0, Math.Max(0, Bounds.Width - size.Width));
            y = Math.Clamp(y, 0, Math.Max(0, Bounds.Height - size.Height));
        }
        SetLeft(control, x);
        SetTop(control, y);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Refresh();
    }

    private static Button MiniButton(
        string glyph, string tip, Action onClick, Color? foreground = null, bool bold = false)
    {
        var button = new Button
        {
            Width = 20,
            Height = 18,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.White,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0)),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Arrow),
            Content = new TextBlock
            {
                Text = glyph,
                FontSize = glyph is "▲" or "▼" ? 8 : 12,
                FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
                Foreground = new SolidColorBrush(foreground ?? Color.FromRgb(0x40, 0x40, 0x40)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>增减选中徽章的编号值（≥1）；不影响后续编号，允许出现重复值。</summary>
    private void ChangeValue(int delta)
    {
        if (_model.Selected is not NumberElement el)
            return;
        var value = Math.Max(1, el.Value + delta);
        if (value == el.Value)
            return;
        var before = el.CaptureState();
        el.Value = value;
        _model.Push(new MutateElementCommand(el, before, el.CaptureState()));
    }

    private void DeleteSelected()
    {
        if (_model.Selected is not NumberElement el)
            return;
        var index = _model.Elements.IndexOf(el);
        _model.Elements.Remove(el);
        _model.Selected = null;
        _model.Push(new RemoveElementCommand(_model, el, index));
    }

    /// <summary>
    /// 重置本序列：同标号类型的全部徽章按当前值排序（值相同按叠放序），
    /// 从最小值起重排为连续编号（2,5,6,7,8 → 2,3,4,5,6），一个撤销单元；
    /// 该类型的下一个编号顺延到重排末尾之后。
    /// </summary>
    private void ResetSequence()
    {
        if (_model.Selected is not NumberElement selected)
            return;
        var kind = selected.Style.Kind;
        var group = _model.Elements.OfType<NumberElement>()
            .Where(n => n.Style.Kind == kind)
            .OrderBy(n => n.Value) // LINQ 稳定排序：同值保持叠放序
            .ToList();
        if (group.Count == 0)
            return;
        var value = group[0].Value;
        var items = new List<(AnnotationElement, object, object)>();
        foreach (var n in group)
        {
            if (n.Value != value)
            {
                var before = n.CaptureState();
                n.Value = value;
                items.Add((n, before, n.CaptureState()));
            }
            value++;
        }
        _state.SetNextNumber(kind, value);
        if (items.Count > 0)
            _model.Push(new BatchMutateCommand(items));
    }
}

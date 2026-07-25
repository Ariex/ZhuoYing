using System;
using System.Collections.Generic;
using Avalonia.Media;
using Zhuoying.Annotations;

namespace Zhuoying.Capture;

public enum EditorTool
{
    Select,
    Shape,
}

/// <summary>
/// 编辑器会话状态：当前工具、当前样式（新元素用）、预设颜色。
/// 样式修改统一走本类：有选中元素时同步改元素并入撤销栈。
/// </summary>
public sealed class EditorState
{
    private readonly AnnotationModel _model;
    private EditorTool _tool = EditorTool.Select;
    private ShapeStyle _currentStyle = new();
    // 连续修改（滑条拖动）期间的快照，结束时一次性入栈
    private (ShapeElement Element, Avalonia.PixelRect Bounds, ShapeStyle Style)? _continuousSnapshot;

    public EditorState(AnnotationModel model, IReadOnlyList<Color> presetColors)
    {
        _model = model;
        PresetColors = presetColors;
        if (presetColors.Count > 0)
            _currentStyle = _currentStyle with { Color = presetColors[0] };
    }

    public IReadOnlyList<Color> PresetColors { get; }

    public AnnotationModel Model => _model;

    public EditorTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value)
                return;
            _tool = value;
            if (value != EditorTool.Select)
                _model.Selected = null;
            ToolChanged?.Invoke();
        }
    }

    /// <summary>当前展示/编辑的样式：有选中元素时为其样式，否则为待创建样式。</summary>
    public ShapeStyle CurrentStyle => _model.Selected?.Style ?? _currentStyle;

    public event Action? ToolChanged;
    public event Action? StyleChanged;

    /// <summary>离散样式修改（复选框/下拉/颜色点击）：立即入撤销栈。</summary>
    public void ModifyStyle(Func<ShapeStyle, ShapeStyle> change)
    {
        _currentStyle = change(CurrentStyle);
        if (_model.Selected is { } el)
        {
            var oldBounds = el.Bounds;
            var oldStyle = el.Style;
            el.Style = change(el.Style);
            _model.Push(new MutateElementCommand(el, oldBounds, oldStyle, el.Bounds, el.Style));
        }
        StyleChanged?.Invoke();
    }

    /// <summary>连续修改开始（滑条按下/弹层打开时快照）。</summary>
    public void BeginContinuousStyle()
    {
        if (_continuousSnapshot == null && _model.Selected is { } el)
            _continuousSnapshot = (el, el.Bounds, el.Style);
    }

    /// <summary>连续修改中的实时应用（不入栈）。</summary>
    public void ModifyStyleLive(Func<ShapeStyle, ShapeStyle> change)
    {
        _currentStyle = change(CurrentStyle);
        if (_model.Selected is { } el)
        {
            el.Style = change(el.Style);
            _model.RaiseChanged();
        }
        StyleChanged?.Invoke();
    }

    /// <summary>连续修改结束：与快照有差异则一次性入栈。</summary>
    public void EndContinuousStyle()
    {
        if (_continuousSnapshot is { } snap)
        {
            _continuousSnapshot = null;
            if (snap.Element.Style != snap.Style || snap.Element.Bounds != snap.Bounds)
                _model.Push(new MutateElementCommand(
                    snap.Element, snap.Bounds, snap.Style, snap.Element.Bounds, snap.Element.Style));
        }
    }

    /// <summary>选中元素时把其样式设为当前样式（后续新元素继承）。</summary>
    public void SyncStyleFromSelection()
    {
        if (_model.Selected is { } el)
        {
            _currentStyle = el.Style;
            StyleChanged?.Invoke();
        }
    }
}

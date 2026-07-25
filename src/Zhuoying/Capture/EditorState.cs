using System;
using System.Collections.Generic;
using Avalonia.Media;
using Zhuoying.Annotations;

namespace Zhuoying.Capture;

public enum EditorTool
{
    Select,
    Shape,
    Arrow,
    Polyline,
    Text,
}

/// <summary>编辑器会话级选项（由设置文件提供）。</summary>
public sealed record EditorOptions(
    IReadOnlyList<Color> PresetColors,
    double FontSizeMin,
    double FontSizeMax);

/// <summary>
/// 编辑器会话状态：当前工具、各工具的当前样式（新元素用）、预设颜色。
/// 样式修改统一走本类：有匹配类型的选中元素时同步改元素并入撤销栈。
/// </summary>
public sealed class EditorState
{
    private readonly AnnotationModel _model;
    private EditorTool _tool = EditorTool.Select;
    private ShapeStyle _currentShapeStyle = new();
    private LineStyle _arrowStyle = new()
    {
        // 默认箭头：末端实心三角，起细尾粗
        StartThickness = 3,
        EndThickness = 40,
        EndCap = LineCapKind.SolidTriangle,
    };
    private LineStyle _polylineStyle = new();
    private TextStyle _textStyle = new();
    // 连续修改（滑条拖动）期间的快照，结束时一次性入栈
    private (AnnotationElement Element, object State)? _continuousSnapshot;
    private bool _continuousDirty;

    public EditorState(AnnotationModel model, EditorOptions options)
    {
        _model = model;
        PresetColors = options.PresetColors;
        FontSizeMin = options.FontSizeMin;
        FontSizeMax = options.FontSizeMax;
        if (PresetColors.Count > 0)
        {
            _currentShapeStyle = _currentShapeStyle with { Color = PresetColors[0] };
            _arrowStyle = _arrowStyle with { Color = PresetColors[0] };
            _polylineStyle = _polylineStyle with { Color = PresetColors[0] };
            _textStyle = _textStyle with { Color = PresetColors[0] };
        }
        _textStyle = _textStyle with
        {
            FontSize = Math.Clamp(_textStyle.FontSize, FontSizeMin, FontSizeMax),
        };
    }

    public IReadOnlyList<Color> PresetColors { get; }

    public double FontSizeMin { get; }
    public double FontSizeMax { get; }

    public AnnotationModel Model => _model;

    /// <summary>最近一次使用的线类工具（主工具栏"箭头"按钮的落点）。</summary>
    public EditorTool LastLineTool { get; private set; } = EditorTool.Arrow;

    public EditorTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value)
                return;
            _tool = value;
            if (value is EditorTool.Arrow or EditorTool.Polyline)
                LastLineTool = value;
            if (value != EditorTool.Select)
                _model.Selected = null;
            ToolChanged?.Invoke();
        }
    }

    /// <summary>当前形状样式：选中形状元素时为其样式，否则为待创建样式。</summary>
    public ShapeStyle CurrentStyle =>
        (_model.Selected as ShapeElement)?.Style ?? _currentShapeStyle;

    /// <summary>当前线样式：选中线元素时为其样式，否则按活动/最近线工具取槽。</summary>
    public LineStyle CurrentLineStyle => _model.Selected is LineElement line
        ? line.Style
        : (Tool == EditorTool.Polyline || (Tool != EditorTool.Arrow && LastLineTool == EditorTool.Polyline))
            ? _polylineStyle
            : _arrowStyle;

    /// <summary>当前文字样式：选中文字元素时为其样式，否则为待创建样式。</summary>
    public TextStyle CurrentTextStyle => (_model.Selected as TextElement)?.Style ?? _textStyle;

    public event Action? ToolChanged;
    public event Action? StyleChanged;

    // ---- 形状样式 ----

    /// <summary>离散样式修改（复选框/下拉/颜色点击）：立即入撤销栈。</summary>
    public void ModifyStyle(Func<ShapeStyle, ShapeStyle> change)
    {
        _currentShapeStyle = change(CurrentStyle);
        if (_model.Selected is ShapeElement el)
        {
            var before = el.CaptureState();
            el.Style = change(el.Style);
            _model.Push(new MutateElementCommand(el, before, el.CaptureState()));
        }
        StyleChanged?.Invoke();
    }

    /// <summary>连续修改中的实时应用（不入栈）。</summary>
    public void ModifyStyleLive(Func<ShapeStyle, ShapeStyle> change)
    {
        _currentShapeStyle = change(CurrentStyle);
        if (_model.Selected is ShapeElement el)
        {
            el.Style = change(el.Style);
            _continuousDirty = true;
            _model.RaiseChanged();
        }
        StyleChanged?.Invoke();
    }

    // ---- 线样式 ----

    public void ModifyLineStyle(Func<LineStyle, LineStyle> change)
    {
        ApplyLineToSlot(change);
        if (_model.Selected is LineElement el)
        {
            var before = el.CaptureState();
            el.Style = change(el.Style);
            _model.Push(new MutateElementCommand(el, before, el.CaptureState()));
        }
        StyleChanged?.Invoke();
    }

    public void ModifyLineStyleLive(Func<LineStyle, LineStyle> change)
    {
        ApplyLineToSlot(change);
        if (_model.Selected is LineElement el)
        {
            el.Style = change(el.Style);
            _continuousDirty = true;
            _model.RaiseChanged();
        }
        StyleChanged?.Invoke();
    }

    private void ApplyLineToSlot(Func<LineStyle, LineStyle> change)
    {
        if (_model.Selected is LineElement el)
        {
            if (el.IsArrowTool)
                _arrowStyle = change(_arrowStyle);
            else
                _polylineStyle = change(_polylineStyle);
        }
        else if (Tool == EditorTool.Polyline
                 || (Tool != EditorTool.Arrow && LastLineTool == EditorTool.Polyline))
        {
            _polylineStyle = change(_polylineStyle);
        }
        else
        {
            _arrowStyle = change(_arrowStyle);
        }
    }

    /// <summary>新建元素用的线样式（按工具取槽）。</summary>
    public LineStyle LineStyleFor(EditorTool tool) =>
        tool == EditorTool.Polyline ? _polylineStyle : _arrowStyle;

    // ---- 文字样式 ----

    public void ModifyTextStyle(Func<TextStyle, TextStyle> change)
    {
        _textStyle = change(CurrentTextStyle);
        if (_model.Selected is TextElement el)
        {
            var before = el.CaptureState();
            el.Style = change(el.Style);
            _model.Push(new MutateElementCommand(el, before, el.CaptureState()));
        }
        StyleChanged?.Invoke();
    }

    public void ModifyTextStyleLive(Func<TextStyle, TextStyle> change)
    {
        _textStyle = change(CurrentTextStyle);
        if (_model.Selected is TextElement el)
        {
            el.Style = change(el.Style);
            _continuousDirty = true;
            _model.RaiseChanged();
        }
        StyleChanged?.Invoke();
    }

    // ---- 连续修改会话（滑条弹层）----

    /// <summary>连续修改开始（滑条按下/弹层打开时快照）。</summary>
    public void BeginContinuousStyle()
    {
        if (_continuousSnapshot == null && _model.Selected is { } el)
        {
            _continuousSnapshot = (el, el.CaptureState());
            _continuousDirty = false;
        }
    }

    /// <summary>连续修改结束：期间有实际改动则一次性入栈。</summary>
    public void EndContinuousStyle()
    {
        if (_continuousSnapshot is { } snap)
        {
            _continuousSnapshot = null;
            if (_continuousDirty)
                _model.Push(new MutateElementCommand(
                    snap.Element, snap.State, snap.Element.CaptureState()));
            _continuousDirty = false;
        }
    }

    /// <summary>选中元素时把其样式设为对应的当前样式（后续新元素继承）。</summary>
    public void SyncStyleFromSelection()
    {
        switch (_model.Selected)
        {
            case ShapeElement shape:
                _currentShapeStyle = shape.Style;
                break;
            case LineElement line:
                if (line.IsArrowTool)
                    _arrowStyle = line.Style;
                else
                    _polylineStyle = line.Style;
                break;
            case TextElement text:
                _textStyle = text.Style;
                break;
            default:
                return;
        }
        StyleChanged?.Invoke();
    }
}

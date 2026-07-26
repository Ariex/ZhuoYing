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
    Number,
    Pixelate,
    Blur,
    Pen,
    Stamp,
    Eraser,
}

/// <summary>编辑器会话级选项（由设置文件提供）。</summary>
public sealed record EditorOptions(
    IReadOnlyList<Color> PresetColors,
    double FontSizeMin,
    double FontSizeMax,
    string SavePath);

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
    private NumberStyle _numberStyle = new();
    private MosaicStyle _mosaicStyle = new();
    private PenStyle _penStyle = new();
    private StampStyle _stampStyle = new();
    // 每种标号类型独立的下一个编号（切换类型各自续接）
    private readonly Dictionary<NumberKind, int> _nextNumbers = new();
    // 连续修改（滑条拖动）期间的快照，结束时一次性入栈
    private (AnnotationElement Element, object State)? _continuousSnapshot;
    private bool _continuousDirty;

    public EditorState(AnnotationModel model, EditorOptions options)
    {
        _model = model;
        PresetColors = options.PresetColors;
        FontSizeMin = options.FontSizeMin;
        FontSizeMax = options.FontSizeMax;
        // 无记忆的槽才套预设首色（有记忆 = 用户上次调过的样式，原样恢复）
        if (PresetColors.Count > 0)
        {
            _currentShapeStyle = _currentShapeStyle with { Color = PresetColors[0] };
            _arrowStyle = _arrowStyle with { Color = PresetColors[0] };
            _polylineStyle = _polylineStyle with { Color = PresetColors[0] };
            _textStyle = _textStyle with { Color = PresetColors[0] };
            _numberStyle = _numberStyle with { Color = PresetColors[0] };
            _penStyle = _penStyle with { Color = PresetColors[0] };
        }
        _currentShapeStyle = StyleMemory.Shape ?? _currentShapeStyle;
        _arrowStyle = StyleMemory.Arrow ?? _arrowStyle;
        _polylineStyle = StyleMemory.Polyline ?? _polylineStyle;
        _textStyle = StyleMemory.Text ?? _textStyle;
        _numberStyle = StyleMemory.Number ?? _numberStyle;
        _mosaicStyle = StyleMemory.Mosaic ?? _mosaicStyle;
        _penStyle = StyleMemory.Pen ?? _penStyle;
        _stampStyle = StyleMemory.Stamp ?? _stampStyle;
        _stampPath = StyleMemory.StampPath;
        _eraserThickness = StyleMemory.Eraser ?? 24;
        _textStyle = _textStyle with
        {
            FontSize = Math.Clamp(_textStyle.FontSize, FontSizeMin, FontSizeMax),
        };
    }

    /// <summary>把全部样式槽写回跨会话记忆（每次样式变化时调用）。</summary>
    private void RaiseStyleChanged()
    {
        StyleMemory.Shape = _currentShapeStyle;
        StyleMemory.Arrow = _arrowStyle;
        StyleMemory.Polyline = _polylineStyle;
        StyleMemory.Text = _textStyle;
        StyleMemory.Number = _numberStyle;
        StyleMemory.Mosaic = _mosaicStyle;
        StyleMemory.Pen = _penStyle;
        StyleMemory.Stamp = _stampStyle;
        StyleMemory.StampPath = _stampPath;
        StyleMemory.Eraser = _eraserThickness;
        StyleChanged?.Invoke();
    }

    /// <summary>橡皮直径（物理像素，1–100；橡皮只有这一个设定）。</summary>
    public double EraserThickness
    {
        get => _eraserThickness;
        set
        {
            _eraserThickness = Math.Clamp(value, 1, 100);
            RaiseStyleChanged();
        }
    }

    private double _eraserThickness = 24;

    public IReadOnlyList<Color> PresetColors { get; }

    public double FontSizeMin { get; }
    public double FontSizeMax { get; }

    public AnnotationModel Model => _model;

    /// <summary>最近一次使用的线类工具（主工具栏"箭头"按钮的落点）。</summary>
    public EditorTool LastLineTool { get; private set; } = EditorTool.Arrow;

    /// <summary>最近一次使用的区域模糊工具（主工具栏"区域模糊"按钮的落点）。</summary>
    public EditorTool LastMosaicTool { get; private set; } = EditorTool.Pixelate;

    /// <summary>冻结帧（区域模糊元素的采样来源，会话开始时注入）。</summary>
    public Avalonia.Media.Imaging.WriteableBitmap? BackgroundFrame { get; private set; }

    /// <summary>冻结帧左上角的虚拟屏幕物理坐标。</summary>
    public Avalonia.PixelPoint BackgroundOrigin { get; private set; }

    public void SetBackground(Avalonia.Media.Imaging.WriteableBitmap frame, Avalonia.PixelPoint origin)
    {
        BackgroundFrame = frame;
        BackgroundOrigin = origin;
    }

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
            if (value is EditorTool.Pixelate or EditorTool.Blur)
                LastMosaicTool = value;
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

    /// <summary>当前编号样式：选中编号元素时为其样式，否则为待创建样式。</summary>
    public NumberStyle CurrentNumberStyle => (_model.Selected as NumberElement)?.Style ?? _numberStyle;

    /// <summary>当前区域模糊样式：选中模糊元素时为其样式，否则为待创建样式。</summary>
    public MosaicStyle CurrentMosaicStyle => (_model.Selected as MosaicElement)?.Style ?? _mosaicStyle;

    /// <summary>当前画笔样式：选中笔迹时为其样式，否则为待创建样式。</summary>
    public PenStyle CurrentPenStyle => (_model.Selected as PenElement)?.Style ?? _penStyle;

    /// <summary>当前图章样式：选中图章时为其样式，否则为待创建样式。</summary>
    public StampStyle CurrentStampStyle => (_model.Selected as StampElement)?.Style ?? _stampStyle;

    /// <summary>当前选中的图章素材路径（选中图章元素时跟随元素）。</summary>
    public string? CurrentStampPath
    {
        get => _model.Selected is StampElement el ? el.SourcePath : _stampPath;
        set
        {
            _stampPath = value;
            RaiseStyleChanged();
        }
    }

    private string? _stampPath;

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
        RaiseStyleChanged();
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
        RaiseStyleChanged();
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
        RaiseStyleChanged();
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
        RaiseStyleChanged();
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
        RaiseStyleChanged();
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
        RaiseStyleChanged();
    }

    // ---- 编号样式与序列 ----

    public void ModifyNumberStyle(Func<NumberStyle, NumberStyle> change)
    {
        _numberStyle = change(CurrentNumberStyle);
        if (_model.Selected is NumberElement el)
        {
            var before = el.CaptureState();
            el.Style = change(el.Style);
            _model.Push(new MutateElementCommand(el, before, el.CaptureState()));
        }
        RaiseStyleChanged();
    }

    public void ModifyNumberStyleLive(Func<NumberStyle, NumberStyle> change)
    {
        _numberStyle = change(CurrentNumberStyle);
        if (_model.Selected is NumberElement el)
        {
            el.Style = change(el.Style);
            _continuousDirty = true;
            _model.RaiseChanged();
        }
        RaiseStyleChanged();
    }

    /// <summary>指定标号类型的下一个编号（默认 1）。</summary>
    public int NextNumber(NumberKind kind) =>
        _nextNumbers.TryGetValue(kind, out var v) ? v : 1;

    /// <summary>设定下一个编号（最小 1）。编号序列独立于撤销栈。</summary>
    public void SetNextNumber(NumberKind kind, int value)
    {
        _nextNumbers[kind] = Math.Max(1, value);
        RaiseStyleChanged();
    }

    /// <summary>取出下一个编号并使序列前进一步（放置新徽章时调用）。</summary>
    public int TakeNextNumber(NumberKind kind)
    {
        var v = NextNumber(kind);
        _nextNumbers[kind] = v + 1;
        RaiseStyleChanged();
        return v;
    }

    // ---- 区域模糊样式 ----

    public void ModifyMosaicStyle(Func<MosaicStyle, MosaicStyle> change)
    {
        _mosaicStyle = change(CurrentMosaicStyle);
        if (_model.Selected is MosaicElement el)
        {
            var before = el.CaptureState();
            el.Style = change(el.Style);
            _model.Push(new MutateElementCommand(el, before, el.CaptureState()));
        }
        RaiseStyleChanged();
    }

    public void ModifyMosaicStyleLive(Func<MosaicStyle, MosaicStyle> change)
    {
        _mosaicStyle = change(CurrentMosaicStyle);
        if (_model.Selected is MosaicElement el)
        {
            el.Style = change(el.Style);
            _continuousDirty = true;
            _model.RaiseChanged();
        }
        RaiseStyleChanged();
    }

    /// <summary>新建区域模糊元素用的样式（模式由工具决定）。</summary>
    public MosaicStyle MosaicStyleFor(EditorTool tool) => _mosaicStyle with
    {
        Mode = tool == EditorTool.Blur ? MosaicMode.Blur : MosaicMode.Pixelate,
        RotationDeg = 0,
    };

    // ---- 画笔样式 ----

    public void ModifyPenStyle(Func<PenStyle, PenStyle> change)
    {
        _penStyle = change(CurrentPenStyle);
        if (_model.Selected is PenElement el)
        {
            var before = el.CaptureState();
            el.Style = change(el.Style);
            _model.Push(new MutateElementCommand(el, before, el.CaptureState()));
        }
        RaiseStyleChanged();
    }

    public void ModifyPenStyleLive(Func<PenStyle, PenStyle> change)
    {
        _penStyle = change(CurrentPenStyle);
        if (_model.Selected is PenElement el)
        {
            el.Style = change(el.Style);
            _continuousDirty = true;
            _model.RaiseChanged();
        }
        RaiseStyleChanged();
    }

    // ---- 图章样式 ----

    public void ModifyStampStyle(Func<StampStyle, StampStyle> change)
    {
        _stampStyle = change(CurrentStampStyle);
        if (_model.Selected is StampElement el)
        {
            var before = el.CaptureState();
            el.Style = change(el.Style);
            _model.Push(new MutateElementCommand(el, before, el.CaptureState()));
        }
        RaiseStyleChanged();
    }

    public void ModifyStampStyleLive(Func<StampStyle, StampStyle> change)
    {
        _stampStyle = change(CurrentStampStyle);
        if (_model.Selected is StampElement el)
        {
            el.Style = change(el.Style);
            _continuousDirty = true;
            _model.RaiseChanged();
        }
        RaiseStyleChanged();
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
            case NumberElement number:
                _numberStyle = number.Style;
                break;
            case MosaicElement mosaic:
                _mosaicStyle = mosaic.Style;
                break;
            case PenElement pen:
                _penStyle = pen.Style;
                break;
            case StampElement stamp:
                _stampStyle = stamp.Style;
                _stampPath = stamp.SourcePath;
                break;
            default:
                return;
        }
        RaiseStyleChanged();
    }
}

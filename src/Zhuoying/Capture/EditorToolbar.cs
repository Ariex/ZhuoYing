using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Zhuoying.Annotations;

namespace Zhuoying.Capture;

/// <summary>
/// 编辑器工具栏：第一行为工具/动作（选择、形状、撤销/重做、复制、取消），
/// 第二行为形状属性（填充、线形、粗细、圆角、颜色），仅在形状工具激活或
/// 选中形状元素时显示。全部代码构建（无 XAML），白底浮条样式。
/// </summary>
public sealed class EditorToolbar
{
    private static readonly Color IconColor = Color.FromRgb(0x40, 0x40, 0x40);
    private static readonly Color ActiveBackground = Color.FromRgb(0xD6, 0xE9, 0xFF);

    private readonly EditorState _state;
    private readonly AnnotationModel _model;
    private bool _refreshing;

    private readonly Button _selectButton;
    private readonly Button _shapeButton;
    private readonly Button _lineToolButton;
    private readonly Button _textToolButton;
    private readonly Button _undoButton;
    private readonly Button _redoButton;
    private readonly StackPanel _propertyRow;
    private readonly CheckBox _fillCheck;
    private readonly Button _shapeDashButton;
    private readonly TextBlock _thicknessText;
    private readonly TextBlock _radiusText;
    private readonly TextBlock _rotationText;
    private readonly TextBlock _opacityText;
    private readonly StackPanel _swatchPanel;
    private readonly StackPanel _linePropertyRow;
    private readonly Button _lineDashButton;
    private readonly Button _startCapButton;
    private readonly Button _endCapButton;
    private readonly CheckBox _splineCheck;
    private readonly TextBlock _startThickText;
    private readonly TextBlock _endThickText;
    private readonly TextBlock _lineOpacityText;
    private readonly StackPanel _lineSwatchPanel;
    private readonly StackPanel _textPropertyRow;
    private readonly TextBlock _fontNameText;
    private readonly TextBlock _fontSizeText;
    private readonly Button _boldButton;
    private readonly Button _italicButton;
    private readonly Button _halignButton;
    private readonly Button _valignButton;
    private readonly StackPanel _textSwatchPanel;
    private readonly Button _numberToolButton;
    private readonly StackPanel _numberPropertyRow;
    private readonly Button _numberStyleButton;
    private readonly Button _numberKindButton;
    private readonly TextBlock _numberSizeText;
    private readonly TextBlock _numberNextText;
    private readonly StackPanel _numberSwatchPanel;
    private readonly Button _mosaicToolButton;
    private readonly Button _lineSwitchButton;
    private readonly Button _mosaicSwitchButton;
    private readonly StackPanel _lineChooserPanel;
    private readonly StackPanel _mosaicChooserPanel;
    private bool _lineChoosing;
    private bool _mosaicChoosing;
    private readonly StackPanel _mosaicPropertyRow;
    private readonly StackPanel _toolRow;
    private readonly StackPanel _rowsPanel;
    private readonly TextBlock _blockSizeText;
    private readonly TextBlock _blurRadiusText;
    private readonly MiniSlider _blockSlider;
    private readonly MiniSlider _blurSlider;
    private readonly Control _blockSizeHost;
    private readonly Control _blurRadiusHost;
    private static string[]? _systemFonts;

    /// <summary>标号类型预览字符（顺序与 NumberKind 枚举一致）。</summary>
    private static readonly string[] KindGlyphs = ["1", "A", "a", "i", "I", "一"];

    public Border Root { get; }

    /// <summary>用户已用左侧手柄手动拖过工具条：窗口停止自动停靠，保留用户位置。</summary>
    public bool ManuallyPositioned { get; private set; }

    /// <summary>行显隐等导致尺寸变化（窗口据此重摆工具条位置）。</summary>
    public event Action? LayoutChanged;

    /// <summary>
    /// 按统一堆叠方向排布内部行序：向上堆叠时工具行放最下、属性行在其上方
    ///（与更上方的弹层方向一致）；向下则工具行在最上。
    /// </summary>
    public void ApplyStackDirection(bool up)
    {
        var index = _rowsPanel.Children.IndexOf(_toolRow);
        var target = up ? _rowsPanel.Children.Count - 1 : 0;
        if (index == target)
            return;
        _rowsPanel.Children.RemoveAt(index);
        _rowsPanel.Children.Insert(target, _toolRow);
    }

    public EditorToolbar(EditorState state, Action copy, Action cancel)
    {
        _state = state;
        _model = state.Model;

        _selectButton = ToolButton(SelectIcon(), "选择 (V)", () => _state.Tool = EditorTool.Select);
        _shapeButton = ToolButton(RectIcon(22, 14, 11), "形状 (S)", () => _state.Tool = EditorTool.Shape);
        // 双工具组按钮：斜线分割双图标（左上=默认工具带蓝底，右下=第二工具），
        // 点击激活组内最近使用的工具；组内切换在属性行最左侧的选择器里做
        _lineToolButton = ToolButton(LineGroupIcon(), "箭头 / 折线 (A / L)",
            () => _state.Tool = _state.LastLineTool);
        _textToolButton = ToolButton(GlyphIcon("T"), "文字 (T)", () => _state.Tool = EditorTool.Text);
        _numberToolButton = ToolButton(NumberToolIcon(), "编号 (N)", () => _state.Tool = EditorTool.Number);
        _mosaicToolButton = ToolButton(MosaicGroupIcon(), "区域模糊：像素化 / 模糊化 (M / B)",
            () => _state.Tool = _state.LastMosaicTool);
        _undoButton = ToolButton(GlyphIcon("↶"), "撤销 (Ctrl+Z)", () => _model.Undo());
        _redoButton = ToolButton(GlyphIcon("↷"), "重做 (Ctrl+Y)", () => _model.Redo());
        var copyButton = ToolButton(CopyIcon(), "复制 (Enter)", copy);
        var cancelButton = ToolButton(GlyphIcon("✕"), "取消 (Esc)", cancel);

        var toolRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Children =
            {
                BuildGrip(), Separator(),
                _selectButton, _shapeButton, _lineToolButton, _textToolButton, _numberToolButton,
                _mosaicToolButton, Separator(),
                _undoButton, _redoButton, Separator(),
                copyButton, cancelButton,
            },
        };

        _fillCheck = new CheckBox { Content = "填充", VerticalAlignment = VerticalAlignment.Center };
        _fillCheck.IsCheckedChanged += (_, _) =>
        {
            if (_refreshing) return;
            var filled = _fillCheck.IsChecked == true;
            _state.ModifyStyle(s => s with { Filled = filled });
        };

        var shapeDashHost = PaletteButton(
            () => _state.CurrentStyle.LineStyleIndex,
            i => _state.ModifyStyle(s => s with { LineStyleIndex = i }),
            LineStyles.All.Length,
            i => DashPreview(i, 40),
            out _shapeDashButton);

        _thicknessText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 22 };
        var thicknessButton = SliderPopupButton(
            ThicknessIcon(), _thicknessText, "粗细",
            min: 1, max: 100,
            get: () => _state.CurrentStyle.Thickness,
            setLive: v => _state.ModifyStyleLive(s => s with { Thickness = Math.Round(v) }));

        _radiusText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 22 };
        var radiusButton = SliderPopupButton(
            RadiusIcon(), _radiusText, "圆角",
            min: 0, max: 100,
            get: () => _state.CurrentStyle.CornerRadiusPercent,
            setLive: v => _state.ModifyStyleLive(s => s with { CornerRadiusPercent = Math.Round(v) }));

        _rotationText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 26 };
        var rotationButton = SliderPopupButton(
            GlyphIcon("↻"), _rotationText, "旋转",
            min: -180, max: 180,
            get: () => _state.CurrentStyle.RotationDeg,
            setLive: v => _state.ModifyStyleLive(s => s with { RotationDeg = Math.Round(v) }));

        _opacityText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 24 };
        var opacityButton = SliderPopupButton(
            OpacityIcon(), _opacityText, "透明度",
            min: 0, max: 100,
            get: () => _state.CurrentStyle.Opacity,
            setLive: v => _state.ModifyStyleLive(s => s with { Opacity = Math.Round(v) }));

        _swatchPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var shapeColorPick = new ColorPickButton(
            () => _state.CurrentStyle.Color,
            c => _state.ModifyStyleLive(s => s with { Color = c }),
            _state.BeginContinuousStyle, _state.EndContinuousStyle)
        {
            VerticalAlignment = VerticalAlignment.Center,
        };

        _propertyRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                _fillCheck, shapeDashHost, thicknessButton, radiusButton, rotationButton, opacityButton,
                Separator(), _swatchPanel, shapeColorPick,
            },
        };

        // ---- 线/箭头属性行 ----

        var capCount = Enum.GetValues<LineCapKind>().Length;
        var lineDashHost = PaletteButton(
            () => _state.CurrentLineStyle.LineStyleIndex,
            i => _state.ModifyLineStyle(s => s with { LineStyleIndex = i }),
            LineStyles.All.Length,
            i => DashPreview(i, 40),
            out _lineDashButton);
        var startCapHost = PaletteButton(
            () => (int)_state.CurrentLineStyle.StartCap,
            i => _state.ModifyLineStyle(s => s with { StartCap = (LineCapKind)i }),
            capCount,
            i => new CapPreview { Kind = (LineCapKind)i, AtStart = true, Width = 24 },
            out _startCapButton);
        var endCapHost = PaletteButton(
            () => (int)_state.CurrentLineStyle.EndCap,
            i => _state.ModifyLineStyle(s => s with { EndCap = (LineCapKind)i }),
            capCount,
            i => new CapPreview { Kind = (LineCapKind)i, AtStart = false, Width = 24 },
            out _endCapButton);

        _startThickText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 20 };
        var startThickButton = SliderPopupButton(
            CapThicknessIcon(atStart: true), _startThickText, "起端粗细",
            min: 1, max: 100,
            get: () => _state.CurrentLineStyle.StartThickness,
            setLive: v => _state.ModifyLineStyleLive(s => s with { StartThickness = Math.Round(v) }),
            extra: ("应用到末端", () => _state.ModifyLineStyle(s => s with { EndThickness = s.StartThickness })));

        _endThickText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 20 };
        var endThickButton = SliderPopupButton(
            CapThicknessIcon(atStart: false), _endThickText, "末端粗细",
            min: 1, max: 100,
            get: () => _state.CurrentLineStyle.EndThickness,
            setLive: v => _state.ModifyLineStyleLive(s => s with { EndThickness = Math.Round(v) }),
            extra: ("应用到起端", () => _state.ModifyLineStyle(s => s with { StartThickness = s.EndThickness })));

        _splineCheck = new CheckBox { Content = "弧线", VerticalAlignment = VerticalAlignment.Center };
        _splineCheck.IsCheckedChanged += (_, _) =>
        {
            if (_refreshing) return;
            var spline = _splineCheck.IsChecked == true;
            _state.ModifyLineStyle(s => s with { Spline = spline });
        };

        _lineOpacityText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 24 };
        var lineOpacityButton = SliderPopupButton(
            OpacityIcon(), _lineOpacityText, "透明度",
            min: 0, max: 100,
            get: () => _state.CurrentLineStyle.Opacity,
            setLive: v => _state.ModifyLineStyleLive(s => s with { Opacity = Math.Round(v) }));

        _lineSwatchPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var lineColorPick = new ColorPickButton(
            () => _state.CurrentLineStyle.Color,
            c => _state.ModifyLineStyleLive(s => s with { Color = c }),
            _state.BeginContinuousStyle, _state.EndContinuousStyle)
        {
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 组内工具选择器（属性行最左侧，PS 式就地切换）：点击后整行临时变为
        // [箭头][折线] 两个选择按钮（其余设置隐藏），选定后恢复设置组并切换工具
        _lineSwitchButton = ToolButton(ArrowIcon(), "切换 箭头 / 折线", () =>
        {
            _lineChoosing = true;
            Refresh();
        });
        _lineChooserPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Children =
            {
                ToolButton(ArrowIcon(), "箭头 (A)", () =>
                {
                    _lineChoosing = false;
                    _state.Tool = EditorTool.Arrow;
                    Refresh();
                }),
                ToolButton(PolylineIcon(), "折线 (L)", () =>
                {
                    _lineChoosing = false;
                    _state.Tool = EditorTool.Polyline;
                    Refresh();
                }),
            },
        };

        _linePropertyRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                _lineSwitchButton, _lineChooserPanel, Separator(),
                lineDashHost, startCapHost, endCapHost,
                startThickButton, endThickButton, _splineCheck, lineOpacityButton,
                Separator(), _lineSwatchPanel, lineColorPick,
            },
        };

        // ---- 文字属性行 ----

        _fontNameText = new TextBlock
        {
            FontSize = 12,
            MaxWidth = 96,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var fontButton = FontListButton();

        _fontSizeText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 20 };
        var fontSizeButton = SliderPopupButton(
            GlyphIcon("A"), _fontSizeText, "字号",
            min: _state.FontSizeMin, max: _state.FontSizeMax,
            get: () => _state.CurrentTextStyle.FontSize,
            setLive: v => _state.ModifyTextStyleLive(s => s with { FontSize = Math.Round(v) }));

        _boldButton = ToolButton(new TextBlock
        {
            Text = "B", FontWeight = FontWeight.Bold, FontSize = 15,
            Foreground = new SolidColorBrush(IconColor),
        }, "加粗", () => _state.ModifyTextStyle(s => s with { Bold = !s.Bold }));
        _boldButton.Width = 30;
        _boldButton.Height = 30;
        _italicButton = ToolButton(new TextBlock
        {
            Text = "I", FontStyle = FontStyle.Italic, FontSize = 15,
            Foreground = new SolidColorBrush(IconColor),
        }, "斜体", () => _state.ModifyTextStyle(s => s with { Italic = !s.Italic }));
        _italicButton.Width = 30;
        _italicButton.Height = 30;

        var halignHost = PaletteButton(
            () => (int)_state.CurrentTextStyle.HAlign,
            i => _state.ModifyTextStyle(s => s with { HAlign = (TextHAlign)i }),
            3, i => AlignIcon(horizontal: true, i), out _halignButton);
        var valignHost = PaletteButton(
            () => (int)_state.CurrentTextStyle.VAlign,
            i => _state.ModifyTextStyle(s => s with { VAlign = (TextVAlign)i }),
            3, i => AlignIcon(horizontal: false, i), out _valignButton);

        var boxSubmenu = SubmenuButton("文本框", BuildBoxSubmenu);
        var strokeSubmenu = SubmenuButton("描边", BuildStrokeSubmenu);

        _textSwatchPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var textColorPick = new ColorPickButton(
            () => _state.CurrentTextStyle.Color,
            c => _state.ModifyTextStyleLive(s => s with { Color = c }),
            _state.BeginContinuousStyle, _state.EndContinuousStyle)
        {
            VerticalAlignment = VerticalAlignment.Center,
        };

        _textPropertyRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                fontButton, fontSizeButton, _boldButton, _italicButton,
                halignHost, valignHost, boxSubmenu, strokeSubmenu,
                Separator(), _textSwatchPanel, textColorPick,
            },
        };

        // ---- 编号属性行 ----

        var numberStyleHost = PaletteButton(
            () => _state.CurrentNumberStyle.Hollow ? 1 : 0,
            i => _state.ModifyNumberStyle(s => s with { Hollow = i == 1 }),
            2, i => BadgePreview(hollow: i == 1), out _numberStyleButton);

        _numberNextText = new TextBlock
        {
            FontSize = 13,
            MinWidth = 18,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var numberSpinner = NumberSpinnerButton(_numberNextText);

        var numberKindHost = PaletteButton(
            () => (int)_state.CurrentNumberStyle.Kind,
            i => _state.ModifyNumberStyle(s => s with { Kind = (NumberKind)i }),
            KindGlyphs.Length, KindPreview, out _numberKindButton);

        _numberSizeText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 24 };
        var numberSizeButton = SliderPopupButton(
            BadgeSizeIcon(), _numberSizeText, "大小",
            min: 10, max: 200,
            get: () => _state.CurrentNumberStyle.Diameter,
            setLive: v => _state.ModifyNumberStyleLive(s => s with { Diameter = Math.Round(v) }));

        _numberSwatchPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var numberColorPick = new ColorPickButton(
            () => _state.CurrentNumberStyle.Color,
            c => _state.ModifyNumberStyleLive(s => s with { Color = c }),
            _state.BeginContinuousStyle, _state.EndContinuousStyle)
        {
            VerticalAlignment = VerticalAlignment.Center,
        };

        _numberPropertyRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                numberStyleHost, numberSpinner, numberKindHost, numberSizeButton,
                Separator(), _numberSwatchPanel, numberColorPick,
            },
        };

        // ---- 区域模糊属性行 ----

        // 像素大小/模糊半径滑条直接内联在属性行里（少一级弹层）
        _blockSizeText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 20 };
        _blockSlider = new MiniSlider { Minimum = 1, Maximum = 50, Width = 110 };
        _blockSizeHost = InlineSlider(PixelateIcon(16), "像素大小", _blockSlider, _blockSizeText,
            v => _state.ModifyMosaicStyleLive(s => s with { BlockSize = Math.Round(v) }));

        _blurRadiusText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 20 };
        _blurSlider = new MiniSlider { Minimum = 1, Maximum = 50, Width = 110 };
        _blurRadiusHost = InlineSlider(BlurIcon(16), "模糊半径", _blurSlider, _blurRadiusText,
            v => _state.ModifyMosaicStyleLive(s => s with { BlurRadius = Math.Round(v) }));

        // 组内工具选择器（属性行最左侧，PS 式就地切换）：像素化↔模糊化
        _mosaicSwitchButton = ToolButton(PixelateIcon(), "切换 像素化 / 模糊化", () =>
        {
            _mosaicChoosing = true;
            Refresh();
        });
        _mosaicChooserPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Children =
            {
                ToolButton(PixelateIcon(), "像素化 (M)", () =>
                {
                    _mosaicChoosing = false;
                    _state.Tool = EditorTool.Pixelate;
                    Refresh();
                }),
                ToolButton(BlurIcon(), "模糊化 (B)", () =>
                {
                    _mosaicChoosing = false;
                    _state.Tool = EditorTool.Blur;
                    Refresh();
                }),
            },
        };

        _mosaicPropertyRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                _mosaicSwitchButton, _mosaicChooserPanel, Separator(),
                _blockSizeHost, _blurRadiusHost,
            },
        };

        _toolRow = toolRow;
        _rowsPanel = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                toolRow, _propertyRow, _linePropertyRow, _textPropertyRow, _numberPropertyRow,
                _mosaicPropertyRow,
            },
        };
        Root = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
            BoxShadow = BoxShadows.Parse("0 2 8 0 #33000000"),
            Cursor = new Cursor(StandardCursorType.Arrow),
            Child = _rowsPanel,
        };

        PopupPlacement.ToolbarContainer = Root; // 一级弹层的堆叠基准（跳过整条工具条）

        _state.ToolChanged += Refresh;
        _state.StyleChanged += Refresh;
        _model.Changed += Refresh;
        Refresh();
    }

    private void Refresh()
    {
        _refreshing = true;
        try
        {
            var style = _state.CurrentStyle;
            var lineStyle = _state.CurrentLineStyle;
            _selectButton.Background = new SolidColorBrush(
                _state.Tool == EditorTool.Select ? ActiveBackground : Colors.Transparent);
            _shapeButton.Background = new SolidColorBrush(
                _state.Tool == EditorTool.Shape ? ActiveBackground : Colors.Transparent);
            _lineToolButton.Background = new SolidColorBrush(
                _state.Tool is EditorTool.Arrow or EditorTool.Polyline
                    ? ActiveBackground : Colors.Transparent);
            _textToolButton.Background = new SolidColorBrush(
                _state.Tool == EditorTool.Text ? ActiveBackground : Colors.Transparent);
            _numberToolButton.Background = new SolidColorBrush(
                _state.Tool == EditorTool.Number ? ActiveBackground : Colors.Transparent);
            _mosaicToolButton.Background = new SolidColorBrush(
                _state.Tool is EditorTool.Pixelate or EditorTool.Blur
                    ? ActiveBackground : Colors.Transparent);
            _undoButton.IsEnabled = _model.CanUndo;
            _redoButton.IsEnabled = _model.CanRedo;

            var showShapeProps = _state.Tool == EditorTool.Shape || _model.Selected is ShapeElement;
            var showLineProps = _state.Tool is EditorTool.Arrow or EditorTool.Polyline
                                || _model.Selected is LineElement;
            var showTextProps = _state.Tool == EditorTool.Text || _model.Selected is TextElement;
            var showNumberProps = _state.Tool == EditorTool.Number
                                  || _model.Selected is NumberElement;
            var showMosaicProps = _state.Tool is EditorTool.Pixelate or EditorTool.Blur
                                  || _model.Selected is MosaicElement;
            // 属性行内只显示当前模式对应的滑条（像素大小 / 模糊半径）
            var showBlockSize = MosaicToolIndex() == 0;
            // 弧线只对折线有意义（箭头固定两点，样条无效果）
            var showSpline = _state.Tool == EditorTool.Polyline
                             || (_model.Selected is LineElement { IsArrowTool: false });
            // 行隐藏时退出就地选择态
            _lineChoosing &= showLineProps;
            _mosaicChoosing &= showMosaicProps;
            if (_propertyRow.IsVisible != showShapeProps
                || _linePropertyRow.IsVisible != showLineProps
                || _textPropertyRow.IsVisible != showTextProps
                || _numberPropertyRow.IsVisible != showNumberProps
                || _mosaicPropertyRow.IsVisible != showMosaicProps
                || _blockSizeHost.IsVisible != (showBlockSize && !_mosaicChoosing)
                || _blurRadiusHost.IsVisible != (!showBlockSize && !_mosaicChoosing)
                || _splineCheck.IsVisible != (showSpline && !_lineChoosing)
                || _lineChooserPanel.IsVisible != _lineChoosing
                || _mosaicChooserPanel.IsVisible != _mosaicChoosing)
            {
                _propertyRow.IsVisible = showShapeProps;
                _linePropertyRow.IsVisible = showLineProps;
                _textPropertyRow.IsVisible = showTextProps;
                _numberPropertyRow.IsVisible = showNumberProps;
                _mosaicPropertyRow.IsVisible = showMosaicProps;
                // 就地选择态：整行只显示两个工具选择按钮，其余全部隐藏
                foreach (var child in _linePropertyRow.Children)
                    child.IsVisible = _lineChoosing
                        ? child == _lineChooserPanel : child != _lineChooserPanel;
                foreach (var child in _mosaicPropertyRow.Children)
                    child.IsVisible = _mosaicChoosing
                        ? child == _mosaicChooserPanel : child != _mosaicChooserPanel;
                if (!_lineChoosing)
                    _splineCheck.IsVisible = showSpline;
                if (!_mosaicChoosing)
                {
                    _blockSizeHost.IsVisible = showBlockSize;
                    _blurRadiusHost.IsVisible = !showBlockSize;
                }
                LayoutChanged?.Invoke();
            }

            _fillCheck.IsChecked = style.Filled;
            _shapeDashButton.Content = WithChevron(DashPreview(style.LineStyleIndex, 30));
            _thicknessText.Text = ((int)style.Thickness).ToString();
            _radiusText.Text = ((int)style.CornerRadiusPercent).ToString();
            _rotationText.Text = $"{(int)style.RotationDeg}°";
            _opacityText.Text = ((int)style.Opacity).ToString();
            RefreshSwatches(_swatchPanel, style.Color, c => _state.ModifyStyle(s => s with { Color = c }));

            _lineDashButton.Content = WithChevron(DashPreview(lineStyle.LineStyleIndex, 30));
            _startCapButton.Content = WithChevron(
                new CapPreview { Kind = lineStyle.StartCap, AtStart = true, Width = 20 });
            _endCapButton.Content = WithChevron(
                new CapPreview { Kind = lineStyle.EndCap, AtStart = false, Width = 20 });
            _startThickText.Text = ((int)lineStyle.StartThickness).ToString();
            _endThickText.Text = ((int)lineStyle.EndThickness).ToString();
            _splineCheck.IsChecked = lineStyle.Spline;
            _lineOpacityText.Text = ((int)lineStyle.Opacity).ToString();
            RefreshSwatches(_lineSwatchPanel, lineStyle.Color,
                c => _state.ModifyLineStyle(s => s with { Color = c }));

            var textStyle = _state.CurrentTextStyle;
            _fontNameText.Text = textStyle.FontFamily;
            _fontSizeText.Text = ((int)textStyle.FontSize).ToString();
            _boldButton.Background = new SolidColorBrush(
                textStyle.Bold ? ActiveBackground : Colors.Transparent);
            _italicButton.Background = new SolidColorBrush(
                textStyle.Italic ? ActiveBackground : Colors.Transparent);
            _halignButton.Content = WithChevron(AlignIcon(horizontal: true, (int)textStyle.HAlign));
            _valignButton.Content = WithChevron(AlignIcon(horizontal: false, (int)textStyle.VAlign));
            RefreshSwatches(_textSwatchPanel, textStyle.Color,
                c => _state.ModifyTextStyle(s => s with { Color = c }));

            var numberStyle = _state.CurrentNumberStyle;
            _numberStyleButton.Content = WithChevron(BadgePreview(numberStyle.Hollow));
            _numberKindButton.Content = WithChevron(KindPreview((int)numberStyle.Kind));
            _numberSizeText.Text = ((int)numberStyle.Diameter).ToString();
            _numberNextText.Text = _state.NextNumber(numberStyle.Kind).ToString();
            RefreshSwatches(_numberSwatchPanel, numberStyle.Color,
                c => _state.ModifyNumberStyle(s => s with { Color = c }));

            var mosaicStyle = _state.CurrentMosaicStyle;
            _blockSizeText.Text = ((int)mosaicStyle.BlockSize).ToString();
            _blurRadiusText.Text = ((int)mosaicStyle.BlurRadius).ToString();
            _blockSlider.Value = mosaicStyle.BlockSize;   // 代码赋值不回触发 ValueChanged
            _blurSlider.Value = mosaicStyle.BlurRadius;
            _lineSwitchButton.Content = WithCornerArrow(
                LineToolIndex() == 1 ? PolylineIcon() : ArrowIcon());
            _mosaicSwitchButton.Content = WithCornerArrow(
                MosaicToolIndex() == 1 ? BlurIcon() : PixelateIcon());
            // 就地选择态中高亮当前工具
            ((Button)_lineChooserPanel.Children[0]).Background = new SolidColorBrush(
                LineToolIndex() == 0 ? ActiveBackground : Colors.Transparent);
            ((Button)_lineChooserPanel.Children[1]).Background = new SolidColorBrush(
                LineToolIndex() == 1 ? ActiveBackground : Colors.Transparent);
            ((Button)_mosaicChooserPanel.Children[0]).Background = new SolidColorBrush(
                MosaicToolIndex() == 0 ? ActiveBackground : Colors.Transparent);
            ((Button)_mosaicChooserPanel.Children[1]).Background = new SolidColorBrush(
                MosaicToolIndex() == 1 ? ActiveBackground : Colors.Transparent);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RefreshSwatches(StackPanel panel, Color current, Action<Color> pick)
    {
        panel.Children.Clear();
        foreach (var color in _state.PresetColors)
        {
            var c = color;
            var button = new Button
            {
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                Content = RingedSwatch(c, ringed: c == current, size: 16),
            };
            ToolTip.SetTip(button, $"#{c.R:X2}{c.G:X2}{c.B:X2}");
            button.Click += (_, _) => pick(c);
            panel.Children.Add(button);
        }
    }

    /// <summary>色块：彩虹渐变外框 + 1px 白色间隔 + 颜色方块（未选中时外框透明占位，布局稳定）。</summary>
    private static Control RingedSwatch(Color color, bool ringed, double size)
    {
        var rainbow = new ConicGradientBrush
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
        };
        return new Border
        {
            BorderThickness = new Thickness(2),
            BorderBrush = ringed ? rainbow : Brushes.Transparent,
            CornerRadius = new CornerRadius(5),
            Child = new Border
            {
                // 彩虹框与色块之间的 1px 白色间隔
                BorderThickness = new Thickness(1),
                BorderBrush = ringed ? Brushes.White : Brushes.Transparent,
                CornerRadius = new CornerRadius(3.5),
                Child = new Border
                {
                    Width = size,
                    Height = size,
                    CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(color),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)),
                },
            },
        };
    }

    /// <summary>
    /// 带悬浮弹层（迷你滑条 + 数值显示）的属性按钮。悬浮/点击打开；
    /// 只有当指针既不在按钮也不在弹层上、且滑条不在拖拽中时才关闭
    ///（看门狗每 300ms 复查，拖拽期间指针捕获导致的 IsPointerOver 失真不会误关）。
    /// 在按钮或弹层上滚轮可直接调值。
    /// </summary>
    private Control SliderPopupButton(
        Control icon, TextBlock valueText, string label,
        double min, double max,
        Func<double> get, Action<double> setLive,
        (string Text, Action Run)? extra = null)
    {
        var button = new Button
        {
            Height = 30,
            Padding = new Thickness(6, 0),
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(5),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { icon, valueText },
            },
        };
        AddStateBackground(button, ":pointerover", Color.FromRgb(0xEA, 0xEA, 0xEA));
        AddStateBackground(button, ":pressed", Color.FromRgb(0xD4, 0xD4, 0xD4));
        // 不加 ToolTip：气泡会悬在按钮上方使 IsPointerOver 失真，导致看门狗误关弹层；
        // 弹层内已有名称标签
        var slider = new MiniSlider { Minimum = min, Maximum = max };
        var popupValue = new TextBlock
        {
            FontSize = 12,
            MinWidth = 24,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        slider.ValueChanged += v =>
        {
            popupValue.Text = ((int)v).ToString();
            setLive(v);
        };

        var content = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8),
            BoxShadow = BoxShadows.Parse("0 2 8 0 #33000000"),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = label,
                                FontSize = 12,
                                VerticalAlignment = VerticalAlignment.Center,
                            },
                            slider, popupValue,
                        },
                    },
                    new TextBlock
                    {
                        Text = "滚轮可调",
                        FontSize = 10,
                        Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0)),
                    },
                },
            },
        };
        if (extra is { } ex)
        {
            var applyButton = new Button
            {
                Content = ex.Text,
                FontSize = 11,
                Padding = new Thickness(8, 3),
                CornerRadius = new CornerRadius(4),
            };
            applyButton.Click += (_, _) => ex.Run();
            ((StackPanel)content.Child!).Children.Insert(1, applyButton);
        }

        var popup = new Popup
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 2,
            IsLightDismissEnabled = false,
            Child = content,
        };

        void SyncFromState()
        {
            slider.Value = get();
            popupValue.Text = ((int)get()).ToString();
        }

        void Open()
        {
            if (popup.IsOpen)
                return;
            _state.BeginContinuousStyle();
            SyncFromState();
            PopupPlacement.Adjust(popup, button, content, minHeightDip: 90, minWidthDip: 240);
            popup.IsOpen = true;
            WatchClose();
        }

        void Close()
        {
            if (!popup.IsOpen)
                return;
            popup.IsOpen = false;
            _state.EndContinuousStyle();
        }

        // 弹层不再贴着按钮（跳过整条工具条），指针从按钮移向弹层要跨过工具条条带，
        // 给一次宽限（连续两轮不在上面才关）避免途中误关
        var watchMisses = 0;
        void WatchClose() => DispatcherTimer.RunOnce(() =>
        {
            if (!popup.IsOpen)
                return;
            if (slider.IsDragging || button.IsPointerOver || content.IsPointerOver)
            {
                watchMisses = 0;
                WatchClose();
                return;
            }
            if (++watchMisses < 2)
            {
                WatchClose();
                return;
            }
            watchMisses = 0;
            Close();
        }, TimeSpan.FromMilliseconds(300));

        button.PointerEntered += (_, _) => Open();
        button.Click += (_, _) => Open();
        void WheelAdjust(PointerWheelEventArgs e)
        {
            Open();
            var v = Math.Clamp(get() + (e.Delta.Y > 0 ? 1 : -1), min, max);
            setLive(v);
            SyncFromState();
            e.Handled = true;
        }
        button.PointerWheelChanged += (_, e) => WheelAdjust(e);
        content.PointerWheelChanged += (_, e) =>
        {
            if (!e.Handled) // 滑条自身已处理的不重复
                WheelAdjust(e);
        };

        return new Panel { Children = { button, popup } };
    }

    /// <summary>
    /// 横向调色板式选择按钮：按钮显示当前项预览，悬浮/点击弹出横向选项条
    ///（子工具条样式，与滑条弹层同一套看门狗关闭逻辑）。
    /// </summary>
    private Control PaletteButton(
        Func<int> get, Action<int> set, int count, Func<int, Control> preview, out Button button)
    {
        var btn = new Button
        {
            Height = 30,
            Padding = new Thickness(5, 0),
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(5),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        AddStateBackground(btn, ":pointerover", Color.FromRgb(0xEA, 0xEA, 0xEA));
        AddStateBackground(btn, ":pressed", Color.FromRgb(0xD4, 0xD4, 0xD4));

        var itemsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var content = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4),
            BoxShadow = BoxShadows.Parse("0 2 8 0 #33000000"),
            Cursor = new Cursor(StandardCursorType.Arrow),
            Child = itemsPanel,
        };
        var popup = new Popup
        {
            PlacementTarget = btn,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 2,
            IsLightDismissEnabled = false,
            Child = content,
        };

        void Rebuild()
        {
            itemsPanel.Children.Clear();
            for (var i = 0; i < count; i++)
            {
                var idx = i;
                var item = new Button
                {
                    Padding = new Thickness(3),
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(
                        idx == get() ? ActiveBackground : Colors.Transparent),
                    Content = preview(idx),
                };
                AddStateBackground(item, ":pointerover", Color.FromRgb(0xEA, 0xEA, 0xEA));
                item.Click += (_, _) =>
                {
                    set(idx);
                    popup.IsOpen = false;
                };
                itemsPanel.Children.Add(item);
            }
        }

        var watchMisses = 0;
        void WatchClose() => DispatcherTimer.RunOnce(() =>
        {
            if (!popup.IsOpen)
                return;
            if (btn.IsPointerOver || content.IsPointerOver)
            {
                watchMisses = 0;
                WatchClose();
                return;
            }
            if (++watchMisses < 2)
            {
                WatchClose();
                return;
            }
            watchMisses = 0;
            popup.IsOpen = false;
        }, TimeSpan.FromMilliseconds(300));

        void Open()
        {
            if (popup.IsOpen)
                return;
            Rebuild();
            PopupPlacement.Adjust(popup, btn, content,
                minHeightDip: 55, minWidthDip: count * 36 + 12);
            popup.IsOpen = true;
            WatchClose();
        }

        btn.PointerEntered += (_, _) => Open();
        btn.Click += (_, _) => Open();
        button = btn;
        return new Panel { Children = { btn, popup } };
    }

    /// <summary>字体选择按钮：弹出可滚动字体名列表（约 10 项可见，不做字体预览）。</summary>
    private Control FontListButton()
    {
        var button = new Button
        {
            Height = 30,
            Padding = new Thickness(6, 0),
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(5),
            Content = WithChevron(_fontNameText),
        };
        AddStateBackground(button, ":pointerover", Color.FromRgb(0xEA, 0xEA, 0xEA));
        AddStateBackground(button, ":pressed", Color.FromRgb(0xD4, 0xD4, 0xD4));

        var content = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4),
            BoxShadow = BoxShadows.Parse("0 2 8 0 #33000000"),
            Cursor = new Cursor(StandardCursorType.Arrow),
        };
        var popup = new Popup
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 2,
            IsLightDismissEnabled = false,
            Child = content,
        };

        var watchMisses = 0;
        void WatchClose() => DispatcherTimer.RunOnce(() =>
        {
            if (!popup.IsOpen)
                return;
            if (button.IsPointerOver || content.IsPointerOver)
            {
                watchMisses = 0;
                WatchClose();
                return;
            }
            if (++watchMisses < 2)
            {
                WatchClose();
                return;
            }
            watchMisses = 0;
            popup.IsOpen = false;
        }, TimeSpan.FromMilliseconds(300));

        void Open()
        {
            if (popup.IsOpen)
                return;
            _systemFonts ??= FontManager.Current.SystemFonts
                .Select(f => f.Name)
                .Where(n => n.Length > 0)
                .Distinct()
                .OrderBy(n => n, StringComparer.CurrentCulture)
                .ToArray();
            var list = new ListBox
            {
                ItemsSource = _systemFonts,
                SelectedItem = _state.CurrentTextStyle.FontFamily,
                Width = 230,
                MaxHeight = 320, // 约 10 项，其余滚动
                FontSize = 13,
            };
            list.SelectionChanged += (_, _) =>
            {
                if (list.SelectedItem is string name && name != _state.CurrentTextStyle.FontFamily)
                {
                    _state.ModifyTextStyle(s => s with { FontFamily = name });
                    popup.IsOpen = false;
                }
            };
            content.Child = list;
            PopupPlacement.Adjust(popup, button, content, minHeightDip: 350, minWidthDip: 250);
            popup.IsOpen = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => list.ScrollIntoView(list.SelectedItem!), DispatcherPriority.Background);
            WatchClose();
        }

        button.PointerEntered += (_, _) => Open();
        button.Click += (_, _) => Open();
        return new Panel { Children = { button, popup } };
    }

    /// <summary>子菜单按钮（外框/描边）：悬浮弹出面板，打开时重建内容以同步当前值；
    /// 面板内滑条走连续修改会话（打开快照、关闭合并入撤销栈）。</summary>
    private Control SubmenuButton(string label, Func<Control> buildContent)
    {
        var button = new Button
        {
            Height = 30,
            Padding = new Thickness(8, 0),
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(5),
            Content = WithChevron(new TextBlock
            {
                Text = label,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            }),
        };
        AddStateBackground(button, ":pointerover", Color.FromRgb(0xEA, 0xEA, 0xEA));
        AddStateBackground(button, ":pressed", Color.FromRgb(0xD4, 0xD4, 0xD4));

        var content = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8),
            BoxShadow = BoxShadows.Parse("0 2 8 0 #33000000"),
            Cursor = new Cursor(StandardCursorType.Arrow),
        };
        var popup = new Popup
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 2,
            IsLightDismissEnabled = false,
            Child = content,
        };

        void Close()
        {
            if (!popup.IsOpen)
                return;
            popup.IsOpen = false;
            _state.EndContinuousStyle();
        }

        var watchMisses = 0;
        void WatchClose() => DispatcherTimer.RunOnce(() =>
        {
            if (!popup.IsOpen)
                return;
            // 嵌套的取色弹层打开时（独立视觉根，IsPointerOver 探不到）保持子菜单不关
            if (button.IsPointerOver || content.IsPointerOver || ColorPickButton.AnyOpen)
            {
                watchMisses = 0;
                WatchClose();
                return;
            }
            if (++watchMisses < 2)
            {
                WatchClose();
                return;
            }
            watchMisses = 0;
            Close();
        }, TimeSpan.FromMilliseconds(300));

        void Open()
        {
            if (popup.IsOpen)
                return;
            _state.BeginContinuousStyle();
            content.Child = buildContent();
            PopupPlacement.Adjust(popup, button, content, minHeightDip: 200, minWidthDip: 300);
            popup.IsOpen = true;
            WatchClose();
        }

        button.PointerEntered += (_, _) => Open();
        button.Click += (_, _) => Open();
        return new Panel { Children = { button, popup } };
    }

    /// <summary>子菜单里的一行滑条（标签 + MiniSlider + 数值）。</summary>
    private static Control SliderRow(
        string label, double min, double max, double value, Action<double> setLive)
    {
        var slider = new MiniSlider { Minimum = min, Maximum = max, Width = 120, Value = value };
        var text = new TextBlock
        {
            Text = ((int)value).ToString(),
            FontSize = 12,
            MinWidth = 24,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        slider.ValueChanged += v =>
        {
            text.Text = ((int)v).ToString();
            setLive(v);
        };
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = label, FontSize = 12, Width = 52,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                slider, text,
            },
        };
    }

    /// <summary>子菜单里的一行色板（标签 + 预设色小方块 + 自定义取色）。</summary>
    private Control ColorRow(string label, Func<Color> get, Action<Color> pick, Action<Color> pickLive)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        var current = get();
        foreach (var color in _state.PresetColors)
        {
            var c = color;
            var b = new Button
            {
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                Content = RingedSwatch(c, ringed: c == current, size: 13),
            };
            b.Click += (_, _) => pick(c);
            row.Children.Add(b);
        }
        // 子菜单本身已运行连续修改会话，取色器不再嵌套开启（Live 修改置脏、关闭时合并入栈）
        row.Children.Add(new ColorPickButton(get, pickLive, buttonSize: 22)
        {
            VerticalAlignment = VerticalAlignment.Center,
        });
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = label, FontSize = 12, Width = 52,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                row,
            },
        };
    }

    private Control BuildBoxSubmenu()
    {
        var style = _state.CurrentTextStyle;
        var enable = new CheckBox { Content = "填充文本框", FontSize = 12, IsChecked = style.BoxEnabled };
        enable.IsCheckedChanged += (_, _) =>
        {
            var on = enable.IsChecked == true;
            _state.ModifyTextStyle(s => s with { BoxEnabled = on });
        };
        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                enable,
                ColorRow("颜色", () => _state.CurrentTextStyle.BoxColor,
                    c => _state.ModifyTextStyle(s => s with { BoxColor = c, BoxEnabled = true }),
                    c => _state.ModifyTextStyleLive(s => s with { BoxColor = c, BoxEnabled = true })),
                SliderRow("圆角", 0, 100, style.BoxRadiusPercent,
                    v => _state.ModifyTextStyleLive(s => s with { BoxRadiusPercent = Math.Round(v) })),
                SliderRow("透明度", 0, 100, style.BoxOpacity,
                    v => _state.ModifyTextStyleLive(s => s with { BoxOpacity = Math.Round(v) })),
                SliderRow("内边距", 0, 60, style.Padding,
                    v => _state.ModifyTextStyleLive(s => s with { Padding = Math.Round(v) })),
            },
        };
    }

    private Control BuildStrokeSubmenu()
    {
        var style = _state.CurrentTextStyle;
        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                ColorRow("颜色", () => _state.CurrentTextStyle.StrokeColor,
                    c => _state.ModifyTextStyle(s => s with { StrokeColor = c }),
                    c => _state.ModifyTextStyleLive(s => s with { StrokeColor = c })),
                SliderRow("粗细", 0, 20, style.StrokeThickness,
                    v => _state.ModifyTextStyleLive(s => s with { StrokeThickness = Math.Round(v) })),
                SliderRow("透明度", 0, 100, style.StrokeOpacity,
                    v => _state.ModifyTextStyleLive(s => s with { StrokeOpacity = Math.Round(v) })),
                new TextBlock
                {
                    Text = "粗细 0 = 无描边",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0)),
                },
            },
        };
    }

    /// <summary>
    /// 工具条左端拖拽手柄（两列圆点）：按住拖动整个工具条；拖过之后
    /// 窗口不再自动停靠（ManuallyPositioned），位置钳在窗口内。
    /// </summary>
    private Control BuildGrip()
    {
        var dots = new Canvas { Width = 8, Height = 20 };
        for (var row = 0; row < 3; row++)
        {
            for (var col = 0; col < 2; col++)
            {
                var dot = new Avalonia.Controls.Shapes.Ellipse
                {
                    Width = 2.6,
                    Height = 2.6,
                    Fill = new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xA8)),
                };
                Canvas.SetLeft(dot, col * 5);
                Canvas.SetTop(dot, 1 + row * 7);
                dots.Children.Add(dot);
            }
        }
        var grip = new Border
        {
            Width = 16,
            Background = Brushes.Transparent, // 命中需要非 null 背景
            Cursor = new Cursor(StandardCursorType.SizeAll),
            Child = new Panel
            {
                Children = { dots },
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        ToolTip.SetTip(grip, "拖动工具条");

        var dragging = false;
        var start = new Point();
        double originX = 0, originY = 0;
        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed
                || Root.GetVisualParent() is not Visual parent)
                return;
            dragging = true;
            start = e.GetPosition(parent); // 用父画布坐标：工具条自身移动不影响基准
            originX = Canvas.GetLeft(Root);
            originY = Canvas.GetTop(Root);
            e.Pointer.Capture(grip);
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (!dragging || Root.GetVisualParent() is not Control parent)
                return;
            var p = e.GetPosition(parent);
            ManuallyPositioned = true;
            var nx = Math.Clamp(originX + (p.X - start.X),
                0, Math.Max(0, parent.Bounds.Width - Root.Bounds.Width));
            var ny = Math.Clamp(originY + (p.Y - start.Y),
                0, Math.Max(0, parent.Bounds.Height - Root.Bounds.Height));
            Canvas.SetLeft(Root, nx);
            Canvas.SetTop(Root, ny);
            // 拖动即时刷新弹层统一堆叠方向（含内部行序）
            if (TopLevel.GetTopLevel(Root) is Window window)
            {
                PopupPlacement.UpdateDirection(window, nx, ny, Root.Bounds.Size);
                ApplyStackDirection(PopupPlacement.StackUp);
            }
            e.Handled = true;
        };
        grip.PointerReleased += (_, e) =>
        {
            dragging = false;
            e.Pointer.Capture(null);
        };
        return grip;
    }

    /// <summary>
    /// 行内滑条（图标 + 滑条 + 数值直接嵌在属性行，不经弹层）。
    /// 拖拽走连续修改会话合并为一条撤销；滚轮/轨道单击立即入栈。
    /// </summary>
    private Control InlineSlider(
        Control icon, string tip, MiniSlider slider, TextBlock valueText, Action<double> setLive)
    {
        icon.VerticalAlignment = VerticalAlignment.Center;
        slider.VerticalAlignment = VerticalAlignment.Center;
        slider.ValueChanged += v =>
        {
            valueText.Text = ((int)v).ToString();
            _state.BeginContinuousStyle();
            setLive(v);
            if (!slider.IsDragging)
                _state.EndContinuousStyle();
        };
        slider.DragEnded += () => _state.EndContinuousStyle();
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { icon, slider, valueText },
        };
        ToolTip.SetTip(panel, tip);
        return panel;
    }

    /// <summary>
    /// 下一个编号调节器：数值 + 右侧上下两个小箭头（也支持滚轮）。
    /// 调整的是当前标号类型"下一个放置"的编号，每种类型序列独立。
    /// </summary>
    private Control NumberSpinnerButton(TextBlock valueText)
    {
        Button Arrow(string glyph, int delta)
        {
            var b = new Button
            {
                Width = 16,
                Height = 13,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(3),
                Background = Brushes.Transparent,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Content = new TextBlock
                {
                    Text = glyph,
                    FontSize = 7,
                    Foreground = new SolidColorBrush(IconColor),
                },
            };
            AddStateBackground(b, ":pointerover", Color.FromRgb(0xEA, 0xEA, 0xEA));
            AddStateBackground(b, ":pressed", Color.FromRgb(0xD4, 0xD4, 0xD4));
            b.Click += (_, _) =>
            {
                var kind = _state.CurrentNumberStyle.Kind;
                _state.SetNextNumber(kind, _state.NextNumber(kind) + delta);
            };
            return b;
        }

        var host = new Border
        {
            Height = 30,
            Padding = new Thickness(6, 0),
            CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 3,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    valueText,
                    new StackPanel
                    {
                        Spacing = 1,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children = { Arrow("▲", 1), Arrow("▼", -1) },
                    },
                },
            },
        };
        ToolTip.SetTip(host, "下一个编号");
        host.PointerWheelChanged += (_, e) =>
        {
            var kind = _state.CurrentNumberStyle.Kind;
            _state.SetNextNumber(kind, _state.NextNumber(kind) + (e.Delta.Y > 0 ? 1 : -1));
            e.Handled = true;
        };
        return host;
    }

    /// <summary>编号形制预览：实心圆白字 / 空心圆同色字。</summary>
    private static Control BadgePreview(bool hollow)
    {
        var brush = new SolidColorBrush(IconColor);
        var circle = new Avalonia.Controls.Shapes.Ellipse { Width = 18, Height = 18 };
        if (hollow)
        {
            circle.Stroke = brush;
            circle.StrokeThickness = 2;
        }
        else
        {
            circle.Fill = brush;
        }
        return new Grid
        {
            Width = 18,
            Height = 18,
            Children =
            {
                circle,
                new TextBlock
                {
                    Text = "1",
                    FontSize = 10,
                    FontWeight = FontWeight.Bold,
                    Foreground = hollow ? brush : Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
    }

    /// <summary>标号类型预览：单个代表字符（1/A/a/i/I/一）。</summary>
    private static Control KindPreview(int index) => new TextBlock
    {
        Text = KindGlyphs[Math.Clamp(index, 0, KindGlyphs.Length - 1)],
        FontSize = 14,
        FontWeight = FontWeight.Bold,
        Width = 16,
        TextAlignment = TextAlignment.Center,
        Foreground = new SolidColorBrush(IconColor),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>编号工具图标：空心圆 + 数字 1。</summary>
    private static Control NumberToolIcon()
    {
        var brush = new SolidColorBrush(IconColor);
        return new Grid
        {
            Width = 22,
            Height = 22,
            Children =
            {
                new Avalonia.Controls.Shapes.Ellipse
                {
                    Width = 19, Height = 19, Stroke = brush, StrokeThickness = 2,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                new TextBlock
                {
                    Text = "1",
                    FontSize = 11,
                    FontWeight = FontWeight.Bold,
                    Foreground = brush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
    }

    /// <summary>徽章大小图标：一小一大两个实心圆。</summary>
    private static Control BadgeSizeIcon()
    {
        var brush = new SolidColorBrush(IconColor);
        var small = new Avalonia.Controls.Shapes.Ellipse { Width = 5, Height = 5, Fill = brush };
        Canvas.SetLeft(small, 1);
        Canvas.SetTop(small, 9);
        var big = new Avalonia.Controls.Shapes.Ellipse { Width = 11, Height = 11, Fill = brush };
        Canvas.SetLeft(big, 7);
        Canvas.SetTop(big, 3);
        return new Canvas { Width = 18, Height = 16, Children = { small, big } };
    }

    /// <summary>对齐图标：三条线按左/中/右（或上/中/下）对齐。</summary>
    private static Control AlignIcon(bool horizontal, int index)
    {
        var canvas = new Canvas { Width = 18, Height = 16 };
        double[] lens = [12, 8, 12];
        for (var i = 0; i < 3; i++)
        {
            var len = lens[i];
            var bar = new Avalonia.Controls.Shapes.Rectangle
            {
                Fill = new SolidColorBrush(IconColor),
                RadiusX = 1, RadiusY = 1,
            };
            if (horizontal)
            {
                bar.Width = len;
                bar.Height = 2;
                Canvas.SetTop(bar, 3 + i * 4);
                Canvas.SetLeft(bar, index switch
                {
                    1 => (18 - len) / 2,   // 居中
                    2 => 16 - len,         // 右
                    _ => 2,                // 左
                });
            }
            else
            {
                bar.Width = 2;
                bar.Height = len;
                Canvas.SetLeft(bar, 3 + i * 4);
                Canvas.SetTop(bar, index switch
                {
                    1 => (16 - len) / 2,   // 居中
                    2 => 14 - len,         // 下
                    _ => 2,                // 上
                });
            }
            canvas.Children.Add(bar);
        }
        return canvas;
    }

    /// <summary>当前项预览 + 下拉指示小箭头。</summary>
    private static Control WithChevron(Control preview) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 2,
        VerticalAlignment = VerticalAlignment.Center,
        Children =
        {
            preview,
            new TextBlock
            {
                Text = "▾",
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90)),
                VerticalAlignment = VerticalAlignment.Center,
            },
        },
    };

    /// <summary>线形预览：指定宽度的短线。</summary>
    private static Control DashPreview(int index, double width)
    {
        var def = LineStyles.All[Math.Clamp(index, 0, LineStyles.All.Length - 1)];
        var line = new Avalonia.Controls.Shapes.Line
        {
            StartPoint = new Point(2, 7),
            EndPoint = new Point(width - 2, 7),
            Stroke = new SolidColorBrush(IconColor),
            StrokeThickness = 2,
        };
        if (def.Dashes != null)
        {
            line.StrokeDashArray = new AvaloniaList<double>(def.Dashes);
            line.StrokeLineCap = PenLineCap.Round; // 与实际渲染一致的圆头
        }
        return new Canvas { Width = width, Height = 14, Children = { line } };
    }

    /// <summary>端头样式预览：短线 + 对应端头（复用 LineElement 端头绘制，所见即所得）。</summary>
    private sealed class CapPreview : Control
    {
        public LineCapKind Kind { get; init; }
        public bool AtStart { get; init; }

        public CapPreview()
        {
            Width = 24;
            Height = 16;
        }

        public override void Render(DrawingContext context)
        {
            const double t = 3; // 稍粗的预览线，突出端头本身
            const double y = 8;
            var right = Bounds.Width - 2;
            var inset = LineElement.CapInset(Kind, LineElement.CapLength(t));
            var pen = new Pen(new SolidColorBrush(IconColor), t) { LineCap = PenLineCap.Round };
            if (AtStart)
            {
                context.DrawLine(pen, new Point(2 + inset, y), new Point(right, y));
                LineElement.DrawCap(context, new Point(2, y), new Vector(-1, 0), t, Kind, IconColor);
            }
            else
            {
                context.DrawLine(pen, new Point(2, y), new Point(right - inset, y));
                LineElement.DrawCap(context, new Point(right, y), new Vector(1, 0), t, Kind, IconColor);
            }
        }
    }

    /// <summary>当前线类工具序号（0=箭头 1=折线；选中元素时以元素为准）。</summary>
    private int LineToolIndex() => _model.Selected is LineElement line
        ? (line.IsArrowTool ? 0 : 1)
        : _state.Tool == EditorTool.Polyline
          || (_state.Tool != EditorTool.Arrow && _state.LastLineTool == EditorTool.Polyline)
            ? 1 : 0;

    /// <summary>当前区域模糊工具序号（0=像素化 1=模糊化；选中元素时以元素为准）。</summary>
    private int MosaicToolIndex() => _model.Selected is MosaicElement mosaic
        ? (mosaic.Style.Mode == MosaicMode.Blur ? 1 : 0)
        : _state.Tool == EditorTool.Blur
          || (_state.Tool != EditorTool.Pixelate && _state.LastMosaicTool == EditorTool.Blur)
            ? 1 : 0;

    /// <summary>PS 式工具组角标：图标右下角的小三角，标示该按钮是多工具组。</summary>
    private static Control WithCornerArrow(Control icon) => new Panel
    {
        Children =
        {
            icon,
            new Avalonia.Controls.Shapes.Polygon
            {
                Points = [new Point(6, 0), new Point(6, 6), new Point(0, 6)],
                Fill = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x6E)),
                Width = 6,
                Height = 6,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 1, 1),
            },
        },
    };

    /// <summary>
    /// 箭头/折线工具组图标：左上小箭头（蓝底 = 默认工具）+ 右下小折线，
    /// 中间一条斜线分割。
    /// </summary>
    private static Control LineGroupIcon()
    {
        var brush = new SolidColorBrush(IconColor);
        var canvas = new Canvas
        {
            Width = 22,
            Height = 22,
            Children =
            {
                // 默认工具（箭头）半区蓝底
                new Avalonia.Controls.Shapes.Polygon
                {
                    Points = [new Point(0, 0), new Point(19, 0), new Point(0, 19)],
                    Fill = new SolidColorBrush(ActiveBackground),
                },
                // 分割斜线
                new Avalonia.Controls.Shapes.Line
                {
                    StartPoint = new Point(1, 21),
                    EndPoint = new Point(21, 1),
                    Stroke = new SolidColorBrush(Color.FromRgb(0xAF, 0xAF, 0xAF)),
                    StrokeThickness = 1.2,
                },
                // 左上小箭头
                new Avalonia.Controls.Shapes.Line
                {
                    StartPoint = new Point(2.5, 9.5),
                    EndPoint = new Point(6, 6),
                    Stroke = brush,
                    StrokeThickness = 1.8,
                    StrokeLineCap = PenLineCap.Round,
                },
                new Avalonia.Controls.Shapes.Polygon
                {
                    Points = [new Point(8.6, 3.4), new Point(4.4, 4.9), new Point(7.1, 7.6)],
                    Fill = brush,
                },
                // 右下小折线
                new Avalonia.Controls.Shapes.Polyline
                {
                    Points =
                    [
                        new Point(11.5, 19.5), new Point(14.4, 14.8),
                        new Point(16.8, 17.2), new Point(20, 12.8),
                    ],
                    Stroke = brush,
                    StrokeThickness = 1.8,
                    StrokeLineCap = PenLineCap.Round,
                    StrokeJoin = PenLineJoin.Round,
                },
            },
        };
        return canvas;
    }

    /// <summary>像素化/模糊化工具组图标：左上小棋盘格（蓝底 = 默认）+ 右下模糊圆点 + 斜线分割。</summary>
    private static Control MosaicGroupIcon()
    {
        var canvas = new Canvas
        {
            Width = 22,
            Height = 22,
            Children =
            {
                new Avalonia.Controls.Shapes.Polygon
                {
                    Points = [new Point(0, 0), new Point(19, 0), new Point(0, 19)],
                    Fill = new SolidColorBrush(ActiveBackground),
                },
                new Avalonia.Controls.Shapes.Line
                {
                    StartPoint = new Point(1, 21),
                    EndPoint = new Point(21, 1),
                    Stroke = new SolidColorBrush(Color.FromRgb(0xAF, 0xAF, 0xAF)),
                    StrokeThickness = 1.2,
                },
            },
        };
        // 左上 3×3 迷你棋盘格
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 3; c++)
            {
                if ((r + c) % 2 != 0)
                    continue;
                var square = new Avalonia.Controls.Shapes.Rectangle
                {
                    Width = 2.6,
                    Height = 2.6,
                    Fill = new SolidColorBrush(Color.FromArgb(
                        (byte)((r * 3 + c) % 3 == 0 ? 0xFF : 0x80),
                        IconColor.R, IconColor.G, IconColor.B)),
                };
                Canvas.SetLeft(square, 2.5 + c * 2.6);
                Canvas.SetTop(square, 2.5 + r * 2.6);
                canvas.Children.Add(square);
            }
        }
        // 右下模糊圆点
        var blurDot = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(IconColor, 0),
                    new GradientStop(IconColor, 0.35),
                    new GradientStop(Color.FromArgb(0x00, IconColor.R, IconColor.G, IconColor.B), 1),
                },
            },
        };
        Canvas.SetLeft(blurDot, 12);
        Canvas.SetTop(blurDot, 12);
        canvas.Children.Add(blurDot);
        return canvas;
    }

    /// <summary>像素化图标：马赛克棋盘格。</summary>
    private static Control PixelateIcon(double box = 22)
    {
        var canvas = new Canvas { Width = box, Height = box };
        var cell = box / 5.5;
        var origin = (box - cell * 4) / 2;
        for (var r = 0; r < 4; r++)
        {
            for (var c = 0; c < 4; c++)
            {
                if ((r + c) % 2 != 0)
                    continue;
                var alpha = (r * 4 + c) % 3 == 0 ? (byte)0xFF : (byte)0x78;
                var square = new Avalonia.Controls.Shapes.Rectangle
                {
                    Width = cell,
                    Height = cell,
                    Fill = new SolidColorBrush(Color.FromArgb(
                        alpha, IconColor.R, IconColor.G, IconColor.B)),
                };
                Canvas.SetLeft(square, origin + c * cell);
                Canvas.SetTop(square, origin + r * cell);
                canvas.Children.Add(square);
            }
        }
        return canvas;
    }

    /// <summary>模糊化图标：中心实、边缘虚的径向渐变圆。</summary>
    private static Control BlurIcon(double box = 22)
    {
        var d = box * 0.72;
        var circle = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = d,
            Height = d,
            Fill = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(IconColor, 0),
                    new GradientStop(IconColor, 0.35),
                    new GradientStop(Color.FromArgb(0x00, IconColor.R, IconColor.G, IconColor.B), 1),
                },
            },
        };
        Canvas.SetLeft(circle, (box - d) / 2);
        Canvas.SetTop(circle, (box - d) / 2);
        return new Canvas { Width = box, Height = box, Children = { circle } };
    }

    private static Control WithLabel(Control control, string label) => new StackPanel
    {
        Spacing = 1,
        Children =
        {
            control,
            new TextBlock
            {
                Text = label,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)),
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        },
    };

    private static Button ToolButton(Control icon, string tip, Action onClick)
    {
        var button = new Button
        {
            Width = 35,
            Height = 35,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(5),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = icon,
        };
        ToolTip.SetTip(button, tip);
        AddStateBackground(button, ":pointerover", Color.FromRgb(0xEA, 0xEA, 0xEA));
        AddStateBackground(button, ":pressed", Color.FromRgb(0xD4, 0xD4, 0xD4));
        button.Click += (_, _) => onClick();
        return button;
    }

    private static void AddStateBackground(Button button, string pseudoClass, Color color)
    {
        var style = new Style(x => x.OfType<Button>().Class(pseudoClass)
            .Template().OfType<ContentPresenter>()
            .Name("PART_ContentPresenter"));
        style.Setters.Add(new Setter(ContentPresenter.BackgroundProperty, new SolidColorBrush(color)));
        button.Styles.Add(style);
    }

    private static Border Separator() => new()
    {
        Width = 1,
        Height = 22,
        Margin = new Thickness(3, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
    };

    // ---- 矢量图标（描边固定色，不随主题变化）----

    private static Control SelectIcon()
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M 7,3 L 7,17.6 L 10.6,14.4 L 13,19.4 L 15.2,18.3 L 12.8,13.4 L 17.6,13 Z"),
            Fill = new SolidColorBrush(IconColor),
        };
        return new Canvas { Width = 22, Height = 22, Children = { path } };
    }

    private static Control RectIcon(double box, double w, double h)
    {
        var rect = new Avalonia.Controls.Shapes.Rectangle
        {
            Width = w, Height = h, RadiusX = 2, RadiusY = 2,
            Stroke = new SolidColorBrush(IconColor), StrokeThickness = 2,
        };
        Canvas.SetLeft(rect, (box - w) / 2);
        Canvas.SetTop(rect, (box - h) / 2);
        return new Canvas { Width = box, Height = box, Children = { rect } };
    }

    private static Control GlyphIcon(string glyph) => new TextBlock
    {
        Text = glyph,
        FontSize = 17,
        Foreground = new SolidColorBrush(IconColor),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static Control CopyIcon()
    {
        var stroke = new SolidColorBrush(IconColor);
        var back = new Avalonia.Controls.Shapes.Rectangle
        {
            Width = 12, Height = 12, RadiusX = 2, RadiusY = 2,
            Stroke = stroke, StrokeThickness = 2,
        };
        Canvas.SetLeft(back, 3);
        Canvas.SetTop(back, 3);
        var front = new Avalonia.Controls.Shapes.Rectangle
        {
            Width = 12, Height = 12, RadiusX = 2, RadiusY = 2,
            Stroke = stroke, StrokeThickness = 2,
            Fill = Brushes.White,
        };
        Canvas.SetLeft(front, 7);
        Canvas.SetTop(front, 7);
        return new Canvas { Width = 22, Height = 22, Children = { back, front } };
    }

    /// <summary>粗细图标：三条由细到粗的横线。</summary>
    private static Control ThicknessIcon()
    {
        var canvas = new Canvas { Width = 18, Height = 16 };
        double[] widths = [1, 2.5, 4.5];
        double y = 2;
        foreach (var w in widths)
        {
            var line = new Avalonia.Controls.Shapes.Rectangle
            {
                Width = 16, Height = w, Fill = new SolidColorBrush(IconColor), RadiusX = w / 2, RadiusY = w / 2,
            };
            Canvas.SetLeft(line, 1);
            Canvas.SetTop(line, y);
            canvas.Children.Add(line);
            y += w + 3;
        }
        return canvas;
    }

    /// <summary>箭头图标：斜线 + 实心三角头。</summary>
    private static Control ArrowIcon()
    {
        var brush = new SolidColorBrush(IconColor);
        var line = new Avalonia.Controls.Shapes.Line
        {
            StartPoint = new Point(5, 17),
            EndPoint = new Point(13, 9),
            Stroke = brush,
            StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round,
        };
        var head = new Avalonia.Controls.Shapes.Polygon
        {
            Points = [new Point(17, 5), new Point(9.8, 7.6), new Point(14.4, 12.2)],
            Fill = brush,
        };
        return new Canvas { Width = 22, Height = 22, Children = { line, head } };
    }

    /// <summary>折线图标：之字线。</summary>
    private static Control PolylineIcon() => new Canvas
    {
        Width = 22,
        Height = 22,
        Children =
        {
            new Avalonia.Controls.Shapes.Polyline
            {
                Points = [new Point(3, 16), new Point(8.5, 7), new Point(13.5, 13), new Point(19, 5)],
                Stroke = new SolidColorBrush(IconColor),
                StrokeThickness = 2,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
            },
        },
    };

    /// <summary>端点粗细图标：一端粗一端细的楔形线。</summary>
    private static Control CapThicknessIcon(bool atStart)
    {
        var thick = atStart ? new Point(2, 8) : new Point(16, 8);
        var thin = atStart ? new Point(16, 8) : new Point(2, 8);
        var n = 3.0;
        var geo = new Avalonia.Controls.Shapes.Polygon
        {
            Points =
            [
                new Point(thick.X, thick.Y - n), new Point(thin.X, thin.Y - 0.6),
                new Point(thin.X, thin.Y + 0.6), new Point(thick.X, thick.Y + n),
            ],
            Fill = new SolidColorBrush(IconColor),
        };
        return new Canvas { Width = 18, Height = 16, Children = { geo } };
    }

    /// <summary>透明度图标：从实到透的渐变方块。</summary>
    private static Control OpacityIcon()
    {
        var rect = new Avalonia.Controls.Shapes.Rectangle
        {
            Width = 14, Height = 12, RadiusX = 2, RadiusY = 2,
            Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(IconColor, 0),
                    new GradientStop(Color.FromArgb(0x30, IconColor.R, IconColor.G, IconColor.B), 1),
                },
            },
        };
        Canvas.SetLeft(rect, 2);
        Canvas.SetTop(rect, 2);
        return new Canvas { Width = 18, Height = 16, Children = { rect } };
    }

    /// <summary>圆角图标：一段圆角轮廓。</summary>
    private static Control RadiusIcon() => new Canvas
    {
        Width = 18,
        Height = 16,
        Children =
        {
            new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M 2,15 L 2,8 Q 2,2 8,2 L 16,2"),
                Stroke = new SolidColorBrush(IconColor),
                StrokeThickness = 2,
            },
        },
    };
}

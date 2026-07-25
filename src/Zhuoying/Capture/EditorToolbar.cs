using System;
using System.Collections.Generic;
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

    public Border Root { get; }

    /// <summary>行显隐等导致尺寸变化（窗口据此重摆工具条位置）。</summary>
    public event Action? LayoutChanged;

    public EditorToolbar(EditorState state, Action copy, Action cancel)
    {
        _state = state;
        _model = state.Model;

        _selectButton = ToolButton(SelectIcon(), "选择 (V)", () => _state.Tool = EditorTool.Select);
        _shapeButton = ToolButton(RectIcon(22, 14, 11), "形状 (S)", () => _state.Tool = EditorTool.Shape);
        _lineToolButton = ToolButton(ArrowIcon(), "箭头/折线 (A / L)",
            () => _state.Tool = _state.LastLineTool);
        var lineToolHost = AttachLineToolPopup(_lineToolButton);
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
                _selectButton, _shapeButton, lineToolHost, Separator(),
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

        _propertyRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                _fillCheck, shapeDashHost, thicknessButton, radiusButton, rotationButton, opacityButton,
                Separator(), _swatchPanel,
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

        _linePropertyRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                lineDashHost, startCapHost, endCapHost,
                startThickButton, endThickButton, _splineCheck, lineOpacityButton,
                Separator(), _lineSwatchPanel,
            },
        };

        Root = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
            BoxShadow = BoxShadows.Parse("0 2 8 0 #33000000"),
            Cursor = new Cursor(StandardCursorType.Arrow),
            Child = new StackPanel
            {
                Spacing = 4,
                Children = { toolRow, _propertyRow, _linePropertyRow },
            },
        };

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
            _lineToolButton.Content = _state.LastLineTool == EditorTool.Polyline
                ? PolylineIcon() : ArrowIcon();
            _undoButton.IsEnabled = _model.CanUndo;
            _redoButton.IsEnabled = _model.CanRedo;

            var showShapeProps = _state.Tool == EditorTool.Shape || _model.Selected is ShapeElement;
            var showLineProps = _state.Tool is EditorTool.Arrow or EditorTool.Polyline
                                || _model.Selected is LineElement;
            // 弧线只对折线有意义（箭头固定两点，样条无效果）
            var showSpline = _state.Tool == EditorTool.Polyline
                             || (_model.Selected is LineElement { IsArrowTool: false });
            if (_propertyRow.IsVisible != showShapeProps
                || _linePropertyRow.IsVisible != showLineProps
                || _splineCheck.IsVisible != showSpline)
            {
                _propertyRow.IsVisible = showShapeProps;
                _linePropertyRow.IsVisible = showLineProps;
                _splineCheck.IsVisible = showSpline;
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

        void WatchClose() => DispatcherTimer.RunOnce(() =>
        {
            if (!popup.IsOpen)
                return;
            if (slider.IsDragging || button.IsPointerOver || content.IsPointerOver)
            {
                WatchClose();
                return;
            }
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

        void WatchClose() => DispatcherTimer.RunOnce(() =>
        {
            if (!popup.IsOpen)
                return;
            if (btn.IsPointerOver || content.IsPointerOver)
            {
                WatchClose();
                return;
            }
            popup.IsOpen = false;
        }, TimeSpan.FromMilliseconds(300));

        void Open()
        {
            if (popup.IsOpen)
                return;
            Rebuild();
            popup.IsOpen = true;
            WatchClose();
        }

        btn.PointerEntered += (_, _) => Open();
        btn.Click += (_, _) => Open();
        button = btn;
        return new Panel { Children = { btn, popup } };
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

    /// <summary>给箭头/折线主按钮挂子工具选择弹层（悬浮出现，选择即激活）。</summary>
    private Control AttachLineToolPopup(Button button)
    {
        // 悬浮弹层按钮不能挂 ToolTip（气泡使 IsPointerOver 失真，见 TROUBLESHOOTING §10 旁注）
        ToolTip.SetTip(button, null);

        var arrowChoice = ToolButton(ArrowIcon(), "", () => { });
        var polyChoice = ToolButton(PolylineIcon(), "", () => { });
        ToolTip.SetTip(arrowChoice, null);
        ToolTip.SetTip(polyChoice, null);

        var content = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4),
            BoxShadow = BoxShadows.Parse("0 2 8 0 #33000000"),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Children =
                {
                    WithLabel(arrowChoice, "箭头"),
                    WithLabel(polyChoice, "折线"),
                },
            },
        };
        var popup = new Popup
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 2,
            IsLightDismissEnabled = false,
            Child = content,
        };

        void Close() => popup.IsOpen = false;
        void WatchClose() => DispatcherTimer.RunOnce(() =>
        {
            if (!popup.IsOpen)
                return;
            if (button.IsPointerOver || content.IsPointerOver)
            {
                WatchClose();
                return;
            }
            Close();
        }, TimeSpan.FromMilliseconds(300));

        button.PointerEntered += (_, _) =>
        {
            if (popup.IsOpen)
                return;
            popup.IsOpen = true;
            WatchClose();
        };
        arrowChoice.Click += (_, _) => { _state.Tool = EditorTool.Arrow; Close(); };
        polyChoice.Click += (_, _) => { _state.Tool = EditorTool.Polyline; Close(); };

        return new Panel { Children = { button, popup } };
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

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
    private readonly Button _undoButton;
    private readonly Button _redoButton;
    private readonly StackPanel _propertyRow;
    private readonly CheckBox _fillCheck;
    private readonly ComboBox _lineCombo;
    private readonly TextBlock _thicknessText;
    private readonly TextBlock _radiusText;
    private readonly TextBlock _rotationText;
    private readonly TextBlock _opacityText;
    private readonly StackPanel _swatchPanel;

    public Border Root { get; }

    /// <summary>行显隐等导致尺寸变化（窗口据此重摆工具条位置）。</summary>
    public event Action? LayoutChanged;

    public EditorToolbar(EditorState state, Action copy, Action cancel)
    {
        _state = state;
        _model = state.Model;

        _selectButton = ToolButton(SelectIcon(), "选择 (V)", () => _state.Tool = EditorTool.Select);
        _shapeButton = ToolButton(RectIcon(22, 14, 11), "形状 (S)", () => _state.Tool = EditorTool.Shape);
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
                _selectButton, _shapeButton, Separator(),
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

        _lineCombo = new ComboBox
        {
            Width = 84,
            Height = 30,
            VerticalAlignment = VerticalAlignment.Center,
            ItemsSource = LineStyles.All,
            SelectedIndex = 0,
            ItemTemplate = new FuncDataTemplate<LineStyleDef>((def, _) =>
            {
                if (def == null)
                    return new Panel();
                var line = new Avalonia.Controls.Shapes.Line
                {
                    StartPoint = new Point(2, 7),
                    EndPoint = new Point(46, 7),
                    Stroke = new SolidColorBrush(IconColor),
                    StrokeThickness = 2,
                };
                if (def.Dashes != null)
                {
                    line.StrokeDashArray = new AvaloniaList<double>(def.Dashes);
                    line.StrokeLineCap = PenLineCap.Round; // 与实际渲染一致的圆头
                }
                return new Canvas { Width = 48, Height = 14, Children = { line } };
            }),
        };
        ToolTip.SetTip(_lineCombo, "线形");
        _lineCombo.SelectionChanged += (_, _) =>
        {
            if (_refreshing || _lineCombo.SelectedIndex < 0) return;
            var index = _lineCombo.SelectedIndex;
            _state.ModifyStyle(s => s with { LineStyleIndex = index });
        };

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
                _fillCheck, _lineCombo, thicknessButton, radiusButton, rotationButton, opacityButton,
                Separator(), _swatchPanel,
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
                Children = { toolRow, _propertyRow },
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
            _selectButton.Background = new SolidColorBrush(
                _state.Tool == EditorTool.Select ? ActiveBackground : Colors.Transparent);
            _shapeButton.Background = new SolidColorBrush(
                _state.Tool == EditorTool.Shape ? ActiveBackground : Colors.Transparent);
            _undoButton.IsEnabled = _model.CanUndo;
            _redoButton.IsEnabled = _model.CanRedo;

            var showProps = _state.Tool == EditorTool.Shape || _model.Selected != null;
            if (_propertyRow.IsVisible != showProps)
            {
                _propertyRow.IsVisible = showProps;
                LayoutChanged?.Invoke();
            }

            _fillCheck.IsChecked = style.Filled;
            _lineCombo.SelectedIndex = style.LineStyleIndex;
            _thicknessText.Text = ((int)style.Thickness).ToString();
            _radiusText.Text = ((int)style.CornerRadiusPercent).ToString();
            _rotationText.Text = $"{(int)style.RotationDeg}°";
            _opacityText.Text = ((int)style.Opacity).ToString();

            _swatchPanel.Children.Clear();
            foreach (var color in _state.PresetColors)
            {
                var c = color;
                var button = new Button
                {
                    Padding = new Thickness(0),
                    Background = Brushes.Transparent,
                    Content = RingedSwatch(c, ringed: c == style.Color, size: 16),
                };
                ToolTip.SetTip(button, $"#{c.R:X2}{c.G:X2}{c.B:X2}");
                button.Click += (_, _) => _state.ModifyStyle(s => s with { Color = c });
                _swatchPanel.Children.Add(button);
            }
        }
        finally
        {
            _refreshing = false;
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
        Func<double> get, Action<double> setLive)
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

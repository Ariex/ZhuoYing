using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Zhuoying.Annotations;

namespace Zhuoying.Capture;

/// <summary>
/// 文字元素的就地编辑：在窗口顶层画布上叠加一个与元素几何/样式一致的真实
/// TextBox（旋转态用 RenderTransform 保持旋转显示，IME/光标/选择原生支持）。
/// 编辑期间元素本体只画外框（TextElement.IsEditing）；编辑框带边框与占位符
/// 提供明确的编辑态反馈；工具栏样式修改实时同步到编辑框（所见即所得）。
/// 结束（点击框外 / Esc）时回写文本并入撤销栈；空文本自动删除元素。
/// </summary>
public sealed class TextEditController
{
    private static readonly IBrush EditBorderBrush = new SolidColorBrush(Color.FromArgb(0xAA, 0x2D, 0x8C, 0xF0));

    private readonly Canvas _host;
    private readonly AnnotationModel _model;
    private readonly EditorState _state;
    private readonly PixelPoint _origin;
    private readonly Func<double> _scaling;
    private readonly Action _onStyleChanged;

    private TextElement? _element;
    private TextBox? _box;
    private bool _isNew;
    private object? _before;

    public TextEditController(
        Canvas host, AnnotationModel model, EditorState state,
        PixelPoint origin, Func<double> scaling)
    {
        _host = host;
        _model = model;
        _state = state;
        _origin = origin;
        _scaling = scaling;
        // 编辑期间工具栏改样式（字号/颜色/加粗/对齐/内边距…）实时应用到编辑框
        _onStyleChanged = () =>
        {
            if (_element != null && _box != null)
                ApplyStyle(_box, _element.Style, _scaling());
        };
    }

    public bool IsActive => _element != null;

    public void Begin(TextElement element, bool isNew)
    {
        Commit();
        _element = element;
        _isNew = isNew;
        _before = element.CaptureState();
        element.IsEditing = true;
        _model.RaiseChanged();

        var s = _scaling();
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Text = element.Text,
            Watermark = "输入文字…",
            Background = Brushes.Transparent,
            // 编辑态明确可见：细蓝边框（聚焦色由 Fluent 主题接管）
            BorderThickness = new Thickness(1),
            BorderBrush = EditBorderBrush,
            CornerRadius = new CornerRadius(2),
            MinHeight = 0,
            MinWidth = 0,
            Width = Math.Max(10, element.Bounds.Width / s),
            Height = Math.Max(10, element.Bounds.Height / s),
        };
        ApplyStyle(box, element.Style, s);
        // 压掉 Fluent 主题的悬停/聚焦背景，保持透明文本区（保留边框反馈）
        SuppressChromeBackground(box);
        // 旋转态编辑：编辑框与元素同角度显示（TOOLS-SPEC §6）
        box.RenderTransformOrigin = RelativePoint.Center;
        if (element.Style.RotationDeg != 0)
            box.RenderTransform = new RotateTransform(element.Style.RotationDeg);
        // 实时把文本回写元素并触发重绘：编辑中元素层同步渲染描边（填充由编辑框呈现）
        box.TextChanged += (_, _) =>
        {
            if (_element is { } el)
            {
                el.Text = box.Text ?? "";
                _model.RaiseChanged();
            }
        };

        Canvas.SetLeft(box, (element.Bounds.X - _origin.X) / s);
        Canvas.SetTop(box, (element.Bounds.Y - _origin.Y) / s);
        _host.Children.Add(box);
        _box = box;
        _state.StyleChanged += _onStyleChanged;
        box.Focus();
        box.CaretIndex = box.Text?.Length ?? 0;
    }

    /// <summary>把元素样式应用/重应用到编辑框（Begin 与编辑中样式变化时调用）。</summary>
    private static void ApplyStyle(TextBox box, TextStyle style, double s)
    {
        box.FontFamily = new FontFamily(style.FontFamily);
        box.FontSize = Math.Max(1, style.FontSize / s);
        box.FontWeight = style.Bold ? FontWeight.Bold : FontWeight.Normal;
        box.FontStyle = style.Italic ? FontStyle.Italic : FontStyle.Normal;
        box.Foreground = new SolidColorBrush(style.Color);
        box.CaretBrush = new SolidColorBrush(style.Color);
        // 编辑框自身有 1 DIP 边框，内边距相应扣除，使编辑文字与元素层描边逐字对位
        box.Padding = new Thickness(Math.Max(0, style.Padding / s - 1));
        box.TextAlignment = style.HAlign switch
        {
            TextHAlign.Center => TextAlignment.Center,
            TextHAlign.Right => TextAlignment.Right,
            _ => TextAlignment.Left,
        };
        box.VerticalContentAlignment = style.VAlign switch
        {
            TextVAlign.Center => VerticalAlignment.Center,
            TextVAlign.Bottom => VerticalAlignment.Bottom,
            _ => VerticalAlignment.Top,
        };
    }

    /// <summary>结束编辑并提交（幂等）。</summary>
    public void Commit()
    {
        if (_element is not { } el || _box is not { } box)
            return;
        _state.StyleChanged -= _onStyleChanged;
        _element = null;
        el.Text = box.Text ?? "";
        el.IsEditing = false;
        _host.Children.Remove(box);
        _box = null;

        if (string.IsNullOrWhiteSpace(el.Text))
        {
            // 空文本：自动删除（TOOLS-SPEC §6）；新建的不留撤销痕迹
            var index = _model.Elements.IndexOf(el);
            _model.Elements.Remove(el);
            if (_model.Selected == el)
                _model.Selected = null;
            if (!_isNew)
                _model.Push(new RemoveElementCommand(_model, el, index));
        }
        else if (_isNew)
        {
            _model.Push(new AddElementCommand(_model, el));
        }
        else if (_before != null && !Equals(el.CaptureState(), _before))
        {
            _model.Push(new MutateElementCommand(el, _before, el.CaptureState()));
        }
        _before = null;
        _model.RaiseChanged();
    }

    private static void SuppressChromeBackground(TextBox box)
    {
        foreach (var pseudo in new[] { ":pointerover", ":focus" })
        {
            var style = new Style(x => x.OfType<TextBox>().Class(pseudo)
                .Template().OfType<Border>().Name("PART_BorderElement"));
            style.Setters.Add(new Setter(Border.BackgroundProperty, Brushes.Transparent));
            box.Styles.Add(style);
        }
    }
}

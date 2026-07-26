using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Zhuoying.Annotations;

namespace Zhuoying.Capture;

/// <summary>
/// 标注元素渲染层（仅渲染，无输入）。位于冻结帧之上、选区暗化遮罩之下——
/// 选区外的标注随底图一起被暗化（REQUIREMENTS：选区外注释半透明显示，输出裁掉）。
/// </summary>
public sealed class AnnotationLayer : Control
{
    private readonly AnnotationModel _model;
    private readonly PixelPoint _origin;

    public AnnotationLayer(AnnotationModel model, PixelPoint origin)
    {
        _model = model;
        _origin = origin;
        IsHitTestVisible = false;
        _model.Changed += InvalidateVisual;
    }

    private double Scaling => (VisualRoot as TopLevel)?.RenderScaling ?? 1.0;

    public override void Render(DrawingContext context)
    {
        var s = Scaling;
        Point ToLocal(Point p) => new((p.X - _origin.X) / s, (p.Y - _origin.Y) / s);
        // 橡皮只作用于画笔：渲染画笔元素时套"全区 − 橡皮带"反向裁剪
        var eraserClip = EraserElement.BuildClip(_model.Elements, ToLocal, s);
        foreach (var el in _model.Elements)
        {
            if (el is EraserElement)
                continue;
            if (el is PenElement && eraserClip != null)
            {
                using (context.PushGeometryClip(eraserClip))
                    el.Render(context, ToLocal, s);
            }
            else
            {
                el.Render(context, ToLocal, s);
            }
        }
    }
}

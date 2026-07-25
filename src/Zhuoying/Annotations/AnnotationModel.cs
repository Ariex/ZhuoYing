using System;
using System.Collections.Generic;
using Avalonia;

namespace Zhuoying.Annotations;

/// <summary>可撤销的编辑命令。构造时操作已生效（Push 不重放）。</summary>
public interface IEditCommand
{
    void Undo();
    void Redo();
}

/// <summary>
/// 标注模型：元素列表（按创建顺序叠放）+ 选中态 + 撤销/重做栈。
/// </summary>
public sealed class AnnotationModel
{
    private readonly Stack<IEditCommand> _undo = new();
    private readonly Stack<IEditCommand> _redo = new();
    private ShapeElement? _selected;

    public List<ShapeElement> Elements { get; } = [];

    public ShapeElement? Selected
    {
        get => _selected;
        set
        {
            if (_selected == value)
                return;
            _selected = value;
            Changed?.Invoke();
        }
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>元素/选中态/栈变化（用于重绘与工具栏使能刷新）。</summary>
    public event Action? Changed;

    public void RaiseChanged() => Changed?.Invoke();

    /// <summary>记录一个已生效的命令。</summary>
    public void Push(IEditCommand command)
    {
        _undo.Push(command);
        _redo.Clear();
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (_undo.Count == 0)
            return;
        var cmd = _undo.Pop();
        cmd.Undo();
        _redo.Push(cmd);
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (_redo.Count == 0)
            return;
        var cmd = _redo.Pop();
        cmd.Redo();
        _undo.Push(cmd);
        Changed?.Invoke();
    }

    /// <summary>命中最上层元素（后创建者优先）。</summary>
    public ShapeElement? HitTest(PixelPoint p, double slop)
    {
        for (var i = Elements.Count - 1; i >= 0; i--)
        {
            if (Elements[i].HitTest(p, slop))
                return Elements[i];
        }
        return null;
    }
}

/// <summary>创建元素（构造前元素已加入模型）。</summary>
public sealed class AddElementCommand(AnnotationModel model, ShapeElement element) : IEditCommand
{
    public void Undo()
    {
        model.Elements.Remove(element);
        if (model.Selected == element)
            model.Selected = null;
    }

    public void Redo() => model.Elements.Add(element);
}

/// <summary>删除元素（构造前元素已从模型移除）。</summary>
public sealed class RemoveElementCommand(AnnotationModel model, ShapeElement element, int index) : IEditCommand
{
    public void Undo() => model.Elements.Insert(Math.Min(index, model.Elements.Count), element);

    public void Redo()
    {
        model.Elements.Remove(element);
        if (model.Selected == element)
            model.Selected = null;
    }
}

/// <summary>几何/样式修改（移动、缩放、圆角、任何属性；构造前新值已生效）。</summary>
public sealed class MutateElementCommand(
    ShapeElement element,
    PixelRect oldBounds, ShapeStyle oldStyle,
    PixelRect newBounds, ShapeStyle newStyle) : IEditCommand
{
    public void Undo()
    {
        element.Bounds = oldBounds;
        element.Style = oldStyle;
    }

    public void Redo()
    {
        element.Bounds = newBounds;
        element.Style = newStyle;
    }
}

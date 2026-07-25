namespace Zhuoying.Annotations;

/// <summary>线形定义：虚线段长度以线宽为单位（Avalonia DashStyle 语义）。</summary>
public sealed record LineStyleDef(string Name, double[]? Dashes);

/// <summary>
/// 线形注册表。扩充线形只需在此表加一行（无需改 UI/渲染代码，
/// 下拉菜单与画笔按数据生成）。
/// 虚线段一律圆头（PixPin 风格）：圆头会让每段两端各外凸半个线宽，
/// 故点用 0.01（视觉为一个圆点）、间隔按视觉效果补偿。
/// </summary>
public static class LineStyles
{
    public static readonly LineStyleDef[] All =
    [
        new("实线", null),
        new("虚线", [3, 3]),
        new("点虚线", [0.01, 2.5]),
        new("短横一点", [3, 2.5, 0.01, 2.5]),
        new("短横两点", [3, 2.5, 0.01, 2.5, 0.01, 2.5]),
    ];
}

using System;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Media;

namespace Zhuoying.Annotations;

/// <summary>标号类型（每种类型维护独立的递增序列）。</summary>
public enum NumberKind
{
    Arabic,      // 1 2 3
    UpperAlpha,  // A B C（Z 后 AA AB…）
    LowerAlpha,  // a b c
    LowerRoman,  // i ii iii
    UpperRoman,  // I II III
    Chinese,     // 一 二 三
}

/// <summary>
/// 编号样式。Diameter 为物理像素；实心圆的数字颜色按圆色灰度自动取黑/白，
/// 空心圆的圆环与数字同用所选颜色。
/// </summary>
public sealed record NumberStyle
{
    public Color Color { get; init; } = Color.FromRgb(0xFF, 0x3B, 0x30);
    public bool Hollow { get; init; }
    public NumberKind Kind { get; init; } = NumberKind.Arabic;
    public double Diameter { get; init; } = 48;
}

/// <summary>
/// 编号元素：固定大小的圆形徽章 + 自动递增标号。编号增大时文字变多，
/// 徽章大小不变、内部文字自动缩小适配。不支持缩放/旋转手柄。
/// </summary>
public sealed class NumberElement : AnnotationElement
{
    /// <summary>圆心（虚拟屏幕物理像素）。</summary>
    public PixelPoint Center { get; set; }

    /// <summary>编号值（≥1）。修改互不影响其他编号，允许重复。</summary>
    public int Value { get; set; } = 1;

    public NumberStyle Style { get; set; } = new();

    public override object CaptureState() => (Center, Value, Style);

    public override void RestoreState(object state)
    {
        var (center, value, style) = ((PixelPoint, int, NumberStyle))state;
        Center = center;
        Value = value;
        Style = style;
    }

    public override void Render(DrawingContext context, Func<Point, Point> toLocal, double scale)
    {
        var c = toLocal(new Point(Center.X, Center.Y));
        var d = Style.Diameter / scale;
        if (d < 2)
            return;
        var r = d / 2;
        var brush = new SolidColorBrush(Style.Color);
        var ring = RingThickness(d);
        Color textColor;
        if (Style.Hollow)
        {
            // 圆环外沿与命中半径一致；文字在环内，互不重叠
            context.DrawEllipse(null, new Pen(brush, ring), c, r - ring / 2, r - ring / 2);
            textColor = Style.Color;
        }
        else
        {
            context.DrawEllipse(brush, null, c, r, r);
            textColor = IsLight(Style.Color) ? Colors.Black : Colors.White;
        }

        var label = Format(Style.Kind, Value);
        var inner = d - (Style.Hollow ? ring * 2 : 0);
        var formatted = BuildLabel(label, d * 0.52, textColor);
        // 徽章大小固定，文字超宽/超高时按比例缩小字号（FormattedText 尺寸与字号线性）
        var k = Math.Min(1.0, Math.Min(
            inner * 0.80 / Math.Max(1e-3, formatted.Width),
            inner * 0.66 / Math.Max(1e-3, formatted.Height)));
        if (k < 1)
            formatted = BuildLabel(label, Math.Max(4, d * 0.52 * k), textColor);
        context.DrawText(formatted, new Point(c.X - formatted.Width / 2, c.Y - formatted.Height / 2));
    }

    private static FormattedText BuildLabel(string text, double fontSize, Color color) => new(
        text,
        CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyle.Normal, FontWeight.Bold),
        fontSize,
        new SolidColorBrush(color));

    /// <summary>空心圆环粗细（随直径等比，有下限保证小徽章可见）。</summary>
    public static double RingThickness(double diameter) => Math.Max(1.5, diameter * 0.09);

    /// <summary>圆颜色是否偏亮（决定实心圆上的数字用黑还是白）。</summary>
    public static bool IsLight(Color c) =>
        (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255 >= 0.6;

    public override bool HitTest(PixelPoint p, double slop)
    {
        double dx = p.X - Center.X, dy = p.Y - Center.Y;
        var r = Style.Diameter / 2 + slop;
        return dx * dx + dy * dy <= r * r;
    }

    // ---- 标号格式化 ----

    public static string Format(NumberKind kind, int value)
    {
        if (value < 1)
            value = 1;
        return kind switch
        {
            NumberKind.UpperAlpha => Alpha(value, 'A'),
            NumberKind.LowerAlpha => Alpha(value, 'a'),
            NumberKind.LowerRoman => Roman(value).ToLowerInvariant(),
            NumberKind.UpperRoman => Roman(value),
            NumberKind.Chinese => Chinese(value),
            _ => value.ToString(CultureInfo.InvariantCulture),
        };
    }

    /// <summary>双射 26 进制（Excel 列名式）：1→A … 26→Z，27→AA。</summary>
    private static string Alpha(int value, char baseChar)
    {
        var sb = new StringBuilder();
        while (value > 0)
        {
            value--;
            sb.Insert(0, (char)(baseChar + value % 26));
            value /= 26;
        }
        return sb.ToString();
    }

    private static readonly (int Value, string Symbol)[] RomanMap =
    [
        (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"),
        (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
    ];

    private static string Roman(int value)
    {
        if (value > 3999) // 罗马数字无标准大数表示，超界退回阿拉伯数字
            return value.ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        foreach (var (v, symbol) in RomanMap)
        {
            while (value >= v)
            {
                sb.Append(symbol);
                value -= v;
            }
        }
        return sb.ToString();
    }

    private static readonly string[] ChineseDigits =
        ["零", "一", "二", "三", "四", "五", "六", "七", "八", "九"];

    private static readonly string[] ChineseUnits = ["", "十", "百", "千"];

    /// <summary>汉字数字（标准读法：10→十、21→二十一、101→一百零一），万以上退回阿拉伯数字。</summary>
    private static string Chinese(int value)
    {
        if (value >= 10000)
            return value.ToString(CultureInfo.InvariantCulture);
        if (value < 10)
            return ChineseDigits[value];
        var sb = new StringBuilder();
        var started = false;
        var zeroPending = false;
        for (var unit = 3; unit >= 0; unit--)
        {
            var digit = value / (int)Math.Pow(10, unit) % 10;
            if (digit == 0)
            {
                zeroPending |= started;
                continue;
            }
            if (zeroPending)
            {
                sb.Append(ChineseDigits[0]);
                zeroPending = false;
            }
            // 10–19 省略前导"一"（十一而非一十一）
            if (!(digit == 1 && unit == 1 && !started))
                sb.Append(ChineseDigits[digit]);
            sb.Append(ChineseUnits[unit]);
            started = true;
        }
        return sb.ToString();
    }
}

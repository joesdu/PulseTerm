using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace VelaShell.Common.Converters;

/// <summary>
/// 比例(0-1 的 double)→ 星号列宽。资源监视窗口的"内存组合"条要按已用/缓存/空闲三段
/// 的实际比例分配宽度,Grid 的 ColumnDefinition.Width 只认 GridLength,靠它换算。
/// 比例为 0 时给一个极小值,避免该段完全塌陷后相邻圆角挤在一起。
/// </summary>
public sealed class StarLengthConverter : IValueConverter
{
    /// <summary>可在 XAML 中直接引用的共享单例。</summary>
    public static readonly StarLengthConverter Instance = new();

    /// <summary>把 0-1 的比例转成 <see cref="GridLength" /> 星号宽度。</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double ratio = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            _ => 0
        };
        return new GridLength(Math.Max(0.0001, ratio), GridUnitType.Star);
    }

    /// <summary>不支持反向转换,调用即抛出 <see cref="NotSupportedException" />。</summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

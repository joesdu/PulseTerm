using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

// ReSharper disable ReturnTypeCanBeNotNullable

namespace VelaShell.Converters;

/// <summary>把 "#RRGGBB" 字符串转换为画刷(设置外观页色板/ANSI 调色板)。</summary>
public sealed class StringToBrushConverter : IValueConverter
{
    /// <summary>可在 XAML 中直接引用的共享单例。</summary>
    public static readonly StringToBrushConverter Instance = new();

    /// <summary>将 "#RRGGBB" 十六进制颜色字符串转换为画刷,解析失败时返回透明画刷。</summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && Color.TryParse(hex, out Color color))
        {
            return new SolidColorBrush(color);
        }
        return Brushes.Transparent;
    }

    /// <summary>不支持反向转换,调用即抛出 <see cref="NotSupportedException" />。</summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

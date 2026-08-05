using System.Globalization;
using Avalonia.Data.Converters;

namespace VelaShell.Converters;

/// <summary>字符串非空(非 null 且去空白后有内容)→ true;用于按“是否已填值”控制显隐。</summary>
public sealed class StringNotEmptyConverter : IValueConverter
{
    /// <summary>可在 XAML 中直接引用的共享单例。</summary>
    public static readonly StringNotEmptyConverter Instance = new();

    /// <summary>非空白字符串返回 true,否则返回 false。</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && !string.IsNullOrWhiteSpace(s);

    /// <summary>不支持反向转换,调用即抛出 <see cref="NotSupportedException" />。</summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

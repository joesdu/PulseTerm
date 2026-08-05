using System.Globalization;
using Avalonia.Data.Converters;

namespace VelaShell.Converters;

/// <summary>当绑定的 int 等于转换器参数时为 true(用于驱动设置页的可见性)。</summary>
public sealed class IntEqualsConverter : IValueConverter
{
    /// <summary>供在 XAML 中直接使用的共享单例。</summary>
    public static readonly IntEqualsConverter Instance = new();

    /// <summary>当绑定的 int 等于从字符串参数解析出的 int 时返回 true。</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is int i && parameter is string s && int.TryParse(s, out int p) && i == p;

    /// <summary>反向转换不受支持,始终抛出 <see cref="NotSupportedException" />。</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

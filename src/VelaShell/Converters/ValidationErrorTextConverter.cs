using System.Collections;
using System.Globalization;
using Avalonia.Data.Converters;

namespace VelaShell.Converters;

/// <summary>
/// 一组校验错误 → 一段可直接显示的文字(多条按行拼接)。
/// 校验提示的呈现是个只有图标的小三角,文案挂在它的悬停提示上;
/// 悬停提示要的是一个字符串,而 <c>DataValidationErrors.Errors</c> 给的是一串对象。
/// </summary>
public sealed class ValidationErrorTextConverter : IValueConverter
{
    /// <summary>可在 XAML 中直接引用的共享单例。</summary>
    public static readonly ValidationErrorTextConverter Instance = new();

    /// <summary>把错误集合拼成多行文本;空集合返回 null,悬停提示自然不出现。</summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IEnumerable errors || value is string)
        {
            return value?.ToString();
        }

        string text = string.Join(Environment.NewLine,
                                  errors.Cast<object?>()
                                        .Select(error => error?.ToString())
                                        .Where(line => !string.IsNullOrWhiteSpace(line)));
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>不支持反向转换,调用即抛出 <see cref="NotSupportedException" />。</summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

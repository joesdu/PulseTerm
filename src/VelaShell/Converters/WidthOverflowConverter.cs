using System.Globalization;
using Avalonia.Data.Converters;

namespace VelaShell.Converters;

/// <summary>
/// 标签条溢出判定(设计 nunbT 的 Tab Overflow Controls 仅在放不下时出现):
/// 输入 [Extent.Width, Viewport.Width],内容比视口宽(留 0.5px 容差)时为 true。
/// </summary>
public sealed class WidthOverflowConverter : IMultiValueConverter
{
    /// <summary>可在 XAML 绑定中直接复用的共享转换器实例。</summary>
    public static readonly WidthOverflowConverter Instance = new();

    /// <summary>当内容宽度(Extent)超过视口宽度(Viewport,含 0.5px 容差)时返回 true。</summary>
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is [double extent, double viewport, ..])
        {
            return extent > viewport + 0.5;
        }
        return false;
    }
}

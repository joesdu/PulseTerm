using System.Globalization;
using Avalonia.Data.Converters;
using VelaShell.Core.Models;

namespace VelaShell.Converters;

/// <summary>
/// 把 <see cref="SessionStatus" /> 与 <c>ConverterParameter</c> 里写的状态名比一比,相等为 true。
/// </summary>
/// <remarks>
/// 用途是把状态点的着色从"转换器直接返回画刷"改成"状态 → 样式类 → <c>DynamicResource</c>"。
/// 前者的问题是转换器的结果不随主题切换重算:切主题后圆点会一直停在旧主题的颜色上,
/// 直到该会话下一次状态变化才刷新。样式选择器 + 动态资源天然跟随主题变体。
/// 会话树(<c>SessionTreeView.axaml</c>)本来就是这么写的,这里只是把停靠标签条也对齐过去。
/// </remarks>
public sealed class SessionStatusEqualsConverter : IValueConverter
{
    /// <summary>供在 XAML 中直接使用的共享单例。</summary>
    public static readonly SessionStatusEqualsConverter Instance = new();

    /// <summary>状态等于参数指定的名称时返回 true。</summary>
    /// <param name="value">绑定过来的 <see cref="SessionStatus" />。</param>
    /// <param name="targetType">目标类型(未使用)。</param>
    /// <param name="parameter">要比对的状态名,如 <c>Connected</c>。</param>
    /// <param name="culture">区域信息(未使用)。</param>
    /// <returns>相等为 true。</returns>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SessionStatus status
        && parameter is string name
        && Enum.TryParse(name, ignoreCase: true, out SessionStatus expected)
        && status == expected;

    /// <summary>反向转换不受支持,始终抛出 <see cref="NotSupportedException" />。</summary>
    /// <param name="value">未使用。</param>
    /// <param name="targetType">未使用。</param>
    /// <param name="parameter">未使用。</param>
    /// <param name="culture">未使用。</param>
    /// <returns>不返回。</returns>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

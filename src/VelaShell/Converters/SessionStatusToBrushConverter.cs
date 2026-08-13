using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using VelaShell.Core.Models;

namespace VelaShell.Converters;

/// <summary>
/// 把 <see cref="SessionStatus" /> 映射到其状态点画刷。设计将其定义为
/// 主题恒定颜色(connected/connecting/disconnected 在暗色与亮色下均相同),
/// 因此此处使用固定画刷是正确的。
/// </summary>
public sealed class SessionStatusToBrushConverter : IValueConverter
{
    /// <summary>供在 XAML 中直接使用的共享单例。</summary>
    public static readonly SessionStatusToBrushConverter Instance = new();

    // 不可变画刷:静态共享的 SolidColorBrush 是 AvaloniaObject,带线程亲和性,
    // 一旦被非 UI 线程先初始化,后续 UI 线程渲染就会抛 VerifyAccess(见 ConnectionAccent 同款注释)。
    private static readonly IBrush Connected = new ImmutableSolidColorBrush(Color.Parse("#00D4AA"));
    private static readonly IBrush Connecting = new ImmutableSolidColorBrush(Color.Parse("#FDCB6E"));
    private static readonly IBrush Disconnected = new ImmutableSolidColorBrush(Color.Parse("#FF6B6B"));

    /// <summary>返回给定 <see cref="SessionStatus" /> 对应的状态点画刷,默认返回断开连接的颜色。</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            SessionStatus.Connected => Connected,
            SessionStatus.Connecting => Connecting,
            _ => Disconnected
        };

    /// <summary>反向转换不受支持,始终抛出 <see cref="NotSupportedException" />。</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

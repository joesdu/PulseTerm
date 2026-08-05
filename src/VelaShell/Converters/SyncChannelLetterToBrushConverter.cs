using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using VelaShell.Services;

namespace VelaShell.Converters;

/// <summary>
/// 把同步输入频道字母(A/B/C/D)映射为频道标识色画刷,与标签头、终端横条的
/// 频道色联动(会话树节点只持有字母字符串,颜色在视图层解析,避免表现层依赖 UI 类型)。
/// </summary>
public sealed class SyncChannelLetterToBrushConverter : IValueConverter
{
    /// <summary>供在 XAML 中直接使用的共享单例。</summary>
    public static readonly SyncChannelLetterToBrushConverter Instance = new();

    /// <summary>返回频道字母对应的标识色;未知字母/空串返回透明。</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            nameof(SyncInputChannel.A) => SyncInputChannels.BrushA,
            nameof(SyncInputChannel.B) => SyncInputChannels.BrushB,
            nameof(SyncInputChannel.C) => SyncInputChannels.BrushC,
            nameof(SyncInputChannel.D) => SyncInputChannels.BrushD,
            _ => Brushes.Transparent
        };

    /// <summary>反向转换不受支持,始终抛出 <see cref="NotSupportedException" />。</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

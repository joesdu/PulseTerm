using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using VelaShell.PluginSdk.Rpc;

namespace VelaShell.PluginHost;

/// <summary>
/// 把宿主下发的主题令牌快照注入本进程 Application 资源:插件的
/// <c>{DynamicResource VelaXxx}</c> 在隔离模式下与进程内一样生效,
/// 主题切换时宿主重发、DynamicResource 自动刷新。
/// </summary>
internal static class PluginHostThemeTokens
{
    /// <summary>应用一批令牌(封送到 UI 线程;坏值逐条跳过,不影响其余)。</summary>
    public static void Apply(ThemeTokensNotification notification) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (Application.Current is not { } app)
            {
                return;
            }
            foreach (ThemeTokenDto token in notification.Tokens)
            {
                try
                {
                    object? value = token.Kind switch
                    {
                        "brush" => new ImmutableSolidColorBrush(Color.Parse(token.Value)),
                        "color" => Color.Parse(token.Value),
                        "double" => double.Parse(token.Value, CultureInfo.InvariantCulture),
                        "font" => new FontFamily(token.Value),
                        _ => null
                    };
                    if (value is not null)
                    {
                        app.Resources[token.Key] = value;
                    }
                }
                catch (FormatException)
                {
                    // 单个坏令牌不拖累整批。
                }
            }
        });
}

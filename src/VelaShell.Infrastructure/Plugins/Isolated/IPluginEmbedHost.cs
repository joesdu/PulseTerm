using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.Ui;

namespace VelaShell.Infrastructure.Plugins.Isolated;

/// <summary>
/// 停靠嵌入的宿主侧 SPI(由 UI 层实现,仅 Windows):把隔离插件进程的无边框窗口
/// (HWND)收养进宿主停靠文档区。返回的面板句柄语义与进程内停靠面板一致
/// (用户关标签触发 <see cref="IPluginPanel.Closed" />,程序性关闭走
/// <see cref="IPluginPanel.CloseAsync" />)。
/// </summary>
public interface IPluginEmbedHost
{
    /// <summary>是否支持嵌入(平台 + 宿主状态);握手时向插件宣告。</summary>
    bool IsSupported { get; }

    /// <summary>把外来窗口收养为停靠文档;失败抛出(插件侧回退为独立窗口)。</summary>
    Task<IPluginPanel> EmbedAsync(string pluginId, IPluginLogger log, string title, nint hwnd,
        CancellationToken cancellationToken);
}

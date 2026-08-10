using VelaShell.PluginSdk.Sessions;

namespace VelaShell.PluginSdk.Events;

/// <summary>
/// 宿主事件订阅。事件在非 UI 线程触发,处理器必须快速返回且不得抛出
/// (异常由宿主捕获并记入插件日志);耗时工作请转投自己的后台任务。
/// 插件停用时全部订阅由宿主自动拆除。
/// </summary>
public interface IHostEvents
{
    /// <summary>一条会话连接成功。</summary>
    event Action<SessionInfo>? SessionConnected;

    /// <summary>一条会话断开(用户关闭或异常掉线)。</summary>
    event Action<SessionInfo>? SessionDisconnected;

    /// <summary>主题切换,参数为新主题名。</summary>
    event Action<string>? ThemeChanged;

    /// <summary>UI 语言切换,参数为新语言代码。</summary>
    event Action<string>? LocaleChanged;
}

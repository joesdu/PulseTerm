using VelaShell.Core.Localization;
using VelaShell.Core.Services;
using VelaShell.Core.Ssh;
using VelaShell.PluginSdk.Events;
using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.Sessions;

namespace VelaShell.Infrastructure.Plugins.Capabilities;

/// <summary>
/// 每插件一个的事件枢纽:订阅宿主事件源并转发给插件,逐处理器守卫异常
/// (单个插件的坏处理器只记日志,不影响宿主与其它订阅方)。
/// <see cref="Dispose" /> 时拆除对宿主源的订阅并清空插件处理器 ——
/// 这是插件 ALC 可回收的前提(悬挂的事件引用会钉住整个插件程序集)。
/// </summary>
internal sealed class PluginEventHub : IHostEvents, IDisposable
{
    private readonly IPluginLogger _log;
    private readonly ISshConnectionService? _connections;
    private readonly IThemeService? _theme;
    private readonly ILocalizationService? _localization;
    private readonly Action<Core.Models.SshSession> _onConnected;
    private readonly Action<Core.Models.SshSession> _onDisconnected;
    private readonly Action<string> _onThemeChanged;
    private readonly Action<string> _onLanguageChanged;
    private bool _disposed;

    public event Action<SessionInfo>? SessionConnected;
    public event Action<SessionInfo>? SessionDisconnected;
    public event Action<string>? ThemeChanged;
    public event Action<string>? LocaleChanged;

    public PluginEventHub(IPluginLogger log, ISshConnectionService? connections,
        IThemeService? theme, ILocalizationService? localization)
    {
        _log = log;
        _connections = connections;
        _theme = theme;
        _localization = localization;
        _onConnected = session => Forward(SessionConnected, SessionsCapability.Map(session));
        _onDisconnected = session => Forward(SessionDisconnected, SessionsCapability.Map(session));
        _onThemeChanged = themeName => Forward(ThemeChanged, themeName);
        _onLanguageChanged = language => Forward(LocaleChanged, language);
        if (_connections is not null)
        {
            _connections.SessionConnected += _onConnected;
            _connections.SessionDisconnected += _onDisconnected;
        }
        _theme?.ThemeChanged += _onThemeChanged;
        _localization?.LanguageChanged += _onLanguageChanged;
    }

    /// <summary>逐处理器转发,插件处理器抛出只记入该插件日志。</summary>
    private void Forward<T>(Action<T>? handlers, T payload)
    {
        if (handlers is null || _disposed)
        {
            return;
        }
        foreach (Action<T> handler in handlers.GetInvocationList().Cast<Action<T>>())
        {
            try
            {
                handler(payload);
            }
            catch (Exception ex)
            {
                _log.Error("Event handler threw.", ex);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_connections is not null)
        {
            _connections.SessionConnected -= _onConnected;
            _connections.SessionDisconnected -= _onDisconnected;
        }
        _theme?.ThemeChanged -= _onThemeChanged;
        _localization?.LanguageChanged -= _onLanguageChanged;
        SessionConnected = null;
        SessionDisconnected = null;
        ThemeChanged = null;
        LocaleChanged = null;
    }
}

using VelaShell.PluginSdk.Events;
using VelaShell.PluginSdk.Sessions;

namespace VelaShell.Plugin.Telnet.Tests;

/// <summary><see cref="IHostEvents" /> 的测试替身:用 Raise 方法从测试触发事件。</summary>
public sealed class TestHostEvents : IHostEvents
{
    /// <inheritdoc />
    public event Action<SessionInfo>? SessionConnected;

    /// <inheritdoc />
    public event Action<SessionInfo>? SessionDisconnected;

    /// <inheritdoc />
    public event Action<string>? ThemeChanged;

    /// <inheritdoc />
    public event Action<string>? LocaleChanged;

    /// <summary>触发会话连接事件。</summary>
    public void RaiseSessionConnected(SessionInfo session) => SessionConnected?.Invoke(session);

    /// <summary>触发会话断开事件。</summary>
    public void RaiseSessionDisconnected(SessionInfo session) => SessionDisconnected?.Invoke(session);

    /// <summary>触发主题切换事件。</summary>
    public void RaiseThemeChanged(string theme) => ThemeChanged?.Invoke(theme);

    /// <summary>触发语言切换事件。</summary>
    public void RaiseLocaleChanged(string locale) => LocaleChanged?.Invoke(locale);
}

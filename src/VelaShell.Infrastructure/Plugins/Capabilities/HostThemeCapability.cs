using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.Theming;

namespace VelaShell.Infrastructure.Plugins.Capabilities;

/// <summary>
/// 每插件一个的 <see cref="IHostThemeApi" />:身份与配色直接透传共享的
/// <see cref="HostThemeSource" />,变更事件逐处理器守卫异常(单个插件的坏处理器只记日志,
/// 不影响宿主与其它插件)。
/// <see cref="Dispose" /> 时拆掉对共享源的订阅并清空插件处理器 ——
/// 与 <see cref="PluginEventHub" /> 同理,悬挂的事件引用会把整个插件程序集钉在内存里,
/// 可收集 ALC 就回收不掉。
/// </summary>
internal sealed class HostThemeCapability : IHostThemeApi, IDisposable
{
    private readonly IPluginLogger _log;
    private readonly HostThemeSource _source;
    private readonly Action<HostThemeInfo> _onChanged;
    private bool _disposed;

    public HostThemeCapability(IPluginLogger log, HostThemeSource source)
    {
        _log = log;
        _source = source;
        _onChanged = info => Forward(Changed, info);
        _source.Changed += _onChanged;
    }

    /// <inheritdoc />
    public HostThemeInfo Current => _source.Current;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Colors => _source.Colors;

    /// <inheritdoc />
    public event Action<HostThemeInfo>? Changed;

    /// <inheritdoc />
    public string? GetColor(string token) =>
        token is not null && _source.Colors.TryGetValue(token, out string? value) ? value : null;

    /// <summary>逐处理器转发,插件处理器抛出只记入该插件日志。</summary>
    private void Forward(Action<HostThemeInfo>? handlers, HostThemeInfo payload)
    {
        if (handlers is null || _disposed)
        {
            return;
        }
        foreach (Action<HostThemeInfo> handler in handlers.GetInvocationList().Cast<Action<HostThemeInfo>>())
        {
            try
            {
                handler(payload);
            }
            catch (Exception ex)
            {
                _log.Error("Theme changed handler threw.", ex);
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
        _source.Changed -= _onChanged;
        Changed = null;
    }
}

/// <summary>
/// 没有主题服务的宿主(headless 测试)用的兜底:交出一套固定的默认主题与空配色,
/// 插件照常跑 —— 与其它能力"缺席即退化,不崩溃"的口径一致。
/// </summary>
internal sealed class StaticHostTheme : IHostThemeApi
{
    /// <inheritdoc />
    public HostThemeInfo Current { get; } = new(
        Core.Models.UiThemeCatalog.DefaultDark.Id,
        Core.Models.UiThemeCatalog.DefaultDark.Name,
        Core.Models.UiThemeCatalog.DefaultDark.IsDark,
        FollowsSystem: false,
        Core.Models.UiThemeCatalog.DefaultDark.Palette.Accent);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Colors { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <inheritdoc />
    public event Action<HostThemeInfo>? Changed
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public string? GetColor(string token) => null;
}

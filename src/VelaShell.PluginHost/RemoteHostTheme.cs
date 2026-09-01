using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.Rpc;
using VelaShell.PluginSdk.Theming;

namespace VelaShell.PluginHost;

/// <summary>
/// 隔离进程侧的 <see cref="IHostThemeApi" />:身份与配色都来自宿主的
/// <see cref="PluginRpc.ThemeTokens" /> 通知(握手应答里带初值)。
/// <para>
/// 令牌与身份走同一条通知,所以这里能保证一件事:<see cref="Changed" /> 触发时
/// <see cref="Current" /> 与 <see cref="Colors" /> 都已经是新的。拆成两条消息就做不到 ——
/// 插件会在“身份已换、颜色还旧”的中间态里被叫醒。
/// </para>
/// </summary>
internal sealed class RemoteHostTheme(IPluginLogger log, HostThemeInfo initial) : IHostThemeApi
{
    private volatile HostThemeInfo _current = initial;
    private volatile Dictionary<string, string> _colors = [with(StringComparer.Ordinal)];

    /// <inheritdoc />
    public HostThemeInfo Current => _current;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Colors => _colors;

    /// <inheritdoc />
    public event Action<HostThemeInfo>? Changed;

    /// <inheritdoc />
    public string? GetColor(string token) =>
        token is not null && _colors.TryGetValue(token, out string? value) ? value : null;

    /// <summary>
    /// 宿主下发了一份新的主题状态(已在线程池上)。
    /// 身份缺席(老宿主)时保留旧身份,只更新颜色。
    /// </summary>
    internal void OnThemeState(ThemeTokensNotification notification)
    {
        var colors = new Dictionary<string, string>(notification.Tokens.Length, StringComparer.Ordinal);
        foreach (ThemeTokenDto token in notification.Tokens)
        {
            if (token.Kind is "brush" or "color")
            {
                colors[token.Key] = token.Value;
            }
        }
        // 空表当"这次没采到颜色":留着上一份(偏一档)也好过让插件的取色全落到兜底灰上。
        if (colors.Count > 0)
        {
            _colors = colors;
        }
        if (notification.Theme is { } info)
        {
            _current = info;
        }
        Forward(_current);
    }

    /// <summary>逐处理器转发,插件处理器抛出只记入插件日志。</summary>
    private void Forward(HostThemeInfo payload)
    {
        if (Changed is not { } handlers)
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
                log.Error("Theme changed handler threw.", ex);
            }
        }
    }
}

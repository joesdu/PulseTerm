using VelaShell.Core.Models;
using VelaShell.Core.Services;
using VelaShell.PluginSdk.Rpc;
using VelaShell.PluginSdk.Theming;

namespace VelaShell.Infrastructure.Plugins.Capabilities;

/// <summary>
/// 宿主主题的**单一**采集点:订阅 <see cref="IThemeService.EffectiveThemeChanged" />,
/// 每次变化重算一次主题身份与整套 <c>Vela*</c> 颜色快照,再一次性广播给所有插件
/// (每插件一个 <see cref="HostThemeCapability" /> 挂在这上面)。
/// <para>
/// 之所以不让每个插件各自去采:采一次要跳到 UI 线程遍历整棵资源树,装了十个插件就是
/// 十次 —— 而它们要的是同一份东西。这里采一次,大家共用同一个不可变快照。
/// </para>
/// </summary>
internal sealed class HostThemeSource : IDisposable
{
    private readonly IThemeService? _theme;
    private readonly SystemDarkModeProbe? _systemPrefersDark;
    private readonly Func<Task<IReadOnlyList<ThemeTokenDto>>>? _tokens;
    private readonly Action _onEffectiveThemeChanged;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private bool _disposed;

    /// <summary>当前主题身份。构造后即有值(不必等第一次刷新)。</summary>
    public HostThemeInfo Current { get; private set; }

    /// <summary>当前颜色快照。整体替换,永不就地改写 —— 插件拿去的实例不会在手里变。</summary>
    public IReadOnlyDictionary<string, string> Colors { get; private set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// 生效配色已变、且 <see cref="Current" /> / <see cref="Colors" /> 都已更新。
    /// 在线程池线程上触发。
    /// </summary>
    public event Action<HostThemeInfo>? Changed;

    public HostThemeSource(
        IThemeService? theme,
        SystemDarkModeProbe? systemPrefersDark,
        Func<Task<IReadOnlyList<ThemeTokenDto>>>? tokens)
    {
        _theme = theme;
        _systemPrefersDark = systemPrefersDark;
        _tokens = tokens;
        Current = Resolve();
        _onEffectiveThemeChanged = () => _ = RefreshAsync();
        _theme?.EffectiveThemeChanged += _onEffectiveThemeChanged;
    }

    /// <summary>
    /// 重算身份与颜色,然后广播。**次序是契约的一部分**:插件在 <c>Changed</c> 里读
    /// <c>Colors</c>,先播后采就会读到上一套颜色。
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_disposed)
        {
            return;
        }
        // 一次只跑一趟:连着切三次主题不该并发采三份快照、再以不确定的次序互相覆盖。
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }
            HostThemeInfo info = Resolve();
            Colors = await CollectColorsAsync().ConfigureAwait(false);
            Current = info;
        }
        finally
        {
            _refreshGate.Release();
        }
        Changed?.Invoke(Current);
    }

    /// <summary>把主题服务的持久化值解析成插件看得懂的身份(“跟随系统”在这里落地)。</summary>
    private HostThemeInfo Resolve()
    {
        string? id = _theme?.CurrentTheme;
        bool followsSystem = UiThemeCatalog.Find(id) is null;
        // 只有"跟随系统"才需要问 UI 层此刻是明是暗;问不到(headless)时按暗色兜底,
        // 与 UiThemeCatalog.Resolve 对未知值的兜底一致。
        UiTheme resolved = UiThemeCatalog.Resolve(id, !followsSystem || (_systemPrefersDark?.Invoke() ?? true));
        // 强调色以用户覆盖优先 —— 插件问"当前强调色是什么",要的是屏幕上那个,
        // 不是主题出厂那个。
        string accent = _theme?.AccentColor ?? resolved.Palette.Accent;
        return new(resolved.Id, resolved.Name, resolved.IsDark, followsSystem, accent);
    }

    /// <summary>取一份颜色令牌快照(brush / color 两类;字号与字体不属于配色)。</summary>
    private async Task<IReadOnlyDictionary<string, string>> CollectColorsAsync()
    {
        if (_tokens is null)
        {
            return Colors;
        }
        try
        {
            IReadOnlyList<ThemeTokenDto> tokens = await _tokens().ConfigureAwait(false);
            var colors = new Dictionary<string, string>(tokens.Count, StringComparer.Ordinal);
            foreach (ThemeTokenDto token in tokens)
            {
                if (token.Kind is "brush" or "color")
                {
                    colors[token.Key] = token.Value;
                }
            }
            // 采空了当采集失败:宁可留着上一份(颜色偏一档),也不要交出一份空表
            // ——那会让插件的取色全落到兜底灰上。
            return colors.Count > 0 ? colors : Colors;
        }
        catch (Exception)
        {
            return Colors;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _theme?.EffectiveThemeChanged -= _onEffectiveThemeChanged;
        Changed = null;
        _refreshGate.Dispose();
    }
}

namespace VelaShell.Core.Services;

/// <summary>
/// 系统是否偏好暗色。由 UI 层实现(Avalonia 的 <c>ActualThemeVariant</c>)——
/// Core 与 Infrastructure 都不引用 Avalonia,而“跟随系统”落到哪一套主题只有它知道。
/// </summary>
public delegate bool SystemDarkModeProbe();

/// <summary>主题服务:管理当前主题(明/暗等)与强调色覆盖,并在变化时通知订阅方。</summary>
public interface IThemeService
{
    /// <summary>当前生效的主题名称。</summary>
    string CurrentTheme { get; }

    /// <summary>
    /// 用户自定义的强调色覆盖,为十六进制字符串(如 "#00D4AA");为 null 时使用主题的默认强调色。
    /// </summary>
    string? AccentColor { get; }

    /// <summary>切换到指定名称的主题;立即应用,无需重启。</summary>
    void SetTheme(string themeName);

    /// <summary>主题变更时触发,参数为新的主题名称。</summary>
    event Action<string>? ThemeChanged;

    /// <summary>设置(或在为 null/空时清除)强调色覆盖;实时生效,无需重启。</summary>
    void SetAccent(string? hexColor);

    /// <summary>强调色覆盖变更时触发;参数为十六进制颜色,或为 null 表示默认。</summary>
    event Action<string?>? AccentChanged;

    /// <summary>
    /// **生效配色**变化时触发 —— 换主题、“跟随系统”下系统明暗翻转、强调色覆盖变化,三者的并集。
    /// <para>
    /// 为什么不能用 <see cref="ThemeChanged" /> 代替:它只覆盖第一种,而且它的参数是主题 id,
    /// 订阅方多半会拿它去判断“变没变” —— 后两种情况下主题 id 根本没动,判断结果是“没变”,
    /// 而屏幕上整套颜色已经换了。要重取颜色的地方(插件令牌快照、一次性取色的缓存)认这个事件。
    /// </para>
    /// <para>触发时机在 <see cref="ThemeChanged" /> / <see cref="AccentChanged" /> **之后**:
    /// 宿主对那两个事件的处理器会把新令牌贴到应用资源上,先后颠倒的话订阅方取到的还是旧色。</para>
    /// </summary>
    event Action? EffectiveThemeChanged;

    /// <summary>
    /// “跟随系统”下系统明暗翻转时由宿主调用:主题 id 没变,但整套生效配色换了。
    /// 只触发 <see cref="EffectiveThemeChanged" />,不触发 <see cref="ThemeChanged" />
    /// (后者的语义是“用户换了主题”,这里没有)。
    /// </summary>
    void NotifySystemVariantChanged();
}

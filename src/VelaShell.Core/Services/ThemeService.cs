using VelaShell.Core.Models;

namespace VelaShell.Core.Services;

/// <summary>
/// <see cref="IThemeService"/> 的默认实现,跟踪当前活动主题(<see cref="UiThemeCatalog" />
/// 里的主题 Id,或 "system" = 跟随系统)与可选的强调色,任一方更新时抛出变更事件。
/// </summary>
public class ThemeService(string initialTheme = "dark", string? initialAccent = null) : IThemeService
{
    /// <summary>
    /// 当前活动的主题 Id("dark"、"light"、"tokyo-night"…,或 "system")。
    /// <para>
    /// "dark" / "light" 是 VelaDark / VelaLight 的 Id —— 历史值,老配置直接沿用,不做迁移。
    /// </para>
    /// </summary>
    public string CurrentTheme { get; private set; } =
        UiThemeCatalog.IsValidId(initialTheme) ? initialTheme.ToLowerInvariant() : "dark";

    /// <summary>
    /// 活动主题变更时触发,携带新的主题名称。
    /// </summary>
    public event Action<string>? ThemeChanged;

    /// <summary>
    /// 当前强调色,为规范化后的十六进制字符串;未设置时为 <c>null</c>。
    /// </summary>
    public string? AccentColor { get; private set; } = NormalizeHex(initialAccent);

    /// <summary>
    /// 强调色变更时触发,携带新的规范化十六进制值(或 <c>null</c>)。
    /// </summary>
    public event Action<string?>? AccentChanged;

    /// <inheritdoc />
    public event Action? EffectiveThemeChanged;

    /// <inheritdoc />
    public void NotifySystemVariantChanged() => EffectiveThemeChanged?.Invoke();

    /// <summary>
    /// 根据给定十六进制字符串设置强调色(经过校验与规范化),
    /// 仅当值确实变化时抛出 <see cref="AccentChanged"/>。
    /// </summary>
    public void SetAccent(string? hexColor)
    {
        string? normalized = NormalizeHex(hexColor);
        if (AccentColor == normalized)
        {
            return;
        }
        AccentColor = normalized;
        AccentChanged?.Invoke(AccentColor);
        // 次序要紧:AccentChanged 的处理器(App.ApplyAccent)刚把新强调色贴到应用资源上,
        // 这一句之后订阅方去取色才取得到新值。
        EffectiveThemeChanged?.Invoke();
    }

    /// <summary>
    /// 将活动主题切换到给定名称,变更时抛出 <see cref="ThemeChanged"/>。
    /// 对于无法识别的主题抛出 <see cref="ArgumentException"/>。
    /// </summary>
    public void SetTheme(string themeName)
    {
        string normalized = themeName.ToLowerInvariant();
        if (!UiThemeCatalog.IsValidId(normalized))
        {
            throw new ArgumentException(
                $@"Invalid theme: '{themeName}'. Valid themes: {string.Join(", ", UiThemeCatalog.SelectableIds)}.",
                nameof(themeName));
        }
        if (CurrentTheme == normalized)
        {
            return;
        }
        CurrentTheme = normalized;
        ThemeChanged?.Invoke(CurrentTheme);
        // 同 SetAccent:ThemeChanged 的处理器(App.ApplyThemeVariant)已把整套令牌换好,
        // 这一句之后订阅方取到的才是新主题的颜色。
        EffectiveThemeChanged?.Invoke();
    }

    /// <summary>
    /// 校验 #RGB / #RRGGBB / #RRGGBBAA 颜色,返回规范化后的值;为空时返回 null。
    /// 值格式非法时抛出。
    /// </summary>
    private static string? NormalizeHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }
        string value = hex.Trim();
        if (!value.StartsWith('#'))
        {
            value = "#" + value;
        }
        int digits = value.Length - 1;
        if (digits is 3 or 6 or 8 && value[1..].All(Uri.IsHexDigit))
        {
            return value.ToUpperInvariant();
        }
        throw new ArgumentException($@"Invalid accent color: '{hex}'. Expected #RGB, #RRGGBB, or #RRGGBBAA.", nameof(hex));
    }
}

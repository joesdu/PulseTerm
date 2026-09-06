namespace VelaShell.Core.Models;

/// <summary>
/// 把「全局终端设置」与「本条配置的覆盖项」合成一份生效值。
/// </summary>
/// <remarks>
/// <para>
/// 覆盖的规则只有一条:<see cref="TerminalOverrides" /> 里那一项不为空就用它,否则用全局。
/// 简单到几乎不值得单开一个类型 —— 但调用点有五六处(建标签、握手、重连、插件终端、
/// 设置热更新),各写一遍 <c>profile.Terminal?.X ?? settings.Y</c> 的结果必然是某一处漏了,
/// 表现为"覆盖在新建标签时生效、重连之后又变回全局",而那种 bug 没人会往这里想。
/// </para>
/// <para>
/// 一律接受 <c>profile</c> 为 null:本地终端、插件借用的终端视图都没有会话配置,
/// 那时整套覆盖自然不适用。
/// </para>
/// </remarks>
public static class SessionTerminalSettings
{
    /// <summary>生效的 <c>TERM</c> 名。</summary>
    /// <param name="profile">会话配置;null 表示没有配置(本地终端等)。</param>
    /// <param name="settings">全局设置。</param>
    /// <returns>会话覆盖值,或全局值。</returns>
    public static string TerminalType(SessionProfile? profile, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Pick(profile?.Terminal?.TerminalType, settings.TerminalType);
    }

    /// <summary>生效的输出解码编码名。</summary>
    /// <param name="profile">会话配置;null 表示没有配置。</param>
    /// <param name="settings">全局设置。</param>
    /// <returns>会话覆盖值,或全局值;两者都空时为 <c>UTF-8</c>。</returns>
    public static string Encoding(SessionProfile? profile, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string resolved = Pick(profile?.Terminal?.Encoding, settings.TerminalEncoding);
        // 全局那一项本身也可能是空(旧配置、手改坏的文件),兜到 UTF-8 而不是让编码解析抛。
        return string.IsNullOrWhiteSpace(resolved) ? "UTF-8" : resolved;
    }

    /// <summary>生效的保活心跳间隔(秒)。</summary>
    /// <param name="profile">会话配置;null 表示没有配置。</param>
    /// <param name="settings">全局设置。</param>
    /// <returns>会话覆盖值,或全局值。</returns>
    public static int KeepAliveSeconds(SessionProfile? profile, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return profile?.Terminal?.KeepAliveSeconds ?? settings.General.KeepAliveSeconds;
    }

    /// <summary>
    /// 生效的终端配色方案名;null = 不覆盖,沿用全局那套颜色。
    /// </summary>
    /// <remarks>
    /// 与其它几项不同,这一项<b>没有</b>「全局值」可回落:全局配色是一整组颜色字段,
    /// 不是一个方案名。所以这里返回 null 就是"别动,用全局那组颜色"。
    /// 认不出来的方案名同样当作没覆盖 —— 用新版选过某个方案再退回旧版就是这个情形,
    /// 那时应当照常显示,而不是空白一片。
    /// </remarks>
    /// <param name="profile">会话配置;null 表示没有配置。</param>
    /// <returns>内置方案,或 null。</returns>
    public static TerminalColorScheme? ColorScheme(SessionProfile? profile)
    {
        string? name = profile?.Terminal?.ColorScheme;
        return string.IsNullOrWhiteSpace(name)
            ? null
            : Array.Find(TerminalColorScheme.BuiltIn, scheme => scheme.Name == name);
    }

    /// <summary>
    /// 用户指定的标签强调色(<c>#RRGGBB</c>);null = 按配置 id 自动配色。
    /// </summary>
    /// <param name="profile">会话配置;null 表示没有配置。</param>
    /// <returns>十六进制色值,或 null。</returns>
    public static string? TabColor(SessionProfile? profile) =>
        string.IsNullOrWhiteSpace(profile?.Terminal?.TabColor) ? null : profile.Terminal.TabColor;

    /// <summary>登录后自动切换到的目录;null 或空 = 不切换。</summary>
    /// <param name="profile">会话配置;null 表示没有配置。</param>
    /// <returns>目录路径,或 null。</returns>
    public static string? StartupDirectory(SessionProfile? profile) =>
        string.IsNullOrWhiteSpace(profile?.Terminal?.StartupDirectory)
            ? null
            : profile.Terminal.StartupDirectory.Trim();

    private static string Pick(string? overrideValue, string globalValue) =>
        string.IsNullOrWhiteSpace(overrideValue) ? globalValue : overrideValue.Trim();
}

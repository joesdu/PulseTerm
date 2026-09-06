using Avalonia.Media;
using VelaShell.Core.Models;

namespace VelaShell.Services;

/// <summary>
/// 每个连接配置的稳定标识色:按 Profile.Id 哈希映射到固定色板,同一配置在任何会话、
/// 任何启动中始终得到同一颜色。用于标签页与 SFTP 面板的颜色联动,让"下方文件面板属于
/// 哪台服务器"可以靠余光辨认,防止多标签时误操作别的服务器。
/// </summary>
/// <remarks>
/// 色板取自当前主题的 <c>VelaAccentPalette0..7</c> 令牌(由种子色派生,见
/// <c>ThemeTokenApplier</c>)。原先这里写死八个 Dracula 色值,切到 Sakura /
/// GitHub Light 这类亮色主题后整条标签强调条都是外来色,而且绕过了
/// <c>UiThemeCatalogTests</c> 的对比度把关。
/// <para>
/// **同一会话在不同主题下会得到不同颜色** —— 这是期望行为:哈希决定的是"第几号色",
/// 具体是什么色由主题说了算。跨启动的稳定性(同一配置永远是同一号)不变。
/// </para>
/// </remarks>
public static class ConnectionAccent
{
    /// <summary>色板容量;与 <c>VelaAccentPalette0..7</c> 一一对应。</summary>
    private const int PaletteSize = 8;

    // 拿不到应用资源时(无头单元测试、设计器)的兜底色板,与 VelaTokens.axaml 的
    // 暗色默认值保持一致。
    private static readonly Color[] Fallback =
    [
        Color.Parse("#8BE9FD"), // cyan
        Color.Parse("#50FA7B"), // green
        Color.Parse("#FFB86C"), // orange
        Color.Parse("#FF79C6"), // pink
        Color.Parse("#BD93F9"), // purple
        Color.Parse("#F1FA8C"), // yellow
        Color.Parse("#FF5555"), // red
        Color.Parse("#6FA8FF")  // blue
    ];

    /// <summary>返回该配置的标识色画刷(FNV-1a 哈希 Guid 字节,跨启动稳定)。</summary>
    /// <param name="profileId">连接配置标识。</param>
    /// <returns>该配置对应的强调色画刷。</returns>
    public static IBrush BrushFor(Guid profileId) => BrushForIndex(IndexFor(profileId));

    /// <summary>
    /// 返回该配置的标识色画刷:用户在配置里指定了颜色就用它,否则按 id 自动配色。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 自动配色保证的是「同一批机器颜色各不相同」,而运维要的恰恰相反 ——
    /// 「所有生产机都是红的、所有测试机都是绿的」。两个目标没法用同一套规则同时满足,
    /// 所以显式指定优先,没指定才自动。
    /// </para>
    /// <para>
    /// 认不出的色值当作没指定:配置文件是可以手改的,一个写错的颜色不该让标签变成透明。
    /// </para>
    /// </remarks>
    /// <param name="profile">连接配置;null 时退回自动配色也无从谈起,返回 0 号色。</param>
    /// <returns>强调色画刷。</returns>
    public static IBrush BrushForProfile(SessionProfile? profile)
    {
        if (profile is null)
        {
            return BrushForIndex(0);
        }
        if (SessionTerminalSettings.TabColor(profile) is { } hex
            && Color.TryParse(hex, out Color chosen))
        {
            return new SolidColorBrush(chosen);
        }
        return BrushFor(profile.Id);
    }

    /// <summary>返回该配置在色板中的序号(0..7)。</summary>
    /// <param name="profileId">连接配置标识。</param>
    /// <returns>色板序号。</returns>
    public static int IndexFor(Guid profileId)
    {
        uint hash = 2166136261;
        foreach (byte b in profileId.ToByteArray())
        {
            hash = (hash ^ b) * 16777619;
        }
        return (int)(hash % PaletteSize);
    }

    /// <summary>按色板序号取画刷(序号越界时按模回绕)。</summary>
    /// <param name="index">色板序号。</param>
    /// <returns>对应的强调色画刷。</returns>
    public static IBrush BrushForIndex(int index)
    {
        int slot = ((index % PaletteSize) + PaletteSize) % PaletteSize;
        return ThemeBrushes.Resolve($"VelaAccentPalette{slot}", Fallback[slot]);
    }
}

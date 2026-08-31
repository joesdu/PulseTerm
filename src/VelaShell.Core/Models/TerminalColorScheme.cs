namespace VelaShell.Core.Models;

/// <summary>
/// 终端配色方案预设(§12.5):一键把整套颜色写入 <see cref="AppearanceOptions" />。
/// 应用后与出厂默认(Dracula 色值)不同的颜色会作为覆盖生效(见 TerminalAppearanceMapper 的
/// 稀疏覆盖机制);选择当前主题的默认方案即恢复出厂、终端重新跟随应用主题
/// (暗 = Dracula / 亮 = Solarized Light)。“(默认)”后缀由设置页按当前主题动态标注。
/// </summary>
/// <param name="Name">方案名称,显示于设置页(内置项如 "Dracula")。</param>
/// <param name="Foreground">终端前景(文本)色,十六进制。</param>
/// <param name="Background">终端背景色,十六进制。</param>
/// <param name="Cursor">光标颜色,十六进制。</param>
/// <param name="Selection">选区高亮色,十六进制。</param>
/// <param name="AnsiNormal">ANSI 常规 8 色(索引 0–7),十六进制。</param>
/// <param name="AnsiBright">ANSI 高亮 8 色(索引 8–15),十六进制。</param>
public sealed record TerminalColorScheme(
    string Name,
    string Foreground,
    string Background,
    string Cursor,
    string Selection,
    string[] AnsiNormal,
    string[] AnsiBright)
{
    /// <summary>
    /// 内置方案;首项 Dracula 色值等同出厂默认(不产生覆盖、跟随主题)。
    /// <para>
    /// 前九项与九套界面主题一一配对(见 <see cref="UiTheme.TerminalSchemeName" />):
    /// 每一项的背景色都等于对应主题的 <c>VelaBgTerminal</c> —— 终端画面和它周围的界面
    /// 是同一个平面,背景差一档就会在终端边缘看出一道拼缝。其余几项是无配对的经典方案,
    /// 供用户单独挑选。
    /// </para>
    /// </summary>
    public static readonly TerminalColorScheme[] BuiltIn =
    [
        new("Dracula",
            "#F8F8F2", "#282A36", "#F8F8F2", "#44475A",
            ["#21222C", "#FF5555", "#50FA7B", "#F1FA8C", "#BD93F9", "#FF79C6", "#8BE9FD", "#F8F8F2"],
            ["#6272A4", "#FF6E6E", "#69FF94", "#FFFFA5", "#D6ACFF", "#FF92DF", "#A4FFFF", "#FFFFFF"]),
        // Alucard = Dracula 官方亮色。配 VelaLight:此前亮色主题配的是 Solarized Light,
        // 终端底 #FDF6E3 与界面底 #FFFBEB 差一档,终端边缘一直挂着一道看得见的拼缝。
        new("Alucard",
            "#1F1F1F", "#FFFBEB", "#644AC9", "#CFCFDE",
            ["#1F1F1F", "#CB3A2A", "#14710A", "#846E15", "#644AC9", "#A3144D", "#036A96", "#CFCFDE"],
            ["#6C664B", "#E35A48", "#1E8E10", "#A38A1B", "#7A5CE0", "#C21A5E", "#0784B5", "#FFFBEB"]),
        new("Tokyo Night",
            "#C0CAF5", "#1A1B26", "#C0CAF5", "#33467C",
            ["#15161E", "#F7768E", "#9ECE6A", "#E0AF68", "#7AA2F7", "#BB9AF7", "#7DCFFF", "#A9B1D6"],
            ["#414868", "#F7768E", "#9ECE6A", "#E0AF68", "#7AA2F7", "#BB9AF7", "#7DCFFF", "#C0CAF5"]),
        new("Nord",
            "#D8DEE9", "#2E3440", "#D8DEE9", "#434C5E",
            ["#3B4252", "#BF616A", "#A3BE8C", "#EBCB8B", "#81A1C1", "#B48EAD", "#88C0D0", "#E5E9F0"],
            ["#4C566A", "#D4757F", "#B5CE9F", "#F0D8A4", "#95B4CE", "#C4A0BD", "#8FBCBB", "#ECEFF4"]),
        new("Everforest Dark",
            "#D3C6AA", "#2D353B", "#D3C6AA", "#475258",
            ["#343F44", "#E67E80", "#A7C080", "#DBBC7F", "#7FBBB3", "#D699B6", "#83C092", "#D3C6AA"],
            ["#859289", "#EC9A9C", "#B9CE97", "#E5CB95", "#96C8C1", "#E0AEC6", "#97CFA5", "#FFFFFF"]),
        // Obsidian:中性近黑底 + 高饱和 ANSI。底色不带色相,色块看上去比在任何
        // 有色底上都干净一档。
        new("Obsidian",
            "#E7E7EA", "#131316", "#22D3EE", "#2E2E38",
            ["#131316", "#F87171", "#4ADE80", "#FACC15", "#38BDF8", "#E879F9", "#22D3EE", "#D4D4D8"],
            ["#6A6A76", "#FCA5A5", "#86EFAC", "#FDE047", "#7DD3FC", "#F0ABFC", "#67E8F9", "#FAFAFA"]),
        new("Gruvbox Dark",
            "#EBDBB2", "#282828", "#EBDBB2", "#504945",
            ["#282828", "#CC241D", "#98971A", "#D79921", "#458588", "#B16286", "#689D6A", "#A89984"],
            ["#928374", "#FB4934", "#B8BB26", "#FABD2F", "#83A598", "#D3869B", "#8EC07C", "#EBDBB2"]),
        // Gruvbox Bright:同样的暖色底,但常规八色取 gruvbox 官方的 bright 一档 ——
        // 原版的 normal 红(#CC241D)压在 #282828 上只有 2.7:1,而报错信息是终端里最该一眼
        // 看见的东西,不能让它比正文还难读。要原汁原味的那套,上面的 "Gruvbox Dark" 还在。
        new("Gruvbox Bright",
            "#EBDBB2", "#282828", "#FE8019", "#504945",
            ["#32302F", "#FB4934", "#B8BB26", "#FABD2F", "#83A598", "#D3869B", "#8EC07C", "#D5C4A1"],
            ["#928374", "#FE8082", "#D3D75B", "#FFD866", "#A8C4BC", "#E2A9BE", "#A9D9AB", "#FBF1C7"]),
        // Rosé Pine Dawn:Rosé Pine Dawn 的色感,但补齐了原版没有的绿,并把几支色压深到
        // 白底上读得出的程度 —— 亮色方案照抄暗色方案的饱和度,结果就是一屏看不清的彩字。
        new("Rosé Pine Dawn",
            "#575279", "#FFFAF3", "#7C5F9F", "#EADDD3",
            ["#575279", "#A5445F", "#3E7A54", "#9A6A12", "#286983", "#7C5F9F", "#2E7A85", "#DFD8D0"],
            ["#797593", "#B85C74", "#4E8F64", "#B07415", "#34798F", "#8A6FAF", "#3D93A0", "#FFFAF3"]),
        // GitHub Light:纯白纸面上的中性 ANSI(GitHub Light 口径),白天强光下最好读。
        new("GitHub Light",
            "#1F2328", "#FFFFFF", "#0969DA", "#CFE4FB",
            ["#24292F", "#CF222E", "#1A7F37", "#9A6700", "#0969DA", "#8250DF", "#1B7C83", "#6E7781"],
            ["#57606A", "#A40E26", "#116329", "#7D4E00", "#0550AE", "#6639BA", "#136061", "#8C959F"]),
        new("Solarized Dark",
            "#839496", "#002B36", "#839496", "#073642",
            ["#073642", "#DC322F", "#859900", "#B58900", "#268BD2", "#D33682", "#2AA198", "#EEE8D5"],
            ["#586E75", "#CB4B16", "#859900", "#B58900", "#268BD2", "#6C71C4", "#93A1A1", "#FDF6E3"]),
        new("Solarized Light",
            "#657B83", "#FDF6E3", "#657B83", "#EEE8D5",
            ["#073642", "#DC322F", "#859900", "#B58900", "#268BD2", "#D33682", "#2AA198", "#EEE8D5"],
            ["#586E75", "#CB4B16", "#859900", "#B58900", "#268BD2", "#6C71C4", "#93A1A1", "#FDF6E3"]),
        new("One Dark",
            "#ABB2BF", "#282C34", "#ABB2BF", "#3E4451",
            ["#282C34", "#E06C75", "#98C379", "#E5C07B", "#61AFEF", "#C678DD", "#56B6C2", "#ABB2BF"],
            ["#5C6370", "#E06C75", "#98C379", "#E5C07B", "#61AFEF", "#C678DD", "#56B6C2", "#FFFFFF"]),
        new("Monokai",
            "#F8F8F2", "#272822", "#F8F8F2", "#49483E",
            ["#272822", "#F92672", "#A6E22E", "#F4BF75", "#66D9EF", "#AE81FF", "#A1EFE4", "#F8F8F2"],
            ["#75715E", "#F92672", "#A6E22E", "#F4BF75", "#66D9EF", "#AE81FF", "#A1EFE4", "#F9F8F5"])
    ];

    /// <summary>
    /// 终端配色当前是否处于「跟随主题」状态 —— 跟随即不产生任何颜色覆盖,
    /// 终端用当前界面主题配套的那套方案。
    /// <para>
    /// <see cref="AppearanceOptions.TerminalColorsFollowTheme" /> 为 null(老配置没有这一项)
    /// 时按老口径推断:颜色与出厂默认(<c>BuiltIn[0]</c> = Dracula)完全一致即视为跟随。
    /// 这正是老版本的判定方式,因此老配置的行为一字不差地保留下来。
    /// </para>
    /// </summary>
    public static bool FollowsTheme(AppearanceOptions appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return appearance.TerminalColorsFollowTheme ?? BuiltIn[0].Matches(appearance);
    }

    /// <summary>把本方案的整套颜色(前景/背景/光标/选区 + ANSI 16 色)写入给定外观选项。</summary>
    public void ApplyTo(AppearanceOptions appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        appearance.TerminalForeground = Foreground;
        appearance.TerminalBackground = Background;
        appearance.CursorColor = Cursor;
        appearance.SelectionColor = Selection;
        appearance.AnsiNormal = [.. AnsiNormal];
        appearance.AnsiBright = [.. AnsiBright];
    }

    /// <summary>
    /// 整套颜色(前景/背景/光标/选区 + ANSI 16 色)与给定外观完全一致才算匹配,
    /// 用于设置页打开时反向选中已保存的方案;用户改过任意单色即不匹配(显示“未选择”)。
    /// </summary>
    public bool Matches(AppearanceOptions appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return HexEquals(appearance.TerminalForeground, Foreground) && HexEquals(appearance.TerminalBackground, Background) && HexEquals(appearance.CursorColor, Cursor) && HexEquals(appearance.SelectionColor, Selection) && HexSequenceEquals(appearance.AnsiNormal, AnsiNormal) && HexSequenceEquals(appearance.AnsiBright, AnsiBright);
    }

    private static bool HexEquals(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool HexSequenceEquals(List<string>? a, string[] b)
    {
        if (a is null || a.Count != b.Length)
        {
            return false;
        }
        return !b.Where((t, i) => !HexEquals(a[i], t)).Any();
    }
}

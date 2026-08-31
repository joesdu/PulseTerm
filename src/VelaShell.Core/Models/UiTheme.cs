namespace VelaShell.Core.Models;

/// <summary>
/// 一套界面配色的**种子色**(24 个)。界面上真正用到的 <c>Vela*</c> 令牌有六十多个,
/// 但其中绝大多数是从这几个种子色派生出来的(同色不同透明度、同一语义色换个用途),
/// 派生规则见宿主侧的 <c>ThemeTokenApplier</c>。
/// <para>
/// 之所以不把六十多个令牌逐主题手写一遍:那样每加一个主题就要抄六十多个色值,
/// 抄错一个既不会编译失败也不会有人发现 —— 而"强调色的淡底"与"强调色"色相不一致
/// 这类错误,历史上已经真的发生过(见 <c>ThemeTokenContrastTests</c> 的通道顺序一条)。
/// 派生出来的令牌天然一致。
/// </para>
/// <para>色值一律 <c>#RRGGBB</c>;<see cref="BgActive" /> 允许 <c>#AARRGGBB</c>(亮色主题用强调色淡底)。</para>
/// </summary>
public sealed record UiThemePalette
{
    /// <summary>强调色:按钮、选中态、进度、链接。</summary>
    public required string Accent { get; init; }

    /// <summary>压在强调色实底上的文字/图标色(与 <see cref="Accent" /> 至少 4.5:1)。</summary>
    public required string AccentForeground { get; init; }

    /// <summary>窗口铬色(最外层:标题栏、标签栏底)。</summary>
    public required string BgPage { get; init; }

    /// <summary>侧边栏底。</summary>
    public required string BgSidebar { get; init; }

    /// <summary>终端平面底;同时是 SFTP 面板、停靠文档区与活动标签的底色。</summary>
    public required string BgTerminal { get; init; }

    /// <summary>浮层/对话框/菜单底;同时是内容区表头底色。</summary>
    public required string BgSurface { get; init; }

    /// <summary>选中行底色。亮色主题用强调色淡底(<c>#AARRGGBB</c>),暗色用中性提亮色。</summary>
    public required string BgActive { get; init; }

    /// <summary>悬停行底色。</summary>
    public required string BgHover { get; init; }

    /// <summary>输入框底色。</summary>
    public required string BgInput { get; init; }

    /// <summary>非活动标签底色。</summary>
    public required string BgTabInactive { get; init; }

    /// <summary>正文一档:标题与主要内容。</summary>
    public required string TextPrimary { get; init; }

    /// <summary>正文二档:次要说明。仍须满足 AA(4.5:1)。</summary>
    public required string TextSecondary { get; init; }

    /// <summary>装饰档:占位符、弱化标签。不承载正文。</summary>
    public required string TextTertiary { get; init; }

    /// <summary>最弱一档:禁用态、水印。</summary>
    public required string TextMuted { get; init; }

    /// <summary>分隔线/描边(弱)。</summary>
    public required string BorderPrimary { get; init; }

    /// <summary>分隔线/描边(强),兼作占用条轨道底。</summary>
    public required string BorderSecondary { get; init; }

    /// <summary>成功/已连接(绿)。</summary>
    public required string Success { get; init; }

    /// <summary>警告(暖色,通常偏橙);语义标记 <c>VelaWarning</c>。</summary>
    public required string Warning { get; init; }

    /// <summary>黄:连接中状态、占用条警戒段、终端黄。与 <see cref="Warning" /> 分开是因为
    /// 暗色主题里橙(警告)与黄(进行中)是两种语义,亮色主题下二者可以取同一个值。</summary>
    public required string Yellow { get; init; }

    /// <summary>错误/离线(红)。</summary>
    public required string Error { get; init; }

    /// <summary>信息(青/蓝):目录名、CPU 占用条、链路追踪线。</summary>
    public required string Info { get; init; }

    /// <summary>品红/粉:图表第六色与终端 magenta。</summary>
    public required string Magenta { get; init; }

    /// <summary>文件浏览器的文件夹图标色。</summary>
    public required string FolderIcon { get; init; }

    /// <summary>链路追踪地图的陆地填充。</summary>
    public required string TraceLand { get; init; }

    /// <summary>链路追踪地图的国境线(必须比陆地明显亮/暗一档,否则看不出分界)。</summary>
    public required string TraceBorder { get; init; }
}

/// <summary>
/// 一套完整的界面主题:配色种子 + 明暗基底 + 配套的终端配色方案。
/// </summary>
/// <param name="Id">持久化标识,写进 <c>AppSettings.Theme</c>。<c>dark</c> / <c>light</c>
/// 两个 Id 沿用历史值,老配置无需迁移。</param>
/// <param name="Name">显示名(品牌名,不本地化)。</param>
/// <param name="IsDark">基底明暗。决定 Avalonia 的 <c>ThemeVariant</c>,Fluent 控件与投影浓度跟着它走。</param>
/// <param name="TerminalSchemeName">配套终端配色方案名,须存在于 <see cref="TerminalColorScheme.BuiltIn" />;
/// 其背景色必须等于 <see cref="UiThemePalette.BgTerminal" /> —— 终端画面与界面是同一个平面,
/// 差一档就会看出一道拼缝。</param>
/// <param name="Palette">配色种子。</param>
public sealed record UiTheme(
    string Id,
    string Name,
    bool IsDark,
    string TerminalSchemeName,
    UiThemePalette Palette)
{
    /// <summary>配套的终端配色方案(按 <see cref="TerminalSchemeName" /> 从内置表取)。</summary>
    public TerminalColorScheme Terminal =>
        Array.Find(TerminalColorScheme.BuiltIn, scheme => scheme.Name == TerminalSchemeName)
        ?? TerminalColorScheme.BuiltIn[0];
}

/// <summary>
/// 内置界面主题目录。<c>AppSettings.Theme</c> 存的是这里的 <see cref="UiTheme.Id" />,
/// 外加一个不在目录里的特殊值 <see cref="SystemThemeId" />(跟随系统明暗)。
/// </summary>
public static class UiThemeCatalog
{
    /// <summary>“跟随系统”的伪主题 Id:按操作系统明暗落到 <see cref="DefaultDark" /> / <see cref="DefaultLight" />。</summary>
    public const string SystemThemeId = "system";

    /// <summary>全部内置主题,顺序即设置页下拉的顺序(暗色在前、亮色在后)。</summary>
    public static readonly UiTheme[] All =
    [
        // ——— 暗色 ———————————————————————————————————————————————————————
        new("dark", "VelaDark", true, "Dracula", new UiThemePalette
        {
            // Dracula 正典:出厂默认,色值与 1.x 起的历史版本逐一致。
            Accent = "#BD93F9", AccentForeground = "#0A0E14",
            BgPage = "#191A21", BgSidebar = "#252734", BgTerminal = "#282A36", BgSurface = "#343746",
            BgActive = "#44475A", BgHover = "#363948", BgInput = "#282A36", BgTabInactive = "#191A21",
            TextPrimary = "#F8F8F2", TextSecondary = "#B0B8D6", TextTertiary = "#6272A4", TextMuted = "#545B76",
            BorderPrimary = "#3B3E51", BorderSecondary = "#44475A",
            Success = "#50FA7B", Warning = "#FFB86C", Yellow = "#F1FA8C", Error = "#FF5555",
            Info = "#8BE9FD", Magenta = "#FF79C6", FolderIcon = "#FFB86C",
            TraceLand = "#333850", TraceBorder = "#5C6488",
        }),
        new("tokyo-night", "Tokyo Night", true, "Tokyo Night", new UiThemePalette
        {
            // Tokyo Night 血统:深蓝夜景,强调色是蓝而不是紫,长时间盯屏最不刺眼的一档。
            Accent = "#7AA2F7", AccentForeground = "#0B0E16",
            BgPage = "#15161E", BgSidebar = "#1A1B26", BgTerminal = "#1A1B26", BgSurface = "#24283B",
            BgActive = "#2C3A63", BgHover = "#2A2E45", BgInput = "#1F2335", BgTabInactive = "#15161E",
            TextPrimary = "#C0CAF5", TextSecondary = "#A9B1D6", TextTertiary = "#7A84B0", TextMuted = "#565F89",
            BorderPrimary = "#2A2F45", BorderSecondary = "#3B4261",
            Success = "#9ECE6A", Warning = "#FF9E64", Yellow = "#E0AF68", Error = "#F7768E",
            Info = "#7DCFFF", Magenta = "#BB9AF7", FolderIcon = "#FF9E64",
            TraceLand = "#2A2F4A", TraceBorder = "#4A5480",
        }),
        new("nord", "Nord", true, "Nord", new UiThemePalette
        {
            // Nord 血统:低饱和的极地蓝灰,冷静、对比温和。红色按 Nord 原色(#BF616A)压在
            // 面板底上只有 2.5:1,状态标签会读不出来,故提亮到 #D4757F。
            Accent = "#88C0D0", AccentForeground = "#1F242E",
            BgPage = "#242933", BgSidebar = "#2B313C", BgTerminal = "#2E3440", BgSurface = "#3B4252",
            BgActive = "#4C566A", BgHover = "#3E4658", BgInput = "#2E3440", BgTabInactive = "#242933",
            TextPrimary = "#ECEFF4", TextSecondary = "#D8DEE9", TextTertiary = "#A3ACBD", TextMuted = "#7B8494",
            BorderPrimary = "#434B5C", BorderSecondary = "#4C566A",
            Success = "#A3BE8C", Warning = "#D08770", Yellow = "#EBCB8B", Error = "#D4757F",
            Info = "#81A1C1", Magenta = "#B48EAD", FolderIcon = "#D08770",
            TraceLand = "#3B4252", TraceBorder = "#5A657B",
        }),
        new("everforest", "Everforest", true, "Everforest Dark", new UiThemePalette
        {
            // Everforest 血统:低对比的墨绿与暖灰,强调色取绿、成功色取青绿,
            // 两者分开才不会让"已连接"的圆点与强调色糊成一个颜色。
            Accent = "#A7C080", AccentForeground = "#1E2529",
            BgPage = "#232A2E", BgSidebar = "#2A3238", BgTerminal = "#2D353B", BgSurface = "#343F44",
            BgActive = "#414D52", BgHover = "#3A464B", BgInput = "#2D353B", BgTabInactive = "#232A2E",
            TextPrimary = "#D3C6AA", TextSecondary = "#B9C0AB", TextTertiary = "#9DA9A0", TextMuted = "#7A8478",
            BorderPrimary = "#3D484D", BorderSecondary = "#4F585E",
            Success = "#83C092", Warning = "#E69875", Yellow = "#DBBC7F", Error = "#E67E80",
            Info = "#7FBBB3", Magenta = "#D699B6", FolderIcon = "#E69875",
            TraceLand = "#3D484D", TraceBorder = "#62706F",
        }),
        new("obsidian", "Obsidian", true, "Obsidian", new UiThemePalette
        {
            // 中性近黑 + 高饱和青:OLED 屏省电、暗房里最不发光的一档,
            // 也是唯一一套底色不带色相的暗色主题(其余几套的底都偏蓝/绿/棕)。
            Accent = "#22D3EE", AccentForeground = "#071013",
            BgPage = "#0A0A0C", BgSidebar = "#111114", BgTerminal = "#131316", BgSurface = "#1B1B20",
            BgActive = "#2E2E38", BgHover = "#212128", BgInput = "#131316", BgTabInactive = "#0A0A0C",
            TextPrimary = "#E7E7EA", TextSecondary = "#C2C2CB", TextTertiary = "#8A8A96", TextMuted = "#6A6A76",
            BorderPrimary = "#26262E", BorderSecondary = "#34343F",
            Success = "#4ADE80", Warning = "#FB923C", Yellow = "#FACC15", Error = "#F87171",
            Info = "#38BDF8", Magenta = "#E879F9", FolderIcon = "#FB923C",
            TraceLand = "#1F1F26", TraceBorder = "#3C3C48",
        }),
        new("gruvbox", "Gruvbox", true, "Gruvbox Bright", new UiThemePalette
        {
            // Gruvbox 血统:暖棕底 + 琥珀强调,是唯一一套暖色暗色主题;
            // 夜间与暖色屏幕滤镜(Night Shift / 护眼模式)叠加时不会发脏。
            Accent = "#FE8019", AccentForeground = "#1D2021",
            BgPage = "#1D2021", BgSidebar = "#252424", BgTerminal = "#282828", BgSurface = "#32302F",
            BgActive = "#504945", BgHover = "#3A3735", BgInput = "#282828", BgTabInactive = "#1D2021",
            TextPrimary = "#EBDBB2", TextSecondary = "#D5C4A1", TextTertiary = "#A89984", TextMuted = "#7C6F64",
            BorderPrimary = "#3C3836", BorderSecondary = "#504945",
            Success = "#B8BB26", Warning = "#FE8019", Yellow = "#FABD2F", Error = "#FB4934",
            Info = "#83A598", Magenta = "#D3869B", FolderIcon = "#FABD2F",
            TraceLand = "#3C3836", TraceBorder = "#665C54",
        }),
        // ——— 亮色 ———————————————————————————————————————————————————————
        new("light", "VelaLight", false, "Alucard", new UiThemePalette
        {
            // Alucard(Dracula 官方亮色)正典:奶油底,与 VelaDark 同源,切明暗不换性格。
            Accent = "#644AC9", AccentForeground = "#FFFBEB",
            BgPage = "#F2EDDA", BgSidebar = "#F8F4E4", BgTerminal = "#FFFBEB", BgSurface = "#FFFBEB",
            BgActive = "#22644AC9", BgHover = "#EDE7D0", BgInput = "#F7F2DF", BgTabInactive = "#EBE5CC",
            TextPrimary = "#1F1F1F", TextSecondary = "#4A4636", TextTertiary = "#6C664B", TextMuted = "#9A9377",
            BorderPrimary = "#E3DCBF", BorderSecondary = "#D3CBA9",
            Success = "#14710A", Warning = "#846E15", Yellow = "#846E15", Error = "#CB3A2A",
            Info = "#036A96", Magenta = "#A3144D", FolderIcon = "#9E841A",
            TraceLand = "#EFE9D2", TraceBorder = "#BDB392",
        }),
        new("rose-pine-dawn", "Rosé Pine Dawn", false, "Rosé Pine Dawn", new UiThemePalette
        {
            // Rosé Pine Dawn 血统:玫瑰灰暖底、鸢尾紫强调。原版 iris(#907AA9)压白底只有
            // 3.7:1,按钮上的文字读不出来,故压深到 #7C5F9F;绿色是原版没有的,补一支
            // 与之同调的森林绿 —— 没有绿,ls 的目录/可执行位就分不开。
            Accent = "#7C5F9F", AccentForeground = "#FFFAF3",
            BgPage = "#F2E9E1", BgSidebar = "#FAF4ED", BgTerminal = "#FFFAF3", BgSurface = "#FFFAF3",
            BgActive = "#247C5F9F", BgHover = "#F2E9E1", BgInput = "#FDF8F1", BgTabInactive = "#E9DED5",
            TextPrimary = "#575279", TextSecondary = "#635D82", TextTertiary = "#797593", TextMuted = "#9893A5",
            BorderPrimary = "#E6DCD4", BorderSecondary = "#D6C9C0",
            Success = "#3E7A54", Warning = "#9A6A12", Yellow = "#9A6A12", Error = "#A5445F",
            Info = "#286983", Magenta = "#8A4E76", FolderIcon = "#B07415",
            TraceLand = "#F0E7DF", TraceBorder = "#C9BBB2",
        }),
        new("github-light", "GitHub Light", false, "GitHub Light", new UiThemePalette
        {
            // 中性冷白:纯白纸面 + 克制的蓝强调,是唯一一套底色不带暖调的亮色主题,
            // 白天强光下最好读,截图贴进文档里也不会跟文档底色打架。
            Accent = "#0969DA", AccentForeground = "#FFFFFF",
            BgPage = "#EEF1F4", BgSidebar = "#F6F8FA", BgTerminal = "#FFFFFF", BgSurface = "#FFFFFF",
            BgActive = "#1A0969DA", BgHover = "#EEF1F4", BgInput = "#FBFCFD", BgTabInactive = "#E6EAEF",
            TextPrimary = "#1F2328", TextSecondary = "#424A53", TextTertiary = "#656D76", TextMuted = "#8C959F",
            BorderPrimary = "#D8DEE4", BorderSecondary = "#C4CCD4",
            Success = "#1A7F37", Warning = "#9A6700", Yellow = "#9A6700", Error = "#CF222E",
            Info = "#1B7C83", Magenta = "#8250DF", FolderIcon = "#9A6700",
            TraceLand = "#EAEEF2", TraceBorder = "#C4CDD5",
        }),
    ];

    /// <summary>跟随系统时的暗色落点。</summary>
    public static UiTheme DefaultDark => Get("dark");

    /// <summary>跟随系统时的亮色落点。</summary>
    public static UiTheme DefaultLight => Get("light");

    /// <summary>设置页主题下拉的可选值:全部主题 Id + <see cref="SystemThemeId" />(置末)。</summary>
    public static string[] SelectableIds { get; } = [.. All.Select(theme => theme.Id), SystemThemeId];

    /// <summary><paramref name="id" /> 是否为可持久化的主题值(含“跟随系统”)。</summary>
    public static bool IsValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && SelectableIds.Contains(id.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>按 Id 取主题;不存在时返回 <see cref="DefaultDark" />。</summary>
    public static UiTheme Get(string? id) => Find(id) ?? All[0];

    /// <summary>按 Id 取主题;不存在(含“跟随系统”)时返回 null。</summary>
    public static UiTheme? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Array.Find(All, theme => string.Equals(theme.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 把持久化的主题值解析为一套实际生效的配色:“跟随系统”按
    /// <paramref name="systemPrefersDark" /> 落到默认暗/亮主题,未知值一律落到默认暗色。
    /// </summary>
    public static UiTheme Resolve(string? id, bool systemPrefersDark) =>
        Find(id) ?? (systemPrefersDark ? DefaultDark : DefaultLight);

    /// <summary>
    /// 主题值对外的明暗名("dark" / "light" / "system")。插件契约里的
    /// <c>IHostInfo.Theme</c> 只认这三个值,新增的具名主题不能直接漏给插件。
    /// </summary>
    public static string VariantName(string? id) =>
        Find(id) is { } theme ? (theme.IsDark ? "dark" : "light") : SystemThemeId;
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using VelaShell.Core.Models;

namespace VelaShell.Services;

/// <summary>
/// 把一套界面主题(<see cref="UiTheme" />)展开成全部 <c>Vela*</c> 令牌,写进应用级资源字典。
/// <para>
/// <b>为什么是运行时写资源、而不是每个主题一份 axaml</b>:令牌有六十多个,但真正需要
/// 逐主题决定的只有 <see cref="UiThemePalette" /> 里那二十几个种子色 —— 其余全是派生
/// (同色换透明度、同一语义色换用途)。每套主题手抄六十多行,抄错了既不会编译失败也没人
/// 看得出来;派生出来的令牌天然自洽,新增主题也只需要填种子色。
/// </para>
/// <para>
/// <b>怎么盖住 axaml 里的值</b>:Avalonia 的资源查找顺序是「字典自身的条目 → 它的
/// ThemeDictionaries → 合并字典」。本类把整套令牌换进 <c>Application.Resources.ThemeDictionaries</c>
/// 当前明暗那一格,于是盖得住 <c>VelaTokens.axaml</c> / <c>VelaShellTokens.axaml</c>(它们是合并字典),
/// 而应用级的自有条目仍高于它 —— 用户的强调色覆盖(#3)照旧生效。
/// axaml 里那两套仍然保留:它们是 VelaDark / VelaLight 的编译期缺省,
/// 设计器、headless 测试与本类跑起来之前的那一瞬间靠它们。
/// </para>
/// </summary>
internal static class ThemeTokenApplier
{
    /// <summary>本类会写入的全部令牌键(切主题时整套重写,不会留下上一套的残值)。</summary>
    internal static IReadOnlyList<string> TokenKeys { get; } = [.. BuildTokens(UiThemeCatalog.DefaultDark).Keys];

    /// <summary>
    /// 把主题的整套令牌贴到应用上:令牌先写进一个**游离**字典,再整格换进
    /// <c>Application.Resources.ThemeDictionaries</c> 对应明暗那一格。
    /// <para>
    /// <b>为什么不逐个写进 <c>Application.Resources</c></b>:资源字典每被写一次,就沿可视树发一遍
    /// 资源变更通知,树上每一处 <c>DynamicResource</c> 都要重新解析 —— 代价与写入次数成正比。
    /// 六十多个令牌逐个写下去,一次切主题实测 40 ms 上下(400 个绑定的合成树;真实窗口只多不少),
    /// 手上就是"切一下顿一下"。整格替换只发一次通知,同一棵树上实测 0.8 ms。
    /// </para>
    /// <para>
    /// 主题字典的查找优先级高于合并字典,因此这一格盖得住 <c>VelaTokens.axaml</c> /
    /// <c>VelaShellTokens.axaml</c> 里的同名条目;而应用级的**自有**条目仍高于它,
    /// 用户的强调色覆盖(<see cref="ResetAccent" /> 与 <c>App.ApplyAccent</c>)照旧生效。
    /// </para>
    /// </summary>
    public static void Apply(Application app, UiTheme theme)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(theme);
        var palette = new ResourceDictionary();
        Fill(palette, theme);
        app.Resources.ThemeDictionaries[theme.IsDark ? ThemeVariant.Dark : ThemeVariant.Light] = palette;
    }

    /// <summary>把主题的整套令牌写进给定字典(不负责挂到应用上,见 <see cref="Apply" />)。</summary>
    public static void Fill(IResourceDictionary resources, UiTheme theme)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(theme);
        foreach ((string key, Color color) in BuildTokens(theme))
        {
            resources[key] = new SolidColorBrush(color);
        }
        // 自绘窗体/浮层的投影:几何两套主题一致,只有不透明度分明暗 —— 同一个 50% 纯黑
        // 压在亮色卡片下面会糊成一块发脏的矩形(见 DarkTheme.axaml 的说明)。
        resources["VelaShadowWindow"] = BoxShadows.Parse(
            theme.IsDark
                ? "0 1 3 0 #40000000, 0 4 12 0 #66000000"
                : "0 1 3 0 #1A000000, 0 4 12 0 #33000000");
    }

    /// <summary>用户自定义强调色会遮蔽的三个令牌(见 <see cref="ResetAccent" />)。</summary>
    private static readonly string[] AccentKeys =
        ["VelaAccent", "VelaAccentDim", "VelaAccentForeground"];

    /// <summary>
    /// 把强调色三件套写回**当前主题自己**的值。用户清空自定义强调色时调用。
    /// <para>
    /// 不能改成从资源里把这几个键删掉:删掉会掉到 axaml 的编译期缺省(VelaDark / VelaLight
    /// 的紫),于是除这两套之外的每一套主题,强调色都会变成不属于它的那个紫。
    /// </para>
    /// </summary>
    public static void ResetAccent(IResourceDictionary resources, UiTheme theme)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(theme);
        Dictionary<string, Color> tokens = BuildTokens(theme);
        foreach (string key in AccentKeys)
        {
            resources[key] = new SolidColorBrush(tokens[key]);
        }
    }

    /// <summary>展开种子色为「令牌键 → 颜色」。派生规则集中在这一处。</summary>
    private static Dictionary<string, Color> BuildTokens(UiTheme theme)
    {
        UiThemePalette p = theme.Palette;
        bool dark = theme.IsDark;

        Color accent = Parse(p.Accent);
        Color info = Parse(p.Info);
        Color success = Parse(p.Success);
        Color warning = Parse(p.Warning);
        Color yellow = Parse(p.Yellow);
        Color error = Parse(p.Error);
        Color magenta = Parse(p.Magenta);
        Color textPrimary = Parse(p.TextPrimary);
        Color textTertiary = Parse(p.TextTertiary);
        Color bgPage = Parse(p.BgPage);
        Color bgTerminal = Parse(p.BgTerminal);
        Color accentForeground = Parse(p.AccentForeground);

        // 遮罩:模态浮层压住底下的界面。一律偏黑 —— 亮色主题上铺一层浅遮罩读不出"下面那层
        // 不可点了",而这正是遮罩唯一要传达的事。掺三成窗口底色是为了不让它在带色相的主题
        // (Sakura 的粉、Gruvbox 的棕)上显得是一块外来的死黑。
        Color scrim = Blend(bgPage, Colors.Black, 0.3);

        // 图表面积填充的不透明度:亮底上要更淡一档,否则几条曲线糊成一片。
        byte dim = dark ? (byte)0x38 : (byte)0x28;
        byte subtleDim = dark ? (byte)0x28 : (byte)0x20;

        return new Dictionary<string, Color>(StringComparer.Ordinal)
        {
            // ——— 强调色与文字(VelaTokens.axaml) ———
            ["VelaAccent"] = accent,
            ["VelaAccentDim"] = WithAlpha(accent, dark ? (byte)0x30 : (byte)0x1A),
            ["VelaAccentForeground"] = Parse(p.AccentForeground),
            ["VelaAccentText"] = accent,
            ["VelaTextPrimary"] = textPrimary,
            ["VelaTextSecondary"] = Parse(p.TextSecondary),
            ["VelaTextTertiary"] = textTertiary,
            ["VelaTextMuted"] = Parse(p.TextMuted),
            ["VelaBorderPrimary"] = Parse(p.BorderPrimary),
            ["VelaBorderSecondary"] = Parse(p.BorderSecondary),

            // ——— 状态与语义 ———
            ["VelaStatusConnected"] = success,
            ["VelaStatusConnecting"] = yellow,
            ["VelaStatusDisconnected"] = error,
            ["VelaWarning"] = warning,
            ["VelaError"] = error,
            ["VelaInfo"] = info,

            // 压在实心语义色上的文字/图标(危险按钮的字、状态徽标的字)。
            // 不能一律用白:暗色主题的语义色本身就是**亮**色(VelaDark 的红 #FF5555、
            // Obsidian 的 #F87171),白字压上去只有 2.7~3.1:1,读不出来 —— 那些位置
            // 需要的是**深**字。派生规则见 OnSolid。
            ["VelaErrorForeground"] = OnSolid(error, accentForeground),
            ["VelaWarningForeground"] = OnSolid(warning, accentForeground),
            ["VelaSuccessForeground"] = OnSolid(success, accentForeground),

            // 关闭按钮悬停时的红底(标题栏、各工具窗口右上角)。原先六处都硬写着
            // Windows 的 #E81123,在 Sakura / GitHub Light 这类主题上是一块外来色。
            ["VelaDangerHover"] = WithAlpha(error, 0xE6),
            // 错误态的浅底面板(重连提示条一类):同色相压到很淡,不抢正文。
            ["VelaErrorSurface"] = WithAlpha(error, 0x26),

            // ——— 会话树拖放高亮 ———
            // 拖到分组上 = 并入(黄),拖到分组外 = 移出(红)。都要很淡:它铺在整行底下。
            ["VelaDropTargetGroup"] = WithAlpha(yellow, 0x20),
            ["VelaDropTargetRemove"] = WithAlpha(error, 0x20),

            // ——— 会话强调色板(标签强调条 / 会话徽标) ———
            // 原先是 ConnectionAccent 里写死的 8 个 Dracula 色值,亮色主题下整体失配。
            // 改为从当前主题的种子色派生:每个色都已经过 UiThemeCatalogTests 的对比度尺子。
            ["VelaAccentPalette0"] = info,
            ["VelaAccentPalette1"] = success,
            ["VelaAccentPalette2"] = warning,
            ["VelaAccentPalette3"] = magenta,
            ["VelaAccentPalette4"] = accent,
            ["VelaAccentPalette5"] = yellow,
            ["VelaAccentPalette6"] = error,
            ["VelaAccentPalette7"] = Blend(info, accent, 0.5),

            // ——— 同步输入通道徽章(A/B/C/D 四个广播组) ———
            ["VelaSyncChannelA"] = magenta,
            ["VelaSyncChannelB"] = info,
            ["VelaSyncChannelC"] = warning,
            ["VelaSyncChannelD"] = success,

            // ——— 遮罩 ———
            // 两档:轻(点空白处即关的浮层)与重(模态抽屉/对话框)。
            ["VelaScrim"] = WithAlpha(scrim, 0x99),
            ["VelaScrimStrong"] = WithAlpha(scrim, 0xCC),

            // ——— 文件浏览器行(设计 dyuii) ———
            ["VelaFileFolderIcon"] = Parse(p.FolderIcon),
            ["VelaFileDirName"] = info,

            // ——— 资源占用条:CPU 与内存用不同色相区分,越界统一转黄/红 ———
            ["VelaGaugeCpu"] = info,
            ["VelaGaugeMemory"] = accent,
            ["VelaGaugeWarn"] = yellow,
            ["VelaGaugeCritical"] = error,
            // 轨道必须比它所在的卡片底明显差一档,否则未填充段看不见,
            // 占用条会变成一颗孤零零的胶囊,读不出"占了多少"。
            ["VelaGaugeTrack"] = Parse(p.BorderSecondary),

            // ——— 链路追踪地图 ———
            ["VelaTraceLine"] = info,
            // 未到达段:把线色按一半掺进窗口底色,压暗但不改色相。
            ["VelaTraceLineDim"] = Blend(info, bgPage, 0.5),
            ["VelaTraceLand"] = Parse(p.TraceLand),
            ["VelaTraceBorder"] = Parse(p.TraceBorder),
            // 标注底片:压在陆地上也要读得清,取色阶最极端的一端(暗色最深、亮色最浅)。
            ["VelaTraceLabelBg"] = WithAlpha(dark ? bgPage : bgTerminal, dark ? (byte)0xD2 : (byte)0xE6),

            // ——— 平面底色(VelaShellTokens.axaml) ———
            ["VelaBgPage"] = bgPage,
            ["VelaBgSidebar"] = Parse(p.BgSidebar),
            ["VelaBgTerminal"] = bgTerminal,
            // SFTP 面板与停靠文档区默认同终端色;单列出来是为了背景图功能可与终端分开调不透明度。
            ["VelaBgSftpPanel"] = bgTerminal,
            ["VelaBgDockDocument"] = bgTerminal,
            ["VelaBgSurface"] = Parse(p.BgSurface),
            ["VelaBgContentHeader"] = Parse(p.BgSurface),
            ["VelaBgActive"] = Parse(p.BgActive),
            ["VelaBgHover"] = Parse(p.BgHover),
            ["VelaBgInput"] = Parse(p.BgInput),
            ["VelaTabActiveBg"] = bgTerminal,
            ["VelaTabInactiveBg"] = Parse(p.BgTabInactive),

            // ——— 终端色标记:图表、徽标等界面元素借用同一套色相 ———
            ["VelaShellWhite"] = textPrimary,
            ["VelaShellGreen"] = success,
            ["VelaShellCyan"] = info,
            ["VelaShellBlue"] = accent,
            ["VelaShellYellow"] = yellow,
            ["VelaShellRed"] = error,
            ["VelaShellMagenta"] = magenta,
            ["VelaShellSubtle"] = textTertiary,
            ["VelaShellGreenDim"] = WithAlpha(success, dim),
            ["VelaShellCyanDim"] = WithAlpha(info, dim),
            ["VelaShellBlueDim"] = WithAlpha(accent, dim),
            ["VelaShellYellowDim"] = WithAlpha(yellow, dim),
            ["VelaShellRedDim"] = WithAlpha(error, dim),
            ["VelaShellMagentaDim"] = WithAlpha(magenta, dim),
            ["VelaShellSubtleDim"] = WithAlpha(textTertiary, subtleDim),
            // 图表刻度文字的底片:曲线面积铺满时,直接写字会糊进填充里。
            ["VelaChartLabelBg"] = WithAlpha(bgTerminal, dark ? (byte)0xC0 : (byte)0xD0),

            // ——— 逻辑处理器热力网格:空闲 → 低 → 中 → 高 → 满 ———
            // 五级必须是能一眼排出先后的色序(灰 → 青 → 强调 → 黄 → 红),
            // 而不是同一个色相加不同透明度 —— 后者在小方块上根本分不出档位。
            ["VelaHeat1"] = WithAlpha(textTertiary, dark ? (byte)0x50 : (byte)0x28),
            ["VelaHeat2"] = WithAlpha(info, dark ? (byte)0x55 : (byte)0x33),
            ["VelaHeat3"] = WithAlpha(accent, dark ? (byte)0x55 : (byte)0x44),
            ["VelaHeat4"] = WithAlpha(yellow, dark ? (byte)0x55 : (byte)0x44),
            ["VelaHeat5"] = WithAlpha(error, dark ? (byte)0x66 : (byte)0x55),

            // ——— 滚动条(ScrollBarThemes.axaml 的控件模板按这几个键取色) ———
            // 滑道是**唯一**带主题色的一支:它是正文底上凹下去的一道槽,必须跟着底色走。
            // 原先它在 axaml 里按明暗写死两个值(Dracula 的 #232532 / Alucard 的 #EDE7D0),
            // 于是 Tokyo Night、Nord、Sakura… 的滚动条槽都顶着别人的颜色。
            // 取法:正文底压暗一档(暗色 15%、亮色 7%)。
            // 不从窗口铬色/描边色去推 —— 那两支与正文底的关系逐主题不同,Gruvbox 的
            // 铬色掺描边正好等于它自己的正文底 #282828,槽就此消失。压暗才是与底色**恒定**
            // 的关系,十二套主题一视同仁。亮色那一档更浅是照 Windows 的比例(#FFFFFF → #F0F0F0)。
            ["VelaScrollBarTrackFill"] = Blend(bgTerminal, Colors.Black, dark ? 0.85 : 0.93),
            // 滑块与箭头**故意**不跟主题:它们是 Windows 滚动条那套中性灰(逐像素量的,
            // 见 ScrollBarThemes.axaml 的文件头)。滚动条是覆盖在内容之上的系统级构件,
            // 染上主题色反而会跟正文抢注意力 —— 这里只按明暗分两套。
            ["VelaScrollBarThumbFill"] = Parse(dark ? "#959595" : "#8C8C8C"),
            ["VelaScrollBarThumbFillPointerOver"] = Parse(dark ? "#B9B9B9" : "#6B6B6B"),
            ["VelaScrollBarThumbFillPressed"] = Parse(dark ? "#D6D6D6" : "#4A4A4A"),
            ["VelaScrollBarThumbFillDisabled"] = Parse("#00000000"),
            ["VelaScrollBarArrowFill"] = Parse(dark ? "#959595" : "#5D5D5D"),
            ["VelaScrollBarArrowFillPointerOver"] = Parse(dark ? "#E8E8E8" : "#1F1F1F"),
            ["VelaScrollBarArrowFillDisabled"] = Parse(dark ? "#5A5A5A" : "#AFAFAF"),
            ["VelaScrollBarButtonBackgroundPointerOver"] = Parse(dark ? "#1AFFFFFF" : "#14000000"),
            ["VelaScrollBarButtonBackgroundPressed"] = Parse(dark ? "#2EFFFFFF" : "#26000000"),
        };
    }

    /// <summary>
    /// 压在**实心**语义色上的文字/图标色。
    /// <para>
    /// 先用主题自己指定的「实心底上的字」色 <see cref="UiThemePalette.AccentForeground" /> ——
    /// 它本来就是为强调色实底挑的(暗色主题是近黑、亮色主题是近白),用它能保住主题的调子。
    /// 那一支够不到 AA(4.5:1)时才退到纯黑/纯白里对比更高的一边:One Light 的红 #E45649
    /// 配它自己的近白只有 3.7:1,而配纯黑有 5.7:1 —— 这种情况下"保调子"就是让人读不见字。
    /// </para>
    /// </summary>
    private static Color OnSolid(Color fill, Color accentForeground) =>
        Contrast(fill, accentForeground) >= 4.5
            ? accentForeground
            : Contrast(fill, Colors.Black) >= Contrast(fill, Colors.White) ? Colors.Black : Colors.White;

    /// <summary>WCAG 相对对比度(1:1 ~ 21:1)。</summary>
    private static double Contrast(Color a, Color b)
    {
        double la = RelativeLuminance(a);
        double lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>WCAG 相对亮度。</summary>
    private static double RelativeLuminance(Color color) =>
        (0.2126 * Linearize(color.R)) + (0.7152 * Linearize(color.G)) + (0.0722 * Linearize(color.B));

    private static double Linearize(byte channel)
    {
        double value = channel / 255.0;
        return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    /// <summary>解析 <c>#RRGGBB</c> / <c>#AARRGGBB</c>;种子色都是常量,解析失败即是写错了色值。</summary>
    private static Color Parse(string hex) =>
        Color.TryParse(hex, out Color color)
            ? color
            : throw new ArgumentException($@"Invalid theme color literal: '{hex}'.", nameof(hex));

    private static Color WithAlpha(Color color, byte alpha) => new(alpha, color.R, color.G, color.B);

    private static Color Blend(Color top, Color bottom, double ratio) =>
        new(
            0xFF,
            (byte)Math.Round((top.R * ratio) + (bottom.R * (1 - ratio))),
            (byte)Math.Round((top.G * ratio) + (bottom.G * (1 - ratio))),
            (byte)Math.Round((top.B * ratio) + (bottom.B * (1 - ratio))));
}

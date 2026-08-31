using Avalonia.Controls;
using Avalonia.Media;
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
/// <b>为什么能盖住 axaml 里的值</b>:Avalonia 的资源查找先看字典自身的条目,再看它的
/// ThemeDictionaries 与合并字典。写进 <c>Application.Resources</c> 顶层的键因此会遮蔽
/// <c>VelaTokens.axaml</c> / <c>VelaShellTokens.axaml</c> 里同名的主题条目,
/// 所有 <c>DynamicResource</c> 立刻跟着变 —— 强调色覆盖(#3)一直就是这么做的。
/// axaml 里的两套仍然保留:它们是 VelaDark / VelaLight 的编译期缺省,
/// 设计器、headless 测试与本类跑起来之前的那一瞬间靠它们。
/// </para>
/// </summary>
internal static class ThemeTokenApplier
{
    /// <summary>本类会写入的全部令牌键(切主题时整套重写,不会留下上一套的残值)。</summary>
    internal static IReadOnlyList<string> TokenKeys { get; } = [.. BuildTokens(UiThemeCatalog.DefaultDark).Keys];

    /// <summary>把主题的整套令牌写入资源字典;随后调用方需要重新贴一次强调色覆盖。</summary>
    public static void Apply(IResourceDictionary resources, UiTheme theme)
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
        };
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

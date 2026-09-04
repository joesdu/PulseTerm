using System.Globalization;
using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Models;

/// <summary>
/// 主题目录的硬约束。九套主题各二十几个种子色,靠眼睛看不出哪一套的次要文字掉到了
/// 3:1 —— 这些用例就是那把尺子,新增一套主题时它们必须仍然是绿的。
/// <para>
/// 约束与宿主侧 <c>ThemeTokenContrastTests</c>(读 axaml 校验 VelaDark / VelaLight)同源,
/// 只是把量程扩到了全部具名主题。
/// </para>
/// </summary>
[TestClass]
[TestCategory("ThemeTokens")]
public sealed class UiThemeCatalogTests
{
    /// <summary>WCAG AA 对正文(小字号)的最低对比度。</summary>
    private const double MinimumBodyContrast = 4.5;

    /// <summary>状态色/图标这类"能认出来就行"的非正文元素,按 AA 的图形与界面组件档(3:1)。</summary>
    private const double MinimumGlyphContrast = 3.0;

    [TestMethod]
    public void Catalog_HasUniqueIdsAndNames()
    {
        Assert.HasCount(
            UiThemeCatalog.All.Length,
            UiThemeCatalog.All.Select(theme => theme.Id).Distinct(StringComparer.OrdinalIgnoreCase),
            "主题 Id 会被写进配置文件,重复即意味着有一套主题永远选不中。");
        Assert.HasCount(
            UiThemeCatalog.All.Length,
            UiThemeCatalog.All.Select(theme => theme.Name).Distinct(StringComparer.Ordinal),
            "下拉里出现两个同名主题,用户无从分辨。");
    }

    /// <summary>历史配置里存的是 "dark" / "light",这两个 Id 不能改名 —— 改了等于把老用户的主题重置。</summary>
    [TestMethod]
    public void LegacyIds_StillResolveToTheDefaultThemes()
    {
        Assert.AreEqual("VelaDark", UiThemeCatalog.Get("dark").Name);
        Assert.AreEqual("VelaLight", UiThemeCatalog.Get("light").Name);
        Assert.IsTrue(UiThemeCatalog.IsValidId("system"));
        Assert.IsFalse(UiThemeCatalog.IsValidId("ocean"), "未知主题必须被拒,否则会静默落到暗色。");
    }

    /// <summary>“跟随系统”不是一套配色:解析时按系统明暗落到默认暗/亮主题。</summary>
    [TestMethod]
    public void Resolve_FollowSystem_LandsOnDefaultThemeByVariant()
    {
        Assert.AreEqual("dark", UiThemeCatalog.Resolve("system", systemPrefersDark: true).Id);
        Assert.AreEqual("light", UiThemeCatalog.Resolve("system", systemPrefersDark: false).Id);
        Assert.AreEqual("tokyo-night", UiThemeCatalog.Resolve("tokyo-night", systemPrefersDark: false).Id,
            "选了具名主题就该按它来,系统明暗不参与。");
    }

    /// <summary>插件契约只认 dark / light / system,具名主题不能原样漏出去。</summary>
    [TestMethod]
    public void VariantName_NeverLeaksNamedThemesToPlugins()
    {
        foreach (UiTheme theme in UiThemeCatalog.All)
        {
            string variant = UiThemeCatalog.VariantName(theme.Id);
            Assert.AreEqual(theme.IsDark ? "dark" : "light", variant, $"{theme.Name} 的对外明暗名不对。");
        }
        Assert.AreEqual("system", UiThemeCatalog.VariantName("system"));
        Assert.AreEqual("system", UiThemeCatalog.VariantName(null));
    }

    /// <summary>正文两档文字压在任何一层底色上都必须达到 AA(4.5:1)。</summary>
    [TestMethod]
    public void BodyText_MeetsWcagAaOnEverySurface()
    {
        var failures = new List<string>();
        foreach (UiTheme theme in UiThemeCatalog.All)
        {
            UiThemePalette p = theme.Palette;
            (string Name, string Value)[] surfaces =
            [
                ("BgSurface", p.BgSurface),
                ("BgInput", p.BgInput),
                ("BgHover", p.BgHover),
                ("BgTerminal", p.BgTerminal),
                ("BgSidebar", p.BgSidebar),
                ("BgPage", p.BgPage),
                // 选中行底可能是半透明的强调色淡底,取它压在浮层底上的观感色。
                ("BgActive", Composite(p.BgActive, p.BgSurface)),
            ];
            foreach ((string surfaceName, string surface) in surfaces)
            {
                foreach ((string textName, string text) in new[]
                         {
                             ("TextPrimary", p.TextPrimary), ("TextSecondary", p.TextSecondary),
                         })
                {
                    double ratio = Contrast(text, surface);
                    if (ratio < MinimumBodyContrast)
                    {
                        failures.Add(
                            $"{theme.Name}: {textName}({text}) 压在 {surfaceName}({surface}) 上只有 {ratio:F2}:1");
                    }
                }
            }
        }
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    /// <summary>按钮上的文字压在强调色实底上同样是正文,必须达到 AA。</summary>
    [TestMethod]
    public void AccentForeground_MeetsWcagAaOnAccent()
    {
        var failures = new List<string>();
        foreach (UiTheme theme in UiThemeCatalog.All)
        {
            double ratio = Contrast(theme.Palette.AccentForeground, theme.Palette.Accent);
            if (ratio < MinimumBodyContrast)
            {
                failures.Add($"{theme.Name}: AccentForeground 压在 Accent 上只有 {ratio:F2}:1");
            }
        }
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    /// <summary>状态点与语义色要在浮层底上认得出来(AA 的图形档 3:1)。</summary>
    [TestMethod]
    public void SemanticColors_AreDistinguishableOnSurface()
    {
        var failures = new List<string>();
        foreach (UiTheme theme in UiThemeCatalog.All)
        {
            UiThemePalette p = theme.Palette;
            foreach ((string name, string value) in new[]
                     {
                         ("Accent", p.Accent), ("Success", p.Success), ("Warning", p.Warning),
                         ("Yellow", p.Yellow), ("Error", p.Error), ("Info", p.Info),
                         ("Magenta", p.Magenta), ("FolderIcon", p.FolderIcon),
                     })
            {
                double ratio = Contrast(value, p.BgSurface);
                if (ratio < MinimumGlyphContrast)
                {
                    failures.Add($"{theme.Name}: {name}({value}) 压在 BgSurface 上只有 {ratio:F2}:1");
                }
            }
        }
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// 强调色淡底只能改透明度、不能改色相 —— Avalonia 的颜色字面量是 <c>#AARRGGBB</c>,
    /// 把透明度写在末尾(<c>#644AC922</c>)不报错,而是悄悄变成另一个颜色(issue #246)。
    /// </summary>
    [TestMethod]
    public void TranslucentBgActive_KeepsTheAccentHue()
    {
        foreach (UiTheme theme in UiThemeCatalog.All)
        {
            (byte alpha, byte r, byte g, byte b) = Parse(theme.Palette.BgActive);
            if (alpha == 0xFF)
            {
                continue; // 不透明变体(暗色主题用中性提亮色)不受此约束。
            }
            (byte _, byte ar, byte ag, byte ab) = Parse(theme.Palette.Accent);
            Assert.AreEqual((ar, ag, ab), (r, g, b),
                $"{theme.Name}: BgActive={theme.Palette.BgActive} 的 RGB 与 Accent 不符 —— "
                + "多半是把透明度写在了末尾。");
        }
    }

    /// <summary>
    /// 每套主题都要有配套的终端方案,且它的背景色必须等于界面的终端平面底色。
    /// 差一档就会在终端边缘留下一道看得见的拼缝 —— 亮色主题此前配 Solarized Light
    /// (#FDF6E3)而界面底是 #FFFBEB,那道缝存在了很久。
    /// </summary>
    [TestMethod]
    public void EveryTheme_PairsWithATerminalSchemeOfTheSameBackground()
    {
        foreach (UiTheme theme in UiThemeCatalog.All)
        {
            TerminalColorScheme? scheme = Array.Find(
                TerminalColorScheme.BuiltIn, s => s.Name == theme.TerminalSchemeName);
            Assert.IsNotNull(scheme, $"{theme.Name} 配套的终端方案「{theme.TerminalSchemeName}」不在内置表里。");
            Assert.AreEqual(
                theme.Palette.BgTerminal.ToUpperInvariant(),
                scheme.Background.ToUpperInvariant(),
                $"{theme.Name}:终端方案「{scheme.Name}」的背景与界面的 BgTerminal 不一致。");
        }
    }

    /// <summary>终端方案自身的可读性:正文与 ANSI 前六色压在方案背景上要认得出。</summary>
    [TestMethod]
    public void PairedTerminalSchemes_AreReadableOnTheirOwnBackground()
    {
        var failures = new List<string>();
        foreach (UiTheme theme in UiThemeCatalog.All)
        {
            TerminalColorScheme scheme = theme.Terminal;
            double fg = Contrast(scheme.Foreground, scheme.Background);
            if (fg < MinimumBodyContrast)
            {
                failures.Add($"{scheme.Name}: 前景压在背景上只有 {fg:F2}:1");
            }
            // 索引 0 是 black、7 是 white,两者本就贴着底色的两端,不参与此项。
            for (int i = 1; i <= 6; i++)
            {
                double ratio = Contrast(scheme.AnsiNormal[i], scheme.Background);
                if (ratio < MinimumGlyphContrast)
                {
                    failures.Add($"{scheme.Name}: ANSI {i}({scheme.AnsiNormal[i]}) 只有 {ratio:F2}:1");
                }
            }
        }
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    // ———— 颜色计算(与 ThemeTokenContrastTests 同一套口径) ————

    private static (byte A, byte R, byte G, byte B) Parse(string literal)
    {
        string hex = literal.TrimStart('#');
        Assert.IsTrue(hex.Length is 6 or 8, $"只支持 #RRGGBB / #AARRGGBB,收到 {literal}。");
        uint packed = uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return hex.Length == 6
            ? ((byte)0xFF, (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed)
            : ((byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
    }

    /// <summary>半透明色压在基底上的观感色。</summary>
    private static string Composite(string top, string under)
    {
        (byte ta, byte tr, byte tg, byte tb) = Parse(top);
        (byte _, byte ur, byte ug, byte ub) = Parse(under);
        double a = ta / 255.0;
        return $"#{(byte)Math.Round((a * tr) + ((1 - a) * ur)):X2}"
               + $"{(byte)Math.Round((a * tg) + ((1 - a) * ug)):X2}"
               + $"{(byte)Math.Round((a * tb) + ((1 - a) * ub)):X2}";
    }

    private static double Luminance(string literal)
    {
        (byte _, byte r, byte g, byte b) = Parse(literal);
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return (0.2126 * Channel(r)) + (0.7152 * Channel(g)) + (0.0722 * Channel(b));
    }

    private static double Contrast(string a, string b)
    {
        double la = Luminance(a);
        double lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}

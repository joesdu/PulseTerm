using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using VelaShell.Core.Models;
using VelaShell.Services;

namespace VelaShell.Tests.Services;

/// <summary>
/// 主题令牌展开器的两条硬约束:
/// <para>
/// 1. <b>覆盖面</b>:每套主题都要把**同一组**令牌键写全。少写一个,切主题时那个键就会
///    留着上一套主题的值 —— 界面上是一小块颜色对不上,而且只在特定的切换顺序下出现。
/// </para>
/// <para>
/// 2. <b>与 axaml 缺省一致</b>:VelaDark / VelaLight 展开出来的值必须逐一等于
///    <c>VelaTokens.axaml</c> / <c>VelaShellTokens.axaml</c> / <c>DarkTheme.axaml</c> /
///    <c>LightTheme.axaml</c> 里的同名令牌。那两份是贴主题之前的编译期缺省(设计器、
///    headless 测试、启动的头一瞬间看到的就是它们),两边漂了就会闪一下色。
/// </para>
/// </summary>
[TestClass]
[TestCategory("ThemeTokens")]
public sealed class ThemeTokenApplierTests
{
    [TestMethod]
    public void EveryTheme_WritesTheSameTokenKeySet()
    {
        string[] expected = [.. ThemeTokenApplier.TokenKeys.Order(StringComparer.Ordinal)];
        Assert.IsNotEmpty(expected);
        foreach (UiTheme theme in UiThemeCatalog.All)
        {
            var resources = new ResourceDictionary();
            ThemeTokenApplier.Fill(resources, theme);
            string[] actual =
            [
                .. resources.Keys.OfType<string>()
                    .Where(key => key != "VelaShadowWindow")
                    .Order(StringComparer.Ordinal),
            ];
            CollectionAssert.AreEqual(expected, actual, $"{theme.Name} 写出的令牌集合与其它主题不一致。");
            Assert.IsTrue(resources.ContainsKey("VelaShadowWindow"), $"{theme.Name} 少了浮层投影令牌。");
        }
    }

    /// <summary>切主题是整套重写:上一套的值一个都不能留下。</summary>
    [TestMethod]
    public void SwitchingThemes_LeavesNoStaleValues()
    {
        var resources = new ResourceDictionary();
        ThemeTokenApplier.Fill(resources, UiThemeCatalog.Get("dark"));
        ThemeTokenApplier.Fill(resources, UiThemeCatalog.Get("github-light"));

        var expected = new ResourceDictionary();
        ThemeTokenApplier.Fill(expected, UiThemeCatalog.Get("github-light"));
        foreach (string key in ThemeTokenApplier.TokenKeys)
        {
            Assert.AreEqual(
                ColorOf(expected, key),
                ColorOf(resources, key),
                $"令牌 {key} 在换主题后仍留着上一套的值。");
        }
    }

    /// <summary>
    /// 清空自定义强调色时,要落回**当前主题**的强调色。
    /// <para>
    /// 老实现是把三个键从资源里删掉,让它们掉回 axaml 的缺省 —— 那在只有明暗两套时是对的,
    /// 到了具名主题上就成了错的:Tokyo Night 的蓝会变成 VelaDark 的紫。
    /// </para>
    /// </summary>
    [TestMethod]
    public void ResetAccent_RestoresTheThemesOwnAccent_NotTheDefaultOne()
    {
        UiTheme midnight = UiThemeCatalog.Get("tokyo-night");
        var resources = new ResourceDictionary();
        ThemeTokenApplier.Fill(resources, midnight);

        // 用户挑了个自定义强调色,随后又清空。
        resources["VelaAccent"] = new SolidColorBrush(Colors.Red);
        ThemeTokenApplier.ResetAccent(resources, midnight);

        Assert.AreEqual(Color.Parse(midnight.Palette.Accent), ColorOf(resources, "VelaAccent"));
        Assert.AreEqual(
            Color.Parse(midnight.Palette.AccentForeground),
            ColorOf(resources, "VelaAccentForeground"));
        Color dim = ColorOf(resources, "VelaAccentDim")!.Value;
        var accent = Color.Parse(midnight.Palette.Accent);
        Assert.AreEqual((accent.R, accent.G, accent.B), (dim.R, dim.G, dim.B),
            "淡底只能改透明度,不能改色相。");
        Assert.AreNotEqual(0xFF, dim.A, "淡底必须是半透明的。");
    }

    /// <summary>
    /// 压在实心语义色上的字必须在**每一套**主题上都达到 AA(4.5:1)。
    /// <para>
    /// 这条不是形式主义:危险按钮上的 <c>#FFFFFF</c> 曾经是硬编码的,而暗色主题的红是**亮**红
    /// (VelaDark 的 #FF5555、Obsidian 的 #F87171),白字压上去只有 2.7~3.1:1 —— 那几个字
    /// 在屏幕上是糊的。派生规则(OnSolid)优先用主题自己的近黑/近白,够不到才退纯黑/纯白。
    /// </para>
    /// </summary>
    [TestMethod]
    public void OnSolidForegrounds_MeetWcagAaOnEveryTheme()
    {
        (string Fill, string Foreground)[] pairs =
        [
            ("VelaError", "VelaErrorForeground"),
            ("VelaWarning", "VelaWarningForeground"),
            ("VelaStatusConnected", "VelaSuccessForeground"),
        ];
        var failures = new List<string>();
        foreach (UiTheme theme in UiThemeCatalog.All)
        {
            var resources = new ResourceDictionary();
            ThemeTokenApplier.Fill(resources, theme);
            foreach ((string fill, string foreground) in pairs)
            {
                double ratio = Contrast(ColorOf(resources, fill)!.Value, ColorOf(resources, foreground)!.Value);
                if (ratio < 4.5)
                {
                    failures.Add($"{theme.Name} {foreground} 压在 {fill} 上只有 {ratio:F2}:1");
                }
            }
        }
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// 遮罩必须**真的**能压暗底下的界面。
    /// <para>
    /// 核的是相对亮度的**跌幅**而不是对比度:暗色主题的正文底本来就接近黑,一层深色遮罩
    /// 压上去算出来的对比度永远只有 1.x —— 但它确实把界面压暗了,而这正是模态遮罩要做的事。
    /// 跌幅在明暗两侧都说得通:亮色主题上"铺了一层浅色什么也没发生"会被这一条抓住。
    /// </para>
    /// <para>按最亮的一层平面(终端底)核 —— 遮罩在那一层上最不容易起作用。</para>
    /// </summary>
    [TestMethod]
    public void Scrims_ActuallyDarkenTheBrightestSurface()
    {
        var failures = new List<string>();
        foreach (UiTheme theme in UiThemeCatalog.All)
        {
            var resources = new ResourceDictionary();
            ThemeTokenApplier.Fill(resources, theme);
            var under = Color.Parse(theme.Palette.BgTerminal);
            double baseline = Luminance(under);
            double previous = double.MaxValue;
            foreach (string key in new[] { "VelaScrim", "VelaScrimStrong" })
            {
                Color scrim = ColorOf(resources, key)!.Value;
                Assert.AreNotEqual(0xFF, scrim.A, $"{theme.Name} 的 {key} 必须是半透明的。");
                double lit = Luminance(Over(scrim, under));
                double drop = 1 - (lit / baseline);
                if (drop < 0.35)
                {
                    failures.Add($"{theme.Name} 的 {key} 压在 {theme.Palette.BgTerminal} 上只压暗了 {drop:P0}");
                }
                // 重的那一档必须比轻的更暗,否则两个令牌就没有分别。
                if (lit >= previous)
                {
                    failures.Add($"{theme.Name} 的 VelaScrimStrong 没有比 VelaScrim 更暗。");
                }
                previous = lit;
            }
        }
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    /// <summary>滚动条滑道要跟着主题走 —— 12 套主题不该共用两个值。</summary>
    [TestMethod]
    public void ScrollBarTrack_FollowsEachTheme()
    {
        var byTrack = new Dictionary<Color, List<string>>();
        foreach (UiTheme theme in UiThemeCatalog.All)
        {
            var resources = new ResourceDictionary();
            ThemeTokenApplier.Fill(resources, theme);
            Color track = ColorOf(resources, "VelaScrollBarTrackFill")!.Value;
            (byTrack.TryGetValue(track, out List<string>? names) ? names : byTrack[track] = []).Add(theme.Name);
            // 槽必须与它所在的正文底分得开,否则未填充段看不见,滚动条变成一颗孤零零的胶囊。
            Assert.AreNotEqual(Color.Parse(theme.Palette.BgTerminal), track,
                $"{theme.Name} 的滚动条滑道与终端底同色。");
        }
        Assert.HasCount(UiThemeCatalog.All.Length, byTrack,
            "各主题的滚动条滑道应互不相同(说明它是派生的,不是写死的两个值)。");
    }

    /// <summary>把半透明色压在不透明底色上(源覆盖合成)。</summary>
    private static Color Over(Color top, Color bottom)
    {
        double a = top.A / 255.0;
        return new(
            0xFF,
            (byte)Math.Round((top.R * a) + (bottom.R * (1 - a))),
            (byte)Math.Round((top.G * a) + (bottom.G * (1 - a))),
            (byte)Math.Round((top.B * a) + (bottom.B * (1 - a))));
    }

    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a);
        double lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            double c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }

    [TestMethod]
    public void VelaDark_MatchesTheCompiledDefaults() => AssertMatchesAxaml("dark", "Dark", "DarkTheme.axaml");

    [TestMethod]
    public void VelaLight_MatchesTheCompiledDefaults() => AssertMatchesAxaml("light", "Light", "LightTheme.axaml");

    private static void AssertMatchesAxaml(string themeId, string variant, string variantFile)
    {
        Dictionary<string, string> axaml = LoadVariantTokens(variant);
        foreach (KeyValuePair<string, string> entry in LoadFlatTokens(variantFile))
        {
            axaml[entry.Key] = entry.Value;
        }
        Assert.IsNotEmpty(axaml, "令牌 axaml 没被复制到测试输出目录。");

        var applied = new ResourceDictionary();
        ThemeTokenApplier.Fill(applied, UiThemeCatalog.Get(themeId));

        var failures = new List<string>();
        foreach ((string key, string literal) in axaml)
        {
            if (ColorOf(applied, key) is not { } color)
            {
                failures.Add($"{key}:axaml 里有,展开器没写");
                continue;
            }
            var expected = Color.Parse(literal);
            if (color != expected)
            {
                failures.Add($"{key}:axaml={literal} 展开器={color}");
            }
        }
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    private static Color? ColorOf(ResourceDictionary resources, string key) =>
        resources.TryGetResource(key, null, out object? value) && value is ISolidColorBrush brush
            ? brush.Color
            : null;

    /// <summary>读 ThemeDictionaries 形态的令牌文件(VelaTokens / VelaShellTokens)。</summary>
    private static Dictionary<string, string> LoadVariantTokens(string variant)
    {
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (string file in new[] { "VelaTokens.axaml", "VelaShellTokens.axaml", "ScrollBarThemes.axaml" })
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Themes", file);
            Assert.IsTrue(File.Exists(path), $"令牌文件未复制到输出目录:{path}");
            foreach (XElement dictionary in XDocument.Load(path).Descendants())
            {
                if (dictionary.Name.LocalName != "ResourceDictionary"
                    || dictionary.Attribute(x + "Key")?.Value != variant)
                {
                    continue;
                }
                foreach (XElement brush in dictionary.Elements())
                {
                    if (brush.Name.LocalName == "SolidColorBrush"
                        && brush.Attribute(x + "Key")?.Value is { } key
                        && brush.Attribute("Color")?.Value is { } color)
                    {
                        tokens[key] = color;
                    }
                }
            }
        }
        return tokens;
    }

    /// <summary>读平铺形态的令牌文件(DarkTheme / LightTheme,按变体各一份)。</summary>
    private static Dictionary<string, string> LoadFlatTokens(string file)
    {
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        string path = Path.Combine(AppContext.BaseDirectory, "Themes", file);
        Assert.IsTrue(File.Exists(path), $"令牌文件未复制到输出目录:{path}");
        foreach (XElement brush in XDocument.Load(path).Root!.Elements())
        {
            if (brush.Name.LocalName == "SolidColorBrush"
                && brush.Attribute(x + "Key")?.Value is { } key
                && brush.Attribute("Color")?.Value is { } color)
            {
                tokens[key] = color;
            }
        }
        return tokens;
    }
}

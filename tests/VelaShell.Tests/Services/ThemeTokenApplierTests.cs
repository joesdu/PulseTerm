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
        Color accent = Color.Parse(midnight.Palette.Accent);
        Assert.AreEqual((accent.R, accent.G, accent.B), (dim.R, dim.G, dim.B),
            "淡底只能改透明度,不能改色相。");
        Assert.AreNotEqual(0xFF, dim.A, "淡底必须是半透明的。");
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
            Color expected = Color.Parse(literal);
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
        foreach (string file in new[] { "VelaTokens.axaml", "VelaShellTokens.axaml" })
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

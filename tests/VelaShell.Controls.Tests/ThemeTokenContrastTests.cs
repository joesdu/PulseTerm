using System.Globalization;
using System.Xml.Linq;

namespace VelaShell.Controls.Tests;

/// <summary>
/// 调色板令牌的两条硬约束(issue #246「快捷指令文字看不清」)。
/// <para>
/// 1. <b>通道顺序</b>:Avalonia 的颜色字面量是 <c>#AARRGGBB</c>。把透明度写在末尾
///    (<c>#644AC922</c>)不会报错,而是被解析成另一个颜色 —— 亮色 <c>VelaBgActive</c>
///    就这么变成了一片 39% 的绿,与 Alucard 的紫强调毫无关系。这类错拼编译期无感,
///    只能靠断言"强调色派生令牌的 RGB 必须等于强调色本身"钉死。
/// </para>
/// <para>
/// 2. <b>对比度</b>:正文两档文字(primary / secondary)压在容器底色家族上必须达到
///    WCAG AA 的 4.5:1。补全弹层的说明文字与来源徽标此前用的是 muted / tertiary,
///    实测只有 1.76:1 与 3.03:1,小字号 + CJK 基本读不出来。
/// </para>
/// </summary>
[TestClass]
[TestCategory("ThemeTokens")]
public class ThemeTokenContrastTests
{
    /// <summary>WCAG AA 对正文(小字号)的最低对比度。</summary>
    private const double MinimumContrast = 4.5;

    private static readonly string[] Variants = ["Dark", "Light"];

    /// <summary>容器底色家族:任何正文都可能落在其中之一上。</summary>
    private static readonly string[] Surfaces =
        ["VelaBgSurface", "VelaBgInput", "VelaBgActive", "VelaBgHover"];

    /// <summary>用于承载正文的两档文字令牌(muted/tertiary 是装饰档,不在此列)。</summary>
    private static readonly string[] BodyText = ["VelaTextPrimary", "VelaTextSecondary"];

    private static readonly Dictionary<string, Dictionary<string, string>> Tokens = Load();

    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        var byVariant = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
        {
            ["Dark"] = new(StringComparer.Ordinal),
            ["Light"] = new(StringComparer.Ordinal),
        };
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (string file in new[] { "VelaTokens.axaml", "VelaShellTokens.axaml" })
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Themes", file);
            Assert.IsTrue(File.Exists(path), $"令牌文件未复制到输出目录:{path}");
            foreach (XElement dictionary in XDocument.Load(path).Descendants())
            {
                string? variant = dictionary.Attribute(x + "Key")?.Value;
                if (dictionary.Name.LocalName != "ResourceDictionary" || variant is null)
                {
                    continue;
                }
                if (!byVariant.TryGetValue(variant, out Dictionary<string, string>? bucket))
                {
                    continue;
                }
                foreach (XElement brush in dictionary.Elements())
                {
                    if (
                        brush.Name.LocalName == "SolidColorBrush"
                        && brush.Attribute(x + "Key")?.Value is { } key
                        && brush.Attribute("Color")?.Value is { } color
                    )
                    {
                        bucket[key] = color;
                    }
                }
            }
        }
        return byVariant;
    }

    private static (byte A, byte R, byte G, byte B) Parse(string literal)
    {
        string hex = literal.TrimStart('#');
        Assert.IsTrue(hex.Length is 6 or 8, $"只支持 #RRGGBB / #AARRGGBB,收到 {literal}。");
        uint packed = uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return hex.Length == 6
            ? ((byte)0xFF, (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed)
            : ((byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
    }

    private static (byte A, byte R, byte G, byte B) Color(string variant, string key)
    {
        Assert.IsTrue(
            Tokens[variant].TryGetValue(key, out string? literal),
            $"{variant} 主题缺少令牌 {key}。"
        );
        return Parse(literal!);
    }

    /// <summary>半透明令牌压在基底上的实际观感色(令牌本身不透明时原样返回)。</summary>
    private static (byte R, byte G, byte B) Over(
        (byte A, byte R, byte G, byte B) top,
        (byte A, byte R, byte G, byte B) under
    )
    {
        double a = top.A / 255.0;
        return (
            (byte)Math.Round((a * top.R) + ((1 - a) * under.R)),
            (byte)Math.Round((a * top.G) + ((1 - a) * under.G)),
            (byte)Math.Round((a * top.B) + ((1 - a) * under.B))
        );
    }

    private static double Luminance((byte R, byte G, byte B) c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    private static double Contrast((byte R, byte G, byte B) a, (byte R, byte G, byte B) b)
    {
        double la = Luminance(a);
        double lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>
    /// 强调色派生的底色令牌只能改透明度,不能改色相 —— RGB 三通道必须与 VelaAccent 一致。
    /// 这正是 <c>#644AC922</c>(应为 <c>#22644AC9</c>)那类通道写反的唯一可靠抓手。
    /// </summary>
    [TestMethod]
    public void AccentDerivedTokens_KeepTheAccentHue()
    {
        foreach (string variant in Variants)
        {
            (byte _, byte ar, byte ag, byte ab) = Color(variant, "VelaAccent");
            foreach (string key in new[] { "VelaAccentDim", "VelaBgActive" })
            {
                if (!Tokens[variant].TryGetValue(key, out string? literal))
                {
                    continue;
                }
                (byte alpha, byte r, byte g, byte b) = Parse(literal);
                if (alpha == 0xFF)
                {
                    continue; // 不透明变体(暗色 VelaBgActive 是中性板岩色)不受此约束。
                }
                Assert.AreEqual(
                    (ar, ag, ab),
                    (r, g, b),
                    $"{variant}/{key} = {literal} 的 RGB 与 VelaAccent 不符 —— "
                        + "多半是把透明度写在了末尾(Avalonia 是 #AARRGGBB)。"
                );
            }
        }
    }

    /// <summary>正文两档文字压在任一容器底色上都必须达到 WCAG AA 的 4.5:1。</summary>
    [TestMethod]
    public void BodyTextTokens_MeetWcagAaOnEverySurface()
    {
        var failures = new List<string>();
        foreach (string variant in Variants)
        {
            (byte A, byte R, byte G, byte B) page = Color(variant, "VelaBgSurface");
            foreach (string surfaceKey in Surfaces)
            {
                // 半透明底色(亮色 VelaBgActive)按压在弹层底色上取观感色。
                (byte R, byte G, byte B) surface = Over(Color(variant, surfaceKey), page);
                foreach (string textKey in BodyText)
                {
                    (byte R, byte G, byte B) text = Over(Color(variant, textKey), (0xFF, surface.R, surface.G, surface.B));
                    double ratio = Contrast(text, surface);
                    if (ratio < MinimumContrast)
                    {
                        failures.Add(
                            $"{variant}: {textKey} 压在 {surfaceKey} 上只有 {ratio:N2}:1"
                        );
                    }
                }
            }
        }
        Assert.IsTrue(
            failures.Count == 0,
            "以下文字/底色组合达不到 4.5:1:\n  " + string.Join("\n  ", failures)
        );
    }

    /// <summary>
    /// 补全弹层的来源徽标(#246):暗色下 tertiary 压在 bg-input 上只有 3.03:1,
    /// 而 <c>#6272A4</c> 即便配纯黑也只到 4.46:1 —— 这一档天生够不到 AA,
    /// 徽标只能用 secondary。此测试把"够不到"这件事本身钉住,防止有人把徽标改回去。
    /// </summary>
    [TestMethod]
    public void DecorativeTextTokens_CannotReachAa_SoTheyMustNotCarryBodyText()
    {
        foreach (string textKey in new[] { "VelaTextTertiary", "VelaTextMuted" })
        {
            (byte _, byte r, byte g, byte b) = Color("Dark", textKey);
            double best = Contrast((r, g, b), (0, 0, 0)); // 配纯黑 = 该色在暗色下的上限
            Assert.IsLessThan(
                MinimumContrast,
                best,
                $"Dark/{textKey} 现在配纯黑能到 {best:N2}:1 —— 它已经够得到 AA 了,"
                    + "本测试的前提失效,请重新评估哪些文字可以用这一档。"
            );
        }
    }
}

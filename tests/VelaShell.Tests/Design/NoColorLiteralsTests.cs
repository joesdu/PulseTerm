using System.Text.RegularExpressions;

namespace VelaShell.Tests.Design;

/// <summary>
/// 守门:XAML 与 C# 里不许再出现颜色字面量。
/// </summary>
/// <remarks>
/// <para>
/// DESIGN.md §2.0 定的规则是"六十多个令牌全部由种子色派生"。写死色值有两个后果:
/// 一是切到亮色主题(Alucard / GitHub Light / Rosé Pine Dawn / Sakura)后那一处整体失配;
/// 二是它绕过了 <c>UiThemeCatalogTests</c> 的 WCAG AA 对比度尺子 —— 没人会发现它不合格。
/// </para>
/// <para>
/// <b>扫描的是裸的 <c>#RRGGBB(AA)</c>,不是 <c>="#…"</c>。</b>色值不一定出现在属性值里:
/// <c>BoxShadow="0 2 8 0 #66000000"</c> 就藏在一串几何参数中间,按属性值扫会整条漏掉。
/// 代价是要先剥掉注释、再排掉少数几个"长得像颜色但不是颜色"的地方(见白名单)。
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Design")]
public sealed class NoColorLiteralsTests
{
    /// <summary>裸的十六进制色值:#RGB 六位或带 alpha 八位。</summary>
    private static readonly Regex ColorLiteral = new(
        @"#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?\b", RegexOptions.Compiled);

    /// <summary>XAML 注释(设计说明里引用色号是正当的,不该被判违规)。</summary>
    private static readonly Regex XamlComment = new(@"<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// 允许保留字面量的地方,以及为什么。
    /// </summary>
    private static readonly (string File, string Reason)[] AllowedFiles =
    [
        ("Settings/AboutPage.axaml",
         "品牌标识的固定渐变:它代表产品本身,不该随用户选的主题变色。")
    ];

    /// <summary>
    /// 允许保留字面量的属性:值长得像颜色,但根本不是颜色。
    /// </summary>
    private static readonly string[] AllowedAttributes =
    [
        // 强调色输入框的提示文字,告诉用户"这里该填 #RRGGBB 格式"。
        "PlaceholderText"
    ];

    [TestMethod]
    public void ViewsContainNoColorLiterals()
    {
        string viewsRoot = Path.Combine(RepoRoot(), "src", "VelaShell", "Views");
        List<string> offenders = [];

        foreach (string file in Directory.EnumerateFiles(viewsRoot, "*.axaml", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(viewsRoot, file).Replace('\\', '/');
            if (AllowedFiles.Any(entry => string.Equals(entry.File, relative, StringComparison.Ordinal)))
            {
                continue;
            }
            // 注释里引用色号(如"暗色下 text-tertiary(#6272A4)对比度只有 3.03:1")是设计记录,不是用色。
            string content = XamlComment.Replace(File.ReadAllText(file), "");
            string[] lines = content.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (!ColorLiteral.IsMatch(lines[i]) || IsAllowedAttribute(lines[i]))
                {
                    continue;
                }
                offenders.Add($"{relative}:{i + 1}  {lines[i].Trim()}");
            }
        }

        Assert.IsEmpty(
            offenders,
            "Views 下不允许出现颜色字面量,请改用 ThemeTokenApplier 派生的令牌"
            + $"({{DynamicResource Vela…}}):{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    [TestMethod]
    public void ColorServicesContainNoColorLiterals()
    {
        // C# 侧同理:配色服务与转换器一律走 ThemeBrushes.Resolve 取令牌。
        // 兜底色(拿不到 Application 时的默认值)仍是字面量,但它们集中在类型顶部的
        // Fallback 表里 —— 这里只扫"运行时真正用来着色"的那几个类。
        string servicesRoot = Path.Combine(RepoRoot(), "src", "VelaShell", "Services");
        string[] files =
        [
            Path.Combine(servicesRoot, "SyncInputChannels.cs")
        ];

        List<string> offenders = [];
        foreach (string file in files.Where(File.Exists))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                // 取令牌时带的兜底色是允许的:ThemeBrushes.Resolve("Key", Color.Parse("#…"))。
                if (lines[i].Contains("ThemeBrushes.Resolve", StringComparison.Ordinal))
                {
                    continue;
                }
                if (lines[i].Contains("Color.Parse(\"#", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}  {lines[i].Trim()}");
                }
            }
        }

        Assert.IsEmpty(
            offenders,
            "配色服务里不允许写死色值,请改为 ThemeBrushes.Resolve(令牌名, 兜底色):"
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    [TestMethod]
    public void EveryAllowedFileStillExists()
    {
        // 白名单里的文件被改名/删掉时,这条会提醒把白名单一起清掉,
        // 免得它悄悄变成一条永远为真的豁免。
        string viewsRoot = Path.Combine(RepoRoot(), "src", "VelaShell", "Views");
        foreach ((string file, string reason) in AllowedFiles)
        {
            Assert.IsTrue(
                File.Exists(Path.Combine(viewsRoot, file.Replace('/', Path.DirectorySeparatorChar))),
                $"白名单条目 {file} 已不存在({reason}),请从 AllowedFiles 里移除。");
        }
    }

    private static bool IsAllowedAttribute(string line) =>
        AllowedAttributes.Any(attribute => line.Contains(attribute + "=\"#", StringComparison.Ordinal));

    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Directory.GetParent(dir)?.FullName)
        {
            if (File.Exists(Path.Combine(dir, "VelaShell.slnx")))
            {
                return dir;
            }
        }
        throw new InvalidOperationException("未能从测试输出目录向上定位到仓库根目录(找不到 VelaShell.slnx)。");
    }
}

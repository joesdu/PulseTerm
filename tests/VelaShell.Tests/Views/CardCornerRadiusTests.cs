using System.Text.RegularExpressions;

namespace VelaShell.Tests.Views;

/// <summary>
/// 自绘卡片窗体的圆角约定检查(静态扫描 .axaml,不实例化窗口)。
/// </summary>
/// <remarks>
/// 卡片是【外圆角 8 + 1px 描边】,子元素被布局在描边内侧,所以子元素的内圆弧半径是 8−1=7。
/// Avalonia 的 ClipToBounds 只裁矩形边界、不按圆角裁剪子元素,因此贴着卡片四角、又有不透明
/// 背景的子元素必须自己带上内圆角,否则它的方角背景会盖住描边在圆弧处的那一段 ——
/// 肉眼看就是四个角上的边框"断掉"了。
///
/// 子元素若照抄外面的 8,圆弧与描边的圆弧重合,同样会把那段描边盖掉(设置窗口的左侧导航
/// 就是这么错的),所以这里把"子元素用 8"也判为错。
/// </remarks>
[TestClass]
[TestCategory("CardShape")]
public sealed partial class CardCornerRadiusTests
{
    /// <summary>卡片外圆角。</summary>
    private const string OuterRadius = "8";

    /// <summary>子元素应当使用的内圆角 = 外圆角 − 1px 描边。</summary>
    private const string InnerRadius = "7";

    [TestMethod]
    public void CardChildren_NeverReuseTheOuterRadius()
    {
        var offenders = new List<string>();
        foreach (string file in CardWindowFiles())
        {
            string[] lines = File.ReadAllLines(file);
            int card = Array.FindIndex(lines, line => line.Contains("VelaShadowWindow", StringComparison.Ordinal));
            if (card < 0)
            {
                continue; // 不是自绘卡片窗体
            }
            for (int i = card + 1; i < lines.Length; i++)
            {
                Match match = CornerRadiusAttributeRegex.Match(lines[i]);
                if (!match.Success)
                {
                    continue;
                }
                // 只揪"带方向的四角写法"(如 8,0,0,8):它必然是在贴着卡片的角对齐外框,
                // 单值圆角(4/6 之类的内部小卡片、药丸)与卡片的角无关,不在此列。
                string[] parts = match.Groups[1].Value.Split(',');
                if (parts.Length == 4 && parts.Contains(OuterRadius))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1} → CornerRadius=\"{match.Groups[1].Value}\"");
                }
            }
        }
        Assert.IsEmpty(offenders,
            $"卡片内的子元素必须用内圆角 {InnerRadius}(= 外圆角 {OuterRadius} − 1px 描边),"
            + $"照抄 {OuterRadius} 会让圆弧与描边重合、把那段描边盖掉,角上看着就是断线:\n"
            + string.Join("\n", offenders));
    }

    /// <summary>src 下所有窗体 .axaml(跳过 obj/bin)。</summary>
    private static IEnumerable<string> CardWindowFiles()
    {
        string root = SourceRoot();
        char sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{sep}obj{sep}", StringComparison.Ordinal)
                && !file.Contains($"{sep}bin{sep}", StringComparison.Ordinal));
    }

    /// <summary>从测试输出目录向上找到仓库里的 src 目录。</summary>
    private static string SourceRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Directory.GetParent(dir)?.FullName)
        {
            string candidate = Path.Combine(dir, "src");
            if (File.Exists(Path.Combine(dir, "VelaShell.slnx")) && Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new InvalidOperationException("未能从测试输出目录向上定位到仓库的 src 目录(找不到同级的 VelaShell.slnx)。");
    }

    [GeneratedRegex(@"CornerRadius=""([^""]+)""")]
    private static partial Regex CornerRadiusAttributeRegex { get; }
}

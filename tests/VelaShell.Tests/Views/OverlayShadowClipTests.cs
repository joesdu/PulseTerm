using System.Xml.Linq;

namespace VelaShell.Tests.Views;

/// <summary>
/// 浮层卡片的投影不能被 UserControl 裁掉(静态扫描 .axaml,不实例化控件)。
/// </summary>
/// <remarks>
/// Avalonia 的 <c>UserControl</c> 默认 <c>ClipToBounds=true</c>。命令面板、隧道面板、传输浮层
/// 这类控件的卡片正好铺满 UserControl,于是 BoxShadow 画在边界之外、被整块裁掉 ——
/// 而四个圆角与矩形裁剪框之间还剩一点楔形空隙,投影只在那儿露出来,看着就是
/// "卡片下面两个角有黑东西"(#171 后续反馈,已实测复现:边上一片纯白,角上却有暗块)。
///
/// 所以:根子元素带 BoxShadow 的 UserControl,必须在根上显式写 ClipToBounds="False"。
/// </remarks>
[TestClass]
[TestCategory("CardShape")]
public sealed class OverlayShadowClipTests
{
    [TestMethod]
    public void UserControlsWithShadowedCard_DisableClipping()
    {
        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(SourceRoot(), "*.axaml", SearchOption.AllDirectories))
        {
            char sep = Path.DirectorySeparatorChar;
            if (file.Contains($"{sep}obj{sep}", StringComparison.Ordinal)
                || file.Contains($"{sep}bin{sep}", StringComparison.Ordinal))
            {
                continue;
            }
            XElement? root = XDocument.Load(file).Root;
            if (root is null || root.Name.LocalName != "UserControl")
            {
                continue;
            }
            // 根的内容元素:跳过 UserControl.Styles / .Resources 之类的属性元素(名字里带点)。
            XElement? card = root.Elements().FirstOrDefault(element => !element.Name.LocalName.Contains('.'));
            if (card?.Attribute("BoxShadow") is null)
            {
                continue;
            }
            if (!string.Equals(root.Attribute("ClipToBounds")?.Value, "False", StringComparison.OrdinalIgnoreCase))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }
        Assert.IsEmpty(offenders,
            "这些 UserControl 的根子元素带投影,却没有关掉默认裁剪,投影会被裁得只剩四个角上的暗块:\n"
            + string.Join("\n", offenders));
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
}

using Avalonia.Media;

namespace VelaShell.Tests.Views;

/// <summary>
/// 用户自定义终端字体必须优先于内置回退。终端字体链把内置 <c>fonts:VelaShell#Cascadia Mono</c>
/// 放在用户字体【之后】作回退;须确保 Avalonia 不会因中段的 <c>源#族名</c> 前缀错拆族名链、
/// 把用户字体吞掉而回落到内置 Cascadia(用户选了自己的字体却渲染成内置字体就尴尬了)。
/// </summary>
[TestClass]
[TestCategory("Fonts")]
public class TerminalFontFallbackTests
{
    // 复刻 MainWindowViewModel.ApplyLiveTerminalSettings 构造的族名链(ResolveTerminalFontFamily 内联)。
    private static string BuildChain(string userFont)
    {
        string resolved = userFont == "Cascadia Mono" ? $"fonts:VelaShell#{userFont}" : userFont;
        return $"{resolved}, fonts:VelaShell#Cascadia Mono, JetBrains Mono, Consolas, monospace";
    }

    [TestMethod]
    public void UserFont_StaysPrimary_OverEmbeddedFallback()
    {
        var ff = FontFamily.Parse(BuildChain("Fira Code"));
        Assert.AreEqual("Fira Code", ff.FamilyNames[0],
            "用户字体必须是首选族名,内置 Cascadia 只作回退");
        Assert.Contains("Cascadia Mono", [.. ff.FamilyNames], "内置 Cascadia 仍须在链中作回退");
        Assert.Contains("JetBrains Mono", [.. ff.FamilyNames], "JetBrains Mono 保留为 Cascadia 之后的回退");
    }

    [TestMethod]
    public void UserPicksCascadiaMono_ResolvesToEmbedded()
    {
        var ff = FontFamily.Parse(BuildChain("Cascadia Mono"));
        Assert.AreEqual("Cascadia Mono", ff.FamilyNames[0]);
        Assert.Contains("fonts:VelaShell", ff.Key?.ToString() ?? "",
            "用户显式填 Cascadia Mono 时应命中随程序分发的内置字体,而非碰运气的系统同名字体");
    }
}

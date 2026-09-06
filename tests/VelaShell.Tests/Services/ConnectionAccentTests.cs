using Avalonia.Media;
using VelaShell.Core.Models;
using VelaShell.Services;

namespace VelaShell.Tests.Services;

/// <summary>
/// 连接配置的标签强调色:用户指定优先,没指定才按 id 自动配色。
/// </summary>
/// <remarks>
/// 自动配色保证的是「同一批机器颜色各不相同」,而运维要的恰恰相反 ——
/// 「所有生产机都是红的、所有测试机都是绿的」。两个目标没法用同一套规则同时满足,
/// 这组用例把优先级钉死。
/// </remarks>
[TestClass]
[TestCategory("Design")]
public sealed class ConnectionAccentTests
{
    [TestMethod]
    public void AnExplicitTabColourWins()
    {
        SessionProfile profile = new() { Terminal = new() { TabColor = "#E05252" } };

        IBrush brush = ConnectionAccent.BrushForProfile(profile);

        Assert.AreEqual(Color.Parse("#E05252"), ((ISolidColorBrush)brush).Color);
    }

    [TestMethod]
    public void TwoProfilesCanShareTheSameExplicitColour()
    {
        // 这正是「生产标红」要的:一批机器同色。自动配色下它们必然各不相同。
        SessionProfile a = new() { Terminal = new() { TabColor = "#E05252" } };
        SessionProfile b = new() { Terminal = new() { TabColor = "#E05252" } };

        Assert.AreEqual(
            ((ISolidColorBrush)ConnectionAccent.BrushForProfile(a)).Color,
            ((ISolidColorBrush)ConnectionAccent.BrushForProfile(b)).Color);
    }

    [TestMethod]
    public void WithoutAnExplicitColourItFallsBackToTheIdHash()
    {
        SessionProfile profile = new();

        Assert.AreEqual(
            ConnectionAccent.BrushFor(profile.Id).ToString(),
            ConnectionAccent.BrushForProfile(profile).ToString());
    }

    [TestMethod]
    public void AnUnparseableColourFallsBackInsteadOfBlankingTheTab()
    {
        // 配置文件是可以手改的,一个写错的颜色不该让标签变成透明。
        SessionProfile profile = new() { Terminal = new() { TabColor = "不是颜色" } };

        Assert.AreEqual(
            ConnectionAccent.BrushFor(profile.Id).ToString(),
            ConnectionAccent.BrushForProfile(profile).ToString());
    }

    [TestMethod]
    public void TheAutomaticColourIsStableAcrossCalls()
    {
        // 跨启动稳定是这套配色的基本承诺:同一配置在任何会话里都是同一号色。
        Guid id = Guid.NewGuid();

        Assert.AreEqual(ConnectionAccent.IndexFor(id), ConnectionAccent.IndexFor(id));
    }
}

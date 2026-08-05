using System.Text.RegularExpressions;
using VelaShell.Services;
using VelaShell.ViewModels;

namespace VelaShell.Tests.Services;

/// <summary>
/// 关于页的依赖版本取自编译期写入的程序集元数据(VelaShell.csproj 的 EmbedPackageVersions
/// 目标,数据源是 Directory.Packages.props)。
/// </summary>
/// <remarks>
/// 这组测试主要防的是「构建脚本静默失效」:MSBuild 目标不生效不会报错,只会让元数据凭空消失,
/// 关于页悄悄退化成没有版本号 —— 正是那种没人会注意到的坏法。所以断言对着真实构建产物跑。
/// </remarks>
[TestClass]
[TestCategory("PackageVersions")]
public class PackageVersionsTests
{
    /// <summary>构建目标确实把包版本写进了程序集(失效则此处为空)。</summary>
    [TestMethod]
    public void Read_FindsPackageVersions_EmbeddedAtBuildTime()
    {
        IReadOnlyDictionary<string, string> versions = PackageVersions.Read(typeof(PackageVersions).Assembly);

        Assert.IsGreaterThan(0, versions.Count, "构建目标 EmbedPackageVersions 应把包版本写进程序集元数据。");
    }

    /// <summary>关于页当前展示的两个包必须查得到,否则界面会退化成只有名称。</summary>
    [TestMethod]
    [DataRow("Avalonia")]
    [DataRow("Tmds.Ssh")]
    public void Of_ResolvesPackagesShownOnTheAboutPage(string packageId)
    {
        string? version = PackageVersions.Of(packageId);

        Assert.IsNotNull(version, $"关于页要显示 {packageId} 的版本,应能查到。");
        Assert.MatchesRegex(new Regex(@"^\d+\.\d+"), version, "版本应形如 12.1.0。");
    }

    /// <summary>SSH 库被隔离在 Infrastructure 层,按包名查得到才说明没走类型引用那条路。</summary>
    [TestMethod]
    public void Of_ResolvesPackages_NotReferencedByThisAssembly()
    {
        Assert.IsNotNull(PackageVersions.Of("Tmds.Ssh"),
                         "Tmds.Ssh 不被 VelaShell 直接引用,版本仍应可查(不必为取版本号破坏分层)。");
    }

    [TestMethod]
    public void Of_UnknownPackage_ReturnsNull()
    {
        Assert.IsNull(PackageVersions.Of("No.Such.Package"));
    }

    /// <summary>没有任何包版本元数据的程序集:返回空表而不是抛。</summary>
    [TestMethod]
    public void Read_AssemblyWithoutMetadata_ReturnsEmpty()
    {
        Assert.IsEmpty(PackageVersions.Read(typeof(object).Assembly));
    }

    /// <summary>
    /// 关于页真正显示出来的文本 —— 这才是用户看到的东西,也是原先写死、并且已经漂移过的地方
    /// (曾停留在 "Avalonia UI 12.0.5",而实际引用早已是 12.1.0)。
    /// </summary>
    [TestMethod]
    public void AboutFramework_ShowsTheReferencedAvaloniaVersion()
    {
        Assert.AreEqual($"Avalonia UI {PackageVersions.Of("Avalonia")}", SettingsViewModel.AboutFramework);
    }

    [TestMethod]
    public void AboutSshLibrary_ShowsTheReferencedSshLibraryVersion()
    {
        Assert.AreEqual($"Tmds.Ssh {PackageVersions.Of("Tmds.Ssh")}", SettingsViewModel.AboutSshLibrary);
    }

    /// <summary>关于页不该再出现写死的版本号 —— 这条盯的就是当初那个漂移。</summary>
    [TestMethod]
    public void AboutStrings_ContainNoHardcodedVersion()
    {
        foreach (string text in new[] { SettingsViewModel.AboutFramework, SettingsViewModel.AboutSshLibrary })
        {
            Assert.IsFalse(text.Contains("12.0.5", StringComparison.Ordinal), $"'{text}' 疑似残留写死的版本号。");
        }
    }
}

using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Models;

/// <summary>
/// 设置载入后的规整(<see cref="AppSettings.Normalize" />):把旧字段迁移到唯一权威字段。
/// </summary>
[TestClass]
[TestCategory("DataStore")]
public class AppSettingsNormalizeTests
{
    [TestMethod]
    public void VisualBell_IsMigratedToBellMode()
    {
        AppSettings settings = new();
        settings.TerminalBehavior.VisualBell = true;

        settings.Normalize();

        Assert.AreEqual("visual", settings.TerminalBehavior.BellMode);
        Assert.IsFalse(settings.TerminalBehavior.VisualBell);
    }

    /// <summary>
    /// 旧版把下载目录的默认值硬写成 "~/Downloads",Windows 上把"下载"文件夹改到别处的用户
    /// 因此永远拿不到自己指定的位置(#257)。存量配置里的这个字面默认值要迁成空串 = 跟随系统。
    /// </summary>
    [TestMethod]
    [DataRow("~/Downloads")]
    [DataRow("~\\Downloads")]
    [DataRow("  ~/Downloads  ")]
    public void LegacyDefaultDownloadDirectory_IsMigratedToFollowSystem(string legacy)
    {
        AppSettings settings = new();
        settings.Transfer.LocalDownloadDirectory = legacy;

        settings.Normalize();

        Assert.AreEqual(string.Empty, settings.Transfer.LocalDownloadDirectory);
        Assert.AreEqual(UserPathResolver.Downloads, UserPathResolver.ResolveOrDownloads(settings.Transfer.LocalDownloadDirectory));
    }

    [TestMethod]
    public void ExplicitDownloadDirectory_IsKept()
    {
        string chosen = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "vela-dl"));
        AppSettings settings = new();
        settings.Transfer.LocalDownloadDirectory = chosen;

        settings.Normalize();

        Assert.AreEqual(chosen, settings.Transfer.LocalDownloadDirectory);
    }

    [TestMethod]
    public void DefaultDownloadDirectory_IsEmpty_MeaningFollowSystem() =>
        Assert.AreEqual(string.Empty, new AppSettings().Transfer.LocalDownloadDirectory);
}

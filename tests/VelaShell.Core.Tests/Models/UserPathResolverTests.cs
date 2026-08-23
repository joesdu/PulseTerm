using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Models;

/// <summary>
/// 设置里目录取值的解析规则(<see cref="UserPathResolver" />)。核心不变式:
/// <b>相对路径以用户主目录为基准,绝不落到进程工作目录上</b> —— 工作目录由外部环境决定
/// (开机自启时曾是 C:\Windows\System32,归一后是只读的安装目录),两者都不是用户想要的落点(#120)。
/// </summary>
[TestClass]
[TestCategory("DataStore")]
public class UserPathResolverTests
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [TestMethod]
    public void Empty_ReturnsFallback() =>
        Assert.AreEqual("fallback", UserPathResolver.Resolve("   ", "fallback"));

    [TestMethod]
    public void Null_ReturnsFallback() =>
        Assert.AreEqual("fallback", UserPathResolver.Resolve(null, "fallback"));

    [TestMethod]
    public void Tilde_ReturnsHome() => Assert.AreEqual(Home, UserPathResolver.Resolve("~", "fallback"));

    [TestMethod]
    public void TildeSlash_ExpandsUnderHome() =>
        Assert.AreEqual(
            Path.GetFullPath(Path.Combine(Home, "Downloads")),
            UserPathResolver.Resolve("~/Downloads", "fallback")
        );

    [TestMethod]
    public void TildeBackslash_ExpandsUnderHome() =>
        Assert.AreEqual(
            Path.GetFullPath(Path.Combine(Home, "Downloads")),
            UserPathResolver.Resolve("~\\Downloads", "fallback")
        );

    [TestMethod]
    public void RelativePath_IsBasedOnHome_NotCurrentDirectory()
    {
        // 这条是本类的存在理由:历史实现把相对路径交给 Path.GetFullPath(单参),
        // 于是按进程工作目录解析 —— 开机自启时那就是 C:\Windows\System32。
        string resolved = UserPathResolver.Resolve("downloads", "fallback");

        Assert.AreEqual(Path.GetFullPath(Path.Combine(Home, "downloads")), resolved);
        Assert.AreNotEqual(Path.GetFullPath("downloads"), resolved);
    }

    [TestMethod]
    public void NestedRelativePath_IsBasedOnHome() =>
        Assert.AreEqual(
            Path.GetFullPath(Path.Combine(Home, "a", "b")),
            UserPathResolver.Resolve(Path.Combine("a", "b"), "fallback")
        );

    [TestMethod]
    public void AbsolutePath_IsPreserved()
    {
        string absolute = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "vela-abs"));
        Assert.AreEqual(absolute, UserPathResolver.Resolve(absolute, "fallback"));
    }

    [TestMethod]
    public void SurroundingWhitespace_IsTrimmed() =>
        Assert.AreEqual(Home, UserPathResolver.Resolve("  ~  ", "fallback"));

    [TestMethod]
    public void DoubleTilde_IsTreatedAsRelative_NotTrimmedAway() =>
        // 历史实现用 TrimStart('~','/','\\') 会把 "~~/a" 的前缀整段吃掉,得到 Home/a;
        // 现在 "~~" 不是主目录记号,应按普通相对路径处理。
        Assert.AreEqual(
            Path.GetFullPath(Path.Combine(Home, "~~", "a")),
            UserPathResolver.Resolve("~~/a", "fallback")
        );

    [TestMethod]
    public void ResolveOrHome_FallsBackToHome() =>
        Assert.AreEqual(Home, UserPathResolver.ResolveOrHome(null));

    /// <summary>
    /// 下载目录留空时跟随系统"下载"文件夹,而不是硬拼 <c>~/Downloads</c> —— 该文件夹在 Windows 上
    /// 可被用户改到任意位置(#257)。真实位置因机器而异,这里只断言"是规范化过的绝对路径"。
    /// </summary>
    [TestMethod]
    public void Downloads_IsRootedAbsolutePath()
    {
        string downloads = UserPathResolver.Downloads;

        Assert.IsFalse(string.IsNullOrWhiteSpace(downloads));
        Assert.IsTrue(Path.IsPathRooted(downloads), downloads);
        Assert.AreEqual(Path.GetFullPath(downloads), downloads);
    }

    [TestMethod]
    public void ResolveOrDownloads_EmptyValue_FallsBackToSystemDownloads()
    {
        Assert.AreEqual(UserPathResolver.Downloads, UserPathResolver.ResolveOrDownloads(null));
        Assert.AreEqual(UserPathResolver.Downloads, UserPathResolver.ResolveOrDownloads("   "));
    }

    [TestMethod]
    public void ResolveOrDownloads_ConfiguredValue_Wins() =>
        Assert.AreEqual(
            Path.GetFullPath(Path.Combine(Home, "somewhere")),
            UserPathResolver.ResolveOrDownloads("~/somewhere")
        );
}

using VelaShell.Services;

namespace VelaShell.Tests.Services;

/// <summary>
/// Unix 风格远程路径的拼接与取父目录。
/// </summary>
/// <remarks>
/// 远程路径**永远是 <c>/</c> 分隔**,与本机是不是 Windows 无关。用 <c>Path.Combine</c>
/// 处理它,在 Windows 上会拼出 <c>/var\log</c> 这种对端不认识的东西 ——
/// 而那种 bug 在 Linux 开发机上永远复现不出来。
/// </remarks>
[TestClass]
[TestCategory("FileBrowser")]
public sealed class RemotePathTests
{
    [TestMethod]
    public void CombiningUnderRootDoesNotDoubleTheSlash() =>
        Assert.AreEqual("/etc", RemotePath.Combine("/", "etc"));

    [TestMethod]
    public void CombiningUnderADirectoryAddsOneSlash() =>
        Assert.AreEqual("/var/log", RemotePath.Combine("/var", "log"));

    [TestMethod]
    public void ATrailingSlashOnTheDirectoryIsAbsorbed() =>
        Assert.AreEqual("/var/log", RemotePath.Combine("/var/", "log"));

    [TestMethod]
    public void CombineAlwaysUsesForwardSlashes()
    {
        // 这是 Path.Combine 在 Windows 上会做错的那一条:它会插一个反斜杠。
        Assert.DoesNotContain(@"\", RemotePath.Combine("/var", "log"));
    }

    [TestMethod]
    public void TheParentOfANestedPathDropsOneLevel() =>
        Assert.AreEqual("/var/log", RemotePath.Parent("/var/log/nginx"));

    [TestMethod]
    public void ATrailingSlashDoesNotCostALevel() =>
        // "/var/log/" 的父目录是 "/var",不是 "/var/log" —— 少了这一步,
        // 在带尾斜杠的路径上按"上一级"会原地不动。
        Assert.AreEqual("/var", RemotePath.Parent("/var/log/"));

    [TestMethod]
    public void TheParentOfATopLevelDirectoryIsRoot() =>
        Assert.AreEqual("/", RemotePath.Parent("/var"));

    /// <summary>根目录的父目录仍是根。</summary>
    /// <remarks>
    /// 返回自身而不是 null:调用点是"往上一级"的导航,在根上按它应当原地不动,
    /// 而不是要求每个调用点各判一次空。
    /// </remarks>
    [TestMethod]
    public void TheParentOfRootIsRoot()
    {
        Assert.AreEqual("/", RemotePath.Parent("/"));
        Assert.AreEqual("/", RemotePath.Parent(""));
    }
}

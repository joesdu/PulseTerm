using VelaShell.Core.Models;
using VelaShell.Services;
using VelaShell.ViewModels;

namespace VelaShell.Tests.Services;

/// <summary>
/// 远程文件列表的排序、隐藏项过滤与表头点击状态机。
/// </summary>
/// <remarks>
/// 这几件都是纯函数,却原先夹在一个三千行、要真实 SFTP 会话才构造得起来的视图模型里 ——
/// 于是"目录永远排在最前"这条最容易被下一次改动破坏的规则,一直没有用例守着。
/// </remarks>
[TestClass]
[TestCategory("FileBrowser")]
public sealed class RemoteFileSortTests
{
    private static RemoteFileInfoViewModel Entry(
        string name,
        bool directory = false,
        long size = 0,
        string owner = "root",
        DateTime? modified = null) =>
        new(new RemoteFileInfo
        {
            Name = name,
            FullPath = "/" + name,
            IsDirectory = directory,
            Size = size,
            Owner = owner,
            Group = "root",
            Permissions = directory ? "drwxr-xr-x" : "-rw-r--r--",
            LastModified = modified ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });

    private static string[] Names(IEnumerable<RemoteFileInfoViewModel> items) =>
        [.. items.Select(i => i.Name)];

    /// <summary>目录始终在最前,与排序列和方向都无关。</summary>
    /// <remarks>
    /// 目录的大小无意义(多数 SFTP 服务器报 4096 或 0),混进大小排序会让它们归到
    /// 一个看起来随机的位置 —— 列表就此读不懂了。
    /// </remarks>
    [TestMethod]
    public void DirectoriesComeFirstWhateverTheSort()
    {
        RemoteFileInfoViewModel[] items =
        [
            Entry("zzz.log", size: 9_000_000),
            Entry("bin", directory: true),
            Entry("aaa.log", size: 1),
        ];

        foreach (string column in (string[])["name", "size", "modified", "owner"])
        {
            foreach (bool descending in (bool[])[false, true])
            {
                string first = Names(RemoteFileSort.Sort(items, column, descending))[0];
                Assert.AreEqual("bin", first, $"按 {column}(降序={descending})排序时目录跑到了后面。");
            }
        }
    }

    [TestMethod]
    public void SizeSortsNumericallyNotAsText()
    {
        // 按文本排的话 "9" 会排在 "10" 前面 —— 这正是大小列最容易出的错。
        RemoteFileInfoViewModel[] items = [Entry("a", size: 10), Entry("b", size: 9)];

        Assert.AreSequenceEqual(new[] { "b", "a" }, Names(RemoteFileSort.Sort(items, "size", descending: false)));
        Assert.AreSequenceEqual(new[] { "a", "b" }, Names(RemoteFileSort.Sort(items, "size", descending: true)));
    }

    [TestMethod]
    public void AnUnknownColumnFallsBackToName()
    {
        // 列名来自视图绑定,重构时打错一个字不该让列表变成随机顺序。
        RemoteFileInfoViewModel[] items = [Entry("b"), Entry("a")];

        Assert.AreSequenceEqual(new[] { "a", "b" }, Names(RemoteFileSort.Sort(items, "打错的列名", descending: false)));
    }

    [TestMethod]
    public void NameSortIgnoresCase()
    {
        // 区分大小写会把 Z 排在 a 前面(序数比较),而用户看到的是"乱序"。
        RemoteFileInfoViewModel[] items = [Entry("Zebra"), Entry("apple")];

        Assert.AreSequenceEqual(new[] { "apple", "Zebra" }, Names(RemoteFileSort.Sort(items, "name", descending: false)));
    }

    [TestMethod]
    public void HiddenEntriesAreFilteredUnlessAsked()
    {
        RemoteFileInfoViewModel[] items = [Entry(".bashrc"), Entry("app.log")];

        Assert.AreSequenceEqual(new[] { "app.log" }, Names(RemoteFileSort.ApplyHiddenFilter(items, showHidden: false)));
        Assert.HasCount(2, RemoteFileSort.ApplyHiddenFilter(items, showHidden: true));
    }

    /// <summary>点同一列反向,换一列则从升序开始。</summary>
    /// <remarks>
    /// 换列时沿用上一列的方向,会让人以为自己点错了地方 —— 明明刚点的是"名称",
    /// 列表却按降序出来。
    /// </remarks>
    [TestMethod]
    public void ClickingTheSameColumnFlipsAndANewColumnStartsAscending()
    {
        Assert.AreEqual(("name", true), RemoteFileSort.NextSortState("name", "name", currentDescending: false));
        Assert.AreEqual(("name", false), RemoteFileSort.NextSortState("name", "name", currentDescending: true));
        Assert.AreEqual(("size", false), RemoteFileSort.NextSortState("size", "name", currentDescending: true));
    }
}

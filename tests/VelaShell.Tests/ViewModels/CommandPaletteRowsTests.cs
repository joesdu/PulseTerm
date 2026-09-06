using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 命令面板结果摊平后的行序。
/// </summary>
/// <remarks>
/// 结果原先是"分组的 <c>ItemsControl</c> 里再套一层条目的 <c>ItemsControl</c>",
/// 嵌在 <c>ScrollViewer</c> 里**一条也虚拟化不了** —— 保存了几百台机器的用户,
/// 每敲一个字符都要把全部结果的控件树重建一遍。摊平成单列表 + 表头行之后才能用上
/// <c>ListBox</c> 的 <c>VirtualizingStackPanel</c>。
/// <para>
/// 这里钉住的关键不变量是:<c>Rows</c> 里条目的先后必须与驱动上下键的顺序完全一致,
/// 否则方向键会在列表里"跳着走"。
/// </para>
/// </remarks>
[TestClass]
[TestCategory("CommandPalette")]
public sealed class CommandPaletteRowsTests
{
    private static CommandPaletteViewModel Build(params (string Category, string Title)[] items)
    {
        List<CommandPaletteItem> built = [.. items.Select(i => new CommandPaletteItem(i.Category, i.Title, () => { }))];
        var vm = new CommandPaletteViewModel(() => built);
        vm.Open();
        return vm;
    }

    [TestMethod]
    public void EachGroupContributesAHeaderRowFollowedByItsItems()
    {
        CommandPaletteViewModel vm = Build(
            ("会话", "web-prod"),
            ("会话", "db-prod"),
            ("命令", "打开设置"));

        Assert.HasCount(5, vm.Rows, "两个分组 = 2 个表头 + 3 个条目。");
        Assert.IsInstanceOfType<CommandPaletteHeader>(vm.Rows[0]);
        Assert.AreEqual("会话", ((CommandPaletteHeader)vm.Rows[0]).Category);
        Assert.IsInstanceOfType<CommandPaletteItem>(vm.Rows[1]);
        Assert.IsInstanceOfType<CommandPaletteItem>(vm.Rows[2]);
        Assert.IsInstanceOfType<CommandPaletteHeader>(vm.Rows[3]);
        Assert.AreEqual("命令", ((CommandPaletteHeader)vm.Rows[3]).Category);
        Assert.IsInstanceOfType<CommandPaletteItem>(vm.Rows[4]);
    }

    [TestMethod]
    public void ItemRowOrderMatchesKeyboardNavigationOrder()
    {
        // 这是本项的核心不变量:看到的顺序 = 上下键走的顺序。
        CommandPaletteViewModel vm = Build(
            ("会话", "alpha"),
            ("命令", "beta"),
            ("会话", "gamma"));

        List<string> rowTitles = [.. vm.Rows.OfType<CommandPaletteItem>().Select(i => i.Title)];
        List<string> navigationTitles = [];
        for (int i = 0; i < rowTitles.Count; i++)
        {
            navigationTitles.Add(vm.SelectedItem!.Title);
            vm.MoveDown();
        }

        CollectionAssert.AreEqual(rowTitles, navigationTitles,
            "方向键走过的顺序与列表里看到的顺序对不上 —— 按下箭头会在列表里跳着走。");
    }

    [TestMethod]
    public void RowsAreRebuiltOnEveryQueryChange()
    {
        CommandPaletteViewModel vm = Build(
            ("会话", "web-prod"),
            ("会话", "db-prod"));

        vm.Query = "web";

        Assert.HasCount(2, vm.Rows, "过滤后只剩一个分组 + 一个条目。");
        Assert.AreEqual("web-prod", vm.Rows.OfType<CommandPaletteItem>().Single().Title);
    }

    [TestMethod]
    public void NoResultsMeansNoRowsAtAll()
    {
        // 一个孤零零的分组表头挂在空列表上比什么都不显示更糟。
        CommandPaletteViewModel vm = Build(("会话", "web-prod"));

        vm.Query = "zzz-nothing";

        Assert.IsEmpty(vm.Rows);
        Assert.IsFalse(vm.HasResults);
    }

    [TestMethod]
    public void RowsAndGroupsStayConsistent()
    {
        // Groups 仍保留(测试与外部读取用),但它必须和 Rows 描述同一件事。
        CommandPaletteViewModel vm = Build(
            ("会话", "a"),
            ("命令", "b"),
            ("命令", "c"));

        Assert.HasCount(vm.Groups.Count, vm.Rows.OfType<CommandPaletteHeader>().ToList());
        Assert.HasCount(vm.Groups.Sum(g => g.Items.Count), vm.Rows.OfType<CommandPaletteItem>().ToList());
    }
}

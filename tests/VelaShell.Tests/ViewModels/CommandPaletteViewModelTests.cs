using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

[TestClass]
public class CommandPaletteViewModelTests
{
    private static CommandPaletteViewModel CreateVm(out int[] runCount, params (string cat, string title)[] items)
    {
        int[] counts = new int[items.Length];
        runCount = counts;
        var built = new List<CommandPaletteItem>();
        for (int i = 0; i < items.Length; i++)
        {
            int index = i;
            built.Add(new(items[i].cat, items[i].title, () => counts[index]++));
        }
        return new(() => built);
    }

    [TestMethod]
    [TestCategory("CommandPalette")]
    public void Open_LoadsItemsGroupedByCategory_AndSelectsFirst()
    {
        CommandPaletteViewModel vm = CreateVm(out _, ("会话", "web-01"), ("会话", "db-01"), ("命令", "打开设置"));
        vm.Open();
        Assert.IsTrue(vm.IsOpen);
        Assert.HasCount(2, vm.Groups);
        Assert.AreEqual("会话", vm.Groups[0].Category);
        Assert.HasCount(2, vm.Groups[0].Items);
        Assert.AreEqual(3, vm.ResultCount);
        Assert.AreEqual("web-01", vm.SelectedItem!.Title);
        Assert.IsTrue(vm.SelectedItem.IsSelected);
    }

    [TestMethod]
    [TestCategory("CommandPalette")]
    public void Query_FiltersItems_CaseInsensitiveAndFuzzy()
    {
        CommandPaletteViewModel vm = CreateVm(out _, ("命令", "打开设置"), ("命令", "新建 SSH 连接"), ("会话", "web-prod-01"));
        vm.Open();
        vm.Query = "web";
        Assert.AreEqual(1, vm.ResultCount);
        Assert.AreEqual("web-prod-01", vm.SelectedItem!.Title);

        // Fuzzy subsequence: "ssh" matches "新建 SSH 连接".
        vm.Query = "ssh";
        Assert.AreEqual(1, vm.ResultCount);
        Assert.AreEqual("新建 SSH 连接", vm.SelectedItem!.Title);
    }

    [TestMethod]
    [TestCategory("CommandPalette")]
    public void MoveDownAndUp_WrapsSelection()
    {
        CommandPaletteViewModel vm = CreateVm(out _, ("命令", "a"), ("命令", "b"), ("命令", "c"));
        vm.Open();
        Assert.AreEqual("a", vm.SelectedItem!.Title);
        vm.MoveDown();
        Assert.AreEqual("b", vm.SelectedItem!.Title);
        vm.MoveUp();
        vm.MoveUp();
        Assert.AreEqual("c", vm.SelectedItem!.Title); // wrapped past the top
    }

    [TestMethod]
    [TestCategory("CommandPalette")]
    public void ExecuteSelected_InvokesActionAndCloses()
    {
        CommandPaletteViewModel vm = CreateVm(out int[] runs, ("命令", "a"), ("命令", "b"));
        vm.Open();
        vm.MoveDown(); // select "b"
        vm.ExecuteSelected();
        Assert.AreEqual(1, runs[1]);
        Assert.AreEqual(0, runs[0]);
        Assert.IsFalse(vm.IsOpen);
    }

    [TestMethod]
    [TestCategory("CommandPalette")]
    public void Activate_SelectsThenRunsItem()
    {
        CommandPaletteViewModel vm = CreateVm(out int[] runs, ("命令", "a"), ("命令", "b"));
        vm.Open();
        CommandPaletteItem target = vm.Groups[0].Items[1];
        vm.Activate(target);
        Assert.AreEqual(1, runs[1]);
        Assert.IsFalse(vm.IsOpen);
    }

    [TestMethod]
    [TestCategory("CommandPalette")]
    public void Open_RefreshesFromProvider()
    {
        var source = new List<CommandPaletteItem>
        {
            new("命令", "one", () => { })
        };
        var vm = new CommandPaletteViewModel(() => source);
        vm.Open();
        Assert.AreEqual(1, vm.ResultCount);
        source.Add(new("命令", "two", () => { }));
        vm.Open();
        Assert.AreEqual(2, vm.ResultCount);
    }
}

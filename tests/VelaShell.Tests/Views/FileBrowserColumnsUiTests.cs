using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// Headless UI 端到端:用 Avalonia headless 平台加载真实的 <see cref="FileBrowserView" />,
/// 布局后直接量表头各列的实际宽度。这一层覆盖单元测试够不到的东西 —— 列宽/最小宽度/
/// 拖拽条三处绑定是否真的接上、以及关列后 Grid 是否真的塌缩到 0(而不是留一条空带)。
/// </summary>
[TestClass]
[TestCategory("FileBrowserUi")]
public class FileBrowserColumnsUiTests
{
    /// <summary>表头 Grid 里各列的列索引(与 FileBrowserView.axaml 的列定义顺序一致)。</summary>
    private const int NameColumn = 0;
    private const int SizeColumn = 2;
    private const int OwnerColumn = 6;
    private const int OwnerSplitterColumn = 7;
    private const int GroupColumn = 8;
    private const int TypeColumn = 10;

    private static HeadlessUnitTestSession _session = null!;

    // 共用全程序集的宿主(见 VelaHeadlessApp):不能各起各的 App,否则谁先跑谁的样式说了算。
    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(FileBrowserColumnsUiTests).Assembly);

    /// <summary>本次用例开出来的窗口;必须关掉,理由见 <see cref="CloseShownWindow" />。</summary>
    private static Window? _shownWindow;

    /// <summary>
    /// 关掉本用例开出来的窗口。**不关会拖垮整个测试运行**:全程序集共用一条 headless UI 线程,
    /// 残留的窗口会持续往 dispatcher 排布局/渲染工作项,而后续测试里的
    /// <c>Dispatcher.UIThread.RunJobs()</c> 要等队列排空才返回 —— 队列被残留窗口一直填着,
    /// 它就永远不返回,UI 线程被占死,再往后每个测试的 Dispatch 都无限期排队。
    /// (实测:本类 4 个用例各漏一个窗口,紧随其后的 MenuGlyphStyleTests 必卡死;单独跑则秒过。)
    /// </summary>
    [TestCleanup]
    public void CloseShownWindow() =>
        OnUi(() =>
        {
            _shownWindow?.Close();
            _shownWindow = null;
        });

    [TestMethod]
    public void AllColumnsVisible_HeaderLaysOutEveryColumnAtItsWidth()
    {
        OnUi(() =>
        {
            (FileBrowserViewModel vm, Grid header) = ShowBrowser();

            Assert.AreEqual(vm.NameColumnWidth.Value, WidthOf(header, NameColumn));
            Assert.AreEqual(vm.SizeColumnWidth.Value, WidthOf(header, SizeColumn));
            Assert.AreEqual(vm.OwnerColumnWidth.Value, WidthOf(header, OwnerColumn));
            Assert.AreEqual(vm.GroupColumnWidth.Value, WidthOf(header, GroupColumn));
            Assert.AreEqual(vm.TypeColumnWidth.Value, WidthOf(header, TypeColumn));

            // 三列都实际占了宽度,才谈得上“显示了所有者/分组/类型”。
            Assert.IsGreaterThan(0, WidthOf(header, OwnerColumn));
        });
    }

    /// <summary>关列必须连宽度、最小宽度、拖拽条一起塌缩;漏掉最小宽度就会留下一条空带。</summary>
    [TestMethod]
    public void HidingColumn_CollapsesItAndItsSplitterToZeroWidth()
    {
        OnUi(() =>
        {
            (FileBrowserViewModel vm, Grid header) = ShowBrowser();

            vm.ShowOwnerColumn = false;
            Relayout(header);

            Assert.AreEqual(0, WidthOf(header, OwnerColumn));
            Assert.AreEqual(0, WidthOf(header, OwnerSplitterColumn));

            // 邻列不受影响。
            Assert.AreEqual(vm.GroupColumnWidth.Value, WidthOf(header, GroupColumn));
        });
    }

    [TestMethod]
    public void ReshowingColumn_RestoresItsWidth()
    {
        OnUi(() =>
        {
            (FileBrowserViewModel vm, Grid header) = ShowBrowser();

            vm.ShowOwnerColumn = false;
            Relayout(header);
            vm.ShowOwnerColumn = true;
            Relayout(header);

            Assert.AreEqual(vm.OwnerColumnWidth.Value, WidthOf(header, OwnerColumn));
            Assert.IsGreaterThan(0, WidthOf(header, OwnerSplitterColumn));
        });
    }

    /// <summary>行与表头共用同一份列宽属性,列数与列宽必须对齐,否则单元格会串列。</summary>
    [TestMethod]
    public void FileRow_UsesTheSameColumnGeometryAsHeader()
    {
        OnUi(() =>
        {
            (FileBrowserViewModel vm, Grid header) = ShowBrowser();
            Grid row = FirstRowGrid(header);

            Assert.AreEqual(header.ColumnDefinitions.Count, row.ColumnDefinitions.Count);
            Assert.AreEqual(WidthOf(header, OwnerColumn), WidthOf(row, OwnerColumn));

            vm.ShowOwnerColumn = false;
            Relayout(header);

            Assert.AreEqual(0, WidthOf(row, OwnerColumn));
        });
    }

    private static double WidthOf(Grid grid, int columnIndex) => grid.ColumnDefinitions[columnIndex].ActualWidth;

    /// <summary>把面板挂进 headless 窗口并完成一次真实布局,返回视图模型与表头 Grid。</summary>
    private static (FileBrowserViewModel Vm, Grid Header) ShowBrowser()
    {
        ISftpService sftp = Substitute.For<ISftpService>();

        // 面板默认是收起的(IsVisible=false),不展开就整个不参与布局,量到的全是 0。
        var vm = new FileBrowserViewModel(sftp, Guid.NewGuid()) { IsVisible = true };
        vm.Files.Add(new(new RemoteFileInfo
        {
            Name = "app.log",
            FullPath = "/srv/app.log",
            Size = 2048,
            Permissions = "-rw-r--r--",
            IsDirectory = false,
            LastModified = DateTime.UtcNow,
            Owner = "deploy",
            Group = "www-data"
        }));
        var view = new FileBrowserView { DataContext = vm };
        var window = new Window { Width = 1200, Height = 400, Content = view };
        _shownWindow = window;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (vm, HeaderGrid(view));
    }

    private static void Relayout(Grid header)
    {
        Dispatcher.UIThread.RunJobs();
        header.UpdateLayout();
        TopLevel.GetTopLevel(header)?.UpdateLayout();
    }

    /// <summary>表头 Grid = 唯一那个直接挂着列显示右键菜单的 Grid。</summary>
    private static Grid HeaderGrid(FileBrowserView view) =>
        view.GetVisualDescendants().OfType<Grid>().First(g => g.ContextMenu is not null);

    private static Grid FirstRowGrid(Grid header) =>
        TopLevel.GetTopLevel(header)!.GetVisualDescendants()
                .OfType<ListBox>().First(l => l.Name == "FileList")
                .GetVisualDescendants().OfType<Grid>()
                .First(g => g.ColumnDefinitions.Count == header.ColumnDefinitions.Count);

    private static void OnUi(Action body) =>
        _session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();
}

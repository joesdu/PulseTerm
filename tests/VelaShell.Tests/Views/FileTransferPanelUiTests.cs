using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Localization;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 传输浮窗的布局回归:2026-07-29 的截图里,面板顶到 280px 上限、却只画出 3 行,
/// 下面空出一大片 —— 罪魁是这个列表上的 VirtualizingStackPanel,它算出的实现窗口
/// 跟真实视口对不上(ScrollViewer 套在外面、还是按 ListBox 那样放进 ItemsControl
/// 模板里,都照样翻车)。面板本就只露约 5 行、列表上限 100 条,虚拟化省不下什么,
/// 已经撤掉。
/// <para>
/// 注意:headless 的布局会一直迭代到收敛,把那个瞬时的实现窗口错误自己抹平了 ——
/// 这几条断言在出问题的版本上同样是绿的,复现只在真实渲染下成立。所以它们守的不是
/// "复现过的那一帧",而是三条只要行没被完整画出来就必然破掉的不变量。
/// </para>
/// </summary>
[TestClass]
[TestCategory("FileTransferUI")]
public sealed class FileTransferPanelUiTests
{
    /// <summary>XAML 里给传输列表设的高度上限。</summary>
    private const double ListMaxHeight = 280;

    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(FileTransferPanelUiTests).Assembly);
        LocalizedStrings.Instance.Attach(new LocalizationService());
    }

    [TestMethod]
    public void OverflowingList_TilesTheWholeViewport_WithNoBlankTail()
    {
        OnUi(() =>
        {
            // 43 条 —— 由截图里滚动条滑块与轨道的比例反推出来的真实规模
            //(历史恢复的旧记录 + 当前这一批),远超 280px 上限。
            using var fixture = Fixture.Show(43);

            ScrollViewer viewport = fixture.Viewport;
            Assert.AreEqual(
                ListMaxHeight,
                viewport.Bounds.Height,
                0.5,
                "6 条应当撑满并顶到列表高度上限。"
            );

            // 每一条都必须实现出来。虚拟化在这个面板上算错过实现窗口(只画头几行、
            // 下面空一片),而 100 条的上限已经让"全量实现"足够便宜 —— 这条断言就是
            // 防止有人再把 VirtualizingStackPanel 装回来。
            List<Control> realized = fixture.RealizedRows();
            Assert.HasCount(43, realized, "有行没被实现出来 —— 列表又被虚拟化了?");

            // 最后一行的下边必须够到视口底部;够不到的那段就是截图里的空白。
            double bottom = realized
                .Select(row => row.TranslatePoint(new(0, row.Bounds.Height), viewport)?.Y ?? double.NaN)
                .Max();
            Assert.IsGreaterThanOrEqualTo(
                viewport.Bounds.Height,
                bottom,
                $"列表底部空出了 {viewport.Bounds.Height - bottom:F1}px:视口高 {viewport.Bounds.Height:F1},"
                + $"最后一行只画到 {bottom:F1}(共实现 {realized.Count} 行 / 全部 {fixture.ViewModel.Transfers.Count} 条)。"
            );
        });
    }

    [TestMethod]
    public void ShortList_ShrinksToItsRows_InsteadOfStretchingToTheCap()
    {
        OnUi(() =>
        {
            using var fixture = Fixture.Show(2);

            ScrollViewer viewport = fixture.Viewport;
            double rows = fixture.RealizedRows().Sum(row => row.Bounds.Height);

            Assert.AreEqual(2, fixture.RealizedRows().Count, "两条都应当实现出来。");
            Assert.AreEqual(
                rows,
                viewport.Bounds.Height,
                0.5,
                $"只有 2 条时列表被撑到了 {viewport.Bounds.Height:F1}px,行高合计才 {rows:F1}px。"
            );
            Assert.IsLessThan(ListMaxHeight, viewport.Bounds.Height);
        });
    }

    /// <summary>建窗口、灌 N 条进行中的传输、渲染一帧。</summary>
    private sealed class Fixture : IDisposable
    {
        private Fixture(Window window, FileTransferView view, FileTransferViewModel viewModel)
        {
            Window = window;
            View = view;
            ViewModel = viewModel;
        }

        public Window Window { get; }

        public FileTransferView View { get; }

        public FileTransferViewModel ViewModel { get; }

        /// <summary>
        /// 传输行的滚动视口(视图里只有这一个)。刻意不去认 ItemsControl 的尺寸:
        /// 修复前后 280px 上限落在不同的元素上,而"用户看到多高一块区域"始终是视口说了算。
        /// </summary>
        public ScrollViewer Viewport => View.GetVisualDescendants().OfType<ScrollViewer>().Single();

        /// <summary>承载传输行的列表(视图里只有这一个)。</summary>
        public ItemsControl List => View.GetVisualDescendants().OfType<ItemsControl>().Single();

        public static Fixture Show(int transferCount)
        {
            var manager = Substitute.For<ITransferManager>();
            manager.ActiveTransfers.Returns([]);
            manager.QueuedTransfers.Returns([]);

            // 复刻真实时序:历史在构造期就已恢复满,而面板此刻还是收起的;
            // 直到某次传输开始才淡入 —— 行的实现发生在面板已经有尺寸之后。
            var viewModel = new FileTransferViewModel(manager);
            for (int i = 0; i < transferCount; i++)
            {
                viewModel.AddTransfer(new()
                {
                    Id = Guid.NewGuid(),
                    Type = TransferType.Download,
                    RemotePath = $"/var/log/server.log.{i}",
                    LocalPath = $"/tmp/server.log.{i}",
                    Status = TransferStatus.InProgress
                });
            }

            // 主窗口把面板钉在右上角、按内容定高 —— 不复刻这两条,面板会被拉满整窗,
            // 空白就成了测试夹具自己造出来的,而不是被测的那一个。
            var view = new FileTransferView
            {
                DataContext = viewModel,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top
            };
            var window = new Window { Width = 640, Height = 640, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            viewModel.IsPanelVisible = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            return new(window, view, viewModel);
        }

        /// <summary>当前已实现出来的行容器(虚拟化下只有视口附近的那些)。</summary>
        public List<Control> RealizedRows() =>
        [
            .. List.ItemsPanelRoot?.Children.Where(child => child.IsVisible) ?? []
        ];

        public void Dispose() => Window.Close();
    }

    private static void OnUi(Action action) => _session.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();
}

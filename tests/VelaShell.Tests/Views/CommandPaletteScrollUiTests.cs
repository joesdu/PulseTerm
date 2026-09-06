using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 命令面板的方向键导航要带着列表一起滚。
/// </summary>
/// <remarks>
/// 结果列表现在是**虚拟化**的单个 ListBox(摊平之后才虚拟化得了)。而"把选中项滚进可视区"
/// 那段代码还停在改造之前:它去可视树里找 <c>Classes="pal-item"</c> 的 Border ——
/// 屏幕外的条目在虚拟化之下<b>根本没有实例化</b>,于是永远找不到,滚动一次都不会发生。
/// 表现就是按住方向键,选中态一路往下走出可视区,列表却纹丝不动。
/// </remarks>
[TestClass]
[TestCategory("PaletteUi")]
public sealed class CommandPaletteScrollUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(CommandPaletteScrollUiTests).Assembly);

    /// <summary>一路按方向键往下,列表要跟着滚。</summary>
    [TestMethod]
    public void ArrowingDownPastTheViewport_ScrollsTheList()
    {
        OnUi(() =>
        {
            (Window window, CommandPaletteView view, CommandPaletteViewModel vm) = Show(60);
            ScrollViewer scroll = ScrollOf(view);

            Assert.AreEqual(0, scroll.Offset.Y, "前置:一开始应停在顶部。");

            for (int i = 0; i < 40; i++)
            {
                PressDown(view);
            }
            Relayout(window);

            Assert.IsGreaterThan(
                0,
                scroll.Offset.Y,
                "按了 40 次方向键,列表一点都没滚 —— 选中项早就跑出可视区了。");
            window.Close();
        });
    }

    /// <summary>往下走远之后再往上走回去,列表也要跟着滚回来。</summary>
    [TestMethod]
    public void ArrowingBackUp_ScrollsTheListBack()
    {
        OnUi(() =>
        {
            (Window window, CommandPaletteView view, CommandPaletteViewModel vm) = Show(60);
            ScrollViewer scroll = ScrollOf(view);

            for (int i = 0; i < 40; i++)
            {
                PressDown(view);
            }
            Relayout(window);
            double bottom = scroll.Offset.Y;
            Assert.IsGreaterThan(0, bottom, "前置:得先滚下去。");

            for (int i = 0; i < 40; i++)
            {
                PressUp(view);
            }
            Relayout(window);

            Assert.IsLessThan(bottom, scroll.Offset.Y, "往回走时列表没有跟着滚回来。");
            window.Close();
        });
    }

    /// <summary>选中项始终留在可视区内(这才是用户真正在意的事)。</summary>
    [TestMethod]
    public void TheSelectedRow_StaysInsideTheViewport()
    {
        OnUi(() =>
        {
            (Window window, CommandPaletteView view, CommandPaletteViewModel vm) = Show(60);
            ScrollViewer scroll = ScrollOf(view);

            for (int i = 0; i < 30; i++)
            {
                PressDown(view);
            }
            Relayout(window);

            Assert.IsNotNull(vm.SelectedItem);
            Border? selected = view.GetVisualDescendants()
                .OfType<Border>()
                .FirstOrDefault(b => b.Classes.Contains("pal-item")
                                     && ReferenceEquals(b.DataContext, vm.SelectedItem));
            Assert.IsNotNull(selected, "选中项连容器都没实例化 —— 它在可视区之外。");

            // 起点用 new Rect(Size):Bounds 本身已是父坐标系里的位置,再乘变换会把位移算两遍。
            Rect inViewport = new Rect(selected.Bounds.Size)
                .TransformToAABB(selected.TransformToVisual(scroll) ?? Matrix.Identity);
            Assert.IsGreaterThanOrEqualTo(-1, inViewport.Top, "选中项跑到了可视区上沿之外。");
            Assert.IsLessThanOrEqualTo(scroll.Bounds.Height + 1, inViewport.Bottom,
                "选中项跑到了可视区下沿之外。");
            window.Close();
        });
    }

    private static void PressDown(CommandPaletteView view) =>
        view.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Down,
            Source = view
        });

    private static void PressUp(CommandPaletteView view) =>
        view.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Up,
            Source = view
        });

    private static ScrollViewer ScrollOf(CommandPaletteView view) =>
        view.GetVisualDescendants().OfType<ListBox>().Single()
            .GetVisualDescendants().OfType<ScrollViewer>().First();

    private static (Window Window, CommandPaletteView View, CommandPaletteViewModel Vm) Show(int itemCount)
    {
        List<CommandPaletteItem> items =
        [
            .. Enumerable.Range(0, itemCount)
                .Select(i => new CommandPaletteItem($"分组{i / 10}", $"命令 {i:000}", () => { }))
        ];
        var vm = new CommandPaletteViewModel(() => items);
        var view = new CommandPaletteView { DataContext = vm };
        var window = new Window { Width = 700, Height = 600, Content = view };
        window.Show();
        vm.Open();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.IsGreaterThan(itemCount, vm.Rows.Count, "前置:结果行没有建出来。");
        return (window, view, vm);
    }

    private static void Relayout(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void OnUi(Action body) =>
        _session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();
}

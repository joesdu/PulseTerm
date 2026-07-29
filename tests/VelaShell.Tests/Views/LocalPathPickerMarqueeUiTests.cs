using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Localization;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 上传选择器的框选:派发真实的指针按下/移动/抬起,验证"拖出一个矩形 → 划过的行成片选中"。
/// <para>
/// 几何部分由 <see cref="MarqueeSelectionMathTests" /> 单独盯住;这一层盯的是接线 ——
/// 事件有没有真的到手(ListBoxItem 会把 PointerPressed 标记为已处理,漏了 handledEventsToo
/// 就一下都收不到)、坐标换算对不对、松手会不会被当成一次点击把选中集顶掉。
/// </para>
/// <para>
/// 这些断言是有牙的:实测过 —— 把框选关掉(起框阈值调到不可能达到)后,同样的拖动只会选中
/// 按下的那一行,ListBox 自己并没有拖选行为。
/// </para>
/// <para>
/// 注意 <see cref="OnUi" /> 里那个 <c>return Task.CompletedTask</c>:body 必须是同步的。
/// 传 <c>async () =&gt; …</c> 会绑到 <c>Dispatch(Func&lt;TResult&gt;)</c> 这个重载上,
/// <c>GetResult()</c> 拿到的是还没跑完的内层 Task —— 测试体根本没执行完就返回,
/// 断言一条都不会跑,四条测试全绿却什么都没测。
/// </para>
/// </summary>
[TestClass]
[TestCategory("MarqueeUI")]
public sealed class LocalPathPickerMarqueeUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(LocalPathPickerMarqueeUiTests).Assembly);
        LocalizedStrings.Instance.Attach(new LocalizationService());
    }

    [TestMethod]
    public void DraggingAcrossRows_SelectsEveryRowTheRectangleSweeps()
    {
        RunWithPicker((dialog, list, vm) =>
        {
            // 从第 1 行(".." 之后的头一个真实条目)拖到第 3 行。
            Drag(dialog, list, fromRow: 1, toRow: 3);

            Assert.AreSequenceEqual(
                ["a0.txt", "a1.txt", "a2.txt"], Names(vm), SequenceOrder.InAnyOrder, "框选划过的三行都该选中。"
            );
        });
    }

    [TestMethod]
    public void DraggingShowsTheRectangle_AndHidesItOnRelease()
    {
        RunWithPicker((dialog, list, vm) =>
        {
            Border marquee = dialog.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "Marquee");
            Assert.IsFalse(marquee.IsVisible, "没拖动时不该有矩形。");

            Drag(dialog, list, fromRow: 1, toRow: 4, midDrag: () =>
            {
                Assert.IsTrue(marquee.IsVisible, "拖动过程中必须看得见框选矩形。");
                Assert.IsGreaterThan(0, marquee.Height, "矩形应随拖动长高。");
            });

            Assert.IsFalse(marquee.IsVisible, "松手后矩形必须收起。");
        });
    }

    [TestMethod]
    public void DraggingUpwards_SelectsTheSameRows()
    {
        RunWithPicker((dialog, list, vm) =>
        {
            Drag(dialog, list, fromRow: 3, toRow: 1);

            Assert.AreSequenceEqual(
                ["a0.txt", "a1.txt", "a2.txt"], Names(vm), Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder, "向上拖应与向下拖选中同一批行。"
            );
        });
    }

    [TestMethod]
    public void MarqueeNeverSelectsTheSyntheticParentRow()
    {
        RunWithPicker((dialog, list, vm) =>
        {
            // 从 ".." 那一行(下标 0)往下拖:".." 不是真实条目,不能被框进来。
            Drag(dialog, list, fromRow: 0, toRow: 2);

            Assert.DoesNotContain(
                entry => entry.IsParentEntry, vm.SelectedEntries,
                "合成的 \"..\" 行不该被框选选中。"
            );
            Assert.AreSequenceEqual(["a0.txt", "a1.txt"], Names(vm), Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
        });
    }

    [TestMethod]
    public void PlainClickWithoutDragging_StillSelectsJustThatRow()
    {
        RunWithPicker((dialog, list, vm) =>
        {
            // 没有超过阈值的移动 = 普通点击,框选不能把它抢走。
            Point point = RowCenter(dialog, list, 2);
            dialog.MouseDown(point, MouseButton.Left);
            dialog.MouseUp(point, MouseButton.Left);
            Pump(dialog);

            Assert.AreSequenceEqual(["a1.txt"], Names(vm), Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
        });
    }

    private static string[] Names(LocalFilePaneViewModel vm) =>
        [.. vm.SelectedEntries.Select(entry => entry.Name)];

    /// <summary>按住左键从 <paramref name="fromRow" /> 拖到 <paramref name="toRow" /> 再松手。</summary>
    private static void Drag(Window dialog, ListBox list, int fromRow, int toRow, Action? midDrag = null)
    {
        Point start = RowCenter(dialog, list, fromRow);
        Point end = RowCenter(dialog, list, toRow);

        dialog.MouseDown(start, MouseButton.Left);
        Pump(dialog);

        // 分几步移动:第一步必须越过起框阈值,否则仍算普通点击。
        for (int step = 1; step <= 4; step++)
        {
            dialog.MouseMove(new(
                start.X + ((end.X - start.X) * step / 4),
                start.Y + ((end.Y - start.Y) * step / 4)
            ));
            Pump(dialog);
        }
        midDrag?.Invoke();

        dialog.MouseUp(end, MouseButton.Left);
        Pump(dialog);
    }

    /// <summary>第 <paramref name="index" /> 行中心点在窗口坐标系里的位置。</summary>
    private static Point RowCenter(Window dialog, ListBox list, int index)
    {
        Control container = list.ContainerFromIndex(index)
                            ?? throw new InvalidOperationException($"第 {index} 行没有实现出容器。");
        return container.TranslatePoint(new(20, container.Bounds.Height / 2), dialog)
               ?? throw new InvalidOperationException("坐标换算失败。");
    }

    private static void Pump(Window dialog)
    {
        Dispatcher.UIThread.RunJobs();
        dialog.UpdateLayout();
    }

    /// <summary>建一个含 6 个文件的临时目录,在其中开选择器,跑完 body 后务必关窗。</summary>
    private static void RunWithPicker(Action<Window, ListBox, LocalFilePaneViewModel> body) =>
        OnUi(() =>
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), $"vela-marquee-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempRoot);
            for (int i = 0; i < 6; i++)
            {
                File.WriteAllText(Path.Combine(tempRoot, $"a{i}.txt"), new string('x', i + 1));
            }

            var vm = new LocalFilePaneViewModel(new TransferOptions());
            PumpUntil(vm.NavigateToAsync(tempRoot));

            var dialog = new LocalPathPickerDialog(vm, loadInitial: false);
            try
            {
                dialog.Show();
                Dispatcher.UIThread.RunJobs();
                dialog.UpdateLayout();

                ListBox list = dialog.GetVisualDescendants().OfType<ListBox>().Single();
                Assert.HasCount(7, vm.Entries, "应为 \"..\" 加 6 个文件。");
                Assert.IsTrue(vm.Entries[0].IsParentEntry, "首行应是合成的 \"..\"。");
                vm.SelectedEntries.Clear();

                body(dialog, list, vm);
            }
            finally
            {
                // 不关窗整个 headless 套件会永久卡死。
                dialog.Close();
                Dispatcher.UIThread.RunJobs();
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, true);
                }
            }
        });

    private static void OnUi(Action body) =>
        _session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>在 UI 线程上把一个异步操作跑完(目录列举是异步的,但 body 必须保持同步)。</summary>
    private static void PumpUntil(Task task)
    {
        for (int i = 0; i < 2000 && !task.IsCompleted; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        task.GetAwaiter().GetResult();
    }
}

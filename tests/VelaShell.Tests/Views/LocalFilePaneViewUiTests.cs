using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Core.Models;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

[TestClass]
[TestCategory("LocalFilePane")]
public sealed class LocalFilePaneViewUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(LocalFilePaneViewUiTests).Assembly);
    }

    [TestMethod]
    public async Task InaccessibleRootSelectionRestoresPreviousSelection()
    {
        await _session.Dispatch(async () =>
        {
            using var first = new TempDirectory();
            var inaccessible = new LocalRootEntry("Unavailable", Path.Combine(first.Path, "missing"), false, Path.Combine(first.Path, "missing"));
            var accessible = new LocalRootEntry("~", first.Path, true, first.Path);
            var roots = new TestRootProvider(accessible, inaccessible);
            var viewModel = new LocalFilePaneViewModel(
                new TransferOptions { LocalDownloadDirectory = first.Path },
                rootProvider: roots);
            await viewModel.LoadInitialAsync();

            var view = new LocalFilePaneView { DataContext = viewModel };
            var window = new Window { Width = 600, Height = 400, Content = view };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                ComboBox combo = view.GetVisualDescendants().OfType<ComboBox>().Single();
                Assert.AreSame(accessible, combo.SelectedItem);

                combo.SelectedItem = inaccessible;
                Dispatcher.UIThread.RunJobs();

                Assert.AreSame(accessible, combo.SelectedItem);
                Assert.AreSame(accessible, viewModel.SelectedRoot);
                Assert.AreEqual(first.Path, viewModel.CurrentPath);
            }
            finally
            {
                window.Close();
            }
                    // **这一行不是凑数,少了它整条用例的断言全部失效。**
            // HeadlessUnitTestSession 只有 Dispatch(Action) 与 Dispatch<T>(Func<Task<T>>) 两族重载,
            // **没有 Func<Task> 那一支**。不返回值的 async lambda 于是被绑到 Action 上、变成 async void:
            // 断言异常落在调度线程上没人接,而 Dispatch 返回的 Task 早就完成了 —— 编译通过、测试恒绿。
            // 实测:把 Assert.Fail 放在用例第一行,dotnet test 照样报全过。
            // 有了返回值才会绑到 Func<Task<T>>,异常才会随 Task 传回来。
            return true;
        }, CancellationToken.None);
    }

    [TestMethod]
    public async Task DraggingFromRowWhitespace_MarqueesAcrossLocalRows()
    {
        await _session.Dispatch(async () =>
        {
            using var root = new TempDirectory();
            for (int i = 0; i < 5; i++)
            {
                File.WriteAllText(Path.Combine(root.Path, $"f{i}.txt"), $"file {i}");
            }

            var accessible = new LocalRootEntry("~", root.Path, true, root.Path);
            var viewModel = new LocalFilePaneViewModel(
                new TransferOptions { LocalDownloadDirectory = root.Path },
                rootProvider: new TestRootProvider(accessible));
            await viewModel.LoadInitialAsync();

            var view = new LocalFilePaneView { DataContext = viewModel };
            var window = new Window { Width = 700, Height = 450, Content = view };
            try
            {
                window.Show();
                Pump(window);

                ListBox list = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "FileList");
                Assert.IsGreaterThanOrEqualTo(4, list.ItemCount);
                viewModel.SelectedEntries.Clear();

                const int firstRow = 1;
                const int lastRow = 3;
                Control first = list.ContainerFromIndex(firstRow)!;
                Control last = list.ContainerFromIndex(lastRow)!;
                Point start = first.TranslatePoint(FindWhitespacePoint(first), window)!.Value;
                Point end = last.TranslatePoint(FindWhitespacePoint(last), window)!.Value;
                Border overlay = view.GetVisualDescendants().OfType<Border>()
                    .Single(border => border.Name == "MarqueeOverlay");

                window.MouseDown(start, MouseButton.Left);
                Pump(window);
                window.MouseMove(end);
                Pump(window);

                Assert.IsTrue(overlay.IsVisible, "本地栏行内空白拖动应显示框选矩形。");

                window.MouseUp(end, MouseButton.Left);
                Pump(window);

                Assert.AreSequenceEqual(
                    [.. viewModel.Entries.Skip(firstRow).Take(lastRow - firstRow + 1).Select(entry => entry.Name)], [.. viewModel.SelectedEntries.Select(entry => entry.Name)], Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
                    // **这一行不是凑数,少了它整条用例的断言全部失效。**
            // HeadlessUnitTestSession 只有 Dispatch(Action) 与 Dispatch<T>(Func<Task<T>>) 两族重载,
            // **没有 Func<Task> 那一支**。不返回值的 async lambda 于是被绑到 Action 上、变成 async void:
            // 断言异常落在调度线程上没人接,而 Dispatch 返回的 Task 早就完成了 —— 编译通过、测试恒绿。
            // 实测:把 Assert.Fail 放在用例第一行,dotnet test 照样报全过。
            // 有了返回值才会绑到 Func<Task<T>>,异常才会随 Task 传回来。
            return true;
        }, CancellationToken.None);
    }

    private static Point FindWhitespacePoint(Control row)
    {
        double y = row.Bounds.Height / 2;
        for (double x = 40; x < row.Bounds.Width - 10; x += 10)
        {
            var point = new Point(x, y);
            if (!MarqueeSelection.IsDndSurface(row, row, point))
            {
                return point;
            }
        }

        throw new InvalidOperationException("本地文件行中没有找到可用于框选的视觉空白区域。");
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private sealed class TestRootProvider(params LocalRootEntry[] roots) : ILocalRootProvider
    {
        public Task<IReadOnlyList<LocalRootEntry>> EnumerateAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LocalRootEntry>>(roots);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"velashell-root-ui-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}

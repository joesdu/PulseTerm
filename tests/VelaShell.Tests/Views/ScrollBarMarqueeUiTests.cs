using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Localization;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 拖动文件列表自己的滚动条不能起框选(用户报告:SFTP/FTP 面板拖滚动条会拉出选区)。
/// </summary>
/// <remarks>
/// <para>
/// 滚动条长在 ListBox 自己的模板里,按下事件照样冒泡到 ListBox —— 而框选是以
/// handledEventsToo 挂在 ListBox 上的,不把滚动条排除掉就一定会被它接走。
/// </para>
/// <para>
/// 本地栏尤其明显:行的最后一列(修改时间)是 <c>HorizontalAlignment="Left"</c> 的
/// dnd-surface,实际只有文字那么宽,右侧滚动条那一带谁都没盖 —— 于是 canStart 放行,框选起头。
/// </para>
/// <para>
/// body 必须同步 + <c>return Task.CompletedTask</c>:传 async lambda 会绑到
/// <c>Dispatch(Func&lt;TResult&gt;)</c>,断言一条都不会跑却全绿。
/// </para>
/// </remarks>
[TestClass]
[TestCategory("MarqueeUI")]
public sealed class ScrollBarMarqueeUiTests
{
    /// <summary>行要够多,列表才滚得动、才有滑块可拖。</summary>
    private const int FileCount = 60;

    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ScrollBarMarqueeUiTests).Assembly);
        LocalizedStrings.Instance.Attach(new LocalizationService());
    }

    /// <summary>本地栏(双栏左侧)—— 用户实际报的那一条。</summary>
    [TestMethod]
    public void LocalPane_DraggingTheScrollBar_DoesNotMarquee()
    {
        using var root = new TempDirectory();
        for (int i = 0; i < FileCount; i++)
        {
            File.WriteAllText(Path.Combine(root.Path, $"f{i:D2}.txt"), "x");
        }

        OnUi(() =>
        {
            var vm = new LocalFilePaneViewModel(
                new TransferOptions { LocalDownloadDirectory = root.Path },
                rootProvider: new TestRootProvider(new LocalRootEntry("~", root.Path, true, root.Path)));
            PumpUntilComplete(vm.LoadInitialAsync());

            var view = new LocalFilePaneView { DataContext = vm };
            RunScrollBarDrag(view, () => vm.SelectedEntries.Count);
        });
    }

    /// <summary>远端栏走的是同一个 MarqueeSelection,同样不该被滚动条起框。</summary>
    [TestMethod]
    public void RemotePane_DraggingTheScrollBar_DoesNotMarquee()
    {
        OnUi(() =>
        {
            ISftpService sftp = Substitute.For<ISftpService>();
            var sessionId = Guid.NewGuid();
            List<RemoteFileInfo> files =
            [
                .. Enumerable.Range(1, FileCount).Select(i => new RemoteFileInfo
                {
                    Name = $"f{i:D2}.txt",
                    FullPath = $"/home/user/f{i:D2}.txt",
                    Size = i,
                    Permissions = "-rw-r--r--",
                    IsDirectory = false,
                    LastModified = DateTime.UtcNow,
                    Owner = "user",
                    Group = "user",
                }),
            ];
            sftp.ListDirectoryAsync(sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(files));

            var vm = new FileBrowserViewModel(sftp, sessionId)
            {
                IsVisible = true,
                IsDragEnabled = true,
                TransferOptions = new(),
                CurrentPath = "/home/user",
            };
            vm.RefreshCommand.Execute().Subscribe();
            PumpUntil(() => vm.Files.Count > 0, "目录列举没能在预期时间内完成。");

            var view = new FileBrowserView { DataContext = vm };
            RunScrollBarDrag(view, () => vm.SelectedFiles.Count);
        });
    }

    /// <summary>
    /// 把视图挂进窗口,按住纵向滚动条的滑块往下拖,全程不得出现框选矩形、不得选中任何行。
    /// </summary>
    private static void RunScrollBarDrag(Control view, Func<int> selectedCount)
    {
        var window = new Window { Width = 900, Height = 600, Content = view };
        try
        {
            window.Show();
            Pump(window);

            ListBox list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "FileList");
            Border overlay = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MarqueeOverlay");
            ScrollBar bar = list.GetVisualDescendants().OfType<ScrollBar>()
                                .Single(b => b.Orientation == Orientation.Vertical);
            Thumb thumb = bar.GetVisualDescendants().OfType<Thumb>().Single();
            Assert.IsGreaterThan(0, thumb.Bounds.Height, "没量到滑块 —— 列表八成没溢出,滚动条不存在。");

            // 刺激送达的凭据:光看"没框选"不算数,得先证明这一下确实按在了滚动条上。
            // (滑块在 headless 下拖不动滚动偏移,所以不能拿 Offset 当凭据。)
            var pressedOnBar = false;
            bar.AddHandler(
                InputElement.PointerPressedEvent,
                (_, _) => pressedOnBar = true,
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
                handledEventsToo: true);

            // 必须落在滑块上:压在轨道上会启动滚动条的连续翻页计时器,RunJobs 再也排不空。
            Point start = thumb.TranslatePoint(new(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;

            window.MouseMove(start);
            Pump(window);
            window.MouseDown(start, MouseButton.Left);
            Pump(window);
            var overlayEverVisible = false;
            for (int step = 1; step <= 6; step++)
            {
                window.MouseMove(new(start.X, start.Y + (20.0 * step)));
                Pump(window);
                overlayEverVisible |= overlay.IsVisible;
            }
            window.MouseUp(new(start.X, start.Y + 120), MouseButton.Left);
            Pump(window);

            Assert.IsTrue(pressedOnBar, "按下没落到滚动条上 —— 这条用例什么都没测。");
            Assert.IsFalse(overlayEverVisible, "拖滚动条时冒出了框选矩形。");
            Assert.AreEqual(0, selectedCount(), "拖滚动条不该选中任何文件。");
        }
        finally
        {
            // 不关窗整个 headless 套件会永久卡死。
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    /// <summary>在 UI 线程上把异步准备跑完(body 必须保持同步,不能 await)。</summary>
    private static void PumpUntilComplete(Task task)
    {
        PumpUntil(() => task.IsCompleted, "本地栏初始化没能在预期时间内完成。");
        task.GetAwaiter().GetResult();
    }

    private static void PumpUntil(Func<bool> done, string message)
    {
        for (int i = 0; i < 5000 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        Assert.IsTrue(done(), message);
    }

    private static void OnUi(Action body) =>
        _session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();

    private sealed class TestRootProvider(params LocalRootEntry[] roots) : ILocalRootProvider
    {
        public Task<IReadOnlyList<LocalRootEntry>> EnumerateAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LocalRootEntry>>(roots);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"velashell-sbmarquee-{Guid.NewGuid():N}");
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

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
/// 路径里的下划线必须原样显示(<c>/app_old</c> 显示成 <c>/appold</c>)。
/// Avalonia 默认 Button 模板的 ContentPresenter 开着
/// RecognizesAccessKey:字符串内容被当成带助记符的菜单文字解析,<c>_</c> 被吃掉、后一个字母加下划线。
/// 三条路径栏(远端面包屑、本地面板、本地路径选择器)共用同一套 Button.crumb,同一个坑。
/// </summary>
/// <remarks>
/// 断言落在真实视图上:面包屑按钮的可视子树里不能出现 <see cref="AccessText" />
/// —— 只要 ContentPresenter 走了助记符那条路,它就会用 AccessText 而不是 TextBlock 承载文字。
/// 光比 <c>AccessText.Text</c> 没用:它保留着下划线,被吞掉的只是渲染那一步。
/// </remarks>
[TestClass]
[TestCategory("FileBrowserUi")]
public sealed class BreadcrumbUnderscoreUiTests
{
    private const string UnderscoreSegment = "app_old";
    private const string RemotePathWithUnderscore = "/root/path/" + UnderscoreSegment;

    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(BreadcrumbUnderscoreUiTests).Assembly);

    /// <summary>远端 SFTP 面板的面包屑 —— 用户实际报的那一条。</summary>
    [TestMethod]
    public void RemoteBreadcrumb_KeepsUnderscoreInsteadOfEatingItAsAccessKey()
    {
        OnUi(() =>
        {
            ISftpService sftp = Substitute.For<ISftpService>();
            var vm = new FileBrowserViewModel(sftp, Guid.NewGuid())
            {
                IsVisible = true,
                CurrentPath = RemotePathWithUnderscore
            };
            ShowAndAssert(new FileBrowserView { DataContext = vm }, minimumSegments: 3);
        });
    }

    /// <summary>本地面板(双栏左侧)的面包屑走同一套样式。</summary>
    [TestMethod]
    public void LocalPaneBreadcrumb_KeepsUnderscoreInsteadOfEatingItAsAccessKey() => RunLocalPaneCase(vm => new LocalFilePaneView { DataContext = vm });

    /// <summary>本地路径选择器对话框的面包屑同理。</summary>
    [TestMethod]
    public void LocalPathPickerBreadcrumb_KeepsUnderscoreInsteadOfEatingItAsAccessKey() => RunLocalPaneCase(vm => new LocalPathPickerDialog(vm, loadInitial: false));

    /// <summary>
    /// 在一个名字带下划线的真实临时目录上起本地面板视图模型,交给 <paramref name="createView" />
    /// 组装出待测视图,再核对渲染。
    /// </summary>
    private static void RunLocalPaneCase(Func<LocalFilePaneViewModel, Control> createView)
    {
        using var temp = new TempDirectory();
        string underscored = Path.Combine(temp.Path, UnderscoreSegment);
        Directory.CreateDirectory(underscored);

        OnUi(() =>
        {
            var roots = new TestRootProvider(new LocalRootEntry("~", temp.Path, true, temp.Path));
            var vm = new LocalFilePaneViewModel(
                new TransferOptions { LocalDownloadDirectory = underscored },
                rootProvider: roots);
            PumpUntilComplete(vm.LoadInitialAsync());
            Assert.AreEqual(underscored, vm.CurrentPath);

            // 本地路径的根(/ 或 C:\)不算分段,临时目录至少还剩 velashell-… 与 app_old 两级。
            ShowAndAssert(createView(vm), minimumSegments: 2);
        });
    }

    /// <summary>
    /// 在 UI 线程上把 <paramref name="task" /> 泵到完成。
    /// </summary>
    /// <remarks>
    /// Dispatch 的消息泵只在它自己那个外层任务挂起期间转。body 写成 async 的话,第一个 await
    /// 之后的续体就再没人泵 —— 表现为整条用例 60s 超时。所以 body 必须同步,异步准备工作
    /// 自己在这里泵。(实测:本类两个本地用例写成 async 必挂,改成这样即过。)
    /// </remarks>
    private static void PumpUntilComplete(Task task)
    {
        for (int i = 0; i < 5000 && !task.IsCompleted; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        Assert.IsTrue(task.IsCompleted, "本地面板初始化没能在 5 秒内完成 —— 泵超时。");
        task.GetAwaiter().GetResult(); // 让 body 里的异常照常抛出,而不是被吞成超时。
    }

    /// <summary>
    /// 把视图挂进 headless 窗口、布局、核对面包屑渲染,然后<b>就地关窗</b>。
    /// </summary>
    /// <remarks>
    /// 关窗必须发生在开窗的这一次 Dispatch 之内。全程序集共用一条 headless UI 线程,
    /// 而异步 Dispatch(body 里有 await 的那种)一旦带着未关闭的窗口返回,后续任何一次
    /// Dispatch 都再也不会完成 —— 表现为测试整体 60s 超时而非断言失败。放到 TestCleanup 里
    /// 关就正好踩中这条:实测本类两个 async 用例必挂,改成就地关窗即过。
    /// </remarks>
    private static void ShowAndAssert(Control view, int minimumSegments)
    {
        Window window = view as Window ?? new Window { Width = 1200, Height = 400, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AssertBreadcrumbKeepsUnderscore(window, minimumSegments);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>核对窗口里 crumb 按钮的真实渲染:带下划线那段在、且没走助记符解析。</summary>
    private static void AssertBreadcrumbKeepsUnderscore(Window window, int minimumSegments)
    {
        TextBlock[] crumbTexts = [.. window.GetVisualDescendants()
                                      .OfType<Button>()
                                      .Where(b => b.Classes.Contains("crumb"))
                                      .SelectMany(b => b.GetVisualDescendants().OfType<TextBlock>())];

        // 扫描本身得先成立:一个面包屑都没找到时,下面两条断言会空转成永远通过的空壳。
        Assert.IsGreaterThanOrEqualTo(minimumSegments, crumbTexts.Length,
                                      "没找到面包屑分段 —— 扫描八成失效了,别让这条测试变成空壳。");
        Assert.Contains(t => t.Text == UnderscoreSegment, crumbTexts,
                      $"面包屑里没有 {UnderscoreSegment},实际渲染:{string.Join(" | ", crumbTexts.Select(t => t.Text))}");
        Assert.IsEmpty(crumbTexts.OfType<AccessText>().Select(t => t.Text).ToArray(),
                       "面包屑走了助记符解析(AccessText),路径里的下划线会被当成访问键吃掉。");
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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"velashell-crumb-ui-{Guid.NewGuid():N}");
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

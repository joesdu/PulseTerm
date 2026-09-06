using System.Text;
using Avalonia.Headless;
using Avalonia.Threading;
using VelaShell.PluginSdk.TerminalView;
using VelaShell.Services.Plugins;

namespace VelaShell.Tests.Services;

/// <summary>
/// 插件终端视图的输入合批。
/// </summary>
/// <remarks>
/// 读循环一次最多读 16KB,一条刷屏命令会在几十毫秒里产出上百块。原先每块都
/// <c>Dispatcher.Post</c> 一次 —— 上百次跨线程调度,每次唤醒一轮 UI 调度器。
/// 合批是对的,但合批最容易出的错是<b>顺序</b>:UTF-8 与转义序列都经不起乱序。
/// </remarks>
[TestClass]
[TestCategory("PluginTerminal")]
public sealed class PluginTerminalFeedBatchingTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(PluginTerminalFeedBatchingTests).Assembly);

    /// <summary>后台线程连着喂很多小块,合批之后内容与顺序都要原样。</summary>
    [TestMethod]
    public void ManySmallFeedsFromABackgroundThread_ArriveInOrder()
    {
        OnUi(() =>
        {
            using IPluginTerminalView view = CreateView();

            // 200 个小块,合起来是一段可辨认的文本(会在 120 列上回绕成若干行)。
            var expected = new StringBuilder();
            Task.Run(() =>
            {
                for (int i = 0; i < 200; i++)
                {
                    string piece = $"[{i:000}]";
                    expected.Append(piece);
                    view.Feed(Encoding.UTF8.GetBytes(piece));
                }
            }).GetAwaiter().GetResult();

            PumpUntil(() => Flatten(view).Contains("[199]", StringComparison.Ordinal));

            Assert.Contains(expected.ToString(), Flatten(view), "合批之后内容或顺序对不上了。");
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// 后台攒着的还没喂进去,此时在 UI 线程上直接喂 —— 新的这段不能插到前面去。
    /// </summary>
    /// <remarks>
    /// 插件在同步代码里写欢迎语走的正是 UI 线程那条路。合批引入的这条快捷路径
    /// 如果不先把攒着的排出去,字节顺序就会颠倒。
    /// </remarks>
    [TestMethod]
    public void AFeedOnTheUiThread_DoesNotJumpAheadOfQueuedBytes()
    {
        OnUi(() =>
        {
            using IPluginTerminalView view = CreateView();

            // 从后台线程入队(此刻只是攒着,Post 还没跑到 —— 我们就在 UI 线程上,没让出去过)。
            Task.Run(() => view.Feed("FIRST"u8.ToArray())).GetAwaiter().GetResult();
            // 紧接着在 UI 线程上直接喂。
            view.Feed("SECOND"u8.ToArray());

            PumpUntil(() => Flatten(view).Contains("SECOND", StringComparison.Ordinal));

            string text = Flatten(view);
            int first = text.IndexOf("FIRST", StringComparison.Ordinal);
            int second = text.IndexOf("SECOND", StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, first, "先入队的那段丢了。");
            Assert.IsLessThan(second, first, "UI 线程上喂的那段插到了先入队的前面。");
            return Task.CompletedTask;
        });
    }

    /// <summary>释放之后不再喂进任何东西。</summary>
    [TestMethod]
    public void AfterDispose_NothingMoreIsFed()
    {
        OnUi(() =>
        {
            IPluginTerminalView view = CreateView();
            Task.Run(() => view.Feed("queued-but-never-shown"u8.ToArray())).GetAwaiter().GetResult();
            view.Dispose();

            // 泵几轮,让那次可能已经排上的刷新有机会跑。
            for (int i = 0; i < 50; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }

            // 走到这里没抛异常即为通过:Dispose 之后的刷新不得碰已释放的控件。
            return Task.CompletedTask;
        });
    }

    private static IPluginTerminalView CreateView() =>
        new PluginTerminalViewApi(() => null).Create(new TerminalViewOptions { FollowHostAppearance = false });

    /// <summary>
    /// 整屏文本去掉换行。控件是 120 列的,连续输出会回绕成好几行,
    /// 而这里要验证的是<b>字节流本身</b>的内容与顺序,不是它被折成了几行。
    /// </summary>
    private static string Flatten(IPluginTerminalView view) =>
        view.GetText(200).Replace("\n", "", StringComparison.Ordinal);

    private static void OnUi(Func<Task> action) =>
        _session.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();

    private static void PumpUntil(Func<bool> done)
    {
        for (int i = 0; i < 2_000 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.IsTrue(done(), "等待的条件没能在超时内成立。");
    }
}

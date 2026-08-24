using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using LiveMarkdown.Avalonia;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 锁住聊天正文给 LaTeX 公式补色所依赖的那条通知:LiveMarkdown 每次渲染定稿都要有一次
/// <c>RenderedTextProjection</c> 变更通知(2.4.0 撤掉了 RenderedTextProjectionChanged 事件,
/// 改走属性通知,见 ChatPanelView.MarkdownSegment)。这条一旦哑掉,公式在暗色主题下会变成
/// 黑底黑字 —— 而那是<b>编译期看不出</b>的静默回归,所以用真渲染守住。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class MarkdownProjectionNotificationTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(
            typeof(MarkdownProjectionNotificationTests).Assembly);

    [TestMethod]
    public void RenderedTextProjection_RaisesPropertyChanged_OnEveryCommittedRender()
    {
        OnUi(async () =>
        {
            var text = new ObservableStringBuilder();
            var renderer = new MarkdownRenderer { MarkdownBuilder = text };
            int hits = 0;
            renderer.PropertyChanged += (_, e) =>
            {
                if (e.Property == MarkdownRenderer.RenderedTextProjectionProperty)
                {
                    hits++;
                }
            };

            var window = new Window { Width = 480, Height = 320, Content = renderer };
            window.Show();
            await PumpAsync();
            try
            {
                text.Append("# 标题\n\n正文一段。\n");
                await PumpAsync();
                Assert.IsGreaterThan(0, hits, "首次渲染定稿后应当有一次投影变更通知。");

                // 流式追加还要再来一次:公式控件是每次渲染新建的,只在首帧补色不够。
                int afterFirst = hits;
                text.Append("\n$E = mc^2$\n\n又一段。\n");
                await PumpAsync();
                Assert.IsGreaterThan(
                    afterFirst, hits,
                    "后续追加渲染定稿后应当再发通知,否则流式回复里新建的公式控件补不到色。"
                );
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>渲染是后台解析 + UI 线程提交,跑几拍调度器让它落定。</summary>
    private static async Task PumpAsync(int rounds = 60)
    {
        for (int i = 0; i < rounds; i++)
        {
            await Task.Delay(5);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>在 headless UI 线程上跑一段异步测试体(重载选择的坑见 ChatPanelViewUiTests.OnUi)。</summary>
    private static void OnUi(Func<Task> body) =>
        _session.Dispatch(async () =>
        {
            await body();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
}

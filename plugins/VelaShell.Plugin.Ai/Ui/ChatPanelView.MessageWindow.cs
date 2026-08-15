using Avalonia.Controls;
using Avalonia.Threading;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 消息流的"活动窗口":只让最近若干条消息留在可视树里,更早的收进一枚可点开的横幅。
/// </summary>
/// <remarks>
/// <b>为什么需要</b>:<c>MessagesPanel</c> 是普通 StackPanel,不虚拟化。每条 assistant 消息
/// 至少挂一个 <c>MarkdownRenderer</c>,带代码块的还拖着一整棵 TextMate 高亮的可视树 ——
/// 聊上百条之后全都常驻内存,滚动与布局都跟着变慢。
///
/// <b>为什么不直接上虚拟化</b>:气泡是命令式构造、并且在流式过程中持续自更新的活控件,
/// 换成 ItemsRepeater 要先把它们改造成数据模型 + 模板,再处理"容器回收时正在流式写入的那条"
/// 与变高元素下的粘底滚动 —— 那是另一个量级的改动,风险远大于收益。
/// 这里退一步:<b>把常驻数量钉死</b>。效果上同样解决了"越聊越卡",而且行为可预测、能测。
/// </remarks>
public partial class ChatPanelView
{
    /// <summary>可视树里最多常驻多少条消息。够翻回去看几轮,又不会让可视树无限长。</summary>
    private const int LiveMessageWindow = 40;

    /// <summary>一次折叠掉多少条 —— 每加一条就折一条会让滚动位置一直抖。</summary>
    private const int CollapseBatch = 20;

    /// <summary>回放历史时每帧建几条。整段一次性建完会把 UI 线程按住好几秒。</summary>
    private const int ReplayBatch = 8;

    /// <summary>被折叠起来的早期气泡(按原顺序);点横幅可以整批放回去。</summary>
    private readonly List<Control> _collapsedMessages = [];

    private Border? _collapsedBanner;

    /// <summary>
    /// 新消息进来之后调用:超出窗口就把最早的一批移出可视树。
    /// 它们只是被摘下来存着,没有销毁 —— 点横幅即可原样挂回。
    /// </summary>
    private void TrimMessageWindow()
    {
        int live = MessagesPanel.Children.Count - (_collapsedBanner is null ? 0 : 1);
        if (live <= LiveMessageWindow + CollapseBatch)
        {
            return;
        }
        int bannerOffset = _collapsedBanner is null ? 0 : 1;
        int take = live - LiveMessageWindow;
        var moved = new List<Control>(take);
        for (int i = 0; i < take; i++)
        {
            Control child = MessagesPanel.Children[bannerOffset];
            MessagesPanel.Children.RemoveAt(bannerOffset);
            moved.Add(child);
        }
        _collapsedMessages.InsertRange(0, moved);
        ShowCollapsedBanner();
    }

    /// <summary>顶部那枚"显示更早的 N 条"横幅(点一下全部放回)。</summary>
    private void ShowCollapsedBanner()
    {
        if (_collapsedMessages.Count == 0)
        {
            RemoveCollapsedBanner();
            return;
        }
        if (_collapsedBanner is null)
        {
            var text = new TextBlock { Classes = { "dim" } };
            _collapsedBanner = new Border
            {
                Classes = { "earlierBanner" },
                Child = text,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
            };
            _collapsedBanner.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                RestoreCollapsedMessages();
            };
            MessagesPanel.Children.Insert(0, _collapsedBanner);
        }
        ((TextBlock)_collapsedBanner.Child!).Text = _loc.F("ShowEarlier", _collapsedMessages.Count);
    }

    private void RestoreCollapsedMessages()
    {
        if (_collapsedMessages.Count == 0)
        {
            return;
        }
        RemoveCollapsedBanner();
        for (int i = 0; i < _collapsedMessages.Count; i++)
        {
            MessagesPanel.Children.Insert(i, _collapsedMessages[i]);
        }
        _collapsedMessages.Clear();
    }

    private void RemoveCollapsedBanner()
    {
        if (_collapsedBanner is { } banner)
        {
            MessagesPanel.Children.Remove(banner);
            _collapsedBanner = null;
        }
    }

    /// <summary>换会话/新建会话时连折叠区一起清干净。</summary>
    private void ResetMessageWindow()
    {
        _collapsedMessages.Clear();
        _collapsedBanner = null;
    }

    /// <summary>
    /// 分帧回放:每 <see cref="ReplayBatch" /> 条让一次 UI 线程。
    /// 历史一次最多能取 2000 条,一口气建完足够把界面按住好几秒。
    /// </summary>
    private static async Task YieldEveryBatchAsync(int index)
    {
        if (index > 0 && index % ReplayBatch == 0)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }
}

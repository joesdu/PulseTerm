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

    // 折叠起来的早期气泡与那枚横幅,按对话各持一份(见 Conversation):
    // _collapsedMessages / _collapsedBanner。

    /// <summary>
    /// 新消息进来之后调用:超出窗口就把最早的一批移出可视树。
    /// 它们只是被摘下来存着,没有销毁 —— 点横幅即可原样挂回。
    /// </summary>
    private void TrimMessageWindow()
    {
        int live = MessagesPanel.Children.Count - (CollapsedBanner is null ? 0 : 1);
        if (live <= LiveMessageWindow + CollapseBatch)
        {
            return;
        }
        int bannerOffset = CollapsedBanner is null ? 0 : 1;
        int take = live - LiveMessageWindow;
        var moved = new List<Control>(take);
        for (int i = 0; i < take; i++)
        {
            Control child = MessagesPanel.Children[bannerOffset];
            MessagesPanel.Children.RemoveAt(bannerOffset);
            moved.Add(child);
        }
        // 追加而不是插到头部。每次折叠摘的都是**当时最旧的**那批存活消息,所以第二批
        // 一定比第一批新 —— InsertRange(0, …) 会把新批排到旧批前面,折叠两次以上再点开
        // "显示更早的",消息就以 B批 → A批 → 当前 的顺序出现,用户/助手配对随之错位。
        CollapsedMessages.AddRange(moved);
        ShowCollapsedBanner();
    }

    /// <summary>顶部那枚"显示更早的 N 条"横幅(点一下全部放回)。</summary>
    private void ShowCollapsedBanner()
    {
        if (CollapsedMessages.Count == 0)
        {
            RemoveCollapsedBanner();
            return;
        }
        if (CollapsedBanner is null)
        {
            var text = new TextBlock { Classes = { "dim" } };
            CollapsedBanner = new Border
            {
                Classes = { "earlierBanner" },
                Child = text,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
            };
            CollapsedBanner.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                RestoreCollapsedMessages();
            };
            MessagesPanel.Children.Insert(0, CollapsedBanner);
        }
        ((TextBlock)CollapsedBanner.Child!).Text = _loc.F("ShowEarlier", CollapsedMessages.Count);
    }

    private void RestoreCollapsedMessages()
    {
        if (CollapsedMessages.Count == 0)
        {
            return;
        }
        RemoveCollapsedBanner();
        for (int i = 0; i < CollapsedMessages.Count; i++)
        {
            MessagesPanel.Children.Insert(i, CollapsedMessages[i]);
        }
        CollapsedMessages.Clear();
    }

    private void RemoveCollapsedBanner()
    {
        if (CollapsedBanner is { } banner)
        {
            MessagesPanel.Children.Remove(banner);
            CollapsedBanner = null;
        }
    }

    /// <summary>换会话/新建会话时连折叠区一起清干净。</summary>
    private void ResetMessageWindow()
    {
        CollapsedMessages.Clear();
        CollapsedBanner = null;
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

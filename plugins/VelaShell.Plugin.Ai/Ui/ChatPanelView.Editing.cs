using Avalonia.Controls;
using Microsoft.Extensions.AI;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 对已发出消息的三个操作:编辑重发、删除、重新生成。
/// </summary>
/// <remarks>
/// 三者共用同一条语义 —— <b>回到某一点,后面的全部作废</b>。对话是有前后依赖的,
/// 改中间一条却留着后面的回答,只会得到一段自相矛盾的记录。所以:
/// <list type="bullet">
/// <item><b>编辑</b>:截断到这条用户消息之前,把原文放回输入框,你改完再发。</item>
/// <item><b>删除</b>:截断到这条用户消息之前,不放回输入框。</item>
/// <item><b>重新生成</b>:截断到最后一条用户消息之前,原样再发一次。</item>
/// </list>
/// 截断会连带重写库里那段会话(见 <c>ChatHistoryStore.RewriteAsync</c>),免得界面和历史对不上。
/// </remarks>
public partial class ChatPanelView
{
    /// <summary>用户气泡 → 它在 <c>_history</c> 里的下标。截断时要靠它找到"从哪儿开始扔"。</summary>
    private readonly Dictionary<Control, int> _userBubbleIndex = [];

    /// <summary>
    /// 已经用掉的最大序号。截断后新消息从这里往后续号,<b>不复用旧号</b> ——
    /// 旧号上可能还挂着被删那截的附加信息,复用会张冠李戴。
    /// </summary>
    private int _sequenceHighWater;

    private void ResetEditing()
    {
        _userBubbleIndex.Clear();
        _sequenceHighWater = _persistedCount;
    }

    /// <summary>把一条用户气泡登记进索引(截断时才知道它对应历史里的哪一条)。</summary>
    private void TrackUserBubble(Control bubble, int historyIndex) => _userBubbleIndex[bubble] = historyIndex;

    /// <summary>编辑这条用户消息:截断到它之前,原文回到输入框。</summary>
    private async Task EditUserMessageAsync(Control bubble, string original)
    {
        if (!await TruncateAtAsync(bubble))
        {
            return;
        }
        InputBox.Text = original;
        InputBox.CaretOffset = original.Length;
        InputBox.TextArea.Focus();
    }

    /// <summary>删除这条用户消息及其之后的一切。</summary>
    private Task DeleteFromAsync(Control bubble) => TruncateAtAsync(bubble);

    /// <summary>
    /// 重新生成 —— 只对<b>最后一条</b>回复有效。中间那条重跑会让它后面的问答全部失效,
    /// 与其悄悄连坐,不如提示用户去"编辑上一条",那条路径是明示的。
    /// </summary>
    private async Task RegenerateIfLastAsync(Control replyBubble)
    {
        RestoreCollapsedMessages();
        if (!ReferenceEquals(MessagesPanel.Children.LastOrDefault(), replyBubble))
        {
            StatusText.Text = _loc["RegenerateOnlyLast"];
            return;
        }
        await RegenerateAsync();
    }

    /// <summary>回到最后一条用户消息,原样再问一次。</summary>
    private async Task RegenerateAsync()
    {
        if (_busy)
        {
            return;
        }
        int index = _history.FindLastIndex(m => m.Role == ChatRole.User);
        if (index < 0)
        {
            return;
        }
        string text = _history[index].Text;
        if (!await TruncateToAsync(index))
        {
            return;
        }
        await SendAsync(text, fromUser: false);
    }

    /// <summary>截断到某条用户气泡之前。返回是否真的动了。</summary>
    private async Task<bool> TruncateAtAsync(Control bubble)
        => _userBubbleIndex.TryGetValue(bubble, out int index) && await TruncateToAsync(index);

    /// <summary>
    /// 丢掉 <paramref name="historyIndex" /> 及其之后的全部消息:界面、上下文、库三处一起。
    /// </summary>
    private async Task<bool> TruncateToAsync(int historyIndex)
    {
        if (_busy || historyIndex < 0 || historyIndex >= _history.Count)
        {
            return false;
        }
        // 被折叠起来的早期消息也要能被截断到,先整批放回来
        RestoreCollapsedMessages();

        // 界面:找到该下标对应的气泡,从它起往后全部移除
        if (_userBubbleIndex.FirstOrDefault(pair => pair.Value == historyIndex).Key is { } anchor)
        {
            int from = MessagesPanel.Children.IndexOf(anchor);
            if (from >= 0)
            {
                for (int i = MessagesPanel.Children.Count - 1; i >= from; i--)
                {
                    MessagesPanel.Children.RemoveAt(i);
                }
            }
        }
        foreach (Control stale in _userBubbleIndex.Where(p => p.Value >= historyIndex).Select(p => p.Key).ToList())
        {
            _userBubbleIndex.Remove(stale);
        }

        // 上下文
        _history.RemoveRange(historyIndex, _history.Count - historyIndex);

        // 库:整段重写(时序库删不了单条),序号沿用原值以便附加信息还能对上
        var surviving = new List<(int, string, string)>(_history.Count);
        for (int i = 0; i < _history.Count; i++)
        {
            string role = _history[i].Role == ChatRole.User ? "user" : "assistant";
            surviving.Add((i, role, _history[i].Text));
        }
        await _historyStore.RewriteAsync(_conversationId, _conversationStartedAt, surviving);

        // 新消息从旧的最大序号之后续,别复用可能还挂着附加信息的旧号
        _sequenceHighWater = Math.Max(_sequenceHighWater, _persistedCount);
        _persistedCount = _sequenceHighWater;

        // 摘要覆盖的是被截断之前的那些消息,截断后它的范围就不可信了 —— 整个作废,
        // 下次接近窗口时重新压一遍。宁可多花一次压缩,也不能让模型读到对不上号的摘要。
        ResetCompaction();

        ClearSuggestions();
        // 删到一条不剩就退回空状态(起手示例在那儿,不再是输入框上方的药丸)
        UpdateEmptyState();
        UpdateUsageText();
        return true;
    }
}

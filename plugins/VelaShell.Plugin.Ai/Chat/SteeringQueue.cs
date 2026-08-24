using Microsoft.Extensions.AI;

namespace VelaShell.Plugin.Ai.Chat;

/// <summary>
/// 一条排队中的插话(用户在上一轮还没答完时补发的消息)。
/// </summary>
/// <param name="DisplayText">
/// 界面与库里留的那一份:短名 <c>@</c> 引用 + 附件留痕,和普通用户消息的口径一致。
/// </param>
/// <param name="RawText">
/// 用户实际敲进输入框的原文。撤回队列时要原样放回输入框,所以不能只留 <paramref name="DisplayText" />
/// —— 那份带着 <c>[截图.png]</c> 这种留痕,放回去只会让人以为附件还在。
/// </param>
/// <param name="Message">
/// 送给模型的那一份:<c>@</c> 引用已展开成完整路径与文件内容,本地附件已并进来。
/// <b>在入队那一刻就定下来</b> —— 排队期间远端文件还可能被改,用户按下回车时看到的那份才算数。
/// </param>
internal sealed record SteeringMessage(string DisplayText, string RawText, ChatMessage Message);

/// <summary>
/// 面板级的插话队列:用户在一轮进行中补发的消息先落在这里,
/// 由 <see cref="SteeringChatClient" /> 在模型下一步之前送进上下文。
/// </summary>
/// <remarks>
/// 必须线程安全:入队发生在 UI 线程,取用发生在流式读循环所在的线程池线程。
/// </remarks>
internal sealed class SteeringQueue
{
    private readonly Lock _gate = new();
    private readonly List<SteeringMessage> _pending = [];

    /// <summary>还没送出去的条数。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    public void Enqueue(SteeringMessage message)
    {
        lock (_gate)
        {
            _pending.Add(message);
        }
    }

    /// <summary>撤回某一条(用户点掉了那枚芯片);返回它当时是否还在队里。</summary>
    public bool Remove(SteeringMessage message)
    {
        lock (_gate)
        {
            return _pending.Remove(message);
        }
    }

    /// <summary>当前队列的快照(渲染芯片用)。</summary>
    public IReadOnlyList<SteeringMessage> Snapshot()
    {
        lock (_gate)
        {
            return [.. _pending];
        }
    }

    /// <summary>取走全部待发项;队列随即清空。</summary>
    public IReadOnlyList<SteeringMessage> DrainAll()
    {
        lock (_gate)
        {
            SteeringMessage[] all = [.. _pending];
            _pending.Clear();
            return all;
        }
    }

    /// <summary>
    /// 取走全部待发项并并成一条 —— 一轮已经结束、这些插话谁也没赶上时,
    /// 它们该作为<b>下一轮</b>整体发出去,而不是一条一轮地排队跑。
    /// </summary>
    /// <returns>队列为空时返回 <see langword="null" />。</returns>
    public SteeringMessage? DrainMerged()
    {
        IReadOnlyList<SteeringMessage> all = DrainAll();
        return all.Count switch
        {
            0 => null,
            1 => all[0],
            _ => new SteeringMessage(
                string.Join("\n\n", all.Select(item => item.DisplayText)),
                string.Join("\n\n", all.Select(item => item.RawText)),
                new ChatMessage(ChatRole.User, [.. all.SelectMany(item => item.Message.Contents)]))
        };
    }
}

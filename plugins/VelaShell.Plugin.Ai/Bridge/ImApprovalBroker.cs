using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Bridge;

/// <summary>
/// 把审批搬到 IM 里:发一条"要不要放行",等有权限的人回一个字。
/// </summary>
/// <remarks>
/// <b>为什么是文本而不是交互卡片。</b>四家平台的卡片回传各有各的坑 ——
/// 飞书的 <c>card.action.trigger</c> 在长连接上并不总能收到(上游项目也挂着同样的 issue),
/// 企微的卡片回调又得走公网。文本回复是唯一在四家都稳的通道,所以它是底线实现;
/// 卡片按钮以后按渠道能力逐个加,加上了也只是把同一个 <see cref="Resolve" /> 换个触发方式。
///
/// <para>一个聊天同一时刻只会有一个待批项 —— 一个聊天的多轮本来就是串行的
/// (见 <see cref="BridgeConversation.Gate" />),所以用聊天键做主键就够了。</para>
/// </remarks>
public sealed class ImApprovalBroker(ChannelHub hub, IPluginContext context)
{
    private sealed record Pending(
        BridgeConversation Conversation,
        ApprovalRequest Request,
        TaskCompletionSource<bool> Completion,
        List<string> Approvers);

    private readonly Dictionary<string, Pending> _pending = [];
    private readonly Lock _sync = new();

    /// <summary>当前有没有人在等审批(设置页的状态用)。</summary>
    public int PendingCount
    {
        get
        {
            lock (_sync)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>
    /// 发一条审批请求到聊天里并等回复。超时按<b>拒绝</b>处理 ——
    /// 没人应答时,不动手才是安全的那一侧。
    /// </summary>
    public async Task<bool> RequestAsync(BridgeConversation conversation, ChannelConfig config,
        ApprovalRequest request, int timeoutSeconds, Loc loc, CancellationToken cancellationToken)
    {
        if (request.RepeatKey is { } key && conversation.AlwaysApproved.Contains(key))
        {
            return true;
        }
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new Pending(conversation, request, completion, Approvers(config));
        lock (_sync)
        {
            // 上一条还悬着(理论上不会,一个聊天串行)——把它按拒绝收掉,免得回复对错了对象
            if (_pending.TryGetValue(conversation.ChatKey, out Pending? stale))
            {
                stale.Completion.TrySetResult(false);
            }
            _pending[conversation.ChatKey] = pending;
        }
        string always = request.RepeatKey is null ? "" : loc["BridgeApprovalAlways"];
        await hub.SendAsync(conversation.ChannelId, conversation.Reply,
            loc.F("BridgeApprovalAsk", request.Kind, Trim(request.Detail), always, timeoutSeconds),
            cancellationToken).ConfigureAwait(false);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 10, 3600)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        using (linked.Token.Register(() => completion.TrySetResult(false)))
        {
            bool granted = await completion.Task.ConfigureAwait(false);
            lock (_sync)
            {
                if (_pending.TryGetValue(conversation.ChatKey, out Pending? current) && current == pending)
                {
                    _pending.Remove(conversation.ChatKey);
                }
            }
            if (!granted && timeout.IsCancellationRequested)
            {
                await hub.SendAsync(conversation.ChannelId, conversation.Reply, loc["BridgeApprovalTimedOut"],
                    CancellationToken.None).ConfigureAwait(false);
            }
            return granted;
        }
    }

    /// <summary>
    /// 看这条消息是不是在回审批。是就地消化掉并返回 true(不再送去 agent)。
    /// </summary>
    public bool TryConsume(InboundMessage message, ChannelConfig config, Loc loc)
    {
        Pending? pending;
        lock (_sync)
        {
            if (!_pending.TryGetValue(message.ChatKey, out pending))
            {
                return false;
            }
        }
        if (ParseVerdict(message.Text) is not { } verdict)
        {
            return false; // 不是 y/n/a,当普通消息处理(可能是补充说明)
        }
        if (pending.Approvers.Count > 0 && !pending.Approvers.Contains(message.UserId))
        {
            // 不是审批人:告诉他一声,但**不消费** —— 他说的话仍然是给 agent 的
            _ = hub.SendAsync(message.ChannelId, new OutboundTarget(message.ChatId, message.ThreadId),
                loc["BridgeApprovalNotAllowed"], CancellationToken.None);
            return true;
        }
        if (verdict == Verdict.Always && pending.Request.RepeatKey is { } key)
        {
            pending.Conversation.AlwaysApproved.Add(key);
        }
        Resolve(message.ChatKey, verdict != Verdict.Deny);
        _ = hub.SendAsync(message.ChannelId, new OutboundTarget(message.ChatId, message.ThreadId),
            verdict == Verdict.Deny ? loc["BridgeApprovalDenied"] : loc["BridgeApprovalGranted"],
            CancellationToken.None);
        context.Log.Info($"Bridge: {message.UserName} {(verdict == Verdict.Deny ? "denied" : "approved")} " +
                         $"{pending.Request.Kind} in {message.ChatKey}.");
        return true;
    }

    /// <summary>放行 / 拒绝一条待批项(卡片按钮以后也走这里)。</summary>
    public void Resolve(string chatKey, bool granted)
    {
        lock (_sync)
        {
            if (_pending.Remove(chatKey, out Pending? pending))
            {
                pending.Completion.TrySetResult(granted);
            }
        }
    }

    /// <summary>丢掉某聊天的待批项(会话重置 / 渠道停掉时)。</summary>
    public void Cancel(string chatKey) => Resolve(chatKey, false);

    private enum Verdict
    {
        Allow,
        Deny,
        Always
    }

    private static Verdict? ParseVerdict(string text) => text.Trim().ToLowerInvariant() switch
    {
        "y" or "yes" or "ok" or "同意" or "批准" or "允许" or "放行" or "可以" => Verdict.Allow,
        "n" or "no" or "拒绝" or "不" or "不行" or "算了" => Verdict.Deny,
        "a" or "always" or "总是" or "以后都行" => Verdict.Always,
        _ => null
    };

    /// <summary>谁能批:优先 <see cref="ChannelConfig.Approvers" />,没配就回落到能说话的人。</summary>
    private static List<string> Approvers(ChannelConfig config)
        => config.Approvers.Count > 0 ? config.Approvers : config.AllowedUsers;

    /// <summary>审批详情发进 IM 前先截一刀 —— 一段几百行的文件内容不该刷屏。</summary>
    private static string Trim(string detail)
        => detail.Length <= 800 ? detail : string.Concat(detail.AsSpan(0, 800), "…");
}

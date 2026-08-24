using Microsoft.Extensions.AI;

namespace VelaShell.Plugin.Ai.Chat;

/// <summary>
/// 插话通道:把 <see cref="SteeringQueue" /> 里排着的用户消息,赶在模型<b>下一步</b>之前
/// 追加到即将发出的那份上下文末尾。
/// </summary>
/// <remarks>
/// <para>
/// 它必须垫在函数调用循环(<c>FunctionInvokingChatClient</c>)<b>里面</b>:那个循环一轮里会向模型
/// 发起多次请求(每调用一次工具就再问一次),每一次都要穿过这一层。于是用户在 Agent 干活时补的
/// 一句"顺便把日志也看一下",能在它读完当前工具结果、决定下一步<b>之前</b>进到上下文里 ——
/// 这正是 ClaudeCode 那种"边跑边补"的手感。垫在循环外面则只有整轮开头那一次机会,补充信息
/// 要等下一轮才生效。
/// </para>
/// <para>
/// 不打断进行中的那一步:已经发出去的请求照常读完,工具照常执行完。插话是"下一步的输入",
/// 不是中断信号 —— 中断有停止按钮。
/// </para>
/// </remarks>
/// <param name="innerClient">被包住的客户端(真正发请求的那个)。</param>
/// <param name="queue">面板级的插话队列。</param>
/// <param name="onDelivered">
/// 有插话真的被送进请求时回调(<b>在发请求的那个线程上</b>,通常不是 UI 线程)。
/// 面板据此把它补进对话历史、在回复里挂一张卡、并把输入框上方那枚排队芯片撤掉。
/// </param>
internal sealed class SteeringChatClient(
    IChatClient innerClient,
    SteeringQueue queue,
    Action? onDelivered = null) : DelegatingChatClient(innerClient)
{
    private readonly Lock _gate = new();
    private readonly List<SteeringMessage> _delivered = [];

    /// <summary>本轮已经送进请求的插话(按送达先后)。</summary>
    public IReadOnlyList<SteeringMessage> Delivered
    {
        get
        {
            lock (_gate)
            {
                return [.. _delivered];
            }
        }
    }

    /// <inheritdoc />
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetResponseAsync(Compose(messages), options, cancellationToken);

    /// <inheritdoc />
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(Compose(messages), options, cancellationToken);

    /// <summary>把队列里的插话并进这一次要发的消息序列。</summary>
    private List<ChatMessage> Compose(IEnumerable<ChatMessage> messages)
    {
        List<ChatMessage> outgoing = [.. messages];
        IReadOnlyList<SteeringMessage> fresh = queue.DrainAll();
        SteeringMessage[] carried;
        lock (_gate)
        {
            _delivered.AddRange(fresh);
            carried = [.. _delivered];
        }
        // 循环把上下文<b>整份</b>重发时(绝大多数情况),之前送过的插话也得跟着重发 ——
        // 那份历史是循环自己攒的,它并不知道我们往里加过东西,不补就等于说过的话下一步就没了。
        // 而服务端替我们存着会话时(OpenAI Responses 那条路),循环只把"这一步新增的"发下来
        // (连系统提示词都不在里头),已送达的插话服务端已经有了,再补一遍就是重复。
        bool fullHistory = outgoing.Exists(message => message.Role == ChatRole.System);
        foreach (SteeringMessage item in fullHistory ? carried : fresh)
        {
            Append(outgoing, item.Message);
        }
        if (fresh.Count > 0)
        {
            onDelivered?.Invoke();
        }
        return outgoing;
    }

    /// <summary>
    /// 追加一条插话。末条<b>也是</b> user 时并进去而不是另起一条 ——
    /// Anthropic 协议要求角色交替,而适配器不会替你合并(与 <c>ContextBuilder</c> 同一个理由)。
    /// </summary>
    /// <remarks>
    /// 并进末条时插话排在原有内容<b>之后</b>:那条 user 往往装的是工具结果,
    /// 而 Anthropic 要求 <c>tool_result</c> 块排在消息内容的最前面,插到前面会直接被拒。
    /// </remarks>
    private static void Append(List<ChatMessage> sink, ChatMessage message)
    {
        if (sink.Count > 0 && sink[^1].Role == ChatRole.User)
        {
            sink[^1] = new ChatMessage(ChatRole.User, [.. sink[^1].Contents, .. message.Contents]);
            return;
        }
        sink.Add(message);
    }
}

using Microsoft.Extensions.AI;

namespace VelaShell.Plugin.Ai.Chat;

/// <summary>一次请求的上下文装配结果。</summary>
/// <param name="Messages">最终发出去的消息(系统提示词 + 摘要 + 裁剪并规整过的历史)。</param>
/// <param name="DroppedMessages">
/// 为放进窗口而<b>直接丢掉</b>的早期消息条数。这是压缩失败时的兜底手段 ——
/// 正常路径上早期内容会被折成摘要而不是丢弃(见 <see cref="ContextCompactor" />)。
/// </param>
/// <param name="EstimatedTokens">装配后的输入 token 估算值。</param>
public readonly record struct RequestContext(
    List<ChatMessage> Messages,
    int DroppedMessages,
    int EstimatedTokens);

/// <summary>
/// 把"系统提示词 + 对话历史"装配成一次请求真正要发的消息序列:先按上下文窗口裁掉最早的几轮,
/// 再把相邻同角色的消息并成一条。纯函数、不碰 UI,所以能直接单测。
/// </summary>
public static class ContextBuilder
{
    /// <summary>无论怎么裁,末尾这些条数一定留着 —— 裁到只剩系统提示词就没法对话了。</summary>
    private const int AlwaysKeep = 4;

    /// <summary>每条消息的固定开销(角色、分隔符之类),各家协议都有,量级一致。</summary>
    private const int PerMessageOverhead = 4;

    /// <summary>
    /// 装配请求。<paramref name="windowTokens" /> ≤ 0(用户没填上下文窗口)时不裁剪 ——
    /// 宁可让服务端报"超长",也不擅自丢用户的上下文。
    /// </summary>
    /// <param name="systemPrompt">本轮的系统提示词。</param>
    /// <param name="history">对话历史(不含系统提示词);<b>本方法不修改它</b>。</param>
    /// <param name="windowTokens">模型的上下文窗口(接入配置里的"最大输入 tokens")。</param>
    /// <param name="reserveTokens">给回复留出的余量(通常就是最大输出 tokens)。</param>
    /// <param name="summary">早期对话的摘要(空 = 没压缩过);它替代 <paramref name="summarizedThrough" /> 之前的那些消息。</param>
    /// <param name="summarizedThrough">摘要覆盖到 <c>history</c> 的哪个下标为止(不含)。</param>
    public static RequestContext Build(string systemPrompt, IReadOnlyList<ChatMessage> history,
        int windowTokens, int reserveTokens, string summary = "", int summarizedThrough = 0)
    {
        ArgumentNullException.ThrowIfNull(history);
        var system = new ChatMessage(ChatRole.System, systemPrompt);
        int from = Math.Clamp(summarizedThrough, 0, history.Count);
        bool hasSummary = summary.Length > 0 && from > 0;
        // 摘要以一条 user 消息的身份排在最前:system 每轮都重建、放不住它,
        // 而 user 这个角色在所有协议里都能安全地打头。
        ChatMessage? digest = hasSummary ? new ChatMessage(ChatRole.User, summary) : null;

        int start = from;
        if (windowTokens > 0)
        {
            // 留出回复的余量,再留 10% 给估算误差 —— 估出来的 token 不可能和服务端完全一致
            int budget = (int)((windowTokens - Math.Max(0, reserveTokens)) * 0.9);
            int fixedCost = Estimate(system) + (digest is null ? 0 : Estimate(digest));
            start = FirstIndexThatFits(fixedCost, history, from, Math.Max(budget, 0));
        }

        // 没有摘要打头时,发出去的第一条必须是 user
        if (digest is null)
        {
            start = FirstUserFrom(history, start);
        }

        var messages = new List<ChatMessage>(history.Count - start + 2) { system };
        if (digest is not null)
        {
            messages.Add(digest);
        }
        AppendNormalized(messages, history, start);
        return new RequestContext(messages, start - from, Estimate(messages));
    }

    /// <summary>
    /// 从 <paramref name="start" /> 起找第一条用户消息;找不到就<b>原样返回</b>。
    /// </summary>
    /// <remarks>
    /// <b>Anthropic 协议要求 messages 的第一条是 user</b>(system 单独走一个字段),
    /// 否则整个请求 400。切点本来只落在用户消息上,但 <see cref="AlwaysKeep" /> 是硬底线,
    /// 顶到它时会停在半轮中间 —— 那时第一条就可能是 assistant 或工具结果。
    /// 摘要在场时不必管:摘要本身就是一条 user,由它打头。
    ///
    /// <para>一条 user 都没有(整段窗口全是 assistant/工具消息)时不动 —— 那时无论怎么切都不合法,
    /// 与其把上下文清空,不如原样发出去让服务端报准确的错。</para>
    /// </remarks>
    private static int FirstUserFrom(IReadOnlyList<ChatMessage> history, int start)
    {
        for (int i = start; i < history.Count; i++)
        {
            if (history[i].Role == ChatRole.User)
            {
                return i;
            }
        }
        return start;
    }

    /// <summary>
    /// 从最早往后找第一个"留下之后就装得下"的起点。只在<b>用户消息</b>处切 ——
    /// 从 assistant 或工具结果中间切会留下没有来由的半截上下文,
    /// 更糟的是把工具调用和它的结果拆开(有些协议直接报错)。
    /// </summary>
    private static int FirstIndexThatFits(int fixedCost, IReadOnlyList<ChatMessage> history, int from, int budget)
    {
        int total = fixedCost + Estimate(history, from);
        int lastCuttable = Math.Max(from, history.Count - AlwaysKeep);
        int start = from;
        while (total > budget && start < lastCuttable)
        {
            total -= Estimate(history[start]);
            start++;
            // 切到下一条用户消息为止,别把一轮拦腰截断
            while (start < lastCuttable && history[start].Role != ChatRole.User)
            {
                total -= Estimate(history[start]);
                start++;
            }
        }
        return start;
    }

    /// <summary>
    /// 追加历史,并做两件规整:丢掉<b>落单的工具调用/结果</b>,再把<b>相邻同角色</b>的消息并成一条。
    /// </summary>
    /// <remarks>
    /// 合并的理由:用户按停止之后,那条没有得到回复的 user 消息会留在历史里,
    /// 下一轮就是两条挨着的 user。实测 Anthropic 适配器<b>不会</b>替你合并,原样发两条,
    /// 而 Anthropic 协议要求角色交替。与其在每个可能产生该状态的地方各补一次,
    /// 不如在唯一的出口这里兜住 —— 配对也是同一个道理。
    /// </remarks>
    private static void AppendNormalized(List<ChatMessage> sink, IReadOnlyList<ChatMessage> history, int start)
    {
        (HashSet<string> calls, HashSet<string> results) = CollectCallIds(history, start);
        for (int i = start; i < history.Count; i++)
        {
            ChatMessage message = history[i];
            IReadOnlyList<AIContent> contents = Paired(message, calls, results);
            if (contents.Count == 0)
            {
                continue; // 整条只剩落单的调用/结果,发出去只会被服务端拒
            }
            if (sink.Count > 1 && sink[^1].Role == message.Role)
            {
                // 并成新的一条,不要就地改 —— sink[^1] 可能就是 history 里那个对象
                sink[^1] = new ChatMessage(message.Role, [.. sink[^1].Contents, .. contents]);
                continue;
            }
            sink.Add(ReferenceEquals(contents, message.Contents)
                ? message
                : new ChatMessage(message.Role, [.. contents]));
        }
    }

    /// <summary>窗口内出现过的工具调用 id 与工具结果 id。</summary>
    private static (HashSet<string> Calls, HashSet<string> Results) CollectCallIds(
        IReadOnlyList<ChatMessage> history, int start)
    {
        HashSet<string> calls = [], results = [];
        for (int i = start; i < history.Count; i++)
        {
            foreach (AIContent content in history[i].Contents)
            {
                switch (content)
                {
                    case FunctionCallContent call:
                        calls.Add(call.CallId);
                        break;
                    case FunctionResultContent result:
                        results.Add(result.CallId);
                        break;
                }
            }
        }
        return (calls, results);
    }

    /// <summary>
    /// 去掉这条消息里落单的工具内容:结果没有对应的调用、或调用没有对应的结果。
    /// 没有落单的就原样返回(不分配)。
    /// </summary>
    /// <remarks>
    /// <b>这不是洁癖,是两家协议都会直接报错的东西</b>:
    /// OpenAI Responses 回 <c>400 No tool call found for tool output with call_id …</c>,
    /// Anthropic 回 <c>tool_use ids must have corresponding tool_result</c>。
    /// 落单在正常使用中真的会出现,两个方向都有:
    /// <list type="bullet">
    /// <item>
    /// <b>结果落单</b> —— 裁剪把调用切掉了。切点本来只落在用户消息上,但
    /// <see cref="AlwaysKeep" /> 是硬底线,顶到它时切点会停在半轮中间。
    /// </item>
    /// <item>
    /// <b>调用落单</b> —— 模型发起调用后用户按了停止(或审批未答就取消),工具从未执行;
    /// 半截回复是有意留在历史里的(用户看得见,模型也该知道自己说过什么)。
    /// </item>
    /// </list>
    /// 丢掉落单的那一半,比整轮丢弃保住的上下文多,也比原样发出去强。
    /// </remarks>
    private static IReadOnlyList<AIContent> Paired(ChatMessage message, HashSet<string> calls, HashSet<string> results)
    {
        bool orphaned = false;
        foreach (AIContent content in message.Contents)
        {
            if (IsOrphan(content, calls, results))
            {
                orphaned = true;
                break;
            }
        }
        if (!orphaned)
        {
            return (IReadOnlyList<AIContent>)message.Contents;
        }
        return [.. message.Contents.Where(c => !IsOrphan(c, calls, results))];
    }

    private static bool IsOrphan(AIContent content, HashSet<string> calls, HashSet<string> results)
        => content switch
        {
            FunctionCallContent call => !results.Contains(call.CallId),
            FunctionResultContent result => !calls.Contains(result.CallId),
            _ => false
        };

    /// <summary>
    /// 把打头那条 <c>system</c> 消息从列表里摘出来,返回它的文本。
    /// </summary>
    /// <remarks>
    /// 给"不收 system 角色"的端点用(ChatGPT 的 Codex 后端就是,照发会 400)。
    /// 摘出来的内容由调用方放进 <c>ChatOptions.Instructions</c> —— 一个字都不会少,只是换了位置。
    /// <para>
    /// <b>只摘打头那条</b>:装配出来的列表里 system 必定在最前(见 <see cref="Build" />),
    /// 而后面若还有 system,那是历史里带来的、模型自己也认得的内容,不该被悄悄吞掉。
    /// </para>
    /// </remarks>
    /// <param name="messages">装配好的消息列表;命中时<b>就地修改</b>。</param>
    /// <returns>系统提示词;列表不以 system 打头时返回 null。</returns>
    public static string? MoveSystemPromptOut(List<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0 || messages[0].Role != ChatRole.System)
        {
            return null;
        }
        string text = messages[0].Text;
        messages.RemoveAt(0);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>整段消息的 token 估算。</summary>
    public static int Estimate(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        int total = 0;
        foreach (ChatMessage message in messages)
        {
            total += Estimate(message);
        }
        return total;
    }

    private static int Estimate(IReadOnlyList<ChatMessage> messages, int from)
    {
        int total = 0;
        for (int i = from; i < messages.Count; i++)
        {
            total += Estimate(messages[i]);
        }
        return total;
    }

    /// <summary>单条消息的 token 估算(含固定开销)。</summary>
    public static int Estimate(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        int total = PerMessageOverhead;
        foreach (AIContent content in message.Contents)
        {
            total += content switch
            {
                TextContent text => Estimate(text.Text),
                TextReasoningContent reasoning => Estimate(reasoning.Text),
                FunctionCallContent call => Estimate(call.Name) + EstimateArguments(call),
                FunctionResultContent result => Estimate(result.Result?.ToString()),
                // 图片按一张中等尺寸图的常见量级算;精确值各家不同,这里只求量级对
                DataContent => 800,
                _ => 8
            };
        }
        return total;
    }

    private static int EstimateArguments(FunctionCallContent call)
    {
        if (call.Arguments is not { Count: > 0 } arguments)
        {
            return 0;
        }
        int total = 0;
        foreach (KeyValuePair<string, object?> pair in arguments)
        {
            total += Estimate(pair.Key) + Estimate(pair.Value?.ToString());
        }
        return total;
    }

    /// <summary>
    /// 文本的 token 估算。没有 tokenizer,按字符类别近似:
    /// ASCII 约 4 字符 1 token,中日韩等非 ASCII 约 1.5 字符 1 token。
    /// 只用来决定"要不要裁",偏差一两成不影响判断。
    /// </summary>
    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }
        int ascii = 0, wide = 0;
        foreach (char c in text)
        {
            if (char.IsAscii(c))
            {
                ascii++;
            }
            else
            {
                wide++;
            }
        }
        return (int)((ascii / 4.0) + (wide / 1.5)) + 1;
    }
}

using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;

namespace VelaShell.Plugin.Ai.Chat;

/// <summary>
/// Anthropic 的提示词缓存(prompt caching)断点。给请求里的内容块打上
/// <c>cache_control</c>,服务端就会把"到这一块为止"的整段前缀缓存下来;
/// 下一轮只要前缀没变就命中,那部分输入按缓存价计费(便宜一个数量级)。
/// </summary>
/// <remarks>
/// <b>断点放哪(两处,协议上限是 4 处)</b>:
/// <list type="number">
/// <item>系统提示词的末尾 —— 整段对话里最稳定的一截。</item>
/// <item>
/// 本轮最后一条消息的末尾 —— 滚动断点。这一轮写缓存,下一轮历史只在其后追加,
/// 于是整个前缀命中。<c>@</c> 引用把整份文件塞进消息的场合,省的就是这些 token。
/// </item>
/// </list>
///
/// <b>为什么每轮都要先清</b>:<c>_history</c> 里的 <see cref="AIContent" /> 对象是跨轮复用的,
/// 标记打在对象上。不清就会一轮攒一个,几轮之后撞上"最多 4 个 cache_control 块"的硬限制,
/// 直接报错。清除就是把标记设回 null(实测会把 <c>AdditionalProperties</c> 里的
/// <c>anthropic:cache_control</c> 键删掉)。
///
/// <b>成本</b>:写缓存比普通输入贵 25%,但短于最小可缓存长度(约 1024 token)的前缀
/// 服务端直接忽略标记、既不缓存也不加价 —— 所以短对话不会白花钱,长对话才开始省。
/// </remarks>
public static class PromptCache
{
    /// <summary>先清掉上一轮的断点,再按"系统提示词 + 末条消息"打两处新的。</summary>
    public static void Apply(IReadOnlyList<ChatMessage> requestMessages)
    {
        ArgumentNullException.ThrowIfNull(requestMessages);
        Clear(requestMessages);
        if (requestMessages.Count == 0)
        {
            return;
        }
        foreach (ChatMessage message in requestMessages)
        {
            if (message.Role == ChatRole.System)
            {
                Mark(message);
            }
        }
        Mark(requestMessages[^1]);
    }

    /// <summary>抹掉这些消息上的全部缓存断点。</summary>
    public static void Clear(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        foreach (ChatMessage message in messages)
        {
            foreach (AIContent content in message.Contents)
            {
                content.WithCacheControl(default(CacheControlEphemeral?));
            }
        }
    }

    /// <summary>在这条消息的最后一个内容块上打断点(缓存的是"到此为止"的整段前缀)。</summary>
    private static void Mark(ChatMessage message)
    {
        // 思考块不支持 cache_control(SDK 文档明说),历史里回放的推理内容跳过
        for (int i = message.Contents.Count - 1; i >= 0; i--)
        {
            if (message.Contents[i] is TextReasoningContent)
            {
                continue;
            }
            message.Contents[i].WithCacheControl(new CacheControlEphemeral());
            return;
        }
    }
}

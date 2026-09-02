using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Bridge;

/// <summary>
/// 一个 IM 聊天(群或单聊)在桥接这边的状态:上下文、绑定的服务器、正在跑的那一轮。
/// </summary>
/// <remarks>
/// 一个聊天一份,<b>串行</b>跑 —— <see cref="Gate" /> 保证同一个群里两个人同时发问时
/// 后一条排队而不是插进上一轮的工具循环中间。跨群则各跑各的(总并发另有上限)。
/// </remarks>
public sealed class BridgeConversation(string channelId, string chatId)
{
    /// <summary>会话键(<c>渠道 id/聊天 id</c>)。</summary>
    public string ChatKey { get; } = $"{channelId}/{chatId}";

    /// <summary>所属渠道实例 id。</summary>
    public string ChannelId { get; } = channelId;

    /// <summary>聊天 id。</summary>
    public string ChatId { get; } = chatId;

    /// <summary>回消息的落点。</summary>
    public OutboundTarget Reply => new(ChatId, ThreadId);

    /// <summary>话题 id(平台支持话题时,回到同一串里)。</summary>
    public string? ThreadId { get; set; }

    /// <summary>历史库里的会话 id(翻回旧对话时用)。</summary>
    public string ConversationId { get; set; } = Chat.ChatHistoryStore.NewConversationId();

    /// <summary>送给模型的对话历史(不含系统提示词)。</summary>
    public List<ChatMessage> History { get; } = [];

    /// <summary>绑定的服务器(<c>user@host:port</c>;空 = 用渠道的默认绑定)。</summary>
    public string BoundTarget { get; set; } = "";

    /// <summary>本聊天单独设的模式(null = 跟随桥接设置)。</summary>
    public ChatMode? ModeOverride { get; set; }

    /// <summary>同一个聊天里的多条消息串行处理。</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>最后一次活动时刻(闲置回收看它)。</summary>
    public DateTimeOffset LastActivity { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>本会话内"总是允许"过的操作键(见 <c>ApprovalRequest.RepeatKey</c>)。</summary>
    public HashSet<string> AlwaysApproved { get; } = new(StringComparer.Ordinal);

    /// <summary>会话创建时刻(历史库里那个"每会话一个点"的摘要用它当时间戳)。</summary>
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>已落库的消息条数(= 下一条的序号)。</summary>
    public int PersistedCount { get; set; }

    /// <summary>正在跑的那一轮的取消源(<c>/stop</c> 用它掐掉)。</summary>
    public CancellationTokenSource? Running { get; set; }

    /// <summary>丢掉上下文,开一段新的。</summary>
    public void Reset()
    {
        History.Clear();
        PersistedCount = 0;
        CreatedAt = DateTimeOffset.UtcNow;
        AlwaysApproved.Clear();
        ConversationId = Chat.ChatHistoryStore.NewConversationId();
    }
}

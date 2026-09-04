using Avalonia.Controls;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Chat;

namespace VelaShell.Plugin.Ai.Ui;

public partial class ChatPanelView
{
    /// <summary>
    /// 一段独立对话的<b>全部</b>可变状态:历史、记账、在途一轮、插话、压缩、审批放行记忆,
    /// 以及它自己那条消息流面板(<see cref="Messages" />)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 按会话各持一份 —— 键是顶栏下拉选中的那台机器的会话 id,<c>""</c> 是"不绑机器"的通用对话。
    /// 切下拉即把 <see cref="_active" /> 换成对应的这一份、并把它的 <see cref="Messages" />
    /// 挂进 <c>ChatScroll</c>:切换是<b>换引用</b>,不是重建,所以即时、也不串台。
    /// </para>
    /// <para>
    /// 后台并行靠的也是它:一轮开始时用 <see cref="_turnScope" /> 记下"这一轮属于哪一份",
    /// 那之后被 await 串起来的整条流水线(渲染、记账、插话、入库)都落到<b>这一份</b>上,
    /// 而不是"当前正显示的那一份"。于是切走之后它照样把话答完,气泡就长在自己那条(此刻不可见的)
    /// 面板里,切回来即见,谁也不打断谁。
    /// </para>
    /// </remarks>
    internal sealed class Conversation(string sessionKey, StackPanel messages)
    {
        /// <summary>这一份绑的是哪条会话(会话的不透明 id);<c>""</c> = 不绑机器的通用对话。</summary>
        public string SessionKey { get; } = sessionKey;

        /// <summary>本对话自己的消息流面板。切到本对话时它被挂进 <c>ChatScroll</c>,平时(后台)脱离可视树留着。</summary>
        public StackPanel Messages { get; } = messages;

        // ---------- 历史与上下文 ----------
        public List<ChatMessage> History { get; } = [];
        public int TurnHistoryStart;
        public string ConversationId = ChatHistoryStore.NewConversationId();
        public DateTimeOffset ConversationStartedAt = DateTimeOffset.UtcNow;
        public int PersistedCount;
        public int DroppedFromContext;
        public int SequenceHighWater;

        // ---------- 记账 ----------
        public long TotalInputTokens;
        public long TotalOutputTokens;
        public long TotalReasoningTokens;
        public long LastInputTokens;
        public long LastCachedInputTokens;
        public long TotalCachedInputTokens;
        public long TotalCacheWriteTokens;

        // ---------- 在途一轮 ----------
        public CancellationTokenSource? Cts;
        public bool Busy;
        public AssistantBubble? ActiveBubble;

        /// <summary>本段对话是否已提示过"没请求思考"(一次就够)。</summary>
        public bool ThinkingHintShown;

        // ---------- 插话(边跑边补)----------
        public SteeringQueue SteeringQueue { get; } = new();
        public SteeringChatClient? Steering;
        public int SteeringCommitted;

        // ---------- 上下文压缩 ----------
        public string ContextSummary = "";
        public int SummarizedThrough;

        // ---------- 审批放行记忆(仅本段对话)----------
        public HashSet<string> AlwaysApproved { get; } = [with(StringComparer.Ordinal)];

        // ---------- 消息窗口(折叠早期气泡)----------
        public List<Control> CollapsedMessages { get; } = [];
        public Border? CollapsedBanner;

        // ---------- 用户气泡 → 历史下标(编辑/截断用)----------
        public Dictionary<Control, int> UserBubbleIndex { get; } = [];

        /// <summary>顶栏状态行的当前内容 —— 后台轮次写这里,切回本对话时照原样贴回共享的状态行。</summary>
        public string Status = "";
    }
}

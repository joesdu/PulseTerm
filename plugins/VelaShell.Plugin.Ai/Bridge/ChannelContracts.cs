namespace VelaShell.Plugin.Ai.Bridge;

/// <summary>渠道连接状态(设置页的状态灯用)。</summary>
public enum ChannelState
{
    /// <summary>没在跑(未启用,或桥接总开关关着)。</summary>
    Stopped,

    /// <summary>正在建连或重连。</summary>
    Connecting,

    /// <summary>已连上,能收消息。</summary>
    Connected,

    /// <summary>连不上,正在按退避重试(<see cref="ChannelStatus.Detail" /> 是最后一次的错因)。</summary>
    Faulted
}

/// <summary>一个渠道此刻的状态。</summary>
/// <param name="ChannelId">渠道实例 id。</param>
/// <param name="State">状态。</param>
/// <param name="Detail">出错时的错因;正常时为 null。</param>
/// <param name="ChangedAt">进入该状态的时刻。</param>
public readonly record struct ChannelStatus(string ChannelId, ChannelState State, string? Detail, DateTimeOffset ChangedAt);

/// <summary>
/// 一条从 IM 收到的消息。各平台的信封在渠道实现里就剥掉了,
/// 到桥接核心手上只剩这几项 —— 核心不认识任何平台。
/// </summary>
/// <param name="ChannelId">来自哪个渠道实例。</param>
/// <param name="ChatId">群 / 单聊 id(回消息就发回这里)。</param>
/// <param name="IsGroup">是不是群聊。</param>
/// <param name="UserId">发送者 id(白名单与审批人比对的就是它)。</param>
/// <param name="UserName">发送者显示名(只用于日志与回帖里的称呼)。</param>
/// <param name="Text">纯文本正文(@机器人 的部分已剔除)。</param>
/// <param name="MessageId">平台消息 id(去重与"回复这条"用)。</param>
/// <param name="MentionsBot">群里是不是 @ 了机器人。单聊恒为 true。</param>
/// <param name="ThreadId">话题 / 回复线程 id(平台支持时)。</param>
public sealed record InboundMessage(
    string ChannelId,
    string ChatId,
    bool IsGroup,
    string UserId,
    string UserName,
    string Text,
    string MessageId,
    bool MentionsBot,
    string? ThreadId = null)
{
    /// <summary>会话键:同一个渠道下的同一个聊天,共用一份上下文。</summary>
    public string ChatKey => $"{ChannelId}/{ChatId}";
}

/// <summary>回消息的落点。</summary>
/// <param name="ChatId">群 / 单聊 id。</param>
/// <param name="ThreadId">话题 id(平台支持时,回到同一话题里)。</param>
public readonly record struct OutboundTarget(string ChatId, string? ThreadId = null);

/// <summary>渠道能做什么。桥接据此决定进度提示是"改同一条"还是"再发一条"。</summary>
/// <param name="CanEdit">能不能改已发出的消息。</param>
/// <param name="MaxMessageChars">单条消息的字符上限(超出由桥接切段)。</param>
public readonly record struct ChannelCapabilities(bool CanEdit, int MaxMessageChars = 4000);

/// <summary>
/// 一个 IM 渠道。<b>只管收发,不认识 agent</b> —— 谁能说话、说了要不要干活,
/// 全在 <see cref="ConversationRouter" /> 那一层。
/// </summary>
/// <remarks>
/// <see cref="RunAsync" /> 的约定是"跑到断开为止":正常断开就返回,出错就抛。
/// 重连退避统一由 <see cref="ChannelHub" /> 做 —— 四个平台的入站传输各不相同
/// (长连接 / Stream / 长轮询 / 回调监听),但"断了要重来"的策略只该有一份。
/// </remarks>
public interface IMessageChannel : IAsyncDisposable
{
    /// <summary>渠道实例 id(= <see cref="ChannelConfig.Id" />)。</summary>
    string Id { get; }

    /// <summary>平台。</summary>
    ChannelKind Kind { get; }

    /// <summary>显示名。</summary>
    string Label { get; }

    /// <summary>能力。</summary>
    ChannelCapabilities Capabilities { get; }

    /// <summary>握手成功时触发一次(状态灯转绿,并把重连退避复位)。</summary>
    event Action? Connected;

    /// <summary>跑到断开为止。取消即正常收摊,其它异常交给上层重连。</summary>
    /// <param name="onMessage">收到消息时的回调(桥接保证它不抛)。</param>
    /// <param name="cancellationToken">停止信号。</param>
    Task RunAsync(Func<InboundMessage, Task> onMessage, CancellationToken cancellationToken);

    /// <summary>发一条文本消息,返回平台消息 id(拿不到则返回 null)。</summary>
    Task<string?> SendAsync(OutboundTarget target, string text, CancellationToken cancellationToken);

    /// <summary>改掉之前发出的一条消息。<see cref="ChannelCapabilities.CanEdit" /> 为 false 时不会被调用。</summary>
    Task EditAsync(OutboundTarget target, string messageId, string text, CancellationToken cancellationToken);
}

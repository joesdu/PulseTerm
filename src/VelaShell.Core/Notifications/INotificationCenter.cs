using VelaShell.Core.Models;

namespace VelaShell.Core.Notifications;

/// <summary>
/// 消息中心(侧边栏铃铛):汇总要留存、可回看的消息 —— 有新版本了、订阅源发了篇公告。
/// <para>
/// 与运行时告警的分工是明确的:主机指纹变了、会话断了,那些要的是**当场打断**
/// (弹窗 / 状态栏 / 标签闪烁),已各有归宿;塞进这里只会把真正要读的东西淹掉。
/// </para>
/// </summary>
public interface INotificationCenter
{
    /// <summary>当前消息列表快照,按发布时间倒序;已过期的不在其中。</summary>
    IReadOnlyList<NotificationItem> Items { get; }

    /// <summary>未读条数(铃铛角标)。</summary>
    int UnreadCount { get; }

    /// <summary>列表或已读状态变化(任意线程触发,订阅方自行调度到 UI)。</summary>
    event Action? Changed;

    /// <summary>从存储载入上次运行留下的消息,并顺手清掉过期与超量的部分。</summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 投递一批消息。<see cref="NotificationItem.Id" /> 已存在的会被跳过,
    /// **且保留原有的已读状态** —— 每次启动都重新投递同一条"有新版本"时,
    /// 不该把用户读过的又变回未读。
    /// </summary>
    Task PublishAsync(IEnumerable<NotificationItem> items, CancellationToken cancellationToken = default);

    /// <summary>把一条标记为已读。</summary>
    Task MarkReadAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>全部标记为已读。</summary>
    Task MarkAllReadAsync(CancellationToken cancellationToken = default);

    /// <summary>删除一条。</summary>
    Task RemoveAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>清空全部消息。</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}

using VelaShell.Core.Models;
using VelaShell.Core.Notifications;
using VelaShell.Core.Resources;
using VelaShell.Core.Services;
using VelaShell.Infrastructure.Diagnostics;

namespace VelaShell.Services;

/// <summary>
/// 攒出这一轮该投进消息中心的条目:有新版本了、订阅源的公告、上次运行留下的崩溃记录。
/// </summary>
/// <remarks>
/// <para>
/// 从 <c>MainWindowViewModel</c> 拆出来的一簇(Q-01)。拆的是<b>决策</b>那一半 ——
/// 投不投、投什么、id 怎么起;而"接铃铛角标、起定时器、开面板"那一半是界面接线,
/// 留在视图模型里。混在一起时,想验一条"关掉推广之后促销条目不该进来"
/// 得先把三十个构造参数的主窗口造出来。
/// </para>
/// <para>
/// 整段对失败极度宽容:消息中心是锦上添花的东西,离线、源不可达、格式变了 ——
/// 任何一样都不该拦住应用启动,更不该让铃铛变得不可用。
/// </para>
/// </remarks>
public static class NotificationSources
{
    /// <summary>
    /// 收集这一轮要投的条目。
    /// </summary>
    /// <param name="settings">当前设置(决定投不投更新、要不要收促销)。</param>
    /// <param name="feed">订阅资讯源;null = 不取。</param>
    /// <param name="updateService">更新服务;null = 不查更新。</param>
    /// <returns>要投的条目;没有则为空列表。</returns>
    public static async Task<IReadOnlyList<NotificationItem>> CollectAsync(
        AppSettings settings,
        IAnnouncementFeed? feed,
        IUpdateService? updateService)
    {
        ArgumentNullException.ThrowIfNull(settings);
        List<NotificationItem> incoming = [];
        // 两个开关都要点头:「通知我有更新」是消息中心的偏好,「启动时检查更新」是
        // 更新功能自己的开关 —— 后者关掉时连网络请求都不该发。
        if (updateService is not null
            && settings.Notifications.NotifyUpdates
            && settings.General.CheckUpdatesOnStartup
            && await BuildUpdateNotificationAsync(updateService).ConfigureAwait(true) is { } update)
        {
            incoming.Add(update);
        }
        if (feed is not null)
        {
            IReadOnlyList<NotificationItem> fetched = await feed.FetchAsync().ConfigureAwait(true);
            incoming.AddRange(settings.Notifications.AllowPromotions
                                  ? fetched
                                  : fetched.Where(item => item.Kind != NotificationKind.Promotion));
        }
        if (BuildCrashNotification() is { } crash)
        {
            incoming.Add(crash);
        }
        return incoming;
    }

    /// <summary>
    /// 上次运行留下了未提示过的崩溃记录时,攒一条消息中心条目(带"打开日志目录"动作)。
    /// </summary>
    /// <remarks>
    /// 走消息中心而不是启动弹窗:用户刚打开应用是想干活,不是想读崩溃报告。
    /// <c>TryTakeUnseenCrash</c> 取走即标记已看,同一份崩溃不会每次启动都再提示一遍。
    /// </remarks>
    /// <returns>崩溃条目;没有未提示过的崩溃时为 null。</returns>
    public static NotificationItem? BuildCrashNotification()
    {
        if (!DiagnosticLog.TryTakeUnseenCrash(out string path))
        {
            return null;
        }
        string fileName = Path.GetFileName(path);
        return new()
        {
            Id = "crash:" + fileName,
            Kind = NotificationKind.System,
            Severity = NotificationSeverity.Warning,
            Title = Strings.Get("Notify_CrashTitle"),
            Body = Strings.Format("Notify_CrashBody", fileName),
            PublishedAt = DateTime.UtcNow,
            Link = new()
            {
                Label = Strings.Get("Notify_CrashAction"),
                CommandId = "app.logs.open"
            }
        };
    }

    /// <summary>
    /// 检查更新,有新版本就攒一条消息。
    /// </summary>
    /// <remarks>
    /// 商店版直接跳过:安装目录只读、更新由 Microsoft Store 接管,推一条"去关于页更新"
    /// 只会把用户送到一个什么也做不了的页面。
    /// </remarks>
    /// <param name="updateService">更新服务。</param>
    /// <returns>更新条目;无更新、离线或商店版时为 null。</returns>
    public static async Task<NotificationItem?> BuildUpdateNotificationAsync(IUpdateService updateService)
    {
        ArgumentNullException.ThrowIfNull(updateService);
        if (updateService.IsStoreManaged)
        {
            return null;
        }
        bool hasUpdate;
        try
        {
            hasUpdate = await updateService.CheckForUpdateAsync().ConfigureAwait(true);
        }
        catch
        {
            // 离线或更新源不可达是常态。
            return null;
        }
        if (!hasUpdate || updateService.AvailableVersion is not { Length: > 0 } available)
        {
            return null;
        }
        return new()
        {
            // id 里带上版本号:同一个版本每次启动都会重投,靠它去重并保住已读状态;
            // 真出了新版本则是一条新 id,会重新亮起未读。
            Id = $"update:{available}",
            Kind = NotificationKind.Update,
            Title = Strings.Format("Notify_UpdateTitle", available),
            Body = Strings.Format("Notify_UpdateBody", updateService.CurrentVersion ?? "?"),
            PublishedAt = DateTime.UtcNow,
            Link = new()
            {
                Label = Strings.Get("Notify_UpdateAction"),
                CommandId = "app.settings.about"
            }
        };
    }
}

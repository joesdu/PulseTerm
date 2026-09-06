using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Notifications;
using VelaShell.Core.Services;
using VelaShell.Services;

namespace VelaShell.Tests.Services;

/// <summary>
/// 决定这一轮往消息中心投什么。
/// </summary>
/// <remarks>
/// 这些判断原先埋在 <c>MainWindowViewModel</c> 里,想验一条"关掉推广之后促销条目不该进来"
/// 得先把三十个构造参数的主窗口造出来。拆成独立的决策之后每条规则都能单独摆上台面。
/// </remarks>
[TestClass]
[TestCategory("Notifications")]
public sealed class NotificationSourcesTests
{
    private static NotificationItem Item(string id, NotificationKind kind) =>
        new()
        {
            Id = id,
            Kind = kind,
            Title = id,
            PublishedAt = DateTime.UtcNow,
        };

    private static IAnnouncementFeed FeedWith(params NotificationItem[] items)
    {
        IAnnouncementFeed feed = Substitute.For<IAnnouncementFeed>();
        feed.FetchAsync(Arg.Any<CancellationToken>()).Returns(items);
        return feed;
    }

    [TestMethod]
    public async Task PromotionsAreDroppedWhenTheUserOptedOut()
    {
        IAnnouncementFeed feed = FeedWith(
            Item("news", NotificationKind.News),
            Item("sale", NotificationKind.Promotion));
        AppSettings settings = new() { Notifications = new() { AllowPromotions = false } };

        IReadOnlyList<NotificationItem> collected = await NotificationSources.CollectAsync(settings, feed, null);

        Assert.IsFalse(collected.Any(i => i.Kind == NotificationKind.Promotion));
        Assert.IsTrue(collected.Any(i => i.Id == "news"), "公告本身照常收。");
    }

    [TestMethod]
    public async Task PromotionsComeThroughWhenAllowed()
    {
        IAnnouncementFeed feed = FeedWith(Item("sale", NotificationKind.Promotion));
        AppSettings settings = new() { Notifications = new() { AllowPromotions = true } };

        IReadOnlyList<NotificationItem> collected = await NotificationSources.CollectAsync(settings, feed, null);

        Assert.IsTrue(collected.Any(i => i.Id == "sale"));
    }

    /// <summary>「启动时检查更新」关掉时,连网络请求都不该发。</summary>
    /// <remarks>
    /// 两个开关是两件事:「通知我有更新」是消息中心的偏好,「启动时检查更新」是更新功能
    /// 自己的开关 —— 后者关掉的用户多半是不希望应用自己联网,那就一次也别联。
    /// </remarks>
    [TestMethod]
    public async Task TheUpdateCheckIsSkippedEntirelyWhenStartupChecksAreOff()
    {
        IUpdateService updates = Substitute.For<IUpdateService>();
        AppSettings settings = new()
        {
            General = new() { CheckUpdatesOnStartup = false },
            Notifications = new() { NotifyUpdates = true },
        };

        await NotificationSources.CollectAsync(settings, null, updates);

        await updates.DidNotReceive().CheckForUpdateAsync();
    }

    [TestMethod]
    public async Task TheUpdateCheckIsSkippedWhenUpdateNotificationsAreOff()
    {
        IUpdateService updates = Substitute.For<IUpdateService>();
        AppSettings settings = new()
        {
            General = new() { CheckUpdatesOnStartup = true },
            Notifications = new() { NotifyUpdates = false },
        };

        await NotificationSources.CollectAsync(settings, null, updates);

        await updates.DidNotReceive().CheckForUpdateAsync();
    }

    /// <summary>商店版不推更新消息。</summary>
    /// <remarks>
    /// 安装目录只读、更新由 Microsoft Store 接管,推一条"去关于页更新"只会把用户
    /// 送到一个什么也做不了的页面。
    /// </remarks>
    [TestMethod]
    public async Task AStoreManagedInstallIsNeverToldToUpdate()
    {
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.IsStoreManaged.Returns(true);

        Assert.IsNull(await NotificationSources.BuildUpdateNotificationAsync(updates));
        await updates.DidNotReceive().CheckForUpdateAsync();
    }

    [TestMethod]
    public async Task AnOfflineUpdateCheckIsSwallowed()
    {
        // 离线或更新源不可达是常态,不该让铃铛变得不可用。
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.CheckForUpdateAsync()
               .Returns<Task<bool>>(_ => throw new HttpRequestException("offline"));

        Assert.IsNull(await NotificationSources.BuildUpdateNotificationAsync(updates));
    }

    /// <summary>更新条目的 id 带版本号 —— 去重与"重新亮起未读"都靠它。</summary>
    /// <remarks>
    /// 同一个版本每次启动都会重投,消息中心按 id 跳过并**保住已读状态**;
    /// 真出了新版本则是一条新 id,会重新亮起未读。id 里不带版本号的话,
    /// 这两件事只能二选一。
    /// </remarks>
    [TestMethod]
    public async Task TheUpdateNotificationIsKeyedByVersion()
    {
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.CheckForUpdateAsync().Returns(true);
        updates.AvailableVersion.Returns("1.9.0");
        updates.CurrentVersion.Returns("1.8.0");

        NotificationItem? item = await NotificationSources.BuildUpdateNotificationAsync(updates);

        Assert.IsNotNull(item);
        Assert.AreEqual("update:1.9.0", item.Id);
        Assert.AreEqual(NotificationKind.Update, item.Kind);
        Assert.AreEqual("app.settings.about", item.Link?.CommandId);
    }

    [TestMethod]
    public async Task NoAvailableVersionMeansNoNotification()
    {
        // 服务说"有更新"却给不出版本号,是它自己的状态不一致;这时候投一条
        // 标题里带空版本的消息只会让人困惑。
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.CheckForUpdateAsync().Returns(true);
        updates.AvailableVersion.Returns((string?)null);

        Assert.IsNull(await NotificationSources.BuildUpdateNotificationAsync(updates));
    }

    [TestMethod]
    public async Task WithNoSourcesAtAllTheResultIsEmpty()
    {
        IReadOnlyList<NotificationItem> collected =
            await NotificationSources.CollectAsync(new AppSettings(), null, null);

        Assert.IsEmpty(collected);
    }
}

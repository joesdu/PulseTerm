using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.Notifications;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>消息中心面板:跳转优先级、已读联动、筛选与相对时间。</summary>
[TestClass]
[TestCategory("Notifications")]
public class NotificationPanelViewModelTests
{
    private static NotificationItem Item(string id, NotificationLink? link = null, bool read = false) =>
        new()
        {
            Id = id,
            Kind = NotificationKind.News,
            Title = $"消息 {id}",
            PublishedAt = DateTime.UtcNow,
            IsRead = read,
            Link = link
        };

    private static async Task<NotificationCenter> SeededCenterAsync(params NotificationItem[] items)
    {
        var center = new NotificationCenter();
        await center.PublishAsync(items);
        return center;
    }

    /// <summary>
    /// 站内命令优先于外链:站内能办的事不该把人赶去浏览器。
    /// 更新提醒正是这条路径 —— 点一下直接落到设置的关于页,而不是打开下载页。
    /// </summary>
    [TestMethod]
    public async Task Activate_PrefersInAppCommandOverUrl()
    {
        NotificationCenter center = await SeededCenterAsync(Item("both", new()
        {
            Label = "查看",
            CommandId = "app.settings.about",
            Url = "https://example.com/release"
        }));
        string? invokedCommand = null;
        var openedUrls = new List<string>();
        var vm = new NotificationPanelViewModel(center,
            id => { invokedCommand = id; return true; },
            url => { openedUrls.Add(url); return Task.CompletedTask; });

        await vm.ActivateCommand.Execute("both").FirstAsync();

        Assert.AreEqual("app.settings.about", invokedCommand);
        Assert.IsEmpty(openedUrls, "站内跳成功后不该再开浏览器。");
    }

    /// <summary>命令没注册(返回 false)时退回外链,而不是什么都不做。</summary>
    [TestMethod]
    public async Task Activate_FallsBackToUrl_WhenCommandMissing()
    {
        NotificationCenter center = await SeededCenterAsync(Item("fallback", new()
        {
            Label = "查看",
            CommandId = "plugin.not.installed",
            Url = "https://example.com/post"
        }));
        var openedUrls = new List<string>();
        var vm = new NotificationPanelViewModel(center, _ => false, url =>
        {
            openedUrls.Add(url);
            return Task.CompletedTask;
        });

        await vm.ActivateCommand.Execute("fallback").FirstAsync();

        Assert.AreSequenceEqual(["https://example.com/post"], openedUrls);
    }

    /// <summary>非 https 的外链一律不开 —— 这是远端来的地址落地前的最后一道闸。</summary>
    [TestMethod]
    public async Task Activate_RefusesNonHttpsUrl()
    {
        NotificationCenter center = await SeededCenterAsync(Item("plain", new()
        {
            Label = "查看",
            Url = "http://example.com"
        }));
        var openedUrls = new List<string>();
        var vm = new NotificationPanelViewModel(center, null, url =>
        {
            openedUrls.Add(url);
            return Task.CompletedTask;
        });

        await vm.ActivateCommand.Execute("plain").FirstAsync();

        Assert.IsEmpty(openedUrls);
    }

    /// <summary>点开一条就把它标为已读,未读数跟着落。</summary>
    [TestMethod]
    public async Task Activate_MarksRead()
    {
        NotificationCenter center = await SeededCenterAsync(Item("a"), Item("b"));
        var vm = new NotificationPanelViewModel(center);
        Assert.AreEqual(2, vm.UnreadCount);

        await vm.ActivateCommand.Execute("a").FirstAsync();

        Assert.AreEqual(1, vm.UnreadCount);
        Assert.IsFalse(vm.Items.Single(item => item.Id == "a").IsUnread);
    }

    /// <summary>没有去处的消息点了也不报错,只是标记已读。</summary>
    [TestMethod]
    public async Task Activate_HandlesItemWithoutLink()
    {
        NotificationCenter center = await SeededCenterAsync(Item("plain"));
        var vm = new NotificationPanelViewModel(center);

        await vm.ActivateCommand.Execute("plain").FirstAsync();

        Assert.AreEqual(0, vm.UnreadCount);
    }

    /// <summary>「只看未读」筛掉已读条目,取消勾选后恢复。</summary>
    [TestMethod]
    public async Task UnreadOnly_FiltersList()
    {
        NotificationCenter center = await SeededCenterAsync(Item("unread"), Item("read", read: true));
        var vm = new NotificationPanelViewModel(center);
        Assert.HasCount(2, vm.Items);

        vm.UnreadOnly = true;
        Assert.HasCount(1, vm.Items);
        Assert.AreEqual("unread", vm.Items[0].Id);

        vm.UnreadOnly = false;
        Assert.HasCount(2, vm.Items);
    }

    /// <summary>全部已读后,「只看未读」下列表为空并给出对应的空态文案。</summary>
    [TestMethod]
    public async Task MarkAllRead_EmptiesUnreadFilter()
    {
        NotificationCenter center = await SeededCenterAsync(Item("a"), Item("b"));
        var vm = new NotificationPanelViewModel(center) { UnreadOnly = true };

        await vm.MarkAllReadCommand.Execute().FirstAsync();

        Assert.IsTrue(vm.IsEmpty);
        Assert.AreEqual(Core.Resources.Strings.Get("Notify_EmptyUnread"), vm.EmptyHint);
    }

    /// <summary>删除与清空要反映到列表上。</summary>
    [TestMethod]
    public async Task Remove_And_Clear_UpdateList()
    {
        NotificationCenter center = await SeededCenterAsync(Item("a"), Item("b"));
        var vm = new NotificationPanelViewModel(center);

        await vm.RemoveCommand.Execute("a").FirstAsync();
        Assert.HasCount(1, vm.Items);

        await vm.ClearCommand.Execute().FirstAsync();
        Assert.IsTrue(vm.IsEmpty);
        Assert.AreEqual(Core.Resources.Strings.Get("Notify_Empty"), vm.EmptyHint);
    }

    /// <summary>外链条目要把主机名摆出来,让用户在点之前就知道会被带去哪个站点。</summary>
    [TestMethod]
    public void ItemViewModel_ExposesLinkHost_ForExternalLinksOnly()
    {
        var external = new NotificationItemViewModel(Item("ext", new() { Label = "读全文", Url = "https://news.example.com/a" }));
        var inApp = new NotificationItemViewModel(Item("in", new() { Label = "查看", CommandId = "app.settings.about" }));

        Assert.AreEqual("news.example.com", external.LinkHost);
        Assert.IsTrue(external.HasLinkHost);
        Assert.IsNull(inApp.LinkHost, "站内跳转没有外部主机可显示。");
        Assert.IsFalse(inApp.HasLinkHost);
    }

    /// <summary>
    /// 拖动后的位置落在 <c>ui-layout/notification-panel</c> —— 与文件传输提示同集合、
    /// 各占一个文档 Id。两个浮层写进同一个 Id 会互相踩,这条钉住 Id 不被写错。
    /// </summary>
    [TestMethod]
    public async Task PersistPanelPosition_WritesCurrentOffsetToStore()
    {
        NotificationCenter center = await SeededCenterAsync(Item("a"));
        IAppDataStore store = Substitute.For<IAppDataStore>();
        var vm = new NotificationPanelViewModel(center, dataStore: store)
        {
            PanelOffsetX = 240,
            PanelOffsetY = -180
        };

        vm.PersistPanelPosition();

        // 断言本身返回 Task(方法是 async 的),丢弃它以免 CS4014 —— 调用记录是同步的。
        _ = store.Received(1).UpsertAsync(
            "ui-layout",
            "notification-panel",
            Arg.Is<PanelPosition>(p => p.OffsetX == 240 && p.OffsetY == -180),
            Arg.Any<CancellationToken>());
    }

    /// <summary>构造时从存储恢复上次的位置 —— 这就是「再次打开回到原有位置」。</summary>
    [TestMethod]
    public async Task Construction_RestoresPersistedPanelPosition()
    {
        NotificationCenter center = await SeededCenterAsync(Item("a"));
        IAppDataStore store = Substitute.For<IAppDataStore>();
        store.GetAsync<PanelPosition>("ui-layout", "notification-panel", Arg.Any<CancellationToken>())
             .Returns(new PanelPosition { OffsetX = 120, OffsetY = -64 });

        var vm = new NotificationPanelViewModel(center, dataStore: store);

        // 恢复是异步的,给它一次调度机会。
        await Task.Yield();
        await Task.Delay(50);

        Assert.AreEqual(120, vm.PanelOffsetX);
        Assert.AreEqual(-64, vm.PanelOffsetY);
    }

    /// <summary>没有存储(单元测试/精简宿主)时不该炸,位置退回默认锚点。</summary>
    [TestMethod]
    public async Task WithoutStore_PanelPositionDefaultsToAnchorAndPersistIsHarmless()
    {
        NotificationCenter center = await SeededCenterAsync(Item("a"));
        var vm = new NotificationPanelViewModel(center);

        Assert.AreEqual(0, vm.PanelOffsetX);
        Assert.AreEqual(0, vm.PanelOffsetY);
        vm.PersistPanelPosition();
    }

    /// <summary>相对时间按量级切换单位;源端时钟偏到未来时一律当作「刚刚」。</summary>
    [TestMethod]
    public void ItemViewModel_FormatsRelativeTime()
    {
        Assert.AreEqual(Core.Resources.Strings.Get("Notify_TimeJustNow"), NotificationItemViewModel.FormatRelative(TimeSpan.FromSeconds(20)));
        Assert.AreEqual(Core.Resources.Strings.Get("Notify_TimeJustNow"), NotificationItemViewModel.FormatRelative(TimeSpan.FromMinutes(-5)));
        Assert.AreEqual(Core.Resources.Strings.Format("Notify_TimeMinutes", 5), NotificationItemViewModel.FormatRelative(TimeSpan.FromMinutes(5)));
        Assert.AreEqual(Core.Resources.Strings.Format("Notify_TimeHours", 3), NotificationItemViewModel.FormatRelative(TimeSpan.FromHours(3)));
        Assert.AreEqual(Core.Resources.Strings.Format("Notify_TimeDays", 2), NotificationItemViewModel.FormatRelative(TimeSpan.FromDays(2)));
        Assert.AreEqual(Core.Resources.Strings.Format("Notify_TimeMonths", 2), NotificationItemViewModel.FormatRelative(TimeSpan.FromDays(70)));
    }
}

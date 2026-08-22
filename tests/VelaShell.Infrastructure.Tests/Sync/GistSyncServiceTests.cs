using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Sync;
using VelaShell.Infrastructure.Sync;

namespace VelaShell.Infrastructure.Tests.Sync;

/// <summary>Gist 远端载荷应用后的运行时刷新通知。</summary>
[TestClass]
public sealed class GistSyncServiceTests
{
    [TestMethod]
    public async Task ApplyRemoteAsync_WithProfiles_NotifiesAfterRepositoryIsUpdated()
    {
        ISettingsService settings = Substitute.For<ISettingsService>();
        ISessionRepository sessions = Substitute.For<ISessionRepository>();
        IAppDataStore store = Substitute.For<IAppDataStore>();
        IQuickCommandRepository snippets = Substitute.For<IQuickCommandRepository>();
        ISecretProtector secrets = Substitute.For<ISecretProtector>();
        sessions.GetAllSessionsAsync().Returns([]);
        var service = new GistSyncService(settings, sessions, store, snippets, secrets);
        SessionProfile profile = new()
        {
            Id = Guid.NewGuid(),
            Name = "cloud-server",
            Host = "cloud.example.com",
            Username = "root",
        };
        bool profileWasSavedWhenNotified = false;
        service.ProfilesApplied += (_, _) =>
            profileWasSavedWhenNotified = sessions.ReceivedCalls().Any(call =>
                call.GetMethodInfo().Name == nameof(ISessionRepository.SaveSessionAsync)
            );

        SyncResult result = await service.ApplyRemoteAsync(
            new SyncSettings
            {
                SyncAppSettings = false,
                SyncProfiles = true,
                SyncSnippets = false,
            },
            new SyncEnvelope
            {
                Payload = new SyncPayload
                {
                    DeviceName = "device-1",
                    UpdatedAtUtc = DateTime.UtcNow,
                    Profiles = [profile],
                },
            },
            remoteVersion: "revision-1",
            CancellationToken.None
        );

        Assert.IsTrue(result.Success);
        Assert.AreEqual(SyncAction.Pulled, result.Action);
        Assert.IsTrue(profileWasSavedWhenNotified, "通知必须发生在连接配置写入仓储之后。");
        await sessions.Received(1).SaveSessionAsync(profile);
    }

    [TestMethod]
    [TestCategory("BackgroundActivity")]
    public async Task SyncEntryPoints_ReportToTheBackgroundLedger_AndAlwaysClearIt()
    {
        // 云同步一向静默(启动拉取、保存后防抖推送,失败都不打扰用户)。接进账本之后
        // "现在有没有在同步"至少有个去处 —— 但静默的另一面是它出错也不吭声,
        // 所以**每一条出口都必须把活动收干净**,包括配置无效直接返回的那条快速失败路径。
        ISettingsService settings = Substitute.For<ISettingsService>();
        ISessionRepository sessions = Substitute.For<ISessionRepository>();
        IAppDataStore store = Substitute.For<IAppDataStore>();
        IQuickCommandRepository snippets = Substitute.For<IQuickCommandRepository>();
        ISecretProtector secrets = Substitute.For<ISecretProtector>();
        sessions.GetAllSessionsAsync().Returns([]);
        using var activity = new Core.Services.BackgroundActivityService();
        var seen = new List<string>();
        activity.Changed += () =>
        {
            lock (seen)
            {
                foreach (Core.Services.BackgroundActivitySnapshot snapshot in activity.Activities)
                {
                    if (snapshot.Detail is { } detail && !seen.Contains(detail))
                    {
                        seen.Add(detail);
                    }
                }
            }
        };
        var service = new GistSyncService(settings, sessions, store, snippets, secrets, activity);

        // 未配置 Gist:四条出口都走 Validate 的快速失败,正好用来验"开了必收"。
        Assert.IsFalse((await service.SyncNowAsync()).Success);
        Assert.IsFalse((await service.PushAsync()).Success);
        Assert.IsFalse((await service.PullAsync()).Success);
        Assert.IsFalse((await service.RestoreRevisionAsync("rev-1")).Success);

        Assert.IsEmpty(activity.Activities, "同步的每一条出口都必须把活动收干净。");
        // 副标题用设置页同名按钮的文案,四条各不相同 —— 用户能分清是在推还是在拉。
        Assert.HasCount(4, seen, $"四条出口应各自可辨:{string.Join(" / ", seen)}");
    }
}

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
}

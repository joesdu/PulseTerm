using Avalonia.Headless;
using NSubstitute;
using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Concurrency;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Docking;
using VelaShell.Infrastructure.Ftp;
using VelaShell.Infrastructure.Tests.Ftp;
using VelaShell.Presentation.ViewModels;
using VelaShell.ViewModels;

namespace VelaShell.Tests.Views;

/// <summary>
/// 应用层的 FTP 连接链路:走 <see cref="MainWindowViewModel.OpenFtpDocumentForProfileAsync" />
/// 真连一个环回 FTP 服务器,验证文档被加进工作区、远端面板确实列出了服务器上的文件。
/// <para>
/// 这一条覆盖的是「用户双击一条 FTP 配置」之后发生的全部事情:建立会话 → 建文档 →
/// 双栏面板走 <c>ISftpService</c> 拉目录。后端自身的协议细节在
/// <c>VelaShell.Infrastructure.Tests</c> 的 FtpFileServiceIntegrationTests 里覆盖。
/// </para>
/// </summary>
[TestClass]
[TestCategory("FtpConnectionFlow")]
public sealed class FtpConnectionFlowTests
{
    private static HeadlessUnitTestSession _session = null!;

    /// <summary>
    /// 本用例不往 UI 线程上派发(理由见测试方法的 remarks),但仍要启动 headless 会话:
    /// <see cref="MainWindowViewModel" /> 途中会碰 <c>Dispatcher.UIThread</c>,没有 Avalonia 应用
    /// 就会因测试执行顺序不同而偶发失败。
    /// </summary>
    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(FtpConnectionFlowTests).Assembly);

    /// <remarks>
    /// 用 <c>await await Dispatch(async …)</c> 而不是在 UI 线程里 <c>GetResult()</c>:
    /// 连接链路全程 <c>ConfigureAwait(true)</c>,续体要回到 UI 线程,在那条线程上同步阻塞等它
    /// 必然死锁(实测卡满 60s 超时)。异步 lambda 一遇 await 就交还线程,调度器才能把续体泵完。
    /// </remarks>
    [TestMethod]
    public async Task ConnectingFtpProfile_OpensDocument_AndListsRemoteFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vela-ftp-flow-{Guid.NewGuid():N}");
        using var server = new LoopbackFtpServer(root);
        File.WriteAllText(Path.Combine(root, "deploy.sh"), "#!/bin/sh\necho hi\n");
        File.WriteAllText(Path.Combine(root, "notes.md"), "# notes");
        Directory.CreateDirectory(Path.Combine(root, "backups"));

        // 不进 headless UI 线程:连接链路全程 ConfigureAwait(true),在那条线程上等它会死锁,
        // 而 Dispatch 只泵到 action 返回为止,异步 lambda 的续体等不到调度。这条链路本身不需要
        // UI 线程(没有控件参与),因此直接在测试线程上跑。
        await Task.Run(async () =>
        {
            var ftp = new FtpFileService();
            var vm = new MainWindowViewModel(sftpService: ftp, ftpSessionService: ftp);
            var profile = new SessionProfile
            {
                ConnectionType = ConnectionType.FTP,
                Name = "环回 FTP",
                Host = "127.0.0.1",
                Port = server.Port,
                Username = "deploy",
                Password = "secret123",
                Ftp = new FtpSettings { EncryptionMode = FtpEncryptionMode.None, MaxConnections = 2 },
            };

            SftpDocument? document = await vm.OpenFtpDocumentForProfileAsync(profile);

            Assert.IsNotNull(document, "FTP 配置应能直接连上并打开一个文档标签。");
            Assert.IsNull(document!.ViewModel.Session, "FTP 文档没有 SSH 会话 —— 这正是文档视图模型被泛化的原因。");
            Assert.Contains(document, vm.Layout.AllDocuments().ToList());

            await document.ViewModel.InitialLoadTask;

            string[] names = [.. document.ViewModel.RemoteFiles.Files
                .Where(entry => !entry.IsParentEntry)
                .Select(entry => entry.Name)];
            Assert.Contains("deploy.sh", names);
            Assert.Contains("notes.md", names);
            Assert.Contains("backups", names);
            Assert.IsNull(document.ViewModel.RemoteFiles.ErrorMessage);

            await document.ViewModel.CloseAsync();
            Assert.IsFalse(ftp.OwnsSession(document.ViewModel.SessionId), "关闭文档应连同 FTP 会话一起断开。");
        });

        try
        {
            Directory.Delete(root, true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响断言。
        }
    }

    /// <summary>
    /// 用户反馈:点太快对同一条 FTP 配置开出两个标签,关掉其中一个,资源管理器里那条的状态
    /// 圆点就灭了 —— 明明还有一个活着。
    /// <para>
    /// 根因与 #321(终端标签那次)同形:树上一条配置只有一个节点,而关闭路径按配置 Id
    /// **直接写**「未连接」,不看同一条配置名下还有没有别的活会话。终端侧当时改成了
    /// 「按名下所有标签重算」,文档型连接(SFTP / FTP / S3 / 工作台)漏在了外面。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task ClosingOneOfTwoFtpDocumentsForTheSameProfile_KeepsTheTreeNodeActive()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vela-ftp-dup-{Guid.NewGuid():N}");
        using var server = new LoopbackFtpServer(root);
        File.WriteAllText(Path.Combine(root, "deploy.sh"), "#!/bin/sh\n");

        // 理由同上一条用例:连接链路全程 ConfigureAwait(true),在 headless UI 线程上等它会死锁。
        await Task.Run(async () =>
        {
            var profile = new SessionProfile
            {
                Id = Guid.NewGuid(),
                ConnectionType = ConnectionType.FTP,
                Name = "外网FTP",
                Host = "127.0.0.1",
                Port = server.Port,
                Username = "deploy",
                Password = "secret123",
                Ftp = new FtpSettings { EncryptionMode = FtpEncryptionMode.None, MaxConnections = 2 },
            };
            ISessionRepository repository = Substitute.For<ISessionRepository>();
            repository.GetAllSessionsAsync().Returns(_ => Task.FromResult(new List<SessionProfile> { profile }));
            repository.GetAllGroupsAsync().Returns(_ => Task.FromResult(new List<ServerGroup>()));

            var ftp = new FtpFileService();
            var vm = new MainWindowViewModel(sessionRepository: repository, sftpService: ftp, ftpSessionService: ftp);
            SessionTreeViewModel tree = vm.Sidebar.SessionTree!;
            await tree.LoadCommand.Execute().FirstAsync();
            SessionTreeNodeViewModel node = tree.Nodes.Single(item => item.Id == profile.Id);

            SftpDocument first = (await vm.OpenFtpDocumentForProfileAsync(profile))!;
            SftpDocument second = (await vm.OpenFtpDocumentForProfileAsync(profile))!;
            Assert.AreNotSame(first, second, "同一条配置开两次应得到两个各自独立的文档。");
            Assert.IsTrue(
                SpinWait.SpinUntil(() => node.Status == SessionStatus.Connected, TimeSpan.FromSeconds(5)),
                "两个文档都开着,节点当然是「活跃」。");

            Guid firstSessionId = first.ViewModel.SessionId;
            vm.Layout.CloseDocument(first);

            // 「关完之后节点仍是活跃」这条断言必须钉在一个确定的时点上,否则它会因为
            // "状态更新还没来得及跑"而假通过 —— 那样即使把修复撤掉,用例照样绿。
            // 两步:先等关闭任务真正跑完(状态更新是在它的收尾里发起的),
            // 再往主线程调度器上压一道栅栏,把它前面排队的刷新全部冲掉。
            await vm.GetStandaloneSftpCloseTask(first);
            await DrainMainThreadAsync();

            Assert.IsFalse(ftp.OwnsSession(firstSessionId), "关掉文档应连同它那条 FTP 会话一起断开。");
            Assert.AreEqual(
                SessionStatus.Connected,
                node.Status,
                "还有一个文档开着,节点不能因为关掉了另一个就变成未连接(用户反馈的正是这一幕)。");

            vm.Layout.CloseDocument(second);
            await vm.GetStandaloneSftpCloseTask(second);
            await DrainMainThreadAsync();

            Assert.AreEqual(
                SessionStatus.Disconnected,
                node.Status,
                "最后一个文档也关掉了,节点必须回到未连接 —— 修复不能反过来让它永远亮着。");
        });

        try
        {
            Directory.Delete(root, true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响断言。
        }
    }

    /// <summary>
    /// 在主线程调度器上压一道栅栏:它跑到时,此刻之前排队的作业(树上的状态刷新走的正是这条队)
    /// 都已经跑完。用它替代"睡一会儿再看" —— 后者对时序有假设,而且失败时只会偶发。
    /// </summary>
    private static Task DrainMainThreadAsync()
    {
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RxSchedulers.MainThreadScheduler.Schedule(() => drained.SetResult());
        return drained.Task;
    }
}

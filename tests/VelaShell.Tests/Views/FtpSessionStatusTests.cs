using Avalonia.Headless;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Docking;
using VelaShell.Infrastructure.Ftp;
using VelaShell.Infrastructure.Tests.Ftp;
using VelaShell.Presentation.ViewModels;
using VelaShell.ViewModels;

namespace VelaShell.Tests.Views;

/// <summary>
/// FTP 会话在资源管理器树上的状态圆点:连上要变绿、断开/关闭要变回红,
/// 且断线后再操作远端不得抛 NullReference。三条都是实机反馈的缺陷。
/// </summary>
[TestClass]
[TestCategory("FtpSessionStatus")]
public sealed class FtpSessionStatusTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(FtpSessionStatusTests).Assembly);

    [TestMethod]
    public async Task ConnectingFtp_TurnsTreeDotGreen_AndClosingTurnsItBack()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vela-ftp-status-{Guid.NewGuid():N}");
        using var server = new LoopbackFtpServer(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "a");

        await Task.Run(async () =>
        {
            var ftp = new FtpFileService();
            SessionProfile profile = FtpProfile(server.Port);
            ISessionRepository repository = Substitute.For<ISessionRepository>();
            repository.GetAllSessionsAsync().Returns(_ => Task.FromResult(new List<SessionProfile> { profile }));
            repository.GetAllGroupsAsync().Returns(_ => Task.FromResult(new List<ServerGroup>()));
            repository.GetSessionAsync(profile.Id).Returns(_ => Task.FromResult<SessionProfile?>(profile));

            var vm = new MainWindowViewModel(sessionRepository: repository, sftpService: ftp, ftpSessionService: ftp);
            SessionTreeViewModel tree = vm.Sidebar.SessionTree!;
            await tree.LoadCommand.Execute().FirstAsync();
            SessionTreeNodeViewModel node = tree.Nodes.Single(n => n.Id == profile.Id);
            Assert.AreEqual(SessionStatus.Disconnected, node.Status);

            await vm.TryConnectProfileAsync(profile);

            await AssertStatusAsync(node, SessionStatus.Connected, "FTP 连上后树上的圆点必须变绿。");

            SftpDocument document = vm.Layout.AllDocuments().OfType<SftpDocument>().Single();
            await document.ViewModel.CloseAsync();
            Assert.IsFalse(ftp.OwnsSession(document.ViewModel.SessionId), "关闭文档应连同释放 FTP 会话。");

            await AssertStatusAsync(node, SessionStatus.Disconnected, "关闭 FTP 文档后圆点应变回红。");
        });

        TryDeleteDirectory(root);
    }

    [TestMethod]
    public async Task ServerGoesAway_TreeDotGoesBackToOffline()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vela-ftp-drop-{Guid.NewGuid():N}");
        var server = new LoopbackFtpServer(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "a");

        await Task.Run(async () =>
        {
            var ftp = new FtpFileService();
            SessionProfile profile = FtpProfile(server.Port);
            ISessionRepository repository = Substitute.For<ISessionRepository>();
            repository.GetAllSessionsAsync().Returns(_ => Task.FromResult(new List<SessionProfile> { profile }));
            repository.GetAllGroupsAsync().Returns(_ => Task.FromResult(new List<ServerGroup>()));

            var vm = new MainWindowViewModel(sessionRepository: repository, sftpService: ftp, ftpSessionService: ftp);
            SessionTreeViewModel tree = vm.Sidebar.SessionTree!;
            await tree.LoadCommand.Execute().FirstAsync();
            SessionTreeNodeViewModel node = tree.Nodes.Single(n => n.Id == profile.Id);
            await vm.TryConnectProfileAsync(profile);
            await AssertStatusAsync(node, SessionStatus.Connected, "连上后应先变绿。");

            SftpDocument document = vm.Layout.AllDocuments().OfType<SftpDocument>().Single();
            server.Dispose();   // 服务器消失:后续任何远端操作都会失败

            await document.ViewModel.RemoteFiles.RefreshCommand.Execute().FirstAsync();

            await AssertStatusAsync(node, SessionStatus.Error,
                "服务器断开后,树上的状态应自动变成离线,而不是一直显示活跃。");
        });

        TryDeleteDirectory(root);
    }

    [TestMethod]
    public async Task OpeningRemoteFileAfterDisconnect_ReportsError_WithoutNullReference()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vela-ftp-open-{Guid.NewGuid():N}");
        var server = new LoopbackFtpServer(root);
        File.WriteAllText(Path.Combine(root, "note.txt"), "hello");

        await Task.Run(async () =>
        {
            var ftp = new FtpFileService();
            SessionProfile profile = FtpProfile(server.Port);
            ISessionRepository repository = Substitute.For<ISessionRepository>();
            repository.GetAllSessionsAsync().Returns(_ => Task.FromResult(new List<SessionProfile> { profile }));
            repository.GetAllGroupsAsync().Returns(_ => Task.FromResult(new List<ServerGroup>()));

            var vm = new MainWindowViewModel(sessionRepository: repository, sftpService: ftp, ftpSessionService: ftp);
            await vm.Sidebar.SessionTree!.LoadCommand.Execute().FirstAsync();
            await vm.TryConnectProfileAsync(profile);
            SftpDocument document = vm.Layout.AllDocuments().OfType<SftpDocument>().Single();
            await document.ViewModel.InitialLoadTask;

            document.ViewModel.RemoteFiles.OpenLocalFile = _ => Task.CompletedTask;
            RemoteFileInfoViewModel file = document.ViewModel.RemoteFiles.Files.Single(f => f.Name == "note.txt");
            server.Dispose();   // 断线

            // 双击远端文件 —— 断线后必须给出可读错误,不能抛 NullReferenceException。
            await document.ViewModel.RemoteFiles.ActivateCommand.Execute(file).FirstAsync();

            string? error = document.ViewModel.RemoteFiles.ErrorMessage;
            Assert.IsNotNull(error, "断线后打开远端文件应给出错误提示。");
            Assert.DoesNotContain("Object reference", error!,
                "断线提示必须是可读的连接错误,而不是库内部漏出来的 NullReferenceException 文案。");
        });

        TryDeleteDirectory(root);
    }

    /// <summary>
    /// 等到树节点变成期望状态。状态由 FTP 服务的事件驱动、再 Post 到 UI 线程,天生是异步的 ——
    /// 直接断言等于赌调度时机(实测会偶发红)。每轮都把 UI 队列泵空再看一眼。
    /// </summary>
    private static async Task AssertStatusAsync(SessionTreeNodeViewModel node, SessionStatus expected, string because)
    {
        for (int i = 0; i < 100 && node.Status != expected; i++)
        {
            await Task.Delay(20);
        }
        Assert.AreEqual(expected, node.Status, because);
    }

    private static SessionProfile FtpProfile(int port) =>
        new()
        {
            ConnectionType = ConnectionType.FTP,
            Name = "环回 FTP",
            Host = "127.0.0.1",
            Port = port,
            Username = "deploy",
            Password = "secret123",
            Ftp = new FtpSettings { EncryptionMode = FtpEncryptionMode.None, MaxConnections = 2 },
        };

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响断言。
        }
    }
}

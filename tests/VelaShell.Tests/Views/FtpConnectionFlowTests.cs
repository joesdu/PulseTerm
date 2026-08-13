using Avalonia.Headless;
using VelaShell.Core.Models;
using VelaShell.Docking;
using VelaShell.Infrastructure.Ftp;
using VelaShell.Infrastructure.Tests.Ftp;
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
}

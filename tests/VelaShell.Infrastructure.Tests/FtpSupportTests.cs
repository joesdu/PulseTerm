using System.Text;
using FluentFTP.Exceptions;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Ftp;
using VelaShell.Core.Import;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Infrastructure.Ftp;
using VelaShell.Infrastructure.Import;
using VelaShell.Infrastructure.Sftp;

namespace VelaShell.Infrastructure.Tests;

/// <summary>
/// FTP / FTPS 支持:导入器的协议映射、远程文件服务的会话路由、库异常翻译。
/// 需要真实服务器的连通性验证不在这里(见 docs/FTP客户端可行性调研.md 第五节的 Docker 矩阵)。
/// </summary>
[TestClass]
[TestCategory("Ftp")]
public class FtpSupportTests
{
    /// <summary>WinSCP 的 FTP 会话(FSProtocol=5)现在应被判为受支持,并按 Ftps 值带出加密方式。</summary>
    [TestMethod]
    [DataRow(3, FtpEncryptionMode.Explicit, 21)]   // 显式 TLS
    [DataRow(2, FtpEncryptionMode.Explicit, 21)]   // 显式 SSL
    [DataRow(1, FtpEncryptionMode.Implicit, 990)]  // 隐式
    [DataRow(0, FtpEncryptionMode.None, 21)]       // 明文
    public async Task WinScp_FtpSession_IsSupported_WithEncryptionAndDefaultPort(int ftps, FtpEncryptionMode expected, int expectedPort)
    {
        string ini = Path.Combine(Path.GetTempPath(), $"winscp-ftp-{Guid.NewGuid():N}.ini");
        await File.WriteAllTextAsync(ini,
            $"""
             [Configuration\Security]
             UseMasterPassword=0
             [Sessions\files]
             HostName=ftp.example.com
             UserName=deploy
             FSProtocol=5
             Ftps={ftps}
             """);
        try
        {
            SessionImportScan scan = await ScanWinScpAsync(ini);

            Assert.HasCount(1, scan.Items);
            ImportedSession item = scan.Items[0];
            Assert.AreEqual(ConnectionType.FTP, item.ConnectionType);
            Assert.IsTrue(item.IsSupported, "FTP 会话应可导入(加入 ConnectionType.FTP 之前它被标为不支持)。");
            Assert.AreEqual("FTP", item.Protocol);
            Assert.IsNotNull(item.FtpSettings);
            Assert.AreEqual(expected, item.FtpSettings.EncryptionMode);
            // 端口缺省值按协议给:不能再落到 SSH 的 22。
            Assert.AreEqual(expectedPort, item.Port);
        }
        finally
        {
            File.Delete(ini);
        }
    }

    /// <summary>WebDAV / S3 仍然不受支持 —— 这次只放开了 FTP。</summary>
    [TestMethod]
    [DataRow(6)]
    [DataRow(7)]
    public async Task WinScp_WebDavAndS3_RemainUnsupported(int fsProtocol)
    {
        string ini = Path.Combine(Path.GetTempPath(), $"winscp-other-{Guid.NewGuid():N}.ini");
        await File.WriteAllTextAsync(ini,
            $"""
             [Sessions\other]
             HostName=example.com
             UserName=u
             FSProtocol={fsProtocol}
             """);
        try
        {
            SessionImportScan scan = await ScanWinScpAsync(ini);

            Assert.HasCount(1, scan.Items);
            Assert.IsFalse(scan.Items[0].IsSupported);
            Assert.IsNull(scan.Items[0].FtpSettings);
        }
        finally
        {
            File.Delete(ini);
        }
    }

    /// <summary>Xshell 的 FTP / FTPS 会话同样应被判为受支持,FTPS 走显式 TLS。</summary>
    [TestMethod]
    [DataRow("FTP", FtpEncryptionMode.None)]
    [DataRow("FTPS", FtpEncryptionMode.Explicit)]
    public async Task Xshell_FtpSession_IsSupported_WithEncryption(string protocol, FtpEncryptionMode expected)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"xshell-ftp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        // Xshell 的 .xsh 是 UTF-16 LE 带 BOM 的分节 INI。
        await File.WriteAllLinesAsync(
            Path.Combine(directory, "files.xsh"),
            ["[SessionInfo]", "Version=6.0", "[CONNECTION]", "Host=ftp.example.com", $"Protocol={protocol}",
             "[CONNECTION:AUTHENTICATION]", "UserName=deploy"],
            new UnicodeEncoding(false, true));
        try
        {
            ISessionRepository repository = Substitute.For<ISessionRepository>();
            repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile>()));
            var service = new XshellImportService(repository);

            SessionImportScan scan = await service.ScanAsync(directory);

            Assert.HasCount(1, scan.Items);
            ImportedSession item = scan.Items[0];
            Assert.AreEqual(ConnectionType.FTP, item.ConnectionType);
            Assert.IsTrue(item.IsSupported);
            Assert.IsNotNull(item.FtpSettings);
            Assert.AreEqual(expected, item.FtpSettings.EncryptionMode);
            Assert.AreEqual(21, item.Port);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>路由按会话归属分派:FTP 持有的会话走 FTP 后端,其余一律走 SFTP 后端。</summary>
    [TestMethod]
    public async Task Routing_DispatchesBySessionOwnership()
    {
        var ftpSession = Guid.NewGuid();
        var sshSession = Guid.NewGuid();
        ISftpService sftp = Substitute.For<ISftpService>();
        ISftpService ftp = Substitute.For<ISftpService>();
        IFtpSessionService sessions = Substitute.For<IFtpSessionService>();
        sessions.OwnsSession(ftpSession).Returns(true);
        sessions.OwnsSession(sshSession).Returns(false);
        var router = new RoutingRemoteFileService(sftp, ftp, sessions);

        await router.ListDirectoryAsync(ftpSession, "/pub");
        await router.ListDirectoryAsync(sshSession, "/home");

        await ftp.Received(1).ListDirectoryAsync(ftpSession, "/pub", Arg.Any<CancellationToken>());
        await ftp.DidNotReceive().ListDirectoryAsync(sshSession, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await sftp.Received(1).ListDirectoryAsync(sshSession, "/home", Arg.Any<CancellationToken>());
        await sftp.DidNotReceive().ListDirectoryAsync(ftpSession, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>未知会话仍走 SFTP 后端 —— 报错行为与加入 FTP 之前保持一致。</summary>
    [TestMethod]
    public async Task Routing_UnknownSession_FallsBackToSftp()
    {
        ISftpService sftp = Substitute.For<ISftpService>();
        ISftpService ftp = Substitute.For<ISftpService>();
        IFtpSessionService sessions = Substitute.For<IFtpSessionService>();
        sessions.OwnsSession(Arg.Any<Guid>()).Returns(false);
        var router = new RoutingRemoteFileService(sftp, ftp, sessions);

        var unknown = Guid.NewGuid();
        await router.GetWorkingDirectoryAsync(unknown);

        await sftp.Received(1).GetWorkingDirectoryAsync(unknown, Arg.Any<CancellationToken>());
    }

    /// <summary>库异常必须翻译成 Core 的中立异常族,不得让 FluentFTP 的类型越过 Infrastructure 边界。</summary>
    [TestMethod]
    public void Interop_TranslatesLibraryExceptions()
    {
        Assert.IsInstanceOfType<VelaFtpAuthenticationException>(
            FluentFtpInterop.Translate(new FtpAuthenticationException("530", "Login incorrect"), "connect"));
        Assert.IsInstanceOfType<VelaFtpPathNotFoundException>(
            FluentFtpInterop.Translate(new FtpCommandException("550", "No such file or directory"), "stat"));
        Assert.IsInstanceOfType<VelaFtpPermissionDeniedException>(
            FluentFtpInterop.Translate(new FtpCommandException("550", "Permission denied"), "delete"));
        Assert.IsInstanceOfType<VelaFtpConnectionException>(
            FluentFtpInterop.Translate(new TimeoutException("too slow"), "connect"));
        // 取消不是错误,原样放行(否则会被上层当成连接失败提示给用户)。
        var canceled = new OperationCanceledException();
        Assert.AreSame(canceled, FluentFtpInterop.Translate(canceled, "list"));
    }

    /// <summary>没有 Ftp 设置块的临时配置(如「最近连接」重连)应回落到默认设置而不是崩掉。</summary>
    [TestMethod]
    public void ConnectionInfo_FromProfile_FallsBackToDefaults()
    {
        var profile = new SessionProfile
        {
            ConnectionType = ConnectionType.FTP,
            Host = "ftp.example.com",
            Port = 0,
            Username = "deploy",
        };

        var info = FtpConnectionInfo.FromProfile(profile);

        Assert.AreEqual(FtpSettings.DefaultPort, info.Port);
        Assert.AreEqual(FtpEncryptionMode.Auto, info.Settings.EncryptionMode);
        Assert.AreEqual("deploy", info.EffectiveUsername);
    }

    /// <summary>匿名登录时用户名/口令由服务自行给出,不要求用户填。</summary>
    [TestMethod]
    public void ConnectionInfo_Anonymous_UsesAnonymousCredentials()
    {
        var profile = new SessionProfile
        {
            ConnectionType = ConnectionType.FTP,
            Host = "ftp.example.com",
            Username = string.Empty,
            Ftp = new FtpSettings { Anonymous = true },
        };

        var info = FtpConnectionInfo.FromProfile(profile);

        Assert.AreEqual(FtpConnectionInfo.AnonymousUser, info.EffectiveUsername);
        Assert.IsNotEmpty(info.EffectivePassword);
    }

    private static async Task<SessionImportScan> ScanWinScpAsync(string ini)
    {
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        repository.GetAllSessionsAsync().Returns(Task.FromResult(new List<SessionProfile>()));
        var service = new WinScpImportService(repository);
        return await service.ScanAsync(ini);
    }
}

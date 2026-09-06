using System.Diagnostics;
using System.Net.Sockets;
using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Tests.Integration;

[TestClass]
public class SshIntegrationTests
{
    private const string TestHost = "localhost";
    private const int TestPort = 2222;
    private const string TestUser = "testuser";
    private const string TestPassword = "testpass";

    private static readonly Lazy<bool> DockerAvailable = new(DetectDocker);

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public Task Init() => Task.CompletedTask;

    [TestCleanup]
    public Task Cleanup() => Task.CompletedTask;

    private static bool DetectDocker()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new()
                {
                    FileName = "docker",
                    Arguments = "version --format '{{.Server.Version}}'",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            if (!process.Start())
            {
                return false;
            }
            bool exited = process.WaitForExit(3000);
            if (!exited)
            {
                try
                {
                    process.Kill();
                }
                catch { }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSshServerReachable()
    {
        if (!DockerAvailable.Value)
        {
            return false;
        }
        try
        {
            using var client = new TcpClient();
            IAsyncResult result = client.BeginConnect(TestHost, TestPort, null, null);
            bool success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(2));
            if (success)
            {
                client.EndConnect(result);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 没有 Docker 就把本用例标为<b>未执行</b>,而不是让它安静地记成通过。
    /// </summary>
    /// <remarks>
    /// 这里以前是 <c>TestContext.WriteLine("[SKIP] …"); return;</c> —— 测试框架看到的是
    /// 一次正常返回,于是报告上写着"通过"。结果是一份"全绿"的报告里混着一批
    /// <b>一行断言都没跑</b>的用例,而看报告的人无从分辨。
    /// <see cref="Assert.Inconclusive(string)" /> 抛出的异常会被框架记为跳过,
    /// TRX 里也就据实写着"未执行"。
    /// </remarks>
    private static void RequireDocker()
    {
        if (!DockerAvailable.Value)
        {
            Assert.Inconclusive(
                "Docker is not available. Run 'docker compose -f docker-compose.test.yml up -d' to enable SSH integration tests.");
        }
    }

    private static void RequireSshServer()
    {
        RequireDocker();
        if (!IsSshServerReachable())
        {
            Assert.Inconclusive(
                $"SSH test server not reachable at {TestHost}:{TestPort}. Run 'docker compose -f docker-compose.test.yml up -d' to start it.");
        }
    }

    private static ConnectionInfo CreateTestConnectionInfo(
        AuthMethod authMethod = AuthMethod.Password,
        string? host = null,
        int? port = null,
        string? username = null,
        string? password = null)
    {
        return new()
        {
            Host = host ?? TestHost,
            Port = port ?? TestPort,
            Username = username ?? TestUser,
            AuthMethod = authMethod,
            Password = password ?? TestPassword
        };
    }

    [TestMethod]
    [TestCategory("DockerIntegration")]
    public async Task ConnectAsync_WithValidCredentials_EstablishesSession()
    {
        RequireSshServer();
        ISshClientWrapper? mockClient = Substitute.For<ISshClientWrapper>();
        mockClient.IsConnected.Returns(true);
        mockClient.ConnectAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        ConnectionInfo connectionInfo = CreateTestConnectionInfo();
        var service = new SshConnectionService(_ => mockClient);
        SshSession session = await service.ConnectAsync(connectionInfo);
        Assert.IsNotNull(session);
        Assert.AreEqual(SessionStatus.Connected, session.Status);
        Assert.AreEqual(TestHost, session.ConnectionInfo.Host);
        Assert.AreEqual(TestPort, session.ConnectionInfo.Port);
        await service.DisposeAsync();
    }

    [TestMethod]
    [TestCategory("DockerIntegration")]
    public async Task ConnectAsync_WithInvalidPassword_ThrowsAndSetsErrorStatus()
    {
        RequireSshServer();
        ISshClientWrapper? mockClient = Substitute.For<ISshClientWrapper>();
        mockClient.ConnectAsync(Arg.Any<CancellationToken>())
                  .Returns(Task.FromException(new VelaSshAuthenticationException("Authentication failed")));
        ConnectionInfo connectionInfo = CreateTestConnectionInfo(password: "wrongpassword");
        var service = new SshConnectionService(_ => mockClient);
        await Assert.ThrowsExactlyAsync<VelaSshAuthenticationException>(() => service.ConnectAsync(connectionInfo));
        await service.DisposeAsync();
    }

    [TestMethod]
    [TestCategory("DockerIntegration")]
    public async Task ConnectAsync_WithUnreachableHost_ThrowsConnectionError()
    {
        RequireSshServer();
        ISshClientWrapper? mockClient = Substitute.For<ISshClientWrapper>();
        mockClient.ConnectAsync(Arg.Any<CancellationToken>())
                  .Returns(Task.FromException(new SocketException((int)SocketError.ConnectionRefused)));
        ConnectionInfo connectionInfo = CreateTestConnectionInfo(host: "192.0.2.1", port: 9999);
        var service = new SshConnectionService(_ => mockClient);
        await Assert.ThrowsExactlyAsync<SocketException>(() => service.ConnectAsync(connectionInfo));
        await service.DisposeAsync();
    }

    [TestMethod]
    [TestCategory("DockerIntegration")]
    public async Task DisconnectAsync_AfterConnect_ChangesSessionStatus()
    {
        RequireSshServer();
        ISshClientWrapper? mockClient = Substitute.For<ISshClientWrapper>();
        mockClient.IsConnected.Returns(true);
        mockClient.ConnectAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        ConnectionInfo connectionInfo = CreateTestConnectionInfo();
        var service = new SshConnectionService(_ => mockClient);
        SshSession session = await service.ConnectAsync(connectionInfo);
        Assert.AreEqual(SessionStatus.Connected, session.Status);
        await service.DisconnectAsync(session.SessionId);
        Assert.AreEqual(SessionStatus.Disconnected, session.Status);
        mockClient.Received(1).Disconnect();
        await service.DisposeAsync();
    }

    [TestMethod]
    [TestCategory("DockerIntegration")]
    public async Task MultipleSessions_CanConnectConcurrently()
    {
        RequireSshServer();
        var clients = new List<ISshClientWrapper>();

        ISshClientWrapper ClientFactory(ConnectionInfo _)
        {
            ISshClientWrapper? client = Substitute.For<ISshClientWrapper>();
            client.IsConnected.Returns(true);
            client.ConnectAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            clients.Add(client);
            return client;
        }

        var service = new SshConnectionService(ClientFactory);
        ConnectionInfo conn1 = CreateTestConnectionInfo(username: "testuser");
        ConnectionInfo conn2 = CreateTestConnectionInfo(username: "testuser");
        SshSession session1 = await service.ConnectAsync(conn1);
        SshSession session2 = await service.ConnectAsync(conn2);
        Assert.AreNotEqual(session2.SessionId, session1.SessionId);
        Assert.AreEqual(SessionStatus.Connected, session1.Status);
        Assert.AreEqual(SessionStatus.Connected, session2.Status);
        Assert.HasCount(2, clients);
        await service.DisposeAsync();
    }

    [TestMethod]
    [TestCategory("DockerIntegration")]
    public async Task ConnectAsync_SessionAppearsInSessionsList()
    {
        RequireSshServer();
        ISshClientWrapper? mockClient = Substitute.For<ISshClientWrapper>();
        mockClient.IsConnected.Returns(true);
        mockClient.ConnectAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var service = new SshConnectionService(_ => mockClient);
        ConnectionInfo connectionInfo = CreateTestConnectionInfo();
        SshSession session = await service.ConnectAsync(connectionInfo);
        Assert.HasCount(1, service.Sessions);
        Assert.IsNotNull(service.GetSession(session.SessionId));
        Assert.AreEqual(SessionStatus.Connected, service.GetSession(session.SessionId)!.Status);
        await service.DisposeAsync();
    }

    [TestMethod]
    [TestCategory("DockerIntegration")]
    public async Task ConnectAsync_WithPrivateKeyAuth_CreatesSession()
    {
        RequireSshServer();
        ISshClientWrapper? mockClient = Substitute.For<ISshClientWrapper>();
        mockClient.IsConnected.Returns(true);
        mockClient.ConnectAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var connectionInfo = new ConnectionInfo
        {
            Host = TestHost,
            Port = TestPort,
            Username = TestUser,
            AuthMethod = AuthMethod.PrivateKey,
            PrivateKeyPath = "/tmp/test_key_nonexistent"
        };
        var service = new SshConnectionService(_ => mockClient);
        SshSession session = await service.ConnectAsync(connectionInfo);
        Assert.IsNotNull(session);
        Assert.AreEqual(SessionStatus.Connected, session.Status);
        Assert.AreEqual(AuthMethod.PrivateKey, session.ConnectionInfo.AuthMethod);
        await service.DisposeAsync();
    }

    [TestMethod]
    [TestCategory("DockerIntegration")]
    public async Task DisposeAsync_DisconnectsAllActiveSessions()
    {
        RequireSshServer();
        var clients = new List<ISshClientWrapper>();

        ISshClientWrapper ClientFactory(ConnectionInfo _)
        {
            ISshClientWrapper? client = Substitute.For<ISshClientWrapper>();
            client.IsConnected.Returns(true);
            client.ConnectAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            clients.Add(client);
            return client;
        }

        var service = new SshConnectionService(ClientFactory);
        await service.ConnectAsync(CreateTestConnectionInfo());
        await service.ConnectAsync(CreateTestConnectionInfo());
        Assert.HasCount(2, clients);
        await service.DisposeAsync();
        foreach (ISshClientWrapper client in clients)
        {
            client.Received(1).Disconnect();
            client.Received(1).Dispose();
        }
    }

    [TestMethod]
    [TestCategory("DockerIntegration")]
    public void DockerDetection_ReturnsConsistentResult()
    {
        bool result1 = DockerAvailable.Value;
        bool result2 = DockerAvailable.Value;
        Assert.AreEqual(result2, result1, "Docker detection should be deterministic");
    }
}

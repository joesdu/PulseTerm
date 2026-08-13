using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Core.Tests.Ssh;

[TestClass]
[TestCategory("SshConnection")]
public class SshConnectionServiceTests
{
    [TestMethod]
    public async Task ConnectAsync_WithPassword_ReturnsConnectedSession()
    {
        ISshClientWrapper? mockClientWrapper = Substitute.For<ISshClientWrapper>();
        mockClientWrapper.IsConnected.Returns(true);
        var connectionInfo = new ConnectionInfo
        {
            Host = "localhost",
            Port = 2222,
            Username = "testuser",
            AuthMethod = AuthMethod.Password,
            Password = "testpass"
        };
        var service = new SshConnectionService(_ => mockClientWrapper);
        SshSession session = await service.ConnectAsync(connectionInfo);
        Assert.IsNotNull(session);
        Assert.AreEqual(SessionStatus.Connected, session.Status);
        Assert.AreEqual(connectionInfo, session.ConnectionInfo);
        await mockClientWrapper.Received(1).ConnectAsync(Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task ConnectAsync_WithPrivateKey_ReturnsConnectedSession()
    {
        ISshClientWrapper? mockClientWrapper = Substitute.For<ISshClientWrapper>();
        mockClientWrapper.IsConnected.Returns(true);
        var connectionInfo = new ConnectionInfo
        {
            Host = "localhost",
            Port = 2222,
            Username = "testuser",
            AuthMethod = AuthMethod.PrivateKey,
            PrivateKeyPath = "/path/to/key"
        };
        var service = new SshConnectionService(_ => mockClientWrapper);
        SshSession session = await service.ConnectAsync(connectionInfo);
        Assert.IsNotNull(session);
        Assert.AreEqual(SessionStatus.Connected, session.Status);
        await mockClientWrapper.Received(1).ConnectAsync(Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task ConnectAsync_AuthFailure_ThrowsSshAuthenticationException()
    {
        ISshClientWrapper? mockClientWrapper = Substitute.For<ISshClientWrapper>();
        mockClientWrapper.When(x => x.ConnectAsync(Arg.Any<CancellationToken>()))
                         .Do(_ => throw new VelaSshAuthenticationException("Authentication failed"));
        var connectionInfo = new ConnectionInfo
        {
            Host = "localhost",
            Port = 2222,
            Username = "baduser",
            AuthMethod = AuthMethod.Password,
            Password = "wrongpass"
        };
        var service = new SshConnectionService(_ => mockClientWrapper);
        await Assert.ThrowsExactlyAsync<VelaSshAuthenticationException>(async () => await service.ConnectAsync(connectionInfo));
    }

    [TestMethod]
    public async Task ConnectAsync_ConnectionRefused_ThrowsSshConnectionException()
    {
        ISshClientWrapper? mockClientWrapper = Substitute.For<ISshClientWrapper>();
        mockClientWrapper.When(x => x.ConnectAsync(Arg.Any<CancellationToken>()))
                         .Do(_ => throw new VelaSshConnectionException("Connection refused"));
        var connectionInfo = new ConnectionInfo
        {
            Host = "localhost",
            Port = 9999,
            Username = "testuser",
            AuthMethod = AuthMethod.Password,
            Password = "testpass"
        };
        var service = new SshConnectionService(_ => mockClientWrapper);
        await Assert.ThrowsExactlyAsync<VelaSshConnectionException>(async () => await service.ConnectAsync(connectionInfo));
    }

    [TestMethod]
    public async Task DisconnectAsync_ExistingSession_DisconnectsSuccessfully()
    {
        ISshClientWrapper? mockClientWrapper = Substitute.For<ISshClientWrapper>();
        mockClientWrapper.IsConnected.Returns(true);
        var connectionInfo = new ConnectionInfo
        {
            Host = "localhost",
            Port = 2222,
            Username = "testuser",
            AuthMethod = AuthMethod.Password,
            Password = "testpass"
        };
        var service = new SshConnectionService(_ => mockClientWrapper);
        SshSession session = await service.ConnectAsync(connectionInfo);
        await service.DisconnectAsync(session.SessionId);
        mockClientWrapper.Received(1).Disconnect();
        Assert.AreEqual(SessionStatus.Disconnected, session.Status);
    }

    [TestMethod]
    public async Task ConnectAsync_ConcurrentSessions_BothSucceed()
    {
        ISshClientWrapper? mockClient1 = Substitute.For<ISshClientWrapper>();
        mockClient1.IsConnected.Returns(true);
        ISshClientWrapper? mockClient2 = Substitute.For<ISshClientWrapper>();
        mockClient2.IsConnected.Returns(true);
        var clients = new Queue<ISshClientWrapper>([mockClient1, mockClient2]);
        var service = new SshConnectionService(_ => clients.Dequeue());
        var connectionInfo1 = new ConnectionInfo
        {
            Host = "localhost",
            Port = 2222,
            Username = "user1",
            AuthMethod = AuthMethod.Password,
            Password = "pass1"
        };
        var connectionInfo2 = new ConnectionInfo
        {
            Host = "localhost",
            Port = 2222,
            Username = "user2",
            AuthMethod = AuthMethod.Password,
            Password = "pass2"
        };
        SshSession session1 = await service.ConnectAsync(connectionInfo1);
        SshSession session2 = await service.ConnectAsync(connectionInfo2);
        Assert.IsNotNull(session1);
        Assert.IsNotNull(session2);
        Assert.AreNotEqual(session2.SessionId, session1.SessionId);
        Assert.HasCount(2, service.Sessions);
    }

    [TestMethod]
    [TestCategory("EdgeCase")]
    public async Task ConnectAsync_SlowConnection_DoesNotBlockOtherConnections()
    {
        // 第一条连接的握手阻塞在 TCS 上(模拟高延迟/不可达主机);第二条应立即连上,
        // 不必等第一条超时。验证已移除全局串行锁(回归:此前会串行阻塞)。
        var gate = new TaskCompletionSource();
        ISshClientWrapper? slowClient = Substitute.For<ISshClientWrapper>();
        slowClient.IsConnected.Returns(true);
        slowClient.ConnectAsync(Arg.Any<CancellationToken>()).Returns(_ => gate.Task);
        ISshClientWrapper? fastClient = Substitute.For<ISshClientWrapper>();
        fastClient.IsConnected.Returns(true);
        fastClient.ConnectAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var clients = new Queue<ISshClientWrapper>([slowClient, fastClient]);
        var service = new SshConnectionService(_ => clients.Dequeue());
        var info = new ConnectionInfo
        {
            Host = "localhost",
            Port = 22,
            Username = "u",
            AuthMethod = AuthMethod.Password,
            Password = "p"
        };
        // 不假定哪次调用取到慢客户端(工厂在异步链内被调,两次调用可能反序出队,
        // 曾致本测试偶发超时):只验证核心不变量——一条被卡住时另一条仍能完成。
        Task<SshSession> first = service.ConnectAsync(info);
        Task<SshSession> second = service.ConnectAsync(info);
        Task<SshSession> completedTask = await Task.WhenAny(first, second)
            .WaitAsync(TimeSpan.FromSeconds(5));
        SshSession fast = await completedTask;
        Assert.AreEqual(SessionStatus.Connected, fast.Status);
        Task<SshSession> pending = ReferenceEquals(completedTask, first) ? second : first;
        Assert.IsFalse(pending.IsCompleted, "慢连接仍应在进行中,不应因串行而完成/被跳过。");
        gate.SetResult();
        SshSession slowSession = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(SessionStatus.Connected, slowSession.Status);
    }

    [TestMethod]
    public async Task ConnectAsync_FactoryReceivesConnectionInfo()
    {
        ISshClientWrapper? mockClientWrapper = Substitute.For<ISshClientWrapper>();
        mockClientWrapper.IsConnected.Returns(true);
        ConnectionInfo? receivedInfo = null;
        var service = new SshConnectionService(info =>
        {
            receivedInfo = info;
            return mockClientWrapper;
        });
        var connectionInfo = new ConnectionInfo
        {
            Host = "myhost.example.com",
            Port = 2222,
            Username = "testuser",
            AuthMethod = AuthMethod.Password,
            Password = "testpass"
        };
        await service.ConnectAsync(connectionInfo);
        Assert.AreEqual(connectionInfo, receivedInfo);
    }

    [TestMethod]
    public async Task GetClient_AfterConnect_ReturnsClient()
    {
        ISshClientWrapper? mockClientWrapper = Substitute.For<ISshClientWrapper>();
        mockClientWrapper.IsConnected.Returns(true);
        var connectionInfo = new ConnectionInfo
        {
            Host = "localhost",
            Port = 2222,
            Username = "testuser",
            AuthMethod = AuthMethod.Password,
            Password = "testpass"
        };
        var service = new SshConnectionService(_ => mockClientWrapper);
        SshSession session = await service.ConnectAsync(connectionInfo);
        ISshClientWrapper? client = service.GetClient(session.SessionId);
        Assert.AreSame(mockClientWrapper, client);
    }

    [TestMethod]
    public void GetClient_UnknownSessionId_ReturnsNull()
    {
        var service = new SshConnectionService(_ => Substitute.For<ISshClientWrapper>());
        ISshClientWrapper? client = service.GetClient(Guid.NewGuid());
        Assert.IsNull(client);
    }

    [TestMethod]
    public async Task ConnectAsync_Failure_DisposesClient()
    {
        ISshClientWrapper? mockClientWrapper = Substitute.For<ISshClientWrapper>();
        mockClientWrapper.When(x => x.ConnectAsync(Arg.Any<CancellationToken>()))
                         .Do(_ => throw new VelaSshConnectionException("Connection refused"));
        var connectionInfo = new ConnectionInfo
        {
            Host = "localhost",
            Port = 9999,
            Username = "testuser",
            AuthMethod = AuthMethod.Password,
            Password = "testpass"
        };
        var service = new SshConnectionService(_ => mockClientWrapper);
        await Assert.ThrowsExactlyAsync<VelaSshConnectionException>(async () => await service.ConnectAsync(connectionInfo));
        mockClientWrapper.Received(1).Dispose();
    }

    [TestMethod]
    public async Task ConnectAsync_Failure_RemovesSessionFromList()
    {
        ISshClientWrapper? mockClientWrapper = Substitute.For<ISshClientWrapper>();
        mockClientWrapper.When(x => x.ConnectAsync(Arg.Any<CancellationToken>()))
                         .Do(_ => throw new VelaSshConnectionException("Connection refused"));
        var connectionInfo = new ConnectionInfo
        {
            Host = "localhost",
            Port = 9999,
            Username = "testuser",
            AuthMethod = AuthMethod.Password,
            Password = "testpass"
        };
        var service = new SshConnectionService(_ => mockClientWrapper);
        await Assert.ThrowsExactlyAsync<VelaSshConnectionException>(async () => await service.ConnectAsync(connectionInfo));
        Assert.IsEmpty(service.Sessions);
    }

    [TestMethod]
    public async Task DisposeAsync_DisconnectsAndDisposesAllClients()
    {
        ISshClientWrapper? mockClient1 = Substitute.For<ISshClientWrapper>();
        mockClient1.IsConnected.Returns(true);
        ISshClientWrapper? mockClient2 = Substitute.For<ISshClientWrapper>();
        mockClient2.IsConnected.Returns(true);
        var clients = new Queue<ISshClientWrapper>([mockClient1, mockClient2]);
        var service = new SshConnectionService(_ => clients.Dequeue());
        var connectionInfo1 = new ConnectionInfo
        {
            Host = "localhost",
            Port = 2222,
            Username = "user1",
            AuthMethod = AuthMethod.Password,
            Password = "pass1"
        };
        var connectionInfo2 = new ConnectionInfo
        {
            Host = "localhost",
            Port = 2222,
            Username = "user2",
            AuthMethod = AuthMethod.Password,
            Password = "pass2"
        };
        await service.ConnectAsync(connectionInfo1);
        await service.ConnectAsync(connectionInfo2);
        await service.DisposeAsync();
        mockClient1.Received(1).Disconnect();
        mockClient1.Received(1).Dispose();
        mockClient2.Received(1).Disconnect();
        mockClient2.Received(1).Dispose();
    }

    [TestMethod]
    [TestCategory("EdgeCase")]
    public async Task ConnectAsync_CancellationToken_ThrowsTimeoutException()
    {
        ISshClientWrapper? mockClientWrapper = Substitute.For<ISshClientWrapper>();
        mockClientWrapper.When(x => x.ConnectAsync(Arg.Any<CancellationToken>()))
                         .Do(callInfo => { throw new OperationCanceledException("The operation was canceled."); });
        var connectionInfo = new ConnectionInfo
        {
            Host = "slow.example.com",
            Port = 22,
            Username = "testuser",
            AuthMethod = AuthMethod.Password,
            Password = "testpass"
        };
        var service = new SshConnectionService(_ => mockClientWrapper);
        TimeoutException ex = await Assert.ThrowsExactlyAsync<TimeoutException>(async () => await service.ConnectAsync(connectionInfo));
        Assert.Contains("slow.example.com", ex.Message);
        Assert.Contains("timed out", ex.Message);
        Assert.IsEmpty(service.Sessions);
    }

    [TestMethod]
    [TestCategory("EdgeCase")]
    public async Task ConnectAsync_CallerCancelled_PropagatesOperationCanceledException()
    {
        // 调用方自己取消(关标签/退出应用/凭据框上取消)必须保留取消语义:
        // 上层靠 catch(OperationCanceledException) 安静撤掉"连接中"标签。
        // 回归:此前所有取消一律翻成 TimeoutException,那条分支永远命中不了,
        // 用户取消反而拿到"连接超时"的失败覆盖层和一个连不上的死标签。
        using var cts = new CancellationTokenSource();
        ISshClientWrapper? mockClientWrapper = Substitute.For<ISshClientWrapper>();
        mockClientWrapper.When(x => x.ConnectAsync(Arg.Any<CancellationToken>()))
                         .Do(_ =>
                         {
                             cts.Cancel();
                             throw new OperationCanceledException(cts.Token);
                         });
        var connectionInfo = new ConnectionInfo
        {
            Host = "slow.example.com",
            Port = 22,
            Username = "testuser",
            AuthMethod = AuthMethod.Password,
            Password = "testpass"
        };
        var service = new SshConnectionService(_ => mockClientWrapper);
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await service.ConnectAsync(connectionInfo, cts.Token));
        mockClientWrapper.Received(1).Dispose();
        Assert.IsEmpty(service.Sessions);
    }

    [TestMethod]
    [TestCategory("EdgeCase")]
    public async Task RapidConnectDisconnect_NoRaceCondition()
    {
        ISshClientWrapper? mockClient = Substitute.For<ISshClientWrapper>();
        mockClient.IsConnected.Returns(true);
        var service = new SshConnectionService(_ => mockClient);
        var connectionInfo = new ConnectionInfo
        {
            Host = "localhost",
            Port = 2222,
            Username = "testuser",
            AuthMethod = AuthMethod.Password,
            Password = "testpass"
        };
        SshSession session = await service.ConnectAsync(connectionInfo);
        await service.DisconnectAsync(session.SessionId);
        Assert.AreEqual(SessionStatus.Disconnected, session.Status);
        Assert.IsNull(service.GetClient(session.SessionId));
    }

    [TestMethod]
    [TestCategory("EdgeCase")]
    public async Task DisconnectAsync_AlreadyDisconnected_DoesNotThrow()
    {
        ISshClientWrapper? mockClient = Substitute.For<ISshClientWrapper>();
        mockClient.IsConnected.Returns(true);
        var service = new SshConnectionService(_ => mockClient);
        var connectionInfo = new ConnectionInfo
        {
            Host = "localhost",
            Port = 2222,
            Username = "testuser",
            AuthMethod = AuthMethod.Password,
            Password = "testpass"
        };
        SshSession session = await service.ConnectAsync(connectionInfo);
        await service.DisconnectAsync(session.SessionId);
        await service.DisconnectAsync(session.SessionId);
        Assert.AreEqual(SessionStatus.Disconnected, session.Status);
    }
}

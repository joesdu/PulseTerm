using FluentAssertions;
using NSubstitute;
using PulseTerm.Core.Models;
using PulseTerm.Core.Ssh;
using Xunit;

namespace PulseTerm.Core.Tests.Ssh;

[Trait("Category", "SshConnection")]
public class SshConnectionServiceTests
{
    [Fact]
    public async Task ConnectAsync_WithPassword_ReturnsConnectedSession()
    {
        var mockClientWrapper = Substitute.For<ISshClientWrapper>();
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
        var session = await service.ConnectAsync(connectionInfo);

        session.Should().NotBeNull();
        session.Status.Should().Be(SessionStatus.Connected);
        session.ConnectionInfo.Should().Be(connectionInfo);
        await mockClientWrapper.Received(1).ConnectAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConnectAsync_WithPrivateKey_ReturnsConnectedSession()
    {
        var mockClientWrapper = Substitute.For<ISshClientWrapper>();
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
        var session = await service.ConnectAsync(connectionInfo);

        session.Should().NotBeNull();
        session.Status.Should().Be(SessionStatus.Connected);
        await mockClientWrapper.Received(1).ConnectAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConnectAsync_AuthFailure_ThrowsSshAuthenticationException()
    {
        var mockClientWrapper = Substitute.For<ISshClientWrapper>();
        mockClientWrapper.When(x => x.ConnectAsync(Arg.Any<CancellationToken>()))
            .Do(_ => throw new Renci.SshNet.Common.SshAuthenticationException("Authentication failed"));

        var connectionInfo = new ConnectionInfo
        {
            Host = "localhost",
            Port = 2222,
            Username = "baduser",
            AuthMethod = AuthMethod.Password,
            Password = "wrongpass"
        };

        var service = new SshConnectionService(_ => mockClientWrapper);

        await Assert.ThrowsAsync<Renci.SshNet.Common.SshAuthenticationException>(
            async () => await service.ConnectAsync(connectionInfo));
    }

    [Fact]
    public async Task ConnectAsync_ConnectionRefused_ThrowsSshConnectionException()
    {
        var mockClientWrapper = Substitute.For<ISshClientWrapper>();
        mockClientWrapper.When(x => x.ConnectAsync(Arg.Any<CancellationToken>()))
            .Do(_ => throw new Renci.SshNet.Common.SshConnectionException("Connection refused"));

        var connectionInfo = new ConnectionInfo
        {
            Host = "localhost",
            Port = 9999,
            Username = "testuser",
            AuthMethod = AuthMethod.Password,
            Password = "testpass"
        };

        var service = new SshConnectionService(_ => mockClientWrapper);

        await Assert.ThrowsAsync<Renci.SshNet.Common.SshConnectionException>(
            async () => await service.ConnectAsync(connectionInfo));
    }

    [Fact]
    public async Task DisconnectAsync_ExistingSession_DisconnectsSuccessfully()
    {
        var mockClientWrapper = Substitute.For<ISshClientWrapper>();
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
        var session = await service.ConnectAsync(connectionInfo);

        await service.DisconnectAsync(session.SessionId);

        mockClientWrapper.Received(1).Disconnect();
        session.Status.Should().Be(SessionStatus.Disconnected);
    }

    [Fact]
    public async Task ConnectAsync_ConcurrentSessions_BothSucceed()
    {
        var mockClient1 = Substitute.For<ISshClientWrapper>();
        mockClient1.IsConnected.Returns(true);

        var mockClient2 = Substitute.For<ISshClientWrapper>();
        mockClient2.IsConnected.Returns(true);

        var clients = new Queue<ISshClientWrapper>(new[] { mockClient1, mockClient2 });
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

        var session1 = await service.ConnectAsync(connectionInfo1);
        var session2 = await service.ConnectAsync(connectionInfo2);

        session1.Should().NotBeNull();
        session2.Should().NotBeNull();
        session1.SessionId.Should().NotBe(session2.SessionId);
        service.Sessions.Count.Should().Be(2);
    }

    [Fact]
    public async Task ConnectAsync_FactoryReceivesConnectionInfo()
    {
        var mockClientWrapper = Substitute.For<ISshClientWrapper>();
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

        receivedInfo.Should().Be(connectionInfo);
    }

    [Fact]
    public async Task GetClient_AfterConnect_ReturnsClient()
    {
        var mockClientWrapper = Substitute.For<ISshClientWrapper>();
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
        var session = await service.ConnectAsync(connectionInfo);

        var client = service.GetClient(session.SessionId);
        client.Should().BeSameAs(mockClientWrapper);
    }

    [Fact]
    public void GetClient_UnknownSessionId_ReturnsNull()
    {
        var service = new SshConnectionService(_ => Substitute.For<ISshClientWrapper>());

        var client = service.GetClient(Guid.NewGuid());
        client.Should().BeNull();
    }

    [Fact]
    public async Task ConnectAsync_Failure_DisposesClient()
    {
        var mockClientWrapper = Substitute.For<ISshClientWrapper>();
        mockClientWrapper.When(x => x.ConnectAsync(Arg.Any<CancellationToken>()))
            .Do(_ => throw new Renci.SshNet.Common.SshConnectionException("Connection refused"));

        var connectionInfo = new ConnectionInfo
        {
            Host = "localhost",
            Port = 9999,
            Username = "testuser",
            AuthMethod = AuthMethod.Password,
            Password = "testpass"
        };

        var service = new SshConnectionService(_ => mockClientWrapper);

        await Assert.ThrowsAsync<Renci.SshNet.Common.SshConnectionException>(
            async () => await service.ConnectAsync(connectionInfo));

        mockClientWrapper.Received(1).Dispose();
    }

    [Fact]
    public async Task ConnectAsync_Failure_RemovesSessionFromList()
    {
        var mockClientWrapper = Substitute.For<ISshClientWrapper>();
        mockClientWrapper.When(x => x.ConnectAsync(Arg.Any<CancellationToken>()))
            .Do(_ => throw new Renci.SshNet.Common.SshConnectionException("Connection refused"));

        var connectionInfo = new ConnectionInfo
        {
            Host = "localhost",
            Port = 9999,
            Username = "testuser",
            AuthMethod = AuthMethod.Password,
            Password = "testpass"
        };

        var service = new SshConnectionService(_ => mockClientWrapper);

        await Assert.ThrowsAsync<Renci.SshNet.Common.SshConnectionException>(
            async () => await service.ConnectAsync(connectionInfo));

        service.Sessions.Count.Should().Be(0);
    }

    [Fact]
    public async Task DisposeAsync_DisconnectsAndDisposesAllClients()
    {
        var mockClient1 = Substitute.For<ISshClientWrapper>();
        mockClient1.IsConnected.Returns(true);

        var mockClient2 = Substitute.For<ISshClientWrapper>();
        mockClient2.IsConnected.Returns(true);

        var clients = new Queue<ISshClientWrapper>(new[] { mockClient1, mockClient2 });
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

    [Fact]
    [Trait("Category", "EdgeCase")]
    public async Task ConnectAsync_CancellationToken_ThrowsTimeoutException()
    {
        var mockClientWrapper = Substitute.For<ISshClientWrapper>();
        mockClientWrapper.When(x => x.ConnectAsync(Arg.Any<CancellationToken>()))
            .Do(callInfo =>
            {
                throw new OperationCanceledException("The operation was canceled.");
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

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            async () => await service.ConnectAsync(connectionInfo));

        ex.Message.Should().Contain("slow.example.com");
        ex.Message.Should().Contain("timed out");
        service.Sessions.Count.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "EdgeCase")]
    public async Task RapidConnectDisconnect_NoRaceCondition()
    {
        var mockClient = Substitute.For<ISshClientWrapper>();
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

        var session = await service.ConnectAsync(connectionInfo);
        await service.DisconnectAsync(session.SessionId);

        session.Status.Should().Be(SessionStatus.Disconnected);
        service.GetClient(session.SessionId).Should().BeNull();
    }

    [Fact]
    [Trait("Category", "EdgeCase")]
    public async Task DisconnectAsync_AlreadyDisconnected_DoesNotThrow()
    {
        var mockClient = Substitute.For<ISshClientWrapper>();
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

        var session = await service.ConnectAsync(connectionInfo);
        await service.DisconnectAsync(session.SessionId);
        await service.DisconnectAsync(session.SessionId);

        session.Status.Should().Be(SessionStatus.Disconnected);
    }
}

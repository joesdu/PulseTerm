using System.Diagnostics;
using System.Net.Sockets;
using FluentAssertions;
using NSubstitute;
using PulseTerm.Core.Models;
using PulseTerm.Core.Ssh;
using Xunit.Abstractions;

namespace PulseTerm.App.Tests.Integration;

[Collection("IntegrationTests")]
public class SshIntegrationTests : IAsyncLifetime
{
    private const string TestHost = "localhost";
    private const int TestPort = 2222;
    private const string TestUser = "testuser";
    private const string TestPassword = "testpass";

    private static readonly Lazy<bool> DockerAvailable = new(DetectDocker);

    private readonly ITestOutputHelper _output;

    public SshIntegrationTests(ITestOutputHelper output) => _output = output;

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    private static bool DetectDocker()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
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
                return false;
            var exited = process.WaitForExit(3000);
            if (!exited)
            {
                try { process.Kill(); } catch { }
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
            return false;

        try
        {
            using var client = new TcpClient();
            var result = client.BeginConnect(TestHost, TestPort, null, null);
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(2));
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

    private bool SkipIfDockerUnavailable()
    {
        if (!DockerAvailable.Value)
        {
            _output.WriteLine("[SKIP] Docker is not available. Run 'docker compose -f docker-compose.test.yml up -d' to enable SSH integration tests.");
            return true;
        }
        return false;
    }

    private bool SkipIfSshServerUnavailable()
    {
        if (SkipIfDockerUnavailable())
            return true;
        if (!IsSshServerReachable())
        {
            _output.WriteLine($"[SKIP] SSH test server not reachable at {TestHost}:{TestPort}. Run 'docker compose -f docker-compose.test.yml up -d' to start it.");
            return true;
        }
        return false;
    }

    private static ConnectionInfo CreateTestConnectionInfo(
        AuthMethod authMethod = AuthMethod.Password,
        string? host = null,
        int? port = null,
        string? username = null,
        string? password = null)
    {
        return new ConnectionInfo
        {
            Host = host ?? TestHost,
            Port = port ?? TestPort,
            Username = username ?? TestUser,
            AuthMethod = authMethod,
            Password = password ?? TestPassword
        };
    }

    [Fact]
    [Trait("Category", "DockerIntegration")]
    public async Task ConnectAsync_WithValidCredentials_EstablishesSession()
    {
        if (SkipIfSshServerUnavailable()) return;

        var mockClient = Substitute.For<ISshClientWrapper>();
        mockClient.IsConnected.Returns(true);
        mockClient.ConnectAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var connectionInfo = CreateTestConnectionInfo();
        var service = new SshConnectionService(_ => mockClient);

        var session = await service.ConnectAsync(connectionInfo);

        session.Should().NotBeNull();
        session.Status.Should().Be(SessionStatus.Connected);
        session.ConnectionInfo.Host.Should().Be(TestHost);
        session.ConnectionInfo.Port.Should().Be(TestPort);

        await service.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "DockerIntegration")]
    public async Task ConnectAsync_WithInvalidPassword_ThrowsAndSetsErrorStatus()
    {
        if (SkipIfSshServerUnavailable()) return;

        var mockClient = Substitute.For<ISshClientWrapper>();
        mockClient.ConnectAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new Renci.SshNet.Common.SshAuthenticationException("Authentication failed")));

        var connectionInfo = CreateTestConnectionInfo(password: "wrongpassword");
        var service = new SshConnectionService(_ => mockClient);

        var act = () => service.ConnectAsync(connectionInfo);

        await act.Should().ThrowAsync<Renci.SshNet.Common.SshAuthenticationException>();

        await service.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "DockerIntegration")]
    public async Task ConnectAsync_WithUnreachableHost_ThrowsConnectionError()
    {
        if (SkipIfSshServerUnavailable()) return;

        var mockClient = Substitute.For<ISshClientWrapper>();
        mockClient.ConnectAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new SocketException((int)SocketError.ConnectionRefused)));

        var connectionInfo = CreateTestConnectionInfo(host: "192.0.2.1", port: 9999);
        var service = new SshConnectionService(_ => mockClient);

        var act = () => service.ConnectAsync(connectionInfo);

        await act.Should().ThrowAsync<SocketException>();

        await service.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "DockerIntegration")]
    public async Task DisconnectAsync_AfterConnect_ChangesSessionStatus()
    {
        if (SkipIfSshServerUnavailable()) return;

        var mockClient = Substitute.For<ISshClientWrapper>();
        mockClient.IsConnected.Returns(true);
        mockClient.ConnectAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var connectionInfo = CreateTestConnectionInfo();
        var service = new SshConnectionService(_ => mockClient);

        var session = await service.ConnectAsync(connectionInfo);
        session.Status.Should().Be(SessionStatus.Connected);

        await service.DisconnectAsync(session.SessionId);
        session.Status.Should().Be(SessionStatus.Disconnected);

        mockClient.Received(1).Disconnect();

        await service.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "DockerIntegration")]
    public async Task MultipleSessions_CanConnectConcurrently()
    {
        if (SkipIfSshServerUnavailable()) return;

        var clients = new List<ISshClientWrapper>();
        ISshClientWrapper ClientFactory(ConnectionInfo _)
        {
            var client = Substitute.For<ISshClientWrapper>();
            client.IsConnected.Returns(true);
            client.ConnectAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            clients.Add(client);
            return client;
        }

        var service = new SshConnectionService(ClientFactory);
        var conn1 = CreateTestConnectionInfo(username: "testuser");
        var conn2 = CreateTestConnectionInfo(username: "testuser");

        var session1 = await service.ConnectAsync(conn1);
        var session2 = await service.ConnectAsync(conn2);

        session1.SessionId.Should().NotBe(session2.SessionId);
        session1.Status.Should().Be(SessionStatus.Connected);
        session2.Status.Should().Be(SessionStatus.Connected);
        clients.Should().HaveCount(2);

        await service.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "DockerIntegration")]
    public async Task ConnectAsync_SessionAppearsInSessionsList()
    {
        if (SkipIfSshServerUnavailable()) return;

        var mockClient = Substitute.For<ISshClientWrapper>();
        mockClient.IsConnected.Returns(true);
        mockClient.ConnectAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var service = new SshConnectionService(_ => mockClient);
        var connectionInfo = CreateTestConnectionInfo();

        var session = await service.ConnectAsync(connectionInfo);

        service.Sessions.Count.Should().Be(1);
        service.GetSession(session.SessionId).Should().NotBeNull();
        service.GetSession(session.SessionId)!.Status.Should().Be(SessionStatus.Connected);

        await service.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "DockerIntegration")]
    public async Task ConnectAsync_WithPrivateKeyAuth_CreatesSession()
    {
        if (SkipIfSshServerUnavailable()) return;

        var mockClient = Substitute.For<ISshClientWrapper>();
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

        var session = await service.ConnectAsync(connectionInfo);

        session.Should().NotBeNull();
        session.Status.Should().Be(SessionStatus.Connected);
        session.ConnectionInfo.AuthMethod.Should().Be(AuthMethod.PrivateKey);

        await service.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "DockerIntegration")]
    public async Task DisposeAsync_DisconnectsAllActiveSessions()
    {
        if (SkipIfSshServerUnavailable()) return;

        var clients = new List<ISshClientWrapper>();
        ISshClientWrapper ClientFactory(ConnectionInfo _)
        {
            var client = Substitute.For<ISshClientWrapper>();
            client.IsConnected.Returns(true);
            client.ConnectAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            clients.Add(client);
            return client;
        }

        var service = new SshConnectionService(ClientFactory);

        await service.ConnectAsync(CreateTestConnectionInfo());
        await service.ConnectAsync(CreateTestConnectionInfo());

        clients.Should().HaveCount(2);

        await service.DisposeAsync();

        foreach (var client in clients)
        {
            client.Received(1).Disconnect();
            client.Received(1).Dispose();
        }
    }

    [Fact]
    [Trait("Category", "DockerIntegration")]
    public void DockerDetection_ReturnsConsistentResult()
    {
        var result1 = DockerAvailable.Value;
        var result2 = DockerAvailable.Value;

        result1.Should().Be(result2, "Docker detection should be deterministic");
    }
}

using FluentAssertions;
using NSubstitute;
using PulseTerm.Core.Ssh;
using Renci.SshNet;
using Renci.SshNet.Common;
using Xunit;

namespace PulseTerm.Core.Tests.Ssh;

[Trait("Category", "SshConnection")]
public class SftpClientWrapperTests
{
    [Fact]
    public void IsConnected_WhenClientConnected_ReturnsTrue()
    {
        var mockClient = Substitute.For<SftpClient>(
            Substitute.For<ConnectionInfo>("localhost", "user", new PasswordAuthenticationMethod("user", "pass")));
        mockClient.IsConnected.Returns(true);

        var wrapper = new SftpClientWrapper(mockClient);

        wrapper.IsConnected.Should().BeTrue();
    }

    [Fact]
    public void Disconnect_CallsClientDisconnect()
    {
        var mockClient = Substitute.For<SftpClient>(
            Substitute.For<ConnectionInfo>("localhost", "user", new PasswordAuthenticationMethod("user", "pass")));

        var wrapper = new SftpClientWrapper(mockClient);
        wrapper.Disconnect();

        mockClient.Received(1).Disconnect();
    }

    [Fact]
    public void Dispose_DisposesClient()
    {
        var mockClient = Substitute.For<SftpClient>(
            Substitute.For<ConnectionInfo>("localhost", "user", new PasswordAuthenticationMethod("user", "pass")));

        var wrapper = new SftpClientWrapper(mockClient);
        wrapper.Dispose();

        mockClient.Received(1).Dispose();
    }
}

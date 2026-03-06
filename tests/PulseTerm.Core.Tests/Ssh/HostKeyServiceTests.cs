using FluentAssertions;
using PulseTerm.Core.Data;
using PulseTerm.Core.Models;
using PulseTerm.Core.Ssh;
using Xunit;

namespace PulseTerm.Core.Tests.Ssh;

[Trait("Category", "HostKey")]
public class HostKeyServiceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _knownHostsPath;
    private readonly JsonDataStore _dataStore;
    private readonly HostKeyService _sut;

    public HostKeyServiceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"pulseterm_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
        _knownHostsPath = Path.Combine(_testDirectory, "known_hosts.json");
        _dataStore = new JsonDataStore();
        _sut = new HostKeyService(_dataStore, _knownHostsPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    [Fact]
    [Trait("Category", "HostKey")]
    public async Task VerifyHostKeyAsync_UnknownHost_ReturnsUnknown()
    {
        var result = await _sut.VerifyHostKeyAsync("example.com", 22, "ssh-rsa", "SHA256:abc123");

        result.Should().Be(HostKeyVerification.Unknown);
    }

    [Fact]
    [Trait("Category", "HostKey")]
    public async Task TrustHostKey_ThenVerify_ReturnsTrusted()
    {
        await _sut.TrustHostKeyAsync("example.com", 22, "ssh-rsa", "SHA256:abc123");

        var result = await _sut.VerifyHostKeyAsync("example.com", 22, "ssh-rsa", "SHA256:abc123");

        result.Should().Be(HostKeyVerification.Trusted);
    }

    [Fact]
    [Trait("Category", "HostKey")]
    public async Task VerifyHostKeyAsync_FingerprintChanged_ReturnsChanged()
    {
        await _sut.TrustHostKeyAsync("example.com", 22, "ssh-rsa", "SHA256:original");

        var result = await _sut.VerifyHostKeyAsync("example.com", 22, "ssh-rsa", "SHA256:different");

        result.Should().Be(HostKeyVerification.Changed);
    }

    [Fact]
    [Trait("Category", "HostKey")]
    public async Task RemoveKnownHostAsync_RemovesTrustedHost()
    {
        await _sut.TrustHostKeyAsync("example.com", 22, "ssh-rsa", "SHA256:abc123");

        await _sut.RemoveKnownHostAsync("example.com", 22);

        var result = await _sut.VerifyHostKeyAsync("example.com", 22, "ssh-rsa", "SHA256:abc123");
        result.Should().Be(HostKeyVerification.Unknown);
    }

    [Fact]
    [Trait("Category", "HostKey")]
    public async Task GetKnownHostsAsync_ReturnsAllTrustedHosts()
    {
        await _sut.TrustHostKeyAsync("host1.com", 22, "ssh-rsa", "SHA256:aaa");
        await _sut.TrustHostKeyAsync("host2.com", 2222, "ssh-ed25519", "SHA256:bbb");

        var hosts = await _sut.GetKnownHostsAsync();

        hosts.Should().HaveCount(2);
        hosts.Should().Contain(h => h.Host == "host1.com" && h.Port == 22);
        hosts.Should().Contain(h => h.Host == "host2.com" && h.Port == 2222);
    }

    [Fact]
    [Trait("Category", "HostKey")]
    public async Task TrustHostKeyAsync_SameHostDifferentPort_StoresBoth()
    {
        await _sut.TrustHostKeyAsync("example.com", 22, "ssh-rsa", "SHA256:aaa");
        await _sut.TrustHostKeyAsync("example.com", 2222, "ssh-rsa", "SHA256:bbb");

        var hosts = await _sut.GetKnownHostsAsync();

        hosts.Should().HaveCount(2);
    }

    [Fact]
    [Trait("Category", "HostKey")]
    public async Task TrustHostKeyAsync_UpdatesLastSeenAt_OnRepeatedTrust()
    {
        await _sut.TrustHostKeyAsync("example.com", 22, "ssh-rsa", "SHA256:abc123");
        var hostsBefore = await _sut.GetKnownHostsAsync();
        var firstSeenBefore = hostsBefore.Single().FirstSeenAt;

        await Task.Delay(50);
        await _sut.TrustHostKeyAsync("example.com", 22, "ssh-rsa", "SHA256:abc123");

        var hostsAfter = await _sut.GetKnownHostsAsync();
        hostsAfter.Should().ContainSingle();
        hostsAfter.Single().FirstSeenAt.Should().Be(firstSeenBefore);
        hostsAfter.Single().LastSeenAt.Should().BeOnOrAfter(firstSeenBefore);
    }

    [Fact]
    [Trait("Category", "HostKey")]
    public async Task Persistence_DataSurvivesNewServiceInstance()
    {
        await _sut.TrustHostKeyAsync("example.com", 22, "ssh-rsa", "SHA256:abc123");

        var newService = new HostKeyService(_dataStore, _knownHostsPath);
        var result = await newService.VerifyHostKeyAsync("example.com", 22, "ssh-rsa", "SHA256:abc123");

        result.Should().Be(HostKeyVerification.Trusted);
    }

    [Fact]
    [Trait("Category", "HostKey")]
    public async Task RemoveKnownHostAsync_NonExistentHost_DoesNotThrow()
    {
        var act = async () => await _sut.RemoveKnownHostAsync("nonexistent.com", 22);

        await act.Should().NotThrowAsync();
    }
}

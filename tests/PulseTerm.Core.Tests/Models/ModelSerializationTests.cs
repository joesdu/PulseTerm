using System.Text.Json;
using FluentAssertions;
using PulseTerm.Core.Models;
using Xunit;

namespace PulseTerm.Core.Tests.Models;

[Trait("Category", "DataStore")]
public class ModelSerializationTests
{
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void SessionProfile_ShouldSerializeWithCamelCase()
    {
        var session = new SessionProfile
        {
            Id = Guid.NewGuid(),
            Name = "Test Server",
            Host = "192.168.1.100",
            Port = 2222,
            Username = "admin",
            AuthMethod = AuthMethod.Password,
            Password = "secret123",
            PrivateKeyPath = "/path/to/key",
            PrivateKeyPassphrase = "passphrase",
            GroupId = Guid.NewGuid(),
            LastConnectedAt = new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc),
            Tags = new List<string> { "production", "critical" }
        };

        var json = JsonSerializer.Serialize(session, _options);

        json.Should().Contain("\"name\":");
        json.Should().Contain("\"host\":");
        json.Should().Contain("\"username\":");
        json.Should().Contain("\"password\":");
        json.Should().Contain("\"privateKeyPath\":");
        json.Should().Contain("\"privateKeyPassphrase\":");
        json.Should().Contain("\"groupId\":");
        json.Should().Contain("\"lastConnectedAt\":");
        json.Should().NotContain("\"Name\":");
        json.Should().NotContain("\"Host\":");
    }

    [Fact]
    public void SessionProfile_ShouldDeserializeCorrectly()
    {
        var id = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var json = $$"""
        {
          "id": "{{id}}",
          "name": "Test Server",
          "host": "192.168.1.100",
          "port": 2222,
          "username": "admin",
          "authMethod": 0,
          "password": "secret123",
          "privateKeyPath": "/path/to/key",
          "privateKeyPassphrase": "passphrase",
          "groupId": "{{groupId}}",
          "lastConnectedAt": "2026-03-05T12:00:00Z",
          "tags": ["production", "critical"]
        }
        """;

        var session = JsonSerializer.Deserialize<SessionProfile>(json, _options);

        session.Should().NotBeNull();
        session!.Id.Should().Be(id);
        session.Name.Should().Be("Test Server");
        session.Host.Should().Be("192.168.1.100");
        session.Port.Should().Be(2222);
        session.Username.Should().Be("admin");
        session.AuthMethod.Should().Be(AuthMethod.Password);
        session.Password.Should().Be("secret123");
        session.PrivateKeyPath.Should().Be("/path/to/key");
        session.PrivateKeyPassphrase.Should().Be("passphrase");
        session.GroupId.Should().Be(groupId);
        session.LastConnectedAt.Should().Be(new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc));
        session.Tags.Should().BeEquivalentTo(new[] { "production", "critical" });
    }

    [Fact]
    public void AppSettings_ShouldSerializeWithCamelCase()
    {
        var settings = new AppSettings
        {
            Language = "en",
            Theme = "dark",
            TerminalFont = "JetBrains Mono",
            TerminalFontSize = 14,
            ScrollbackLines = 10000,
            DefaultPort = 22
        };

        var json = JsonSerializer.Serialize(settings, _options);

        json.Should().Contain("\"language\":");
        json.Should().Contain("\"theme\":");
        json.Should().Contain("\"terminalFont\":");
        json.Should().Contain("\"terminalFontSize\":");
        json.Should().Contain("\"scrollbackLines\":");
        json.Should().Contain("\"defaultPort\":");
    }

    [Fact]
    public void AppSettings_ShouldDeserializeCorrectly()
    {
        var json = """
        {
          "language": "zh",
          "theme": "light",
          "terminalFont": "Consolas",
          "terminalFontSize": 16,
          "scrollbackLines": 5000,
          "defaultPort": 2222
        }
        """;

        var settings = JsonSerializer.Deserialize<AppSettings>(json, _options);

        settings.Should().NotBeNull();
        settings!.Language.Should().Be("zh");
        settings.Theme.Should().Be("light");
        settings.TerminalFont.Should().Be("Consolas");
        settings.TerminalFontSize.Should().Be(16);
        settings.ScrollbackLines.Should().Be(5000);
        settings.DefaultPort.Should().Be(2222);
    }

    [Fact]
    public void AppState_ShouldSerializeWithNestedObjects()
    {
        var state = new AppState
        {
            RecentConnections = new List<string> { "session1", "session2" },
            WindowPosition = new WindowPosition { X = 100, Y = 200 },
            WindowSize = new WindowSize { Width = 1024, Height = 768 },
            LastActiveTab = "tab1"
        };

        var json = JsonSerializer.Serialize(state, _options);

        json.Should().Contain("\"recentConnections\":");
        json.Should().Contain("\"windowPosition\":");
        json.Should().Contain("\"windowSize\":");
        json.Should().Contain("\"lastActiveTab\":");
        json.Should().Contain("\"x\":");
        json.Should().Contain("\"y\":");
        json.Should().Contain("\"width\":");
        json.Should().Contain("\"height\":");
    }

    [Fact]
    public void ServerGroup_ShouldSerializeWithSessionsList()
    {
        var group = new ServerGroup
        {
            Id = Guid.NewGuid(),
            Name = "Production",
            Icon = "server",
            SortOrder = 1,
            Sessions = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() }
        };

        var json = JsonSerializer.Serialize(group, _options);

        json.Should().Contain("\"id\":");
        json.Should().Contain("\"name\":");
        json.Should().Contain("\"icon\":");
        json.Should().Contain("\"sortOrder\":");
        json.Should().Contain("\"sessions\":");
    }

    [Fact]
    public void KnownHost_ShouldSerializeWithDates()
    {
        var host = new KnownHost
        {
            HostKey = "AAAAB3NzaC1...",
            Fingerprint = "SHA256:abc123...",
            Algorithm = "ssh-rsa",
            FirstSeenAt = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc),
            LastSeenAt = new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc)
        };

        var json = JsonSerializer.Serialize(host, _options);

        json.Should().Contain("\"hostKey\":");
        json.Should().Contain("\"fingerprint\":");
        json.Should().Contain("\"algorithm\":");
        json.Should().Contain("\"firstSeenAt\":");
        json.Should().Contain("\"lastSeenAt\":");
    }

    [Fact]
    public void SessionProfile_RoundTripSerialization_ShouldPreserveAllProperties()
    {
        var original = new SessionProfile
        {
            Id = Guid.NewGuid(),
            Name = "Test",
            Host = "example.com",
            Port = 2222,
            Username = "user",
            AuthMethod = AuthMethod.PrivateKey,
            Password = "pass",
            PrivateKeyPath = "/key",
            PrivateKeyPassphrase = "phrase",
            GroupId = Guid.NewGuid(),
            LastConnectedAt = DateTime.UtcNow,
            Tags = new List<string> { "tag1", "tag2" }
        };

        var json = JsonSerializer.Serialize(original, _options);
        var deserialized = JsonSerializer.Deserialize<SessionProfile>(json, _options);

        deserialized.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void AppSettings_RoundTripSerialization_ShouldPreserveAllProperties()
    {
        var original = new AppSettings
        {
            Language = "fr",
            Theme = "dark",
            TerminalFont = "Fira Code",
            TerminalFontSize = 18,
            ScrollbackLines = 20000,
            DefaultPort = 2222
        };

        var json = JsonSerializer.Serialize(original, _options);
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json, _options);

        deserialized.Should().BeEquivalentTo(original);
    }
}

using System.Text.Json;
using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Models;

[TestClass]
[TestCategory("DataStore")]
public class ModelSerializationTests
{
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [TestMethod]
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
            Tags = ["production", "critical"]
        };
        string json = JsonSerializer.Serialize(session, _options);
        Assert.Contains("\"name\":", json);
        Assert.Contains("\"host\":", json);
        Assert.Contains("\"username\":", json);
        Assert.Contains("\"password\":", json);
        Assert.Contains("\"privateKeyPath\":", json);
        Assert.Contains("\"privateKeyPassphrase\":", json);
        Assert.Contains("\"groupId\":", json);
        Assert.Contains("\"lastConnectedAt\":", json);
        Assert.Contains("\"connectionType\":", json);
        Assert.DoesNotContain("\"Name\":", json);
        Assert.DoesNotContain("\"Host\":", json);
    }

    [TestMethod]
    public void SessionProfile_ShouldDeserializeCorrectly()
    {
        var id = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        string json = $$"""
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
        SessionProfile? session = JsonSerializer.Deserialize<SessionProfile>(json, _options);
        Assert.IsNotNull(session);
        Assert.AreEqual(id, session!.Id);
        Assert.AreEqual("Test Server", session.Name);
        Assert.AreEqual("192.168.1.100", session.Host);
        Assert.AreEqual(2222, session.Port);
        Assert.AreEqual("admin", session.Username);
        Assert.AreEqual(AuthMethod.Password, session.AuthMethod);
        Assert.AreEqual("secret123", session.Password);
        Assert.AreEqual("/path/to/key", session.PrivateKeyPath);
        Assert.AreEqual("passphrase", session.PrivateKeyPassphrase);
        Assert.AreEqual(groupId, session.GroupId);
        Assert.AreEqual(new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc), session.LastConnectedAt);
        Assert.AreSequenceEqual(["production", "critical"], [.. session.Tags], SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public void SessionProfile_ConnectionType_DefaultsToSsh_AndRoundTripsSftp()
    {
        string legacyJson = "{\"name\":\"legacy\"}";
        SessionProfile? legacy = JsonSerializer.Deserialize<SessionProfile>(legacyJson, _options);
        Assert.IsNotNull(legacy);
        Assert.AreEqual(ConnectionType.SSH, legacy!.ConnectionType);

        SessionProfile sftp = new() { ConnectionType = ConnectionType.SFTP };
        string json = JsonSerializer.Serialize(sftp, _options);
        SessionProfile? roundTrip = JsonSerializer.Deserialize<SessionProfile>(json, _options);
        Assert.IsNotNull(roundTrip);
        Assert.AreEqual(ConnectionType.SFTP, roundTrip!.ConnectionType);

        SessionProfile? invalid = JsonSerializer.Deserialize<SessionProfile>(
            "{\"connectionType\":99}",
            _options
        );
        Assert.IsNotNull(invalid);
        Assert.AreEqual(ConnectionType.SSH, invalid!.ConnectionType);
    }

    [TestMethod]
    public void AppearanceOptions_BackgroundImage_DefaultsAndRoundTrip()
    {
        // 默认:无背景图,行为与旧版一致(路径空、图片不透明度 100、内容背景不透明度 85 但仅在设图后生效)。
        var fresh = new AppearanceOptions();
        Assert.AreEqual("", fresh.BackgroundImagePath);
        Assert.AreEqual(100, fresh.BackgroundImageOpacity);
        Assert.AreEqual(85, fresh.ContentBackgroundOpacity);

        var settings = new AppSettings();
        settings.Appearance.BackgroundImagePath = "/home/user/wall.png";
        settings.Appearance.BackgroundImageOpacity = 60;
        settings.Appearance.ContentBackgroundOpacity = 40;
        string json = JsonSerializer.Serialize(settings, _options);
        Assert.Contains("\"backgroundImagePath\":", json);
        var back = JsonSerializer.Deserialize<AppSettings>(json, _options)!;
        Assert.AreEqual("/home/user/wall.png", back.Appearance.BackgroundImagePath);
        Assert.AreEqual(60, back.Appearance.BackgroundImageOpacity);
        Assert.AreEqual(40, back.Appearance.ContentBackgroundOpacity);
    }

    [TestMethod]
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
        string json = JsonSerializer.Serialize(settings, _options);
        Assert.Contains("\"language\":", json);
        Assert.Contains("\"theme\":", json);
        Assert.Contains("\"terminalFont\":", json);
        Assert.Contains("\"terminalFontSize\":", json);
        Assert.Contains("\"scrollbackLines\":", json);
        Assert.Contains("\"defaultPort\":", json);
    }

    [TestMethod]
    public void AppSettings_ShouldDeserializeCorrectly()
    {
        string json = """
                      {
                        "language": "zh",
                        "theme": "light",
                        "terminalFont": "Consolas",
                        "terminalFontSize": 16,
                        "scrollbackLines": 5000,
                        "defaultPort": 2222
                      }
                      """;
        AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json, _options);
        Assert.IsNotNull(settings);
        Assert.AreEqual("zh", settings!.Language);
        Assert.AreEqual("light", settings.Theme);
        Assert.AreEqual("Consolas", settings.TerminalFont);
        Assert.AreEqual(16, settings.TerminalFontSize);
        Assert.AreEqual(5000, settings.ScrollbackLines);
        Assert.AreEqual(2222, settings.DefaultPort);
    }

    [TestMethod]
    public void AppState_ShouldSerializeWithNestedObjects()
    {
        var state = new AppState
        {
            RecentConnections = ["session1", "session2"],
            WindowPosition = new() { X = 100, Y = 200 },
            WindowSize = new() { Width = 1024, Height = 768 },
            LastActiveTab = "tab1"
        };
        string json = JsonSerializer.Serialize(state, _options);
        Assert.Contains("\"recentConnections\":", json);
        Assert.Contains("\"windowPosition\":", json);
        Assert.Contains("\"windowSize\":", json);
        Assert.Contains("\"lastActiveTab\":", json);
        Assert.Contains("\"x\":", json);
        Assert.Contains("\"y\":", json);
        Assert.Contains("\"width\":", json);
        Assert.Contains("\"height\":", json);
    }

    [TestMethod]
    public void ServerGroup_ShouldSerializeWithSessionsList()
    {
        var group = new ServerGroup
        {
            Id = Guid.NewGuid(),
            Name = "Production",
            Icon = "server",
            SortOrder = 1,
            Sessions = [Guid.NewGuid(), Guid.NewGuid()]
        };
        string json = JsonSerializer.Serialize(group, _options);
        Assert.Contains("\"id\":", json);
        Assert.Contains("\"name\":", json);
        Assert.Contains("\"icon\":", json);
        Assert.Contains("\"sortOrder\":", json);
        Assert.Contains("\"sessions\":", json);
    }

    [TestMethod]
    public void KnownHost_ShouldSerializeWithDates()
    {
        var host = new KnownHost
        {
            HostKey = "AAAAB3NzaC1...",
            Fingerprint = "SHA256:abc123...",
            Algorithm = "ssh-rsa",
            FirstSeenAt = new(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc),
            LastSeenAt = new(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc)
        };
        string json = JsonSerializer.Serialize(host, _options);
        Assert.Contains("\"hostKey\":", json);
        Assert.Contains("\"fingerprint\":", json);
        Assert.Contains("\"algorithm\":", json);
        Assert.Contains("\"firstSeenAt\":", json);
        Assert.Contains("\"lastSeenAt\":", json);
    }

    [TestMethod]
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
            Tags = ["tag1", "tag2"]
        };
        string json = JsonSerializer.Serialize(original, _options);
        SessionProfile? deserialized = JsonSerializer.Deserialize<SessionProfile>(json, _options);
        Assert.AreEqual(JsonSerializer.Serialize(original, _options),
            JsonSerializer.Serialize(deserialized, _options));
    }

    [TestMethod]
    public void RecentConnectionEntry_ConnectionType_DefaultsAndNormalizesUnknownValues()
    {
        RecentConnectionEntry? legacy = JsonSerializer.Deserialize<RecentConnectionEntry>("{}", _options);
        Assert.IsNotNull(legacy);
        Assert.AreEqual(ConnectionType.SSH, legacy!.ConnectionType);

        RecentConnectionEntry? sftp = JsonSerializer.Deserialize<RecentConnectionEntry>(
            "{\"connectionType\":1}",
            _options
        );
        Assert.IsNotNull(sftp);
        Assert.AreEqual(ConnectionType.SFTP, sftp!.ConnectionType);

        RecentConnectionEntry? invalid = JsonSerializer.Deserialize<RecentConnectionEntry>(
            "{\"connectionType\":99}",
            _options
        );
        Assert.IsNotNull(invalid);
        Assert.AreEqual(ConnectionType.SSH, invalid!.ConnectionType);
    }

    [TestMethod]
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
        string json = JsonSerializer.Serialize(original, _options);
        AppSettings? deserialized = JsonSerializer.Deserialize<AppSettings>(json, _options);
        Assert.AreEqual(JsonSerializer.Serialize(original, _options),
            JsonSerializer.Serialize(deserialized, _options));
    }
}

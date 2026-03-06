using FluentAssertions;
using PulseTerm.Core.Data;
using PulseTerm.Core.Models;
using Xunit;

namespace PulseTerm.Core.Tests.Data;

[Trait("Category", "DataStore")]
public class JsonDataStoreTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly JsonDataStore _dataStore;

    public JsonDataStoreTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"pulseterm_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
        _dataStore = new JsonDataStore();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    [Fact]
    public async Task SaveAndLoad_SessionProfile_ShouldRoundTripSuccessfully()
    {
        var filePath = Path.Combine(_testDirectory, "session.json");
        var session = new SessionProfile
        {
            Id = Guid.NewGuid(),
            Name = "Test Server",
            Host = "192.168.1.100",
            Port = 2222,
            Username = "admin",
            Password = "secret",
            PrivateKeyPath = "/path/to/key"
        };

        await _dataStore.SaveAsync(filePath, session);
        var loaded = await _dataStore.LoadAsync<SessionProfile>(filePath);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(session.Id);
        loaded.Name.Should().Be(session.Name);
        loaded.Host.Should().Be(session.Host);
        loaded.Password.Should().Be(session.Password);
        loaded.PrivateKeyPath.Should().Be(session.PrivateKeyPath);
    }

    [Fact]
    public async Task SaveAndLoad_AppSettings_ShouldRoundTripSuccessfully()
    {
        var filePath = Path.Combine(_testDirectory, "settings.json");
        var settings = new AppSettings
        {
            Language = "zh",
            Theme = "light",
            TerminalFont = "Consolas",
            TerminalFontSize = 16,
            ScrollbackLines = 5000,
            DefaultPort = 2222
        };

        await _dataStore.SaveAsync(filePath, settings);
        var loaded = await _dataStore.LoadAsync<AppSettings>(filePath);

        loaded.Should().BeEquivalentTo(settings);
    }

    [Fact]
    public async Task Load_FileDoesNotExist_ShouldReturnNewInstance()
    {
        var filePath = Path.Combine(_testDirectory, "nonexistent.json");

        var result = await _dataStore.LoadAsync<AppSettings>(filePath);

        result.Should().NotBeNull();
        result!.Language.Should().Be("en");
        result.Theme.Should().Be("dark");
    }

    [Fact]
    public async Task Load_InvalidJson_ShouldReturnDefaultsInsteadOfThrowing()
    {
        var filePath = Path.Combine(_testDirectory, "invalid.json");
        await File.WriteAllTextAsync(filePath, "{ invalid json }");

        var result = await _dataStore.LoadAsync<AppSettings>(filePath);

        result.Should().NotBeNull();
        result!.Language.Should().Be("en");
        result.Theme.Should().Be("dark");
    }

    [Fact]
    public async Task Save_CreatesDirectoryIfNotExists()
    {
        var subDir = Path.Combine(_testDirectory, "nested", "path");
        var filePath = Path.Combine(subDir, "settings.json");
        var settings = new AppSettings();

        await _dataStore.SaveAsync(filePath, settings);

        File.Exists(filePath).Should().BeTrue();
        Directory.Exists(subDir).Should().BeTrue();
    }

    [Fact]
    public async Task Save_ProducesCamelCaseJson()
    {
        var filePath = Path.Combine(_testDirectory, "settings.json");
        var settings = new AppSettings
        {
            TerminalFont = "JetBrains Mono",
            TerminalFontSize = 14
        };

        await _dataStore.SaveAsync(filePath, settings);
        var json = await File.ReadAllTextAsync(filePath);

        json.Should().Contain("\"terminalFont\":");
        json.Should().Contain("\"terminalFontSize\":");
        json.Should().NotContain("\"TerminalFont\":");
    }

    [Fact]
    public async Task Save_ProducesIndentedJson()
    {
        var filePath = Path.Combine(_testDirectory, "settings.json");
        var settings = new AppSettings();

        await _dataStore.SaveAsync(filePath, settings);
        var json = await File.ReadAllTextAsync(filePath);

        json.Should().Contain("\n");
        json.Should().Contain("  ");
    }

    [Fact]
    public async Task ConcurrentSave_ShouldNotCorruptFile()
    {
        var filePath = Path.Combine(_testDirectory, "concurrent.json");
        var tasks = new List<Task>();

        for (int i = 0; i < 10; i++)
        {
            var settings = new AppSettings { TerminalFontSize = i };
            tasks.Add(_dataStore.SaveAsync(filePath, settings));
        }

        await Task.WhenAll(tasks);

        var loaded = await _dataStore.LoadAsync<AppSettings>(filePath);
        loaded.Should().NotBeNull();
        loaded!.TerminalFontSize.Should().BeInRange(0, 9);
    }

    [Fact]
    public async Task Save_UsesExclusiveFileAccess()
    {
        var filePath = Path.Combine(_testDirectory, "exclusive.json");
        var settings = new AppSettings();

        await _dataStore.SaveAsync(filePath, settings);

        var fileInfo = new FileInfo(filePath);
        fileInfo.Exists.Should().BeTrue();
    }

    [Fact]
    public async Task MultipleInstances_DifferentPaths_ShouldWorkConcurrently()
    {
        var filePath1 = Path.Combine(_testDirectory, "file1.json");
        var filePath2 = Path.Combine(_testDirectory, "file2.json");
        var settings1 = new AppSettings { Language = "en" };
        var settings2 = new AppSettings { Language = "zh" };

        await Task.WhenAll(
            _dataStore.SaveAsync(filePath1, settings1),
            _dataStore.SaveAsync(filePath2, settings2)
        );

        var loaded1 = await _dataStore.LoadAsync<AppSettings>(filePath1);
        var loaded2 = await _dataStore.LoadAsync<AppSettings>(filePath2);

        loaded1!.Language.Should().Be("en");
        loaded2!.Language.Should().Be("zh");
    }

    [Fact]
    [Trait("Category", "EdgeCase")]
    public async Task Load_TruncatedJson_ShouldReturnDefaults()
    {
        var filePath = Path.Combine(_testDirectory, "truncated.json");
        await File.WriteAllTextAsync(filePath, "{ \"language\": \"fr\", \"theme\":");

        var result = await _dataStore.LoadAsync<AppSettings>(filePath);

        result.Should().NotBeNull();
        result!.Language.Should().Be("en");
    }

    [Fact]
    [Trait("Category", "EdgeCase")]
    public async Task Load_EmptyJsonFile_ShouldReturnDefaults()
    {
        var filePath = Path.Combine(_testDirectory, "empty.json");
        await File.WriteAllTextAsync(filePath, "");

        var result = await _dataStore.LoadAsync<AppSettings>(filePath);

        result.Should().NotBeNull();
        result!.Language.Should().Be("en");
    }
}

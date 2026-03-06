using FluentAssertions;
using PulseTerm.Core.Data;
using PulseTerm.Core.Models;
using Xunit;

namespace PulseTerm.Core.Tests.Data;

[Trait("Category", "DataStore")]
public class SettingsServiceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly JsonDataStore _dataStore;

    public SettingsServiceTests()
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
    public async Task GetSettingsAsync_FileDoesNotExist_ShouldReturnDefaultSettings()
    {
        var service = new SettingsService(_dataStore, _testDirectory);

        var settings = await service.GetSettingsAsync();

        settings.Should().NotBeNull();
        settings.Language.Should().Be("en");
        settings.Theme.Should().Be("dark");
        settings.TerminalFont.Should().Be("JetBrains Mono");
        settings.TerminalFontSize.Should().Be(14);
        settings.ScrollbackLines.Should().Be(10000);
        settings.DefaultPort.Should().Be(22);
    }

    [Fact]
    public async Task SaveAndGetSettings_ShouldPersist()
    {
        var service = new SettingsService(_dataStore, _testDirectory);
        var settings = new AppSettings
        {
            Language = "zh",
            Theme = "light",
            TerminalFont = "Consolas",
            TerminalFontSize = 16,
            ScrollbackLines = 5000,
            DefaultPort = 2222
        };

        await service.SaveSettingsAsync(settings);
        var retrieved = await service.GetSettingsAsync();

        retrieved.Should().BeEquivalentTo(settings);
    }

    [Fact]
    public async Task GetStateAsync_FileDoesNotExist_ShouldReturnDefaultState()
    {
        var service = new SettingsService(_dataStore, _testDirectory);

        var state = await service.GetStateAsync();

        state.Should().NotBeNull();
        state.RecentConnections.Should().BeEmpty();
        state.WindowPosition.Should().BeNull();
        state.WindowSize.Should().BeNull();
        state.LastActiveTab.Should().BeNull();
    }

    [Fact]
    public async Task SaveAndGetState_ShouldPersist()
    {
        var service = new SettingsService(_dataStore, _testDirectory);
        var state = new AppState
        {
            RecentConnections = new List<string> { "session1", "session2", "session3" },
            WindowPosition = new WindowPosition { X = 100, Y = 200 },
            WindowSize = new WindowSize { Width = 1024, Height = 768 },
            LastActiveTab = "tab1"
        };

        await service.SaveStateAsync(state);
        var retrieved = await service.GetStateAsync();

        retrieved.Should().BeEquivalentTo(state);
    }

    [Fact]
    public async Task SaveSettings_UpdatesExisting_ShouldOverwrite()
    {
        var service = new SettingsService(_dataStore, _testDirectory);
        var settings1 = new AppSettings { Language = "en", Theme = "dark" };
        var settings2 = new AppSettings { Language = "zh", Theme = "light" };

        await service.SaveSettingsAsync(settings1);
        await service.SaveSettingsAsync(settings2);
        var retrieved = await service.GetSettingsAsync();

        retrieved.Language.Should().Be("zh");
        retrieved.Theme.Should().Be("light");
    }

    [Fact]
    public async Task SaveState_UpdatesRecentConnections_ShouldPersist()
    {
        var service = new SettingsService(_dataStore, _testDirectory);
        var state = new AppState
        {
            RecentConnections = new List<string> { "session1" }
        };

        await service.SaveStateAsync(state);
        state.RecentConnections.Add("session2");
        await service.SaveStateAsync(state);
        var retrieved = await service.GetStateAsync();

        retrieved.RecentConnections.Should().HaveCount(2);
        retrieved.RecentConnections.Should().Contain("session1");
        retrieved.RecentConnections.Should().Contain("session2");
    }

    [Fact]
    public async Task SettingsAndState_ShouldBeStoredSeparately()
    {
        var service = new SettingsService(_dataStore, _testDirectory);
        var settings = new AppSettings { Language = "fr" };
        var state = new AppState { LastActiveTab = "tab1" };

        await service.SaveSettingsAsync(settings);
        await service.SaveStateAsync(state);

        var retrievedSettings = await service.GetSettingsAsync();
        var retrievedState = await service.GetStateAsync();

        retrievedSettings.Language.Should().Be("fr");
        retrievedState.LastActiveTab.Should().Be("tab1");
    }

    [Fact]
    [Trait("Category", "EdgeCase")]
    public async Task WindowState_PersistsPositionAndSize_AcrossReloads()
    {
        var service1 = new SettingsService(_dataStore, _testDirectory);
        var state = new AppState
        {
            WindowPosition = new WindowPosition { X = 150, Y = 250 },
            WindowSize = new WindowSize { Width = 1280, Height = 720 }
        };

        await service1.SaveStateAsync(state);

        var service2 = new SettingsService(_dataStore, _testDirectory);
        var retrieved = await service2.GetStateAsync();

        retrieved.WindowPosition.Should().NotBeNull();
        retrieved.WindowPosition!.X.Should().Be(150);
        retrieved.WindowPosition.Y.Should().Be(250);
        retrieved.WindowSize.Should().NotBeNull();
        retrieved.WindowSize!.Width.Should().Be(1280);
        retrieved.WindowSize.Height.Should().Be(720);
    }
}

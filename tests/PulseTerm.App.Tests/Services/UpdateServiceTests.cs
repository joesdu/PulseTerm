using FluentAssertions;
using PulseTerm.App.Services;
using Velopack.Locators;

namespace PulseTerm.App.Tests.Services;

public class UpdateServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TestVelopackLocator _locator;

    public UpdateServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"pulseterm_update_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _locator = new TestVelopackLocator("com.pulseterm.test", "1.0.0", _tempDir, null);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    [Trait("Category", "Update")]
    public void CurrentVersion_WhenNotInstalled_ReturnsAssemblyVersion()
    {
        var service = new UpdateService("https://example.com/updates", _locator);

        service.CurrentVersion.Should().NotBeNullOrEmpty();
    }

    [Fact]
    [Trait("Category", "Update")]
    public void AvailableVersion_Initially_IsNull()
    {
        var service = new UpdateService("https://example.com/updates", _locator);

        service.AvailableVersion.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Update")]
    public async Task CheckForUpdateAsync_WhenNetworkUnavailable_ReturnsFalse()
    {
        var service = new UpdateService("https://invalid.test.example.com/updates", _locator);

        var result = await service.CheckForUpdateAsync();

        result.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Update")]
    public async Task DownloadUpdateAsync_WhenNoUpdateAvailable_CompletesWithoutError()
    {
        var service = new UpdateService("https://example.com/updates", _locator);

        var act = () => service.DownloadUpdateAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    [Trait("Category", "Update")]
    public void ApplyUpdateAndRestart_WhenNoUpdateAvailable_ThrowsInvalidOperation()
    {
        var service = new UpdateService("https://example.com/updates", _locator);

        var act = () => service.ApplyUpdateAndRestart();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    [Trait("Category", "Update")]
    public void Constructor_WithNullUrl_ThrowsArgumentNullException()
    {
        var act = () => new UpdateService(null!, _locator);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    [Trait("Category", "Update")]
    public void ImplementsIUpdateService()
    {
        var service = new UpdateService("https://example.com/updates", _locator);

        service.Should().BeAssignableTo<IUpdateService>();
    }
}

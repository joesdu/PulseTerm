using System.Reactive.Linq;
using FluentAssertions;
using NSubstitute;
using PulseTerm.App.ViewModels;
using PulseTerm.Core.Data;
using PulseTerm.Core.Models;
using PulseTerm.Core.Services;
using PulseTerm.Core.Ssh;

namespace PulseTerm.App.Tests.ViewModels;

public class SettingsViewModelTests
{
    private readonly ISettingsService _settingsService;
    private readonly IThemeService _themeService;

    public SettingsViewModelTests()
    {
        _settingsService = Substitute.For<ISettingsService>();
        _themeService = Substitute.For<IThemeService>();
    }

    private SettingsViewModel CreateVm() => new(_settingsService, _themeService);

    [Fact]
    [Trait("Category", "Settings")]
    public async Task LoadCommand_LoadsSettingsFromService()
    {
        var settings = new AppSettings
        {
            Language = "zh-CN",
            Theme = "light",
            TerminalFont = "Fira Code",
            TerminalFontSize = 16,
            ScrollbackLines = 5000,
            DefaultPort = 2222
        };
        _settingsService.GetSettingsAsync().Returns(settings);

        var vm = CreateVm();
        await vm.LoadCommand.Execute().FirstAsync();

        vm.Language.Should().Be("zh-CN");
        vm.Theme.Should().Be("light");
        vm.TerminalFont.Should().Be("Fira Code");
        vm.TerminalFontSize.Should().Be(16);
        vm.ScrollbackLines.Should().Be(5000);
        vm.DefaultPort.Should().Be(2222);
    }

    [Fact]
    [Trait("Category", "Settings")]
    public async Task SaveCommand_PersistsToService()
    {
        var vm = CreateVm();
        vm.Language = "zh-CN";
        vm.Theme = "light";
        vm.TerminalFont = "Cascadia Code";
        vm.TerminalFontSize = 18;
        vm.ScrollbackLines = 20000;
        vm.DefaultPort = 8022;

        await vm.SaveCommand.Execute().FirstAsync();

        await _settingsService.Received(1).SaveSettingsAsync(
            Arg.Is<AppSettings>(s =>
                s.Language == "zh-CN" &&
                s.Theme == "light" &&
                s.TerminalFont == "Cascadia Code" &&
                s.TerminalFontSize == 18 &&
                s.ScrollbackLines == 20000 &&
                s.DefaultPort == 8022));
    }

    [Fact]
    [Trait("Category", "Settings")]
    public async Task SaveCommand_AppliesTheme()
    {
        var vm = CreateVm();
        vm.Theme = "light";

        await vm.SaveCommand.Execute().FirstAsync();

        _themeService.Received(1).SetTheme("light");
    }

    [Fact]
    [Trait("Category", "Settings")]
    public void ConnectionProfile_ValidatesRequiredFields()
    {
        var vm = new ConnectionProfileViewModel();

        // Host and Username empty → SaveCommand not executable
        vm.Host = "";
        vm.Username = "";
        var canExecute = false;
        vm.SaveCommand.CanExecute.Subscribe(x => canExecute = x);

        canExecute.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Settings")]
    public void ConnectionProfile_AuthMethodToggle_SwitchesVisibility()
    {
        var vm = new ConnectionProfileViewModel();

        // Default is Password
        vm.IsPasswordAuth.Should().BeTrue();
        vm.IsKeyAuth.Should().BeFalse();

        vm.AuthMethod = AuthMethod.PrivateKey;

        vm.IsPasswordAuth.Should().BeFalse();
        vm.IsKeyAuth.Should().BeTrue();

        vm.AuthMethod = AuthMethod.Password;

        vm.IsPasswordAuth.Should().BeTrue();
        vm.IsKeyAuth.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Settings")]
    public void HostKeyPrompt_TrustCommand_SetsResultTrue()
    {
        var vm = new HostKeyPromptViewModel(
            "example.com", 22, "ssh-ed25519",
            "SHA256:abc123def456", HostKeyVerification.Unknown);

        vm.Result.Should().BeNull();

        vm.TrustCommand.Execute().Subscribe();

        vm.Result.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Settings")]
    public void HostKeyPrompt_RejectCommand_SetsResultFalse()
    {
        var vm = new HostKeyPromptViewModel(
            "example.com", 22, "ssh-rsa",
            "SHA256:xyz789", HostKeyVerification.Unknown);

        vm.RejectCommand.Execute().Subscribe();

        vm.Result.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Settings")]
    public void HostKeyPrompt_ChangedKey_ShowsWarning()
    {
        var vmChanged = new HostKeyPromptViewModel(
            "server.local", 22, "ssh-ed25519",
            "SHA256:changed123", HostKeyVerification.Changed);

        vmChanged.IsChanged.Should().BeTrue();

        var vmUnknown = new HostKeyPromptViewModel(
            "server.local", 22, "ssh-ed25519",
            "SHA256:unknown456", HostKeyVerification.Unknown);

        vmUnknown.IsChanged.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Settings")]
    public void ConnectionProfile_PortValidation_AcceptsValidRange()
    {
        var vm = new ConnectionProfileViewModel();
        vm.Host = "test.example.com";
        vm.Username = "admin";

        // Valid port
        vm.Port = 22;
        var canExecute = false;
        vm.SaveCommand.CanExecute.Subscribe(x => canExecute = x);
        canExecute.Should().BeTrue();

        // Port 0 — invalid
        vm.Port = 0;
        vm.SaveCommand.CanExecute.Subscribe(x => canExecute = x);
        canExecute.Should().BeFalse();

        // Port 65535 — valid max
        vm.Port = 65535;
        vm.SaveCommand.CanExecute.Subscribe(x => canExecute = x);
        canExecute.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Settings")]
    public void ConnectionProfile_SaveCommand_ReturnsProfile()
    {
        var vm = new ConnectionProfileViewModel();
        vm.Name = "My Server";
        vm.Host = "192.168.1.100";
        vm.Port = 2222;
        vm.Username = "deploy";
        vm.AuthMethod = AuthMethod.PrivateKey;
        vm.PrivateKeyPath = "/home/user/.ssh/id_rsa";

        SessionProfile? result = null;
        vm.SaveCommand.Execute().Subscribe(profile => result = profile);

        result.Should().NotBeNull();
        result!.Name.Should().Be("My Server");
        result.Host.Should().Be("192.168.1.100");
        result.Port.Should().Be(2222);
        result.Username.Should().Be("deploy");
        result.AuthMethod.Should().Be(AuthMethod.PrivateKey);
        result.PrivateKeyPath.Should().Be("/home/user/.ssh/id_rsa");
    }
}

using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Services;
using VelaShell.Core.Ssh;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

[TestClass]
public class SettingsViewModelTests
{
    private readonly ISettingsService _settingsService;
    private readonly IThemeService _themeService;

    public SettingsViewModelTests()
    {
        _settingsService = Substitute.For<ISettingsService>();
        _themeService = Substitute.For<IThemeService>();
    }

    private SettingsViewModel CreateVm(ISettingsPreviewService? previewService = null) =>
        new(_settingsService, _themeService, previewService: previewService);

    [TestMethod]
    [TestCategory("Settings")]
    public async Task AppearanceOpacityChange_PreviewsEveryValueImmediately_WithoutSnapshot()
    {
        ISettingsPreviewService preview = new SettingsPreviewService();
        var opacityValues = new List<int>();
        var snapshots = new List<AppSettings>();
        preview.WindowOpacityPreviewRequested += value => opacityValues.Add(value);
        preview.PreviewRequested += snapshot => snapshots.Add(snapshot);
        _settingsService.GetSettingsAsync().Returns(new AppSettings());

        SettingsViewModel vm = CreateVm(preview);
        await vm.LoadCommand.Execute().FirstAsync();

        foreach (int value in new[] { 20, 30, 40, 50 })
        {
            vm.Appearance.WindowOpacityPercent = value;
        }

        Assert.AreSequenceEqual([20, 30, 40, 50], opacityValues);
        Assert.IsEmpty(snapshots);
    }

    [TestMethod]
    [TestCategory("Settings")]
    public async Task OpacityOnlyPreview_NotifyClosed_BroadcastsOriginalBaseline()
    {
        ISettingsPreviewService preview = new SettingsPreviewService();
        var snapshots = new List<AppSettings>();
        preview.PreviewRequested += snapshot => snapshots.Add(snapshot);
        var baseline = new AppSettings { Appearance = new() { WindowOpacityPercent = 80 } };
        _settingsService.GetSettingsAsync().Returns(baseline);

        SettingsViewModel vm = CreateVm(preview);
        await vm.LoadCommand.Execute().FirstAsync();
        vm.Appearance.WindowOpacityPercent = 40;
        vm.NotifyClosed();

        Assert.HasCount(1, snapshots);
        Assert.AreEqual(80, snapshots[0].Appearance.WindowOpacityPercent);
    }

    [TestMethod]
    [TestCategory("Settings")]
    public async Task LoadCommand_LoadsSettingsFromService()
    {
        var settings = new AppSettings
        {
            Language = "zh-CN",
            Theme = "light",
            TerminalFont = "Fira Code",
            TerminalFontSize = 16,
            ScrollbackLines = 5000,
            DefaultPort = 2222,
            TerminalType = "vt220",
            TerminalEncoding = "GBK",
            Appearance = new() { ShowQuickCommandsPanel = true },
        };
        _settingsService.GetSettingsAsync().Returns(settings);

        SettingsViewModel vm = CreateVm();
        await vm.LoadCommand.Execute().FirstAsync();

        Assert.AreEqual("zh-CN", vm.Language);
        Assert.AreEqual("light", vm.Theme);
        Assert.AreEqual("Fira Code", vm.TerminalFont);
        Assert.AreEqual(16, vm.TerminalFontSize);
        Assert.AreEqual(5000, vm.ScrollbackLines);
        Assert.AreEqual(2222, vm.DefaultPort);
        Assert.AreEqual("vt220", vm.TerminalType);
        Assert.AreEqual("GBK", vm.TerminalEncoding);
        Assert.IsTrue(vm.Appearance.ShowQuickCommandsPanel);
    }

    [TestMethod]
    [TestCategory("Settings")]
    public async Task SaveCommand_PersistsToService()
    {
        SettingsViewModel vm = CreateVm();
        vm.Language = "zh-CN";
        vm.Theme = "light";
        vm.TerminalFont = "Cascadia Code";
        vm.TerminalFontSize = 18;
        vm.ScrollbackLines = 20000;
        vm.DefaultPort = 8022;
        vm.TerminalType = "xterm-256color";
        vm.TerminalEncoding = "UTF-8";
        vm.Appearance.ShowQuickCommandsPanel = true;

        await vm.SaveCommand.Execute().FirstAsync();

        await _settingsService
            .Received(1)
            .SaveSettingsAsync(
                Arg.Is<AppSettings>(s =>
                    s.Language == "zh-CN"
                    && s.Theme == "light"
                    && s.TerminalFont == "Cascadia Code"
                    && s.TerminalFontSize == 18
                    && s.ScrollbackLines == 20000
                    && s.DefaultPort == 8022
                    && s.TerminalType == "xterm-256color"
                    && s.TerminalEncoding == "UTF-8"
                    && s.Appearance.ShowQuickCommandsPanel
                )
            );
    }

    [TestMethod]
    [TestCategory("Settings")]
    public async Task SaveCommand_AppliesTheme()
    {
        SettingsViewModel vm = CreateVm();
        vm.Theme = "light";

        await vm.SaveCommand.Execute().FirstAsync();

        _themeService.Received(1).SetTheme("light");
    }

    [TestMethod]
    [TestCategory("Settings")]
    public void ConnectionProfile_ValidatesRequiredFields()
    {
        var vm = new ConnectionProfileViewModel
        {
            // Host and Username empty → SaveCommand not executable
            Host = "",
            Username = "",
        };
        bool canExecute = false;
        vm.SaveCommand.CanExecute.Subscribe(x => canExecute = x);

        Assert.IsFalse(canExecute);
    }

    [TestMethod]
    [TestCategory("Settings")]
    public void ConnectionProfile_AuthMethodToggle_SwitchesVisibility()
    {
        var vm = new ConnectionProfileViewModel();

        // Default is Password
        Assert.IsTrue(vm.IsPasswordAuth);
        Assert.IsFalse(vm.IsKeyAuth);

        vm.AuthMethod = AuthMethod.PrivateKey;

        Assert.IsFalse(vm.IsPasswordAuth);
        Assert.IsTrue(vm.IsKeyAuth);

        vm.AuthMethod = AuthMethod.Password;

        Assert.IsTrue(vm.IsPasswordAuth);
        Assert.IsFalse(vm.IsKeyAuth);
    }

    [TestMethod]
    [TestCategory("Settings")]
    public void HostKeyPrompt_TrustPermanentlyCommand_SetsResult()
    {
        var vm = new HostKeyPromptViewModel(
            "example.com",
            22,
            "ssh-ed25519",
            "SHA256:abc123def456",
            HostKeyVerification.Unknown
        );

        Assert.IsNull(vm.Result);

        vm.TrustPermanentlyCommand.Execute().Subscribe();

        Assert.AreEqual(HostKeyDecision.TrustPermanently, vm.Result);
    }

    [TestMethod]
    [TestCategory("Settings")]
    public void HostKeyPrompt_TrustOnceCommand_SetsResult()
    {
        var vm = new HostKeyPromptViewModel(
            "example.com",
            22,
            "ssh-ed25519",
            "SHA256:abc123def456",
            HostKeyVerification.Unknown
        );

        vm.TrustOnceCommand.Execute().Subscribe();

        Assert.AreEqual(HostKeyDecision.TrustOnce, vm.Result);
    }

    [TestMethod]
    [TestCategory("Settings")]
    public void HostKeyPrompt_CancelCommand_SetsReject()
    {
        var vm = new HostKeyPromptViewModel(
            "example.com",
            22,
            "ssh-rsa",
            "SHA256:xyz789",
            HostKeyVerification.Unknown
        );

        vm.CancelCommand.Execute().Subscribe();

        Assert.AreEqual(HostKeyDecision.Reject, vm.Result);
    }

    [TestMethod]
    [TestCategory("Settings")]
    public void HostKeyPrompt_ChangedKey_ShowsWarning()
    {
        var vmChanged = new HostKeyPromptViewModel(
            "server.local",
            22,
            "ssh-ed25519",
            "SHA256:changed123",
            HostKeyVerification.Changed
        );

        Assert.IsTrue(vmChanged.IsChanged);

        var vmUnknown = new HostKeyPromptViewModel(
            "server.local",
            22,
            "ssh-ed25519",
            "SHA256:unknown456",
            HostKeyVerification.Unknown
        );

        Assert.IsFalse(vmUnknown.IsChanged);
    }

    [TestMethod]
    [TestCategory("Settings")]
    public void ConnectionProfile_PortValidation_AcceptsValidRange()
    {
        var vm = new ConnectionProfileViewModel
        {
            Host = "test.example.com",
            Username = "admin",

            // Valid port
            Port = 22,
        };
        bool canExecute = false;
        vm.SaveCommand.CanExecute.Subscribe(x => canExecute = x);
        Assert.IsTrue(canExecute);

        // Port 0 — invalid
        vm.Port = 0;
        vm.SaveCommand.CanExecute.Subscribe(x => canExecute = x);
        Assert.IsFalse(canExecute);

        // Port 65535 — valid max
        vm.Port = 65535;
        vm.SaveCommand.CanExecute.Subscribe(x => canExecute = x);
        Assert.IsTrue(canExecute);
    }

    [TestMethod]
    [TestCategory("Settings")]
    public void ConnectionProfile_SaveCommand_ReturnsProfile()
    {
        var vm = new ConnectionProfileViewModel
        {
            Name = "My Server",
            Host = "192.168.1.100",
            Port = 2222,
            Username = "deploy",
            AuthMethod = AuthMethod.PrivateKey,
            PrivateKeyPath = "/home/user/.ssh/id_rsa",
        };

        SessionProfile? result = null;
        vm.SaveCommand.Execute().Subscribe(profile => result = profile);

        Assert.IsNotNull(result);
        Assert.AreEqual("My Server", result!.Name);
        Assert.AreEqual("192.168.1.100", result.Host);
        Assert.AreEqual(2222, result.Port);
        Assert.AreEqual("deploy", result.Username);
        Assert.AreEqual(AuthMethod.PrivateKey, result.AuthMethod);
        Assert.AreEqual("/home/user/.ssh/id_rsa", result.PrivateKeyPath);
    }
}

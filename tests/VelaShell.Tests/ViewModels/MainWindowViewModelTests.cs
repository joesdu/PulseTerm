using NSubstitute;
using ReactiveUI.Builder;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Concurrency;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Core.Sync;
using VelaShell.Core.Tunnels;
using VelaShell.Docking;
using VelaShell.Presentation.Commands;
using VelaShell.Presentation.Services;
using VelaShell.Presentation.ViewModels;
using VelaShell.Services;
using VelaShell.Tests.TestSupport;
using VelaShell.Terminal;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

[TestClass]
public class MainWindowViewModelTests
{
    static MainWindowViewModelTests()
    {
        try
        {
            RxAppBuilder
                .CreateReactiveUIBuilder()
                .WithMainThreadScheduler(CurrentThreadSequencer.Instance)
                .WithCoreServices()
                .BuildApp();
        }
        catch (InvalidOperationException)
        {
            // Already initialized
        }
    }

    [TestMethod]
    [TestCategory("UI")]
    public void MainWindowViewModel_Initializes_WithAllSubViewModels()
    {
        var vm = new MainWindowViewModel();

        Assert.IsNotNull(vm.Sidebar);
        Assert.IsNotNull(vm.Layout);
        Assert.IsNotNull(vm.StatusBar);
    }

    [TestMethod]
    [TestCategory("UI")]
    public void OpenTunnelPanel_WithWorkflowService_OpensPanel()
    {
        ITunnelWorkflowService workflow = Substitute.For<ITunnelWorkflowService>();
        var vm = new MainWindowViewModel(tunnelWorkflowService: workflow);

        vm.OpenTunnelPanel();

        Assert.IsNotNull(vm.TunnelPanel);
        Assert.IsTrue(vm.IsTunnelPanelOpen);
    }

    [TestMethod]
    [TestCategory("UI")]
    public void OpenTunnelPanel_WithOnlyCoreTunnelService_DoesNotBypassWorkflow()
    {
        ITunnelService tunnelService = Substitute.For<ITunnelService>();
        var vm = new MainWindowViewModel(tunnelService: tunnelService);

        vm.OpenTunnelPanel();

        Assert.IsNull(vm.TunnelPanel);
        Assert.IsFalse(vm.IsTunnelPanelOpen);
    }

    [TestMethod]
    [TestCategory("UI")]
    public void ToolsFilesCommand_IsRegistered_WithShortcut()
    {
        var vm = new MainWindowViewModel();

        CommandDescriptor? command = vm.Commands.Find("tools.files");

        Assert.IsNotNull(
            command,
            "SFTP file manager command must be wired so the panel can be opened."
        );
        Assert.AreEqual("Ctrl+Shift+F", command.Shortcut);
    }

    [TestMethod]
    [TestCategory("UI")]
    public void ToggleFileBrowser_RequiresConnectedSsh_AndKeepsStatePerTab()
    {
        ISftpService sftp = Substitute.For<ISftpService>();
        var vm = new MainWindowViewModel(sftpService: sftp);

        Assert.IsFalse(vm.FileBrowser.IsVisible);
        Assert.IsFalse(vm.CanToggleFileBrowser);
        vm.ToggleFileBrowser();
        Assert.IsFalse(
            vm.FileBrowser.IsVisible,
            "No active SSH terminal must keep the panel closed."
        );

        var first = new TerminalTabViewModel(Substitute.For<ITerminalEmulator>())
        {
            Profile = new() { Name = "one", Host = "one.example" },
            SessionId = Guid.NewGuid(),
            ConnectionStatus = SessionStatus.Connected,
        };
        var firstDocument = new TerminalDocument(first);
        vm.Layout.AddDocument(firstDocument);
        Assert.IsTrue(vm.CanToggleFileBrowser);
        Assert.IsTrue(
            vm.FileBrowser.IsVisible,
            "默认设置(自动打开)下,新标签的面板自动展示。"
        );

        vm.ToggleFileBrowser();
        Assert.IsFalse(vm.FileBrowser.IsVisible);

        var second = new TerminalTabViewModel(Substitute.For<ITerminalEmulator>())
        {
            Profile = new() { Name = "two", Host = "two.example" },
            SessionId = Guid.NewGuid(),
            ConnectionStatus = SessionStatus.Connected,
        };
        var secondDocument = new TerminalDocument(second);
        vm.Layout.AddDocument(secondDocument);
        Assert.IsTrue(
            vm.FileBrowser.IsVisible,
            "第一个标签隐藏面板不影响新标签:新标签按设置自动打开。"
        );

        // 面板开关是每个标签自己的状态:切回隐藏过的标签保持隐藏,切回打开的恢复显示。
        // 切标签走工作区(Q-02 之后它是唯一事实来源),ActiveTerminalTab 由它派生。
        vm.Layout.ActivateDocument(firstDocument);
        Assert.IsFalse(vm.FileBrowser.IsVisible, "切回曾隐藏面板的标签:面板保持隐藏。");
        vm.Layout.ActivateDocument(secondDocument);
        Assert.IsTrue(vm.FileBrowser.IsVisible, "切回面板开着的标签:面板自动恢复显示。");

        second.ConnectionStatus = SessionStatus.Disconnected;
        Assert.IsFalse(vm.CanToggleFileBrowser);

        var local = new TerminalTabViewModel(Substitute.For<ITerminalEmulator>())
        {
            LocalShell = new("pwsh", "PowerShell", "pwsh.exe"),
            ConnectionStatus = SessionStatus.Connected,
        };
        vm.Layout.AddDocument(new TerminalDocument(local));
        Assert.IsFalse(vm.CanToggleFileBrowser);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void ConnectedStateChange_AddsTargetAndEnablesCurrentTerminalExecution()
    {
        IQuickCommandRepository repository = Substitute.For<IQuickCommandRepository>();
        var library = new QuickCommandsViewModel(repository);
        var vm = new MainWindowViewModel(quickCommands: library);
        ITerminalEmulator emulator = Substitute.For<ITerminalEmulator>();
        var tab = new TerminalTabViewModel(emulator)
        {
            Profile = new() { Name = "server", Host = "server.example" },
        };
        vm.Layout.AddDocument(new TerminalDocument(tab));
        QuickCommandRunnerViewModel runner = vm.Sidebar.QuickCommands!;
        Assert.IsEmpty(runner.Targets);

        tab.ConnectionStatus = SessionStatus.Connected;

        Assert.HasCount(1, runner.Targets);
        Assert.AreEqual(tab.Id, runner.Targets[0].Id);
        Assert.IsTrue(runner.CanRun);
        runner.SendCommand.Execute(library.AllCommands[0]).Subscribe();
        emulator.Received(1).WriteInput(Arg.Any<byte[]>());

        tab.ConnectionStatus = SessionStatus.Disconnected;
        Assert.IsEmpty(runner.Targets);
        Assert.IsFalse(runner.CanRun);
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void QuickCommandExecution_RequestsFocusForActiveTerminal()
    {
        IQuickCommandRepository repository = Substitute.For<IQuickCommandRepository>();
        var library = new QuickCommandsViewModel(repository);
        var vm = new MainWindowViewModel(quickCommands: library);
        ITerminalEmulator emulator = Substitute.For<ITerminalEmulator>();
        var tab = new TerminalTabViewModel(emulator) { ConnectionStatus = SessionStatus.Connected };
        vm.Layout.AddDocument(new TerminalDocument(tab));
        bool focusRequested = false;
        vm.TerminalFocusRequested += (_, _) => focusRequested = true;

        vm.Sidebar.QuickCommands!.SendCommand.Execute(library.AllCommands[0]).Subscribe();

        Assert.IsTrue(focusRequested);
    }

    [TestMethod]
    [TestCategory("SyncInput")]
    public void SyncChannel_JoinPauseLeave_UpdatesTabState()
    {
        var tab = new TerminalTabViewModel(Substitute.For<ITerminalEmulator>());

        tab.JoinSyncChannel(SyncInputChannel.A);
        Assert.IsTrue(tab.IsInSyncChannel);
        Assert.AreEqual("A", tab.SyncChannelLetter);

        tab.ToggleSyncPauseCommand.Execute().Subscribe();
        Assert.IsTrue(tab.IsSyncPaused);

        // 改挂新频道时清除暂停态。
        tab.JoinSyncChannel(SyncInputChannel.B);
        Assert.AreEqual("B", tab.SyncChannelLetter);
        Assert.IsFalse(tab.IsSyncPaused);

        tab.LeaveSyncChannelCommand.Execute().Subscribe();
        Assert.IsFalse(tab.IsInSyncChannel);
        Assert.AreEqual(string.Empty, tab.SyncChannelLetter);
    }

    [TestMethod]
    [TestCategory("SyncInput")]
    public void SyncChannel_CloseChannel_RemovesAllChannelMembersOnly()
    {
        var vm = new MainWindowViewModel();
        var first = new TerminalTabViewModel(Substitute.For<ITerminalEmulator>());
        var second = new TerminalTabViewModel(Substitute.For<ITerminalEmulator>());
        var other = new TerminalTabViewModel(Substitute.For<ITerminalEmulator>());
        vm.Layout.AddDocument(new TerminalDocument(first));
        vm.Layout.AddDocument(new TerminalDocument(second));
        vm.Layout.AddDocument(new TerminalDocument(other));
        first.JoinSyncChannel(SyncInputChannel.A);
        second.JoinSyncChannel(SyncInputChannel.A);
        other.JoinSyncChannel(SyncInputChannel.B);

        first.CloseSyncChannelCommand.Execute().Subscribe();

        Assert.IsFalse(first.IsInSyncChannel);
        Assert.IsFalse(second.IsInSyncChannel);
        Assert.IsTrue(other.IsInSyncChannel);
    }

    [TestMethod]
    [TestCategory("SyncInput")]
    public void SyncChannel_RemovedTab_LeavesChannel()
    {
        var vm = new MainWindowViewModel();
        var tab = new TerminalTabViewModel(Substitute.For<ITerminalEmulator>());
        vm.Layout.AddDocument(new TerminalDocument(tab));
        tab.JoinSyncChannel(SyncInputChannel.C);

        vm.Layout.RemoveDocument(vm.Layout.AllDocuments().OfType<TerminalDocument>().First(d => ReferenceEquals(d.Terminal, tab)));

        Assert.IsFalse(tab.IsInSyncChannel);
    }

    [TestMethod]
    [TestCategory("SyncInput")]
    public void SyncChannel_ForwardedInput_BypassesPeerEmulatorInputEvents()
    {
        var vm = new MainWindowViewModel();
        ITerminalEmulator firstEmulator = Substitute.For<ITerminalEmulator>();
        ITerminalEmulator secondEmulator = Substitute.For<ITerminalEmulator>();
        var first = new TerminalTabViewModel(firstEmulator)
        {
            ConnectionStatus = SessionStatus.Connected,
        };
        var second = new TerminalTabViewModel(secondEmulator)
        {
            ConnectionStatus = SessionStatus.Connected,
        };
        vm.Layout.AddDocument(new TerminalDocument(first));
        vm.Layout.AddDocument(new TerminalDocument(second));
        first.JoinSyncChannel(SyncInputChannel.A);
        second.JoinSyncChannel(SyncInputChannel.A);

        firstEmulator.TypedInput += Raise.Event<Action<byte[]>>("ls"u8.ToArray());

        // 转发必须直写桥(SendRaw),不得经接收端终端控件的输入 API——否则会二次
        // 触发 TypedInput(转发回环)并驱动非焦点标签的命令补全弹层。
        secondEmulator.DidNotReceive().WriteInput(Arg.Any<byte[]>());
        secondEmulator.DidNotReceive().WriteTextInput(Arg.Any<string>());
    }

    [TestMethod]
    [TestCategory("UI")]
    public void SettingsSaved_ReappliesScrollbackToOpenTabs()
    {
        ISettingsService settingsService = Substitute.For<ISettingsService>();
        ITerminalEmulator emulator = Substitute.For<ITerminalEmulator>();
        var vm = new MainWindowViewModel(settingsService: settingsService);

        var tab = new TerminalTabViewModel(emulator);
        vm.Layout.AddDocument(new TerminalDocument(tab));

        // Saving settings must re-apply live values to already-open terminals (#3/#15/#21).
        settingsService.SettingsSaved += Raise.Event<Action<AppSettings>>(
            new AppSettings { ScrollbackLines = 88_000 }
        );

        // The re-apply is marshalled through RxApp.MainThreadScheduler, which other test
        // classes may have initialized to an asynchronous scheduler — allow it to land.
        SpinWait.SpinUntil(() => emulator.ScrollbackLines == 88_000, TimeSpan.FromSeconds(5));
        Assert.AreEqual(88_000, emulator.ScrollbackLines);

        tab.Dispose();
    }

    [TestMethod]
    [TestCategory("QuickCommands")]
    public void SettingsSaved_UpdatesQuickCommandsPanelVisibility()
    {
        ISettingsService settingsService = Substitute.For<ISettingsService>();
        IQuickCommandRepository repository = Substitute.For<IQuickCommandRepository>();
        var vm = new MainWindowViewModel(
            settingsService: settingsService,
            quickCommands: new QuickCommandsViewModel(repository)
        );

        Assert.IsFalse(vm.Sidebar.IsQuickCommandsVisible);
        settingsService.SettingsSaved += Raise.Event<Action<AppSettings>>(
            new AppSettings { Appearance = new() { ShowQuickCommandsPanel = true } }
        );

        Assert.IsTrue(vm.Sidebar.IsQuickCommandsVisible);
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task SyncProfilesApplied_RefreshesSessionTreeWithoutRestart()
    {
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        IGistSyncService syncService = Substitute.For<IGistSyncService>();
        var profiles = new List<SessionProfile>();
        repository.GetAllGroupsAsync().Returns([]);
        repository.GetAllSessionsAsync().Returns(_ => [.. profiles]);
        var vm = new MainWindowViewModel(
            sessionRepository: repository,
            gistSyncService: syncService
        );
        await vm.InitializeAsync();
        Assert.IsTrue(vm.Sidebar.SessionTree?.HasNoSessions);

        SessionProfile pulled = new()
        {
            Id = Guid.NewGuid(),
            Name = "pulled-server",
            Host = "pulled.example.com",
            Username = "root",
        };
        profiles.Add(pulled);

        syncService.ProfilesApplied += Raise.Event<EventHandler>(syncService, EventArgs.Empty);

        bool appeared = SpinWait.SpinUntil(
            () => vm.Sidebar.SessionTree?.Nodes.Any(node => node.Id == pulled.Id) == true,
            TimeSpan.FromSeconds(5)
        );
        Assert.IsTrue(appeared, "云同步拉取的连接应立即出现在会话树中。");
    }

    [TestMethod]
    [TestCategory("UI")]
    public void SidebarViewModel_Initializes_WithCommands()
    {
        var vm = new SidebarViewModel();

        Assert.IsNotNull(vm.NotificationsCommand);
        Assert.IsNotNull(vm.RecentConnections);
    }

    [TestMethod]
    [TestCategory("Sidebar")]
    public async Task InitializeAsync_RestoresAndPersistsSidebarLayoutState()
    {
        ISettingsService settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(new AppSettings());
        settingsService
            .GetStateAsync()
            .Returns(
                new AppState
                {
                    SidebarQuickCommandsExpanded = false,
                    SidebarQuickCommandsHeight = 245,
                    SidebarRecentConnectionsExpanded = true,
                    SidebarRecentConnectionsHeight = 275,
                }
            );
        var vm = new MainWindowViewModel(settingsService: settingsService);

        await vm.InitializeAsync();

        Assert.IsFalse(vm.Sidebar.QuickCommandsExpanded);
        Assert.AreEqual(245, vm.Sidebar.QuickCommandsHeight);
        Assert.IsTrue(vm.Sidebar.RecentConnectionsExpanded);
        Assert.AreEqual(275, vm.Sidebar.RecentConnectionsHeight);

        vm.Sidebar.QuickCommandsExpanded = true;
        vm.Sidebar.QuickCommandsHeight = 310;
        vm.Sidebar.RecentConnectionsExpanded = false;
        await vm.PersistSidebarStateAsync();

        await settingsService
            .Received()
            .SaveStateAsync(
                Arg.Is<AppState>(state =>
                    state.SidebarQuickCommandsExpanded
                    && state.SidebarQuickCommandsHeight == 310
                    && !state.SidebarRecentConnectionsExpanded
                    && state.SidebarRecentConnectionsHeight == 275
                )
            );
    }

    /// <summary>构造带设置桩的 VM 并完成初始化,用于验证 SFTP 面板按设置决定新标签初始状态。</summary>
    private static async Task<MainWindowViewModel> CreateInitializedVmAsync(
        bool autoOpenFileBrowser,
        ISettingsService? settingsServiceOut = null)
    {
        ISettingsService settingsService = settingsServiceOut ?? Substitute.For<ISettingsService>();
        settingsService
            .GetSettingsAsync()
            .Returns(new AppSettings
            {
                TerminalBehavior = new() { AutoOpenFileBrowser = autoOpenFileBrowser }
            });
        settingsService.GetStateAsync().Returns(new AppState());
        var vm = new MainWindowViewModel(
            settingsService: settingsService,
            sftpService: Substitute.For<ISftpService>()
        );
        await vm.InitializeAsync();
        return vm;
    }

    private static async Task<MainWindowViewModel> CreateInitializedVmAsync(
        ISettingsService settingsService)
    {
        var vm = new MainWindowViewModel(
            settingsService: settingsService,
            sftpService: Substitute.For<ISftpService>()
        );
        await vm.InitializeAsync();
        return vm;
    }

    private static TerminalTabViewModel CreateConnectedSshTab() =>
        new(Substitute.For<ITerminalEmulator>())
        {
            Profile = new() { Name = "srv", Host = "srv.example" },
            SessionId = Guid.NewGuid(),
            ConnectionStatus = SessionStatus.Connected,
        };

    [TestMethod]
    [TestCategory("UI")]
    public async Task FileBrowser_AutoOpenSettingOn_NewTabOpensBrowser()
    {
        MainWindowViewModel vm = await CreateInitializedVmAsync(autoOpenFileBrowser: true);

        vm.Layout.AddDocument(new TerminalDocument(CreateConnectedSshTab()));

        Assert.IsTrue(vm.FileBrowser.IsVisible, "开关开启:新连接的标签自动打开面板。");
    }

    [TestMethod]
    [TestCategory("UI")]
    public async Task FileBrowser_AutoOpenSettingOff_NewTabKeepsBrowserHidden()
    {
        MainWindowViewModel vm = await CreateInitializedVmAsync(autoOpenFileBrowser: false);

        vm.Layout.AddDocument(new TerminalDocument(CreateConnectedSshTab()));

        Assert.IsFalse(vm.FileBrowser.IsVisible, "开关关闭:新连接的标签不自动打开面板。");
    }

    [TestMethod]
    [TestCategory("UI")]
    public async Task FileBrowser_SettingSavedAtRuntime_AppliesToSubsequentNewTabs()
    {
        ISettingsService settingsService = Substitute.For<ISettingsService>();
        MainWindowViewModel vm = await CreateInitializedVmAsync(
            autoOpenFileBrowser: false, settingsService);

        TerminalTabViewModel first = CreateConnectedSshTab();
        vm.Layout.AddDocument(new TerminalDocument(first));
        Assert.IsFalse(vm.FileBrowser.IsVisible);

        // 运行中保存设置(开关改为开启):已开标签不受影响,之后新开的标签立即生效。
        settingsService.SettingsSaved += Raise.Event<Action<AppSettings>>(
            new AppSettings { TerminalBehavior = new() { AutoOpenFileBrowser = true } });

        vm.Layout.AddDocument(new TerminalDocument(CreateConnectedSshTab()));
        Assert.IsTrue(vm.FileBrowser.IsVisible, "保存后的开关状态对新标签立即生效。");

        vm.Activate(first);
        Assert.IsFalse(vm.FileBrowser.IsVisible, "已存在标签保持自己原有的面板状态。");
    }

    [TestMethod]
    [TestCategory("UI")]
    public async Task FileBrowser_PanelClose_PersistsNextLaunchDefaultButKeepsTabState()
    {
        var settingsService = new MemorySettingsService(
            new AppSettings { TerminalBehavior = new() { AutoOpenFileBrowser = true } });
        MainWindowViewModel vm = await CreateInitializedVmAsync(settingsService);

        TerminalTabViewModel first = CreateConnectedSshTab();
        vm.Layout.AddDocument(new TerminalDocument(first));
        Assert.IsTrue(vm.FileBrowser.IsVisible);

        // 模拟面板右上角关闭按钮:直接改当前面板实例的可见性。
        vm.FileBrowser.IsVisible = false;
        Assert.IsTrue(
            SpinWait.SpinUntil(
                () => settingsService.Current.TerminalBehavior.AutoOpenFileBrowser == false,
                TimeSpan.FromSeconds(5)),
            "手动关闭后,下次启动默认值要落成关闭。");

        TerminalTabViewModel second = CreateConnectedSshTab();
        vm.Layout.AddDocument(new TerminalDocument(second));
        Assert.IsFalse(vm.FileBrowser.IsVisible, "保存后的默认值会影响之后的新标签。");

        vm.Activate(first);
        Assert.IsFalse(vm.FileBrowser.IsVisible, "切回关闭过面板的标签:保持关闭。");
        vm.Activate(second);
        Assert.IsFalse(vm.FileBrowser.IsVisible, "切回按关闭默认值创建的标签:保持关闭。");
    }

    [TestMethod]
    [TestCategory("UI")]
    public async Task FileBrowser_PanelOpen_PersistsNextLaunchDefault()
    {
        var settingsService = new MemorySettingsService(
            new AppSettings { TerminalBehavior = new() { AutoOpenFileBrowser = false } });
        MainWindowViewModel vm = await CreateInitializedVmAsync(settingsService);

        TerminalTabViewModel first = CreateConnectedSshTab();
        vm.Layout.AddDocument(new TerminalDocument(first));
        Assert.IsFalse(vm.FileBrowser.IsVisible);

        vm.ToggleFileBrowser();

        Assert.IsTrue(vm.FileBrowser.IsVisible);
        Assert.IsTrue(
            SpinWait.SpinUntil(
                () => settingsService.Current.TerminalBehavior.AutoOpenFileBrowser,
                TimeSpan.FromSeconds(5)),
            "手动打开后,下次启动默认值要落成打开。");
    }

    [TestMethod]
    [TestCategory("UI")]
    public async Task FileBrowser_RapidCloseThenOpen_PersistsLastState()
    {
        var settingsService = new MemorySettingsService(
            new AppSettings { TerminalBehavior = new() { AutoOpenFileBrowser = true } },
            TimeSpan.FromMilliseconds(50));
        MainWindowViewModel vm = await CreateInitializedVmAsync(settingsService);

        vm.Layout.AddDocument(new TerminalDocument(CreateConnectedSshTab()));
        Assert.IsTrue(vm.FileBrowser.IsVisible);

        vm.FileBrowser.IsVisible = false;
        vm.ToggleFileBrowser();

        Assert.IsTrue(vm.FileBrowser.IsVisible);
        Assert.IsTrue(
            SpinWait.SpinUntil(
                () =>
                    settingsService.SaveCount >= 2
                    && settingsService.Current.TerminalBehavior.AutoOpenFileBrowser,
                TimeSpan.FromSeconds(5)),
            "连续关再开时,最后落盘的必须是最后一次打开。");
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task ActiveTerminalTab_FollowsSavedProfileWhenSettingEnabled()
    {
        ISettingsService settingsService = Substitute.For<ISettingsService>();
        ISessionRepository sessionRepository = Substitute.For<ISessionRepository>();
        SessionProfile profile = new()
        {
            Id = Guid.NewGuid(),
            Name = "server",
            Host = "server.example",
            Username = "root",
        };
        settingsService
            .GetSettingsAsync()
            .Returns(new AppSettings { General = new() { FollowActiveTerminalInExplorer = true } });
        settingsService.GetStateAsync().Returns(new AppState());
        sessionRepository.GetAllGroupsAsync().Returns([]);
        sessionRepository.GetAllSessionsAsync().Returns([profile]);
        var vm = new MainWindowViewModel(
            settingsService: settingsService,
            sessionRepository: sessionRepository
        );
        await vm.InitializeAsync();

        vm.Layout.AddDocument(new TerminalDocument(
            new TerminalTabViewModel(Substitute.For<ITerminalEmulator>()) { Profile = profile }
        ));

        Assert.AreEqual(profile.Id, vm.Sidebar.SessionTree?.SelectedNode?.Id);
    }

    [TestMethod]
    [TestCategory("SessionTree")]
    public async Task ActiveTerminalTab_DoesNotChangeTreeSelectionWhenSettingDisabled()
    {
        ISettingsService settingsService = Substitute.For<ISettingsService>();
        ISessionRepository sessionRepository = Substitute.For<ISessionRepository>();
        SessionProfile profile = new()
        {
            Id = Guid.NewGuid(),
            Name = "server",
            Host = "server.example",
            Username = "root",
        };
        settingsService
            .GetSettingsAsync()
            .Returns(
                new AppSettings { General = new() { FollowActiveTerminalInExplorer = false } }
            );
        settingsService.GetStateAsync().Returns(new AppState());
        sessionRepository.GetAllGroupsAsync().Returns([]);
        sessionRepository.GetAllSessionsAsync().Returns([profile]);
        var vm = new MainWindowViewModel(
            settingsService: settingsService,
            sessionRepository: sessionRepository
        );
        await vm.InitializeAsync();

        vm.Layout.AddDocument(new TerminalDocument(
            new TerminalTabViewModel(Substitute.For<ITerminalEmulator>()) { Profile = profile }
        ));

        Assert.IsNull(vm.Sidebar.SessionTree?.SelectedNode);
    }

    private sealed class MemorySettingsService(
        AppSettings initial,
        TimeSpan? saveDelay = null) : ISettingsService
    {
        private int _saveCount;

        public event Action<AppSettings>? SettingsSaved;

        public AppSettings Current { get; private set; } = initial;

        public int SaveCount => Volatile.Read(ref _saveCount);

        public Task<AppSettings> GetSettingsAsync() => Task.FromResult(Current);

        public async Task SaveSettingsAsync(AppSettings settings)
        {
            if (saveDelay is { } delay)
            {
                await Task.Delay(delay);
            }
            Current = settings;
            Interlocked.Increment(ref _saveCount);
            SettingsSaved?.Invoke(settings);
        }

        public Task<AppState> GetStateAsync() => Task.FromResult(new AppState());

        public Task SaveStateAsync(AppState state) => Task.CompletedTask;
    }
}

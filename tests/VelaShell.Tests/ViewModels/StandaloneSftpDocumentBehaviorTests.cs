using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Docking;
using VelaShell.Docking.Controls;
using VelaShell.Presentation.Services;
using VelaShell.Presentation.ViewModels;
using VelaShell.Terminal;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.ViewModels;

[TestClass]
public sealed class StandaloneSftpDocumentBehaviorTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Initialize(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(
            typeof(StandaloneSftpDocumentBehaviorTests).Assembly);
    }

    [TestMethod]
    [TestCategory("Sftp")]
    public async Task TryConnectProfileAsync_SftpProfile_DispatchesStandaloneFlowWithoutTerminal()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService sshConnectionService = Substitute.For<ISshConnectionService>();
        int terminalFactoryCalls = 0;
        SessionProfile profile = CreateSftpProfile();

        var vm = new MainWindowViewModel(
            workflow,
            sshConnectionService,
            () =>
            {
                terminalFactoryCalls++;
                return Substitute.For<ITerminalEmulator>();
            }
        );

        await vm.TryConnectProfileAsync(profile);

        await workflow.Received(1).ConnectProfileAsync(profile, Arg.Any<CancellationToken>());
        Assert.AreEqual(0, terminalFactoryCalls, "SFTP must not create a terminal emulator.");
        Assert.IsEmpty(vm.TerminalTabs, "Standalone SFTP must not create a terminal tab.");
    }

    [TestMethod]
    [TestCategory("Sftp")]
    public async Task OpenSftpForProfileAsync_SftpProfile_UsesStandaloneDispatcher()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService sshConnectionService = Substitute.For<ISshConnectionService>();
        SessionProfile profile = CreateSftpProfile();
        var vm = new MainWindowViewModel(
            workflow,
            sshConnectionService,
            () => Substitute.For<ITerminalEmulator>()
        );

        await vm.OpenSftpForProfileAsync(profile);

        await workflow.Received(1).ConnectProfileAsync(profile, Arg.Any<CancellationToken>());
        Assert.IsEmpty(vm.TerminalTabs, "Standalone SFTP must be represented by a document, not a terminal tab.");
    }

    [TestMethod]
    [TestCategory("Sftp")]
    public async Task OpenSftpForProfileAsync_SshProfile_DoesNotCreateShellTerminal()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService sshConnectionService = Substitute.For<ISshConnectionService>();
        ISshClientWrapper sshClient = Substitute.For<ISshClientWrapper>();
        IShellStreamWrapper shellStream = Substitute.For<IShellStreamWrapper>();
        int terminalFactoryCalls = 0;
        SessionProfile profile = CreateSshProfile();
        SshSession session = new()
        {
            SessionId = Guid.NewGuid(),
            ConnectionInfo = new()
            {
                Host = profile.Host,
                Port = profile.Port,
                Username = profile.Username,
                AuthMethod = profile.AuthMethod,
                Password = profile.Password,
            },
            Status = SessionStatus.Connected,
        };
        workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>()).Returns(session);
        sshConnectionService.GetClient(session.SessionId).Returns(sshClient);
        sshClient.CreateShellStreamAsync(
                Arg.Any<string>(),
                Arg.Any<uint>(),
                Arg.Any<uint>(),
                Arg.Any<uint>(),
                Arg.Any<uint>(),
                Arg.Any<int>(),
                Arg.Any<IReadOnlyDictionary<TerminalMode, uint>?>(), Arg.Any<CancellationToken>()
            )
            .Returns(shellStream);

        var vm = new MainWindowViewModel(
            workflow,
            sshConnectionService,
            () =>
            {
                terminalFactoryCalls++;
                return Substitute.For<ITerminalEmulator>();
            },
            sftpService: Substitute.For<ISftpService>()
        );

        await vm.OpenSftpForProfileAsync(profile);

        await workflow.Received(1).ConnectProfileAsync(profile, Arg.Any<CancellationToken>());
        await sshClient.DidNotReceive().CreateShellStreamAsync(
            Arg.Any<string>(),
            Arg.Any<uint>(),
            Arg.Any<uint>(),
            Arg.Any<uint>(),
            Arg.Any<uint>(),
            Arg.Any<int>(),
            Arg.Any<IReadOnlyDictionary<TerminalMode, uint>?>(), Arg.Any<CancellationToken>()
        );
        Assert.AreEqual(0, terminalFactoryCalls, "Explorer SFTP must not open a shell terminal first.");
    }

    [TestMethod]
    [TestCategory("Sftp")]
    public async Task OpenSftpCommand_SftpProfile_RaisesStandaloneOpenRequest()
    {
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        SessionProfile profile = CreateSftpProfile();
        repository.GetAllGroupsAsync().Returns([]);
        repository.GetAllSessionsAsync().Returns([profile]);
        var tree = new SessionTreeViewModel(repository);
        await tree.LoadCommand.Execute().FirstAsync();
        tree.SelectedNode = tree.Nodes.Single();
        SessionProfile? requested = null;
        tree.OpenSftpRequested += profile => requested = profile;

        await tree.OpenSftpCommand.Execute().FirstAsync();

        Assert.AreSame(profile, requested);
    }

    [TestMethod]
    [TestCategory("Sftp")]
    public async Task SftpDocumentClose_WhenCallerWaitIsCancelled_StillDisconnectsEventually()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISftpService sftp = Substitute.For<ISftpService>();
        SshSession session = CreateSession();
        ConfigureInitialLoad(sftp, session.SessionId);
        var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sftp.CloseSessionAsync(session.SessionId, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                closeStarted.SetResult();
                await releaseClose.Task;
            });
        var document = new SftpDocumentViewModel(
            CreateSftpProfile(),
            session,
            workflow,
            sftp,
            new TransferOptions());
        using var cancellation = new CancellationTokenSource();

        Task wait = document.CloseAsync(cancellation.Token);
        await closeStarted.Task;
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => wait);

        releaseClose.SetResult();
        await document.CloseAsync();
        await workflow.Received(1).DisconnectAsync(session.SessionId, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("Sftp")]
    public async Task SftpDocumentClose_WhenSerializerCloseFails_StillDisconnectsOnce()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISftpService sftp = Substitute.For<ISftpService>();
        SshSession session = CreateSession();
        ConfigureInitialLoad(sftp, session.SessionId);
        sftp.CloseSessionAsync(session.SessionId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new InvalidOperationException("close failed")));
        var document = new SftpDocumentViewModel(
            CreateSftpProfile(),
            session,
            workflow,
            sftp,
            new TransferOptions());

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => document.CloseAsync());
        await workflow.Received(1).DisconnectAsync(session.SessionId, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    [TestCategory("Sftp")]
    public async Task CloseSftpDocumentAsync_WhenCloseCompletesOnWorker_UpdatesSessionTreeOnUiThread()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISessionRepository repository = Substitute.For<ISessionRepository>();
        ISftpService sftp = Substitute.For<ISftpService>();
        SessionProfile profile = CreateSftpProfile();
        SshSession session = CreateSession(profile.Id);
        repository.GetAllGroupsAsync().Returns([]);
        repository.GetAllSessionsAsync().Returns([profile]);
        ConfigureInitialLoad(sftp, session.SessionId);
        var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int closeContinuationThread = 0;
        sftp.CloseSessionAsync(session.SessionId, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                closeStarted.SetResult();
                await releaseClose.Task;
                closeContinuationThread = Environment.CurrentManagedThreadId;
            });

        var vm = new MainWindowViewModel(
            workflow,
            sessionRepository: repository,
            sftpService: sftp);
        await vm.Sidebar.SessionTree!.LoadCommand.Execute().FirstAsync();
        SessionTreeNodeViewModel node = vm.Sidebar.SessionTree.Nodes.Single();
        vm.Sidebar.SessionTree.SetSessionStatus(profile.Id, SessionStatus.Connected);
        List<int> notificationThreads = [];
        void propertyChanged(object? _, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(SessionTreeNodeViewModel.Status))
            {
                notificationThreads.Add(Environment.CurrentManagedThreadId);
            }
        }
        node.PropertyChanged += propertyChanged;

        var document = new SftpDocument(
            new SftpDocumentViewModel(
                profile,
                session,
                workflow,
                sftp,
                new TransferOptions()));
        MethodInfo closeMethod = typeof(MainWindowViewModel).GetMethod(
            "CloseSftpDocumentAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        try
        {
            await _session.Dispatch(async () =>
            {
                int uiThread = Environment.CurrentManagedThreadId;
                var invocation = Task.Run(() => (Task)closeMethod.Invoke(vm, [document])!);
                await closeStarted.Task;
                await Task.Run(releaseClose.SetResult);
                await invocation;

                Assert.AreEqual(SessionStatus.Disconnected, node.Status);
                Assert.AreNotEqual(uiThread, closeContinuationThread);
                Assert.IsNotEmpty(notificationThreads);
                Assert.IsTrue(notificationThreads.All(thread => thread == uiThread));
            }, CancellationToken.None);
        }
        finally
        {
            node.PropertyChanged -= propertyChanged;
        }
    }

    [TestMethod]
    [TestCategory("Sftp")]
    public async Task CloseSftpDocumentAsync_WhenCloseFaultsOnWorker_UpdatesLastConnectionErrorOnUiThread()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISftpService sftp = Substitute.For<ISftpService>();
        SessionProfile profile = CreateSftpProfile();
        SshSession session = CreateSession(profile.Id);
        ConfigureInitialLoad(sftp, session.SessionId);
        var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int closeContinuationThread = 0;
        sftp.CloseSessionAsync(session.SessionId, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                closeStarted.SetResult();
                await releaseClose.Task;
                closeContinuationThread = Environment.CurrentManagedThreadId;
                throw new InvalidOperationException("close failed");
            });

        var vm = new MainWindowViewModel(workflow, sftpService: sftp);
        List<int> notificationThreads = [];
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.LastConnectionError))
            {
                notificationThreads.Add(Environment.CurrentManagedThreadId);
            }
        };
        var document = new SftpDocument(
            new SftpDocumentViewModel(
                profile,
                session,
                workflow,
                sftp,
                new TransferOptions()));
        MethodInfo closeMethod = typeof(MainWindowViewModel).GetMethod(
            "CloseSftpDocumentAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        await _session.Dispatch(async () =>
        {
            int uiThread = Environment.CurrentManagedThreadId;
            var invocation = Task.Run(() => (Task)closeMethod.Invoke(vm, [document])!);
            await closeStarted.Task;
            await Task.Run(releaseClose.SetResult);
            await invocation;

            Assert.AreEqual("close failed", vm.LastConnectionError);
            Assert.AreNotEqual(uiThread, closeContinuationThread);
            Assert.IsNotEmpty(notificationThreads);
            Assert.IsTrue(notificationThreads.All(thread => thread == uiThread));
        }, CancellationToken.None);
    }

    [TestMethod]
    [TestCategory("Sftp")]
    public async Task MainWindowClose_WhenDocumentCleanupAlreadyStarted_StillWaitsForIt()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISftpService sftp = Substitute.For<ISftpService>();
        var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SessionProfile profile = CreateSftpProfile();
        SshSession session = CreateSession(profile.Id);
        ConfigureInitialLoad(sftp, session.SessionId);
        sftp.CloseSessionAsync(session.SessionId, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                closeStarted.SetResult();
                await releaseClose.Task;
            });

        await _session.Dispatch(async () =>
        {
            var vm = new MainWindowViewModel(workflow, sftpService: sftp);
            var document = new SftpDocument(
                new SftpDocumentViewModel(
                    profile,
                    session,
                    workflow,
                    sftp,
                    new TransferOptions()));
            vm.Layout.AddDocument(document);
            vm.Layout.CloseDocument(document);
            await closeStarted.Task;

            var window = new MainWindow { DataContext = vm };
            var finalClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => finalClose.TrySetResult();
            window.Show();
            window.Close();
            await Dispatcher.UIThread.InvokeAsync(() => { });

            Assert.IsTrue(window.IsVisible, "The window must remain open while the removed document is still closing.");
            Assert.IsFalse(finalClose.Task.IsCompleted, "Shutdown must await an already-running document close.");
            releaseClose.SetResult();
            await finalClose.Task;
            await sftp.Received(1).CloseSessionAsync(session.SessionId, Arg.Any<CancellationToken>());
            await workflow.Received(1).DisconnectAsync(session.SessionId);
        }, CancellationToken.None);
    }

    [TestMethod]
    [TestCategory("Sftp")]
    public async Task MainWindowClose_StandaloneSftpCountsAsConnectedForConfirmation()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISftpService sftp = Substitute.For<ISftpService>();
        SessionProfile profile = CreateSftpProfile();
        SshSession session = CreateSession(profile.Id);
        ConfigureInitialLoad(sftp, session.SessionId);

        await _session.Dispatch(() =>
        {
            var vm = new MainWindowViewModel(workflow, sftpService: sftp);
            vm.Layout.AddDocument(new SftpDocument(
                new SftpDocumentViewModel(
                    profile,
                    session,
                    workflow,
                    sftp,
                    new TransferOptions())));
            var window = new MainWindow { DataContext = vm };
            MethodInfo hasConnected = typeof(MainWindow).GetMethod(
                "HasConnectedSessions",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            Assert.IsTrue((bool)hasConnected.Invoke(window, null)!);
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [TestMethod]
    [TestCategory("Sftp")]
    public async Task MainWindowClose_PersistsConnectedTerminalAndStandaloneSftpProfiles()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISftpService sftp = Substitute.For<ISftpService>();
        ISettingsService settingsService = Substitute.For<ISettingsService>();
        SessionProfile sftpProfile = CreateSftpProfile();
        SshSession sftpSession = CreateSession(sftpProfile.Id);
        ConfigureInitialLoad(sftp, sftpSession.SessionId);
        SessionProfile terminalProfile = CreateSshProfile();
        var settings = new AppSettings();
        settings.General.RestoreSessionsOnStartup = true;

        await _session.Dispatch(() =>
        {
            var vm = new MainWindowViewModel(workflow, sftpService: sftp);
            vm.Layout.AddDocument(new TerminalDocument(new TerminalTabViewModel(Substitute.For<ITerminalEmulator>())
            {
                Profile = terminalProfile,
                ConnectionStatus = SessionStatus.Connected,
            }));
            vm.Layout.AddDocument(new SftpDocument(
                new SftpDocumentViewModel(
                    sftpProfile,
                    sftpSession,
                    workflow,
                    sftp,
                    new TransferOptions())));
            var window = new MainWindow { DataContext = vm };
            typeof(MainWindow)
                .GetField("_settingsService", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(window, settingsService);
            MethodInfo persist = typeof(MainWindow).GetMethod(
                "PersistWindowBounds",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            persist.Invoke(window, [settings]);

            Assert.AreSequenceEqual(
                [terminalProfile.Id, sftpProfile.Id], settings.General.LastOpenProfileIds, SequenceOrder.InAnyOrder);
            settingsService.Received(1).SaveSettingsAsync(settings);
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [TestMethod]
    [TestCategory("Sftp")]
    public void SftpDocument_ConnectionTooltipUsesNeutralConnectionIdentity()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISftpService sftp = Substitute.For<ISftpService>();
        SshSession session = CreateSession();
        ConfigureInitialLoad(sftp, session.SessionId);
        var viewModel = new SftpDocumentViewModel(
            CreateSftpProfile(),
            session,
            workflow,
            sftp,
            new TransferOptions());
        var document = new SftpDocument(viewModel);

        Assert.AreEqual("Files · SFTP · root@files.example.com:22", document.ConnectionTooltip);
    }

    /// <summary>
    /// 回归:独立 SFTP 标签的远程栏必须拿到「默认编辑器」解析回调。
    /// 曾经只有终端侧边栏的 FileBrowserViewModel 被宿主注入该回调,独立 SFTP 标签自己 new
    /// 的那个漏了,导致右键「使用默认编辑器打开」在明明已配置的情况下仍报“未配置”。
    /// </summary>
    [TestMethod]
    [TestCategory("Sftp")]
    public async Task SftpDocument_ForwardsDefaultEditorResolverToRemotePane()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISftpService sftp = Substitute.For<ISftpService>();
        SshSession session = CreateSession();
        ConfigureInitialLoad(sftp, session.SessionId);

        var document = new SftpDocumentViewModel(
            CreateSftpProfile(),
            session,
            workflow,
            sftp,
            new TransferOptions(),
            transferSink: null,
            getDefaultEditorPath: () => Task.FromResult<string?>("code"));

        Assert.IsNotNull(
            document.RemoteFiles.GetDefaultEditorPath,
            "远程栏没有拿到默认编辑器解析回调,「使用默认编辑器打开」会误报未配置。");
        Assert.AreEqual("code", await document.RemoteFiles.GetDefaultEditorPath!());
    }

    /// <summary>未注入解析回调时的失败形态:面板只能当作“未配置”处理(即修复前的行为)。</summary>
    [TestMethod]
    [TestCategory("Sftp")]
    public void SftpDocument_WithoutDefaultEditorResolver_LeavesResolverUnset()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISftpService sftp = Substitute.For<ISftpService>();
        SshSession session = CreateSession();
        ConfigureInitialLoad(sftp, session.SessionId);

        var document = new SftpDocumentViewModel(
            CreateSftpProfile(),
            session,
            workflow,
            sftp,
            new TransferOptions());

        Assert.IsNull(document.RemoteFiles.GetDefaultEditorPath);
    }

    /// <summary>
    /// 下载完成后对本地栏的刷新通知必须留在 UI 线程上。
    /// (自 Todo2VisualCaptureUiTests 迁入:那个文件其余部分是视觉断言,已删除;这条验的是
    /// 线程编组这种真会出 bug 的行为,与视觉无关,故保留。)
    /// </summary>
    [TestMethod]
    [TestCategory("Sftp")]
    public async Task DownloadSelectedAsync_RefreshNotificationsStayOnUiSynchronizationContext()
    {
        await _session.Dispatch(async () =>
        {
            (MainWindowViewModel vm, SessionProfile profile, _, ISftpService sftpService) = CreateConnectedSshViewModel();
            await vm.OpenSftpForProfileAsync(profile);
            SftpDocument document = vm.Layout.AllDocuments().OfType<SftpDocument>().Single();
            await document.ViewModel.InitialLoadTask;
            RemoteFileInfoViewModel remote = document.ViewModel.RemoteFiles.Files.Single(file => !file.IsParentEntry);
            document.ViewModel.RemoteFiles.SelectedFiles.Add(remote);

            int uiThread = Environment.CurrentManagedThreadId;
            List<int> notificationThreads = [];
            void propertyChanged(object? _1, PropertyChangedEventArgs _2) => notificationThreads.Add(Environment.CurrentManagedThreadId);
            void collectionChanged(object? _1, NotifyCollectionChangedEventArgs _2) => notificationThreads.Add(Environment.CurrentManagedThreadId);
            document.ViewModel.LocalFiles.PropertyChanged += propertyChanged;
            document.ViewModel.LocalFiles.Entries.CollectionChanged += collectionChanged;

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            sftpService.DownloadFileAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<IProgress<TransferProgress>?>(),
                    cancellationToken: Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    started.SetResult();
                    return finished.Task;
                });

            try
            {
                Task download = document.ViewModel.DownloadSelectedAsync();
                await started.Task;

                // 从线程池完成下载 —— 若刷新通知没被编组回 UI 线程,下面的断言就会抓到工作线程 Id。
                await Task.Run(finished.SetResult);
                await download;
                Assert.IsNotEmpty(notificationThreads);
                Assert.IsTrue(notificationThreads.All(thread => thread == uiThread));
            }
            finally
            {
                document.ViewModel.LocalFiles.PropertyChanged -= propertyChanged;
                document.ViewModel.LocalFiles.Entries.CollectionChanged -= collectionChanged;

                // 收尾必须留在 Dispatch 体**内部** await。headless 会话只在派发的工作项执行期间
                // 泵送 Avalonia dispatcher;把 CloseAsync 挪到工作项之外等,它要排空的在途操作
                // 其续体绑在 UI 线程上,永远不会被执行 —— 实测那样改会让本用例 60s 超时。
                await document.ViewModel.CloseAsync();
            }
        }, CancellationToken.None);
    }

    /// <summary>装配一个「SSH 会话已连上、SFTP 可列目录」的主窗口视图模型(含一个 readme.txt)。</summary>
    private static (MainWindowViewModel ViewModel, SessionProfile Profile, ISshClientWrapper SshClient, ISftpService SftpService) CreateConnectedSshViewModel()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService sshConnectionService = Substitute.For<ISshConnectionService>();
        ISshClientWrapper sshClient = Substitute.For<ISshClientWrapper>();
        IShellStreamWrapper shellStream = Substitute.For<IShellStreamWrapper>();
        ISftpService sftpService = Substitute.For<ISftpService>();
        ITerminalEmulator terminal = Substitute.For<ITerminalEmulator>();
        shellStream.CanRead.Returns(true);
        shellStream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new TaskCompletionSource<int>().Task);
        var profile = new SessionProfile
        {
            Name = "Files",
            Host = "files.example.com",
            Port = 22,
            Username = "root",
            AuthMethod = AuthMethod.Password,
            Password = "secret",
        };
        var session = new SshSession
        {
            SessionId = Guid.NewGuid(),
            ConnectionInfo = new()
            {
                Host = profile.Host,
                Port = profile.Port,
                Username = profile.Username,
                AuthMethod = profile.AuthMethod,
            },
            Status = SessionStatus.Connected,
        };
        workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>()).Returns(session);
        sshConnectionService.GetClient(session.SessionId).Returns(sshClient);
        sshClient.CreateShellStreamAsync("xterm-256color", 120, 32, 0, 0, 4096, Arg.Any<IReadOnlyDictionary<TerminalMode, uint>?>(), Arg.Any<CancellationToken>()).Returns(shellStream);
        sftpService.GetWorkingDirectoryAsync(session.SessionId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("/home/testuser"));
        sftpService.ListDirectoryAsync(session.SessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>
            {
                new()
                {
                    Name = "readme.txt",
                    FullPath = "/home/testuser/readme.txt",
                    Size = 42,
                    Permissions = "-rw-r--r--",
                    IsDirectory = false,
                    LastModified = new DateTime(2026, 7, 19),
                    Owner = "testuser",
                    Group = "testuser",
                },
            }));
        return (
            new MainWindowViewModel(workflow, sshConnectionService, () => terminal, sftpService: sftpService),
            profile,
            sshClient,
            sftpService
        );
    }

    [TestMethod]
    [TestCategory("Sftp")]
    public async Task SftpTabItem_ForAnFtpDocumentWithoutSshSession_BindsTheStatusDotWithoutErrors()
    {
        // FTP 文档没有 SshSession(它不走 SSH 握手)。标签页曾直接绑 ViewModel.Session.Status,
        // 于是每开一个 FTP 标签就刷一条
        // "An error occurred binding 'Fill' to 'ViewModel.Session.Status': 'Value is null.'",
        // 那颗状态灯还自始至终不上色。状态改由视图模型给出,两个问题一起消失。
        ISftpService sftp = Substitute.For<ISftpService>();
        var sessionId = Guid.NewGuid();
        ConfigureInitialLoad(sftp, sessionId);

        await _session.Dispatch(() =>
        {
            var sink = new BindingErrorSink();
            ILogSink? previous = Logger.Sink;
            Logger.Sink = sink;
            try
            {
                // 先用一条必错的绑定验明接收器在工作,否则"没有错误"只是空跑。
                var canary = new TextBlock();
                canary.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding("Missing.Deeper"));
                var probe = new Window { Content = canary, DataContext = new object() };
                probe.Show();
                Dispatcher.UIThread.RunJobs();
                probe.Close();
                Assert.IsNotEmpty(sink.Errors, "绑定错误接收器没有生效,后面的断言会一直是空跑。");
                sink.Errors.Clear();

                var document = new SftpDocument(new SftpDocumentViewModel(
                    CreateFtpProfile(),
                    sessionId,
                    static (_, _) => Task.CompletedTask,
                    sftp,
                    new TransferOptions()));
                Assert.IsNull(document.ViewModel.Session, "FTP 文档不该有 SSH 会话。");
                Assert.AreEqual(SessionStatus.Connected, document.ViewModel.Status);

                var tab = new SftpDockTabItem { DataContext = document };
                var window = new Window { Content = tab };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                try
                {
                    Ellipse dot = tab.GetVisualDescendants().OfType<Ellipse>().First();
                    Assert.IsNotNull(dot.Fill, "状态灯没有拿到画刷,标签上就是个看不见的点。");
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Logger.Sink = previous;
            }

            Assert.IsEmpty(sink.Errors, $"出现了 {sink.Errors.Count} 条绑定错误,例如:{sink.Errors.FirstOrDefault()}");
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    /// <summary>只收集绑定相关的告警/错误,供上面的回归测试断言。</summary>
    private sealed class BindingErrorSink : ILogSink
    {
        public List<string> Errors { get; } = [];

        public bool IsEnabled(LogEventLevel level, string area) =>
            level >= LogEventLevel.Warning && area == LogArea.Binding;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
        {
            if (IsEnabled(level, area))
            {
                Errors.Add(messageTemplate);
            }
        }

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            if (IsEnabled(level, area))
            {
                Errors.Add(messageTemplate + " | " + string.Join(", ", propertyValues));
            }
        }
    }

    private static SessionProfile CreateFtpProfile() => new()
    {
        ConnectionType = ConnectionType.FTP,
        Name = "Files",
        Host = "files.example.com",
        Port = 21,
        Username = "anonymous",
        AuthMethod = AuthMethod.Password,
        Password = "secret",
    };

    private static void ConfigureInitialLoad(ISftpService sftp, Guid sessionId)
    {
        sftp.GetWorkingDirectoryAsync(sessionId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("/"));
        sftp.ListDirectoryAsync(sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<RemoteFileInfo>()));
    }

    private static SshSession CreateSession(Guid? sessionId = null) => new()
    {
        SessionId = sessionId ?? Guid.NewGuid(),
        ConnectionInfo = new()
        {
            Host = "files.example.com",
            Port = 22,
            Username = "root",
            AuthMethod = AuthMethod.Password,
            Password = "secret",
        },
        Status = SessionStatus.Connected,
    };

    private static SessionProfile CreateSftpProfile() => new()
    {
        ConnectionType = ConnectionType.SFTP,
        Name = "Files",
        Host = "files.example.com",
        Port = 22,
        Username = "root",
        AuthMethod = AuthMethod.Password,
        Password = "secret",
    };

    private static SessionProfile CreateSshProfile() => new()
    {
        ConnectionType = ConnectionType.SSH,
        Name = "Server",
        Host = "server.example.com",
        Port = 22,
        Username = "root",
        AuthMethod = AuthMethod.Password,
        Password = "secret",
    };
}

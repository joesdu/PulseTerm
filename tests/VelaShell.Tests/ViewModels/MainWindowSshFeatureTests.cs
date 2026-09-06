using System.Net.Sockets;
using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.Docking;
using VelaShell.Presentation.Services;
using VelaShell.Tests.TestSupport;
using VelaShell.Terminal;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

[TestClass]
public sealed class MainWindowSshFeatureTests
{
    [TestMethod]
    public void BuildStartupCommand_WithoutUserCommand_InstallsWorkingDirectoryReporter()
    {
        string command = MainWindowViewModel.BuildStartupCommand(null);

        Assert.Contains("PROMPT_COMMAND", command);
        Assert.Contains("vela_shell_osc7", command);
        Assert.Contains(@"\033]7;file://%s%s", command);
    }

    [TestMethod]
    public void BuildStartupCommand_WithUserCommand_AppendsItToReporter()
    {
        string command = MainWindowViewModel.BuildStartupCommand("  cd /srv/app  ");

        Assert.Contains("vela_shell_osc7", command);
        Assert.EndsWith("; cd /srv/app", command);
    }

    /// <summary>
    /// 关掉「上报终端工作目录」后必须一个字节都不注入(#286):用户报的就是每次开窗
    /// 终端里都要闪一串 test -n "$BASH_VERSION"。空串在 SendSilentCommand 里被直接丢弃,
    /// 连多余的回车都不会有。
    /// </summary>
    [TestMethod]
    public void BuildStartupCommand_WhenReportingDisabled_WithoutUserCommand_InjectsNothing()
    {
        string command = MainWindowViewModel.BuildStartupCommand(null, reportWorkingDirectory: false);

        Assert.IsEmpty(command);
    }

    /// <summary>关掉钩子不该连累用户自己的"连接后执行命令" —— 那是两件事。</summary>
    [TestMethod]
    public void BuildStartupCommand_WhenReportingDisabled_KeepsUserCommandOnly()
    {
        string command = MainWindowViewModel.BuildStartupCommand("  cd /srv/app  ", reportWorkingDirectory: false);

        Assert.AreEqual("cd /srv/app", command);
        Assert.DoesNotContain("vela_shell_osc7", command);
        Assert.DoesNotContain("BASH_VERSION", command);
    }

    [TestMethod]
    public async Task ConnectProfileAsync_AddsTerminalTab_AndUpdatesStatusBar()
    {
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService? sshConnectionService = Substitute.For<ISshConnectionService>();
        ISshClientWrapper? sshClient = Substitute.For<ISshClientWrapper>();
        IShellStreamWrapper? shellStream = Substitute.For<IShellStreamWrapper>();
        ITerminalEmulator? terminal = Substitute.For<ITerminalEmulator>();

        // 模拟活连接:读循环阻塞在 ReadAsync(不立即 EOF),否则桥会异步触发 Closed 与连接
        // 结果竞态,把刚连上的标签翻回断开(真实连接不会立即 EOF)。
        shellStream.CanRead.Returns(true);
        shellStream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                   .Returns(new TaskCompletionSource<int>().Task);
        var profile = new SessionProfile
        {
            Name = "Prod",
            Host = "prod.example.com",
            Port = 22,
            Username = "root",
            AuthMethod = AuthMethod.Password,
            Password = "secret"
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
                Password = profile.Password
            },
            Status = SessionStatus.Connected
        };
        workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>()).Returns(session);
        sshConnectionService.GetClient(session.SessionId).Returns(sshClient);
        sshClient.CreateShellStreamAsync("xterm-256color", 120, 32, 0, 0, 4096, Arg.Any<IReadOnlyDictionary<TerminalMode, uint>?>(), Arg.Any<CancellationToken>()).Returns(shellStream);

        // 连接历史由工作流写入 SonnetDB;侧边栏“最近连接”刷新时从服务读取。
        IRecentConnectionService? recents = Substitute.For<IRecentConnectionService>();
        recents.GetRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns([
                   new() { ProfileId = profile.Id, Name = "Prod", GroupName = "生产环境", Host = profile.Host, Port = 22, Username = "root" }
               ]);
        var vm = new MainWindowViewModel(workflow, sshConnectionService, () => terminal, recentConnectionService: recents);
        TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(profile);
        Assert.IsNotNull(tab);
        Assert.AreEqual("Prod", tab.Title);
        Assert.AreEqual(SessionStatus.Connected, tab.ConnectionStatus);
        Assert.AreSame(tab, vm.ActiveTerminalTab);
        Assert.HasCount(1, vm.TerminalTabs);
        // 设计 gzmsb 调整(cfe16d2):状态栏只显示"SSH • <显示名称>",不暴露用户名/IP/端口(安全要求)。
        Assert.AreEqual("SSH • Prod", vm.StatusBar.ConnectionInfo);
        Assert.AreEqual(Strings.Connected, vm.StatusBar.Status);
        Assert.HasCount(1, vm.Sidebar.RecentConnections.Connections);
        Assert.AreEqual("Prod - 生产环境", vm.Sidebar.RecentConnections.Connections[0].DisplayName);
    }

    /// <summary>
    /// 配置自带的「认证后执行命令」必须真的注入到这条会话的 shell 里(延迟 0 = 握手完立刻发)。
    /// 它与设置里那条全局命令是两件事,顺序固定「先全局、后本条」—— 与用户在两个界面上
    /// 看到的顺序一致。只断言本条命令确实落到了线上:全局那条由 BuildStartupCommand 的用例覆盖。
    /// </summary>
    [TestMethod]
    public async Task ConnectProfileAsync_InjectsThePerProfilePostAuthCommand()
    {
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService? sshConnectionService = Substitute.For<ISshConnectionService>();
        ISshClientWrapper? sshClient = Substitute.For<ISshClientWrapper>();
        IShellStreamWrapper? shellStream = Substitute.For<IShellStreamWrapper>();
        ITerminalEmulator? terminal = Substitute.For<ITerminalEmulator>();

        // 同上一个用例:读循环必须阻塞而不是立刻 EOF,否则桥会把刚连上的标签翻回断开。
        shellStream.CanRead.Returns(true);
        shellStream.CanWrite.Returns(true);
        shellStream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                   .Returns(new TaskCompletionSource<int>().Task);

        // 出站写由桥的写循环在后台线程逐段刷出,不与 TryConnectProfileAsync 同步完成;
        // 用 TCS 等那一段字节真的落到流上,而不是 sleep 一个猜出来的毫秒数。
        var injected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        shellStream.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                   .Returns(callInfo =>
                   {
                       string payload = System.Text.Encoding.UTF8.GetString(
                           (byte[])callInfo[0], (int)callInfo[1], (int)callInfo[2]);
                       if (payload.Contains("tmux attach", StringComparison.Ordinal))
                       {
                           injected.TrySetResult(payload);
                       }
                       return Task.CompletedTask;
                   });

        var profile = new SessionProfile
        {
            Name = "dev",
            Host = "dev.example.com",
            Port = 22,
            Username = "dev",
            AuthMethod = AuthMethod.Password,
            Password = "secret",
            PostAuthCommand = "  tmux attach  ",
            PostAuthCommandDelaySeconds = 0,
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
                Password = profile.Password
            },
            Status = SessionStatus.Connected
        };
        workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>()).Returns(session);
        sshConnectionService.GetClient(session.SessionId).Returns(sshClient);
        sshClient.CreateShellStreamAsync("xterm-256color", 120, 32, 0, 0, 4096,
                                         Arg.Any<IReadOnlyDictionary<TerminalMode, uint>?>(),
                                         Arg.Any<CancellationToken>())
                 .Returns(shellStream);

        var vm = new MainWindowViewModel(workflow, sshConnectionService, () => terminal);
        TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(profile);

        Assert.IsNotNull(tab);
        string payload = await injected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // 首尾空白在保存与注入两处都会被裁掉;注入本身按 SendSilentCommand 的约定
        // 前置一个空格(避免进 shell 历史)、以 \n 收尾。
        Assert.AreEqual(" tmux attach\n", payload);
    }

    [TestMethod]
    public async Task TryConnectProfileAsync_AuthFailure_DoesNotThrow_AndReportsError()
    {
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService? sshConnectionService = Substitute.For<ISshConnectionService>();
        var profile = new SessionProfile
        {
            Name = "Prod",
            Host = "prod.example.com",
            Port = 22,
            Username = "root",
            AuthMethod = AuthMethod.Password,
            Password = "wrong"
        };

        // 模拟服务端拒绝密码。必须抛 Core 的真实中立类型:VM 按类型匹配(ex is VelaSshAuthenticationException),
        // 这里以前伪造过一个同名假异常去迎合旧的类型名字符串匹配,结果生产路径从未被覆盖。
        workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>())
                .Returns<Task<SshSession>>(_ => throw new VelaSshAuthenticationException("Permission denied (password)."));
        var vm = new MainWindowViewModel(workflow, sshConnectionService, () => Substitute.For<ITerminalEmulator>());
        TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(profile);
        Assert.IsNull(tab);
        Assert.IsEmpty(vm.TerminalTabs);
        Assert.IsFalse(string.IsNullOrEmpty(vm.LastConnectionError));

        // 比对本地化资源而非中文字面量:该文案随 UI 语言变化,写死会让测试只在中文环境通过。
        // 提示后面还会换行附上底层库给出的具体原因(DescribeConnectionError 有意为之,便于用户诊断)。
        // 中间那一句是两步验证说明:服务器只放行 keyboard-interactive(2FA / OTP)时,
        // "用户名、密码或密钥不正确"是**错的** —— 凭据没问题,是本版根本不会那套认证。
        // 少了这句,用户会照着错文案反复改密码,永远改不对(F-11)。
        Assert.AreEqual(
            $"{Strings.Format("Msg_AuthFailed", "root@prod.example.com:22")}"
            + $"\n{Strings.Get("Msg_AuthFailedTwoFactorHint")}"
            + "\nPermission denied (password).",
            vm.LastConnectionError);
    }

    [TestMethod]
    public async Task TryConnectProfileAsync_NetworkFailure_KeepsTab_WithInTabOverlay()
    {
        // 设计 yxjmg:网络/超时失败不销毁标签、不弹全局框,仅在标签页内显示失败覆盖层。
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService? sshConnectionService = Substitute.For<ISshConnectionService>();
        var profile = new SessionProfile
        {
            Name = "LAN",
            Host = "192.168.1.50",
            Port = 22,
            Username = "root",
            AuthMethod = AuthMethod.Password,
            Password = "secret"
        };
        workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>())
                .Returns<Task<SshSession>>(_ => throw new SocketException());
        var vm = new MainWindowViewModel(workflow, sshConnectionService, () => Substitute.For<ITerminalEmulator>());
        TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(profile);
        Assert.IsNotNull(tab);
        Assert.HasCount(1, vm.TerminalTabs);
        Assert.AreEqual(SessionStatus.Disconnected, tab.ConnectionStatus);
        Assert.IsTrue(tab.ShowDisconnectedOverlay);
        Assert.IsTrue(tab.HasConnectionError);
        Assert.AreEqual(Strings.Get("Msg_ConnectionFailedTitle"), tab.DisconnectOverlayTitle);
        Assert.IsFalse(string.IsNullOrEmpty(vm.LastConnectionError));
    }

    [TestMethod]
    public async Task ReconnectTabAsync_ReusesSameTab_AndRestoresConnectedState()
    {
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService? sshConnectionService = Substitute.For<ISshConnectionService>();
        ISshClientWrapper? sshClient = Substitute.For<ISshClientWrapper>();
        IShellStreamWrapper? shellStream = Substitute.For<IShellStreamWrapper>();
        // 模拟活连接:读循环阻塞在 ReadAsync,避免桥立即 EOF 触发 Closed 与连接结果竞态。
        shellStream.CanRead.Returns(true);
        shellStream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                   .Returns(new TaskCompletionSource<int>().Task);
        ITerminalEmulator? terminal = Substitute.For<ITerminalEmulator>();
        var profile = new SessionProfile
        {
            Name = "Prod",
            Host = "prod.example.com",
            Port = 22,
            Username = "root",
            AuthMethod = AuthMethod.Password,
            Password = "secret"
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
                Password = profile.Password
            },
            Status = SessionStatus.Connected
        };
        workflow.ConnectProfileAsync(profile, Arg.Any<CancellationToken>()).Returns(session);
        sshConnectionService.GetClient(session.SessionId).Returns(sshClient);
        sshClient.CreateShellStreamAsync("xterm-256color", 120, 32, 0, 0, 4096, Arg.Any<IReadOnlyDictionary<TerminalMode, uint>?>(), Arg.Any<CancellationToken>()).Returns(shellStream);
        var vm = new MainWindowViewModel(workflow, sshConnectionService, () => terminal);
        TerminalTabViewModel? tab = await vm.TryConnectProfileAsync(profile);
        Assert.IsNotNull(tab);
        Assert.AreEqual(SessionStatus.Connected, tab.ConnectionStatus);

        // The remote drops (exit / reboot).
        tab.MarkDisconnected();
        Assert.AreEqual(SessionStatus.Disconnected, tab.ConnectionStatus);

        // Reconnect in place — same tab, not a new one.
        await vm.ReconnectTabAsync(tab);
        Assert.AreEqual(SessionStatus.Connected, tab.ConnectionStatus);
        Assert.HasCount(1, vm.TerminalTabs);
        Assert.AreSame(tab, vm.TerminalTabs.First());
    }

    [TestMethod]
    public async Task ReconnectTabAsync_WhenAlreadyConnected_IsNoOp()
    {
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService? sshConnectionService = Substitute.For<ISshConnectionService>();
        ITerminalEmulator? terminal = Substitute.For<ITerminalEmulator>();
        var vm = new MainWindowViewModel(workflow, sshConnectionService, () => terminal);
        var tab = new TerminalTabViewModel(terminal)
        {
            ConnectionStatus = SessionStatus.Connected,
            Profile = new() { Host = "h", Port = 22, Username = "u" }
        };
        await vm.ReconnectTabAsync(tab);

        // No connect attempt should have been made.
        await workflow.DidNotReceive().ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());
        Assert.AreEqual(SessionStatus.Connected, tab.ConnectionStatus);
    }

    [TestMethod]
    public async Task InitializeAsync_LoadsRecentHistory_IntoRecentConnections()
    {
        IRecentConnectionService? recents = Substitute.For<IRecentConnectionService>();
        recents.GetRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns([
                   new() { Name = "Prod", GroupName = "生产环境", Host = "prod.example.com", Port = 22, Username = "root", ConnectedAt = DateTimeOffset.Now.AddHours(-2) },
                   new() { Name = "Dev", Host = "dev.example.com", Port = 22, Username = "pi", ConnectedAt = DateTimeOffset.Now.AddDays(-3) }
               ]);
        var vm = new MainWindowViewModel(recentConnectionService: recents);
        await vm.InitializeAsync();
        Assert.HasCount(2, vm.Sidebar.RecentConnections.Connections);
        // 名称是测试数据(照原样显示),相对时间是本地化文案 —— 后者必须比对资源。
        Assert.AreEqual("Prod - 生产环境", vm.Sidebar.RecentConnections.Connections[0].DisplayName);
        Assert.AreEqual(Strings.Format("Svc_HoursAgo", 2), vm.Sidebar.RecentConnections.Connections[0].RelativeTime);
        Assert.AreEqual("Dev", vm.Sidebar.RecentConnections.Connections[1].DisplayName);
        Assert.AreEqual(Strings.Format("Svc_DaysAgo", 3), vm.Sidebar.RecentConnections.Connections[1].RelativeTime);
    }

    [TestMethod]
    public async Task TryConnectRecentAsync_ReconstructsSftpType_WithoutCreatingTerminal()
    {
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService? sshConnectionService = Substitute.For<ISshConnectionService>();
        var entry = new RecentConnectionEntry
        {
            ConnectionType = ConnectionType.SFTP,
            Name = "Files",
            Host = "files.example.com",
            Port = 22,
            Username = "root",
        };

        var vm = new MainWindowViewModel(workflow, sshConnectionService, () => Substitute.For<ITerminalEmulator>());

        TerminalTabViewModel? tab = await vm.TryConnectRecentAsync(entry);

        Assert.IsNull(tab);
        Assert.IsEmpty(vm.TerminalTabs);
        await workflow.DidNotReceive().ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task OpenSftpForProfileAsync_ConnectsSshAndEnsuresExistingPanelVisible()
    {
        IConnectionWorkflowService? workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService? sshConnectionService = Substitute.For<ISshConnectionService>();
        ISshClientWrapper? sshClient = Substitute.For<ISshClientWrapper>();
        ISftpService? sftpService = Substitute.For<ISftpService>();
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
        sftpService.GetWorkingDirectoryAsync(session.SessionId, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult("/"));
        sftpService.ListDirectoryAsync(session.SessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult(new List<RemoteFileInfo>()));
        var vm = new MainWindowViewModel(
            workflow,
            sshConnectionService,
            sftpService: sftpService);

        TerminalTabViewModel? tab = await vm.OpenSftpForProfileAsync(profile);

        Assert.IsNull(tab);
        Assert.IsEmpty(vm.TerminalTabs);
        Assert.HasCount(1, vm.Layout.AllDocuments().OfType<SftpDocument>());
        await workflow.Received(1).ConnectProfileAsync(profile, Arg.Any<CancellationToken>());
        await sshClient.DidNotReceive().CreateShellStreamAsync(
            Arg.Any<string>(),
            Arg.Any<uint>(),
            Arg.Any<uint>(),
            Arg.Any<uint>(),
            Arg.Any<uint>(),
            Arg.Any<int>(),
            Arg.Any<IReadOnlyDictionary<TerminalMode, uint>?>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public void StatusBar_FollowsActiveTab_ConnectionInfo()
    {
        var vm = new MainWindowViewModel();
        var tabA = new TerminalTabViewModel(Substitute.For<ITerminalEmulator>(), Substitute.For<IShellStreamWrapper>())
        {
            Title = "A",
            ConnectionStatus = SessionStatus.Connected,
            ConnectionSummary = "SSH • a@host-a:22"
        };
        var tabB = new TerminalTabViewModel(Substitute.For<ITerminalEmulator>(), Substitute.For<IShellStreamWrapper>())
        {
            Title = "B",
            ConnectionStatus = SessionStatus.Connected,
            ConnectionSummary = "SSH • b@host-b:22"
        };
        vm.Layout.AddDocument(new TerminalDocument(tabA));
        vm.Layout.AddDocument(new TerminalDocument(tabB)); // B becomes active
        Assert.AreEqual("SSH • b@host-b:22", vm.StatusBar.ConnectionInfo);
        vm.Activate(tabA); // switch back to A
        Assert.AreEqual("SSH • a@host-a:22", vm.StatusBar.ConnectionInfo);
    }
}

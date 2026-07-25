using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Services;
using VelaShell.Terminal;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

[TestClass]
public class TerminalTabViewModelTests
{
    private readonly IShellStreamWrapper _shellStream;
    private readonly ITerminalEmulator _terminalEmulator;
    private readonly TerminalTabViewModel _vm;

    public TerminalTabViewModelTests()
    {
        _terminalEmulator = Substitute.For<ITerminalEmulator>();
        _shellStream = Substitute.For<IShellStreamWrapper>();
        _vm = new(_terminalEmulator, _shellStream);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void Constructor_SetsDefaultTitle() => Assert.IsFalse(string.IsNullOrEmpty(_vm.Title));

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void Constructor_SetsDefaultConnectionStatus_ToDisconnected()
    {
        Assert.AreEqual(SessionStatus.Disconnected, _vm.ConnectionStatus);
        Assert.IsFalse(_vm.IsConnected);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void ConnectionStatus_Connected_SetsIsConnectedTrue()
    {
        _vm.ConnectionStatus = SessionStatus.Connected;
        Assert.IsTrue(_vm.IsConnected);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void ConnectionStatus_Disconnected_SetsIsConnectedFalse()
    {
        _vm.ConnectionStatus = SessionStatus.Connected;
        _vm.ConnectionStatus = SessionStatus.Disconnected;
        Assert.IsFalse(_vm.IsConnected);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void TrySendCommandText_Connected_SendsBodyWithoutTrailingNewline()
    {
        _vm.ConnectionStatus = SessionStatus.Connected;

        bool sent = _vm.TrySendCommandText(" echo hello\r\n");

        Assert.IsTrue(sent);
        // 只发正文不带回车:快捷命令可能是待补全的模板,由用户自行按 Enter 执行。
        _terminalEmulator
            .Received(1)
            .WriteInput(
                Arg.Is<byte[]>(bytes =>
                    bytes.SequenceEqual(System.Text.Encoding.UTF8.GetBytes(" echo hello"))
                )
            );
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void TrySendCommandText_Disconnected_DoesNotSend()
    {
        bool sent = _vm.TrySendCommandText("uptime");

        Assert.IsFalse(sent);
        _terminalEmulator.DidNotReceive().WriteInput(Arg.Any<byte[]>());
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void IncrementReconnectAttempt_IncrementsCounter()
    {
        Assert.AreEqual(0, _vm.ReconnectAttempts);
        _vm.IncrementReconnectAttempt();
        Assert.AreEqual(1, _vm.ReconnectAttempts);
        _vm.IncrementReconnectAttempt();
        Assert.AreEqual(2, _vm.ReconnectAttempts);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void ResetReconnectAttempts_ResetsToZero()
    {
        _vm.IncrementReconnectAttempt();
        _vm.IncrementReconnectAttempt();
        Assert.AreEqual(2, _vm.ReconnectAttempts);
        _vm.ResetReconnectAttempts();
        Assert.AreEqual(0, _vm.ReconnectAttempts);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void CanReconnect_UnderMax_ReturnsTrue()
    {
        Assert.IsTrue(_vm.CanReconnect);
        _vm.IncrementReconnectAttempt();
        Assert.IsTrue(_vm.CanReconnect);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void CanReconnect_AtMax_ReturnsFalse()
    {
        for (int i = 0; i < _vm.MaxReconnectAttempts; i++)
        {
            _vm.IncrementReconnectAttempt();
        }
        Assert.IsFalse(_vm.CanReconnect);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void Dispose_DisposesBridgeAndTerminalEmulator()
    {
        _vm.Dispose();
        _terminalEmulator.Received(1).Dispose();
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void Dispose_CalledTwice_OnlyDisposesOnce()
    {
        _vm.Dispose();
        _vm.Dispose();
        _terminalEmulator.Received(1).Dispose();
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void Constructor_InitializesAllCommands()
    {
        Assert.IsNotNull(_vm.DisconnectCommand);
        Assert.IsNotNull(_vm.ReconnectCommand);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void Constructor_StoresTerminalEmulatorAndShellStream()
    {
        Assert.AreSame(_terminalEmulator, _vm.TerminalEmulator);
        Assert.AreSame(_shellStream, _vm.ShellStream);
        Assert.IsNotNull(_vm.Bridge);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void Id_IsUniquePerInstance()
    {
        var vm2 = new TerminalTabViewModel(
            Substitute.For<ITerminalEmulator>(),
            Substitute.For<IShellStreamWrapper>()
        );
        Assert.AreNotEqual(vm2.Id, _vm.Id);
        Assert.AreNotEqual(Guid.Empty, _vm.Id);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void ConnectingConstructor_HasNoTransportYet()
    {
        var vm = new TerminalTabViewModel(_terminalEmulator);
        Assert.IsNull(vm.Bridge);
        Assert.IsNull(vm.ShellStream);
        Assert.AreEqual(SessionStatus.Disconnected, vm.ConnectionStatus);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void Start_WithoutTransport_IsNoOp()
    {
        var vm = new TerminalTabViewModel(_terminalEmulator);
        vm.Start(); // must not throw with no transport attached
        Assert.IsNull(vm.Bridge);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void AttachTransport_WiresBridgeAndShellStream()
    {
        var vm = new TerminalTabViewModel(_terminalEmulator);
        IShellStreamWrapper? stream = Substitute.For<IShellStreamWrapper>();
        vm.AttachTransport(stream);
        Assert.IsNotNull(vm.Bridge);
        Assert.AreSame(stream, vm.ShellStream);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void AttachTransport_Null_Throws()
    {
        var vm = new TerminalTabViewModel(_terminalEmulator);
        Assert.ThrowsExactly<ArgumentNullException>(() => vm.AttachTransport(null!));
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void AttachTransport_AfterDisposed_Throws()
    {
        var vm = new TerminalTabViewModel(_terminalEmulator);
        vm.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            vm.AttachTransport(Substitute.For<IShellStreamWrapper>())
        );
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void DetachTransport_ClearsBridgeAndShellStream()
    {
        _vm.DetachTransport();
        Assert.IsNull(_vm.Bridge);
        Assert.IsNull(_vm.ShellStream);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void MarkDisconnected_SetsStatus_AndRaisesEvent()
    {
        _vm.ConnectionStatus = SessionStatus.Connected;
        bool raised = false;
        _vm.Disconnected += (_, _) => raised = true;
        _vm.MarkDisconnected();
        Assert.AreEqual(SessionStatus.Disconnected, _vm.ConnectionStatus);
        Assert.IsFalse(_vm.IsConnected);
        Assert.IsTrue(raised);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void MarkDisconnected_WhenAlreadyDisconnected_DoesNotRaise()
    {
        bool raised = false;
        _vm.Disconnected += (_, _) => raised = true;
        _vm.MarkDisconnected(); // already Disconnected from construction
        Assert.IsFalse(raised);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void RequestReconnect_OnlyFiresWhenDisconnected()
    {
        int count = 0;
        _vm.ReconnectRequested += (_, _) => count++;
        _vm.ConnectionStatus = SessionStatus.Connected;
        _vm.RequestReconnect(); // must be ignored while connected
        Assert.AreEqual(0, count);
        _vm.ConnectionStatus = SessionStatus.Disconnected;
        _vm.RequestReconnect();
        Assert.AreEqual(1, count);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void RequestClose_RaisesCloseRequested()
    {
        int count = 0;
        _vm.CloseRequested += (_, _) => count++;
        _vm.RequestClose();
        Assert.AreEqual(1, count);
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void DisconnectOverlayDetail_PrefersProfileName_OverHostPort()
    {
        _vm.Profile = new()
        {
            Name = "生产数据库",
            Host = "192.168.16.123",
            Port = 22
        };
        _vm.ConnectionStatus = SessionStatus.Disconnected;
        Assert.IsTrue(_vm.DisconnectOverlayDetail.Contains("生产数据库", StringComparison.Ordinal));
        Assert.IsFalse(_vm.DisconnectOverlayDetail.Contains("192.168.16.123", StringComparison.Ordinal));
        // 设计稿注释前缀曾漏进用户可见文案,回归守卫。
        Assert.IsFalse(_vm.DisconnectOverlayDetail.StartsWith("//", StringComparison.Ordinal));
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void DisconnectOverlayDetail_UnnamedProfile_FallsBackToHostPort()
    {
        _vm.Profile = new() { Name = "", Host = "10.0.0.5", Port = 2222 };
        _vm.ConnectionStatus = SessionStatus.Disconnected;
        Assert.IsTrue(_vm.DisconnectOverlayDetail.Contains("10.0.0.5:2222", StringComparison.Ordinal));
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void DisconnectOverlayDetail_LocalShell_DoesNotClaimSshConnection()
    {
        _vm.LocalShell = new("pwsh", "PowerShell", "pwsh.exe");
        _vm.ConnectionStatus = SessionStatus.Disconnected;
        Assert.IsTrue(_vm.DisconnectOverlayDetail.Contains("PowerShell", StringComparison.Ordinal));
        Assert.IsFalse(
            _vm.DisconnectOverlayDetail.Contains("SSH", StringComparison.OrdinalIgnoreCase)
        );
    }

    [TestMethod]
    [TestCategory("TerminalTab")]
    public void ShowDisconnectedOverlay_TrueForExitedLocalShell()
    {
        _vm.LocalShell = new("cmd", "命令提示符", "cmd.exe");
        _vm.ConnectionStatus = SessionStatus.Connected;
        Assert.IsFalse(_vm.ShowDisconnectedOverlay);
        _vm.ConnectionStatus = SessionStatus.Disconnected;
        Assert.IsTrue(_vm.ShowDisconnectedOverlay);
    }
}

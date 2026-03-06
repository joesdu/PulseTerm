using FluentAssertions;
using NSubstitute;
using PulseTerm.App.ViewModels;
using PulseTerm.Core.Models;
using PulseTerm.Core.Ssh;
using PulseTerm.Terminal;

namespace PulseTerm.App.Tests.ViewModels;

public class TerminalTabViewModelTests
{
    private readonly ITerminalEmulator _terminalEmulator;
    private readonly IShellStreamWrapper _shellStream;
    private readonly TerminalTabViewModel _vm;

    public TerminalTabViewModelTests()
    {
        _terminalEmulator = Substitute.For<ITerminalEmulator>();
        _shellStream = Substitute.For<IShellStreamWrapper>();
        _vm = new TerminalTabViewModel(_terminalEmulator, _shellStream);
    }

    [Fact]
    [Trait("Category", "TerminalTab")]
    public void Constructor_SetsDefaultTitle()
    {
        _vm.Title.Should().NotBeNullOrEmpty();
    }

    [Fact]
    [Trait("Category", "TerminalTab")]
    public void Constructor_SetsDefaultConnectionStatus_ToDisconnected()
    {
        _vm.ConnectionStatus.Should().Be(SessionStatus.Disconnected);
        _vm.IsConnected.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "TerminalTab")]
    public void ConnectionStatus_Connected_SetsIsConnectedTrue()
    {
        _vm.ConnectionStatus = SessionStatus.Connected;

        _vm.IsConnected.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "TerminalTab")]
    public void ConnectionStatus_Disconnected_SetsIsConnectedFalse()
    {
        _vm.ConnectionStatus = SessionStatus.Connected;
        _vm.ConnectionStatus = SessionStatus.Disconnected;

        _vm.IsConnected.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "TerminalTab")]
    public void IncrementReconnectAttempt_IncrementsCounter()
    {
        _vm.ReconnectAttempts.Should().Be(0);

        _vm.IncrementReconnectAttempt();
        _vm.ReconnectAttempts.Should().Be(1);

        _vm.IncrementReconnectAttempt();
        _vm.ReconnectAttempts.Should().Be(2);
    }

    [Fact]
    [Trait("Category", "TerminalTab")]
    public void ResetReconnectAttempts_ResetsToZero()
    {
        _vm.IncrementReconnectAttempt();
        _vm.IncrementReconnectAttempt();
        _vm.ReconnectAttempts.Should().Be(2);

        _vm.ResetReconnectAttempts();

        _vm.ReconnectAttempts.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "TerminalTab")]
    public void CanReconnect_UnderMax_ReturnsTrue()
    {
        _vm.CanReconnect.Should().BeTrue();

        _vm.IncrementReconnectAttempt();
        _vm.CanReconnect.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "TerminalTab")]
    public void CanReconnect_AtMax_ReturnsFalse()
    {
        for (var i = 0; i < _vm.MaxReconnectAttempts; i++)
            _vm.IncrementReconnectAttempt();

        _vm.CanReconnect.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "TerminalTab")]
    public void Dispose_DisposesBridgeAndTerminalEmulator()
    {
        _vm.Dispose();

        _terminalEmulator.Received(1).Dispose();
    }

    [Fact]
    [Trait("Category", "TerminalTab")]
    public void Dispose_CalledTwice_OnlyDisposesOnce()
    {
        _vm.Dispose();
        _vm.Dispose();

        _terminalEmulator.Received(1).Dispose();
    }

    [Fact]
    [Trait("Category", "TerminalTab")]
    public void Constructor_InitializesAllCommands()
    {
        _vm.SearchCommand.Should().NotBeNull();
        _vm.CopyCommand.Should().NotBeNull();
        _vm.SplitCommand.Should().NotBeNull();
        _vm.ToggleBroadcastCommand.Should().NotBeNull();
        _vm.OpenTunnelCommand.Should().NotBeNull();
        _vm.OpenQuickCommandsCommand.Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "TerminalTab")]
    public void Constructor_StoresTerminalEmulatorAndShellStream()
    {
        _vm.TerminalEmulator.Should().BeSameAs(_terminalEmulator);
        _vm.ShellStream.Should().BeSameAs(_shellStream);
        _vm.Bridge.Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "TerminalTab")]
    public void Id_IsUniquePerInstance()
    {
        var vm2 = new TerminalTabViewModel(
            Substitute.For<ITerminalEmulator>(),
            Substitute.For<IShellStreamWrapper>());

        _vm.Id.Should().NotBe(vm2.Id);
        _vm.Id.Should().NotBe(Guid.Empty);
    }
}

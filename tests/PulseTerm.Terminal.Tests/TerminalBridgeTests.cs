using System.Text;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PulseTerm.Core.Ssh;
using Xunit;

namespace PulseTerm.Terminal.Tests;

[Trait("Category", "TerminalBridge")]
public class TerminalBridgeTests
{
    private readonly ITerminalEmulator _terminal;
    private readonly IShellStreamWrapper _shellStream;

    public TerminalBridgeTests()
    {
        _terminal = Substitute.For<ITerminalEmulator>();
        _shellStream = Substitute.For<IShellStreamWrapper>();
    }

    [Fact]
    public void Constructor_NullTerminal_ThrowsArgumentNullException()
    {
        var act = () => new SshTerminalBridge(null!, _shellStream);
        act.Should().Throw<ArgumentNullException>().WithParameterName("terminal");
    }

    [Fact]
    public void Constructor_NullShellStream_ThrowsArgumentNullException()
    {
        var act = () => new SshTerminalBridge(_terminal, null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("shellStream");
    }

    [Fact]
    public void Start_CalledTwice_ThrowsInvalidOperationException()
    {
        _shellStream.CanRead.Returns(false);

        using var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.Start();

        var act = () => bridge.Start();
        act.Should().Throw<InvalidOperationException>().WithMessage("*already started*");
    }

    [Fact]
    public void UserInput_WritesToShellStream()
    {
        _shellStream.CanRead.Returns(false);
        _shellStream.CanWrite.Returns(true);
        _shellStream.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        using var bridge = new SshTerminalBridge(_terminal, _shellStream);

        var testData = Encoding.UTF8.GetBytes("hello");

        _terminal.UserInput += Raise.Event<Action<byte[]>>(testData);

        // WriteUserInputAsync is fire-and-forget, need to let it complete
        Thread.Sleep(100);

        _shellStream.Received().WriteAsync(
            testData,
            0,
            testData.Length,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void UserInput_WhenDisposed_DoesNotWriteToShellStream()
    {
        _shellStream.CanRead.Returns(false);
        _shellStream.CanWrite.Returns(true);

        var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.Dispose();

        _shellStream.DidNotReceive().WriteAsync(
            Arg.Any<byte[]>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void UserInput_WhenStreamCannotWrite_DoesNotWrite()
    {
        _shellStream.CanRead.Returns(false);
        _shellStream.CanWrite.Returns(false);

        using var bridge = new SshTerminalBridge(_terminal, _shellStream);

        var testData = Encoding.UTF8.GetBytes("hello");
        _terminal.UserInput += Raise.Event<Action<byte[]>>(testData);

        Thread.Sleep(100);

        _shellStream.DidNotReceive().WriteAsync(
            Arg.Any<byte[]>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ReadLoop_WhenCanReadFalse_ExitsImmediately()
    {
        _shellStream.CanRead.Returns(false);

        using var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.Start();

        // Task.Run in Start() needs time to enter and exit the loop
        Thread.Sleep(200);

        _shellStream.DidNotReceive().ReadAsync(
            Arg.Any<byte[]>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ReadLoop_WhenReadReturnsZero_ExitsGracefully()
    {
        _shellStream.CanRead.Returns(true);
        _shellStream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0));

        using var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.Start();

        Thread.Sleep(200);

        _terminal.DidNotReceive().Feed(Arg.Any<byte[]>());
    }

    [Fact]
    public void ReadLoop_WhenExceptionOccurs_FiresErrorEvent()
    {
        var expectedException = new IOException("connection lost");
        Exception? capturedError = null;

        _shellStream.CanRead.Returns(true);
        _shellStream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(expectedException);

        using var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.Error += ex => capturedError = ex;
        bridge.Start();

        Thread.Sleep(500);

        capturedError.Should().NotBeNull();
        capturedError.Should().BeSameAs(expectedException);
    }

    [Fact]
    public void Dispose_CancelsReadLoopAndDisposesStream()
    {
        _shellStream.CanRead.Returns(true);

        // ReadAsync blocks forever until CancellationToken fires
        _shellStream.ReadAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var ct = callInfo.ArgAt<CancellationToken>(3);
                await Task.Delay(Timeout.Infinite, ct);
                return 0;
            });

        var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.Start();

        Thread.Sleep(100);

        bridge.Dispose();

        _shellStream.Received().Dispose();
    }

    [Fact]
    public void Dispose_CalledMultipleTimes_DoesNotThrow()
    {
        _shellStream.CanRead.Returns(false);

        var bridge = new SshTerminalBridge(_terminal, _shellStream);

        var act = () =>
        {
            bridge.Dispose();
            bridge.Dispose();
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void UserInput_WriteThrowsObjectDisposed_DoesNotPropagate()
    {
        _shellStream.CanRead.Returns(false);
        _shellStream.CanWrite.Returns(true);
        _shellStream.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ObjectDisposedException("stream"));

        Exception? capturedError = null;
        using var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.Error += ex => capturedError = ex;

        var testData = Encoding.UTF8.GetBytes("hello");
        _terminal.UserInput += Raise.Event<Action<byte[]>>(testData);

        Thread.Sleep(200);

        // ObjectDisposedException is swallowed per WriteUserInputAsync contract
        capturedError.Should().BeNull();
    }

    [Fact]
    public void UserInput_WriteThrowsGenericException_FiresErrorEvent()
    {
        var expectedException = new InvalidOperationException("write failed");
        _shellStream.CanRead.Returns(false);
        _shellStream.CanWrite.Returns(true);
        _shellStream.WriteAsync(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(expectedException);

        Exception? capturedError = null;
        using var bridge = new SshTerminalBridge(_terminal, _shellStream);
        bridge.Error += ex => capturedError = ex;

        var testData = Encoding.UTF8.GetBytes("hello");
        _terminal.UserInput += Raise.Event<Action<byte[]>>(testData);

        Thread.Sleep(200);

        capturedError.Should().NotBeNull();
        capturedError.Should().BeSameAs(expectedException);
    }
}

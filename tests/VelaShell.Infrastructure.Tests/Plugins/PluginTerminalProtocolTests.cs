using System.Text;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Plugins.Protocols;
using VelaShell.PluginSdk.Protocols;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// 插件**终端**协议接进宿主的那一层:注册表分辨终端/文件协议、连接请求的设置合并、
/// 以及适配成 <see cref="IShellStreamWrapper" /> 后的 EOF 归一与非阻塞关闭。
/// </summary>
[TestClass]
public sealed class PluginTerminalProtocolTests
{
    /// <summary>可编排的终端替身:读侧从队列出字节,队列空且已完成即 EOF。</summary>
    private sealed class FakeTerminal : IProtocolTerminal, IProtocolTerminalSession
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _available = new(0);
        private readonly Queue<byte[]> _inbound = new();

        public ProtocolConnectRequest? Request { get; private set; }

        public ProtocolTerminalOptions Options { get; private set; }

        public List<byte> Written { get; } = [];

        public (int Columns, int Rows)? LastResize { get; private set; }

        public bool Disposed { get; private set; }

        /// <summary>在 <see cref="DisposeAsync" /> 被调用前一直挂着的读:用于验证"关闭能唤醒读循环"。</summary>
        public bool BlockReads { get; init; }

        public Task<IProtocolTerminalSession> ConnectAsync(
            ProtocolConnectRequest request, ProtocolTerminalOptions options, CancellationToken cancellationToken = default)
        {
            Request = request;
            Options = options;
            return Task.FromResult<IProtocolTerminalSession>(this);
        }

        public void Push(string text)
        {
            _inbound.Enqueue(Encoding.ASCII.GetBytes(text));
            _available.Release();
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (BlockReads)
            {
                // 永远等下去,直到令牌被取消 —— 真实传输阻塞在套接字上时就是这个形状。
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
            byte[] chunk = _inbound.Dequeue();
            chunk.CopyTo(buffer.Span);
            return chunk.Length;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            Written.AddRange(data.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
        {
            LastResize = (columns, rows);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _released.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public Task Released => _released.Task;
    }

    private static ProtocolDescriptor Descriptor() => new()
    {
        Id = "test.telnet",
        DisplayName = "Telnet",
        DefaultPort = 23,
        Fields =
        [
            new() { Key = "enterMode", Label = "Enter", DefaultValue = "crlf" },
            new() { Key = "token", Label = "Token", IsSecret = true }
        ]
    };

    private static SessionProfile Profile() => new()
    {
        Name = "switch-01",
        Host = "10.0.0.9",
        Port = 23,
        ConnectionType = ConnectionType.Plugin,
        PluginProtocolId = "test.telnet",
        PluginSettings = new(StringComparer.Ordinal) { ["enterMode"] = "crnul" },
        PluginSecrets = new(StringComparer.Ordinal) { ["token"] = "s3cret" }
    };

    [TestMethod]
    public void Registry_KeepsTerminalAndFileProtocolsApart()
    {
        // 分派点只看注册表里挂的是什么:终端协议开终端标签,文件协议开文件面板。
        // 混了的表现是"点开 Telnet 会话弹出一个空的双栏浏览器"。
        var registry = new PluginProtocolRegistry();
        var terminal = new FakeTerminal();
        using IDisposable handle = registry.Register("test", Descriptor(), terminal);

        Assert.IsTrue(registry.TryGet("test.telnet", out PluginProtocolRegistration registration));
        Assert.AreSame(terminal, registration.Terminal);
        Assert.IsNull(registration.FileSystem, "终端协议不该凭空多出一个文件系统。");
    }

    [TestMethod]
    public async Task Connector_MergesDefaults_StoredSettingsAndSecrets_IntoTheRequest()
    {
        // 与文件协议同一份合并规则(默认值 → 用户设置 → 机密):两处各写一遍必然分叉,
        // 分叉的表现是"同一个字段在文件面板生效、在终端标签不生效"。
        var terminal = new FakeTerminal();
        var registration = new PluginProtocolRegistration("test", Descriptor(), FileSystem: null, terminal);

        using IShellStreamWrapper stream = await PluginProtocolTerminalConnector.OpenAsync(
            registration, Profile(), new("xterm-256color", 100, 40));

        Assert.IsNotNull(terminal.Request);
        Assert.AreEqual("10.0.0.9", terminal.Request.Host);
        Assert.AreEqual("switch-01", terminal.Request.DisplayName);
        Assert.AreEqual("crnul", terminal.Request.GetString("enterMode"), "用户填的值应覆盖字段默认值。");
        Assert.AreEqual("s3cret", terminal.Request.GetString("token"), "机密也要一并交给插件。");
        Assert.AreEqual(100, terminal.Options.Columns);
        Assert.AreEqual("xterm-256color", terminal.Options.TerminalType);
    }

    [TestMethod]
    public async Task Connector_TranslatesSdkAuthenticationException_IntoTheHostFamily()
    {
        // 宿主的重试/弹框逻辑认的是 Core 的异常族;漏翻的表现是"密码错了却当成网络故障"。
        var registration = new PluginProtocolRegistration("test", Descriptor(), FileSystem: null, new ThrowingTerminal());
        await Assert.ThrowsExactlyAsync<VelaShell.Core.Protocols.PluginProtocolAuthenticationException>(
            () => PluginProtocolTerminalConnector.OpenAsync(registration, Profile(), new("xterm", 80, 24)));
    }

    private sealed class ThrowingTerminal : IProtocolTerminal
    {
        public Task<IProtocolTerminalSession> ConnectAsync(
            ProtocolConnectRequest request, ProtocolTerminalOptions options, CancellationToken cancellationToken = default) =>
            throw new ProtocolAuthenticationException("bad password");
    }

    [TestMethod]
    public async Task Stream_ForwardsReadsWritesAndResize()
    {
        var terminal = new FakeTerminal();
        var registration = new PluginProtocolRegistration("test", Descriptor(), FileSystem: null, terminal);
        using IShellStreamWrapper stream = await PluginProtocolTerminalConnector.OpenAsync(
            registration, Profile(), new("xterm", 80, 24));

        terminal.Push("ok");
        byte[] buffer = new byte[8];
        int read = await stream.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None);
        Assert.AreEqual(2, read);
        Assert.AreEqual("ok", Encoding.ASCII.GetString(buffer, 0, read));

        await stream.WriteAsync(Encoding.ASCII.GetBytes("hi"), 0, 2, CancellationToken.None);
        Assert.AreSequenceEqual(Encoding.ASCII.GetBytes("hi"), [.. terminal.Written]);

        stream.Resize(132, 43);
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (terminal.LastResize is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.AreEqual((132, 43), terminal.LastResize, "窗口尺寸变化必须转达给插件(Telnet 要发 NAWS)。");
    }

    [TestMethod]
    public async Task Dispose_WakesABlockedRead_AndNeverBlocksTheCaller()
    {
        // 关标签走的是同步 Dispose。它若等插件关连接,一个卡住的传输就能冻死整个界面
        // (串口 Close() 在硬件流控下可以永久阻塞);因此:取消令牌唤醒读、关闭推后台。
        var terminal = new FakeTerminal { BlockReads = true };
        var registration = new PluginProtocolRegistration("test", Descriptor(), FileSystem: null, terminal);
        IShellStreamWrapper stream = await PluginProtocolTerminalConnector.OpenAsync(
            registration, Profile(), new("xterm", 80, 24));

        Task<int> pending = stream.ReadAsync(new byte[8], 0, 8, CancellationToken.None);
        Assert.IsFalse(pending.IsCompleted, "读应当挂着(替身模拟阻塞在传输上)。");

        stream.Dispose();
        int read = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, read, "被唤醒的读必须归一化为 EOF,而不是抛取消异常。");
        await terminal.Released.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(terminal.Disposed);
    }

    [TestMethod]
    public async Task Read_AfterThePluginThrows_ReportsEof()
    {
        // 插件的传输层什么异常都可能抛;对终端来说结论只有一个:这条会话结束了。
        var registration = new PluginProtocolRegistration("test", Descriptor(), FileSystem: null, new FaultingTerminal());
        using IShellStreamWrapper stream = await PluginProtocolTerminalConnector.OpenAsync(
            registration, Profile(), new("xterm", 80, 24));
        Assert.AreEqual(0, await stream.ReadAsync(new byte[8], 0, 8, CancellationToken.None));
    }

    private sealed class FaultingTerminal : IProtocolTerminal, IProtocolTerminalSession
    {
        public Task<IProtocolTerminalSession> ConnectAsync(
            ProtocolConnectRequest request, ProtocolTerminalOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult<IProtocolTerminalSession>(this);

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("transport exploded");

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

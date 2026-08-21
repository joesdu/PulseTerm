using System.Net.Sockets;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Protocols;

namespace VelaShell.TestPlugin.Terminal;

/// <summary>
/// 终端协议夹具:验证宿主这一侧的整条链 —— 清单发现(**不装载程序集**就能画出页签)
/// → 用户点到页签触发 <c>onProtocol</c> 惰性激活 → 注册成**终端**协议 → 宿主适配成
/// <c>IShellStreamWrapper</c> → 在真实套接字上收发字节。
/// <para>
/// 单测宿主与真实应用之间最容易断的就是这一段:插件自己的单测全绿,但清单少一个字段、
/// 协议 id 大小写不一致、或注册走了**文件**协议那条重载,用户看到的就是"页签在,
/// 点了没反应"(或者更糟:点开一个空的双栏文件浏览器)。这个夹具专门盯它。
/// </para>
/// <para>
/// 协议本身刻意做成裸 TCP 直通,只在连上后先发一个固定问候序列 —— 那三个字节是给
/// 用例断言用的"确实是本插件在说话"的记号,不代表任何真实协议。具体协议的状态机
/// (Telnet 的选项协商之类)由插件自己的单测负责,那些测试在工具链仓库里。
/// </para>
/// </summary>
[VelaPlugin]
public sealed class TestTerminalPlugin : IVelaPlugin
{
    /// <summary>本夹具的插件 id / 协议 id(两者相同),与 <c>plugin.json</c> 一致。</summary>
    public const string Id = "velashell.test-terminal";

    /// <summary>入口程序集文件名。</summary>
    public const string EntryFileName = "VelaShell.TestPlugin.Terminal.dll";

    /// <summary>连接建立后插件立刻发给对端的问候序列(用例据此确认是本插件在收发)。</summary>
    public static ReadOnlySpan<byte> Greeting => [0xF0, 0x9F, 0x94];

    /// <summary>连接表单里那个自定义字段的键,用来验证描述符的 Fields 传得到宿主。</summary>
    public const string GreetingModeField = "greetingMode";

    private IDisposable? _registration;

    /// <inheritdoc />
    public Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _registration = context.Protocols.Register(BuildDescriptor(context), new TestTerminal(context));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeactivateAsync(CancellationToken cancellationToken)
    {
        _registration?.Dispose();
        _registration = null;
        return Task.CompletedTask;
    }

    private static ProtocolDescriptor BuildDescriptor(IPluginContext context) => new()
    {
        Id = context.PluginId,
        DisplayName = "Test Terminal",
        DefaultPort = 2323,
        // NoCredentials:用例断言宿主据此收起用户名/口令两栏。
        Features = ProtocolFeatures.AnonymousAccess | ProtocolFeatures.NoCredentials,
        Fields =
        [
            new()
            {
                Key = GreetingModeField,
                Label = "Greeting",
                Kind = ProtocolSettingKind.Choice,
                DefaultValue = "on",
                Choices = [new("on", "Send greeting"), new("off", "Stay quiet")]
            }
        ]
    };
}

/// <summary>裸 TCP 直通的 <see cref="IProtocolTerminal" /> 实现。</summary>
/// <param name="context">插件上下文(取日志)。</param>
internal sealed class TestTerminal(IPluginContext context) : IProtocolTerminal
{
    /// <inheritdoc />
    public async Task<IProtocolTerminalSession> ConnectAsync(
        ProtocolConnectRequest request,
        ProtocolTerminalOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Host))
        {
            throw new ProtocolConnectionException("Test terminal requires a host name or IP address.");
        }
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(request.Host.Trim(), request.Port, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            client.Dispose();
            throw new ProtocolConnectionException($"Test terminal could not connect: {ex.Message}", ex);
        }
        NetworkStream stream = client.GetStream();
        if (request.GetString(TestTerminalPlugin.GreetingModeField, "on") == "on")
        {
            await stream.WriteAsync(TestTerminalPlugin.Greeting.ToArray(), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        context.Log.Info($"Test terminal connected to {request.Host}:{request.Port}.");
        return new TestTerminalSession(client, stream);
    }
}

/// <summary>字节双工会话。断线归一化成 EOF(返回 0),不抛异常 —— 宿主据此走"可重连"那条路。</summary>
/// <param name="client">底层连接。</param>
/// <param name="stream">底层流。</param>
internal sealed class TestTerminalSession(TcpClient client, NetworkStream stream) : IProtocolTerminalSession
{
    /// <summary>宿主最后一次转达的终端尺寸。用例据此确认 Resize 确实传到了插件。</summary>
    public (int Columns, int Rows) LastResize { get; private set; }

    /// <inheritdoc />
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            return await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            return 0;
        }
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        stream.WriteAsync(data, cancellationToken);

    /// <inheritdoc />
    public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        // 本协议没有尺寸上报机制,记下来即可 —— 但**绝不能抛**:
        // 抛了的话用户每拉一次窗口都会在日志里刷一条。
        LastResize = (columns, rows);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        stream.Dispose();
        client.Dispose();
        return ValueTask.CompletedTask;
    }
}

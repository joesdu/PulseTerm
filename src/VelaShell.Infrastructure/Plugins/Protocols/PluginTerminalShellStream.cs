using System.Diagnostics;
using System.Text;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.PluginSdk.Protocols;

namespace VelaShell.Infrastructure.Plugins.Protocols;

/// <summary>
/// 插件终端协议的宿主适配器:把插件的 <see cref="IProtocolTerminalSession" /> 翻成
/// <see cref="IShellStreamWrapper" />,于是 Telnet / 串口这类协议**零改动**接进
/// 桥 → VT 引擎 → 自绘控件 那条既有管线,连同回滚、搜索、会话日志、会话录制与 ZMODEM。
/// <para>
/// 这一层只做三件插件不该各写一遍的事:
/// </para>
/// <list type="number">
///   <item>**异常一律归一化成 EOF**:掉线不是崩溃 —— 返回 0 才会走到"标签置为已断开、
///     可按 Enter 重连"那条路上,抛出去只会让读循环带着异常收尾。</item>
///   <item>**Dispose 绝不阻塞**:先取消读令牌唤醒读循环,再把插件的 DisposeAsync 丢到后台。
///     同步等待插件关连接是 UI 线程死锁的经典配方(串口 <c>Close()</c> 在硬件流控卡住时
///     可以永久阻塞,dotnet/runtime#20362)。</item>
///   <item>**Resize 即发即忘**:窗口缩放每帧都可能来一次,不能让它变成一次可等待的往返。</item>
/// </list>
/// </summary>
/// <param name="session">插件建立的终端会话。</param>
/// <param name="protocolId">协议 id(仅用于日志)。</param>
internal sealed class PluginTerminalShellStream(IProtocolTerminalSession session, string protocolId) : IShellStreamWrapper
{
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _linked;
    private CancellationToken _linkedFor;
    private volatile bool _disposed;

    /// <summary>桥不轮询数据可用性(读循环阻塞在 <see cref="ReadAsync" /> 上),故恒为 <c>false</c>。</summary>
    public bool DataAvailable => false;

    /// <summary>流是否可读。</summary>
    public bool CanRead => !_disposed;

    /// <summary>流是否可写。</summary>
    public bool CanWrite => !_disposed;

    /// <summary>插件协议没有登录握手可供匹配,恒返回 <see langword="null" />。</summary>
    public string? Expect(string regex, TimeSpan timeout) => null;

    /// <summary>写入一行文本(以回车结尾);换行语义由协议自己决定(Telnet 会按需改写成 CR LF)。</summary>
    public void WriteLine(string line)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\r");
        WriteAsync(bytes, 0, bytes.Length, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return 0;
        }
        try
        {
            return await session.ReadAsync(buffer.AsMemory(offset, count), LinkedToken(cancellationToken))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 归一化为 EOF。插件抛什么都可能(它自己写的传输层),但对终端来说结论只有一个:
            // 这条会话结束了。
            if (ex is not OperationCanceledException)
            {
                Trace.WriteLine($"[PluginTerminal] Read on '{protocolId}' failed: {ex.Message}");
            }
            return 0;
        }
    }

    /// <inheritdoc />
    public async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }
        try
        {
            await session.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 会话已断:丢弃输入,等读循环那侧收到 EOF 去改标签状态。
            Trace.WriteLine($"[PluginTerminal] Write on '{protocolId}' failed: {ex.Message}");
        }
    }

    /// <summary>无缓冲可刷(写在 <see cref="WriteAsync" /> 里就已交给插件)。</summary>
    public void Flush()
    {
    }

    /// <inheritdoc />
    public void Resize(int columns, int rows)
    {
        if (_disposed)
        {
            return;
        }
        // 即发即忘:窗口缩放不该等一次网络往返(Telnet 的 NAWS 是要真发出去的)。
        _ = Task.Run(async () =>
        {
            try
            {
                await session.ResizeAsync(columns, rows, _lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Trace.WriteLine($"[PluginTerminal] Resize on '{protocolId}' failed: {ex.Message}");
            }
        });
    }

    /// <summary>拆掉会话:取消读令牌唤醒读循环,插件那侧的关闭推到后台完成(绝不在此阻塞)。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 并发 Dispose:忽略。
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[PluginTerminal] Disposing '{protocolId}' threw: {ex.Message}");
            }
            finally
            {
                _linked?.Dispose();
                _lifetime.Dispose();
            }
        });
    }

    /// <summary>
    /// 把桥的读令牌与本流的生命周期令牌合成一个。桥整条读循环复用同一个令牌,
    /// 因此这里按令牌缓存,避免每读一块就 new 一个 CTS(高吞吐输出下是每秒上千次分配)。
    /// </summary>
    private CancellationToken LinkedToken(CancellationToken token)
    {
        if (!token.CanBeCanceled)
        {
            return _lifetime.Token;
        }
        if (_linked is { } cached && _linkedFor == token)
        {
            return cached.Token;
        }
        _linked?.Dispose();
        _linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        _linkedFor = token;
        return _linked.Token;
    }
}

/// <summary>
/// 打开一条插件终端协议会话的入口:构造连接请求(合并协议字段默认值、用户填的设置与机密)、
/// 调用插件、把 SDK 异常翻成宿主的中立异常族,并包成 <see cref="IShellStreamWrapper" />。
/// <para>
/// 做成静态函数而不是又一个注入服务:它没有任何状态 —— 会话的生命周期由返回的流持有,
/// 终端标签本来就管着它(与本地终端 ConPTY 那条路径同构)。
/// </para>
/// </summary>
public static class PluginProtocolTerminalConnector
{
    /// <summary>按会话配置打开一条终端会话。</summary>
    /// <param name="registration">已解析的协议注册(必须带终端实现)。</param>
    /// <param name="profile">会话配置。</param>
    /// <param name="options">终端初始参数(TERM 与初始行列)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>可直接交给 <c>TerminalTabViewModel.AttachTransport</c> 的传输流。</returns>
    /// <exception cref="ArgumentException">该协议没有注册终端实现。</exception>
    public static async Task<IShellStreamWrapper> OpenAsync(
        PluginProtocolRegistration registration,
        SessionProfile profile,
        ProtocolTerminalOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(profile);
        if (registration.Terminal is not { } terminal)
        {
            throw new ArgumentException(
                $"Protocol '{registration.Descriptor.Id}' does not provide a terminal implementation.",
                nameof(registration));
        }
        var request = new ProtocolConnectRequest
        {
            Host = profile.Host,
            Port = profile.Port,
            Username = profile.Username,
            Password = profile.Password ?? string.Empty,
            Settings = PluginProtocolFileService.BuildSettings(registration.Descriptor, profile),
            DisplayName = profile.Name
        };
        IProtocolTerminalSession session;
        try
        {
            session = await terminal.ConnectAsync(request, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw PluginProtocolFileService.Translate(ex, registration.Descriptor);
        }
        return new PluginTerminalShellStream(session, registration.Descriptor.Id);
    }
}

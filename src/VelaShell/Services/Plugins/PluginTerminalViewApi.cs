using System.Text;
using System.Threading.Channels;
using Avalonia.Threading;
using VelaShell.PluginSdk.TerminalView;
using VelaShell.Terminal.Emulation;
using VelaShell.Terminal.Rendering;
using VelaShell.ViewModels;

namespace VelaShell.Services.Plugins;

/// <summary>
/// 终端视图能力(<see cref="ITerminalViewApi" />)的进程内实现:
/// 每次 <c>Create</c> 交出一个独立的 <see cref="VelaTerminalControl" />,
/// 外观跟随宿主当前的终端设置。
/// <para>
/// 不按插件分实例 —— 它不持有任何每插件状态,交出去的控件归调用方自己管。
/// </para>
/// </summary>
internal sealed class PluginTerminalViewApi(Func<MainWindowViewModel?> mainViewModel) : ITerminalViewApi
{
    public bool IsAvailable => true;

    public IPluginTerminalView Create(TerminalViewOptions? options = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        TerminalViewOptions opts = options ?? new TerminalViewOptions();
        var control = new VelaTerminalControl
        {
            TerminalType = TerminalTypeExtensions.FromTermName(opts.TerminalType)
        };
        if (opts.FollowHostAppearance && mainViewModel() is { } viewModel)
        {
            viewModel.ApplyTerminalAppearanceToPluginView(control);
        }
        // 这两项在宿主外观之后套用:它们是**这一个**终端的语义,不是用户的偏好。
        control.ScrollbackLines = Math.Max(0, opts.ScrollbackLines);
        control.LocalEchoEnabled = opts.LocalEcho;
        control.PeerEchoesInput = !opts.LocalEcho;
        return new PluginTerminalView(control);
    }
}

/// <summary>交给插件的那个终端视图。</summary>
internal sealed class PluginTerminalView(VelaTerminalControl control) : IPluginTerminalView
{
    private readonly Lock _gate = new();
    private CancellationTokenSource? _attachCts;
    private bool _disposed;

    public object Control => control;

    public int Columns => control.Columns;

    public int Rows => control.Rows;

    public void Feed(ReadOnlySpan<byte> data)
    {
        if (_disposed || data.IsEmpty)
        {
            return;
        }
        // 读循环在后台线程,渲染必须回 UI 线程。span 不能跨越 await/Post,先拷一份。
        byte[] copy = data.ToArray();
        if (Dispatcher.UIThread.CheckAccess())
        {
            control.Feed(copy);
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                control.Feed(copy);
            }
        });
    }

    public void Write(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        Feed(Encoding.UTF8.GetBytes(text));
    }

    // 清屏 + 清回滚:CSI H(回原点)· CSI 2J(清屏)· CSI 3J(清回滚)。
    // 走喂字节这条路而不是伸手动内部状态 —— 与远端发来的清屏走完全同一条代码。
    public void Clear() => OnUi(() => control.Feed("\u001b[H\u001b[2J\u001b[3J"u8.ToArray()));

    public string GetText(int maxLines = 1000)
    {
        if (_disposed)
        {
            return "";
        }
        return Dispatcher.UIThread.Invoke(() =>
        {
            int total = control.TotalLines;
            if (total <= 0)
            {
                return "";
            }
            int take = Math.Clamp(maxLines, 1, total);
            var sb = new StringBuilder();
            for (int row = total - take; row < total; row++)
            {
                sb.Append(control.GetBufferLine(row).TrimEnd()).Append('\n');
            }
            return sb.ToString();
        });
    }

    public void Resize(int columns, int rows) =>
        OnUi(() => control.Resize(Math.Max(1, columns), Math.Max(1, rows)));

    public event Action<byte[]>? UserInput
    {
        add => control.UserInput += value;
        remove => control.UserInput -= value;
    }

    public event Action<int, int>? Resized
    {
        add => control.PtySizeChanged += value;
        remove => control.PtySizeChanged -= value;
    }

    /// <summary>
    /// 把这个视图接到一条双工流上。读在后台、渲染回 UI 线程、写回串行化 ——
    /// 三件容易做错的事在这里做一次,插件不必各做一遍。
    /// </summary>
    public async Task AttachAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? previous;
        lock (_gate)
        {
            previous = _attachCts;
            _attachCts = cts;
        }
        // 同一个视图同一时刻只接一条流:先把上一条断掉,否则两条流会交替往同一块屏幕上画。
        if (previous is not null)
        {
            await previous.CancelAsync().ConfigureAwait(false);
            previous.Dispose();
        }

        CancellationToken token = cts.Token;
        // 用户按键经一个无界通道串行化后再写回:并发写同一条流会把一次按键的多个字节劈开,
        // 而 UTF-8 与转义序列都经不起劈。
        var outbound = Channel.CreateUnbounded<byte[]>(new() { SingleReader = true });
        void OnUserInput(byte[] bytes) => outbound.Writer.TryWrite(bytes);
        control.UserInput += OnUserInput;
        try
        {
            Task writer = PumpOutboundAsync(stream, outbound, token);
            await PumpInboundAsync(stream, token).ConfigureAwait(false);
            outbound.Writer.TryComplete();
            await writer.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            control.UserInput -= OnUserInput;
            outbound.Writer.TryComplete();
            lock (_gate)
            {
                if (ReferenceEquals(_attachCts, cts))
                {
                    _attachCts = null;
                }
            }
            cts.Dispose();
        }
    }

    private async Task PumpInboundAsync(Stream stream, CancellationToken token)
    {
        byte[] buffer = new byte[16 * 1024];
        while (!token.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                // 远端把连接掐了。这是 exec 会话正常的结束方式之一,不是错误。
                return;
            }
            if (read <= 0)
            {
                return;
            }
            Feed(buffer.AsSpan(0, read));
        }
    }

    private static async Task PumpOutboundAsync(Stream stream, Channel<byte[]> outbound, CancellationToken token)
    {
        try
        {
            await foreach (byte[] bytes in outbound.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // 对面先走了;入站那一侧会读到 0 并收尾。
        }
    }

    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }
        Dispatcher.UIThread.Post(action);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _attachCts;
            _attachCts = null;
        }
        cts?.Cancel();
        cts?.Dispose();
        OnUi(control.Dispose);
    }
}

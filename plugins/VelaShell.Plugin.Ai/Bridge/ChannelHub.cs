using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Bridge;

/// <summary>
/// 渠道的起停与重连。四个平台的入站传输各不相同,但"断了要重来、重来要退避、
/// 连上了要把退避复位"这套策略只该有一份 —— 就在这里。
/// </summary>
public sealed class ChannelHub(IPluginContext context) : IAsyncDisposable
{
    /// <summary>重连退避的上限。再久用户就该怀疑是不是配错了,而不是等它自己好。</summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private sealed class Entry(IMessageChannel channel)
    {
        public IMessageChannel Channel { get; } = channel;
        public CancellationTokenSource Cts { get; } = new();
        public Task? Loop { get; set; }
        public ChannelStatus Status { get; set; } = new(channel.Id, ChannelState.Stopped, null, DateTimeOffset.UtcNow);
    }

    private readonly Dictionary<string, Entry> _entries = [];
    private readonly Lock _sync = new();

    /// <summary>任一渠道的状态变化(设置页据此刷状态灯)。</summary>
    public event Action<ChannelStatus>? StatusChanged;

    /// <summary>当前全部渠道的状态快照。</summary>
    public IReadOnlyList<ChannelStatus> Snapshot()
    {
        lock (_sync)
        {
            return [.. _entries.Values.Select(e => e.Status)];
        }
    }

    /// <summary>挂上一个渠道并开始跑(已存在同 id 则先停掉旧的)。</summary>
    public async Task StartAsync(IMessageChannel channel, Func<InboundMessage, Task> onMessage)
    {
        ArgumentNullException.ThrowIfNull(channel);
        await StopAsync(channel.Id).ConfigureAwait(false);
        var entry = new Entry(channel);
        lock (_sync)
        {
            _entries[channel.Id] = entry;
        }
        channel.Connected += () => SetStatus(entry, ChannelState.Connected, null);
        entry.Loop = RunWithRetryAsync(entry, onMessage);
    }

    /// <summary>停掉一个渠道并释放它。</summary>
    public async Task StopAsync(string channelId)
    {
        Entry? entry;
        lock (_sync)
        {
            if (!_entries.Remove(channelId, out entry))
            {
                return;
            }
        }
        await entry.Cts.CancelAsync().ConfigureAwait(false);
        if (entry.Loop is { } loop)
        {
            // 循环里已经把异常都吞掉了,这里只是等它收摊
            await loop.ConfigureAwait(false);
        }
        entry.Cts.Dispose();
        await entry.Channel.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>停掉全部渠道。</summary>
    public async Task StopAllAsync()
    {
        string[] ids;
        lock (_sync)
        {
            ids = [.. _entries.Keys];
        }
        foreach (string id in ids)
        {
            await StopAsync(id).ConfigureAwait(false);
        }
    }

    /// <summary>往某渠道发一条消息(渠道已停则静默丢弃 —— 掉线时不该把整轮拖崩)。</summary>
    public async Task<string?> SendAsync(string channelId, OutboundTarget target, string text,
        CancellationToken cancellationToken)
    {
        if (Find(channelId) is not { } channel)
        {
            return null;
        }
        try
        {
            return await channel.SendAsync(target, text, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Log.Warn($"Bridge: sending to {channel.Label} failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>改掉之前发出的一条消息(渠道不支持编辑或已停则什么都不做)。</summary>
    public async Task EditAsync(string channelId, OutboundTarget target, string messageId, string text,
        CancellationToken cancellationToken)
    {
        if (Find(channelId) is not { Capabilities.CanEdit: true } channel)
        {
            return;
        }
        try
        {
            await channel.EditAsync(target, messageId, text, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Log.Warn($"Bridge: editing a message on {channel.Label} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 把一个本机文件发进聊天。成功返回 null,失败返回<b>给模型看的那句话</b>。
    /// </summary>
    /// <remarks>
    /// 这里刻意不像 <see cref="SendAsync" /> 那样把失败咽掉。发文件是用户<b>明确要的</b>
    /// 一件事("把日志发我"),悄悄失败等于让他一直等一个永远不来的附件 ——
    /// 而普通回复掉一条,下一轮还能再说一遍。
    /// </remarks>
    public async Task<string?> SendFileAsync(string channelId, OutboundTarget target, string localPath,
        CancellationToken cancellationToken)
    {
        if (Find(channelId) is not { } channel)
        {
            return "This chat is not connected right now, so the file could not be sent.";
        }
        if (channel.Capabilities.MaxFileBytes <= 0)
        {
            return $"{channel.Label} cannot receive files from this bot.";
        }
        // 上限在发之前查:超了之后各家的表现不一(有的报错,有的静默丢),
        // 而"传了两分钟然后无事发生"是最难查的一种。
        var info = new FileInfo(localPath);
        if (info.Exists && info.Length > channel.Capabilities.MaxFileBytes)
        {
            long limit = channel.Capabilities.MaxFileBytes / (1024 * 1024);
            return $"That file is {info.Length / (1024 * 1024)} MB; {channel.Label} accepts at most {limit} MB. "
                   + "Split or compress it on the server first, then download and send the smaller file.";
        }
        try
        {
            await channel.SendFileAsync(target, localPath, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Log.Warn($"Bridge: sending a file to {channel.Label} failed: {ex.Message}");
            return $"The platform refused the upload: {ex.Message}";
        }
    }

    /// <summary>取某渠道的能力(找不到则返回默认值)。</summary>
    public ChannelCapabilities CapabilitiesOf(string channelId)
        => Find(channelId)?.Capabilities ?? new ChannelCapabilities(false);

    private IMessageChannel? Find(string channelId)
    {
        lock (_sync)
        {
            return _entries.TryGetValue(channelId, out Entry? entry) ? entry.Channel : null;
        }
    }

    private async Task RunWithRetryAsync(Entry entry, Func<InboundMessage, Task> onMessage)
    {
        TimeSpan backoff = TimeSpan.FromSeconds(1);
        CancellationToken token = entry.Cts.Token;
        while (!token.IsCancellationRequested)
        {
            SetStatus(entry, ChannelState.Connecting, null);
            try
            {
                // 连上的那一刻由渠道自己报(Connected 事件),这里只负责"跑到断为止"
                await entry.Channel.RunAsync(Guard(onMessage), token).ConfigureAwait(false);
                // 正常返回 = 对端把连接关了。这在长连接上是常事(平台会定期换端),
                // 所以不当故障:马上重来,退避也不涨。
                backoff = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                context.Log.Warn($"Bridge: {entry.Channel.Label} dropped ({ex.Message}); retrying in {backoff.TotalSeconds:0}s.");
                SetStatus(entry, ChannelState.Faulted, ex.Message);
            }
            if (token.IsCancellationRequested)
            {
                break;
            }
            if (!await ChannelShutdown.DelayAsync(backoff, token).ConfigureAwait(false))
            {
                break; // 退避期间被停掉
            }
            backoff = backoff >= MaxBackoff ? MaxBackoff : backoff * 2;
        }
        SetStatus(entry, ChannelState.Stopped, null);
    }

    /// <summary>
    /// 把消息回调裹一层。渠道实现里那条读循环不该因为"这条消息处理出错"就整个断掉 ——
    /// 断掉的代价是重连,而重连期间的消息平台不会补发。
    /// </summary>
    private Func<InboundMessage, Task> Guard(Func<InboundMessage, Task> onMessage)
        => async message =>
        {
            try
            {
                await onMessage(message).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                context.Log.Error($"Bridge: handling a message from {message.ChatKey} failed: {ex}");
            }
        };

    private void SetStatus(Entry entry, ChannelState state, string? detail)
    {
        var status = new ChannelStatus(entry.Channel.Id, state, detail, DateTimeOffset.UtcNow);
        entry.Status = status;
        StatusChanged?.Invoke(status);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAllAsync().ConfigureAwait(false);
}

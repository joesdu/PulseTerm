using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;

namespace VelaShell.Plugin.Redis.Ui;

/// <summary>底部抽屉的页签。</summary>
public enum RedisDrawerTab
{
    /// <summary>控制台。</summary>
    Console,

    /// <summary>服务器概览。</summary>
    Overview,

    /// <summary>慢日志。</summary>
    Slowlog,

    /// <summary>客户端连接。</summary>
    Clients,

    /// <summary>订阅。</summary>
    PubSub,

    /// <summary>内存抽样分析。</summary>
    Memory
}

/// <summary>订阅面板里的一条消息。</summary>
/// <param name="At">收到时间。</param>
/// <param name="Channel">频道。</param>
/// <param name="Payload">载荷。</param>
public sealed record RedisPubSubMessage(DateTimeOffset At, string Channel, string Payload)
{
    /// <summary>时间的显示形式。</summary>
    public string TimeText => At.ToString("HH:mm:ss.fff", CultureInfo.CurrentCulture);
}

/// <summary>
/// 底部抽屉:控制台 / 概览 / 慢日志 / 客户端 / 订阅 / 内存分析。
/// <para>
/// 全部按**空状态降级**处理:托管 Redis 普遍禁掉 <c>CONFIG</c>/<c>CLIENT</c>/<c>SLOWLOG</c>/<c>MEMORY</c>,
/// 拿不到就在那一页写一句"该服务器未开放 X",灰掉而不是弹一条红色的失败
/// —— 「没配过」与「不支持」是空状态,不是错误。
/// </para>
/// </summary>
public sealed partial class RedisWorkspaceViewModel
{
    /// <summary>抽屉是否展开。</summary>
    public bool IsDrawerOpen
    {
        get;
        set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(DrawerToggleLabel));
            if (field)
            {
                _ = RefreshActiveTabAsync();
            }
        }
    }

    /// <summary>展开/收起抽屉。</summary>
    public AsyncCommand ToggleDrawerCommand { get; private set; } = null!;

    /// <summary>抽屉开关按钮的文案(展开 / 收起)。</summary>
    public string DrawerToggleLabel => IsDrawerOpen ? Loc["Redis_Collapse"] : Loc["Redis_Expand"];

    /// <summary>概览有说明要显示(不可用 / 出错)。</summary>
    public bool HasOverviewNotice => OverviewNotice.Length > 0;

    /// <summary>慢日志有说明要显示。</summary>
    public bool HasSlowlogNotice => SlowlogNotice.Length > 0;

    /// <summary>客户端列表有说明要显示。</summary>
    public bool HasClientsNotice => ClientsNotice.Length > 0;

    /// <summary>当前页签。</summary>
    public RedisDrawerTab ActiveTab
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            foreach (string name in TabFlagNames)
            {
                RaisePropertyChanged(name);
            }
        }
    } = RedisDrawerTab.Console;

    private static readonly string[] TabFlagNames =
    [
        nameof(IsConsoleTab), nameof(IsOverviewTab), nameof(IsSlowlogTab),
        nameof(IsClientsTab), nameof(IsPubSubTab), nameof(IsMemoryTab)
    ];

    /// <summary>页签选中态(XAML 里绑它上样式,视图里不写逻辑)。</summary>
    public bool IsConsoleTab => ActiveTab == RedisDrawerTab.Console;

    /// <inheritdoc cref="IsConsoleTab" />
    public bool IsOverviewTab => ActiveTab == RedisDrawerTab.Overview;

    /// <inheritdoc cref="IsConsoleTab" />
    public bool IsSlowlogTab => ActiveTab == RedisDrawerTab.Slowlog;

    /// <inheritdoc cref="IsConsoleTab" />
    public bool IsClientsTab => ActiveTab == RedisDrawerTab.Clients;

    /// <inheritdoc cref="IsConsoleTab" />
    public bool IsPubSubTab => ActiveTab == RedisDrawerTab.PubSub;

    /// <inheritdoc cref="IsConsoleTab" />
    public bool IsMemoryTab => ActiveTab == RedisDrawerTab.Memory;

    /// <summary>切到控制台页签。</summary>
    public AsyncCommand ShowConsoleCommand { get; private set; } = null!;

    /// <summary>切到概览页签。</summary>
    public AsyncCommand ShowOverviewCommand { get; private set; } = null!;

    /// <summary>切到慢日志页签。</summary>
    public AsyncCommand ShowSlowlogCommand { get; private set; } = null!;

    /// <summary>切到客户端页签。</summary>
    public AsyncCommand ShowClientsCommand { get; private set; } = null!;

    /// <summary>切到订阅页签。</summary>
    public AsyncCommand ShowPubSubCommand { get; private set; } = null!;

    /// <summary>切到内存分析页签。</summary>
    public AsyncCommand ShowMemoryCommand { get; private set; } = null!;

    /// <summary>刷新当前页签。</summary>
    public AsyncCommand RefreshTabCommand { get; private set; } = null!;

    // ── 概览 ──────────────────────────────────────────────────────

    /// <summary>概览的分组指标。</summary>
    public ObservableCollection<RedisMetricGroup> Overview { get; } = [];

    /// <summary>概览不可用时的说明(服务器禁了 <c>INFO</c>)。</summary>
    public string OverviewNotice
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(HasOverviewNotice));
        }
    } = string.Empty;

    // ── 慢日志 ────────────────────────────────────────────────────

    /// <summary>慢日志条目。</summary>
    public ObservableCollection<RedisSlowlogEntry> Slowlog { get; } = [];

    /// <summary>慢日志不可用时的说明。</summary>
    public string SlowlogNotice
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(HasSlowlogNotice));
        }
    } = string.Empty;

    /// <summary>清空慢日志。</summary>
    public AsyncCommand ResetSlowlogCommand { get; private set; } = null!;

    // ── 客户端 ────────────────────────────────────────────────────

    /// <summary>客户端连接。</summary>
    public ObservableCollection<RedisClientEntry> Clients { get; } = [];

    /// <summary>客户端列表不可用时的说明。</summary>
    public string ClientsNotice
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(HasClientsNotice));
        }
    } = string.Empty;

    /// <summary>选中的客户端。</summary>
    public RedisClientEntry? SelectedClient
    {
        get;
        set
        {
            SetProperty(ref field, value);
            KillClientCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>断开选中的客户端。</summary>
    public AsyncCommand KillClientCommand { get; private set; } = null!;

    // ── 订阅 ──────────────────────────────────────────────────────

    /// <summary>已订阅的频道/模式。</summary>
    public ObservableCollection<string> Subscriptions { get; } = [];

    /// <summary>收到的消息(最新在前)。</summary>
    public ObservableCollection<RedisPubSubMessage> Messages { get; } = [];

    /// <summary>频道输入框。</summary>
    public string ChannelDraft
    {
        get;
        set
        {
            SetProperty(ref field, value);
            SubscribeCommand.RaiseCanExecuteChanged();
        }
    } = string.Empty;

    /// <summary>选中的订阅(用于退订)。</summary>
    public string? SelectedSubscription
    {
        get;
        set
        {
            SetProperty(ref field, value);
            UnsubscribeCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>订阅。</summary>
    public AsyncCommand SubscribeCommand { get; private set; } = null!;

    /// <summary>退订。</summary>
    public AsyncCommand UnsubscribeCommand { get; private set; } = null!;

    // ── 内存分析 ──────────────────────────────────────────────────

    /// <summary>按前缀聚合的占用。</summary>
    public ObservableCollection<RedisMemoryBucket> MemoryByPrefix { get; } = [];

    /// <summary>占用最大的键。</summary>
    public ObservableCollection<RedisMemoryBucket> MemoryTopKeys { get; } = [];

    /// <summary>
    /// 抽样说明。**必须写在页面上**:这些数字是抽样估计,不是全量审计 ——
    /// 把它藏进文档里等于默认用户会把它当成确定值。
    /// </summary>
    public string MemoryNotice
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>正在抽样。</summary>
    public bool IsSamplingMemory
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            SampleMemoryCommand.RaiseCanExecuteChanged();
            StopSamplingCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>开始抽样。</summary>
    public AsyncCommand SampleMemoryCommand { get; private set; } = null!;

    /// <summary>停止抽样。</summary>
    public AsyncCommand StopSamplingCommand { get; private set; } = null!;

    private CancellationTokenSource? _samplingCts;

    // ── 接线 ──────────────────────────────────────────────────────

    private void InitializeDrawer()
    {
        ShowConsoleCommand = new(() => SwitchTabAsync(RedisDrawerTab.Console));
        ShowOverviewCommand = new(() => SwitchTabAsync(RedisDrawerTab.Overview));
        ShowSlowlogCommand = new(() => SwitchTabAsync(RedisDrawerTab.Slowlog));
        ShowClientsCommand = new(() => SwitchTabAsync(RedisDrawerTab.Clients));
        ShowPubSubCommand = new(() => SwitchTabAsync(RedisDrawerTab.PubSub));
        ShowMemoryCommand = new(() => SwitchTabAsync(RedisDrawerTab.Memory));
        RefreshTabCommand = new(RefreshActiveTabAsync);
        ResetSlowlogCommand = new(ResetSlowlogAsync, () => CanWrite);
        KillClientCommand = new(KillClientAsync, () => SelectedClient is { IsSelf: false });
        SubscribeCommand = new(SubscribeAsync, () => ChannelDraft.Trim().Length > 0);
        UnsubscribeCommand = new(UnsubscribeAsync, () => SelectedSubscription is { Length: > 0 });
        SampleMemoryCommand = new(SampleMemoryAsync, () => !IsSamplingMemory);
        StopSamplingCommand = new(() =>
        {
            StopSampling();
            return Task.CompletedTask;
        }, () => IsSamplingMemory);
    }

    private async Task SwitchTabAsync(RedisDrawerTab tab)
    {
        ActiveTab = tab;
        IsDrawerOpen = true;
        await RefreshActiveTabAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 刷新当前页签。**只刷当前那一页** —— 后台把六页都轮一遍是在替用户敲不需要的命令,
    /// 对生产实例尤其不该。
    /// </summary>
    private async Task RefreshActiveTabAsync()
    {
        if (!IsDrawerOpen)
        {
            return;
        }
        switch (ActiveTab)
        {
            case RedisDrawerTab.Overview:
                await LoadOverviewAsync().ConfigureAwait(true);
                break;
            case RedisDrawerTab.Slowlog:
                await LoadSlowlogAsync().ConfigureAwait(true);
                break;
            case RedisDrawerTab.Clients:
                await LoadClientsAsync().ConfigureAwait(true);
                break;
            default:
                // 控制台 / 订阅 / 内存分析都是用户驱动的,没有"刷新"这回事。
                break;
        }
    }

    private async Task LoadOverviewAsync()
    {
        try
        {
            IReadOnlyList<RedisMetricGroup> groups = await _connection.ReadOverviewAsync().ConfigureAwait(true);
            Overview.Clear();
            OverviewNotice = string.Empty;
            foreach (RedisMetricGroup group in groups)
            {
                if (group.Unavailable)
                {
                    OverviewNotice = Loc.Format("Redis_Unavailable", "INFO");
                    continue;
                }
                // 全空的组不显示:一整片空白比少一组更让人以为是坏了。
                if (group.Items.Any(item => item.Value.Length > 0))
                {
                    Overview.Add(group);
                }
            }
        }
        catch (Exception ex)
        {
            OverviewNotice = Loc.Format("Redis_Error", ex.Message);
            _log.Error("Reading INFO failed.", ex);
        }
    }

    private async Task LoadSlowlogAsync()
    {
        try
        {
            IReadOnlyList<RedisSlowlogEntry>? entries = await _connection.ReadSlowlogAsync().ConfigureAwait(true);
            Slowlog.Clear();
            if (entries is null)
            {
                SlowlogNotice = Loc.Format("Redis_Unavailable", "SLOWLOG");
                return;
            }
            SlowlogNotice = string.Empty;
            foreach (RedisSlowlogEntry entry in entries)
            {
                Slowlog.Add(entry);
            }
        }
        catch (Exception ex)
        {
            SlowlogNotice = Loc.Format("Redis_Error", ex.Message);
            _log.Error("Reading SLOWLOG failed.", ex);
        }
    }

    private async Task ResetSlowlogAsync() =>
        await GuardedAsync("SLOWLOG", async () =>
        {
            await _connection.ResetSlowlogAsync().ConfigureAwait(true);
            await LoadSlowlogAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);

    private async Task LoadClientsAsync()
    {
        try
        {
            IReadOnlyList<RedisClientEntry>? entries = await _connection.ReadClientsAsync().ConfigureAwait(true);
            Clients.Clear();
            SelectedClient = null;
            if (entries is null)
            {
                ClientsNotice = Loc.Format("Redis_Unavailable", "CLIENT LIST");
                return;
            }
            ClientsNotice = string.Empty;
            foreach (RedisClientEntry entry in entries)
            {
                Clients.Add(entry);
            }
        }
        catch (Exception ex)
        {
            ClientsNotice = Loc.Format("Redis_Error", ex.Message);
            _log.Error("Reading CLIENT LIST failed.", ex);
        }
    }

    private async Task KillClientAsync()
    {
        if (SelectedClient is not { IsSelf: false } client)
        {
            return;
        }
        await GuardedAsync("CLIENT", async () =>
        {
            await _connection.KillClientAsync(client.Id).ConfigureAwait(true);
            await LoadClientsAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task SubscribeAsync()
    {
        string channel = ChannelDraft.Trim();
        if (channel.Length == 0 || Subscriptions.Contains(channel))
        {
            return;
        }
        try
        {
            await _connection.SubscribeAsync(channel, (actual, payload) =>
                // 库在自己的线程上回调:改集合必须封送回 UI 线程。
                Dispatcher.UIThread.Post(() =>
                {
                    Messages.Insert(0, new(DateTimeOffset.Now, actual, payload));
                    while (Messages.Count > MaxMessages)
                    {
                        Messages.RemoveAt(Messages.Count - 1);
                    }
                })).ConfigureAwait(true);
            Subscriptions.Add(channel);
            ChannelDraft = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.Format("Redis_Error", ex.Message);
            _log.Error($"Subscribing to '{channel}' failed.", ex);
        }
    }

    private async Task UnsubscribeAsync()
    {
        if (SelectedSubscription is not { Length: > 0 } channel)
        {
            return;
        }
        try
        {
            await _connection.UnsubscribeAsync(channel).ConfigureAwait(true);
            Subscriptions.Remove(channel);
            SelectedSubscription = null;
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.Format("Redis_Error", ex.Message);
            _log.Error($"Unsubscribing from '{channel}' failed.", ex);
        }
    }

    private async Task SampleMemoryAsync()
    {
        StopSampling();
        _samplingCts = new();
        CancellationToken token = _samplingCts.Token;
        IsSamplingMemory = true;
        MemoryByPrefix.Clear();
        MemoryTopKeys.Clear();
        MemoryNotice = Loc["Redis_Refreshing"];
        try
        {
            RedisMemorySample sample = await _connection
                .SampleMemoryAsync(_connection.Settings.ScanBudget, sampled =>
                    Dispatcher.UIThread.Post(() => MemoryNotice = Loc.Format("Redis_MemorySampleNote",
                        sampled.ToString("N0", CultureInfo.CurrentCulture), "…")),
                    token)
                .ConfigureAwait(true);
            if (!sample.Available)
            {
                MemoryNotice = Loc["Redis_MemoryNeedsCommand"];
                return;
            }
            foreach (RedisMemoryBucket bucket in sample.Buckets)
            {
                MemoryByPrefix.Add(bucket);
            }
            foreach (RedisMemoryBucket bucket in sample.TopKeys)
            {
                MemoryTopKeys.Add(bucket);
            }
            // 抽样比例如实报出:分母未知时写"?"而不是假装 100%。
            string share = sample.EstimatedTotal > 0
                ? ((double)sample.SampledKeys / sample.EstimatedTotal).ToString("P1", CultureInfo.CurrentCulture)
                : "?";
            MemoryNotice = Loc.Format("Redis_MemorySampleNote",
                sample.SampledKeys.ToString("N0", CultureInfo.CurrentCulture), share);
        }
        catch (OperationCanceledException)
        {
            MemoryNotice = Loc.Format("Redis_MemorySampleNote",
                MemoryTopKeys.Count.ToString("N0", CultureInfo.CurrentCulture), "?");
        }
        catch (Exception ex)
        {
            MemoryNotice = Loc.Format("Redis_Error", ex.Message);
            _log.Error("Sampling memory failed.", ex);
        }
        finally
        {
            IsSamplingMemory = false;
        }
    }

    private void StopSampling()
    {
        CancellationTokenSource? cts = _samplingCts;
        _samplingCts = null;
        if (cts is null)
        {
            return;
        }
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经停过了。
        }
        cts.Dispose();
    }

    /// <summary>订阅消息的保留条数。一个永不截断的消息流迟早把内存吃光。</summary>
    private const int MaxMessages = 1000;
}

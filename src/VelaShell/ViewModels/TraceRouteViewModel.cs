using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using ReactiveUI;
using VelaShell.Core.Diagnostics;
using VelaShell.Core.Resources;

namespace VelaShell.ViewModels;

/// <summary>链路追踪面板中的一跳。</summary>
public sealed class TraceHopViewModel(TraceHop hop) : ReactiveObject
{
    /// <summary>本跳的 TTL。</summary>
    public int Ttl { get; } = hop.Ttl;

    /// <summary>主机显示文本:有 PTR 时显示主机名,否则显示 IP;整跳无回应时显示 * * *。</summary>
    public string Host { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "*";

    /// <summary>该跳观测到的额外地址数(ECMP 多路径),为 0 时不显示。</summary>
    public string ExtraAddresses { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = string.Empty;

    /// <summary>丢包率文本。</summary>
    public string Loss { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "-";

    /// <summary>已发探测数。</summary>
    public string Sent { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "0";

    /// <summary>最近一次 RTT。</summary>
    public string Last { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "-";

    /// <summary>平均 RTT。</summary>
    public string Average { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "-";

    /// <summary>最快 RTT。</summary>
    public string Best { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "-";

    /// <summary>最慢 RTT。</summary>
    public string Worst { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "-";

    /// <summary>RTT 标准差。</summary>
    public string StdDev { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "-";

    /// <summary>判定结论,驱动行的着色。</summary>
    public HopVerdict Verdict { get; private set => this.RaiseAndSetIfChanged(ref field, value); }

    /// <summary>是否为疑似真实丢包(红色)。</summary>
    public bool IsSuspect => Verdict is HopVerdict.SuspectedLoss or HopVerdict.Unreachable;

    /// <summary>是否为 ICMP 限速/不回应(灰色,不算故障)。</summary>
    public bool IsQuiet => Verdict is HopVerdict.IcmpRateLimited or HopVerdict.NoResponse;

    /// <summary>本跳是否就是目标。</summary>
    public bool IsTarget { get; private set => this.RaiseAndSetIfChanged(ref field, value); }

    /// <summary>判定结论的本地化说明,作为整行的悬停提示。</summary>
    public string VerdictHint { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = string.Empty;

    /// <summary>推断出的归属地文本("国家/城市");查不到时为空串。</summary>
    public string LocationText { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = string.Empty;

    /// <summary>本跳的首个地址;整跳无回应时为 null。用户中途换库时据此回填位置。</summary>
    public System.Net.IPAddress? Address { get; private set => this.RaiseAndSetIfChanged(ref field, value); }

    /// <summary>是否拿到了经纬度(决定是否落到地图上)。</summary>
    public bool HasLocation { get; private set => this.RaiseAndSetIfChanged(ref field, value); }

    /// <summary>推断纬度。</summary>
    public double Latitude { get; private set => this.RaiseAndSetIfChanged(ref field, value); }

    /// <summary>推断经度。</summary>
    public double Longitude { get; private set => this.RaiseAndSetIfChanged(ref field, value); }

    /// <summary>写入归属地。位置只是"推断",界面上要与确定信息区别呈现。</summary>
    /// <param name="location">查到的位置;null 表示查不到。</param>
    public void SetLocation(IpLocation? location)
    {
        if (location is null)
        {
            HasLocation = false;
            LocationText = string.Empty;
            return;
        }
        Latitude = location.Latitude;
        Longitude = location.Longitude;
        LocationText = location.Display;
        HasLocation = true;
    }

    /// <summary>用最新统计刷新本行。</summary>
    /// <param name="sample">同一 TTL 的最新累计统计。</param>
    public void Update(TraceHop sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        Address = sample.Addresses.Count > 0 ? sample.Addresses[0] : null;
        Host = sample.Addresses.Count == 0
                   ? "* * *"
                   : sample.HostName is { Length: > 0 } name
                       ? $"{name} ({sample.Addresses[0]})"
                       : sample.Addresses[0].ToString();
        ExtraAddresses = sample.Addresses.Count > 1
                             ? string.Create(CultureInfo.CurrentCulture, $"+{sample.Addresses.Count - 1}")
                             : string.Empty;
        Loss = sample.Sent == 0 ? "-" : string.Create(CultureInfo.CurrentCulture, $"{sample.LossPercent:F1}%");
        Sent = sample.Sent.ToString(CultureInfo.CurrentCulture);
        Last = Ms(sample.Last);
        Average = Ms(sample.Average);
        Best = Ms(sample.Best);
        Worst = Ms(sample.Worst);
        StdDev = sample.Received < 2 ? "-" : string.Create(CultureInfo.CurrentCulture, $"{sample.StdDevMs:F1}");
        Verdict = sample.Verdict;
        IsTarget = sample.IsTarget;
        VerdictHint = Strings.Get(
            sample.Verdict switch
            {
                HopVerdict.IcmpRateLimited => "Trace_HintRateLimited",
                HopVerdict.NoResponse => "Trace_HintNoResponse",
                HopVerdict.SuspectedLoss => "Trace_HintSuspectedLoss",
                HopVerdict.Unreachable => "Trace_HintUnreachable",
                _ => "Trace_HintOk"
            }
        );
        this.RaisePropertyChanged(nameof(IsSuspect));
        this.RaisePropertyChanged(nameof(IsQuiet));
    }

    private static string Ms(TimeSpan? value) =>
        value is { } span ? string.Create(CultureInfo.CurrentCulture, $"{span.TotalMilliseconds:F1}") : "-";
}

/// <summary>
/// 链路追踪面板:对目标逐跳探测并持续刷新统计。呈现与显示时机对齐 SFTP 资源管理器面板
/// (标题栏按钮切换、共用底部面板区、同一套启用条件)。
/// </summary>
public sealed class TraceRouteViewModel : ReactiveObject, IDisposable
{
    private readonly ITraceRouteService? _service;
    private readonly IIpGeolocationService? _geolocation;
    private readonly Action<string>? _rememberDatabasePath;
    private readonly Func<Action, Task> _toUi;
    private readonly Dictionary<int, TraceHopViewModel> _rows = [];
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>创建面板视图模型。</summary>
    /// <param name="service">链路追踪服务;为 null 时面板可见但无法启动(无头测试)。</param>
    /// <param name="geolocation">离线归属地查询;为 null 或库缺失时地图无落点,追踪不受影响。</param>
    /// <param name="rememberDatabasePath">用户选定数据库后的持久化回调。</param>
    /// <param name="uiDispatcher">把回调切回 UI 线程的方式;默认走 Avalonia 调度器,测试可注入同步实现。</param>
    public TraceRouteViewModel(
        ITraceRouteService? service,
        IIpGeolocationService? geolocation = null,
        Action<string>? rememberDatabasePath = null,
        Func<Action, Task>? uiDispatcher = null
    )
    {
        // 调度做成可注入的接缝:无头单测里没有调度器泵消息,InvokeAsync 的回调永远不执行,
        // 所有等待 UI 回调的断言都会卡到超时。
        _toUi = uiDispatcher ?? (action => Dispatcher.UIThread.InvokeAsync(action).GetTask());
        _service = service;
        _geolocation = geolocation;
        _rememberDatabasePath = rememberDatabasePath;
        GeoDatabaseMissing = geolocation is null || !geolocation.IsAvailable;
        GeoDatabaseStatus = geolocation?.DatabaseDescription ?? string.Empty;
        PickDatabaseCommand = ReactiveCommand.CreateFromTask(PickDatabaseAsync);
        OpenDatabaseUrlCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (UrlOpener is { } open)
            {
                await open(GeoDatabaseUrl).ConfigureAwait(true);
            }
        });
        // 开始按钮不看是否正在跑:再点一次就是"重来一遍"。此前要求先停后开,
        // 改完目标想重跑必须多点一次停止,没有道理。
        IObservable<bool> canStart = this.WhenAnyValue(x => x.Target)
            .Select(target => !string.IsNullOrWhiteSpace(target) && _service is not null);
        StartCommand = ReactiveCommand.Create(Start, canStart);
        StopCommand = ReactiveCommand.Create(Stop, this.WhenAnyValue(x => x.IsRunning));
    }

    /// <summary>
    /// 是否持续追踪(mtr 风格,统计量随轮次收敛);关闭则只跑 <see cref="RoundLimit" /> 轮就停。
    /// 判断抖动与间歇丢包必须持续跑,因此默认开启。
    /// </summary>
    public bool Continuous
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    /// <summary>非持续模式下的轮数。</summary>
    public int RoundLimit
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = 3;

    /// <summary>追踪目标(主机名或 IP)。</summary>
    public string Target
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>面板标题里显示的会话名称。</summary>
    public string SessionLabel
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>是否正在追踪。</summary>
    public bool IsRunning
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>状态栏文本(轮数、是否到达、错误原因)。</summary>
    public string Status
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>是否已探到目标。</summary>
    public bool TargetReached
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>逐跳结果,按 TTL 升序。</summary>
    public ObservableCollection<TraceHopViewModel> Hops { get; } = [];

    /// <summary>数据版本号,每轮递增一次;地图控件据此重绘。</summary>
    public int Revision
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>离线归属地库是否缺失(缺则地图无落点,界面上给出说明与下载指引)。</summary>
    public bool GeoDatabaseMissing
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    /// <summary>已加载的数据库说明,或最近一次加载失败的提示。</summary>
    public string GeoDatabaseStatus
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>
    /// DB-IP Lite City 的直链。选它是因为 CC BY 4.0 只要求署名,没有相同方式共享条款,
    /// 商业版也能用;文件名里的年月每月一换,失效时到 https://db-ip.com/db/lite.php 取最新的。
    /// </summary>
    public static string GeoDatabaseUrl => "https://download.db-ip.com/free/dbip-city-lite-2026-07.mmdb.gz";

    /// <summary>选择本地 .mmdb / .mmdb.gz 文件。</summary>
    public ReactiveCommand<Unit, Unit> PickDatabaseCommand { get; private set; } = null!;

    /// <summary>在浏览器里打开数据库下载地址。</summary>
    public ReactiveCommand<Unit, Unit> OpenDatabaseUrlCommand { get; private set; } = null!;

    /// <summary>由视图注入的文件选择器,返回选中文件的绝对路径;取消返回 null。</summary>
    public Func<Task<string?>>? DatabaseFilePicker { get; set; }

    /// <summary>由视图注入的浏览器打开回调。</summary>
    public Func<string, Task>? UrlOpener { get; set; }

    /// <summary>选中文件后的落库流程:必要时解压 .gz,加载,记住路径,并回填已有跃点的位置。</summary>
    private async Task PickDatabaseAsync()
    {
        if (DatabaseFilePicker is not { } picker || _geolocation is null)
        {
            return;
        }
        string? picked = await picker().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(picked))
        {
            return;
        }
        string path = picked;
        if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            // 官方下载就是 .mmdb.gz,免得用户还要先找个工具解压一遍。
            GeoDatabaseStatus = Strings.Get("Trace_GeoExtracting");
            string? extracted = await Task.Run(() => TryExtract(path)).ConfigureAwait(true);
            if (extracted is null)
            {
                GeoDatabaseStatus = Strings.Get("Trace_GeoLoadFailed");
                return;
            }
            path = extracted;
        }
        if (!_geolocation.TryLoad(path))
        {
            GeoDatabaseStatus = Strings.Get("Trace_GeoLoadFailed");
            return;
        }
        GeoDatabaseMissing = false;
        GeoDatabaseStatus = _geolocation.DatabaseDescription ?? string.Empty;
        _rememberDatabasePath?.Invoke(path);

        // 已经跑过的跃点立刻补上位置,不用等下一轮。
        foreach (TraceHopViewModel row in Hops)
        {
            if (!row.HasLocation && row.Address is { } address)
            {
                row.SetLocation(_geolocation.Lookup(address));
            }
        }
        Revision++;
    }

    /// <summary>把 .mmdb.gz 解到同目录下的 .mmdb;失败返回 null。</summary>
    private static string? TryExtract(string archivePath)
    {
        try
        {
            string target = archivePath[..^3];
            using FileStream source = File.OpenRead(archivePath);
            using var gzip = new System.IO.Compression.GZipStream(source, System.IO.Compression.CompressionMode.Decompress);
            using FileStream destination = File.Create(target);
            gzip.CopyTo(destination);
            return target;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>开始追踪。</summary>
    public ReactiveCommand<Unit, Unit> StartCommand { get; }

    /// <summary>停止追踪。</summary>
    public ReactiveCommand<Unit, Unit> StopCommand { get; }

    /// <summary>停止追踪并释放取消源。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Stop();
    }

    /// <summary>把面板指向某个会话的主机;正在追踪时先停下。</summary>
    /// <param name="host">目标主机名或 IP。</param>
    /// <param name="label">会话显示名称。</param>
    public void PointAt(string host, string label)
    {
        Stop();
        Target = host;
        SessionLabel = label;
        _rows.Clear();
        Hops.Clear();
        TargetReached = false;
        Status = string.Empty;
    }

    private void Start()
    {
        if (_service is null)
        {
            return;
        }
        Stop(); // 再点一次 = 重来一遍;正在跑的那轮先收掉
        _rows.Clear();
        Hops.Clear();
        TargetReached = false;
        Status = Strings.Get("Trace_Running");
        IsRunning = true;
        CancellationTokenSource cts = new();
        _cts = cts;
        _ = RunAsync(cts);
    }

    private void Stop()
    {
        // 只取消不 Dispose:正在跑的那一轮还在用这个令牌(Task.Delay 会往上注册),
        // 提前释放会抛 ObjectDisposedException,被外层当成"追踪失败"显示出来。
        _cts?.Cancel();
        _cts = null;
        if (IsRunning)
        {
            IsRunning = false;
            Status = Strings.Get("Trace_Stopped");
        }
    }

    /// <summary>
    /// 跑一轮或多轮追踪。收尾时只有"自己仍是当前那一轮"才动 <see cref="IsRunning" /> ——
    /// 否则被取消的旧轮次的收尾会在新轮次启动之后才排到,把新轮次的运行标记清掉:
    /// 表现就是停止按钮变灰、开始按钮还亮着,而追踪其实还在跑。
    /// </summary>
    private async Task RunAsync(CancellationTokenSource cts)
    {
        CancellationToken token = cts.Token;
        try
        {
            TraceOptions options = new(Target.Trim(), Rounds: Continuous ? 0 : Math.Max(1, RoundLimit));
            await foreach (TraceUpdate update in _service!.RunAsync(options, token).ConfigureAwait(true))
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }
                // 采集在后台线程推进,属性变更必须回到 UI 线程,否则绑定不刷新 —— 表现就是
                // "跑起来了但表格不动"。身份校验放在回调内部:检查与 Merge 之间隔着一次线程
                // 切换,用户正好在这个空档点了重开的话,旧轮次的结果会灌进新轮次的列表里。
                await _toUi(() =>
                {
                    if (ReferenceEquals(_cts, cts))
                    {
                        Merge(update);
                    }
                });
            }
            if (!token.IsCancellationRequested)
            {
                await _toUi(() =>
                {
                    if (ReferenceEquals(_cts, cts))
                    {
                        Status = Strings.Get("Trace_Completed");
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            // 用户按了停止或重开,正常路径。
        }
        catch (Exception ex)
        {
            await _toUi(() =>
            {
                if (ReferenceEquals(_cts, cts))
                {
                    Status = Strings.Format("Trace_Failed", ex.Message);
                }
            });
        }
        finally
        {
            await _toUi(() =>
            {
                if (ReferenceEquals(_cts, cts))
                {
                    IsRunning = false;
                    _cts = null;
                }
            });
            // 由本轮自己释放:Stop() 只取消不释放(取消时循环还在用这个令牌),
            // 跑到这里才是最后一个使用者。不释放虽然也能被 GC 回收,但连点开始会攒下一堆。
            cts.Dispose();
        }
    }

    /// <summary>把一轮快照并入行集合:行对象按 TTL 复用,避免每轮重建打断滚动与选中。</summary>
    private void Merge(TraceUpdate update)
    {
        for (int i = 0; i < update.Hops.Count; i++)
        {
            TraceHop hop = update.Hops[i];
            if (!_rows.TryGetValue(hop.Ttl, out TraceHopViewModel? row))
            {
                row = new(hop);
                _rows[hop.Ttl] = row;
                Hops.Insert(Math.Min(i, Hops.Count), row);
            }
            row.Update(hop);
            // 归属地只查一次:同一跳的地址在整轮追踪里不会变,而查库要走内存里的整个 mmdb。
            if (!row.HasLocation && hop.Addresses.Count > 0 && _geolocation is { IsAvailable: true })
            {
                row.SetLocation(_geolocation.Lookup(hop.Addresses[0]));
            }
        }

        // 尾部静默跳被服务端裁掉后,界面上也要跟着收掉。
        while (Hops.Count > update.Hops.Count)
        {
            TraceHopViewModel removed = Hops[^1];
            Hops.RemoveAt(Hops.Count - 1);
            _rows.Remove(removed.Ttl);
        }

        Revision++;
        TargetReached = update.TargetReached;
        Status = update.TargetReached
                     ? Strings.Format("Trace_ReachedFormat", update.Hops.Count, update.Round)
                     : Strings.Format("Trace_ProbingFormat", update.Hops.Count, update.Round);
    }
}

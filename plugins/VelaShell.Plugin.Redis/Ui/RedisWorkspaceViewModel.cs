using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace VelaShell.Plugin.Redis.Ui;

/// <summary>过滤条的匹配方式。三段式分段控件,决定生成什么样的 <c>MATCH</c> 模式。</summary>
public enum RedisMatchMode
{
    /// <summary>前缀:<c>user</c> → <c>user*</c>。</summary>
    Prefix,

    /// <summary>包含:<c>user</c> → <c>*user*</c>。</summary>
    Contains,

    /// <summary>通配:用户输入的就是模式本身。</summary>
    Glob
}

/// <summary>
/// Redis 工作台面板的视图模型:键空间浏览器 + 键详情。
/// <para>
/// 三条纪律在这里落地(见 <c>docs/Redis客户端插件化调研与设计.md</c> §6.2):
/// 永不 <c>KEYS</c>、进度必须诚实(只有游标归零才敢说"扫完了")、一批一个往返。
/// </para>
/// <para>
/// 刻意不引 ReactiveUI(与 S3 插件同一条理由):插件 ALC 里那份 <c>RxApp</c> 与宿主的是
/// 两个独立实例,它的主线程调度器不会自动挂到 Avalonia 的调度器上。
/// </para>
/// </summary>
public sealed partial class RedisWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly RedisConnection _connection;
    private readonly IPluginLoggerFacade _log;

    /// <summary>插件私有持久化(收藏 + 控制台历史);无 DB 的宿主上为 null。</summary>
    private readonly RedisStore? _store;

    /// <summary>持久化的分组键。用端点而不是会话 id —— 收藏与历史要跨会话留住。</summary>
    private readonly string _connectionKey;
    /// <summary>
    /// 已扫描到的键(去重后)与它们的元数据。列表每来一页就按它整份重排 ——
    /// 元数据挂在这里而不是行对象上,重排、换阈值、展开分组都不必再问一次服务器。
    /// </summary>
    private readonly Dictionary<RedisKeyName, RedisKeyMeta> _scanned = [];

    /// <summary>已展开的分组行 id。重排时查它,展开态因此不会被下一页扫描抹掉。</summary>
    private readonly HashSet<string> _expandedGroups = [with(StringComparer.Ordinal)];
    private CancellationTokenSource? _scanCts;

    /// <summary>
    /// 扫描代数。换过滤条件会把上一轮取消掉,而**被取消的那一轮仍会走完自己的 finally** ——
    /// 不按代数判别的话,它那句 <c>IsScanning = false</c> 会落在新一轮已经开跑之后,
    /// 于是「继续扫描」按钮在真的还在扫时就亮了。
    /// </summary>
    private int _scanGeneration;
    private string _cursor = "0";
    private int _visited;
    private long _totalKeys = -1;
    private bool _disposed;

    /// <summary>构造。</summary>
    /// <param name="connection">已连接的连接。</param>
    /// <param name="title">会话展示名(标签页标题同源)。</param>
    /// <param name="endpoint">端点文本(主机:端口)。</param>
    /// <param name="loc">文案表。</param>
    /// <param name="log">日志出口。</param>
    /// <param name="store">插件私有持久化(收藏 + 控制台历史);为 null 时两者只在本次会话内有效。</param>
    internal RedisWorkspaceViewModel(
        RedisConnection connection,
        string title,
        string endpoint,
        Loc loc,
        IPluginLoggerFacade log,
        RedisStore? store = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _store = store;
        _connectionKey = endpoint;
        Loc = loc ?? throw new ArgumentNullException(nameof(loc));
        Title = title;
        Endpoint = endpoint;

        RescanCommand = new(() => ScanAsync(restart: true));
        ContinueScanCommand = new(() => ScanAsync(restart: false), () => !IsScanning && !IsScanComplete);
        StopScanCommand = new(() =>
        {
            StopScan();
            return Task.CompletedTask;
        });
        // 三个方式各给一条无参命令,而不是一条带枚举参数的:
        // XAML 里的 CommandParameter 是字符串,转不成枚举时命令会**永久灰掉**,
        // 而那种失败在界面上表现为"点了没反应",极难往参数类型上想。
        UsePrefixMatchCommand = new(() => ApplyMatchModeAsync(RedisMatchMode.Prefix));
        UseContainsMatchCommand = new(() => ApplyMatchModeAsync(RedisMatchMode.Contains));
        UseGlobMatchCommand = new(() => ApplyMatchModeAsync(RedisMatchMode.Glob));
        LoadMoreCommand = new(LoadMoreElementsAsync, () => !IsLoadingDetail && HasMoreElements);
        NavigateHomeCommand = new(() => NavigateToAsync(null));
        NavigateCommand = new(NavigateToAsync);
        ToggleDrawerCommand = new(() =>
        {
            IsDrawerOpen = !IsDrawerOpen;
            return Task.CompletedTask;
        });

        Console = new(connection, Confirmation, Loc, store, endpoint) { Endpoint = endpoint };
        // 控制台里 SELECT 切库要让浏览器跟上:静默分叉(控制台在 db3、浏览器还在 db0)
        // 是比"多刷新一次"糟糕得多的失败模式。
        Console.DatabaseSelected += database => _ = FollowConsoleDatabaseAsync(database);

        InitializeEditing();
        InitializeDrawer();
        InitializeFavorites();
        BuildDatabases();
    }

    /// <summary>文案表。</summary>
    public Loc Loc { get; }

    /// <summary>内置控制台(底部抽屉的第一个页签)。</summary>
    public RedisConsoleViewModel Console { get; }

    /// <summary>控制台切库后让浏览器跟上,并显式提示 —— 静默分叉是更差的失败模式。</summary>
    private async Task FollowConsoleDatabaseAsync(int database)
    {
        RedisDatabaseOption? option = Databases.FirstOrDefault(item => item.Index == database);
        if (option is null)
        {
            return;
        }
        SelectedDatabase = option;
        StatusMessage = Loc.Format("Redis_ConsoleSelectedDatabase", database);
        await Task.CompletedTask.ConfigureAwait(true);
    }

    /// <summary>会话展示名。</summary>
    public string Title { get; }

    /// <summary>端点文本。</summary>
    public string Endpoint { get; }

    /// <summary>服务器摘要:发行 + 版本 + 协议(状态条右侧那一格)。</summary>
    public string ServerSummary =>
        $"{_connection.Info.Protocol} · {_connection.Info.Flavor} {_connection.Info.Version}"
        + (LatencyMs >= 0 ? $" · {LatencyMs} ms" : string.Empty);

    /// <summary>环境标记文案(生产 / 预发 / 开发)。</summary>
    public string EnvironmentLabel => _connection.Settings.Environment switch
    {
        RedisEnvironment.Production => Loc["Redis_EnvProduction"],
        RedisEnvironment.Staging => Loc["Redis_EnvStaging"],
        _ => Loc["Redis_EnvDevelopment"]
    };

    /// <summary>是否生产环境(界面据此把标记染成 <c>VelaError</c>)。</summary>
    public bool IsProduction => _connection.Settings.Environment == RedisEnvironment.Production;

    /// <summary>只读模式(M1 只呈现,写入能力随类型编辑器一起做)。</summary>
    public bool IsReadOnly => _connection.Settings.ReadOnly;

    /// <summary>只读徽章文案。</summary>
    public string ReadOnlyLabel => Loc["Redis_ReadOnlyBadge"];

    /// <summary>最近一次探活延迟(毫秒);未测为 -1。</summary>
    public int LatencyMs
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(ServerSummary));
        }
    } = -1;

    // ── 数据库选择 ────────────────────────────────────────────────

    /// <summary>可选数据库(带已知键数,来自 <c>INFO keyspace</c>)。</summary>
    public ObservableCollection<RedisDatabaseOption> Databases { get; } = [];

    /// <summary>当前数据库序号。</summary>
    public int CurrentDatabase => _connection.Database;

    /// <summary>
    /// 下拉选中的数据库。切换即重扫 —— 换库之后旧库的键树一条都不再成立,
    /// 留在界面上比清空更糟(用户会以为那些键在新库里)。
    /// </summary>
    public RedisDatabaseOption? SelectedDatabase
    {
        get;
        set
        {
            RedisDatabaseOption? previous = field;
            SetProperty(ref field, value);
            if (value is null || !SupportsDatabases || value.Index == _connection.Database)
            {
                return;
            }
            _connection.SelectDatabase(value.Index);
            RaisePropertyChanged(nameof(CurrentDatabase));
            previous?.IsSelected = false;
            value.IsSelected = true;
            // 控制台的提示符要跟上:提示符里写着库号,不刷新就会与实际操作的库不一致。
            Console.RefreshPrompt();
            _ = ScanAsync(restart: true);
        }
    }

    /// <summary>集群下没有多数据库,界面据此禁用下拉。</summary>
    public bool SupportsDatabases => _connection.Settings.SupportsDatabases;

    /// <summary>数据库下拉的提示(集群下解释为什么禁用)。</summary>
    public string DatabaseHint => SupportsDatabases ? string.Empty : Loc["Redis_ClusterNoDatabases"];

    // ── 过滤条 ────────────────────────────────────────────────────

    /// <summary>过滤文本。</summary>
    public string Filter
    {
        get;
        set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(MatchEcho));
        }
    } = string.Empty;

    /// <summary>匹配方式。</summary>
    public RedisMatchMode MatchMode
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(IsPrefixMode));
            RaisePropertyChanged(nameof(IsContainsMode));
            RaisePropertyChanged(nameof(IsGlobMode));
            RaisePropertyChanged(nameof(MatchEcho));
        }
    } = RedisMatchMode.Prefix;

    /// <summary>分段控件的三个选中态(XAML 里绑它上样式,不在视图里写逻辑)。</summary>
    public bool IsPrefixMode => MatchMode == RedisMatchMode.Prefix;

    /// <inheritdoc cref="IsPrefixMode" />
    public bool IsContainsMode => MatchMode == RedisMatchMode.Contains;

    /// <inheritdoc cref="IsPrefixMode" />
    public bool IsGlobMode => MatchMode == RedisMatchMode.Glob;

    /// <summary>类型过滤(空 = 全部)。</summary>
    public string TypeFilter
    {
        get;
        set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(MatchEcho));
        }
    } = string.Empty;

    /// <summary>可选的类型过滤项。</summary>
    public IReadOnlyList<string> TypeOptions { get; } = ["", "string", "hash", "list", "set", "zset", "stream"];

    /// <summary>
    /// **回显真正要发的命令。** 所有 Redis 图形客户端的头号困惑是"我明明有这个键,为什么搜不到"
    /// —— 答案通常是用户以为在做子串搜索,而 <c>MATCH</c> 是通配匹配。把生成的命令摊在
    /// 输入框下面,比任何提示文案都有效,顺带教会了用户 <c>SCAN</c> 怎么用。
    /// </summary>
    public string MatchEcho
    {
        get
        {
            StringBuilder builder = new StringBuilder("SCAN 0 MATCH ")
                .Append(BuildPattern())
                .Append(" COUNT ")
                .Append(_connection.Settings.ScanCount.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(TypeFilter))
            {
                builder.Append(" TYPE ").Append(TypeFilter);
            }
            return builder.ToString();
        }
    }

    // ── 键列表与扫描状态 ──────────────────────────────────────────

    /// <summary>
    /// 键列表的行。一行一个完整键名,同前缀多到成噪音时折成一条分组行 ——
    /// 规则见 <see cref="RedisKeyLayout" />。
    /// </summary>
    public ObservableCollection<RedisKeyRow> Rows { get; } = [];

    /// <summary>
    /// 面包屑:已扫到的这批键共享的前缀逐段。点某一段 = 把过滤条设成该前缀重扫,
    /// 于是"下钻"这件事和过滤条是同一个东西,而不是第二套导航状态。
    /// </summary>
    public ObservableCollection<RedisBreadcrumbSegment> Breadcrumb { get; } = [];

    /// <summary>有没有可点的面包屑(只有一段"全部"时不必占一行)。</summary>
    public bool HasBreadcrumb => Breadcrumb.Count > 0;

    /// <summary>正在扫描。</summary>
    public bool IsScanning
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            ContinueScanCommand.RaiseCanExecuteChanged();
            RaisePropertyChanged(nameof(ScanStatus));
        }
    }

    /// <summary>游标已归零 —— **唯一**可以说"这就是全部"的依据。</summary>
    public bool IsScanComplete
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            ContinueScanCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>已匹配到的键数(去重后)。</summary>
    public int MatchedCount
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(ScanStatus));
            RaisePropertyChanged(nameof(IsEmpty));
        }
    }

    /// <summary>列表是空的(而且不是因为还在扫)。</summary>
    public bool IsEmpty => MatchedCount == 0 && !IsScanning;

    /// <summary>
    /// 扫描状态文案。措辞刻意区分三种情形:还在扫 / 扫完了(游标归零)/ 停在预算上限。
    /// **只有第二种才允许出现"全部"这个词。**
    /// </summary>
    public string ScanStatus
    {
        get
        {
            if (IsScanning)
            {
                return Loc["Redis_Scanning"];
            }
            if (MatchedCount == 0 && IsScanComplete)
            {
                return _totalKeys == 0
                    ? Loc.Format("Redis_EmptyDatabase", CurrentDatabase)
                    : Loc.Format("Redis_ScanNoMatch", Approx(_visited), BuildPattern());
            }
            if (IsScanComplete)
            {
                return Loc.Format("Redis_ScanComplete", MatchedCount.ToString("N0", CultureInfo.CurrentCulture));
            }
            string matched = MatchedCount.ToString("N0", CultureInfo.CurrentCulture);
            return _totalKeys >= 0
                ? Loc.Format("Redis_ScanProgress", matched, Approx(_totalKeys), Percent())
                : Loc.Format("Redis_ScanBudgetHit", matched);
        }
    }

    // ── 键详情 ────────────────────────────────────────────────────

    /// <summary>
    /// 当前选中的列表行(视图直接绑 ListBox 的 SelectedItem)。
    /// <para>
    /// 选中一条**分组行**不加载任何键 —— 它不是键,只是一批键的折叠态。
    /// 详情区因此保持原样,而不是闪一下空白。
    /// </para>
    /// </summary>
    public RedisKeyRow? SelectedRow
    {
        get;
        set
        {
            if (value is { IsGroup: true })
            {
                // **否决**掉分组行的选中:它不是键,选中它什么也不会显示,而那道高亮
                // 会让用户以为"当前项"变了 —— 如果之前选着某个键,详情区还停在那个键上,
                // 高亮却跑了,两边就开始各说各话。通知一次让列表把选中态弹回去。
                RaisePropertyChanged();
                return;
            }
            SetProperty(ref field, value);
            if (field?.Key is { } key)
            {
                _ = LoadKeyAsync(key);
            }
            else if (field is null)
            {
                Selected = null;
                Elements.Clear();
                StringValue = string.Empty;
                RaisePropertyChanged(nameof(HasSelection));
            }
        }
    }

    /// <summary>选中键的元信息。</summary>
    public RedisKeyInfo? Selected
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(HasSelection));
            RaisePropertyChanged(nameof(SelectedTtlText));
            RaisePropertyChanged(nameof(SelectedMetaText));
            RaisePropertyChanged(nameof(IsStringSelected));
            RaisePropertyChanged(nameof(IsCollectionSelected));
            RaisePropertyChanged(nameof(ShowsScore));
            RaisePropertyChanged(nameof(LabelColumnHeader));
        }
    }

    /// <summary>有选中的键。</summary>
    public bool HasSelection => Selected is { IsGone: false };

    /// <summary>TTL 的显示形式("永不过期"或 <c>29:58</c>)。</summary>
    public string SelectedTtlText => Selected?.Ttl is { } ttl
        ? ttl.TotalHours >= 1
            ? $"{(int)ttl.TotalHours}:{ttl.Minutes:00}:{ttl.Seconds:00}"
            : $"{ttl.Minutes:00}:{ttl.Seconds:00}"
        : Loc["Redis_NoTtl"];

    /// <summary>编码与长度那一行。取不到编码(服务器禁了 <c>OBJECT</c>)就只显示长度。</summary>
    public string SelectedMetaText
    {
        get
        {
            if (Selected is not { } info)
            {
                return string.Empty;
            }
            var parts = new List<string>(2);
            if (!string.IsNullOrEmpty(info.Encoding))
            {
                parts.Add($"{Loc["Redis_Encoding"]} = {info.Encoding}");
            }
            if (info.Length >= 0)
            {
                parts.Add(info.Type is "hash"
                    ? Loc.Format("Redis_Fields", info.Length.ToString("N0", CultureInfo.CurrentCulture))
                    : info.Type is "string"
                        ? $"{info.Length:N0} B"
                        : Loc.Format("Redis_Elements", info.Length.ToString("N0", CultureInfo.CurrentCulture)));
            }
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>选中的是字符串(界面显示值编辑器)。</summary>
    public bool IsStringSelected => Selected?.Type is "string";

    /// <summary>选中的是集合类(界面显示行表)。</summary>
    public bool IsCollectionSelected => Selected?.Type is "hash" or "list" or "set" or "zset" or "stream";

    /// <summary>有序集合才显示分值列。</summary>
    public bool ShowsScore => Selected?.Type is "zset";

    /// <summary>行表第一列的表头(按类型换:字段 / 索引 / 成员 / ID)。</summary>
    public string LabelColumnHeader => Selected?.Type switch
    {
        "hash" => Loc["Redis_ColumnField"],
        "list" => Loc["Redis_ColumnIndex"],
        "set" or "zset" => Loc["Redis_ColumnMember"],
        "stream" => Loc["Redis_ColumnId"],
        _ => Loc["Redis_ColumnField"]
    };

    /// <summary>集合类值的行。</summary>
    public ObservableCollection<RedisElementRow> Elements { get; } = [];

    /// <summary>
    /// 服务端现值在**当前形态**下的文本形式(超出预览上限时只有前 N 字节)。
    /// 真相始终是原始字节;这一条只是它的一种渲染。
    /// </summary>
    public string StringValue
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    // ── 值的形态(文本 / 转义 / 十六进制)────────────────────────────
    //
    // Redis 的值和键一样是**二进制安全的字节串**。多数图形客户端在这里静默改坏数据:
    // 按 UTF-8 解码显示,用户点保存,再按 UTF-8 编回去 —— 非法序列在解码那一步就被
    // 替换字符顶掉了,于是"保存"实际上是"用一段近似值覆盖原值",而界面全程正常。
    //
    // 这里的规矩:显示与回写走**同一种可逆表示**,并且原始字节全程留着。

    /// <summary>选中键的原始字节(字符串类型)。所有回写都从它或它的可逆表示出发。</summary>
    private byte[] _valueBytes = [];

    /// <summary>当前形态。</summary>
    public RedisValueFormat ValueFormat
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(IsTextFormat));
            RaisePropertyChanged(nameof(IsEscapedFormat));
            RaisePropertyChanged(nameof(IsHexFormat));
            RaisePropertyChanged(nameof(CanEditString));
            RaisePropertyChanged(nameof(ValueFormatNotice));
            RaisePropertyChanged(nameof(HasValueFormatNotice));
            SaveStringCommand.RaiseCanExecuteChanged();
        }
    } = RedisValueFormat.Text;

    /// <summary>当前是原样文本形态。</summary>
    public bool IsTextFormat => ValueFormat == RedisValueFormat.Text;

    /// <summary>当前是转义形态。</summary>
    public bool IsEscapedFormat => ValueFormat == RedisValueFormat.Escaped;

    /// <summary>当前是十六进制形态(只读)。</summary>
    public bool IsHexFormat => ValueFormat == RedisValueFormat.Hex;

    /// <summary>
    /// 这个值能不能当文本看。
    /// <para>
    /// 为 <see langword="false" /> 时「文本」按钮是灰的 —— **不是**为了限制用户,而是因为
    /// 那条路会经过一次有损解码:界面上看到的替换字符并不是值里真有的字节,
    /// 顺手一保存就把原值换成了那段近似值。要把二进制值改写成一段纯文本,
    /// 在「转义」形态里直接键入即可(那边照样接受直接输入的中文与符号)。
    /// </para>
    /// </summary>
    public bool CanUseTextFormat
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(ValueFormatNotice));
            RaisePropertyChanged(nameof(HasValueFormatNotice));
        }
    } = true;

    /// <summary>形态说明:值是二进制、或当前形态只读时给一行话。</summary>
    public string ValueFormatNotice =>
        IsHexFormat ? Loc["Redis_HexReadOnly"]
        : CanUseTextFormat ? string.Empty
        : Loc["Redis_BinaryValue"];

    /// <summary>有没有形态说明要显示。</summary>
    public bool HasValueFormatNotice => ValueFormatNotice.Length > 0;

    /// <summary>切到原样文本。</summary>
    public AsyncCommand UseTextFormatCommand { get; private set; } = null!;

    /// <summary>切到转义(可逆,二进制值也能安全编辑)。</summary>
    public AsyncCommand UseEscapedFormatCommand { get; private set; } = null!;

    /// <summary>切到十六进制转储(只读)。</summary>
    public AsyncCommand UseHexFormatCommand { get; private set; } = null!;

    /// <summary>
    /// 切换形态。**先把当前草稿解回字节再按新形态渲染** —— 用户改到一半切个形态,
    /// 改动不会丢;草稿里有写坏的转义时拒绝切换并说清位置,而不是悄悄丢掉那几个字节。
    /// </summary>
    /// <param name="format">目标形态。</param>
    /// <returns>表示异步操作的任务。</returns>
    private Task SwitchValueFormatAsync(RedisValueFormat format)
    {
        if (format == ValueFormat)
        {
            return Task.CompletedTask;
        }
        if (format == RedisValueFormat.Text && !CanUseTextFormat)
        {
            // 按钮已经灰着;键盘或脚本触发时再挡一道。
            StatusMessage = Loc["Redis_BinaryValue"];
            return Task.CompletedTask;
        }
        // 十六进制只读,草稿不可能被改过,直接从原始字节走。
        byte[] bytes = _valueBytes;
        if (!IsHexFormat && !TryEncodeDraft(out bytes))
        {
            return Task.CompletedTask;
        }
        ValueFormat = format;
        StringValue = RedisValueText.Render(_valueBytes, format);
        StringDraft = RedisValueText.Render(bytes, format);
        RaisePropertyChanged(nameof(IsStringDirty));
        SaveStringCommand.RaiseCanExecuteChanged();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 把编辑框里的文本按当前形态解回字节。
    /// <para>
    /// 转义形态下认不出的转义**一律报错**,不猜 —— 把一个不认识的转义当成字面量,
    /// 就是在用户没察觉的情况下改动他要写的字节,而这正是整套形态机制要杜绝的事。
    /// </para>
    /// </summary>
    /// <param name="bytes">解出的字节。</param>
    /// <returns>解析成功。</returns>
    private bool TryEncodeDraft(out byte[] bytes)
    {
        if (ValueFormat == RedisValueFormat.Text)
        {
            bytes = Encoding.UTF8.GetBytes(StringDraft);
            return true;
        }
        if (RedisValueText.TryUnescape(StringDraft, out bytes, out string? error))
        {
            return true;
        }
        StatusMessage = Loc.Format("Redis_BadEscape", error ?? string.Empty);
        return false;
    }

    /// <summary>值被截断的说明;未截断时为空串。</summary>
    public string TruncationNotice
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>分页状态("已加载 12 / 12")。</summary>
    public string PageStatus
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>还有更多行可加载。</summary>
    public bool HasMoreElements
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            LoadMoreCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>正在读详情。</summary>
    public bool IsLoadingDetail
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            LoadMoreCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>一行状态/错误提示(空串表示无异常)。</summary>
    public string StatusMessage
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>
    /// 部署形态配错了的提示;空串表示形态相符。
    /// <para>
    /// 刻意**不用** <see cref="StatusMessage" />:那一行是瞬时的,每次扫描、每次选键都会
    /// 把它清空 —— 一条"你的形态选错了"的话刚写上去就被下一次扫描擦掉,等于没说。
    /// 配置错误不会自己好,所以它得有一块自己的、常驻的地方。
    /// </para>
    /// </summary>
    public string DeploymentWarning
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(HasDeploymentWarning));
        }
    } = string.Empty;

    /// <summary>形态提示条是否可见。</summary>
    public bool HasDeploymentWarning => DeploymentWarning.Length > 0;

    // ── 命令 ──────────────────────────────────────────────────────

    /// <summary>重新扫描(清空后从游标 0 开始)。</summary>
    public AsyncCommand RescanCommand { get; }

    /// <summary>继续扫描(从上次游标接着来)。</summary>
    public AsyncCommand ContinueScanCommand { get; }

    /// <summary>停止扫描。</summary>
    public AsyncCommand StopScanCommand { get; }

    /// <summary>改用前缀匹配并重扫。</summary>
    public AsyncCommand UsePrefixMatchCommand { get; }

    /// <summary>面包屑「全部」:清掉过滤条重扫。</summary>
    public AsyncCommand NavigateHomeCommand { get; }

    /// <summary>面包屑某一段:下钻到该前缀。</summary>
    public AsyncCommand<RedisBreadcrumbSegment?> NavigateCommand { get; }

    /// <summary>改用包含匹配并重扫。</summary>
    public AsyncCommand UseContainsMatchCommand { get; }

    /// <summary>改用通配匹配并重扫。</summary>
    public AsyncCommand UseGlobMatchCommand { get; }

    /// <summary>加载更多行。</summary>
    public AsyncCommand LoadMoreCommand { get; }

    /// <summary>面板挂上后的首次加载:探活 + 首轮扫描。</summary>
    /// <returns>表示异步操作的任务。</returns>
    public async Task InitializeAsync()
    {
        WarnOnDeploymentMismatch();
        await MeasureLatencyAsync().ConfigureAwait(true);
        // 收藏与历史先读回来:它们不依赖扫描,而用户可能一上来就想跳到某个收藏的键。
        await RestorePersistedStateAsync().ConfigureAwait(true);
        await ScanAsync(restart: true).ConfigureAwait(true);
    }

    /// <summary>
    /// 服务器自报的形态与连接配置里选的不一致时,把话说在最前面。
    /// <para>
    /// 这两种形态的扫描路径完全不同(集群按节点逐个扫,单机在当前库上扫),配错了的表现是
    /// **键树一片空白**而不是一条报错 —— 用户面对一个空列表根本无从判断是"库里没键"、
    /// "过滤条太窄"还是"形态选错了"。<c>INFO server</c> 的 <c>redis_mode</c> 已经把答案
    /// 摆在那里,不用它就是明知而不说。
    /// </para>
    /// </summary>
    private void WarnOnDeploymentMismatch()
    {
        bool serverSaysCluster = string.Equals(_connection.Info.Mode, "cluster", StringComparison.OrdinalIgnoreCase);
        bool profileSaysCluster = _connection.Settings.Deployment == RedisDeployment.Cluster;
        if (serverSaysCluster == profileSaysCluster)
        {
            return;
        }
        DeploymentWarning = serverSaysCluster
            ? Loc["Redis_ModeMismatchCluster"]
            : Loc["Redis_ModeMismatchStandalone"];
    }

    /// <summary>连接可用性变化(由文档转发)。</summary>
    /// <param name="available">是否可用。</param>
    public void OnAvailabilityChanged(bool available)
    {
        StatusMessage = available ? string.Empty : Loc["Redis_Disconnected"];
        if (available)
        {
            _ = MeasureLatencyAsync();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        StopScan();
        StopSampling();
        // 有确认框正开着就当作取消:不解开那个 TaskCompletionSource,
        // 等在它上面的那条写操作会永远挂着(而它握着连接)。
        Confirmation.Dismiss();
        _scanCts?.Dispose();
        _scanCts = null;
    }

    /// <summary>
    /// 扫一轮或多轮,直到游标归零、撞上预算上限,或用户按停。
    /// <para>
    /// **永不 <c>KEYS</c>**:即便只是想数一数总量也走 <c>DBSIZE</c>。自动扫描到预算上限就
    /// 停下并提示收窄条件,把"继续与否"交给用户 —— 而不是替他把生产库扫穿。
    /// </para>
    /// </summary>
    private async Task ScanAsync(bool restart)
    {
        if (_disposed)
        {
            return;
        }
        StopScan();
        _scanCts = new();
        int generation = ++_scanGeneration;
        CancellationToken token = _scanCts.Token;
        string pattern = BuildPattern();
        string? type = string.IsNullOrEmpty(TypeFilter) ? null : TypeFilter;
        if (restart)
        {
            Rows.Clear();
            Breadcrumb.Clear();
            _scanned.Clear();
            // 展开态也一起清:换了过滤条件之后,上一批的分组 id 大概率一个都不再出现,
            // 留着只会让集合无限长胖。
            _expandedGroups.Clear();
            _cursor = "0";
            _visited = 0;
            MatchedCount = 0;
            IsScanComplete = false;
            SelectedRow = null;
            RaisePropertyChanged(nameof(HasBreadcrumb));
            _totalKeys = await SafeDatabaseSizeAsync().ConfigureAwait(true);
        }

        IsScanning = true;
        StatusMessage = string.Empty;
        try
        {
            int budget = _connection.Settings.ScanBudget;
            while (!token.IsCancellationRequested && _visited < budget)
            {
                RedisScanPage page = await _connection.ScanAsync(_cursor, pattern, type, token).ConfigureAwait(true);
                _cursor = page.Cursor;
                _visited += page.Scanned;
                await MergeAsync(page.Keys, type, token).ConfigureAwait(true);
                if (page.IsComplete)
                {
                    IsScanComplete = true;
                    break;
                }
                RaisePropertyChanged(nameof(ScanStatus));
            }
        }
        catch (OperationCanceledException)
        {
            // 用户按了停,或换了过滤条件把上一轮取消掉了:不是错误。
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.Format("Redis_Error", ex.Message);
            _log.Error("Scanning the keyspace failed.", ex);
        }
        finally
        {
            // 只有最新那一轮才有资格改按钮状态(见 _scanGeneration 的说明)。
            if (generation == _scanGeneration)
            {
                IsScanning = false;
                RaisePropertyChanged(nameof(ScanStatus));
                RaisePropertyChanged(nameof(IsEmpty));
            }
        }
    }

    /// <summary>
    /// 把一页键并进列表:先去重登记,再批量补齐类型/TTL/规模,最后整份重排。
    /// <para>
    /// 每页三次往返(类型、TTL、长度),与页大小无关 —— 逐键单发才是那个把浏览器
    /// 拖慢十倍的写法。类型过滤开着时服务端已经筛过,类型那一趟省掉。
    /// </para>
    /// </summary>
    private async Task MergeAsync(IReadOnlyList<RedisKeyName> keys, string? typeFilter, CancellationToken token)
    {
        if (keys.Count == 0)
        {
            return;
        }
        var fresh = new List<RedisKeyName>(keys.Count);
        foreach (RedisKeyName key in keys)
        {
            // SCAN 在 rehash 期间会返回重复键,登记表本身就是去重的依据。
            if (_scanned.TryAdd(key, new(string.Empty, null, -1)))
            {
                fresh.Add(key);
            }
        }
        MatchedCount = _scanned.Count;
        if (fresh.Count == 0)
        {
            return;
        }
        IReadOnlyList<string> types = typeFilter is { Length: > 0 }
            ? [.. Enumerable.Repeat(typeFilter, fresh.Count)]
            : await _connection.TypesAsync(fresh, token).ConfigureAwait(true);
        IReadOnlyList<RedisKeyMeasure> measures =
            await _connection.MeasureAsync(fresh, types, token).ConfigureAwait(true);
        for (int i = 0; i < fresh.Count; i++)
        {
            _scanned[fresh[i]] = new(
                i < types.Count ? types[i] : string.Empty,
                i < measures.Count ? measures[i].Ttl : null,
                i < measures.Count ? measures[i].Length : -1);
        }
        RebuildRows();
    }

    /// <summary>
    /// 按当前已扫到的键整份重排列表。
    /// <para>
    /// 每页重排一次听起来奢侈,实际是 O(N log N) 的纯内存排序,几千个键上跑在毫秒级;
    /// 换来的是折叠规则**始终对得上当前这批键** —— 增量往一棵已折好的列表里插,
    /// 迟早会插出一个"本该折起来却散着"的中间态。行对象按 id 复用(见
    /// <see cref="RedisKeyLayout.Sync" />),所以选中项与滚动位置不受影响。
    /// </para>
    /// </summary>
    private void RebuildRows()
    {
        List<RedisKeyRow> desired = RedisKeyLayout.Build(
            _scanned.Keys, _connection.Settings.Delimiter, _connection.Settings.GroupThreshold, _expandedGroups);
        foreach (RedisKeyRow row in desired)
        {
            if (row.IsGroup)
            {
                row.GroupTip = Loc.Format("Redis_GroupTip", row.Count.ToString("N0", CultureInfo.CurrentCulture));
                continue;
            }
            if (row.Key is { } key && _scanned.TryGetValue(key, out RedisKeyMeta meta))
            {
                row.TypeName = meta.Type;
                // Describe 已经是窄列要的形状(30d / 2d 3h / 29:58);没有过期时间就一个破折号。
                row.TtlText = meta.Ttl is { } ttl ? RedisTtl.Describe(ttl) : Loc["Redis_TtlNone"];
                row.SizeText = FormatSize(meta.Type, meta.Length);
                // 五分钟内就要没的键值得一眼看见:对着一个马上消失的键做操作是白做。
                row.IsExpiringSoon = meta.Ttl is { TotalMinutes: < 5 };
            }
        }
        RedisKeyLayout.Sync(Rows, desired);
        UpdateBreadcrumb();
    }

    /// <summary>
    /// 把某个键在列表上露出来:必要时逐层展开挡着它的分组行,返回它那一行。
    /// <para>
    /// 选中一个折在分组里的键,用户什么也看不见 —— 所以"跳转到某个键"必须先把路让开。
    /// 循环有硬上限:每轮至少展开一条分组行,展不动就说明键真的不在已扫到的这批里。
    /// </para>
    /// </summary>
    /// <summary>
    /// 某个键被"露出来"了(跳到收藏的键)。视图据此把它滚进视野 ——
    /// 列表关掉了自动滚动(理由见 AXAML),所以需要这一下显式通知。
    /// </summary>
    public event EventHandler<RedisKeyRow>? KeyRevealed;

    /// <summary>触发 <see cref="KeyRevealed" />(供分部类调用)。</summary>
    /// <param name="row">要滚进视野的行。</param>
    private void RaiseKeyRevealed(RedisKeyRow row) => KeyRevealed?.Invoke(this, row);

    /// <param name="keyDisplay">键的显示形式(转义后)。</param>
    /// <returns>该键所在的行;不在已扫结果里时为 <see langword="null" />。</returns>
    private RedisKeyRow? RevealKey(string keyDisplay)
    {
        for (int guard = 0; guard <= 64; guard++)
        {
            if (Rows.FirstOrDefault(row => row.Key is { } key
                                           && string.Equals(key.Display, keyDisplay, StringComparison.Ordinal))
                is { } found)
            {
                return found;
            }
            // 找一条"键就在它底下"的折叠分组行展开;没有就到头了。
            RedisKeyRow? blocking = Rows.FirstOrDefault(row =>
                row is { IsGroup: true, IsExpanded: false }
                && keyDisplay.StartsWith(row.Display[..^1], StringComparison.Ordinal));
            if (blocking is null)
            {
                return null;
            }
            _expandedGroups.Add(blocking.Id);
            RebuildRows();
        }
        return null;
    }

    /// <summary>展开/折叠一条分组行,并就地重排。</summary>
    /// <param name="row">分组行;传键行时什么都不做。</param>
    public void ToggleGroup(RedisKeyRow? row)
    {
        if (row is not { IsGroup: true })
        {
            return;
        }
        if (!_expandedGroups.Remove(row.Id))
        {
            _expandedGroups.Add(row.Id);
        }
        RebuildRows();
    }

    private void UpdateBreadcrumb()
    {
        IReadOnlyList<string> segments = RedisKeyLayout.Breadcrumb(_scanned.Keys, _connection.Settings.Delimiter);
        Breadcrumb.Clear();
        string delimiter = _connection.Settings.Delimiter;
        var path = new System.Text.StringBuilder();
        foreach (string segment in segments)
        {
            path.Append(segment).Append(delimiter);
            Breadcrumb.Add(new(segment, path.ToString()));
        }
        RaisePropertyChanged(nameof(HasBreadcrumb));
    }

    /// <summary>
    /// 跳到面包屑的某一段:把过滤条设成该前缀并重扫。
    /// <para>下钻复用过滤条而不是另立一套导航状态 —— 否则"我现在看到的是哪一批键"
    /// 就有了两个互相打架的来源,而回显那行小字只认得其中一个。</para>
    /// </summary>
    /// <param name="segment">面包屑段;<see langword="null" /> 表示回到"全部"。</param>
    /// <returns>表示异步操作的任务。</returns>
    public Task NavigateToAsync(RedisBreadcrumbSegment? segment)
    {
        Filter = segment?.Prefix ?? string.Empty;
        return ApplyMatchModeAsync(RedisMatchMode.Prefix);
    }

    /// <summary>规模一列的文案:字符串给字节数,集合类给元素个数,未知给空。</summary>
    private string FormatSize(string type, long length)
    {
        if (length < 0)
        {
            return string.Empty;
        }
        if (type is "string")
        {
            return length switch
            {
                < 1024 => Loc.Format("Redis_SizeBytes", length.ToString("N0", CultureInfo.CurrentCulture)),
                < 1024 * 1024 => $"{length / 1024.0:0.#} KB",
                _ => $"{length / (1024.0 * 1024):0.#} MB"
            };
        }
        return Loc.Format("Redis_SizeItems", length.ToString("N0", CultureInfo.CurrentCulture));
    }

    /// <summary>一个已扫到的键的元数据(挂在登记表上,不挂在会被重排换掉的行对象上)。</summary>
    /// <param name="Type">类型名。</param>
    /// <param name="Ttl">剩余存活时间。</param>
    /// <param name="Length">元素个数 / 字节数;未知为 -1。</param>
    private readonly record struct RedisKeyMeta(string Type, TimeSpan? Ttl, long Length);

    private void StopScan()
    {
        CancellationTokenSource? cts = _scanCts;
        _scanCts = null;
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
            // 已经被上一次停掉了。
        }
        cts.Dispose();
    }

    private async Task LoadKeyAsync(RedisKeyName key)
    {
        IsLoadingDetail = true;
        try
        {
            RedisKeyInfo info = await _connection.DescribeAsync(key, includeMemory: false).ConfigureAwait(true);
            Selected = info;
            Elements.Clear();
            _valueBytes = [];
            CanUseTextFormat = true;
            ValueFormat = RedisValueFormat.Text;
            StringValue = string.Empty;
            TruncationNotice = string.Empty;
            PageStatus = string.Empty;
            HasMoreElements = false;
            _elementCursor = "0";
            if (info.IsGone)
            {
                // 查看期间过期是**正常生命周期**,不是故障 —— 就地说明,不弹错误弹窗。
                StatusMessage = Loc["Redis_KeyGone"];
                return;
            }
            StatusMessage = string.Empty;
            if (info.Type is "string")
            {
                RedisStringValue value = await _connection.ReadStringAsync(key).ConfigureAwait(true);
                // 原始字节留着 —— 它才是真相,界面上那段文本只是它的一种渲染。
                _valueBytes = value.Bytes;
                CanUseTextFormat = RedisValueText.IsTextSafe(_valueBytes);
                ValueFormat = RedisValueText.Detect(_valueBytes);
                StringValue = RedisValueText.Render(_valueBytes, ValueFormat);
                if (value.IsTruncated)
                {
                    TruncationNotice = Loc.Format("Redis_Truncated",
                        Approx(value.Bytes.LongLength), Approx(value.TotalLength));
                }
                ResetEditingForSelection();
                return;
            }
            if (IsCollectionSelected)
            {
                await LoadMoreElementsAsync().ConfigureAwait(true);
            }
            else
            {
                StatusMessage = Loc.Format("Redis_UnsupportedType", info.Type);
            }
            ResetEditingForSelection();
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.Format("Redis_Error", ex.Message);
            _log.Error($"Reading key '{key.Display}' failed.", ex);
        }
        finally
        {
            IsLoadingDetail = false;
        }
    }

    private string _elementCursor = "0";

    private async Task LoadMoreElementsAsync()
    {
        if (Selected is not { } info || info.IsGone || info.Key is null)
        {
            return;
        }
        IsLoadingDetail = true;
        try
        {
            RedisElementPage page = await _connection
                .ReadElementsAsync(info.Key, info.Type, _elementCursor, PageSize)
                .ConfigureAwait(true);
            foreach (RedisElement row in page.Rows)
            {
                Elements.Add(new(row));
            }
            _elementCursor = page.Cursor;
            HasMoreElements = !page.IsComplete;
            PageStatus = page.Total >= 0
                ? Loc.Format("Redis_PageStatus", Elements.Count.ToString("N0", CultureInfo.CurrentCulture), Approx(page.Total))
                : Elements.Count.ToString("N0", CultureInfo.CurrentCulture);
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.Format("Redis_Error", ex.Message);
            _log.Error($"Reading elements of '{info.Key?.Display}' failed.", ex);
        }
        finally
        {
            IsLoadingDetail = false;
        }
    }

    private async Task ApplyMatchModeAsync(RedisMatchMode mode)
    {
        MatchMode = mode;
        await ScanAsync(restart: true).ConfigureAwait(true);
    }

    private async Task MeasureLatencyAsync()
    {
        try
        {
            TimeSpan rtt = await _connection.PingAsync().ConfigureAwait(true);
            LatencyMs = (int)Math.Round(rtt.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            LatencyMs = -1;
            _log.Info($"PING failed: {ex.Message}");
        }
    }

    private async Task<long> SafeDatabaseSizeAsync()
    {
        try
        {
            return await _connection.DatabaseSizeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // DBSIZE 被禁不是故障:进度条退化成只报"已匹配多少"。
            _log.Info($"DBSIZE unavailable: {ex.Message}");
            return -1;
        }
    }

    private void BuildDatabases()
    {
        Databases.Clear();
        if (!SupportsDatabases)
        {
            Databases.Add(new(0, _connection.Info.KeyCountByDatabase.GetValueOrDefault(0, -1)) { IsSelected = true });
        }
        else
        {
            for (int i = 0; i < _connection.Info.Databases; i++)
            {
                long count = _connection.Info.KeyCountByDatabase.TryGetValue(i, out long known) ? known : 0;
                Databases.Add(new(i, count) { IsSelected = i == CurrentDatabase });
            }
        }
        // 初值直接赋:setter 里"目标库 == 当前库"会提前返回,所以这一步不会触发一次多余的重扫。
        SelectedDatabase = Databases.FirstOrDefault(option => option.Index == CurrentDatabase)
                           ?? Databases.FirstOrDefault();
    }

    /// <summary>生成 <c>MATCH</c> 模式(转换规则与转义见 <see cref="RedisMatchPattern" />)。</summary>
    private string BuildPattern() => RedisMatchPattern.Build(MatchMode, Filter);

    private string Percent()
    {
        if (_totalKeys <= 0)
        {
            return "?";
        }
        double ratio = Math.Min(1.0, (double)_visited / _totalKeys);
        return ratio.ToString("P1", CultureInfo.CurrentCulture);
    }

    private static string Approx(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private const int PageSize = 200;
}

/// <summary>
/// 集合类值的一行(界面用)。把领域模型 <see cref="RedisElement" /> 的
/// <c>double?</c> 分值转成可直接绑定的文本 —— 视图里不该出现格式化逻辑,
/// 而 <c>null</c> 分值在 XAML 里会渲染成空白还是 "0" 取决于转换器,不确定的东西不留给视图。
/// </summary>
/// <param name="element">领域模型行。</param>
public sealed class RedisElementRow(RedisElement element)
{
    /// <summary>字段名 / 索引 / 成员 / 流条目 id。</summary>
    public string Label { get; } = element.Label;

    /// <summary>值。</summary>
    public string Value { get; } = element.Value;

    /// <summary>分值的显示形式;非有序集合为空串。</summary>
    public string ScoreText { get; } = element.Score is { } score
        ? score.ToString("0.############", CultureInfo.CurrentCulture)
        : string.Empty;
}

/// <summary>数据库下拉里的一项。带键数,省掉"逐个库点进去看有没有东西"的盲测。</summary>
/// <param name="index">库序号。</param>
/// <param name="keyCount">已知键数;未知时为 -1。</param>
public sealed class RedisDatabaseOption(int index, long keyCount) : ObservableObject
{
    /// <summary>库序号。</summary>
    public int Index { get; } = index;

    /// <summary>显示文本(<c>db0 (1.2M)</c> / <c>db1</c>)。</summary>
    public string Display => keyCount switch
    {
        < 0 => $"db{Index}",
        0 => $"db{Index}",
        < 1000 => $"db{Index} ({keyCount})",
        < 1_000_000 => $"db{Index} ({keyCount / 1000.0:0.#}k)",
        _ => $"db{Index} ({keyCount / 1_000_000.0:0.#}M)"
    };

    /// <summary>是否为当前库。</summary>
    public bool IsSelected
    {
        get;
        set => SetProperty(ref field, value);
    }
}

/// <summary>
/// 视图模型对日志的最小依赖。直接引 <c>IPluginLogger</c> 也行,
/// 但那会让单测必须造一个上下文;一个两方法的门面就够。
/// </summary>
internal interface IPluginLoggerFacade
{
    /// <summary>信息。</summary>
    /// <param name="message">消息。</param>
    void Info(string message);

    /// <summary>错误。</summary>
    /// <param name="message">消息。</param>
    /// <param name="error">异常。</param>
    void Error(string message, Exception? error = null);
}

/// <summary>把 SDK 的日志能力适配成 <see cref="IPluginLoggerFacade" />。</summary>
/// <param name="log">SDK 日志。</param>
internal sealed class PluginLoggerFacade(PluginSdk.Logging.IPluginLogger log) : IPluginLoggerFacade
{
    /// <inheritdoc />
    public void Info(string message) => log.Info(message);

    /// <inheritdoc />
    public void Error(string message, Exception? error = null) => log.Error(message, error);
}

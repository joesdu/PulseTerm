namespace VelaShell.Plugin.Redis.Ui;

/// <summary>
/// 键列表上的一行。两种形态:**一个真实的键**,或**一条把同前缀的一批键折起来的分组行**。
/// <para>
/// 为什么是扁平列表而不是树:Redis 的键是**扁平的字节串**,<c>:</c> 只是一条书写约定。
/// 把约定画成树,界面就在陈述一件服务器从没说过的事 —— 而且树上每行只有本层片段,
/// 用户看不到自己正在看哪个键,复制键名还得靠脑子拼路径。列表一行一个完整键名,
/// 与 redis-cli 里所见、与代码里写的完全一致;缩进省下来的宽度正好给 TTL 与规模两列。
/// </para>
/// <para>
/// 分组行是把"形状感"补回来的那一手:同前缀的键多到成为噪音时(默认 ≥8)折成一行,
/// 点开**就地展开**而不是嵌套下钻。少量同前缀的键一律平铺 —— 折叠的目的是压噪音,
/// 不是制造点击。
/// </para>
/// </summary>
public sealed class RedisKeyRow : ObservableObject
{
    private RedisKeyRow(string id, string display, int depth)
    {
        Id = id;
        Display = display;
        Depth = depth;
    }

    /// <summary>
    /// 行的稳定标识。列表每来一页 SCAN 就重排一次,靠它把新旧两份行对上 ——
    /// 对上了就复用**同一个对象**,选中状态、已取到的类型/TTL/规模因此都不会在扫描中途丢掉。
    /// </summary>
    public string Id { get; }

    /// <summary>显示文本:键行是完整键名(转义后),分组行是 <c>前缀*</c>。</summary>
    public string Display { get; }

    /// <summary>缩进层级。只有"展开某个分组后露出来的成员"才 &gt; 0。</summary>
    public int Depth { get; }

    /// <summary>
    /// 左侧缩进。每层 24px —— 与宿主资源管理器一致(那里分组行缩进 12、组内会话行缩进 36)。
    /// 模板直接绑它,免得在 XAML 里做算术。
    /// </summary>
    public Avalonia.Thickness Indent => new(Depth * 24, 0, 0, 0);

    /// <summary>键行的键名;分组行为 <see langword="null" />。</summary>
    public RedisKeyName? Key { get; private init; }

    /// <summary>这一行是不是一个真实的键。</summary>
    public bool IsKey => Key is not null;

    /// <summary>这一行是不是分组行。</summary>
    public bool IsGroup => Key is null;

    /// <summary>
    /// 分组行折进去的键数(**已扫描到的**,不是服务器上的总数)。
    /// 扫描每来一页就会长,所以它是可变的 —— 行对象在重排时按 id 复用,值要跟着更新。
    /// </summary>
    public int Count
    {
        get;
        set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(CountText));
        }
    }

    /// <summary>计数的显示形式(上千折成 <c>1.2k</c>,列宽才不会被大数撑开)。</summary>
    public string CountText => Count switch
    {
        < 1 => string.Empty,
        < 1000 => Count.ToString(System.Globalization.CultureInfo.CurrentCulture),
        < 1_000_000 => $"{Count / 1000.0:0.#}k",
        _ => $"{Count / 1_000_000.0:0.#}M"
    };

    /// <summary>分组行是否已展开。</summary>
    public bool IsExpanded
    {
        get;
        set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(IsCollapsed));
            RaisePropertyChanged(nameof(IsExpandedGroup));
        }
    }

    /// <summary>
    /// 分组行处于折叠态。与 <see cref="IsExpanded" /> 各驱动一个箭头图标的可见性 ——
    /// 与宿主资源管理器同一套做法(那里也是 chevron-down / chevron-right 两个图标切换,
    /// 而不是旋转一个)。
    /// </summary>
    public bool IsCollapsed => IsGroup && !IsExpanded;

    /// <summary>分组行处于展开态(键行恒为 false,免得模板里再判一次)。</summary>
    public bool IsExpandedGroup => IsGroup && IsExpanded;

    /// <summary>
    /// 分组行计数的悬停提示。**必须写明这是"已扫描到的"** —— SCAN 是增量的,
    /// 而一个折起来写着 40 的行看上去就像 <c>DBSIZE</c> 那样确定。
    /// 由视图模型在建行时填(行对象不认识文案表)。
    /// </summary>
    public string GroupTip
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>类型名(键行;未探测到时为空串)。</summary>
    public string TypeName
    {
        get;
        set
        {
            SetProperty(ref field, value);
            foreach (string flag in TypeFlags)
            {
                RaisePropertyChanged(flag);
            }
        }
    } = string.Empty;

    private static readonly string[] TypeFlags =
        [nameof(IsString), nameof(IsHash), nameof(IsList), nameof(IsSet), nameof(IsSortedSet), nameof(IsStream)];

    // 六个类型各给一个布尔,供样式选择器上色。
    //
    // 一列全是同色徽章等于没有信息 —— 40 个 string 排下来,眼睛只能逐行读文字。
    // 按类型分色之后这一列才真正可扫:混合类型的库一眼就看得出哪几行是 hash、哪几行是 zset。
    // 用布尔类而不是转换器,是因为 Avalonia 的 Classes 不接受字符串绑定,
    // 而 ConverterParameter 不能是绑定(这个面板别处已经踩过一次)。

    /// <summary>类型是 string。</summary>
    public bool IsString => TypeName is "string";

    /// <summary>类型是 hash。</summary>
    public bool IsHash => TypeName is "hash";

    /// <summary>类型是 list。</summary>
    public bool IsList => TypeName is "list";

    /// <summary>类型是 set。</summary>
    public bool IsSet => TypeName is "set";

    /// <summary>类型是 zset。</summary>
    public bool IsSortedSet => TypeName is "zset";

    /// <summary>类型是 stream。</summary>
    public bool IsStream => TypeName is "stream";

    /// <summary>TTL 一列的文案;无过期时间时是一个破折号。</summary>
    public string TtlText
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>规模一列的文案:字符串是字节数,集合类是元素个数。</summary>
    public string SizeText
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>快过期(&lt; 5 分钟):这一列改用警示色,免得用户对着一个马上要消失的键做操作。</summary>
    public bool IsExpiringSoon
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>造一个键行。</summary>
    /// <param name="key">键名。</param>
    /// <param name="depth">缩进层级。</param>
    /// <returns>键行。</returns>
    public static RedisKeyRow ForKey(RedisKeyName key, int depth = 0)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new("k:" + key.Display, key.Display, depth) { Key = key };
    }

    /// <summary>造一条分组行。</summary>
    /// <param name="prefix">折起来的公共前缀(不含结尾的 <c>*</c>)。</param>
    /// <param name="count">折进去的键数。</param>
    /// <param name="depth">缩进层级。</param>
    /// <returns>分组行。</returns>
    public static RedisKeyRow ForGroup(string prefix, int count, int depth = 0)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        // id 里带上 depth:同一个前缀在"折叠态"与"展开后的子分组"里是两行不同的东西。
        return new($"g:{depth}:{prefix}", prefix + "*", depth) { Count = count };
    }

    /// <summary>
    /// 把同 id 的新算结果整份搬过来。
    /// <para>
    /// **新算的那一份永远是真相**,旧对象只用来保住身份(选中项、滚动位置)。
    /// 只搬一部分的话就会出现"分组行计数涨到了 40、悬停提示还写着 2 个"这种
    /// 半新半旧的状态 —— 而且只在扫描跨页时才复现。
    /// </para>
    /// </summary>
    /// <param name="fresh">新算出来的同 id 行。</param>
    internal void AdoptFrom(RedisKeyRow fresh)
    {
        ArgumentNullException.ThrowIfNull(fresh);
        Count = fresh.Count;
        IsExpanded = fresh.IsExpanded;
        TypeName = fresh.TypeName;
        TtlText = fresh.TtlText;
        SizeText = fresh.SizeText;
        IsExpiringSoon = fresh.IsExpiringSoon;
        GroupTip = fresh.GroupTip;
    }
}

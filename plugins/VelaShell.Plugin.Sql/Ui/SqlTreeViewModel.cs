using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Data.Common;
using VelaShell.Plugin.Sql.Metadata;

namespace VelaShell.Plugin.Sql.Ui;

/// <summary>对象树上的一个节点。</summary>
public sealed class SqlTreeNode : ObservableObject
{
    private readonly Func<SqlTreeNode, CancellationToken, Task<IReadOnlyList<SqlTreeNode>>>? _load;

    /// <summary>
    /// **未经过滤**的全部子节点。<see cref="Children" /> 是它按过滤词投影出来的可见部分。
    /// <para>
    /// 两份分开存是过滤能"活着"的前提:早先只有一份 <c>Children</c>,过滤词只在**装载那一刻**
    /// 参与筛选(<c>LoadRelationsAsync</c> 里的一句 <c>Where</c>),于是改过滤词对已经展开的节点
    /// 一点反应都没有 —— 那个输入框看起来能用,实际上只对"改完再展开"的那次生效。
    /// </para>
    /// </summary>
    private readonly List<SqlTreeNode> _all = [];

    private bool _expanded;
    private bool _loading;
    private bool _loaded;
    private string _countText = "";

    /// <summary>
    /// 这个节点当前生效的过滤词。
    /// <para>
    /// 挂在**实例**上而不是一个静态字段:一个 VelaShell 里可以同时开着几条数据库连接,
    /// 静态字段会让在 A 连接里敲的过滤词悄悄作用到 B 连接的树上 ——
    /// 而且那种串扰只在"两个文档同时开着"时出现,单开时怎么点都是对的。
    /// </para>
    /// <para>
    /// 值由父节点在 <see cref="Project" /> 里往下推:懒加载与过滤是异步交错的,
    /// 一个节点可能在过滤词已经敲下之后才装载完,那时它得知道现在按什么筛。
    /// </para>
    /// </summary>
    private string _filter = "";

    internal SqlTreeNode(
        string title,
        SqlNodeKind kind,
        SqlObject? target = null,
        Func<SqlTreeNode, CancellationToken, Task<IReadOnlyList<SqlTreeNode>>>? load = null,
        string schema = "",
        string database = "",
        bool isSystem = false,
        string detail = "",
        bool isCurrent = false,
        IReadOnlyList<SqlTreeNode>? children = null)
    {
        Title = title;
        Kind = kind;
        Target = target;
        Schema = schema;
        Database = database;
        IsSystem = isSystem;
        Detail = detail;
        IsCurrent = isCurrent;
        _load = load;
        if (children is not null)
        {
            // 子节点在建这个节点的那一刻就已经在手上了(系统对象分组就是这样:
            // 它装的东西与外面那批是同一次查询的结果)。**直接当成"已装载"**,
            // 而不是挂一个假的加载器 —— 那样过滤穿不透它、计数也说不出来,
            // 而它明明什么都不用再查。
            _all.AddRange(children);
            _loaded = true;
            Project("");
        }
        else if (load is not null)
        {
            // 有加载器 = 可展开。先塞一个占位子节点,树控件才会画出展开箭头 ——
            // 否则用户看到的是一个"没有子项"的叶子,而其实里面有 5000 张表。
            _all.Add(Placeholder);
            Children.Add(_all[0]);
        }
    }

    private static SqlTreeNode Placeholder => new("…", SqlNodeKind.Placeholder);

    /// <summary>显示文本。</summary>
    public string Title { get; }

    /// <summary>节点类别(界面据此选图标)。</summary>
    public SqlNodeKind Kind { get; }

    /// <summary>对应的数据库对象;分类节点为 <see langword="null" />。</summary>
    internal SqlObject? Target { get; }

    /// <summary>所属 schema。</summary>
    internal string Schema { get; }

    /// <summary>
    /// 这个节点所在的**库**(catalog)。
    /// <para>
    /// PG 与 SQL Server 上,库这一级<b>无法用限定名表达</b>(两段名只到 schema),
    /// 只能落在连接上 —— 所以每个节点必须一路把自己在哪个库带下来,
    /// 元数据查询与"打开数据"才知道该用哪条连接。这一格缺失正是
    /// "每个库都点得开、每个库都是空的"的直接成因。
    /// </para>
    /// </summary>
    internal string Database { get; }

    /// <summary>
    /// 是不是服务端自带的对象(系统库 / 系统 schema / 目录表 / 自带例程)。
    /// <para>树据此把它们收进"系统对象"分组,而不是与用户对象按字母序混排。</para>
    /// </summary>
    public bool IsSystem { get; }

    /// <summary>
    /// 这是不是**当前连接落脚**的那个库 / schema。界面上加粗,让"我现在在哪"一眼可见。
    /// </summary>
    public bool IsCurrent { get; }

    /// <summary>右侧的次要说明(注释)。空串表示没有。</summary>
    public string Detail { get; }

    /// <summary>有没有次要说明。</summary>
    public bool HasDetail => Detail.Length > 0;

    /// <summary>子节点(**已按过滤词投影**)。</summary>
    public ObservableCollection<SqlTreeNode> Children { get; } = [];

    /// <summary>
    /// 这个节点能不能「打开数据 / 查看结构」。
    /// <para>
    /// 判定**必须落在 <see cref="Kind" /> 上**,不能只看 <c>Target</c> 有没有 ——
    /// 库节点与 schema 节点同样带着 <c>Target</c>。曾经的写法是"有 Target 且不是分类/列",
    /// 于是双击库节点 <c>ops_pg</c> 拼出 <c>SELECT * FROM "ops_pg"</c>,PG 回 42P01;
    /// 双击 Oracle 的 schema 节点 <c>SYSBACKUP</c> 拼出 <c>SELECT * FROM "SYSBACKUP"</c>,
    /// Oracle 回 ORA-00942。库名 / schema 名被当成表名拼进了 <c>FROM</c>。
    /// </para>
    /// <para>
    /// 物化视图算在内:<c>SELECT</c> 得动、结构也查得出,对用户来说它就是一种视图。
    /// <b>例程与序列不算</b>:<c>SELECT * FROM 某个函数</c> 只在个别方言的个别形态下成立,
    /// 而序列点开是 <c>last_value/log_cnt/is_called</c> 三列 —— 与"打开这张表的数据"
    /// 完全不是一回事,画成同一个动作只会让人点错。
    /// </para>
    /// </summary>
    public bool CanOpenData =>
        Target is not null && Kind is SqlNodeKind.Table or SqlNodeKind.View or SqlNodeKind.MaterializedView;

    /// <summary>
    /// 有没有懒加载器 —— 也就是这个节点能不能展开。
    /// <para>
    /// 双击一个不能"打开数据"的节点时靠它决定是展开收起还是彻底不动:
    /// 库 / schema / 分类双击的正确含义是展开,把它们一起挡成"什么都不做"是另一种坏。
    /// </para>
    /// </summary>
    public bool CanExpand => _load is not null || _all.Count > 0;

    /// <summary>
    /// 这个节点自己**重装得了**吗(F5 该不该落在它身上)。
    /// <para>
    /// 与 <see cref="CanExpand" /> 分开是必须的:系统对象分组的子节点建好就在手上、没有加载器,
    /// 于是 <c>CanExpand</c> 为真(它确实展得开)而 <see cref="RefreshAsync" /> 是空操作。
    /// 树那一层的 F5 若按 <c>CanExpand</c> 判,它就从"自己重装"与"全树兜底"两边的缝里漏过去 ——
    /// 按下去什么都不发生,而这正是本文件反复反对的那种"看起来在转、实际什么都没查"。
    /// </para>
    /// </summary>
    internal bool CanReload => _load is not null;

    /// <summary>
    /// 计数后缀。**只在展开过之后才有值** ——
    /// 展开前显示估算值是撒谎,显示光秃秃的"表"又少了信息,所以是"表 …"→展开后变"表 (37)"(§7.2)。
    /// <para>数的是**当前可见**的子节点:过滤时显示的是筛出来的那个数,而不是一个对不上的总数。</para>
    /// </summary>
    public string CountText
    {
        get => _countText;
        private set => SetProperty(ref _countText, value);
    }

    /// <summary>正在加载。</summary>
    public bool IsLoading
    {
        get => _loading;
        private set => SetProperty(ref _loading, value);
    }

    /// <summary>展开状态。树控件双向绑定它,展开时触发懒加载。</summary>
    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            SetProperty(ref _expanded, value);
            if (value && !_loaded && !_loading)
            {
                _ = LoadAsync(CancellationToken.None);
            }
        }
    }

    /// <summary>加载子节点(懒加载 + 显式刷新都走这里)。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (_load is null)
        {
            return;
        }
        IsLoading = true;
        try
        {
            IReadOnlyList<SqlTreeNode> children = await _load(this, cancellationToken).ConfigureAwait(true);
            _all.Clear();
            _all.AddRange(children);
            _loaded = true;
        }
        catch (Exception ex)
        {
            // 加载失败要**说出来**,而不是留一棵看起来空的树 ——
            // 空树和"这个库真的没有表"长得一模一样(§7.8)。
            _all.Clear();
            _all.Add(new(ex.Message, SqlNodeKind.Error));
            _loaded = false;
        }
        finally
        {
            IsLoading = false;
            // 装载完成后立刻按当前过滤词投影一次:用户可能在等待期间就把过滤词敲进去了。
            Project(_filter);
        }
    }

    /// <summary>强制重新加载(F5)。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        // 没有加载器的节点(子节点建好就在手上)刷不出新东西,把 _loaded 清掉只会
        // 让它的计数凭空消失。它的内容由**父节点**重新装载时一并换掉。
        if (_load is null)
        {
            return Task.CompletedTask;
        }
        _loaded = false;
        return LoadAsync(cancellationToken);
    }

    /// <summary>把过滤词递归投影下去(自底向上:先让孩子筛完,父节点才知道自己还剩不剩内容)。</summary>
    /// <param name="term">过滤词。</param>
    internal void ApplyFilter(string term)
    {
        _filter = term;
        foreach (SqlTreeNode child in _all)
        {
            child.ApplyFilter(term);
        }
        Project(term);
    }

    /// <summary>
    /// 这个节点在当前过滤词下该不该出现。
    /// <para>
    /// 只有**对象叶子**参与匹配。分类 / 库 / schema 这些容器不按名字筛:
    /// 用户敲 <c>order</c> 是想找 <c>orders</c> 表,不是想让"表"这个分类消失。
    /// 容器的去留由"筛完还剩不剩东西"决定 —— 但**没展开过的容器一律留着**,
    /// 否则一敲字整棵树就空了(里面有什么还没查过,凭什么说它不匹配)。
    /// </para>
    /// </summary>
    /// <param name="term">过滤词。</param>
    /// <returns>可见与否。</returns>
    internal bool MatchesFilter(string term)
    {
        if (string.IsNullOrEmpty(term))
        {
            return true;
        }
        if (IsFilterableLeaf)
        {
            return Title.Contains(term, StringComparison.OrdinalIgnoreCase);
        }
        return !_loaded || Children.Count > 0;
    }

    /// <summary>参与名字匹配的节点类别。</summary>
    private bool IsFilterableLeaf => Kind is SqlNodeKind.Table or SqlNodeKind.View or SqlNodeKind.MaterializedView
        or SqlNodeKind.Procedure or SqlNodeKind.Function or SqlNodeKind.Sequence;

    private void Project(string term)
    {
        _filter = term;
        Children.Clear();
        foreach (SqlTreeNode child in _all)
        {
            if (child.Kind == SqlNodeKind.SystemGroup)
            {
                // **系统对象分组是唯一"生下来就已装载"的孩子**(构造里就 `_loaded = true`
                // 并按空串投影过一次),所以只推 `_filter` 推不动它 —— 它的 `Children`
                // 还是那份没筛过的全量,于是 `MatchesFilter` 的容器分支恒真、分组恒可见,
                // 计数也会把全量加进来。
                //
                // 走的路径是「**先敲过滤词、后展开**」(与 SqlTreeGuardTests 里那条正好反序)。
                // 它得当场按新词重筛。Group() 不会把分组套进分组,所以这层递归只有一层。
                child.ApplyFilter(term);
            }
            else
            {
                // 往下推一格:刚装载出来的孩子还不知道现在的过滤词是什么,
                // 而它自己装载完之后要用这个值去筛自己的孩子。
                child._filter = term;
            }
            if (child.MatchesFilter(term))
            {
                Children.Add(child);
            }
        }
        // **数的是对象,不是行。** 两条:
        //
        // ① 系统对象分组自己占一行,但它代表的是里面那一批 —— 把它算成 1 会让
        //    "表 (3)"在 2 张用户表 + 1 张系统表时刚好也是 3。一个碰巧对上的数字
        //    比一个明显错的数字更难发现。
        //
        // ② **分类节点不算数。** schema 底下挂的是「表 / 视图 / 存储过程与函数 / 序列」
        //    四个抽屉,给 public 标一个 "(4)" 说的是"有四个抽屉",而读的人一定会
        //    理解成"这个 schema 里有 4 个对象" —— 真机上那个 schema 有 10 张表。
        //    抽屉本身各自会报自己的数,这一层报总数只会是一句误导。
        if (Children.Any(c => c.Kind == SqlNodeKind.Category))
        {
            CountText = "";
            return;
        }
        int objects = 0;
        foreach (SqlTreeNode child in Children)
        {
            objects += child.Kind == SqlNodeKind.SystemGroup ? child.Children.Count : 1;
        }
        CountText = _loaded && objects > 0 ? $"({objects})" : "";
    }
}

/// <summary>树节点类别。</summary>
public enum SqlNodeKind
{
    /// <summary>占位(未展开)。</summary>
    Placeholder,

    /// <summary>数据库。</summary>
    Database,

    /// <summary>Schema。</summary>
    Schema,

    /// <summary>分类(表 / 视图 / …)。</summary>
    Category,

    /// <summary>
    /// 系统对象分组 —— 把服务端自带的库 / schema / 表收在一起的那个折叠节点。
    /// </summary>
    SystemGroup,

    /// <summary>表。</summary>
    Table,

    /// <summary>视图。</summary>
    View,

    /// <summary>物化视图。</summary>
    MaterializedView,

    /// <summary>存储过程。</summary>
    Procedure,

    /// <summary>函数。</summary>
    Function,

    /// <summary>序列。</summary>
    Sequence,

    /// <summary>列。</summary>
    Column,

    /// <summary>加载失败。</summary>
    Error
}

/// <summary>
/// 对象清单的缓存键。
/// <para>
/// <b>刻意用记录而不是拼一个字符串。</b> 第一版拼的是"桶 + 分隔符 + 库 + 分隔符 + schema",
/// 而作废那一侧按另一个分隔符去后缀匹配 —— 两处对不上,于是 F5 清不掉缓存里的任何一条,
/// 树上一直是旧清单。这类错编译期看不出来、跑起来也不报错,表现只是"F5 没反应",
/// 而用户会据此认定"库里真的没有那张新表"。
/// 记录的相等性由编译器生成,压根没有分隔符这回事。
/// </para>
/// <para>库名与 schema 名按<b>原样</b>比较:它们来自同一批节点字段,不存在大小写分叉。</para>
/// </summary>
/// <param name="Group">类别桶(表与视图共用一个)。</param>
/// <param name="Database">库。</param>
/// <param name="Schema">schema。</param>
internal readonly record struct SqlObjectKey(SqlObjectGroup Group, string Database, string Schema);

/// <summary>一个分类节点要装什么。</summary>
internal enum SqlObjectGroup
{
    /// <summary>表(含分区表)。</summary>
    Table,

    /// <summary>视图与物化视图。</summary>
    View,

    /// <summary>存储过程与函数。</summary>
    Routine,

    /// <summary>序列。</summary>
    Sequence
}

/// <summary>
/// 对象树。
/// <para>
/// 四条纪律(前三条来自设计文档 §7.2,第四条是这一轮真机复盘补上的):
/// ① **懒加载**——一个 5000 张表的库,一次性拉全表清单会让展开动作卡两秒;
/// ② **计数只在展开后显示**——展开前给估算值是撒谎;
/// ③ **永不自动轮询**——线上库的系统表查询本身就有代价;
/// ④ **系统对象与用户对象永不混排**——它们归进各自层级下的"系统对象"分组。
/// </para>
/// <para>
/// 还有一条没写在 §7.2 但同样是实测逼出来的:**元数据一律走方言包**,
/// 一个 <c>IDbMaintenance</c> 方法都不调 —— 它在四台真机上到处说谎(§2.3)。
/// </para>
/// </summary>
public sealed class SqlTreeViewModel : ObservableObject
{
    private readonly SqlSession _session;
    private readonly Loc _loc;

    /// <summary>
    /// 按 (类别, 库, schema) 缓存的对象清单。
    /// <para>
    /// <b>这一层不是为了省网络,是为了不把同一条查询发两遍。</b>
    /// "表"与"视图"是同一份 <see cref="IDialectPack.ListRelationsAsync" /> 结果的两个投影
    /// (方言包刻意做成一次查完,§7.2 的计数要求),而早先每个分类节点各自去查一次 ——
    /// 展开一个 schema 的表和视图,同一条系统表查询就发了两遍。
    /// </para>
    /// <para>
    /// <see cref="Lazy{T}" /> 的理由与 <c>SqlSession</c> 的库连接池一样:
    /// 树的展开是即发即忘的,<c>GetOrAdd</c> 的工厂在并发下会被调用多次。
    /// </para>
    /// </summary>
    private readonly ConcurrentDictionary<SqlObjectKey, Lazy<Task<IReadOnlyList<SqlObject>>>> _objects = new();

    /// <summary>未经过滤的根节点。<see cref="Roots" /> 是它的可见投影,理由见 <c>SqlTreeNode._all</c>。</summary>
    private readonly List<SqlTreeNode> _allRoots = [];

    private string _filter = "";
    private int _queries;

    /// <summary>
    /// 这棵树累计发出去的**对象清单查询**条数。
    /// <para>
    /// 存在的理由是它能被断言。"表与视图共用一次查询"这件事没有任何外部表征 ——
    /// 两边都出数、都对,只是系统表被查了两遍;而对着一个 5000 张表的库,
    /// 那一遍就是实打实的一秒。计数是唯一能把它钉住的东西。
    /// </para>
    /// </summary>
    internal int MetadataQueries => Volatile.Read(ref _queries);

    internal SqlTreeViewModel(SqlSession session, Loc loc)
    {
        _session = session;
        _loc = loc;
        Roots = [];
    }

    /// <summary>根节点(**已按过滤词投影**)。</summary>
    public ObservableCollection<SqlTreeNode> Roots { get; }

    /// <summary>
    /// 过滤词。改动<b>立刻</b>作用到整棵已装载的树上 ——
    /// 而不是像早先那样只在下一次展开时参与一句 <c>Where</c>。
    /// </summary>
    public string Filter
    {
        get => _filter;
        set
        {
            // 归一成空串:绑定在清空时给的是 null,而下面每一处都要拿它去 Contains。
            string term = value ?? "";
            if (string.Equals(_filter, term, StringComparison.Ordinal))
            {
                return;
            }
            _ = SetProperty(ref _filter, term);
            foreach (SqlTreeNode root in _allRoots)
            {
                root.ApplyFilter(_filter);
            }
            ProjectRoots();
        }
    }

    /// <summary>过滤框的水印。</summary>
    public string FilterWatermark => _loc["Sql_FilterObjects"];

    /// <summary>当前选中的节点。</summary>
    public SqlTreeNode? Selected { get; set; }

    /// <summary>装载根。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // 重新装根 = 用户按了 F5 或刚连上。缓存整份作废:F5 的含义就是"我改过东西了"。
        _objects.Clear();
        _allRoots.Clear();
        Roots.Clear();

        IDialectPack pack = _session.Pack;
        string current = _session.DefaultCatalog;

        if (pack.HasDatabases)
        {
            IReadOnlyList<SqlObject> databases = await _session.Metadata
                .UseAsync(pack.ListDatabasesAsync, cancellationToken).ConfigureAwait(true);
            _allRoots.AddRange(Group(
                [.. databases.Select(d => DatabaseNode(d, current))],
                parentIsSystem: false));
        }
        else if (pack.HasSchemas)
        {
            // Oracle:没有"库"这一级(方言包上有为什么),根就是 schema。
            //
            // 「当前」这一格拿**登录名**去比,不是拿 s.Name 去比自己 —— 后者恒为真,
            // 于是三十个 schema 全部加粗,加粗也就不再是信息。
            // Oracle 的 schema 就是 user,所以登录名正是"我在哪"。
            IReadOnlyList<SqlObject> schemas = await _session.Metadata
                .UseAsync(pack.ListSchemasAsync, cancellationToken).ConfigureAwait(true);
            _allRoots.AddRange(Group(
                [.. schemas.Select(s => SchemaNode(s, current, _session.LoginName))],
                parentIsSystem: false));
        }
        else
        {
            // SQLite:没有库也没有 schema,直接就是分类。
            _allRoots.AddRange(Categories(current, "", parentIsSystem: false));
        }

        foreach (SqlTreeNode root in _allRoots)
        {
            root.ApplyFilter(_filter);
        }
        ProjectRoots();
    }

    private void ProjectRoots()
    {
        Roots.Clear();
        foreach (SqlTreeNode root in _allRoots)
        {
            if (root.MatchesFilter(_filter))
            {
                Roots.Add(root);
            }
        }
    }

    // ─────────────────────────── 建节点 ───────────────────────────

    private SqlTreeNode DatabaseNode(SqlObject database, string current) =>
        new(database.Name,
            SqlNodeKind.Database,
            database,
            LoadUnderDatabaseAsync,
            schema: "",
            database: database.Name,
            isSystem: database.IsSystem,
            detail: database.Comment,
            isCurrent: string.Equals(database.Name, current, StringComparison.OrdinalIgnoreCase));

    private SqlTreeNode SchemaNode(SqlObject schema, string database, string currentSchema) =>
        new(schema.Name,
            SqlNodeKind.Schema,
            schema,
            LoadCategoriesAsync,
            schema: schema.Name,
            database: database,
            isSystem: schema.IsSystem,
            detail: schema.Comment,
            isCurrent: string.Equals(schema.Name, currentSchema, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 把一批同级节点按"系统 / 用户"归并:用户的在前,系统的收进一个折叠分组。
    /// <para>
    /// 三条边界都是刻意的:<br />
    /// ① <b>父节点本身就是系统的</b>(比如 <c>pg_catalog</c> 下的表)→ 不再套一层,
    ///    里面全是系统对象,再分组只是多一次点击;<br />
    /// ② <b>一个系统对象都没有</b> → 不画空分组;<br />
    /// ③ <b>全都是系统对象</b> → 平铺。这一层里没有"混排"这回事,套个分组反而多一层。
    /// </para>
    /// </summary>
    /// <param name="nodes">同级节点。</param>
    /// <param name="parentIsSystem">父节点是不是系统对象。</param>
    /// <returns>归并后的同级节点。</returns>
    private IReadOnlyList<SqlTreeNode> Group(IReadOnlyList<SqlTreeNode> nodes, bool parentIsSystem)
    {
        if (parentIsSystem)
        {
            return nodes;
        }
        SqlTreeNode[] system = [.. nodes.Where(n => n.IsSystem)];
        if (system.Length == 0 || system.Length == nodes.Count)
        {
            return nodes;
        }
        SqlTreeNode[] user = [.. nodes.Where(n => !n.IsSystem)];
        return
        [
            .. user,
            new SqlTreeNode(
                _loc["Sql_NodeSystemObjects"],
                SqlNodeKind.SystemGroup,
                isSystem: true,
                children: system)
        ];
    }

    private async Task<IReadOnlyList<SqlTreeNode>> LoadUnderDatabaseAsync(SqlTreeNode node, CancellationToken token)
    {
        string database = node.Database;
        if (!_session.Pack.HasSchemas)
        {
            // MySQL:database 就是 schema,下一级直接是分类。
            return Categories(database, database, node.IsSystem);
        }

        // **这一句是"每个库都是空的"那个缺陷的修复点。** 早先这里拿的是会话上那条
        // 唯一的元数据连接,而 PG / SQL Server 的目录表只覆盖连接所在的库 ——
        // 于是无论展开哪个库,查到的都是连接串里那个库的 schema。
        SqlConnection connection = await _session.MetadataForAsync(database, token).ConfigureAwait(false);
        IReadOnlyList<SqlObject> schemas = await connection
            .UseAsync(_session.Pack.ListSchemasAsync, token).ConfigureAwait(false);

        // 当前 schema 只在"当前库"里才谈得上 —— 别的库里没有"我在这儿"这回事,
        // 所以这条额外的查询整个会话最多也就发这么几次。
        string currentSchema = string.Equals(database, _session.DefaultCatalog, StringComparison.OrdinalIgnoreCase)
            ? await CurrentSchemaAsync(connection, token).ConfigureAwait(false)
            : "";
        return Group([.. schemas.Select(s => SchemaNode(s, database, currentSchema))], node.IsSystem);
    }

    /// <summary>
    /// 问服务端"当前 schema 是哪个",用来加粗那一行。
    /// <para>
    /// <b>问,而不是按 <c>public</c> / <c>dbo</c> 猜。</b> PG 上它由连接的 <c>search_path</c> 决定、
    /// SQL Server 上由登录的 <c>DEFAULT_SCHEMA</c> 决定,两者都可以被配置成别的值。
    /// 按名字猜在那些库上会**把一个不是当前的 schema 加粗** —— 而加粗的全部意义就是
    /// "这一个和别的不一样",指错了比不指更坏。
    /// </para>
    /// <para>拿不到就返回空串:少一处加粗是可以接受的降级,让整棵树展不开不是。</para>
    /// </summary>
    /// <param name="connection">该库的元数据连接。</param>
    /// <param name="token">取消令牌。</param>
    /// <returns>当前 schema 名;方言不支持或查不到时为空串。</returns>
    private async Task<string> CurrentSchemaAsync(SqlConnection connection, CancellationToken token)
    {
        if (_session.Pack.CurrentSchemaSql is not { } sql)
        {
            return "";
        }
        try
        {
            return await connection.UseAsync(async (raw, inner) =>
            {
                await using DbCommand command = raw.CreateCommand();
                command.CommandText = sql;
                command.CommandTimeout = 10;
                object? value = await command.ExecuteScalarAsync(inner).ConfigureAwait(false);
                return value is null or DBNull ? "" : value.ToString() ?? "";
            }, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "";
        }
    }

    private Task<IReadOnlyList<SqlTreeNode>> LoadCategoriesAsync(SqlTreeNode node, CancellationToken token) =>
        Task.FromResult(Categories(node.Database, node.Schema, node.IsSystem));

    private IReadOnlyList<SqlTreeNode> Categories(string database, string schema, bool parentIsSystem)
    {
        List<SqlTreeNode> categories =
        [
            Category("Sql_NodeTables", SqlObjectGroup.Table, database, schema, parentIsSystem),
            Category("Sql_NodeViews", SqlObjectGroup.View, database, schema, parentIsSystem)
        ];
        // 分类只在方言真有这一类对象时才画。**画一个恒空的"序列"给 MySQL 用户看**
        // 与"这个库真的没有序列"长得一模一样(§7.8 那条"给不了要说出来"的反面:
        // 这里不是给不了,是压根没有这个概念)。
        if (_session.Pack.HasRoutines)
        {
            categories.Add(Category("Sql_NodeRoutines", SqlObjectGroup.Routine, database, schema, parentIsSystem));
        }
        if (_session.Pack.HasSequences)
        {
            categories.Add(Category("Sql_NodeSequences", SqlObjectGroup.Sequence, database, schema, parentIsSystem));
        }
        return categories;
    }

    private SqlTreeNode Category(
        string labelKey, SqlObjectGroup group, string database, string schema, bool parentIsSystem) =>
        new(_loc[labelKey],
            SqlNodeKind.Category,
            load: (n, t) => LoadObjectsAsync(n, group, parentIsSystem, t),
            schema: schema,
            database: database,
            isSystem: parentIsSystem);

    private async Task<IReadOnlyList<SqlTreeNode>> LoadObjectsAsync(
        SqlTreeNode node, SqlObjectGroup group, bool parentIsSystem, CancellationToken token)
    {
        IReadOnlyList<SqlObject> objects = await FetchAsync(group, node.Database, node.Schema, token)
            .ConfigureAwait(false);

        IEnumerable<SqlObject> wanted = group switch
        {
            // 物化视图归到"视图"下 —— 它在 DbMaintenance 里根本不存在,
            // 但用户心里它就是一种视图。节点类别仍然分开,图标要看得出区别。
            SqlObjectGroup.View => objects.Where(o => o.Kind is SqlObjectKind.View or SqlObjectKind.MaterializedView),
            SqlObjectGroup.Table => objects.Where(o => o.Kind == SqlObjectKind.Table),
            _ => objects
        };

        SqlTreeNode[] nodes =
        [
            .. wanted
                .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                .Select(o => new SqlTreeNode(
                    o.Name,
                    KindOf(o.Kind),
                    o,
                    // 只有关系挂得住"列"这一层。给函数挂一个展开箭头,点开是一条查不到东西的
                    // DescribeAsync —— 那是画一个假的可展开。
                    IsRelation(o.Kind) ? LoadColumnsAsync : null,
                    schema: o.Schema,
                    database: node.Database,
                    isSystem: o.IsSystem,
                    detail: o.Comment))
        ];
        return Group(nodes, parentIsSystem);
    }

    private static bool IsRelation(SqlObjectKind kind) =>
        kind is SqlObjectKind.Table or SqlObjectKind.View or SqlObjectKind.MaterializedView;

    private static SqlNodeKind KindOf(SqlObjectKind kind) => kind switch
    {
        SqlObjectKind.MaterializedView => SqlNodeKind.MaterializedView,
        SqlObjectKind.View => SqlNodeKind.View,
        SqlObjectKind.Procedure => SqlNodeKind.Procedure,
        SqlObjectKind.Function => SqlNodeKind.Function,
        SqlObjectKind.Sequence => SqlNodeKind.Sequence,
        _ => SqlNodeKind.Table
    };

    private async Task<IReadOnlyList<SqlTreeNode>> LoadColumnsAsync(SqlTreeNode node, CancellationToken token)
    {
        if (node.Target is not { } target)
        {
            return [];
        }
        SqlConnection connection = await _session.MetadataForAsync(node.Database, token).ConfigureAwait(false);
        SqlTableSchema schema = await connection
            .UseAsync((c, t) => _session.Pack.DescribeAsync(c, target, t), token).ConfigureAwait(false);

        return
        [
            .. schema.Columns.Select(c => new SqlTreeNode(
                // 列后面直接把类型原文摆出来 —— 这是打开一张表最先要问的问题,
                // 不该逼用户再点一次"表结构"。
                $"{c.Name}  {c.DataType}{(c.IsPrimaryKey ? "  PK" : "")}{(c.IsAutoIncrement ? "  AI" : "")}"
                + $"{(c.IsGenerated ? "  " + _loc["Sql_GeneratedColumn"] : "")}{(c.IsNullable ? "" : "  NOT NULL")}",
                SqlNodeKind.Column,
                schema: node.Schema,
                database: node.Database,
                isSystem: node.IsSystem,
                detail: c.Comment))
        ];
    }

    /// <summary>
    /// 刷新一个节点(F5)。
    /// <para>
    /// <b>必须由树来做,不能直接调 <c>node.RefreshAsync</c>。</b>
    /// 对象清单现在是缓存的 —— 只让节点重新装载,它会原样拿回**同一份缓存**,
    /// 于是 F5 变成一个看起来在转、实际什么都没查的动作。这比没有 F5 更坏:
    /// 用户会据此认定"库里真的没有那张新表"。
    /// </para>
    /// <para>
    /// 作废范围取"这个节点所在的库 + schema"下的全部类别。
    /// 库 / schema 节点(它们自己的 <c>Schema</c> 就是自己)刷新时,连它下面各类别的缓存一起清 ——
    /// 在一个 schema 上按 F5,用户的意思就是"这底下的东西我都要重读"。
    /// </para>
    /// </summary>
    /// <param name="node">要刷新的节点;<see langword="null" /> 表示整棵树。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    public async Task RefreshAsync(SqlTreeNode? node, CancellationToken cancellationToken = default)
    {
        // 判据是 CanReload 而不是 CanExpand:重装不了的节点(系统对象分组)要落进全树兜底,
        // 否则它两边都不占,F5 变成彻底的空操作。
        if (node is null || !node.CanReload)
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(true);
            return;
        }
        Invalidate(node.Database, node.Schema);
        await node.RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// 作废某个 (库, schema) 下全部类别的缓存。
    /// <para>
    /// <b><paramref name="schema" /> 为空时清掉整个库</b>,这不是图省事:
    /// 库节点自己的 <c>Schema</c> 就是空的(它下面挂的是 schema,不是对象),
    /// 而它底下各 schema 的缓存键带着各自的 schema 名。只按 (库, "") 精确匹配的话,
    /// **在库节点上按 F5 一条都清不掉** —— 树会重建出一批新的 schema 节点,
    /// 而它们一展开又原样拿回旧清单。
    /// </para>
    /// </summary>
    /// <param name="database">库。</param>
    /// <param name="schema">schema;空表示"这个库底下全部"。</param>
    private void Invalidate(string database, string schema)
    {
        foreach (SqlObjectKey key in _objects.Keys)
        {
            if (key.Database == database && (schema.Length == 0 || key.Schema == schema))
            {
                _ = _objects.TryRemove(key, out _);
            }
        }
    }

    // ─────────────────────────── 取数与缓存 ───────────────────────────

    private async Task<IReadOnlyList<SqlObject>> FetchAsync(
        SqlObjectGroup group, string database, string schema, CancellationToken token)
    {
        // 表与视图共用同一条 ListRelationsAsync,所以它们的缓存桶必须相同。
        SqlObjectGroup bucket = group is SqlObjectGroup.Table or SqlObjectGroup.View ? SqlObjectGroup.Table : group;
        SqlObjectKey key = new(bucket, database, schema);

        Lazy<Task<IReadOnlyList<SqlObject>>> entry = _objects.GetOrAdd(
            key,
            _ => new(() => QueryAsync(group, database, schema), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await entry.Value.WaitAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 失败的不留缓存,否则"起服务端之后按 F5"永远拿回同一条异常。
            _objects.TryRemove(new KeyValuePair<SqlObjectKey, Lazy<Task<IReadOnlyList<SqlObject>>>>(key, entry));
            throw;
        }
    }

    /// <summary>
    /// 真正发查询。
    /// <para>
    /// <b>用 <see cref="CancellationToken.None" /> 而不是调用方的令牌</b>:这个任务是
    /// 缓存里所有人共享的,一个人取消不该让别人拿到一条永远取消掉的结果。
    /// 调用方的取消作用在 <c>WaitAsync</c> 上 —— 那才是"我不等了"的正确位置。
    /// </para>
    /// </summary>
    /// <param name="group">类别。</param>
    /// <param name="database">库。</param>
    /// <param name="schema">schema。</param>
    /// <returns>对象清单。</returns>
    private async Task<IReadOnlyList<SqlObject>> QueryAsync(SqlObjectGroup group, string database, string schema)
    {
        Interlocked.Increment(ref _queries);
        SqlConnection connection = await _session.MetadataForAsync(database).ConfigureAwait(false);
        return await connection.UseAsync(
            (c, t) => group switch
            {
                SqlObjectGroup.Routine => _session.Pack.ListRoutinesAsync(c, schema, t),
                SqlObjectGroup.Sequence => _session.Pack.ListSequencesAsync(c, schema, t),
                _ => _session.Pack.ListRelationsAsync(c, schema, t)
            }).ConfigureAwait(false);
    }
}

using System.Collections.ObjectModel;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Sql.Ui;

/// <summary>
/// 数据库工作台面板。
/// <para>
/// 布局是"左对象树 + 右工作区(内含多标签)",而不是互斥的顶层页签:
/// 翻对象、敲 SQL、看结果这三件事是**并行**的 —— 用户会一边看着表结构一边写查询。
/// </para>
/// <para>
/// 编辑器与结果网格是**上下**而不是左右:SQL 是宽的(一行 80–200 字符),结果也是宽的,
/// 左右分栏会让两边都被压扁。商业工具无一例外是上下分,这不是抄,是同一个约束下的同一个解。
/// </para>
/// </summary>
public sealed class SqlWorkspaceViewModel : ObservableObject, IAsyncDisposable
{
    private readonly SqlSession _session;
    private readonly Loc _loc;
    private readonly IPluginLogger _log;
    private readonly IPluginContext _context;
    private SqlTabViewModel? _activeTab;
    private int _tabSeq;

    internal SqlWorkspaceViewModel(SqlSession session, WorkspaceConnectRequest request, Loc loc, IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        _session = session;
        _loc = loc;
        _context = context;
        _log = context.Log;

        SqlDialectInfo info = SqlDialects.Of(session.Dialect);
        Title = string.IsNullOrWhiteSpace(request.DisplayName) ? info.DisplayName : request.DisplayName;
        DialectName = info.DisplayName;
        Endpoint = session.Metadata.Endpoint;
        ServerVersion = string.IsNullOrWhiteSpace(session.Metadata.Info.ServerVersion)
            ? loc["Sql_UnknownVersion"]
            : session.Metadata.Info.ServerVersion;
        DatabaseName = string.IsNullOrWhiteSpace(session.Metadata.Info.DatabaseName)
            ? loc["Sql_NoDatabaseSelected"]
            : session.Metadata.Info.DatabaseName;

        EnvironmentText = loc[$"Sql_Env{session.Settings.Environment}"];
        IsProduction = session.Settings.Environment == SqlEnvironment.Production;
        IsReadOnly = session.Settings.ReadOnly;
        ReadOnlyText = loc["Sql_ReadOnlyBadge"];
        TunnelText = request.Tunnel is { } tunnel
            ? loc.Format("Sql_ViaTunnel", tunnel.JumpDisplayName, tunnel.TargetHost, tunnel.TargetPort)
            : "";
        HasTunnel = request.Tunnel is not null;

        // 探针连接开不出来 = 取消阶梯少一档(旁路取消)。如实告诉用户,而不是等他按下取消才发现。
        HasBypassCancel = session.Probe is not null;
        BypassCancelWarning = HasBypassCancel ? "" : loc["Sql_NoBypassCancel"];

        Tree = new(session, loc);
        Tabs = [];
        NewTabCommand = new(NewTab);
        OpenOpsCommand = new(OpenOps);
        RefreshTreeCommand = new(RefreshTreeAsync);
        NewTab();
    }

    /// <summary>标签页标题。</summary>
    public string Title { get; }

    /// <summary>方言名。</summary>
    public string DialectName { get; }

    /// <summary>端点。</summary>
    public string Endpoint { get; }

    /// <summary>服务端版本。</summary>
    public string ServerVersion { get; }

    /// <summary>当前库。</summary>
    public string DatabaseName { get; }

    /// <summary>环境文案。</summary>
    public string EnvironmentText { get; }

    /// <summary>是否生产环境。</summary>
    public bool IsProduction { get; }

    /// <summary>是否只读。</summary>
    public bool IsReadOnly { get; }

    /// <summary>只读徽标文案。</summary>
    public string ReadOnlyText { get; }

    /// <summary>隧道来路。</summary>
    public string TunnelText { get; }

    /// <summary>是否经隧道。</summary>
    public bool HasTunnel { get; }

    /// <summary>旁路取消通道可用吗。</summary>
    public bool HasBypassCancel { get; }

    /// <summary>旁路取消不可用时的提示。</summary>
    public string BypassCancelWarning { get; }

    /// <summary>对象树。</summary>
    public SqlTreeViewModel Tree { get; }

    /// <summary>查询标签。</summary>
    public ObservableCollection<SqlTabViewModel> Tabs { get; }

    /// <summary>当前标签。</summary>
    public SqlTabViewModel? ActiveTab
    {
        get => _activeTab;
        set => SetProperty(ref _activeTab, value);
    }

    /// <summary>当前标签(仅当它是查询标签时);否则 <see langword="null" />。</summary>
    public SqlQueryTabViewModel? ActiveQueryTab => _activeTab as SqlQueryTabViewModel;

    /// <summary>新建查询标签。</summary>
    public RelayCommand NewTabCommand { get; }

    /// <summary>打开运维面(会话与锁)。</summary>
    public RelayCommand OpenOpsCommand { get; }

    /// <summary>右键菜单「打开数据」。</summary>
    public string OpenDataLabel => _loc["Sql_OpenData"];

    /// <summary>右键菜单「查看结构」。</summary>
    public string OpenStructureLabel => _loc["Sql_OpenStructure"];

    /// <summary>工具条「会话与锁」。</summary>
    public string OpenOpsLabel => _loc["Sql_OpenOps"];

    /// <summary>刷新对象树(F5)。</summary>
    public AsyncRelayCommand RefreshTreeCommand { get; }

    /// <summary>面板首次显示时装载对象树。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await Tree.InitializeAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Warn($"对象树装载失败:{ex.Message}");
        }
    }

    /// <summary>
    /// 双击对象树上一个节点的**统一入口**。
    /// <para>
    /// 双击的含义随节点种类变:表 / 视图是"打开数据",库 / schema / 分类是"展开收起"。
    /// 以前这里直接调 <see cref="OpenData" />,于是双击库节点既开不出数据、也不展开 ——
    /// 两种正确行为一个都没做到。把分派收在这一处,视图那边只管"用户双击了谁"。
    /// </para>
    /// <para>
    /// 表节点自己也能展开(下面挂着列),但双击仍然优先"打开数据" ——
    /// 这是打开一张表之后最高频的动作,展开有旁边的箭头。
    /// </para>
    /// </summary>
    /// <param name="node">被双击的节点。</param>
    public void ActivateNode(SqlTreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.CanOpenData)
        {
            OpenData(node);
            return;
        }
        if (node.CanExpand)
        {
            node.IsExpanded = !node.IsExpanded;
        }
    }

    /// <summary>
    /// 打开一张表 / 视图的数据。
    /// <para>
    /// 生成的是 <b>服务端 LIMIT 的 SELECT</b>,而不是 <c>select *</c> 再客户端截断 ——
    /// 实测后者在 50 万行宽表上,光是 <c>reader.Dispose()</c> 的排水就要 6.5 秒(§7.3)。
    /// </para>
    /// <para>
    /// 非关系节点(库 / schema / 分类 / 列)在这里**直接退回**:它们的名字拼进 <c>FROM</c>
    /// 只会换来 42P01 / ORA-00942。判定统一交给 <see cref="SqlTreeNode.CanOpenData" />,
    /// 免得每个入口各写一遍、再各漏一种。
    /// </para>
    /// </summary>
    /// <param name="node">被双击的节点。</param>
    public void OpenData(SqlTreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!node.CanOpenData || node.Target is not { } target)
        {
            return;
        }
        string quoted = _session.Pack is Metadata.DialectPackBase pack
            ? pack.QuoteQualified(target)
            : _session.Pack.QuoteIdentifier(target.Name);
        // 标签绑在**这个节点所在的库**上 —— 限定名只到 schema,库这一级只能落在连接上。
        SqlQueryTabViewModel tab = NewTab(target.Name, node.Database);
        tab.Sql = _session.Pack.ApplyPaging($"SELECT * FROM {quoted}", 0, SqlFetchPreview);

        // **先跑再补编辑能力**,顺序是刻意的:取表结构要发三条元数据查询,
        // 而用户按下双击之后想看到的是数据,不是一个先转两百毫秒再开始查的空网格。
        tab.ExecuteAllCommand.Execute(null);
        _ = BindEditTargetAsync(tab, target, node.Database);
    }

    /// <summary>
    /// 补上"这个网格能不能就地编辑"的判定。
    /// <para>
    /// 独立成一个方法是因为 <see cref="OpenData" /> 曾经是 <c>async void</c> ——
    /// 它里面 <c>await</c> 的那条元数据查询一旦抛出(库不可达、权限不够),
    /// 异常会直接落到同步上下文上,在 Avalonia 里就是**整个应用崩掉**,
    /// 而用户做的只是双击了一张表。现在失败只影响"这一格能不能编辑"。
    /// </para>
    /// </summary>
    /// <param name="tab">查询标签。</param>
    /// <param name="target">目标对象。</param>
    /// <param name="catalog">对象所在的库。</param>
    /// <returns>表示异步操作的任务。</returns>
    private async Task BindEditTargetAsync(SqlQueryTabViewModel tab, Metadata.SqlObject target, string catalog)
    {
        try
        {
            SqlConnection connection = await _session
                .MetadataForAsync(catalog, CancellationToken.None).ConfigureAwait(true);
            Metadata.SqlTableSchema schema = await connection
                .UseAsync((c, t) => _session.Pack.DescribeAsync(c, target, t), CancellationToken.None).ConfigureAwait(true);
            tab.BindEditTarget(target, schema);
        }
        catch (Exception ex)
        {
            _log.Debug($"取表结构失败,{target.Name} 的网格将是只读的:{ex.Message}");
        }
    }

    /// <summary>
    /// 打开一张表 / 视图的**结构页**(列 / 索引 / 外键 / DDL)。
    /// <para>结果网格的列头只给驱动报的类型,那不等于建表时的类型 —— 准确类型在这一页(§7.3)。</para>
    /// <para>
    /// 与 <see cref="OpenData" /> 同一条闸门:一个库或一个 schema 没有"结构"可看,
    /// 硬开出来只会是一页查不到东西的空表。
    /// </para>
    /// </summary>
    /// <param name="node">对象树节点。</param>
    public void OpenStructure(SqlTreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!node.CanOpenData || node.Target is not { } target)
        {
            return;
        }
        var tab = new SqlStructureTabViewModel(_session, target, _loc, node.Database);
        Tabs.Add(tab);
        ActiveTab = tab;
        _ = tab.LoadAsync(CancellationToken.None);
    }

    /// <summary>打开运维面。已经开着就切过去,不重复开 —— 它是全会话唯一的一页。</summary>
    private void OpenOps()
    {
        if (Tabs.OfType<SqlOpsTabViewModel>().FirstOrDefault() is { } existing)
        {
            ActiveTab = existing;
            return;
        }
        var tab = new SqlOpsTabViewModel(_session, _loc);
        Tabs.Add(tab);
        ActiveTab = tab;
        _ = tab.LoadAsync(CancellationToken.None);
    }

    /// <summary>预览一张表时默认取多少行。与 <c>SqlFetchOptions</c> 的默认值一致。</summary>
    private const int SqlFetchPreview = 200;

    private void NewTab() => NewTab("", "");

    private SqlQueryTabViewModel NewTab(string hint, string catalog)
    {
        _tabSeq++;
        string title = string.IsNullOrEmpty(hint)
            ? _loc.Format("Sql_QueryTabTitle", _tabSeq)
            : hint;
        var tab = new SqlQueryTabViewModel(_session, _loc, _log, title, catalog);
        Tabs.Add(tab);
        ActiveTab = tab;
        return tab;
    }

    private Task RefreshTreeAsync() => Tree.RefreshAsync(Tree.Selected, CancellationToken.None);

    /// <summary>
    /// 复制文本到剪贴板。
    /// <para>
    /// 走**宿主的**剪贴板能力而不是 <c>TopLevel.Clipboard</c> —— 隔离进程里没有窗口
    /// (与 AI 插件同一条注记)。
    /// </para>
    /// </summary>
    /// <param name="text">文本。</param>
    public void CopyToClipboard(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            _ = _context.Clipboard.SetTextAsync(text);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (SqlQueryTabViewModel tab in Tabs.OfType<SqlQueryTabViewModel>())
        {
            await tab.DisposeAsync().ConfigureAwait(false);
        }
        Tabs.Clear();
    }
}

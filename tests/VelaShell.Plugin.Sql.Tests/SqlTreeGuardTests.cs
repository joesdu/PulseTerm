using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Plugin.Sql.Metadata;
using VelaShell.Plugin.Sql.Ui;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Sql.Tests;

/// <summary>
/// 对象树上"把非表节点当表用"的两个真机缺陷的回归网。
/// <para>
/// 现象(真机截图实拍):双击库节点 <c>ops_pg</c> 开出一页
/// <c>SELECT * FROM "ops_pg"</c>,PG 回 42P01;双击 Oracle 的 schema 节点
/// <c>SYSBACKUP</c> 开出 <c>SELECT * FROM "SYSBACKUP"</c>,回 ORA-00942;
/// 在分类节点「表」上右键照样弹出「打开数据 / 查看结构」两项,点了静默无效。
/// </para>
/// <para>
/// 根因是同一个:"能不能打开"以前按"有没有 <c>Target</c>"判定,
/// 而库与 schema 同样带 <c>Target</c>。所以这一组盯的是**判定本身**,
/// 而不是某一个入口 —— 双击、右键两条路都要走一遍。
/// </para>
/// <para>
/// 用 SQLite 建临时 <c>.db</c> 就够:要验的是节点种类的闸门,不是哪个方言的元数据。
/// 库 / schema 节点 SQLite 上不存在(它两样都没有),那两种按真实形状手工造,
/// 造出来的节点走的是与真机完全同一条判定。
/// </para>
/// <para>
/// <b>每个用例体末尾那句 <c>return true;</c> 不是装饰,少了它整组用例会全绿而且永远绿。</b>
/// <c>HeadlessUnitTestSession</c> 只有 <c>Dispatch(Action, …)</c> 与
/// <c>Dispatch&lt;T&gt;(Func&lt;Task&lt;T&gt;&gt;, …)</c> 两族重载,**没有** <c>Func&lt;Task&gt;</c> 那一支。
/// 于是一个不返回值的 <c>async () =&gt; { … }</c> 会被绑到 <c>Action</c> 上变成 <b>async void</b>:
/// 断言抛出的异常落在调度线程上没人接,<c>Dispatch</c> 返回的 Task 早就完成了,
/// 测试报"通过"。实测过:往用例里塞一句 <c>Assert.Fail("SANITY")</c> 依旧 5 个全过。
/// 让 lambda 返回一个值,重载就落到 <c>Func&lt;Task&lt;T&gt;&gt;</c> 上,异常才回得来。
/// </para>
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class SqlTreeGuardTests
{
    private static HeadlessUnitTestSession _session = null!;

    /// <summary>MSTest 注入的上下文。</summary>
    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SqlTreeGuardTests).Assembly);

    // 与 SqlPanelUiTests 同一条口径:**不 Dispose 这个会话**,它是整个程序集共用的一个。

    /// <summary>
    /// 双击库节点:不开查询页,而是展开 —— 这才是双击一个库的正确含义。
    /// <para>挡住"开查询页"的同时把展开也一起挡掉,是把一个缺陷换成另一个。</para>
    /// </summary>
    [TestMethod]
    public Task 双击库节点只展开不产生查询页() => _session.Dispatch(async () =>
    {
        string file = NewDbPath();
        try
        {
            await using SqlSession session = await OpenAsync(file);
            var viewModel = new SqlWorkspaceViewModel(session, Request(file), new("zh-Hans"), new TestPluginContext());
            int before = viewModel.Tabs.Count;

            SqlTreeNode database = Node("ops_pg", SqlNodeKind.Database, SqlObjectKind.Database);
            Assert.IsFalse(database.CanOpenData, "库节点不该被认成能打开数据的关系。");

            viewModel.ActivateNode(database);
            Dispatcher.UIThread.RunJobs();

            Assert.AreEqual(before, viewModel.Tabs.Count,
                "双击库节点开出了查询页 —— 那一页里是 SELECT * FROM \"ops_pg\",真机上回 42P01。");
            Assert.IsTrue(database.IsExpanded, "双击库节点的正确行为是展开,不是什么都不做。");

            // 再双击一次要收起来:展开/收起是一对,只做单向是半个功能。
            viewModel.ActivateNode(database);
            Assert.IsFalse(database.IsExpanded);
            Assert.AreEqual(before, viewModel.Tabs.Count);
            return true;
        }
        finally
        {
            TryDelete(file);
        }
    }, CancellationToken.None);

    /// <summary>双击 schema 节点:同上 —— Oracle 的 <c>SYSBACKUP</c> 就是这么变成 ORA-00942 的。</summary>
    [TestMethod]
    public Task 双击schema节点只展开不产生查询页() => _session.Dispatch(async () =>
    {
        string file = NewDbPath();
        try
        {
            await using SqlSession session = await OpenAsync(file);
            var viewModel = new SqlWorkspaceViewModel(session, Request(file), new("zh-Hans"), new TestPluginContext());
            int before = viewModel.Tabs.Count;

            SqlTreeNode schema = Node("SYSBACKUP", SqlNodeKind.Schema, SqlObjectKind.Schema);
            Assert.IsFalse(schema.CanOpenData, "schema 节点不该被认成能打开数据的关系。");

            viewModel.ActivateNode(schema);
            Dispatcher.UIThread.RunJobs();

            Assert.AreEqual(before, viewModel.Tabs.Count,
                "双击 schema 节点开出了查询页 —— 真机上是 SELECT * FROM \"SYSBACKUP\" / ORA-00942。");
            Assert.IsTrue(schema.IsExpanded, "双击 schema 节点的正确行为是展开。");
            return true;
        }
        finally
        {
            TryDelete(file);
        }
    }, CancellationToken.None);

    /// <summary>
    /// 右键菜单的两条命令**自己**也要设防,而不是只靠界面把菜单藏起来。
    /// <para>
    /// 界面那一层是给人看的,这一层是给代码看的:以后再多一个入口(命令面板、快捷键),
    /// 忘了抄防护也不会重新拼出 <c>SELECT * FROM "库名"</c>。
    /// </para>
    /// </summary>
    [TestMethod]
    public Task 非关系节点上的打开数据与查看结构一律不产生标签() => _session.Dispatch(async () =>
    {
        string file = NewDbPath();
        try
        {
            await using SqlSession session = await OpenAsync(file);
            await ExecAsync(session, "create table t(id integer primary key, name text)");

            var viewModel = new SqlWorkspaceViewModel(session, Request(file), new("zh-Hans"), new TestPluginContext());
            await viewModel.InitializeAsync(CancellationToken.None);

            // SQLite 没有库也没有 schema,根就是分类节点(「表」/「视图」)。
            SqlTreeNode category = viewModel.Tree.Roots[0];
            await category.LoadAsync(CancellationToken.None);
            SqlTreeNode table = category.Children.First(c => c.Title == "t");
            await table.LoadAsync(CancellationToken.None);
            SqlTreeNode column = table.Children[0];

            SqlTreeNode[] forbidden =
            [
                category,
                column,
                Node("ops_pg", SqlNodeKind.Database, SqlObjectKind.Database),
                Node("SYSBACKUP", SqlNodeKind.Schema, SqlObjectKind.Schema)
            ];

            int before = viewModel.Tabs.Count;
            foreach (SqlTreeNode node in forbidden)
            {
                Assert.IsFalse(node.CanOpenData, $"「{node.Title}」({node.Kind})不该被认成关系。");
                viewModel.OpenData(node);
                viewModel.OpenStructure(node);
            }
            Dispatcher.UIThread.RunJobs();

            Assert.AreEqual(before, viewModel.Tabs.Count,
                $"非关系节点开出了标签页:{string.Join(" / ", viewModel.Tabs.Select(t => t.Title))}");
            return true;
        }
        finally
        {
            TryDelete(file);
        }
    }, CancellationToken.None);

    /// <summary>
    /// 分类节点上右键 —— 菜单**整个不挂**,而不是弹出来两项点了没反应。
    /// <para>
    /// 这条走真控件:菜单挂不挂是代码后置按选中项装卸 <c>TreeView.ContextMenu</c> 决定的,
    /// 视图模型上看不见。<c>ContextMenu</c> 为 <see langword="null" /> 时 Avalonia 连
    /// <c>ContextRequested</c> 的处理器都不挂,右键彻底没有反应。
    /// </para>
    /// <para>
    /// 断言的是"属性变没变",不是"弹窗开没开" —— 后者验不了:Avalonia 12 上
    /// <c>ContextMenu.Open(control)</c> 直接把 <c>IsOpen</c> 置真,<c>Opening</c> 一次都不抛,
    /// 于是"在 Opening 里 Cancel"这条路既拦不住程序化入口、headless 里也看不出真假。
    /// </para>
    /// </summary>
    [TestMethod]
    public Task 分类节点与列上不挂右键菜单而真表上挂() => _session.Dispatch(async () =>
    {
        string file = NewDbPath();
        try
        {
            await using SqlSession session = await OpenAsync(file);
            await ExecAsync(session, "create table t(id integer primary key)");

            var viewModel = new SqlWorkspaceViewModel(session, Request(file), new("zh-Hans"), new TestPluginContext());
            var view = new SqlWorkspaceView(viewModel);
            var window = new Window { Width = 1200, Height = 700, Content = view };
            window.Show();

            await viewModel.InitializeAsync(CancellationToken.None);
            Dispatcher.UIThread.RunJobs();

            TreeView tree = view.GetVisualDescendants().OfType<TreeView>().First();
            Assert.IsNull(tree.ContextMenu, "还没选中任何东西就先把菜单挂上了。");

            SqlTreeNode category = viewModel.Tree.Roots[0];
            await category.LoadAsync(CancellationToken.None);
            SqlTreeNode table = category.Children.First(c => c.Title == "t");
            await table.LoadAsync(CancellationToken.None);
            Dispatcher.UIThread.RunJobs();

            tree.SelectedItem = table;
            Dispatcher.UIThread.RunJobs();
            ContextMenu? menu = tree.ContextMenu;
            Assert.IsNotNull(menu, "真表上的右键菜单被一起挡掉了 —— 防过头等于砍掉功能。");
            Assert.AreEqual(2, menu.Items.Count, "菜单里应当就是「打开数据」「查看结构」两项。");

            // 摘下来再挂回去,菜单项上那两条 {Binding OpenDataLabel} 必须还活着。
            // 这是"装卸 ContextMenu"这条路唯一真正的风险:ContextMenu 不在宿主的逻辑树里,
            // DataContext 是弹出时由 PlacementTarget 带进去的,摘挂一轮之后如果接不回去,
            // 屏幕上就是两条**没有文字**的菜单项 —— 编译、装载、Items.Count 全都照样绿。
            menu.Open(tree);
            Dispatcher.UIThread.RunJobs();
            string[] headers = [.. menu.Items.OfType<MenuItem>().Select(m => m.Header?.ToString() ?? "")];
            CollectionAssert.AreEqual(
                new[] { viewModel.OpenDataLabel, viewModel.OpenStructureLabel }, headers,
                "菜单项的文案没接上 —— 摘挂之后 DataContext 没回来。");
            menu.Close();
            Dispatcher.UIThread.RunJobs();

            tree.SelectedItem = category;
            Dispatcher.UIThread.RunJobs();
            Assert.IsNull(tree.ContextMenu,
                "分类节点上还挂着右键菜单 —— 里面两项都点不出任何结果,就是「摆一个不起作用的控件」。");

            tree.SelectedItem = table.Children[0];
            Dispatcher.UIThread.RunJobs();
            Assert.IsNull(tree.ContextMenu, "列节点上还挂着右键菜单。");

            // 挂回去要挂的是**同一个**菜单实例:每次新建一个会把 Click 处理器与 DataContext 一起丢掉。
            tree.SelectedItem = table;
            Dispatcher.UIThread.RunJobs();
            Assert.AreSame(menu, tree.ContextMenu, "菜单被换成了另一个实例。");

            window.Close();
            return true;
        }
        finally
        {
            TryDelete(file);
        }
    }, CancellationToken.None);

    /// <summary>
    /// 设防之后真表的双击**照样**打开数据。
    /// <para>这条是上面所有"不许"的对照组:少了它,把 <c>CanOpenData</c> 写成常量 false 也能全绿。</para>
    /// </summary>
    [TestMethod]
    public Task 双击真表仍然正常打开数据() => _session.Dispatch(async () =>
    {
        string file = NewDbPath();
        try
        {
            await using SqlSession session = await OpenAsync(file);
            await ExecAsync(session, "create table t(id integer primary key, name text)");
            await ExecAsync(session, "insert into t(name) values('张三'), ('李四')");

            var viewModel = new SqlWorkspaceViewModel(session, Request(file), new("zh-Hans"), new TestPluginContext());
            await viewModel.InitializeAsync(CancellationToken.None);

            SqlTreeNode category = viewModel.Tree.Roots[0];
            await category.LoadAsync(CancellationToken.None);
            SqlTreeNode table = category.Children.First(c => c.Title == "t");
            Assert.IsTrue(table.CanOpenData);

            viewModel.ActivateNode(table);
            for (int i = 0; i < 200 && viewModel.ActiveQueryTab?.Grid.Rows.Count == 0; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(20);
            }

            SqlQueryTabViewModel tab = viewModel.ActiveQueryTab!;
            StringAssert.Contains(tab.Sql, "t", "生成的 SQL 里连表名都没有。");
            Assert.AreEqual(2, tab.Grid.Rows.Count, "双击真表该把两行数据取回来。");
            Assert.AreEqual(2, tab.Grid.Columns.Count);
            return true;
        }
        finally
        {
            TryDelete(file);
        }
    }, CancellationToken.None);

    /// <summary>
    /// **系统对象永不与用户对象混排。**
    /// <para>
    /// 这是用户报上来的第一号故障:"数据库实例下的系统表和用户表混在一起根本没法用"。
    /// 根因在方言包那边 —— 五个包各自把系统性写进 <c>SqlObject.Comment</c> 的一个
    /// <c>"@system"</c> 记号里,而对象树<b>一个字都没读</b>(见
    /// <c>OraclePackTests.系统对象的标记不再借道注释字段</c>)。
    /// </para>
    /// <para>
    /// 这条盯的是树这一侧:拿到带 <c>IsSystem</c> 的对象之后,它必须把系统对象
    /// 收进一个单独的分组节点,而不是按字母序插在用户表中间。
    /// 用 SQLite 验是因为它的系统对象是精确可造的:<c>AUTOINCREMENT</c> 一定会
    /// 让引擎建出 <c>sqlite_sequence</c>,而 <c>sqlite_</c> 前缀是引擎保留的、
    /// 用户建不出来 —— 判据不是启发式的。
    /// </para>
    /// </summary>
    [TestMethod]
    public Task 系统对象收进单独分组而不与用户表混排() => _session.Dispatch(async () =>
    {
        string file = NewDbPath();
        try
        {
            await using SqlSession session = await OpenAsync(file);
            // AUTOINCREMENT 建出 sqlite_sequence;另外两张是纯用户表。
            await ExecAsync(session, "create table zzz_last(id integer primary key autoincrement, v text)");
            await ExecAsync(session, "create table aaa_first(id integer primary key)");

            var viewModel = new SqlWorkspaceViewModel(session, Request(file), new("zh-Hans"), new TestPluginContext());
            await viewModel.Tree.InitializeAsync(CancellationToken.None);

            // SQLite 没有库也没有 schema,根就是分类。第一个是"表"。
            SqlTreeNode tables = viewModel.Tree.Roots[0];
            await tables.LoadAsync(CancellationToken.None);

            string[] direct = [.. tables.Children.Select(c => c.Title)];
            CollectionAssert.Contains(direct, "aaa_first", "用户表要在分类下直接可见。");
            CollectionAssert.Contains(direct, "zzz_last", "用户表要在分类下直接可见。");
            CollectionAssert.DoesNotContain(
                direct,
                "sqlite_sequence",
                "系统表混在用户表里正是被报上来的那个故障 —— 它该在'系统对象'分组下面。");

            SqlTreeNode group = tables.Children.Single(c => c.Kind == SqlNodeKind.SystemGroup);
            await group.LoadAsync(CancellationToken.None);
            CollectionAssert.Contains(
                (string[])[.. group.Children.Select(c => c.Title)],
                "sqlite_sequence",
                "系统表要**照样列得出来** —— 藏起来是另一种撒谎(sqlite_sequence 是查自增位点的唯一途径)。");

            // 分组一定排在最后:用户对象先到眼前。
            Assert.AreEqual(
                SqlNodeKind.SystemGroup,
                tables.Children[^1].Kind,
                "系统对象分组要排在同级的最后。");
            return true;
        }
        finally
        {
            TryDelete(file);
        }
    }, CancellationToken.None);

    /// <summary>
    /// **"表"与"视图"两个分类共用同一次对象清单查询。**
    /// <para>
    /// 方言包刻意把表与视图做成一次查完(§7.2 的"表 (37)"计数要求),
    /// 而早先对象树把这份好意浪费掉了:两个分类节点各自调一次
    /// <c>ListRelationsAsync</c>,同一条系统表查询发两遍。对着一个几千张表的库,
    /// 多出来的那一遍是实打实的一秒。
    /// </para>
    /// <para>
    /// 这件事没有任何外部表征(两边都出数、都对),所以只能靠计数钉住。
    /// </para>
    /// </summary>
    [TestMethod]
    public Task 表与视图共用一次对象清单查询() => _session.Dispatch(async () =>
    {
        string file = NewDbPath();
        try
        {
            await using SqlSession session = await OpenAsync(file);
            await ExecAsync(session, "create table t(id integer primary key)");
            await ExecAsync(session, "create view v as select * from t");

            var viewModel = new SqlWorkspaceViewModel(session, Request(file), new("zh-Hans"), new TestPluginContext());
            await viewModel.Tree.InitializeAsync(CancellationToken.None);

            SqlTreeNode tables = viewModel.Tree.Roots[0];
            SqlTreeNode views = viewModel.Tree.Roots[1];
            await tables.LoadAsync(CancellationToken.None);
            await views.LoadAsync(CancellationToken.None);

            Assert.AreEqual(
                1,
                viewModel.Tree.MetadataQueries,
                "表与视图是同一份清单的两个投影,查两遍是白跑一趟。");
            CollectionAssert.Contains((string[])[.. tables.Children.Select(c => c.Title)], "t");
            CollectionAssert.Contains((string[])[.. views.Children.Select(c => c.Title)], "v");
            return true;
        }
        finally
        {
            TryDelete(file);
        }
    }, CancellationToken.None);

    /// <summary>
    /// **F5 必须真的重查,不能拿回缓存。**
    /// <para>
    /// 对象清单加了缓存之后,这条就成了必须钉住的事:只让节点重新装载,
    /// 它会原样拿回同一份缓存 —— F5 变成一个看起来在转、实际什么都没查的动作。
    /// 那比没有 F5 更坏:用户会据此认定"库里真的没有那张新表"。
    /// </para>
    /// </summary>
    [TestMethod]
    public Task F5要真的重查而不是拿回缓存() => _session.Dispatch(async () =>
    {
        string file = NewDbPath();
        try
        {
            await using SqlSession session = await OpenAsync(file);
            await ExecAsync(session, "create table before_refresh(id integer primary key)");

            var viewModel = new SqlWorkspaceViewModel(session, Request(file), new("zh-Hans"), new TestPluginContext());
            await viewModel.Tree.InitializeAsync(CancellationToken.None);
            SqlTreeNode tables = viewModel.Tree.Roots[0];
            await tables.LoadAsync(CancellationToken.None);
            Assert.AreEqual(1, tables.Children.Count);

            // 在树背后建一张新表 —— 正是"我刚建完表,按一下 F5"那个场景。
            await ExecAsync(session, "create table after_refresh(id integer primary key)");

            viewModel.Tree.Selected = tables;
            await viewModel.Tree.RefreshAsync(tables, CancellationToken.None);

            CollectionAssert.Contains(
                (string[])[.. tables.Children.Select(c => c.Title)],
                "after_refresh",
                "F5 之后新建的表还是看不见 —— 缓存没作废。");
            Assert.AreEqual(2, viewModel.Tree.MetadataQueries, "F5 要真的再发一条查询。");
            return true;
        }
        finally
        {
            TryDelete(file);
        }
    }, CancellationToken.None);

    /// <summary>
    /// **过滤词对已经展开的节点立刻生效。**
    /// <para>
    /// 早先过滤只在装载那一刻参与一句 <c>Where</c>,于是改过滤词对已经展开的分类
    /// 一点反应都没有 —— 那个输入框看起来能用,实际上只对"改完再展开"的那次生效。
    /// 一个不起作用的控件比没有更坏。
    /// </para>
    /// </summary>
    [TestMethod]
    public Task 过滤词对已展开的节点立刻生效() => _session.Dispatch(async () =>
    {
        string file = NewDbPath();
        try
        {
            await using SqlSession session = await OpenAsync(file);
            await ExecAsync(session, "create table orders(id integer primary key)");
            await ExecAsync(session, "create table customers(id integer primary key)");

            var viewModel = new SqlWorkspaceViewModel(session, Request(file), new("zh-Hans"), new TestPluginContext());
            await viewModel.Tree.InitializeAsync(CancellationToken.None);
            SqlTreeNode tables = viewModel.Tree.Roots[0];
            await tables.LoadAsync(CancellationToken.None);
            Assert.AreEqual(2, tables.Children.Count);

            // 展开**之后**才敲过滤词 —— 这正是旧实现失效的那条路径。
            viewModel.Tree.Filter = "ord";
            CollectionAssert.AreEqual(
                (string[])["orders"],
                (string[])[.. tables.Children.Select(c => c.Title)],
                "过滤要立刻作用在已经展开的节点上。");
            Assert.AreEqual("(1)", tables.CountText, "计数要跟着可见项走,不能报一个对不上的总数。");

            viewModel.Tree.Filter = "";
            Assert.AreEqual(2, tables.Children.Count, "清空过滤词要把节点还回来。");
            return true;
        }
        finally
        {
            TryDelete(file);
        }
    }, CancellationToken.None);

    /// <summary>
    /// **过滤词要穿透「系统对象」分组** —— 走的是「先敲词、后展开」这一序。
    /// <para>
    /// 与 <c>过滤词对已展开的节点立刻生效</c> 正好反序,而两条走的是不同代码路径:
    /// 那条走 <c>ApplyFilter</c> 的深度递归,这条走懒加载完成后的浅层 <c>Project</c>。
    /// </para>
    /// <para>
    /// 系统对象分组是唯一一种<b>生下来就已装载</b>的节点(子节点建好就在手上)。
    /// 浅层投影只往下推一格过滤词、不重投影,于是它的 <c>Children</c> 保持着未过滤的全量:
    /// 分组恒可见、计数也把全量算进去。界面上就是一个不匹配过滤词的系统表明晃晃挂在那儿。
    /// </para>
    /// </summary>
    [TestMethod]
    public Task 过滤词要穿透系统对象分组() => _session.Dispatch(async () =>
    {
        string file = NewDbPath();
        try
        {
            await using SqlSession session = await OpenAsync(file);
            await ExecAsync(session, "create table orders(id integer primary key)");
            // AUTOINCREMENT 必然逼出 sqlite_sequence,而它会被标成系统对象。
            await ExecAsync(session, "create table t2(id integer primary key autoincrement)");

            var viewModel = new SqlWorkspaceViewModel(session, Request(file), new("zh-Hans"), new TestPluginContext());
            await viewModel.Tree.InitializeAsync(CancellationToken.None);

            // **先敲过滤词,此时「表」还没展开。**
            viewModel.Tree.Filter = "zzz";
            SqlTreeNode tables = viewModel.Tree.Roots[0];
            await tables.LoadAsync(CancellationToken.None);

            CollectionAssert.AreEqual(
                (string[])[],
                (string[])[.. tables.Children.Select(c => c.Title)],
                "没有一个对象匹配 zzz,系统对象分组也不该留下来。");
            Assert.AreEqual("", tables.CountText, "计数要跟着可见项走,不能把分组里的全量算进来。");

            // 换一个真的匹配得上的词:用户表出来,系统表仍然不出来。
            viewModel.Tree.Filter = "ord";
            CollectionAssert.AreEqual(
                (string[])["orders"],
                (string[])[.. tables.Children.Select(c => c.Title)],
                "sqlite_sequence 不匹配 ord,分组不该因为它而出现。");
            Assert.AreEqual("(1)", tables.CountText);

            // 清空:用户表与系统对象分组一起回来。
            viewModel.Tree.Filter = "";
            string[] restored = [.. tables.Children.Select(c => c.Title)];
            CollectionAssert.Contains(restored, "orders");
            Assert.IsTrue(
                tables.Children.Any(c => c.Kind == SqlNodeKind.SystemGroup),
                "清空过滤词之后系统对象分组要回来。");
            return true;
        }
        finally
        {
            TryDelete(file);
        }
    }, CancellationToken.None);

    /// <summary>
    /// **在「系统对象」分组上按 F5 不能是空操作。**
    /// <para>
    /// 它是唯一一种"展得开、却重装不了"的节点:子节点建好就在手上,没有加载器。
    /// 树那一层的 F5 若按 <c>CanExpand</c> 判,它就从"自己重装"与"全树兜底"两边的缝里漏过去,
    /// 按下去什么都不发生 —— 而那正是本插件反复反对的"看起来在转、实际什么都没查"。
    /// </para>
    /// </summary>
    [TestMethod]
    public Task 在系统对象分组上按F5要落到全树重装() => _session.Dispatch(async () =>
    {
        string file = NewDbPath();
        try
        {
            await using SqlSession session = await OpenAsync(file);
            await ExecAsync(session, "create table t2(id integer primary key autoincrement)");

            var viewModel = new SqlWorkspaceViewModel(session, Request(file), new("zh-Hans"), new TestPluginContext());
            await viewModel.Tree.InitializeAsync(CancellationToken.None);
            SqlTreeNode tables = viewModel.Tree.Roots[0];
            await tables.LoadAsync(CancellationToken.None);
            SqlTreeNode group = tables.Children.Single(c => c.Kind == SqlNodeKind.SystemGroup);

            int before = viewModel.Tree.MetadataQueries;
            await viewModel.Tree.RefreshAsync(group, CancellationToken.None);

            // 断言的是"树真的被重建了",而不是"发了几条查询" —— SQLite 上分类节点是懒加载的,
            // 全树重装本身一条查询都不发,拿查询数当观测点会把一个正确的实现判红。
            Assert.IsFalse(
                ReferenceEquals(viewModel.Tree.Roots[0], tables),
                "在系统对象分组上按 F5 之后树还是原来那些节点 —— 那是一个彻底的空操作。");

            // 再展开一次,证明缓存也一并作废了(不是拿回旧清单)。
            SqlTreeNode fresh = viewModel.Tree.Roots[0];
            await fresh.LoadAsync(CancellationToken.None);
            Assert.IsGreaterThan(before, viewModel.Tree.MetadataQueries, "F5 之后重新展开要真的再查一次。");
            return true;
        }
        finally
        {
            TryDelete(file);
        }
    }, CancellationToken.None);

    /// <summary>
    /// 按真实形状造一个库 / schema 节点。
    /// <para>
    /// 关键是**带上 <c>Target</c> 也带上懒加载器** —— 真机上的库节点就是这样:
    /// 正因为它有 <c>Target</c>,旧判定才会把它当成表。造一个没有 <c>Target</c> 的
    /// 假节点等于把要验的那件事绕过去了。
    /// </para>
    /// </summary>
    private static SqlTreeNode Node(string name, SqlNodeKind kind, SqlObjectKind objectKind) =>
        new(name, kind, new(objectKind, name),
            (_, _) => Task.FromResult<IReadOnlyList<SqlTreeNode>>([]));

    private static string NewDbPath() => Path.Combine(Path.GetTempPath(), $"guard-{Guid.NewGuid():N}.db");

    private static WorkspaceConnectRequest Request(string file) => new()
    {
        SessionId = "guard",
        Host = file,
        Port = 1,
        DisplayName = "guard-test",
        Settings = new Dictionary<string, string>(StringComparer.Ordinal) { ["readOnly"] = "false" }
    };

    private static Task<SqlSession> OpenAsync(string file) =>
        SqlSession.OpenAsync(Request(file), SqlDialect.Sqlite, new("zh-Hans"));

    private static async Task ExecAsync(SqlSession session, string sql)
    {
        await using System.Data.Common.DbCommand command = session.Metadata.Raw.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch (IOException)
        {
            // 临时文件删不掉不该让测试失败。
        }
    }
}

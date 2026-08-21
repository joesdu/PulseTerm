using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using VelaShell.Plugin.Sql;
using VelaShell.Plugin.Sql.Metadata;
using VelaShell.Plugin.Sql.Ui;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Sql.Tests;

/// <summary>
/// 截图验界面。
/// <para>
/// <b>可视树断言与"看起来对"是两件事。</b> 前面那几条用例能保证控件在树里、
/// 文字绑对了,但它们对**画出来什么样**一无所知:控件被裁掉、宽度塌成 0、
/// 两块内容叠在一起、深色主题下前景与背景同色 —— 每一种在可视树里都是完全正常的。
/// </para>
/// <para>
/// 所以这一组把真渲染的一帧存成 PNG 交给人看。断言只守最低限:**帧出得来、不是一片空白**。
/// 真正的价值在产物本身 —— 它是这两页第一次被人眼看见。
/// </para>
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class SqlPanelScreenshotTests
{
    private static HeadlessUnitTestSession _session = null!;

    /// <summary>MSTest 注入的上下文。</summary>
    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SqlPanelScreenshotTests).Assembly);

    // **不 Dispose 这个会话。** GetOrStartForAssembly 给的是**整个程序集共用的一个**,
    // 谁先跑完谁 Dispose,另一个类的用例就会全部炸在
    // "Session was already disposed" 上 —— 而且报错完全指不到症结。
    // 与 Redis / AI 插件的 UI 测试同一条口径:会话随进程结束。

    /// <summary>结构页(含改结构那一段)的一帧。</summary>
    [TestMethod]
    public Task 截图_结构页与表设计器() => _session.Dispatch(async () =>
    {
        string file = Path.Combine(Path.GetTempPath(), $"shot-{Guid.NewGuid():N}.db");
        try
        {
            await using SqlSession session = await OpenAsync(file);
            await ExecAsync(session, """
                create table orders(
                  id integer primary key autoincrement,
                  code text not null unique,
                  amount real not null default 0,
                  total real generated always as (amount * 1.13) stored,
                  memo text
                )
                """);
            await ExecAsync(session, "create index ix_orders_code on orders(code)");

            var viewModel = new SqlWorkspaceViewModel(session, Request(file), new("zh-Hans"), new TestPluginContext());
            var view = new SqlWorkspaceView(viewModel);
            var window = new Window { Width = 1280, Height = 800, Content = view };
            ApplyTokens(window);
            window.Show();
            await viewModel.InitializeAsync(CancellationToken.None);
            Dispatcher.UIThread.RunJobs();

            SqlTreeNode tables = viewModel.Tree.Roots[0];
            await tables.LoadAsync(CancellationToken.None);
            viewModel.OpenStructure(tables.Children.First(c => c.Title == "orders"));
            var structure = (SqlStructureTabViewModel)viewModel.ActiveTab!;
            for (int i = 0; i < 200 && structure.Columns.Count == 0; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(20);
            }
            Dispatcher.UIThread.RunJobs();

            Save(window, "structure-designer");
            window.Close();
        }
        finally
        {
            TryDelete(file);
        }
            // **这一行不是凑数,少了它整条用例的断言全部失效。**
        // HeadlessUnitTestSession 只有 Dispatch(Action) 与 Dispatch<T>(Func<Task<T>>) 两族重载,
        // **没有 Func<Task> 那一支**。不返回值的 async lambda 于是被绑到 Action 上、变成 async void:
        // 断言异常落在调度线程上没人接,而 Dispatch 返回的 Task 早就完成了 —— 编译通过、测试恒绿。
        // 实测:把 Assert.Fail 放在用例第一行,dotnet test 照样报全过。
        // 有了返回值才会绑到 Func<Task<T>>,异常才会随 Task 传回来。
        return true;
    }, CancellationToken.None);

    /// <summary>
    /// 对象树的一帧:用户表在上、系统对象收在一个分组里、每一类一个图标。
    /// <para>
    /// <b>这一帧盯的是三件在可视树里验不出来的事</b>:
    /// ① 系统对象<b>看起来</b>是不是真的与用户表分开了(断言只能验节点在哪个父下面,
    ///    验不了它在屏幕上是不是仍然挤在一起);
    /// ② 图标画没画出来 —— 转换器拿不到资源时返回 <see langword="null" />,
    ///    <c>Path</c> 静默不画,而"没有图标"在断言里完全看不见;
    /// ③ <c>TreeDataTemplate</c> 上那句 <c>x:DataType="vm:SqlTreeNode"</c> 有没有把
    ///    绑定解析到节点上。写错的话每一行画出来的都是**文档标题**(工作台 VM 上也有 Title),
    ///    而且编译期不报错、可视树里也照样有 TextBlock。
    /// </para>
    /// </summary>
    [TestMethod]
    public Task 截图_对象树的系统分组与图标() => _session.Dispatch(async () =>
    {
        string file = Path.Combine(Path.GetTempPath(), $"shot-{Guid.NewGuid():N}.db");
        try
        {
            await using SqlSession session = await OpenAsync(file);
            await ExecAsync(session, "create table customers(id integer primary key autoincrement, name text)");
            await ExecAsync(session, "create table orders(id integer primary key, customer_id integer, total real)");
            await ExecAsync(session, "create view v_open_orders as select * from orders where total > 0");

            var viewModel = new SqlWorkspaceViewModel(session, Request(file), new("zh-Hans"), new TestPluginContext());
            var view = new SqlWorkspaceView(viewModel);
            var window = new Window { Width = 1280, Height = 800, Content = view };
            ApplyTokens(window);
            window.Show();
            await viewModel.InitializeAsync(CancellationToken.None);
            Dispatcher.UIThread.RunJobs();

            SqlTreeNode tables = viewModel.Tree.Roots[0];
            await tables.LoadAsync(CancellationToken.None);
            tables.IsExpanded = true;
            // AUTOINCREMENT 必然带出 sqlite_sequence,它要落在"系统对象"分组里。
            SqlTreeNode systemGroup = tables.Children.Single(c => c.Kind == SqlNodeKind.SystemGroup);
            await systemGroup.LoadAsync(CancellationToken.None);
            systemGroup.IsExpanded = true;

            SqlTreeNode views = viewModel.Tree.Roots[1];
            await views.LoadAsync(CancellationToken.None);
            views.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();

            Save(window, "object-tree-system-group");
            window.Close();
        }
        finally
        {
            TryDelete(file);
        }
        // 见文件头:少了这句,整条用例的断言会被静默吞掉。
        return true;
    }, CancellationToken.None);

    /// <summary>
    /// **真机 PostgreSQL 的一帧** —— 用户报的那两条,在一张图上同时验完。
    /// <para>
    /// 连接的"数据库"栏**留空**(表单提示原文是"留空则列出你能看见的每一个库"),
    /// Npgsql 落到 <c>postgres</c> 库 —— 这正是那条死路的入口:
    /// 修复前每一个库点开都是一个空的 <c>public</c>。
    /// </para>
    /// <para>
    /// 这一帧要看到的是:根上用户库在前、<c>postgres</c> 收进「系统对象」;
    /// 展开 <c>ops_pg</c> 有真的 schema;<c>public</c> 底下四个分类都在;
    /// 「表」里是 <c>ops_pg</c> 的表<b>而不是 <c>postgres</c> 的空清单</b>。
    /// </para>
    /// <para>没有 PostgreSQL 时 <c>Inconclusive</c>,按仓库惯例不算失败。</para>
    /// </summary>
    [TestMethod]
    public Task 截图_真机PostgreSQL的对象树() => _session.Dispatch(async () =>
    {
        if (!await EnsureShotDatabaseAsync())
        {
            Assert.Inconclusive("没有可用的 PostgreSQL(127.0.0.1:55432)。");
            return true;
        }
        SqlSession session = await SqlSession.OpenAsync(PostgresRequest(), SqlDialect.PostgreSql, new("zh-Hans"));

        await using (session)
        {
            var viewModel = new SqlWorkspaceViewModel(
                session, PostgresRequest(), new("zh-Hans"), new TestPluginContext());
            var view = new SqlWorkspaceView(viewModel);
            var window = new Window { Width = 1280, Height = 800, Content = view };
            ApplyTokens(window);
            window.Show();
            await viewModel.InitializeAsync(CancellationToken.None);
            Dispatcher.UIThread.RunJobs();

            SqlTreeNode database = viewModel.Tree.Roots.First(n => n.Title == ShotDatabase);
            await database.LoadAsync(CancellationToken.None);
            database.IsExpanded = true;

            SqlTreeNode schema = database.Children.First(n => n.Title == "public");
            await schema.LoadAsync(CancellationToken.None);
            schema.IsExpanded = true;

            SqlTreeNode tables = schema.Children.First(n => n.Kind == SqlNodeKind.Category);
            await tables.LoadAsync(CancellationToken.None);
            tables.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();

            // 图之外再钉一句:这一帧里的表清单必须真的来自另一个库。
            CollectionAssert.Contains(
                (string[])[.. tables.Children.Select(c => c.Title)],
                ShotProbe,
                $"连在 postgres 上展开 {ShotDatabase} 却看不见它的表 —— 那正是被报上来的故障。");

            Save(window, "postgres-object-tree");
            window.Close();
        }
        // 见文件头:少了这句,上面的断言会被静默吞掉。
        return true;
    }, CancellationToken.None);

    /// <summary>运维面在 SQLite 上的一帧 —— 要看到的是**那句说明**,不是一张空表。</summary>
    [TestMethod]
    public Task 截图_运维面不支持时的说明() => _session.Dispatch(async () =>
    {
        string file = Path.Combine(Path.GetTempPath(), $"shot-{Guid.NewGuid():N}.db");
        try
        {
            await using SqlSession session = await OpenAsync(file);
            var viewModel = new SqlWorkspaceViewModel(session, Request(file), new("zh-Hans"), new TestPluginContext());
            var view = new SqlWorkspaceView(viewModel);
            var window = new Window { Width = 1280, Height = 800, Content = view };
            ApplyTokens(window);
            window.Show();
            viewModel.OpenOpsCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Save(window, "ops-unsupported");
            window.Close();
        }
        finally
        {
            TryDelete(file);
        }
            // **这一行不是凑数,少了它整条用例的断言全部失效。**
        // HeadlessUnitTestSession 只有 Dispatch(Action) 与 Dispatch<T>(Func<Task<T>>) 两族重载,
        // **没有 Func<Task> 那一支**。不返回值的 async lambda 于是被绑到 Action 上、变成 async void:
        // 断言异常落在调度线程上没人接,而 Dispatch 返回的 Task 早就完成了 —— 编译通过、测试恒绿。
        // 实测:把 Assert.Fail 放在用例第一行,dotnet test 照样报全过。
        // 有了返回值才会绑到 Func<Task<T>>,异常才会随 Task 传回来。
        return true;
    }, CancellationToken.None);

    /// <summary>DDL 确认框的一帧 —— 这是发出去之前用户唯一会看的那一屏。</summary>
    [TestMethod]
    public Task 截图_DDL确认框() => _session.Dispatch(async () =>
    {
        string file = Path.Combine(Path.GetTempPath(), $"shot-{Guid.NewGuid():N}.db");
        try
        {
            await using SqlSession session = await OpenAsync(file);
            await ExecAsync(session, "create table orders(id integer primary key, code text)");

            var viewModel = new SqlWorkspaceViewModel(session, Request(file), new("zh-Hans"), new TestPluginContext());
            var view = new SqlWorkspaceView(viewModel);
            var window = new Window { Width = 1280, Height = 800, Content = view };
            ApplyTokens(window);
            window.Show();
            await viewModel.InitializeAsync(CancellationToken.None);
            Dispatcher.UIThread.RunJobs();

            SqlTreeNode tables = viewModel.Tree.Roots[0];
            await tables.LoadAsync(CancellationToken.None);
            viewModel.OpenStructure(tables.Children.First(c => c.Title == "orders"));
            var structure = (SqlStructureTabViewModel)viewModel.ActiveTab!;
            for (int i = 0; i < 200 && structure.Columns.Count == 0; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(20);
            }

            structure.NewColumnName = "shipped_at";
            structure.NewColumnType = "TEXT";
            structure.AddColumnCommand.Execute(null);
            for (int i = 0; i < 200 && !structure.HasConfirmation; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
            }
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(structure.HasConfirmation, "确认框没弹出来,这一帧就没有意义。");

            Save(window, "ddl-confirmation");
            structure.RejectCommand.Execute(null);
            window.Close();
        }
        finally
        {
            TryDelete(file);
        }
            // **这一行不是凑数,少了它整条用例的断言全部失效。**
        // HeadlessUnitTestSession 只有 Dispatch(Action) 与 Dispatch<T>(Func<Task<T>>) 两族重载,
        // **没有 Func<Task> 那一支**。不返回值的 async lambda 于是被绑到 Action 上、变成 async void:
        // 断言异常落在调度线程上没人接,而 Dispatch 返回的 Task 早就完成了 —— 编译通过、测试恒绿。
        // 实测:把 Assert.Fail 放在用例第一行,dotnet test 照样报全过。
        // 有了返回值才会绑到 Func<Task<T>>,异常才会随 Task 传回来。
        return true;
    }, CancellationToken.None);

    /// <summary>
    /// 给窗口挂一份主题令牌。
    /// <para>
    /// <b>为什么截图要单独做这件事</b>:共用的 headless 宿主
    /// (<see cref="SqlPanelHeadlessApp" />)<b>刻意一个 <c>Vela*</c> 令牌都不给</b>,
    /// 那是用来守"宿主令牌缺席时面板照样装载"的。但在那套环境下截图毫无意义 ——
    /// 面板里所有走 <c>{DynamicResource VelaText*}</c> 的前景色都解析不到,
    /// 画出来是一片黑底黑字:**看起来像"什么都没渲染",其实只是没有配色**。
    /// (第一版截图正是这样,差点被当成布局 bug。)
    /// </para>
    /// <para>
    /// 取值抄自 <c>VelaShell.Controls/Themes/VelaTokens.axaml</c> 的深色档,
    /// 于是这几张图与用户真正看到的是同一套配色。这里不引 <c>VelaShell.Controls</c> 项目,
    /// 是为了不让插件测试反过来依赖宿主。
    /// </para>
    /// </summary>
    private static void ApplyTokens(Window window)
    {
        var tokens = new ResourceDictionary
        {
            ["VelaBgPage"] = new SolidColorBrush(Color.Parse("#191A21")),
            ["VelaBgSidebar"] = new SolidColorBrush(Color.Parse("#252734")),
            ["VelaBgSurface"] = new SolidColorBrush(Color.Parse("#343746")),
            ["VelaBgInput"] = new SolidColorBrush(Color.Parse("#282A36")),
            ["VelaBgActive"] = new SolidColorBrush(Color.Parse("#44475A")),
            ["VelaBorderPrimary"] = new SolidColorBrush(Color.Parse("#3B3E51")),
            ["VelaTextPrimary"] = new SolidColorBrush(Color.Parse("#F8F8F2")),
            ["VelaTextSecondary"] = new SolidColorBrush(Color.Parse("#B0B8D6")),
            ["VelaTextTertiary"] = new SolidColorBrush(Color.Parse("#6272A4")),
            ["VelaError"] = new SolidColorBrush(Color.Parse("#FF5555")),
            ["VelaWarning"] = new SolidColorBrush(Color.Parse("#FFB86C")),
            ["VelaFontSize10"] = 10d,
            ["VelaFontSize11"] = 11d,
            ["VelaFontSize12"] = 12d,
            ["VelaUiMonoFont"] = new FontFamily("Consolas, monospace")
        };
        window.Resources.MergedDictionaries.Add(tokens);
        Application.Current?.Resources.MergedDictionaries.Add(Icons());
    }

    /// <summary>
    /// 对象树的节点图标。
    /// <para>
    /// <b>为什么要在测试里复制一份</b>:图标是宿主主题字典里那套 lucide 图集,而插件测试
    /// <b>刻意不引 <c>VelaShell.Controls</c></b>(理由与 <see cref="ApplyTokens" /> 上写的同一条:
    /// 不让插件测试反过来依赖宿主)。缺了它们截图上是一排空白 ——
    /// 而"图标画不出来"与"图标位置不对"在图上长得一样,验不出东西。
    /// </para>
    /// <para>
    /// 路径数据逐字抄自 <c>VelaShell.Controls/Themes/Icons.axaml</c>;
    /// 挂在 <c>Application</c> 上而不是窗口上,因为转换器读的就是应用级资源。
    /// </para>
    /// </summary>
    /// <returns>图标字典。</returns>
    private static ResourceDictionary Icons() => new()
    {
        ["Icon.hard-drive"] = StreamGeometry.Parse("M22 12H2 M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11Z M6 16h.01 M10 16h.01"),
        ["Icon.folder"] = StreamGeometry.Parse("M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z"),
        ["Icon.folder-open"] = StreamGeometry.Parse("M6 14l1.5-2.9A2 2 0 0 1 9.24 10H20a2 2 0 0 1 1.94 2.5l-1.54 6a2 2 0 0 1-1.95 1.5H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h3.9a2 2 0 0 1 1.69.9l.81 1.2a2 2 0 0 0 1.67.9H18a2 2 0 0 1 2 2v2"),
        ["Icon.settings"] = StreamGeometry.Parse("M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2Z M15 12a3 3 0 1 1-6 0 3 3 0 0 1 6 0Z"),
        ["Icon.grid-3x3"] = StreamGeometry.Parse("M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2Z M3 9h18 M3 15h18 M9 3v18 M15 3v18"),
        ["Icon.eye"] = StreamGeometry.Parse("M2.062 12.348a1 1 0 0 1 0-.696 10.75 10.75 0 0 1 19.876 0 1 1 0 0 1 0 .696 10.75 10.75 0 0 1-19.876 0 M15 12a3 3 0 1 1-6 0 3 3 0 0 1 6 0Z"),
        ["Icon.layers"] = StreamGeometry.Parse("M12.83 2.18a2 2 0 0 0-1.66 0L2.6 6.08a1 1 0 0 0 0 1.83l8.58 3.91a2 2 0 0 0 1.66 0l8.58-3.9a1 1 0 0 0 0-1.83Z M2 12l8.58 3.91a2 2 0 0 0 1.66 0L21 12 M2 17l8.58 3.91a2 2 0 0 0 1.66 0L21 17"),
        ["Icon.terminal"] = StreamGeometry.Parse("M4 17l6-6-6-6 M12 19h8"),
        ["Icon.zap"] = StreamGeometry.Parse("M13 2 3 14h9l-1 8 10-12h-9l1-8Z"),
        ["Icon.list-ordered"] = StreamGeometry.Parse("M10 6h11 M10 12h11 M10 18h11 M4 6h1v4 M4 10h2 M6 18H4c0-1 2-2 2-3s-1-1.5-2-1"),
        ["Icon.columns-2"] = StreamGeometry.Parse("M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2Z M12 3v18"),
        ["Icon.circle-alert"] = StreamGeometry.Parse("M22 12a10 10 0 1 1-20 0 10 10 0 0 1 20 0Z M12 8v4 M12 16h.01")
    };

    /// <summary>
    /// 存一帧,并守住最低限:帧出得来、且**不是一片纯色**。
    /// <para>
    /// 纯色那一条不是凑数 —— 渲染器没接上、或者内容被裁到窗口外时,
    /// <c>CaptureRenderedFrame</c> 照样返回一张完整的、全是背景色的图。
    /// </para>
    /// </summary>
    private void Save(Window window, string name)
    {
        using WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.IsNotNull(frame, "真渲染器没出帧 —— 检查 UseSkia / UseHeadlessDrawing=false。");

        string directory = Path.Combine(Path.GetTempPath(), "vela-sql-shots");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{name}.png");
        frame.Save(path, PngBitmapEncoderOptions.Default);
        TestContext.WriteLine($"screenshot: {path}");
        TestContext.AddResultFile(path);

        Assert.IsTrue(new FileInfo(path).Length > 4096, "帧太小,多半什么都没画出来。");
        Assert.IsTrue(IsNotFlat(frame), "整帧只有一种颜色 —— 内容没画出来,或者被裁到窗口外了。");
    }

    private static bool IsNotFlat(WriteableBitmap frame)
    {
        using ILockedFramebuffer buffer = frame.Lock();
        int height = buffer.Size.Height;
        int width = buffer.Size.Width;
        unsafe
        {
            byte* baseAddress = (byte*)buffer.Address;
            uint first = *(uint*)baseAddress;
            // 抽样够了:真画了东西的一帧,任意一条扫描线上都不可能全是同一个像素。
            for (int y = 0; y < height; y += 7)
            {
                uint* row = (uint*)(baseAddress + (y * buffer.RowBytes));
                for (int x = 0; x < width; x += 7)
                {
                    if (row[x] != first)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private static WorkspaceConnectRequest Request(string file) => new()
    {
        SessionId = "shot",
        Host = file,
        Port = 1,
        DisplayName = "shot",
        Settings = new Dictionary<string, string>(StringComparer.Ordinal) { ["readOnly"] = "false" }
    };

    private static Task<SqlSession> OpenAsync(string file) =>
        SqlSession.OpenAsync(Request(file), SqlDialect.Sqlite, new("zh-Hans"));

    /// <summary>
    /// 这一帧用的库。
    /// <para>
    /// <b>刻意不用 ops_pg。</b> 那个库是 <c>PostgreSqlOpsTests</c> / <c>PgBinaryFallbackTests</c>
    /// 建的,而 MSTest 不保证类间顺序 —— 单跑本类(<c>--filter ~SqlPanelScreenshotTests</c>)
    /// 或在一台有 PG 但没跑过那两组的机器上,<c>Roots.First(… == "ops_pg")</c> 会抛
    /// <c>Sequence contains no matching element</c>,而报错完全指不到症结。自建自用。
    /// </para>
    /// </summary>
    private const string ShotDatabase = "shot_pg";

    /// <summary>这一帧里要认出来的那张表。名字带前缀,免得与别人的探针表重名。</summary>
    private const string ShotProbe = "shot_tree_probe";

    /// <summary>
    /// 备好 <see cref="ShotDatabase" /> 与它里面那张探针表。
    /// <para>没有 PostgreSQL 时返回 <see langword="false" />(调用方 <c>Inconclusive</c>)。</para>
    /// </summary>
    /// <returns>库可用与否。</returns>
    private static async Task<bool> EnsureShotDatabaseAsync()
    {
        SqlSession? bootstrap = await TryOpenPostgresAsync();
        if (bootstrap is null)
        {
            return false;
        }
        await using (bootstrap)
        {
            object? exists = await bootstrap.Metadata.UseAsync(async (raw, token) =>
            {
                await using System.Data.Common.DbCommand probe = raw.CreateCommand();
                probe.CommandText = "select 1 from pg_catalog.pg_database where datname = @name";
                System.Data.Common.DbParameter name = probe.CreateParameter();
                name.ParameterName = "@name";
                name.Value = ShotDatabase;
                probe.Parameters.Add(name);
                return await probe.ExecuteScalarAsync(token).ConfigureAwait(false);
            });
            if (exists is null)
            {
                // PG 没有 CREATE DATABASE IF NOT EXISTS,而且它不能在事务里跑,只能先查再单独发。
                await ExecOnAsync(bootstrap, $"create database {ShotDatabase}");
            }
        }

        await using SqlSession seed = await SqlSession.OpenAsync(
            PostgresRequest(ShotDatabase), SqlDialect.PostgreSql, new("zh-Hans"));
        await ExecOnAsync(seed, $"drop table if exists public.{ShotProbe}");
        await ExecOnAsync(seed, $"create table public.{ShotProbe}(id int primary key, tag text)");
        return true;
    }

    /// <summary>在指定会话上发一条语句。走闸门,与产品代码同一条路。</summary>
    /// <param name="session">会话。</param>
    /// <param name="sql">语句。</param>
    /// <returns>等待句柄。</returns>
    private static Task ExecOnAsync(SqlSession session, string sql) =>
        session.Metadata.UseAsync(async (raw, token) =>
        {
            await using System.Data.Common.DbCommand command = raw.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        });

    /// <summary>
    /// 真机 PostgreSQL 的连接请求。**"数据库"这一栏默认留空** ——
    /// 那是用户实际走的那条路,也是缺陷的入口(见「截图_真机PostgreSQL的对象树」)。
    /// </summary>
    /// <param name="database">要落到哪个库;空表示留空(由驱动决定,实际会落到 postgres)。</param>
    /// <returns>连接请求。</returns>
    private static WorkspaceConnectRequest PostgresRequest(string database = "") => new()
    {
        SessionId = "shot-pg",
        Host = "127.0.0.1",
        Port = 55432,
        Username = "postgres",
        Password = "velaspike",
        DisplayName = "ops",
        Settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["readOnly"] = "false",
            ["database"] = database
        }
    };

    private static async Task<SqlSession?> TryOpenPostgresAsync()
    {
        try
        {
            return await SqlSession.OpenAsync(PostgresRequest(), SqlDialect.PostgreSql, new("zh-Hans"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

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

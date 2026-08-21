using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using VelaShell.Plugin.Sql;
using VelaShell.Plugin.Sql.Ui;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Sql.Tests;

/// <summary>
/// 面板的 headless 装载与主链路。
/// <para>
/// <b>AXAML 的错全是运行期才炸的</b>:样式选择器写错、模板里绑了个不存在的属性、
/// <c>x:DataType</c> 与实际 DataContext 对不上 —— 编译期一个都看不出来
/// (本轮就踩到一次:样式里的 <c>IsExpanded</c> 绑定被编译期绑定按外层
/// <c>x:DataType</c> 解析,只有真装载才发现)。所以这一组的第一价值就是"真装载一次"。
/// </para>
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class SqlPanelUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    /// <summary>MSTest 注入的上下文。</summary>
    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SqlPanelUiTests).Assembly);

    // **不 Dispose 这个会话。** GetOrStartForAssembly 给的是**整个程序集共用的一个**,
    // 谁先跑完谁 Dispose,另一个类的用例就会全部炸在
    // "Session was already disposed" 上 —— 而且报错完全指不到症结。
    // 与 Redis / AI 插件的 UI 测试同一条口径:会话随进程结束。

    /// <summary>面板真装载一次:控件树建得出来,编辑器与结果网格都在。</summary>
    [TestMethod]
    public Task 面板能装载并含有编辑器与结果网格() => _session.Dispatch(async () =>
    {
        string file = Path.Combine(Path.GetTempPath(), $"ui-{Guid.NewGuid():N}.db");
        try
        {
            await using SqlSession session = await OpenAsync(file);
            var viewModel = new SqlWorkspaceViewModel(session, Request(file), new("zh-Hans"), new TestPluginContext());
            var view = new SqlWorkspaceView(viewModel);
            var window = new Window { Width = 1200, Height = 700, Content = view };

            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.IsNotNull(view.GetVisualDescendants().OfType<TreeView>().FirstOrDefault(), "对象树控件不在。");
            Assert.IsNotNull(view.GetVisualDescendants().OfType<TextEditor>().FirstOrDefault(), "SQL 编辑器不在。");
            Assert.IsNotNull(view.GetVisualDescendants().OfType<DataGrid>().FirstOrDefault(), "结果网格不在。");
            Assert.AreEqual(1, viewModel.Tabs.Count, "打开面板就该有一个查询标签,而不是一片空白。");

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
    /// 主链路:装载对象树 → 双击一张表 → 网格里出现列与行。
    /// <para>这是用户打开这个插件之后做的第一件事,它必须真的能走通。</para>
    /// </summary>
    [TestMethod]
    public Task 双击一张表就能看到数据() => _session.Dispatch(async () =>
    {
        string file = Path.Combine(Path.GetTempPath(), $"ui-{Guid.NewGuid():N}.db");
        try
        {
            await using SqlSession session = await OpenAsync(file);
            await ExecAsync(session, "create table t(id integer primary key, name text, memo text)");
            await ExecAsync(session, "insert into t(name, memo) values('张三', null), ('李四', '')");

            var viewModel = new SqlWorkspaceViewModel(session, Request(file), new("zh-Hans"), new TestPluginContext());
            var view = new SqlWorkspaceView(viewModel);
            var window = new Window { Width = 1200, Height = 700, Content = view };
            window.Show();

            await viewModel.InitializeAsync(CancellationToken.None);
            Dispatcher.UIThread.RunJobs();

            SqlTreeNode tables = viewModel.Tree.Roots[0];
            await tables.LoadAsync(CancellationToken.None);
            SqlTreeNode table = tables.Children.First(c => c.Title == "t");

            viewModel.OpenData(table);
            // OpenData 会起一条查询;等它落地。
            for (int i = 0; i < 200 && viewModel.ActiveQueryTab!.Grid.Rows.Count == 0; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(20);
            }

            SqlGridViewModel grid = viewModel.ActiveQueryTab!.Grid;
            Assert.AreEqual(3, grid.Columns.Count, "三列都该在。");
            Assert.AreEqual(2, grid.Rows.Count, "两行都该在。");
            // 四态显示:NULL 与空串必须看得出区别。
            Assert.AreEqual("NULL", grid.Rows[0][2].Text);
            Assert.AreEqual("''", grid.Rows[1][2].Text);

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
    /// 只读连接上执行写操作:**在发出之前**被拒,而且错误文案要说清"是这条连接标了只读"。
    /// </summary>
    [TestMethod]
    public Task 只读连接拒绝写操作() => _session.Dispatch(async () =>
    {
        string file = Path.Combine(Path.GetTempPath(), $"ui-{Guid.NewGuid():N}.db");
        try
        {
            await using SqlSession session = await OpenAsync(file, readOnly: true);
            await ExecAsync(session, "create table t(id integer primary key)");

            var viewModel = new SqlWorkspaceViewModel(session, Request(file, readOnly: true), new("zh-Hans"), new TestPluginContext());
            SqlQueryTabViewModel tab = (SqlQueryTabViewModel)viewModel.Tabs[0];
            tab.Sql = "delete from t where id = 1";

            tab.ExecuteAllCommand.Execute(null);
            for (int i = 0; i < 100 && !tab.HasError; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
            }

            Assert.IsTrue(tab.HasError, "只读连接必须拒掉写操作。");
            StringAssert.Contains(tab.ErrorText, "只读");
            Assert.AreEqual(0, tab.Grid.Rows.Count);
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
    /// 结构页真渲染一次:模板匹配得上,四块内容都落到控件树里。
    /// <para>
    /// <b>DataTemplate 选不中是运行期才看得见的错</b>:<c>DataType</c> 写错一个字,
    /// Avalonia 不报错,它退回默认模板 —— 屏幕上出现的是
    /// <c>VelaShell.Plugin.Sql.Ui.SqlStructureTabViewModel</c> 这行类名。
    /// 编译、单元测试、`Tabs.Count` 断言全都照样绿。所以这里断言的是**渲染结果**:
    /// 建表语句的文字真的出现在某个控件里,而且类名**没有**出现在任何控件里。
    /// </para>
    /// </summary>
    [TestMethod]
    public Task 结构页真渲染出列与建表语句() => _session.Dispatch(async () =>
    {
        string file = Path.Combine(Path.GetTempPath(), $"ui-{Guid.NewGuid():N}.db");
        try
        {
            await using SqlSession session = await OpenAsync(file);
            await ExecAsync(session, "create table addr(id integer primary key, city text not null)");

            var viewModel = new SqlWorkspaceViewModel(session, Request(file), new("zh-Hans"), new TestPluginContext());
            var view = new SqlWorkspaceView(viewModel);
            var window = new Window { Width = 1200, Height = 700, Content = view };
            window.Show();

            await viewModel.InitializeAsync(CancellationToken.None);
            Dispatcher.UIThread.RunJobs();
            SqlTreeNode tables = viewModel.Tree.Roots[0];
            await tables.LoadAsync(CancellationToken.None);

            viewModel.OpenStructure(tables.Children.First(c => c.Title == "addr"));
            var structure = (SqlStructureTabViewModel)viewModel.ActiveTab!;
            for (int i = 0; i < 200 && structure.Columns.Count == 0; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(20);
            }
            Dispatcher.UIThread.RunJobs();

            string[] rendered = [.. view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "")];
            string[] selectable = [.. view.GetVisualDescendants().OfType<SelectableTextBlock>().Select(t => t.Text ?? "")];

            Assert.IsTrue(rendered.Any(t => t == "city"), "列名没渲染出来,模板多半没选中。");
            Assert.IsTrue(
                selectable.Any(t => t.Contains("create table addr", StringComparison.OrdinalIgnoreCase)),
                "建表语句没渲染出来。");
            // 退回默认模板的特征:控件里出现的是视图模型的类名。
            Assert.IsFalse(
                rendered.Concat(selectable).Any(t => t.Contains(nameof(SqlStructureTabViewModel), StringComparison.Ordinal)),
                "屏幕上出现了视图模型类名 —— DataTemplate 没匹配上,退回了默认模板。");

            // 改结构那一段:可写连接 + 真表 + SQLite 包给得出 DDL,三条都满足,所以它必须真出现。
            // 这一段是整个模板里唯一带 IsVisible 开关的,写反了就是"功能做了但没人看得见"。
            Assert.IsTrue(structure.CanDesign, "可写连接上的表应当能改结构。");
            Assert.IsTrue(
                view.GetVisualDescendants().OfType<ComboBox>()
                    .Any(c => c.ItemCount == structure.TypeChoices.Count && c.IsEffectivelyVisible),
                "类型候选下拉没渲染出来 —— 改结构那一段没露面。");
            Assert.IsTrue(
                view.GetVisualDescendants().OfType<Button>()
                    .Any(b => (b.Content as string) == structure.AddColumnLabel && b.IsEffectivelyVisible),
                "「加列」按钮没渲染出来。");

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
    /// 运维面在 SQLite 上渲染的是**一句说明**,不是一张空表。
    /// <para>
    /// 空表与"现在真的没人连"在屏幕上长得一模一样(§7.8),而这两件事差得很远。
    /// 这条断言的是可见性真的切过去了 —— <c>IsVisible="{Binding !IsSupported}"</c>
    /// 这种取反绑定写错了也不报错,只是永远不显示。
    /// </para>
    /// </summary>
    [TestMethod]
    public Task 运维面在SQLite上渲染的是说明而不是空表() => _session.Dispatch(async () =>
    {
        string file = Path.Combine(Path.GetTempPath(), $"ui-{Guid.NewGuid():N}.db");
        try
        {
            await using SqlSession session = await OpenAsync(file);
            var viewModel = new SqlWorkspaceViewModel(session, Request(file), new("zh-Hans"), new TestPluginContext());
            var view = new SqlWorkspaceView(viewModel);
            var window = new Window { Width = 1200, Height = 700, Content = view };
            window.Show();

            viewModel.OpenOpsCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            var ops = (SqlOpsTabViewModel)viewModel.ActiveTab!;
            Assert.IsFalse(ops.IsSupported);

            TextBlock? notice = view.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => (t.Text ?? "").Contains("SQLite", StringComparison.Ordinal));
            Assert.IsNotNull(notice, "说明文字没渲染出来。");
            Assert.IsTrue(notice.IsVisible, "说明文字渲染了却不可见 —— 取反绑定没生效。");

            // 说明与空表不能同时在场:两张会话/锁网格此刻应当整个不可见。
            Assert.IsTrue(
                view.GetVisualDescendants().OfType<DataGrid>().All(g => !g.IsEffectivelyVisible),
                "方言不支持时不该还摆着一张空网格。");

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

    private static WorkspaceConnectRequest Request(string file, bool readOnly = false) => new()
    {
        SessionId = "ui",
        Host = file,
        Port = 1,
        DisplayName = "ui-test",
        Settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["readOnly"] = readOnly ? "true" : "false"
        }
    };

    private static Task<SqlSession> OpenAsync(string file, bool readOnly = false) =>
        SqlSession.OpenAsync(Request(file, readOnly), SqlDialect.Sqlite, new("zh-Hans"));

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

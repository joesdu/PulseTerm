using System.Data.Common;
using VelaShell.Plugin.Sql;
using VelaShell.Plugin.Sql.Metadata;
using VelaShell.Plugin.Sql.Ui;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Sql.Tests;

/// <summary>
/// 表设计器:加列 / 删列 / 建索引 / 删索引。
/// <para>
/// <b>这一组的重点不是"DDL 拼得对"</b> —— 那一半在各方言包自己的用例里。
/// 这里验的是**发出去之前的那几道闸**:只读连接拦不拦得住、方言给不出 DDL 时说不说话、
/// 确认框否掉之后是不是真的一条都没发。DDL 与改数据不同:<c>DROP COLUMN</c> 发出去那一刻
/// 数据就没了,多数引擎还不给回滚,所以"没确认就别发"必须是可测的,而不是靠读代码相信。
/// </para>
/// </summary>
[TestClass]
public sealed class SqlDesignerTests
{
    private static readonly Loc Localization = new("zh-Hans");

    /// <summary>MSTest 注入的上下文。</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>加一列:确认之后真落到库里,而且这一页立刻重读得到它。</summary>
    [TestMethod]
    public async Task 加列_确认之后真的加上了并且页面自动重读()
    {
        string file = Path.Combine(Path.GetTempPath(), $"dz-{Guid.NewGuid():N}.db");
        try
        {
            await using SqlSession session = await OpenAsync(file);
            await ExecAsync(session, "create table t(id integer primary key)");
            SqlStructureTabViewModel tab = await LoadTabAsync(session, "t");
            Assert.AreEqual(1, tab.Columns.Count);

            tab.NewColumnName = "note";
            tab.NewColumnType = "TEXT";
            tab.NewColumnNullable = true;
            Task apply = RunAsync(tab.AddColumnCommand);

            // 没确认之前,一条都不许发。
            await WaitAsync(() => tab.HasConfirmation);
            Assert.AreEqual(1, await CountColumnsAsync(session, "t"), "确认之前就把 DDL 发出去了。");
            StringAssert.Contains(tab.Confirmation!.Message, "note", "确认框里给的应当是真要发的那条原文。");

            tab.ConfirmCommand.Execute(null);
            await apply;

            Assert.AreEqual(2, await CountColumnsAsync(session, "t"), "确认之后该真的加上。");
            Assert.AreEqual(2, tab.Columns.Count, "结构页应当自动重读 —— 否则屏幕上显示的是过期结构。");
            Assert.IsTrue(tab.Columns.Any(c => c.Name == "note"));
        }
        finally
        {
            TryDelete(file);
        }
    }

    /// <summary>
    /// <b>否掉确认 = 一条都没发。</b>
    /// <para>这是这一整套确认机制唯一真正要保证的事,所以单独测一条。</para>
    /// </summary>
    [TestMethod]
    public async Task 否掉确认_一条DDL都不发()
    {
        string file = Path.Combine(Path.GetTempPath(), $"dz-{Guid.NewGuid():N}.db");
        try
        {
            await using SqlSession session = await OpenAsync(file);
            await ExecAsync(session, "create table t(id integer primary key, gone text)");
            SqlStructureTabViewModel tab = await LoadTabAsync(session, "t");

            tab.SelectedColumn = tab.Columns.First(c => c.Name == "gone");
            Task apply = RunAsync(tab.DropColumnCommand);
            await WaitAsync(() => tab.HasConfirmation);

            tab.RejectCommand.Execute(null);
            await apply;

            Assert.AreEqual(2, await CountColumnsAsync(session, "t"), "否掉之后列还必须在。");
            StringAssert.Contains(tab.Status, "取消");
        }
        finally
        {
            TryDelete(file);
        }
    }

    /// <summary>建索引:唯一性要真的落到元数据上,而不是只出现在 DDL 文本里。</summary>
    [TestMethod]
    public async Task 建索引与删索引_唯一性落到元数据上()
    {
        string file = Path.Combine(Path.GetTempPath(), $"dz-{Guid.NewGuid():N}.db");
        try
        {
            await using SqlSession session = await OpenAsync(file);
            await ExecAsync(session, "create table t(id integer primary key, code text)");
            SqlStructureTabViewModel tab = await LoadTabAsync(session, "t");

            tab.NewIndexName = "ux_t_code";
            tab.NewIndexColumns = "code";
            tab.NewIndexUnique = true;
            Task create = RunAsync(tab.CreateIndexCommand);
            await WaitAsync(() => tab.HasConfirmation);
            tab.ConfirmCommand.Execute(null);
            await create;

            SqlStructureRow? created = tab.Indexes.FirstOrDefault(i => i.Name == "ux_t_code");
            Assert.IsNotNull(created, $"索引没建出来。状态:{tab.Status}");
            StringAssert.Contains(created.Extra, "UNIQUE", "唯一性没落到元数据上。");

            tab.SelectedIndex = created;
            Task drop = RunAsync(tab.DropIndexCommand);
            await WaitAsync(() => tab.HasConfirmation);
            tab.ConfirmCommand.Execute(null);
            await drop;

            Assert.IsFalse(tab.Indexes.Any(i => i.Name == "ux_t_code"), "索引没删掉。");
        }
        finally
        {
            TryDelete(file);
        }
    }

    /// <summary>
    /// 只读连接上改结构:**连确认框都不该弹**,在那之前就被拦下。
    /// <para>
    /// 弹了框再拒绝是更差的设计:它让用户以为自己有权改,点下去才发现没有。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 只读连接_改结构在弹确认框之前就被拦下()
    {
        string file = Path.Combine(Path.GetTempPath(), $"dz-{Guid.NewGuid():N}.db");
        try
        {
            await using SqlSession writable = await OpenAsync(file);
            await ExecAsync(writable, "create table t(id integer primary key)");

            await using SqlSession session = await OpenAsync(file, readOnly: true);
            SqlStructureTabViewModel tab = await LoadTabAsync(session, "t");

            Assert.IsFalse(tab.CanDesign, "只读连接不该把改结构那一段摆出来。");

            tab.NewColumnName = "note";
            await RunAsync(tab.AddColumnCommand);

            Assert.IsFalse(tab.HasConfirmation, "只读连接上根本不该弹确认框。");
            StringAssert.Contains(tab.Status, "只读");
            Assert.AreEqual(1, await CountColumnsAsync(writable, "t"));
        }
        finally
        {
            TryDelete(file);
        }
    }

    /// <summary>列名为空时先拦下,不去拼一条名字是空串的 DDL。</summary>
    [TestMethod]
    public async Task 没填列名_不发DDL()
    {
        string file = Path.Combine(Path.GetTempPath(), $"dz-{Guid.NewGuid():N}.db");
        try
        {
            await using SqlSession session = await OpenAsync(file);
            await ExecAsync(session, "create table t(id integer primary key)");
            SqlStructureTabViewModel tab = await LoadTabAsync(session, "t");

            tab.NewColumnName = "   ";
            await RunAsync(tab.AddColumnCommand);

            Assert.IsFalse(tab.HasConfirmation);
            Assert.AreEqual(1, await CountColumnsAsync(session, "t"));
        }
        finally
        {
            TryDelete(file);
        }
    }

    /// <summary>
    /// 类型候选由方言包给。
    /// <para>
    /// 一份写死的通用清单会把 <c>NVARCHAR</c> 摆给 PostgreSQL、把 <c>SERIAL</c> 摆给 SQL Server,
    /// 用户照着选就写出跑不了的 DDL —— 所以这一条断言的是"它确实来自方言包"。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 类型候选来自方言包()
    {
        string file = Path.Combine(Path.GetTempPath(), $"dz-{Guid.NewGuid():N}.db");
        try
        {
            await using SqlSession session = await OpenAsync(file);
            await ExecAsync(session, "create table t(id integer primary key)");
            SqlStructureTabViewModel tab = await LoadTabAsync(session, "t");

            CollectionAssert.AreEqual(
                (System.Collections.ICollection)session.Pack.CommonTypes,
                (System.Collections.ICollection)tab.TypeChoices);
            Assert.IsTrue(tab.TypeChoices.Count > 0, "SQLite 包该给得出类型候选。");
        }
        finally
        {
            TryDelete(file);
        }
    }

    /// <summary>
    /// <b>约束背后的索引:按钮直接不给点,并说清为什么。</b>
    /// <para>
    /// <c>UNIQUE</c> 列会让引擎自己造一个索引(SQLite 叫 <c>sqlite_autoindex_*</c>),
    /// 它在结构页的索引栏里是照实列出来的——但 <c>DROP INDEX</c> 删不掉它:
    /// SQL Server 报 <c>Msg 3723</c>、PG 报 <c>2BP01</c>,都要求改走 <c>DROP CONSTRAINT</c>。
    /// </para>
    /// <para>
    /// 这里断言两件事:① 按钮**在点之前**就不可用(而不是点下去等服务端报错);
    /// ② 状态栏当场给出理由——<b>一个没有理由的灰按钮与"这功能坏了"分不开</b>。
    /// 另外还要确认普通索引不受影响,否则"全都不给删"也能让这条用例通过。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 约束背后的索引_按钮不可用且说明理由()
    {
        string file = Path.Combine(Path.GetTempPath(), $"dz-{Guid.NewGuid():N}.db");
        try
        {
            await using SqlSession session = await OpenAsync(file);
            await ExecAsync(session, "create table t(id integer primary key, code text unique, note text)");
            await ExecAsync(session, "create index ix_t_note on t(note)");
            SqlStructureTabViewModel tab = await LoadTabAsync(session, "t");

            SqlStructureRow auto = tab.Indexes.First(i => i.Name.StartsWith("sqlite_autoindex", StringComparison.Ordinal));
            Assert.IsTrue(tab.IsConstraintIndex(auto.Name), "UNIQUE 列造出来的索引应当被认成约束的实现。");

            tab.SelectedIndex = auto;
            Assert.IsFalse(tab.DropIndexCommand.CanExecute(null), "这个索引删不掉,按钮就不该可点。");
            StringAssert.Contains(tab.Status, auto.Name, "灰按钮必须同时给出理由。");

            // 普通索引不受影响 —— 否则"一律不给删"也能让上面几条通过。
            SqlStructureRow ordinary = tab.Indexes.First(i => i.Name == "ix_t_note");
            Assert.IsFalse(tab.IsConstraintIndex(ordinary.Name));
            tab.SelectedIndex = ordinary;
            Assert.IsTrue(tab.DropIndexCommand.CanExecute(null), "普通索引应当照常可删。");
        }
        finally
        {
            TryDelete(file);
        }
    }

    private static async Task<SqlStructureTabViewModel> LoadTabAsync(SqlSession session, string table)
    {
        var tab = new SqlStructureTabViewModel(session, new(SqlObjectKind.Table, table), Localization);
        await tab.LoadAsync(CancellationToken.None);
        return tab;
    }

    /// <summary>
    /// 把 <see cref="AsyncRelayCommand" /> 的 <c>async void</c> 变回一个可等待的任务。
    /// <para>确认框是"命令跑到一半停下来等人点"的形态,所以必须能在它挂起时观察库的状态。</para>
    /// </summary>
    private static Task RunAsync(AsyncRelayCommand command)
    {
        var done = new TaskCompletionSource();
        void OnChanged(object? _, EventArgs __)
        {
            if (command.CanExecute(null))
            {
                command.CanExecuteChanged -= OnChanged;
                done.TrySetResult();
            }
        }
        command.CanExecuteChanged += OnChanged;
        command.Execute(null);
        return done.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static async Task WaitAsync(Func<bool> until)
    {
        for (int i = 0; i < 300 && !until(); i++)
        {
            await Task.Delay(10);
        }
        Assert.IsTrue(until(), "等的条件一直没成立。");
    }

    private static async Task<int> CountColumnsAsync(SqlSession session, string table)
    {
        await using DbCommand command = session.Metadata.Raw.CreateCommand();
        command.CommandText = $"select count(*) from pragma_table_info('{table}')";
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Task<SqlSession> OpenAsync(string file, bool readOnly = false) =>
        SqlSession.OpenAsync(
            new WorkspaceConnectRequest
            {
                SessionId = "dz",
                Host = file,
                Port = 1,
                Settings = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["readOnly"] = readOnly ? "true" : "false"
                }
            },
            SqlDialect.Sqlite, Localization);

    private static async Task ExecAsync(SqlSession session, string sql)
    {
        await using DbCommand command = session.Metadata.Raw.CreateCommand();
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

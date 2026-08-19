using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using StackExchange.Redis;
using VelaShell.Plugin.Redis.Ui;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Redis.Tests;

/// <summary>
/// 面板的 headless 装载与交互:AXAML 真装载一次(样式选择器、模板、
/// <c>Loc[...]</c> 索引器绑定这些编译期看不出的问题在此暴露),并验证
/// "扫描 → 键树 → 选中 → 详情"这条主链路真的接上了。
/// <para>
/// 需要本机有 <c>127.0.0.1:6379</c>;没有则报 Inconclusive 跳过(与集成测试同一口径)。
/// </para>
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class RedisPanelUiTests
{
    private const string Host = "127.0.0.1";
    private const int Port = 6379;
    private const int Database = 9;

    private static HeadlessUnitTestSession _session = null!;
    private static string _prefix = "";
    private static bool _serverAvailable;

    [ClassInitialize]
    public static async Task InitAsync(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RedisPanelUiTests).Assembly);
        _prefix = $"velashell-ui-{Guid.NewGuid():N}";
        try
        {
            using ConnectionMultiplexer mux = await ConnectionMultiplexer.ConnectAsync(
                new ConfigurationOptions { EndPoints = { { Host, Port } }, AllowAdmin = true, AbortOnConnectFail = true });
            IDatabase db = mux.GetDatabase(Database);
            await db.StringSetAsync($"{_prefix}:user:1:name", "张三");
            await db.HashSetAsync($"{_prefix}:user:1:profile", [new HashEntry("name", "张三")]);
            // 一批"只有末段不同"的键:这正是键列表要折起来的那种噪音(默认阈值 8)。
            for (int i = 0; i < 10; i++)
            {
                await db.StringSetAsync($"{_prefix}:order:2026:{i:0000}", "paid", TimeSpan.FromMinutes(30));
            }
            await mux.CloseAsync();
            _serverAvailable = true;
        }
        catch (Exception)
        {
            _serverAvailable = false;
        }
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (!_serverAvailable)
        {
            return;
        }
        using ConnectionMultiplexer mux = await ConnectionMultiplexer.ConnectAsync(
            new ConfigurationOptions { EndPoints = { { Host, Port } }, AllowAdmin = true, AbortOnConnectFail = true });
        IDatabase db = mux.GetDatabase(Database);
        IServer server = mux.GetServer(Host, Port);
        // 只删自己造的键(连清理脚本也不用 KEYS)。
        await foreach (RedisKey key in server.KeysAsync(Database, $"{_prefix}*", pageSize: 100))
        {
            await db.KeyDeleteAsync(key);
        }
        await mux.CloseAsync();
    }

    private static RedisSettings Settings(string deployment = "standalone") =>
        RedisSettings.From(new WorkspaceConnectRequest
        {
            SessionId = "ui",
            Host = Host,
            Port = Port,
            Settings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["database"] = Database.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["environment"] = "production",
                // 声明的键名是 mode(见 RedisSettings.KeyMode),不是 deployment。
                ["mode"] = deployment
            }
        });

    /// <summary>
    /// 在 headless UI 线程上跑一段**异步**测试体。lambda 必须带返回值 ——
    /// <see cref="HeadlessUnitTestSession" /> 没有 <c>Func&lt;Task&gt;</c> 重载,
    /// 写成无返回值会拿到一个从未被等待的 <c>Task&lt;Task&gt;</c>:测试体跑到第一个 await 就
    /// "通过"了,后面的断言失败全部丢失(仓库 README 与 AI 插件测试都记过这一条)。
    /// </summary>
    private static void OnUi(Func<Task> body) =>
        _session.Dispatch(async () =>
        {
            await body();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();

    private static async Task PumpAsync(int rounds = 60)
    {
        for (int i = 0; i < rounds; i++)
        {
            await Task.Delay(5);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task<(Window Window, RedisWorkspaceView View, RedisWorkspaceViewModel ViewModel, RedisConnection Connection)>
        ShowAsync(string filter = "", string deployment = "standalone")
    {
        RedisConnection connection = await RedisConnection.ConnectAsync(Host, Port, "", "", Settings(deployment));
        using var context = new TestPluginContext();
        var viewModel = new RedisWorkspaceViewModel(
            connection, "prod-cache", $"{Host}:{Port}", new Loc("zh-Hans"), new PluginLoggerFacade(context.Log))
        {
            Filter = filter
        };
        var view = new RedisWorkspaceView(viewModel);
        var window = new Window { Width = 1200, Height = 700, Content = view };
        window.Show();
        await PumpAsync(10);
        await viewModel.InitializeAsync();
        await PumpAsync();
        return (window, view, viewModel, connection);
    }

    /// <summary>
    /// 键列表在真机上的样子:噪音折起来、其余平铺、每行带齐类型/TTL/规模,点一下就地展开。
    /// <para>
    /// 这是左栏从树改成列表的验收点。原先这一屏是一棵树:看一个键要点三层,
    /// 行上只有本层片段,TTL 与规模压根没地方放。
    /// </para>
    /// </summary>
    [TestMethod]
    public void KeyList_FoldsTheNoisyPrefix_ShowsMetadata_AndExpandsInPlace()
    {
        RequireServer();
        OnUi(async () =>
        {
            (Window window, RedisWorkspaceView view, RedisWorkspaceViewModel viewModel, RedisConnection connection) =
                await ShowAsync(_prefix);
            try
            {
                Assert.IsTrue(viewModel.IsScanComplete);
                Assert.AreEqual(12, viewModel.MatchedCount);

                // 10 个订单折成一行;2 个 user 键低于阈值,照旧平铺。
                CollectionAssert.AreEqual(
                    new[] { $"{_prefix}:order:2026:*", $"{_prefix}:user:1:name", $"{_prefix}:user:1:profile" },
                    viewModel.Rows.Select(row => row.Display).ToArray());

                RedisKeyRow group = viewModel.Rows.Single(row => row.IsGroup);
                Assert.AreEqual(10, group.Count);
                Assert.IsTrue(group.IsCollapsed, "折叠态应显示向右的箭头。");
                Assert.IsFalse(group.IsExpandedGroup);

                // 每行带齐元数据 —— 缩进省下来的宽度就是给这两列的。
                RedisKeyRow hash = Row(viewModel, $"{_prefix}:user:1:profile");
                Assert.AreEqual("hash", hash.TypeName);
                Assert.AreEqual("—", hash.TtlText, "没有过期时间的键给一个破折号,不是空白。");
                Assert.AreEqual("1 项", hash.SizeText);
                Assert.AreEqual("6 字节", Row(viewModel, $"{_prefix}:user:1:name").SizeText);

                // 点一下就地展开:成员缩进一级铺在原位,不是跳进另一层视图。
                viewModel.ToggleGroup(group);
                await PumpAsync(10);

                Assert.IsTrue(group.IsExpandedGroup, "展开态应显示向下的箭头。");
                Assert.IsFalse(group.IsCollapsed);
                Assert.HasCount(13, viewModel.Rows, "10 个成员就地铺开。");
                RedisKeyRow member = Row(viewModel, $"{_prefix}:order:2026:0000");
                Assert.AreEqual(1, member.Depth);
                Assert.AreEqual("string", member.TypeName);
                // 种下去是 30 分钟,读到的是 29:5x —— 断言前缀,别跟秒数较劲。
                Assert.StartsWith("29:", member.TtlText, "带 TTL 的键要显示倒计时。");
                Assert.IsFalse(member.IsExpiringSoon, "还有半小时,不该标成快过期。");

                // 再点一下收起来 —— 走 Tapped 而不是 SelectionChanged 才有的第二下。
                viewModel.ToggleGroup(group);
                await PumpAsync(10);
                Assert.HasCount(3, viewModel.Rows);

                Assert.IsNotNull(view.GetControl<ListBox>("KeyList"));
            }
            finally
            {
                window.Close();
                await connection.DisposeAsync();
            }
        });
    }

    /// <summary>按**完整键名**取列表行 —— 列表世界里"找一个键"就这么直接。</summary>
    private static RedisKeyRow Row(RedisWorkspaceViewModel viewModel, string display) =>
        viewModel.Rows.FirstOrDefault(row => row.Key?.Display == display)
        ?? throw new AssertFailedException(
            $"列表里没有 {display};当前行:{string.Join(" | ", viewModel.Rows.Select(row => row.Display))}");

    private static void RequireServer()
    {
        if (!_serverAvailable)
        {
            Assert.Inconclusive($"没有可用的 Redis({Host}:{Port}),跳过面板 UI 测试。");
        }
    }

    [TestMethod]
    public void Panel_Loads_WithoutHostThemeTokens()
    {
        RequireServer();
        OnUi(async () =>
        {
            (Window window, RedisWorkspaceView view, _, RedisConnection connection) = await ShowAsync();
            try
            {
                // AXAML 真装载了:样式、模板、Loc[...] 索引器绑定全部就位。
                Assert.IsNotNull(view.GetControl<ListBox>("KeyList"));
                Assert.IsNotNull(view.GetControl<TextBox>("FilterBox"));
                Assert.IsNotNull(view.GetControl<TextBlock>("MatchEcho"));
                Assert.IsNotNull(view.GetControl<TextBlock>("ScanStatus"));
            }
            finally
            {
                window.Close();
                await connection.DisposeAsync();
            }
        });
    }

    [TestMethod]
    public void MatchEcho_ShowsTheCommandThatWillActuallyBeSent()
    {
        // 这一行小字是"过滤条语义看得见"那条设计决定的落地处,值得钉住。
        RequireServer();
        OnUi(async () =>
        {
            (Window window, RedisWorkspaceView view, _, RedisConnection connection) = await ShowAsync($"{_prefix}:user");
            try
            {
                string echo = view.GetControl<TextBlock>("MatchEcho").Text ?? "";
                Assert.Contains("SCAN 0 MATCH", echo);
                Assert.Contains($"{_prefix}:user*", echo);
                Assert.Contains("COUNT 500", echo);
            }
            finally
            {
                window.Close();
                await connection.DisposeAsync();
            }
        });
    }

    [TestMethod]
    public void Scan_BuildsTheKeyTreeAndReportsAnHonestStatus()
    {
        RequireServer();
        OnUi(async () =>
        {
            (Window window, RedisWorkspaceView view, RedisWorkspaceViewModel viewModel, RedisConnection connection) =
                await ShowAsync($"{_prefix}:user");
            try
            {
                Assert.IsTrue(viewModel.IsScanComplete, "游标应已归零。");
                Assert.AreEqual(2, viewModel.MatchedCount);
                // 扁平列表:一行一个**完整键名**,两个键都在眼前,不必逐层点开。
                CollectionAssert.AreEquivalent(
                    new[] { $"{_prefix}:user:1:name", $"{_prefix}:user:1:profile" },
                    viewModel.Rows.Select(row => row.Display).ToArray());
                Assert.DoesNotContain(row => row.IsGroup, viewModel.Rows, "两个键远低于折叠阈值。");
                Assert.AreEqual("string", viewModel.Rows.Single(row => row.Display.EndsWith(":name", StringComparison.Ordinal)).TypeName);
                Assert.AreEqual("hash", viewModel.Rows.Single(row => row.Display.EndsWith(":profile", StringComparison.Ordinal)).TypeName);

                // 面包屑 = 这批键的公共前缀,用户据此知道自己在哪一层。
                CollectionAssert.AreEqual(
                    new[] { _prefix, "user", "1" },
                    viewModel.Breadcrumb.Select(segment => segment.Label).ToArray());

                // **只有游标归零才敢说"全部"** —— 状态条的措辞是这条纪律的出口。
                string status = view.GetControl<TextBlock>("ScanStatus").Text ?? "";
                Assert.Contains("游标已归零", status);
            }
            finally
            {
                window.Close();
                await connection.DisposeAsync();
            }
        });
    }

    /// <summary>
    /// 选了「集群」但连的是单机:扫描仍必须真的扫得动,并且要**明说**形态不符。
    /// <para>
    /// 这个测试来自真机上的一次翻车。集群路径原先走 <c>IServer.ExecuteAsync("SCAN", …)</c>,
    /// 那条路不带库号,服务器直接回 <c>A target database is required for SCAN</c> ——
    /// 用户看到的是一棵空键树加一句红字。改用 <c>IServer.KeysAsync(database, …)</c> 后
    /// 这条路显式携带库号,单机上也走得通,所以本机没有集群也能把这个回归钉住。
    /// </para>
    /// <para>
    /// 顺带验证形态不符的提示:配错形态最难受的表现正是"什么都没有,也没人告诉你为什么"。
    /// </para>
    /// </summary>
    [TestMethod]
    public void ClusterDeploymentAgainstStandalone_StillScans_AndSaysTheModeDoesNotMatch()
    {
        RequireServer();
        OnUi(async () =>
        {
            (Window window, RedisWorkspaceView view, RedisWorkspaceViewModel viewModel, RedisConnection connection) =
                await ShowAsync($"{_prefix}:user", deployment: "cluster");
            try
            {
                // 1)扫描没有炸在"没有目标库"上 —— 状态条里不该出现那句服务器错误。
                Assert.IsFalse(
                    (viewModel.StatusMessage ?? "").Contains("target database", StringComparison.OrdinalIgnoreCase),
                    $"集群路径又丢了库号:{viewModel.StatusMessage}");
                Assert.IsTrue(viewModel.IsScanComplete, "逐节点扫描应已走到最后一个节点的游标归零。");

                // 2)形态不符要说出来(服务器自报 standalone,配置里选的是集群),而且这句话
                //    必须**活过一次扫描** —— 它写在常驻的提示条上,不是被扫描清空的状态行。
                Assert.AreEqual("standalone", connection.Info.Mode);
                Assert.AreEqual(
                    new Loc("zh-Hans")["Redis_ModeMismatchStandalone"],
                    viewModel.DeploymentWarning,
                    "形态不符时提示条要给出那句话。");
                Assert.IsTrue(viewModel.HasDeploymentWarning);
                Assert.IsTrue(view.GetControl<Border>("DeploymentWarningBar").IsVisible,
                    "提示条应当在界面上真的可见。");
            }
            finally
            {
                window.Close();
                await connection.DisposeAsync();
            }
        });
    }

    [TestMethod]
    public void SelectingAHashKey_LoadsItsFieldsIntoTheDetailPane()
    {
        RequireServer();
        OnUi(async () =>
        {
            (Window window, RedisWorkspaceView view, RedisWorkspaceViewModel viewModel, RedisConnection connection) =
                await ShowAsync($"{_prefix}:user");
            try
            {
                viewModel.SelectedRow = Row(viewModel, $"{_prefix}:user:1:profile");
                await PumpAsync();

                Assert.IsTrue(viewModel.HasSelection);
                Assert.AreEqual("hash", viewModel.Selected!.Type);
                Assert.IsTrue(viewModel.IsCollectionSelected);
                Assert.IsFalse(viewModel.IsStringSelected);
                Assert.AreEqual("字段", viewModel.LabelColumnHeader);
                Assert.AreEqual("张三", viewModel.Elements.Single(e => e.Label == "name").Value);
                Assert.AreEqual("永不过期", viewModel.SelectedTtlText);
                Assert.IsTrue(view.GetControl<ListBox>("ElementList").IsVisible);
            }
            finally
            {
                window.Close();
                await connection.DisposeAsync();
            }
        });
    }

    [TestMethod]
    public void SelectingAStringKey_ShowsTheValueEditor()
    {
        RequireServer();
        OnUi(async () =>
        {
            (Window window, RedisWorkspaceView view, RedisWorkspaceViewModel viewModel, RedisConnection connection) =
                await ShowAsync($"{_prefix}:user");
            try
            {
                viewModel.SelectedRow = Row(viewModel, $"{_prefix}:user:1:name");
                await PumpAsync();

                Assert.IsTrue(viewModel.IsStringSelected);
                Assert.AreEqual("张三", viewModel.StringValue);
                Assert.AreEqual(string.Empty, viewModel.TruncationNotice, "没超上限就不该出现截断提示。");
                TextBox box = view.GetControl<TextBox>("StringValueBox");
                Assert.IsTrue(box.IsVisible);
                Assert.IsTrue(box.IsReadOnly, "M1 的值编辑器是只读的:写入随类型编辑器一起做。");
            }
            finally
            {
                window.Close();
                await connection.DisposeAsync();
            }
        });
    }

    [TestMethod]
    public void ProductionProfile_TurnsOnReadOnlyAndShowsTheBadges()
    {
        // 护栏的第一档在界面上必须看得见:生产标记 + 只读徽章。
        RequireServer();
        OnUi(async () =>
        {
            (Window window, _, RedisWorkspaceViewModel viewModel, RedisConnection connection) = await ShowAsync();
            try
            {
                Assert.IsTrue(viewModel.IsProduction);
                Assert.IsTrue(viewModel.IsReadOnly);
                Assert.AreEqual("生产", viewModel.EnvironmentLabel);
                Assert.AreEqual("只读", viewModel.ReadOnlyLabel);
            }
            finally
            {
                window.Close();
                await connection.DisposeAsync();
            }
        });
    }

    [TestMethod]
    public void DatabaseDropdown_ListsEveryDatabaseWithItsKeyCount()
    {
        RequireServer();
        OnUi(async () =>
        {
            (Window window, _, RedisWorkspaceViewModel viewModel, RedisConnection connection) = await ShowAsync();
            try
            {
                Assert.IsTrue(viewModel.SupportsDatabases);
                Assert.IsGreaterThanOrEqualTo(10, viewModel.Databases.Count);
                Assert.AreEqual(Database, viewModel.SelectedDatabase!.Index);
                // 键数直接进下拉文本,省掉"逐个库点进去看有没有东西"的盲测。
                Assert.Contains("db9", viewModel.Databases[Database].Display);
            }
            finally
            {
                window.Close();
                await connection.DisposeAsync();
            }
        });
    }
}

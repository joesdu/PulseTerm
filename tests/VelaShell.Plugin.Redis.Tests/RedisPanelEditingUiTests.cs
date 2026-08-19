using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using StackExchange.Redis;
using VelaShell.Plugin.Redis.Ui;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Redis.Tests;

/// <summary>
/// 写入路径与底部抽屉的 headless 交互测试:确认闸门、只读拦截、值编辑、成员增删、
/// 控制台执行、页签切换。
/// <para>
/// 这些是 M2/M3 的**行为**,不是渲染 —— 但它们只有把面板真的挂进窗口、让命令真的跑一遍
/// 才验得出来(确认框是个异步闸门,靠单测桩是绕过它的)。需要本机 Redis;没有则跳过。
/// </para>
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class RedisPanelEditingUiTests
{
    private const string Host = "127.0.0.1";
    private const int Port = 6379;
    private const int Database = 9;

    private static HeadlessUnitTestSession _session = null!;
    private static string _prefix = "";
    private static bool _available;

    [ClassInitialize]
    public static async Task InitAsync(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RedisPanelEditingUiTests).Assembly);
        _prefix = $"velashell-eui-{Guid.NewGuid():N}";
        try
        {
            using ConnectionMultiplexer mux = await ConnectionMultiplexer.ConnectAsync(
                new ConfigurationOptions { EndPoints = { { Host, Port } }, AllowAdmin = true, AbortOnConnectFail = true });
            await mux.CloseAsync();
            _available = true;
        }
        catch (Exception)
        {
            _available = false;
        }
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (!_available)
        {
            return;
        }
        using ConnectionMultiplexer mux = await ConnectionMultiplexer.ConnectAsync(
            new ConfigurationOptions { EndPoints = { { Host, Port } }, AllowAdmin = true, AbortOnConnectFail = true });
        IDatabase db = mux.GetDatabase(Database);
        IServer server = mux.GetServer(Host, Port);
        await foreach (RedisKey key in server.KeysAsync(Database, $"{_prefix}*", pageSize: 100))
        {
            await db.KeyDeleteAsync(key);
        }
        await mux.CloseAsync();
    }

    private static RedisSettings Settings(string environment) =>
        RedisSettings.From(new WorkspaceConnectRequest
        {
            SessionId = "eui",
            Host = Host,
            Port = Port,
            Settings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["database"] = Database.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["environment"] = environment,
                ["clientName"] = "velashell-eui"
            }
        });

    private static void OnUi(Func<Task> body) =>
        _session.Dispatch(async () =>
        {
            await body();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();

    private static async Task PumpAsync(int rounds = 40)
    {
        for (int i = 0; i < rounds; i++)
        {
            await Task.Delay(5);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private sealed record Harness(
        Window Window,
        RedisWorkspaceView View,
        RedisWorkspaceViewModel ViewModel,
        RedisConnection Connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Window.Close();
            ViewModel.Dispose();
            await Connection.DisposeAsync();
        }
    }

    private static async Task<Harness> ShowAsync(string filter, string environment = "development")
    {
        RedisConnection connection = await RedisConnection.ConnectAsync(Host, Port, "", "", Settings(environment));
        using var context = new TestPluginContext();
        var viewModel = new RedisWorkspaceViewModel(
            connection, "dev-cache", $"{Host}:{Port}", new Loc("zh-Hans"), new PluginLoggerFacade(context.Log))
        {
            Filter = filter
        };
        var view = new RedisWorkspaceView(viewModel);
        var window = new Window { Width = 1280, Height = 800, Content = view };
        window.Show();
        await PumpAsync(10);
        await viewModel.InitializeAsync();
        await PumpAsync();
        return new(window, view, viewModel, connection);
    }

    private static void Require()
    {
        if (!_available)
        {
            Assert.Inconclusive($"没有可用的 Redis({Host}:{Port}),跳过编辑 UI 测试。");
        }
    }

    private static async Task SeedAsync(string suffix, Action<IDatabase, RedisKey> write)
    {
        using ConnectionMultiplexer mux = await ConnectionMultiplexer.ConnectAsync(
            new ConfigurationOptions { EndPoints = { { Host, Port } }, AllowAdmin = true, AbortOnConnectFail = true });
        write(mux.GetDatabase(Database), $"{_prefix}:{suffix}");
        await mux.CloseAsync();
    }

    /// <summary>
    /// 按路径段取列表行。列表世界里"找一个键"就是拿**完整键名**去找一行 ——
    /// 不再逐层下钻(那正是树逼着用户做、也逼着测试做的事)。
    /// </summary>
    private static RedisKeyRow Leaf(RedisWorkspaceViewModel viewModel, params string[] path)
    {
        string display = string.Join(':', path);
        return viewModel.Rows.FirstOrDefault(row => row.Key?.Display == display)
               ?? throw new AssertFailedException(
                   $"列表里没有 {display};当前行:{string.Join(" | ", viewModel.Rows.Select(row => row.Display))}");
    }

    [TestMethod]
    public void StringValue_CanBeEditedAndSaved()
    {
        Require();
        OnUi(async () =>
        {
            await SeedAsync("edit:text", (db, key) => db.StringSet(key, "before"));
            await using Harness harness = await ShowAsync($"{_prefix}:edit");
            RedisWorkspaceViewModel vm = harness.ViewModel;

            vm.SelectedRow = Leaf(vm, _prefix, "edit", "text");
            await PumpAsync();

            Assert.IsTrue(vm.CanEditString, "开发环境默认可写。");
            Assert.AreEqual("before", vm.StringDraft);
            Assert.IsFalse(vm.IsStringDirty);

            vm.StringDraft = "after";
            Assert.IsTrue(vm.IsStringDirty, "草稿与现值不同才显示保存按钮。");
            vm.SaveStringCommand.Execute(null);
            await PumpAsync();

            Assert.AreEqual("after", vm.StringValue);
            Assert.IsFalse(vm.IsStringDirty);
        });
    }

    [TestMethod]
    public void ReadOnlyMode_BlocksWritesWithAnExplanation()
    {
        // 拦住时给"为什么 + 怎么解除",而不是灰一个按钮让用户猜。
        Require();
        OnUi(async () =>
        {
            await SeedAsync("ro:text", (db, key) => db.StringSet(key, "keep"));
            await using Harness harness = await ShowAsync($"{_prefix}:ro", environment: "production");
            RedisWorkspaceViewModel vm = harness.ViewModel;

            Assert.IsTrue(vm.IsReadOnlyNow, "生产环境默认只读。");
            Assert.IsFalse(vm.CanWrite);

            vm.SelectedRow = Leaf(vm, _prefix, "ro", "text");
            await PumpAsync();

            Assert.IsFalse(vm.CanEditString, "只读模式下值编辑器不可编辑。");
            vm.StringDraft = "changed";
            vm.SaveStringCommand.Execute(null);
            await PumpAsync();

            Assert.AreEqual("keep", vm.StringValue, "只读模式必须真的没写进去。");
        });
    }

    [TestMethod]
    public void TurningOffReadOnlyInProduction_RequiresTypedConfirmation()
    {
        Require();
        OnUi(async () =>
        {
            await using Harness harness = await ShowAsync($"{_prefix}:none", environment: "production");
            RedisWorkspaceViewModel vm = harness.ViewModel;

            vm.ToggleReadOnlyCommand.Execute(null);
            await PumpAsync();

            Assert.IsTrue(vm.Confirmation.IsOpen);
            Assert.IsTrue(vm.Confirmation.RequiresTyping, "生产环境要手打确认串。");
            Assert.IsFalse(vm.Confirmation.CanConfirm);

            // 打错不放行。
            vm.Confirmation.TypedText = "yes";
            Assert.IsFalse(vm.Confirmation.CanConfirm);

            vm.Confirmation.TypedText = vm.Confirmation.ExpectedText;
            Assert.IsTrue(vm.Confirmation.CanConfirm);
            vm.Confirmation.ConfirmCommand.Execute(null);
            await PumpAsync();

            Assert.IsFalse(vm.Confirmation.IsOpen);
            Assert.IsFalse(vm.IsReadOnlyNow);
            Assert.IsTrue(vm.CanWrite);
        });
    }

    [TestMethod]
    public void DeletingAKey_GoesThroughTypedConfirmationAndCanBeCancelled()
    {
        Require();
        OnUi(async () =>
        {
            await SeedAsync("del:text", (db, key) => db.StringSet(key, "v"));
            await using Harness harness = await ShowAsync($"{_prefix}:del");
            RedisWorkspaceViewModel vm = harness.ViewModel;
            vm.SelectedRow = Leaf(vm, _prefix, "del", "text");
            await PumpAsync();

            vm.DeleteKeyCommand.Execute(null);
            await PumpAsync();

            Assert.IsTrue(vm.Confirmation.IsOpen);
            Assert.IsTrue(vm.Confirmation.IsDestructive);
            Assert.Contains("UNLINK", vm.Confirmation.Detail, "确认框要给出确切要跑的命令。");

            // 取消 → 键还在。
            vm.Confirmation.CancelCommand.Execute(null);
            await PumpAsync();
            Assert.IsFalse(vm.Confirmation.IsOpen);
            Assert.IsFalse((await harness.Connection.DescribeAsync(new($"{_prefix}:del:text"))).IsGone);
        });
    }

    [TestMethod]
    public void HashFields_CanBeAddedAndRemovedThroughTheEditStrip()
    {
        Require();
        OnUi(async () =>
        {
            await SeedAsync("h:profile", (db, key) => db.HashSet(key, [new HashEntry("name", "张三")]));
            await using Harness harness = await ShowAsync($"{_prefix}:h");
            RedisWorkspaceViewModel vm = harness.ViewModel;
            vm.SelectedRow = Leaf(vm, _prefix, "h", "profile");
            await PumpAsync();

            Assert.IsTrue(vm.IsCollectionSelected);
            Assert.IsTrue(vm.NewLabelApplies, "哈希的新增行需要字段名。");
            Assert.IsFalse(vm.ScoreApplies);

            vm.NewLabel = "age";
            vm.NewValue = "32";
            vm.AddElementCommand.Execute(null);
            await PumpAsync();

            Assert.AreEqual("32", vm.Elements.Single(row => row.Label == "age").Value);

            vm.SelectedElement = vm.Elements.Single(row => row.Label == "age");
            vm.RemoveElementCommand.Execute(null);
            await PumpAsync();

            Assert.DoesNotContain(row => row.Label == "age", vm.Elements);
        });
    }

    [TestMethod]
    public void ListSelection_ShowsTheNoDeleteByIndexNote()
    {
        // 列表没有按索引删除的原语 —— 这句话必须出现在界面上,不能只在代码注释里。
        Require();
        OnUi(async () =>
        {
            await SeedAsync("l:queue", (db, key) => db.ListRightPush(key, ["a", "b"]));
            await using Harness harness = await ShowAsync($"{_prefix}:l");
            RedisWorkspaceViewModel vm = harness.ViewModel;
            vm.SelectedRow = Leaf(vm, _prefix, "l", "queue");
            await PumpAsync();

            Assert.IsTrue(vm.HasElementRemoveNote);
            Assert.Contains("LREM", vm.ElementRemoveNote);
            Assert.IsFalse(vm.NewLabelApplies, "列表按索引追加,不需要标签。");
        });
    }

    [TestMethod]
    public void SortedSetSelection_ExposesTheScoreColumn()
    {
        Require();
        OnUi(async () =>
        {
            await SeedAsync("z:board", (db, key) => db.SortedSetAdd(key, "alice", 10));
            await using Harness harness = await ShowAsync($"{_prefix}:z");
            RedisWorkspaceViewModel vm = harness.ViewModel;
            vm.SelectedRow = Leaf(vm, _prefix, "z", "board");
            await PumpAsync();

            Assert.IsTrue(vm.ShowsScore);
            Assert.AreEqual("10", vm.Elements.Single().ScoreText);

            vm.SelectedElement = vm.Elements.Single();
            vm.EditScore = "42";
            vm.SaveElementCommand.Execute(null);
            await PumpAsync();

            Assert.AreEqual("42", vm.Elements.Single().ScoreText);
        });
    }

    [TestMethod]
    public void BadScore_IsReportedInsteadOfWritingGarbage()
    {
        Require();
        OnUi(async () =>
        {
            await SeedAsync("zbad:board", (db, key) => db.SortedSetAdd(key, "alice", 10));
            await using Harness harness = await ShowAsync($"{_prefix}:zbad");
            RedisWorkspaceViewModel vm = harness.ViewModel;
            vm.SelectedRow = Leaf(vm, _prefix, "zbad", "board");
            await PumpAsync();
            vm.SelectedElement = vm.Elements.Single();

            vm.EditScore = "not-a-number";
            vm.SaveElementCommand.Execute(null);
            await PumpAsync();

            Assert.Contains("数字", vm.StatusMessage);
            Assert.AreEqual("10", vm.Elements.Single().ScoreText, "分值没被改坏。");
        });
    }

    [TestMethod]
    public void Ttl_IsPreviewedBeforeItIsApplied()
    {
        // 换算给用户看,而不是等他按下去才发现填的是分钟还是秒。
        Require();
        OnUi(async () =>
        {
            await SeedAsync("ttl:text", (db, key) => db.StringSet(key, "v"));
            await using Harness harness = await ShowAsync($"{_prefix}:ttl");
            RedisWorkspaceViewModel vm = harness.ViewModel;
            vm.SelectedRow = Leaf(vm, _prefix, "ttl", "text");
            await PumpAsync();

            Assert.AreEqual("永不过期", vm.SelectedTtlText);

            vm.TtlDraft = "abc";
            Assert.Contains("读不出", vm.TtlPreview);

            vm.TtlDraft = "15m";
            Assert.Contains("还剩", vm.TtlPreview);

            vm.ApplyTtlCommand.Execute(null);
            await PumpAsync();

            Assert.AreNotEqual("永不过期", vm.SelectedTtlText);

            vm.PersistCommand.Execute(null);
            await PumpAsync();
            Assert.AreEqual("永不过期", vm.SelectedTtlText);
        });
    }

    [TestMethod]
    public void Rename_ToAnExistingKey_AsksBeforeOverwriting()
    {
        Require();
        OnUi(async () =>
        {
            await SeedAsync("rn:a", (db, key) => db.StringSet(key, "from"));
            await SeedAsync("rn:b", (db, key) => db.StringSet(key, "to"));
            await using Harness harness = await ShowAsync($"{_prefix}:rn");
            RedisWorkspaceViewModel vm = harness.ViewModel;
            vm.SelectedRow = Leaf(vm, _prefix, "rn", "a");
            await PumpAsync();

            vm.RenameDraft = $"{_prefix}:rn:b";
            vm.RenameCommand.Execute(null);
            await PumpAsync();

            Assert.IsTrue(vm.Confirmation.IsOpen, "RENAME 会静默覆盖,所以必须先问。");
            Assert.IsTrue(vm.Confirmation.IsDestructive);
            vm.Confirmation.CancelCommand.Execute(null);
            await PumpAsync();

            // 取消后两个键都还在原处。
            Assert.IsFalse((await harness.Connection.DescribeAsync(new($"{_prefix}:rn:a"))).IsGone);
        });
    }

    // ── 控制台与抽屉 ──────────────────────────────────────────────

    [TestMethod]
    public void Console_RunsACommandAndRendersTheReply()
    {
        Require();
        OnUi(async () =>
        {
            await using Harness harness = await ShowAsync($"{_prefix}:none");
            RedisWorkspaceViewModel vm = harness.ViewModel;
            vm.IsDrawerOpen = true;
            await PumpAsync();

            vm.Console.Input = "PING";
            vm.Console.RunCommand.Execute(null);
            await PumpAsync();

            Assert.Contains(line => line.Text == "PONG", vm.Console.Lines);
            Assert.AreEqual(string.Empty, vm.Console.Input, "跑完就清空输入。");
            Assert.Contains(line => line.IsCommand, vm.Console.Lines, "敲过的那一行要留在输出里。");
        });
    }

    [TestMethod]
    public void Console_History_IsWalkableWithArrows()
    {
        Require();
        OnUi(async () =>
        {
            await using Harness harness = await ShowAsync($"{_prefix}:none");
            RedisConsoleViewModel console = harness.ViewModel.Console;

            console.Input = "PING";
            console.RunCommand.Execute(null);
            await PumpAsync();
            console.Input = "ECHO hi";
            console.RunCommand.Execute(null);
            await PumpAsync();

            console.HistoryBack();
            Assert.AreEqual("ECHO hi", console.Input);
            console.HistoryBack();
            Assert.AreEqual("PING", console.Input);
            console.HistoryForward();
            Assert.AreEqual("ECHO hi", console.Input);
            console.HistoryForward();
            Assert.AreEqual(string.Empty, console.Input, "走到末尾即清空,与 shell 一致。");
        });
    }

    [TestMethod]
    public void Console_Prompt_ShowsTheDatabaseAndReadOnlyState()
    {
        Require();
        OnUi(async () =>
        {
            await using Harness harness = await ShowAsync($"{_prefix}:none", environment: "production");
            RedisWorkspaceViewModel vm = harness.ViewModel;

            Assert.Contains($"db{Database}", vm.Console.Prompt);
            Assert.Contains(":ro]", vm.Console.Prompt, "只读状态要在提示符上看得见。");
        });
    }

    [TestMethod]
    public void Console_BlockedCommand_ExplainsWhyInsteadOfRunning()
    {
        Require();
        OnUi(async () =>
        {
            await using Harness harness = await ShowAsync($"{_prefix}:none", environment: "production");
            RedisConsoleViewModel console = harness.ViewModel.Console;

            console.Input = "SET a 1";
            // 敲下回车**之前**就把结论给出来。
            Assert.Contains("只读", console.InputHint);

            console.RunCommand.Execute(null);
            await PumpAsync();

            Assert.Contains(line => line.IsNote && line.Text.Contains("只读", StringComparison.Ordinal), console.Lines);
        });
    }

    [TestMethod]
    public void Console_UnsupportedCommand_IsFlaggedBeforeItIsTyped()
    {
        Require();
        OnUi(async () =>
        {
            await using Harness harness = await ShowAsync($"{_prefix}:none");
            RedisConsoleViewModel console = harness.ViewModel.Console;

            console.Input = "MONITOR";

            Assert.Contains("跑不了", console.InputHint);
        });
    }

    [TestMethod]
    public void SendToConsole_PrefillsTheReadCommandForTheSelectedKey()
    {
        // 把"点点点"与"敲命令"缝在一起:省掉最烦的那一步 —— 手抄键名。
        Require();
        OnUi(async () =>
        {
            await SeedAsync("send:profile", (db, key) => db.HashSet(key, [new HashEntry("f", "v")]));
            await using Harness harness = await ShowAsync($"{_prefix}:send");
            RedisWorkspaceViewModel vm = harness.ViewModel;
            vm.SelectedRow = Leaf(vm, _prefix, "send", "profile");
            await PumpAsync();

            vm.SendToConsoleCommand.Execute(null);
            await PumpAsync();

            Assert.IsTrue(vm.IsDrawerOpen);
            Assert.IsTrue(vm.IsConsoleTab);
            Assert.Contains("HGETALL", vm.Console.Input);
            Assert.Contains($"{_prefix}:send:profile", vm.Console.Input);
        });
    }

    [TestMethod]
    public void Drawer_OverviewTab_LoadsServerMetrics()
    {
        Require();
        OnUi(async () =>
        {
            await using Harness harness = await ShowAsync($"{_prefix}:none");
            RedisWorkspaceViewModel vm = harness.ViewModel;

            vm.ShowOverviewCommand.Execute(null);
            await PumpAsync();

            Assert.IsTrue(vm.IsOverviewTab);
            Assert.IsTrue(vm.IsDrawerOpen);
            Assert.IsFalse(vm.HasOverviewNotice, $"概览应能读到:{vm.OverviewNotice}");
            Assert.Contains(group => group.Title == "server", vm.Overview);
        });
    }

    [TestMethod]
    public void Drawer_ClientsTab_MarksOurOwnConnection()
    {
        Require();
        OnUi(async () =>
        {
            await using Harness harness = await ShowAsync($"{_prefix}:none");
            RedisWorkspaceViewModel vm = harness.ViewModel;

            vm.ShowClientsCommand.Execute(null);
            await PumpAsync();

            Assert.IsFalse(vm.HasClientsNotice, $"客户端列表应能读到:{vm.ClientsNotice}");
            Assert.Contains(client => client.IsSelf, vm.Clients);
            // 自己的连接禁止断开。
            vm.SelectedClient = vm.Clients.First(client => client.IsSelf);
            Assert.IsFalse(vm.KillClientCommand.CanExecute(null));
        });
    }

    [TestMethod]
    public void Drawer_ToggleLabel_FollowsItsState()
    {
        Require();
        OnUi(async () =>
        {
            await using Harness harness = await ShowAsync($"{_prefix}:none");
            RedisWorkspaceViewModel vm = harness.ViewModel;

            Assert.AreEqual("展开", vm.DrawerToggleLabel);
            vm.ToggleDrawerCommand.Execute(null);
            await PumpAsync();
            Assert.AreEqual("收起", vm.DrawerToggleLabel);
        });
    }

    [TestMethod]
    public void ConfirmOverlay_IsInTheVisualTreeAndFollowsState()
    {
        Require();
        OnUi(async () =>
        {
            await SeedAsync("ov:text", (db, key) => db.StringSet(key, "v"));
            await using Harness harness = await ShowAsync($"{_prefix}:ov");
            RedisWorkspaceViewModel vm = harness.ViewModel;
            Border overlay = harness.View.GetControl<Border>("ConfirmOverlay");

            Assert.IsFalse(overlay.IsVisible);

            vm.SelectedRow = Leaf(vm, _prefix, "ov", "text");
            await PumpAsync();
            vm.DeleteKeyCommand.Execute(null);
            await PumpAsync();

            Assert.IsTrue(overlay.IsVisible);
            Assert.IsTrue(harness.View.GetControl<TextBox>("ConfirmTypedBox").IsVisible);
            vm.Confirmation.CancelCommand.Execute(null);
            await PumpAsync();
            Assert.IsFalse(overlay.IsVisible);
        });
    }
    /// <summary>
    /// 二进制值的完整一圈:打开 → 自动落到「转义」 → 改一处 → 保存 → 服务端字节逐字节对得上。
    /// <para>
    /// 这是"值编辑区遇到二进制怎么办"的验收点。修复前这条路会毁数据:界面显示转义形式,
    /// 保存时却按 UTF-8 编码那串文本写回去 —— 十个字节的 gzip 头变成四十个字节的 ASCII,
    /// 而界面全程没有任何异常。
    /// </para>
    /// </summary>
    [TestMethod]
    public void BinaryValue_OpensEscaped_AndSavesByteExact()
    {
        Require();
        OnUi(async () =>
        {
            byte[] gzip = [0x1F, 0x8B, 0x08, 0x00, 0xC3, 0x28, 0x00, 0x03];
            await SeedAsync("bin:blob", (db, key) => db.StringSet(key, gzip));
            await using Harness harness = await ShowAsync($"{_prefix}:bin");
            RedisWorkspaceViewModel vm = harness.ViewModel;

            vm.SelectedRow = Leaf(vm, _prefix, "bin", "blob");
            await PumpAsync();

            // 1) 不是合法 UTF-8 → 自动落到转义形态,「文本」不可用。
            Assert.IsFalse(vm.CanUseTextFormat, "非 UTF-8 的值不该提供原样文本形态。");
            Assert.IsTrue(vm.IsEscapedFormat);
            Assert.AreEqual(@"\x1f\x8b\b\x00\xc3(\x00\x03", vm.StringDraft);
            Assert.IsTrue(vm.HasValueFormatNotice, "要说清为什么是这个形态。");

            // 2) 十六进制是只读转储。
            vm.UseHexFormatCommand.Execute(null);
            await PumpAsync(10);
            Assert.IsTrue(vm.IsHexFormat);
            Assert.IsFalse(vm.CanEditString, "十六进制是排版不是表示,不能在上面编辑。");
            Assert.Contains("1f 8b 08", vm.StringDraft);

            // 3) 切回转义:字节没丢。
            vm.UseEscapedFormatCommand.Execute(null);
            await PumpAsync(10);
            Assert.AreEqual(@"\x1f\x8b\b\x00\xc3(\x00\x03", vm.StringDraft);
            Assert.IsTrue(vm.CanEditString);

            // 4) 改一个字节再存,服务端拿到的必须是**字节**而不是那串反斜杠字面量。
            vm.StringDraft = @"\x1f\x8b\b\x00\xc3(\x00\xff";
            Assert.IsTrue(vm.IsStringDirty);
            vm.SaveStringCommand.Execute(null);
            await PumpAsync();

            byte[] stored = await ReadRawAsync("bin:blob");
            CollectionAssert.AreEqual(
                new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0xC3, 0x28, 0x00, 0xFF }, stored,
                "值被按文本写回去了 —— 这正是要杜绝的那次静默损坏。");
        });
    }

    /// <summary>写坏的转义**不写入**:宁可让用户改一处笔误,也不要写进一段谁也说不清的字节。</summary>
    [TestMethod]
    public void BinaryValue_MalformedEscape_RefusesToWrite()
    {
        Require();
        OnUi(async () =>
        {
            byte[] original = [0x00, 0x01, 0x02];
            await SeedAsync("bad:blob", (db, key) => db.StringSet(key, original));
            await using Harness harness = await ShowAsync($"{_prefix}:bad");
            RedisWorkspaceViewModel vm = harness.ViewModel;

            vm.SelectedRow = Leaf(vm, _prefix, "bad", "blob");
            await PumpAsync();
            Assert.IsTrue(vm.IsEscapedFormat);

            vm.StringDraft = @"\x00\q\x02";
            vm.SaveStringCommand.Execute(null);
            await PumpAsync();

            Assert.Contains("转义写坏了", vm.StatusMessage);
            CollectionAssert.AreEqual(original, await ReadRawAsync("bad:blob"), "拒绝写入时服务端的值必须原封不动。");
        });
    }

    /// <summary>多行文本值走原样文本形态 —— 把一段多行 JSON 显示成 \n 字面量才是错的。</summary>
    [TestMethod]
    public void MultilineTextValue_StaysPlainText()
    {
        Require();
        OnUi(async () =>
        {
            await SeedAsync("multi:json", (db, key) => db.StringSet(key, "{\n  \"a\": 1\n}"));
            await using Harness harness = await ShowAsync($"{_prefix}:multi");
            RedisWorkspaceViewModel vm = harness.ViewModel;

            vm.SelectedRow = Leaf(vm, _prefix, "multi", "json");
            await PumpAsync();

            Assert.IsTrue(vm.CanUseTextFormat);
            Assert.IsTrue(vm.IsTextFormat);
            Assert.AreEqual("{\n  \"a\": 1\n}", vm.StringDraft, "换行要是真换行,不是 \n 两个字符。");
            Assert.IsFalse(vm.HasValueFormatNotice, "普通文本不必解释形态。");
        });
    }

    /// <summary>按 key 后缀读回服务端的原始字节(绕开视图模型,直接问 Redis)。</summary>
    private static async Task<byte[]> ReadRawAsync(string suffix)
    {
        using ConnectionMultiplexer mux = await ConnectionMultiplexer.ConnectAsync(
            new ConfigurationOptions { EndPoints = { { Host, Port } }, AllowAdmin = true, AbortOnConnectFail = true });
        RedisValue value = await mux.GetDatabase(Database).StringGetAsync($"{_prefix}:{suffix}");
        await mux.CloseAsync();
        return (byte[]?)value ?? [];
    }

}

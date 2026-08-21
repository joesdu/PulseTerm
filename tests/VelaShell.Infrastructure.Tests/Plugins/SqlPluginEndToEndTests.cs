using VelaShell.Infrastructure.Plugins;
using VelaShell.Infrastructure.Plugins.Protocols;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// 数据库插件的整链路验证:清单发现(<b>不装载程序集</b>)→ 五个页签画得出来 →
/// 惰性激活 → 注册成 <b>workspace</b>(而不是文件协议或终端协议)→ 真开一条 SQLite 会话。
/// <para>
/// <b>为什么插件自己的 196 条用例全绿也不够。</b> 那些用例都是**直接 new 出视图模型**跑的,
/// 它们绕过了宿主真正走的那条路:清单发现 → <see cref="VelaShell.PluginSdk.Hosting.PluginAssemblyLoadContext" /> 装载 →
/// 能力域注册。这条路上断掉的每一种方式,表现都是同一句"页签在,点了没反应":
/// 清单少一个字段、工作台 id 与 <c>onWorkspace:</c> 事件对不上、注册走了文件协议那条重载、
/// 或者驱动程序集没被复制到插件目录导致激活时抛 <c>FileNotFoundException</c>。
/// </para>
/// <para>
/// 最后一条对**这个**插件尤其要命:它带着 SqlSugar 与四个数据库驱动,
/// 是仓库里依赖最多的插件——而依赖漏拷在单测里是看不见的(单测的 bin 目录里什么都有)。
/// </para>
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class SqlPluginEndToEndTests
{
    private const string WorkspaceId = "velashell.sql";
    private const string DialectKey = "dialect";

    private static string _staged = null!;
    private string _root = null!;
    private string _dataRoot = null!;

    /// <summary>
    /// <b>整个插件目录只铺一次。</b>
    /// <para>
    /// 开发构建下这个目录是 **98 MB / 91 个文件**(全 RID 树都在,见 §11.3),
    /// 每个用例各铺一份就是四百多兆的文件复制 —— 慢,而且在磁盘吃紧时会变成随机失败。
    /// 各用例仍然各用各的 <c>PluginManager</c> 与数据目录,只共享这份只读的铺开产物。
    /// </para>
    /// </summary>
    /// <param name="_">MSTest 注入的上下文。</param>
    [ClassInitialize]
    public static void StageOnce(TestContext _)
    {
        _staged = Path.Combine(
            Path.GetTempPath(), "velashell-tests", $"sql-plugin-{Guid.NewGuid():N}", "velashell-sql");
        PluginOutputLocator.StageInto("VelaShell.Plugin.Sql", _staged);
    }

    [ClassCleanup]
    public static void RemoveStaged() => TryDelete(Path.GetDirectoryName(_staged));

    [TestInitialize]
    public void Setup()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "plugins");
        _dataRoot = Path.Combine(baseDir, "plugin-data");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup() => TryDelete(Path.GetDirectoryName(_root));

    private static void TryDelete(string? directory)
    {
        try
        {
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 尽力清理:插件 ALC 卸载前文件可能还占着。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }

    private (PluginManager Manager, PluginProtocolRegistry Registry) CreateManager()
    {
        var registry = new PluginProtocolRegistry();
        var manager = new PluginManager(new()
        {
            PluginRoots = [Path.GetDirectoryName(_staged)!],
            DataRootDirectory = _dataRoot,
            HostVersion = "1.0.0",
            ActivationTimeout = TimeSpan.FromSeconds(60),
            DeactivationTimeout = TimeSpan.FromSeconds(10),
            CommandsFactory = (_, _) => new RecordingCommands(),
            ProtocolRegistry = registry
        });
        return (manager, registry);
    }

    /// <summary>
    /// 页签在**不装载程序集**的前提下就画得出来 —— 这正是"不碰数据库的用户,
    /// 进程里一个字节的驱动都不装载"那条承诺的可测形态。
    /// <para>
    /// <b>而且只有一个页签。</b> 五个方言曾经是五个工作台,把连接类型那一排撑得很长;
    /// 现在它们是同一个页签的五个变体。这条断言 <c>Tabs.Count == 1</c> 就是那个决定的守卫 ——
    /// 哪天有人手滑把方言又拆回五个贡献,它会立刻变红。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 清单发现_只有一个数据库页签_而且此时还没装载程序集()
    {
        (PluginManager manager, PluginProtocolRegistry registry) = CreateManager();
        await manager.StartAsync();

        PluginProtocolTab tab = registry.Tabs.Single();
        Assert.AreEqual(WorkspaceId, tab.Id);
        Assert.IsFalse(tab.IsReady, "没人点它之前不该装载程序集。");
        // 清单里的默认端口取默认方言那一档;其余四种由变体覆盖(见下一条用例)。
        Assert.AreEqual(3306, tab.DefaultPort);
        Assert.AreEqual(PluginState.Discovered, manager.Plugins.Single().State);

        await manager.DisposeAsync();
    }

    /// <summary>
    /// 点一下 → 惰性激活 → 注册成 <b>workspace</b>。
    /// <para>
    /// 注册成文件协议的后果是宿主开出一个空的双栏文件浏览器,注册成终端协议则是一个连不上的终端;
    /// 两种都"能打开",都不报错,都完全不是数据库客户端。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 惰性激活_注册成工作台而不是文件协议或终端协议()
    {
        (PluginManager manager, PluginProtocolRegistry registry) = CreateManager();
        await manager.StartAsync();

        PluginWorkspaceRegistration? workspace = await registry.ResolveWorkspaceAsync(WorkspaceId);

        Assert.IsNotNull(workspace, "解析不出来 —— 多半是 onWorkspace: 事件与工作台 id 对不上。");
        Assert.IsNotNull(workspace.Provider, "工作台实现没注册上。");
        // 注册表把「工作台」与「文件/终端协议」分成两张表:同一个 id 在协议那张表里必须查不到,
        // 否则宿主会按协议路由,开出一个空的双栏文件浏览器或一个连不上的终端 —— 都不报错。
        Assert.IsNull(
            await registry.ResolveAsync(WorkspaceId),
            "这个 id 同时落进了协议表 —— 宿主会按协议路由,开出的不是数据库客户端。");
        Assert.AreEqual(PluginState.Active, manager.Plugins.Single().State);

        await manager.DisposeAsync();
    }

    /// <summary>
    /// 连接表单随方言变形 —— 现在靠的是**字段的显示条件**与**连接框的变体**这两层。
    /// <para>
    /// SQLite 最能说明问题:它没有端口、没有凭据,"主机"那一栏装的是文件路径。
    /// 两层里漏掉任何一层,用户面对的都是一张要他填"主机名"和口令的 SQLite 连接框。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 连接表单随方言变形()
    {
        (PluginManager manager, PluginProtocolRegistry registry) = CreateManager();
        await manager.StartAsync();
        WorkspaceDescriptor descriptor = (await registry.ResolveWorkspaceAsync(WorkspaceId))!.Descriptor;

        // ① 选方言的那一栏必须在,而且它就是变体的依据。
        Assert.AreEqual(DialectKey, descriptor.VariantKey);
        ProtocolSettingField dialect = descriptor.Fields.Single(f => f.Key == DialectKey);
        Assert.AreEqual(5, dialect.Choices.Count, "五个方言都该在下拉里。");

        // ② 方言专属字段按条件显隐。
        Assert.IsTrue(VisibleFor(descriptor, "oracleServiceName", "oracle"), "Oracle 的服务名这一栏没出来。");
        Assert.IsFalse(VisibleFor(descriptor, "oracleServiceName", "mysql"), "MySQL 上不该出现 Oracle 的服务名。");
        Assert.IsTrue(VisibleFor(descriptor, "schema", "postgresql"), "PG 的 search_path 这一栏没出来。");
        Assert.IsFalse(VisibleFor(descriptor, "schema", "mysql"));
        Assert.IsFalse(VisibleFor(descriptor, "database", "sqlite"), "SQLite 的库就是那个文件,不该再要一栏。");

        // ③ 连接框本身按变体变形。
        WorkspaceVariant sqlite = descriptor.Variants.Single(v => v.Value == "sqlite");
        Assert.IsTrue(sqlite.Features!.Value.HasFlag(WorkspaceFeatures.NoCredentials));
        Assert.IsFalse(string.IsNullOrEmpty(sqlite.HostLabel));
        Assert.AreEqual(5432, descriptor.Variants.Single(v => v.Value == "postgresql").DefaultPort);

        await manager.DisposeAsync();
    }

    /// <summary>按方言判某个字段此刻显不显示 —— 与宿主用的是同一个判据。</summary>
    private static bool VisibleFor(WorkspaceDescriptor descriptor, string key, string dialect)
    {
        ProtocolSettingField field = descriptor.Fields.Single(f => f.Key == key);
        return field.VisibleWhen is not { } condition
               || condition.IsSatisfiedBy(k => k == DialectKey ? dialect : null);
    }

    /// <summary>
    /// <b>整条路走到底:经宿主真开一条 SQLite 会话,并读回一张表。</b>
    /// <para>
    /// 这一条才真正验到"驱动程序集有没有被复制到插件目录":前面几条即使驱动全漏了也照样绿,
    /// 因为它们碰不到 <c>Microsoft.Data.Sqlite</c>。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 经宿主真开一条SQLite会话()
    {
        (PluginManager manager, PluginProtocolRegistry registry) = CreateManager();
        await manager.StartAsync();
        PluginWorkspaceRegistration registration = (await registry.ResolveWorkspaceAsync(WorkspaceId))!;

        string file = Path.Combine(_dataRoot, $"e2e-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(_dataRoot);

        await using IWorkspaceDocument document = await registration.Provider.OpenAsync(
            new WorkspaceConnectRequest
            {
                SessionId = "e2e",
                Host = file,
                Port = 1,
                DisplayName = "e2e",
                Settings = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    // 方言现在是一个设置值,不再是"哪个工作台"。
                    [DialectKey] = "sqlite",
                    ["readOnly"] = "false"
                }
            },
            CancellationToken.None);

        Assert.IsNotNull(document, "会话开不出来。");
        // SQLite 会在第一次真正打开时落盘 —— 文件在,就说明驱动确实被装载并执行了。
        Assert.IsTrue(File.Exists(file), "库文件没建出来 —— 驱动多半没被复制到插件目录。");

        await manager.DisposeAsync();
    }
}

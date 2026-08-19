using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Redis.Tests;

/// <summary>
/// 连接设置的解析。守住的是**默认值的语义**:哪些默认跟环境走、哪些必须夹取、
/// 哪些在集群下要被强制归零 —— 这些错了都不会编译失败,只会在真机上表现成怪现象。
/// </summary>
[TestClass]
public sealed class RedisSettingsTests
{
    private static WorkspaceConnectRequest Request(params (string Key, string Value)[] settings) =>
        new()
        {
            SessionId = "s1",
            Host = "127.0.0.1",
            Port = 6379,
            Settings = settings.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        };

    [TestMethod]
    public void From_NoSettings_UsesDeclaredDefaults()
    {
        var settings = RedisSettings.From(Request());

        Assert.AreEqual(RedisDeployment.Standalone, settings.Deployment);
        Assert.AreEqual(RedisEnvironment.Development, settings.Environment);
        Assert.AreEqual(":", settings.Delimiter);
        Assert.AreEqual(500, settings.ScanCount);
        Assert.AreEqual(5000, settings.ScanBudget);
        Assert.AreEqual(0, settings.Database);
        Assert.IsFalse(settings.UseTls);
        Assert.IsTrue(settings.SupportsDatabases);
    }

    [TestMethod]
    public void From_ProductionEnvironment_DefaultsToReadOnly()
    {
        // 生产环境**默认只读**:护栏的第一档。缺 readOnly 键时按环境定,
        // 这正是 Declare() 刻意不给该字段默认值的原因。
        var settings = RedisSettings.From(Request(("environment", "production")));

        Assert.IsTrue(settings.ReadOnly);
    }

    [TestMethod]
    public void From_ProductionWithExplicitReadOnlyFalse_RespectsUser()
    {
        // 用户显式关掉只读就听用户的 —— 区分"用户关掉了"与"没配过"是这段解析的全部意义。
        var settings = RedisSettings.From(Request(("environment", "production"), ("readOnly", "false")));

        Assert.IsFalse(settings.ReadOnly);
    }

    [TestMethod]
    public void From_DevelopmentEnvironment_DefaultsToWritable()
    {
        var settings = RedisSettings.From(Request(("environment", "development")));

        Assert.IsFalse(settings.ReadOnly);
    }

    [TestMethod]
    public void From_ClusterMode_ForcesDatabaseZeroAndHidesSelector()
    {
        // 集群只有 db0。与其等服务器回一句 SELECT 报错,不如在解析这一步就归零。
        var settings = RedisSettings.From(Request(("mode", "cluster"), ("database", "7")));

        Assert.AreEqual(RedisDeployment.Cluster, settings.Deployment);
        Assert.AreEqual(0, settings.Database);
        Assert.IsFalse(settings.SupportsDatabases);
    }

    [TestMethod]
    public void From_EmptyDelimiter_FallsBackToColon()
    {
        // 分隔符留空会让键空间塌成一层平列表(每个键都是根节点),不是用户想表达的"不分层"。
        var settings = RedisSettings.From(Request(("delimiter", "")));

        Assert.AreEqual(":", settings.Delimiter);
    }

    [TestMethod]
    public void From_OutOfRangeNumbers_AreClamped()
    {
        var settings = RedisSettings.From(Request(
            ("scanCount", "1"),
            ("scanBudget", "0"),
            ("valuePreview", "1"),
            ("connectTimeout", "1"),
            ("database", "999")));

        Assert.AreEqual(10, settings.ScanCount);
        Assert.AreEqual(100, settings.ScanBudget);
        Assert.AreEqual(1024, settings.ValuePreviewBytes);
        Assert.AreEqual(500, settings.ConnectTimeoutMs);
        Assert.AreEqual(255, settings.Database);
    }

    [TestMethod]
    public void From_UnparsableNumbers_FallBackToDefaults()
    {
        var settings = RedisSettings.From(Request(("scanCount", "abc"), ("database", "x")));

        Assert.AreEqual(500, settings.ScanCount);
        Assert.AreEqual(0, settings.Database);
    }

    [TestMethod]
    public void From_SentinelMode_KeepsMasterName()
    {
        var settings = RedisSettings.From(Request(("mode", "sentinel"), ("masterName", "mymaster")));

        Assert.AreEqual(RedisDeployment.Sentinel, settings.Deployment);
        Assert.AreEqual("mymaster", settings.MasterName);
        Assert.IsTrue(settings.SupportsDatabases, "哨兵背后仍是一台普通实例,数据库照常存在。");
    }

    [TestMethod]
    public void Declare_PutsTuningFieldsBehindAdvanced()
    {
        var loc = new Loc("zh-Hans");
        var fields = RedisSettings.Declare(loc).ToList();

        // 决定"连不连得上"的留在外面;调优类收进高级选项 —— 否则对话框会被顶出屏幕。
        Assert.IsFalse(fields.Single(f => f.Key == "mode").IsAdvanced);
        Assert.IsFalse(fields.Single(f => f.Key == "tls").IsAdvanced);
        Assert.IsFalse(fields.Single(f => f.Key == "environment").IsAdvanced);
        Assert.IsTrue(fields.Single(f => f.Key == "scanCount").IsAdvanced);
        Assert.IsTrue(fields.Single(f => f.Key == "valuePreview").IsAdvanced);
    }

    [TestMethod]
    public void Declare_ReadOnlyFieldHasNoDefault()
    {
        // 只读的默认值随环境走,在声明里写死任一个都会让另一半环境的默认是错的。
        var loc = new Loc("en");

        Assert.IsNull(RedisSettings.Declare(loc).Single(f => f.Key == "readOnly").DefaultValue);
    }

    [TestMethod]
    public void Declare_ThumbprintFieldIsHiddenAndMatchesDescriptorKey()
    {
        // 指纹字段必须真的在字段表里,否则宿主的"信任证书"点了等于没点。
        var loc = new Loc("en");
        ProtocolSettingField field = RedisSettings.Declare(loc).Single(f => f.Key == RedisSettings.TrustedThumbprintKey);

        Assert.IsTrue(field.IsHidden);
    }

    /// <summary>
    /// 只在某种形态下有意义的字段要**声明**成那样,而不是靠一行小字解释。
    /// <para>
    /// 顺带钉住"条件指向的键真的存在":写错键名的话条件永远不成立,
    /// 字段就此从表单上永久消失 —— 而这种错编译器一句话都不会说。
    /// </para>
    /// </summary>
    [TestMethod]
    public void Declare_ModeSpecificFieldsDeclareTheirVisibility()
    {
        var loc = new Loc("zh-Hans");
        var fields = RedisSettings.Declare(loc).ToList();
        var keys = fields.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);

        ProtocolSettingVisibility master = fields.Single(f => f.Key == "masterName").VisibleWhen!;
        Assert.AreEqual("mode", master.Key);
        CollectionAssert.AreEquivalent(new[] { "sentinel" }, master.Values.ToArray());

        ProtocolSettingVisibility database = fields.Single(f => f.Key == "database").VisibleWhen!;
        Assert.AreEqual("mode", database.Key);
        CollectionAssert.AreEquivalent(new[] { "standalone", "sentinel" }, database.Values.ToArray());

        // 每一条条件指向的键都必须是同一张表里真实存在的字段。
        foreach (ProtocolSettingField declared in fields.Where(f => f.VisibleWhen is not null))
        {
            Assert.Contains(declared.VisibleWhen!.Key, keys,
                $"字段 {declared.Key} 的显示条件指向了不存在的键 {declared.VisibleWhen.Key}。");
        }

        // 条件的取值必须落在被依赖字段的候选集里 —— 写成 "Sentinel" 也一样永不成立。
        foreach (ProtocolSettingField declared in fields.Where(f => f.VisibleWhen is not null))
        {
            ProtocolSettingField target = fields.Single(f => f.Key == declared.VisibleWhen!.Key);
            if (target.Kind != ProtocolSettingKind.Choice)
            {
                continue;
            }
            foreach (string value in declared.VisibleWhen!.Values)
            {
                Assert.Contains(choice => choice.Value == value, target.Choices,
                    $"字段 {declared.Key} 的条件取值 {value} 不在 {target.Key} 的候选里。");
            }
        }
    }

    /// <summary>集群形态下"默认数据库"这一格不显示,但落盘时照旧归零 —— 两件事各自成立。</summary>
    [TestMethod]
    public void ClusterMode_HidesTheDatabaseField_AndStillZeroesTheValue()
    {
        var loc = new Loc("en");
        ProtocolSettingVisibility condition =
            RedisSettings.Declare(loc).Single(f => f.Key == "database").VisibleWhen!;
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mode"] = "cluster",
            ["database"] = "9"
        };

        Assert.IsFalse(condition.IsSatisfiedBy(key => form.GetValueOrDefault(key)));

        var settings = RedisSettings.From(new WorkspaceConnectRequest
        {
            SessionId = "x",
            Host = "h",
            Port = 6379,
            Settings = form
        });
        Assert.AreEqual(0, settings.Database, "集群只有 db0:界面藏起来了,值也得归零。");
        Assert.IsFalse(settings.SupportsDatabases);
    }
}

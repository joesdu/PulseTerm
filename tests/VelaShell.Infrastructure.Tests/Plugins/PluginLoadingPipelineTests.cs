using VelaShell.Core.Resources;
using VelaShell.Core.Services;
using VelaShell.Infrastructure.Plugins;
using VelaShell.PluginSdk.Testing;
using VelaShell.TestPlugin;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// 装载链路的三条约定:装载过程要在后台活动账本上有交代、启动激活并行不串行、
/// 冷启动预读只读文件绝不越界去激活。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public class PluginLoadingPipelineTests
{
    private string _root = null!;
    private string _dataRoot = null!;
    private RecordingCommands _commands = null!;
    private BackgroundActivityService _activity = null!;

    /// <summary>账本上出现过的活动名(含已结束的),用于断言"转过圈"。</summary>
    private readonly List<string> _seenTitles = [];

    [TestInitialize]
    public void Setup()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "plugins");
        _dataRoot = Path.Combine(baseDir, "plugin-data");
        Directory.CreateDirectory(_root);
        _commands = new();
        _activity = new();
        _activity.Changed += () =>
        {
            lock (_seenTitles)
            {
                foreach (BackgroundActivitySnapshot snapshot in _activity.Activities)
                {
                    if (!_seenTitles.Contains(snapshot.Title))
                    {
                        _seenTitles.Add(snapshot.Title);
                    }
                }
            }
        };
    }

    [TestCleanup]
    public void Cleanup()
    {
        _activity.Dispose();
        try
        {
            Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);
        }
        catch
        {
            // ALC 卸载异步,dll 偶尔还锁着:留给临时目录。
        }
    }

    /// <summary>摊一个夹具插件到 <paramref name="directoryName" /> 子目录。</summary>
    private void StageFixture(string directoryName, string id, string manifestExtras = "")
    {
        string dir = Path.Combine(_root, directoryName);
        Directory.CreateDirectory(dir);
        File.Copy(typeof(TestFixturePlugin).Assembly.Location, Path.Combine(dir, "VelaShell.TestPlugin.dll"));
        File.WriteAllText(Path.Combine(dir, "plugin.json"), $$"""
            { "id": "{{id}}", "version": "0.1.0", "displayName": "Fixture {{directoryName}}",
              "entry": "VelaShell.TestPlugin.dll",
              "contributes": { "commands": [
                { "id": "{{id}}.list-sessions", "title": "Fixture: List Sessions", "category": "Fixture" }
              ] }{{manifestExtras}} }
            """);
    }

    private PluginManager CreateManager(bool prewarm = false, TimeSpan? prewarmDelay = null) => new(new()
    {
        PluginRoots = [_root],
        DataRootDirectory = _dataRoot,
        HostVersion = "1.0.0",
        ActivationTimeout = TimeSpan.FromSeconds(30),
        DeactivationTimeout = TimeSpan.FromSeconds(10),
        CommandsFactory = (_, _) => _commands,
        Activity = _activity,
        PrewarmLazyPlugins = prewarm,
        PrewarmDelay = prewarmDelay ?? TimeSpan.FromMilliseconds(50)
    });

    private bool SawActivity(string key)
    {
        lock (_seenTitles)
        {
            return _seenTitles.Contains(Strings.Get(key));
        }
    }

    [TestMethod]
    public async Task Activation_ReportsToTheBackgroundLedger_AndClearsItWhenDone()
    {
        StageFixture("hello", "velashell.fixture-a",
            """, "activationEvents": ["onCommand:velashell.fixture-a.list-sessions"]""");
        PluginManager manager = CreateManager();
        await manager.StartAsync();

        // 惰性等待期间不该有任何活动挂在账本上 —— 圆环此刻必须是收起来的。
        Assert.IsEmpty(_activity.Activities);

        await _commands.RunAsync("velashell.fixture-a.list-sessions");

        Assert.AreEqual(PluginState.Active, manager.Plugins.Single().State, manager.Plugins.Single().Error);
        Assert.IsTrue(SawActivity("Msg_PluginLoading"), "装载插件的过程必须在账本上露过面。");
        // 圆环绝不能因为某条活动没被收尾而一直转下去。
        Assert.IsEmpty(_activity.Activities, "激活结束后账本必须归零。");

        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task FailedActivation_StillClearsTheLedger()
    {
        // 入口 dll 不存在:激活必然失败。失败路径同样要把活动收干净,
        // 否则一次装载失败会让圆环永远转下去。
        string dir = Path.Combine(_root, "broken");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), """
            { "id": "velashell.broken", "version": "0.1.0", "displayName": "Broken",
              "entry": "NotThere.dll" }
            """);
        PluginManager manager = CreateManager();
        await manager.StartAsync();

        Assert.AreEqual(PluginState.Failed, manager.Plugins.Single().State);
        Assert.IsEmpty(_activity.Activities, "激活失败后账本必须归零。");

        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task MultipleStartupPlugins_AllActivate()
    {
        // 启动激活已改成并行:这条守的是"并行之后每一个都仍然被激活到位"。
        StageFixture("alpha", "velashell.fixture-alpha");
        StageFixture("beta", "velashell.fixture-beta");
        PluginManager manager = CreateManager();
        await manager.StartAsync();

        Assert.HasCount(2, manager.Plugins);
        Assert.IsEmpty(manager.Plugins.Where(p => p.State != PluginState.Active),
            $"仍有插件未激活:{string.Join(", ", manager.Plugins.Select(p => $"{p.Id}={p.State}({p.Error})"))}");
        Assert.IsEmpty(_activity.Activities);

        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task Prewarm_ReadsFiles_ButNeverActivatesTheLazyPlugin()
    {
        StageFixture("hello", "velashell.fixture-a",
            """, "activationEvents": ["onCommand:velashell.fixture-a.list-sessions"]""");
        PluginManager manager = CreateManager(prewarm: true);
        await manager.StartAsync();

        await WaitForAsync(() => SawActivity("Msg_PluginPrewarming"), TimeSpan.FromSeconds(10),
            "预读应在启动后不久跑起来");

        // 这是"只预读不激活"这条策略的全部要害:状态不动,数据目录不落痕迹。
        Assert.AreEqual(PluginState.Discovered, manager.Plugins.Single().State,
            "预读绝不能把惰性插件激活 —— 那是另一条策略。");
        Assert.IsFalse(File.Exists(Path.Combine(_dataRoot, "velashell.fixture-a", "storage.json")),
            "预读后不应有任何激活痕迹。");

        // 预读之后正常触发仍然照常激活。
        await _commands.RunAsync("velashell.fixture-a.list-sessions");
        Assert.AreEqual(PluginState.Active, manager.Plugins.Single().State, manager.Plugins.Single().Error);

        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task PrewarmDisabled_NeverTouchesTheLedger()
    {
        StageFixture("hello", "velashell.fixture-a",
            """, "activationEvents": ["onCommand:velashell.fixture-a.list-sessions"]""");
        PluginManager manager = CreateManager(prewarm: false);
        await manager.StartAsync();
        await Task.Delay(300);

        Assert.IsFalse(SawActivity("Msg_PluginPrewarming"), "关掉预读后不该有预热活动。");
        Assert.AreEqual(PluginState.Discovered, manager.Plugins.Single().State);

        await manager.DisposeAsync();
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, string message)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(50);
        }
        Assert.Fail(message);
    }
}

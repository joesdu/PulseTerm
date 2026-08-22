using VelaShell.Infrastructure.Plugins;
using VelaShell.PluginSdk;
using VelaShell.TestPlugin;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// 隔离模式真实端到端:PluginManager 拉起真实的 VelaShell.PluginHost 子进程,
/// 经命名管道握手、跨进程激活夹具插件、停用后进程回收。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public class IsolatedPluginTests
{
    private string _root = null!;
    private string _dataRoot = null!;

    [TestInitialize]
    public void Setup()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "plugins");
        _dataRoot = Path.Combine(baseDir, "plugin-data");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);
        }
        catch
        {
            // 子进程退出与文件释放是异步的,清不掉留给临时目录。
        }
    }

    private void StageFixture(string hostMode)
    {
        string dir = Path.Combine(_root, "hello");
        Directory.CreateDirectory(dir);
        File.Copy(typeof(TestFixturePlugin).Assembly.Location, Path.Combine(dir, "VelaShell.TestPlugin.dll"));
        File.WriteAllText(Path.Combine(dir, "plugin.json"), $$"""
            { "id": "velashell.test-fixture", "version": "0.1.0", "displayName": "Test Fixture",
              "entry": "VelaShell.TestPlugin.dll", "hostMode": "{{hostMode}}" }
            """);
    }

    [TestMethod]
    public async Task IsolatedPlugin_ActivatesInChildProcess_AndDeactivatesCleanly()
    {
        StageFixture("isolated");
        var manager = new PluginManager(new()
        {
            PluginRoots = [_root],
            DataRootDirectory = _dataRoot,
            HostVersion = "1.0.0",
            ActivationTimeout = TimeSpan.FromSeconds(30), // 子进程冷启动比进程内慢,放宽
            DeactivationTimeout = TimeSpan.FromSeconds(10)
        });
        await manager.StartAsync();
        PluginDescriptor descriptor = manager.Plugins.Single();
        Assert.AreEqual(PluginState.Active, descriptor.State, descriptor.Error);
        // 激活计数由插件进程本地写入存储:文件存在即证明跨进程激活真实跑通。
        Assert.IsTrue(File.Exists(Path.Combine(_dataRoot, "velashell.test-fixture", "storage.json")),
            "插件进程应把激活计数写入其数据目录");

        await manager.DisposeAsync();
        Assert.AreEqual(PluginState.Deactivated, manager.Plugins.Single().State);
    }

    [TestMethod]
    public async Task IsolatedPlugin_EntryMissing_FailsWithoutAffectingHost()
    {
        string dir = Path.Combine(_root, "ghost");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), """
            { "id": "a.ghost", "version": "1.0.0", "displayName": "G", "entry": "Ghost.dll", "hostMode": "isolated" }
            """);
        var manager = new PluginManager(new()
        {
            PluginRoots = [_root],
            DataRootDirectory = _dataRoot,
            HostVersion = "1.0.0"
        });
        await manager.StartAsync();
        Assert.AreEqual(PluginState.Failed, manager.Plugins.Single().State);
        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task IsolatedPlugin_CrashedProcess_RestartsWithBackoff_ThenFailsWhenExceeded()
    {
        StageFixture("isolated");
        var manager = new PluginManager(new()
        {
            PluginRoots = [_root],
            DataRootDirectory = _dataRoot,
            HostVersion = "1.0.0",
            ActivationTimeout = TimeSpan.FromSeconds(30),
            DeactivationTimeout = TimeSpan.FromSeconds(10),
            // 测试用极短退避:只允许 1 次自动重启,第二次崩溃即放弃。
            CrashRestartBackoff = [TimeSpan.FromMilliseconds(100)],
            CrashRestartWindow = TimeSpan.FromMinutes(5)
        });
        await manager.StartAsync();
        Assert.AreEqual(PluginState.Active, manager.Plugins.Single().State, manager.Plugins.Single().Error);
        int firstPid = manager.GetIsolatedProcessId("velashell.test-fixture")!.Value;

        // 第一次崩溃:强杀子进程 → 应按退避自动重启为新进程。
        System.Diagnostics.Process.GetProcessById(firstPid).Kill();
        await WaitForAsync(() => manager.Plugins.Single().State == PluginState.Active
                                 && manager.GetIsolatedProcessId("velashell.test-fixture") is { } pid
                                 && pid != firstPid,
            TimeSpan.FromSeconds(30), "崩溃后应自动重启为新的插件进程");
        int secondPid = manager.GetIsolatedProcessId("velashell.test-fixture")!.Value;
        Assert.AreNotEqual(firstPid, secondPid);

        // 第二次崩溃:超过退避上限 → Failed,不再重启。
        System.Diagnostics.Process.GetProcessById(secondPid).Kill();
        await WaitForAsync(() => manager.Plugins.Single().State == PluginState.Failed,
            TimeSpan.FromSeconds(15), "窗口内第二次崩溃应判 Failed 放弃自愈");
        Assert.Contains("crashed", manager.Plugins.Single().Error);

        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task IsolatedPlugin_AfterDeactivation_HostProcessLeavesOnItsOwn()
    {
        // 宿主进程必须自己退场,不能靠父进程那一刀:主程序退出路径上的兜底强杀是有时限的
        // (App 退出只给容器 2 秒),一旦子进程没能自行收摊,它就会活过主程序 ——
        // 实测留下过一个孤儿 VelaShell.PluginHost,锁着 bin\plugins 里的 dll,
        // 下一次编译直接以 MSB3027「文件被另一进程锁定」失败。
        // 自退场的三个码:0 = 停用应答后自退,2 = 管道断后自退,3 = 父进程消失后自退;
        // 被 Process.Kill 强杀则是 -1。
        StageFixture("isolated");
        var manager = new PluginManager(new()
        {
            PluginRoots = [_root],
            DataRootDirectory = _dataRoot,
            HostVersion = "1.0.0",
            ActivationTimeout = TimeSpan.FromSeconds(30),
            DeactivationTimeout = TimeSpan.FromSeconds(10)
        });
        await manager.StartAsync();
        Assert.AreEqual(PluginState.Active, manager.Plugins.Single().State, manager.Plugins.Single().Error);

        int pid = manager.GetIsolatedProcessId("velashell.test-fixture")!.Value;
        // 自己开一个句柄:PluginManager 释放后它那份 Process 就没了,退出码得从这里读。
        // 必须在进程还活着时把 Handle 抓在手里,否则退出后 ExitCode 会抛
        //「Process was not started by this object」。
        using var host = System.Diagnostics.Process.GetProcessById(pid);
        _ = host.Handle;

        await manager.DisposeAsync();

        Assert.IsTrue(host.WaitForExit(15_000), "停用后插件宿主进程必须退出");
        Assert.IsTrue(host.ExitCode is 0 or 2 or 3,
            $"宿主应自行退场(0/2/3),实际退出码 {host.ExitCode} —— 其它码说明它是被父进程强杀的,"
            + "而强杀在主程序退出路径上只有 2 秒预算,超时就会留下孤儿进程。");
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, string message)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(100);
        }
        Assert.Fail(message);
    }

    [TestMethod]
    public void Manifest_HostMode_ParsesCaseInsensitive()
    {
        PluginManifest manifest = PluginManifestReader.Parse("""
            { "id": "a.b", "version": "1.0.0", "displayName": "X", "entry": "X.dll", "hostMode": "isolated" }
            """);
        Assert.AreEqual(PluginHostMode.Isolated, manifest.HostMode);
        PluginManifest defaulted = PluginManifestReader.Parse("""
            { "id": "a.b", "version": "1.0.0", "displayName": "X", "entry": "X.dll" }
            """);
        Assert.AreEqual(PluginHostMode.InProcess, defaulted.HostMode);
    }
}

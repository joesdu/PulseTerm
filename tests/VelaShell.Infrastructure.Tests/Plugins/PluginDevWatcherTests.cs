using VelaShell.Infrastructure.Plugins;
using VelaShell.PluginSdk;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// 开发期插件的重建监视:哪些文件变化才算数,以及去抖。
/// </summary>
/// <remarks>
/// 这段逻辑原先埋在 <c>PluginManager</c> 里,只能靠真的动文件系统再等一秒半来验 ——
/// 那种用例既慢又不稳。拆出来之后,过滤规则可以直接断言,去抖也能用手动通知驱动。
/// </remarks>
[TestClass]
[TestCategory("PluginDevWatch")]
public sealed class PluginDevWatcherTests
{
    /// <summary>只有入口程序集与清单值得触发重载。</summary>
    /// <remarks>
    /// 一次构建会在 obj/bin 里翻出成百个中间文件。全都放行的话,去抖窗口会被无关变更
    /// 一直顶着 —— 永远等不到静默,于是<b>自动重载彻底不工作</b>,而现场看起来只是
    /// "改了代码没反应"。
    /// </remarks>
    [TestMethod]
    public void OnlyAssembliesAndTheManifestCount()
    {
        Assert.IsTrue(PluginDevWatcher.ShouldReactTo(@"C:\dev\plugin\bin\My.Plugin.dll"));
        Assert.IsTrue(PluginDevWatcher.ShouldReactTo("/home/dev/plugin/" + PluginManifestReader.FileName));

        Assert.IsFalse(PluginDevWatcher.ShouldReactTo(@"C:\dev\plugin\bin\My.Plugin.pdb"));
        Assert.IsFalse(PluginDevWatcher.ShouldReactTo(@"C:\dev\plugin\obj\project.assets.json"));
        Assert.IsFalse(PluginDevWatcher.ShouldReactTo(@"C:\dev\plugin\obj\build.cache"));
        Assert.IsFalse(PluginDevWatcher.ShouldReactTo(@"C:\dev\plugin\README.md"));
    }

    [TestMethod]
    public void TheExtensionMatchIsCaseInsensitive() =>
        // MSBuild 在某些路径上会写出大写扩展名;漏掉它等于在那台机器上自动重载不工作。
        Assert.IsTrue(PluginDevWatcher.ShouldReactTo(@"C:\dev\plugin\bin\My.Plugin.DLL"));

    [TestMethod]
    public void AnEmptyPathIsIgnored()
    {
        Assert.IsFalse(PluginDevWatcher.ShouldReactTo(""));
        Assert.IsFalse(PluginDevWatcher.ShouldReactTo("   "));
    }

    /// <summary>一串变更只触发一次重载。</summary>
    /// <remarks>
    /// 一次 <c>dotnet build</c> 会连着写十几个文件;每个都重载一次,插件会在几秒内
    /// 被拆装十几遍 —— 而其中大部分次都撞在"文件正被写"上失败。
    /// </remarks>
    [TestMethod]
    public async Task ABurstOfChangesFiresOnlyOnce()
    {
        int fired = 0;
        using PluginDevWatcher watcher = new(() => Interlocked.Increment(ref fired), _ => { });
        watcher.Start([]);   // 不挂真实监视器,只用手动通知驱动去抖

        for (int i = 0; i < 20; i++)
        {
            Assert.IsTrue(watcher.Notify($@"C:\dev\plugin\bin\Part{i}.dll"));
        }

        await WaitForAsync(() => Volatile.Read(ref fired) > 0);
        Assert.AreEqual(1, Volatile.Read(ref fired), "一串变更只该触发一次。");
    }

    [TestMethod]
    public async Task IrrelevantChangesDoNotKeepPushingTheWindowOut()
    {
        int fired = 0;
        using PluginDevWatcher watcher = new(() => Interlocked.Increment(ref fired), _ => { });
        watcher.Start([]);

        Assert.IsTrue(watcher.Notify(@"C:\dev\plugin\bin\My.Plugin.dll"));
        // 之后一直有 pdb / cache 在写:它们必须被挡在门外,否则窗口永远推不完。
        Assert.IsFalse(watcher.Notify(@"C:\dev\plugin\bin\My.Plugin.pdb"));
        Assert.IsFalse(watcher.Notify(@"C:\dev\plugin\obj\x.cache"));

        await WaitForAsync(() => Volatile.Read(ref fired) > 0);
        Assert.AreEqual(1, Volatile.Read(ref fired));
    }

    [TestMethod]
    public async Task NothingFiresAfterDispose()
    {
        int fired = 0;
        PluginDevWatcher watcher = new(() => Interlocked.Increment(ref fired), _ => { });
        watcher.Start([]);
        watcher.Notify(@"C:\dev\plugin\bin\My.Plugin.dll");
        watcher.Dispose();

        await Task.Delay(PluginDevWatcher.DebounceDelay + TimeSpan.FromMilliseconds(500));

        Assert.AreEqual(0, Volatile.Read(ref fired), "停机之后不该再触发重载。");
    }

    [TestMethod]
    public void DisposeIsIdempotent()
    {
        PluginDevWatcher watcher = new(() => { }, _ => { });
        watcher.Start([]);
        watcher.Dispose();
        watcher.Dispose();   // 停机路径会走到两次;第二次必须是空操作
    }

    [TestMethod]
    public void AMissingRootIsSkippedRatherThanThrowing()
    {
        // 开发期根目录来自命令行参数,指向一个还没建出来的目录是常事。
        using PluginDevWatcher watcher = new(() => { }, _ => { });
        watcher.Start([Path.Combine(Path.GetTempPath(), $"vela-nonexistent-{Guid.NewGuid():N}")]);
    }

    /// <summary>等条件成立;去抖是真实定时器,所以这里给足余量。</summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + PluginDevWatcher.DebounceDelay + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(25);
        }
        Assert.Fail("等超时了:去抖之后应当触发一次重载。");
    }
}

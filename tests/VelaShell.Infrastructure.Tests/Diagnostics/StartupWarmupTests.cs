using VelaShell.Infrastructure.Diagnostics;
using VelaShell.Infrastructure.Persistence;

namespace VelaShell.Infrastructure.Tests.Diagnostics;

/// <summary>
/// 把打开数据库提前到后台,与 Avalonia 初始化并行。
/// </summary>
/// <remarks>
/// <para>
/// 这一项的收益是实测出来的:交替跑 6 轮 A/B,首帧中位数 4293 → 4062 ms(−231 ms)。
/// 但风险也集中在一处 —— SonnetDB 对 WAL 持独占锁,<b>预热一个、DI 再开一个就必崩</b>。
/// 下面的用例围着这一条转:认领必须拿到同一个实例;根目录对不上要把预热的关掉;
/// 没人认领也要收回,否则用户下次启动打不开自己的数据库。
/// </para>
/// <para>
/// <see cref="StartupWarmup" /> 是进程级静态的(它量的就是这一个进程的启动),所以整类串行,
/// 且每条用例自己收尾 —— 留下一个开着的引擎会把同程序集里别的用例带崩。
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class StartupWarmupTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"vela-warmup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        StartupWarmup.DiscardIfUnclaimed();
        Environment.SetEnvironmentVariable(StartupWarmup.DisableEnvironmentVariable, null);
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // 被映射的段文件在 Windows 上删不掉,留给系统清临时目录。
        }
    }

    private VelaShellStoragePaths Paths(string? root = null) => new(root ?? _root);

    [TestMethod]
    public void ClaimWithoutAWarmupJustOpensTheDatabase()
    {
        // 测试、设计期、以及停用了预热的启动都走这条路 —— 行为必须与改动前完全一致。
        using SonnetDbEngine engine = StartupWarmup.Claim(Paths());

        Assert.StartsWith(Paths().RootDirectory, engine.RootDirectory);
    }

    [TestMethod]
    public void ClaimHandsBackTheVeryEngineThatWasWarmed()
    {
        // 这是整个机制的命门:若认领时另开一个,两个引擎会同时抢同一份 WAL,启动直接崩。
        StartupWarmup.Begin(Paths());
        Assert.IsTrue(StartupWarmup.IsPending);

        using SonnetDbEngine engine = StartupWarmup.Claim(Paths());

        Assert.IsFalse(StartupWarmup.IsPending, "认领之后不该还留着待认领的预热。");
        Assert.StartsWith(Paths().RootDirectory, engine.RootDirectory);
    }

    [TestMethod]
    public void ASecondClaimOpensAFreshEngineRatherThanReturningTheSameOne()
    {
        // 预热只兑现一次。第二次认领(DI 容器重建、测试宿主复用进程)必须就地新建,
        // 绝不能把已经交出去、可能已被 Dispose 的那一个再发一遍。
        StartupWarmup.Begin(Paths());
        SonnetDbEngine first = StartupWarmup.Claim(Paths());
        first.Dispose();

        using SonnetDbEngine second = StartupWarmup.Claim(Paths());

        Assert.AreNotSame(first, second);
    }

    [TestMethod]
    public void AWarmupForAnotherRootIsClosedInsteadOfHandedOver()
    {
        // 根目录对不上说明预热的是另一个库(--data-root 换过、或测试里覆盖了路径)。
        // 交出去就是打开了错的数据库;留着不管就是一直占着那个库的 WAL。
        string other = Path.Combine(Path.GetTempPath(), $"vela-warmup-other-{Guid.NewGuid():N}");
        Directory.CreateDirectory(other);
        try
        {
            StartupWarmup.Begin(Paths(other));

            using SonnetDbEngine engine = StartupWarmup.Claim(Paths());

            Assert.StartsWith(Paths().RootDirectory, engine.RootDirectory);
            // 预热那一个已被关掉,所以另一个库现在能正常打开 —— 没关的话这里会撞上 WAL 占用。
            using SonnetDbEngine reopened = new(Paths(other));
            Assert.StartsWith(Paths(other).RootDirectory, reopened.RootDirectory);
        }
        finally
        {
            try
            {
                Directory.Delete(other, true);
            }
            catch (IOException)
            {
                // 同 Cleanup。
            }
        }
    }

    [TestMethod]
    public void AnUnclaimedWarmupReleasesTheDatabaseAgain()
    {
        // 启动在认领之前就断了(迁移抛异常、库被占用)时不收回的话,那个后台开出来的引擎
        // 会一直占着 WAL —— 用户重开一次就撞上"数据库被占用",
        // 一个为了快半秒的优化反而把应用变成打不开。
        StartupWarmup.Begin(Paths());

        StartupWarmup.DiscardIfUnclaimed();

        Assert.IsFalse(StartupWarmup.IsPending);
        using SonnetDbEngine reopened = new(Paths());
        Assert.StartsWith(Paths().RootDirectory, reopened.RootDirectory);
    }

    [TestMethod]
    public void TheEscapeHatchTurnsWarmupIntoANoOp()
    {
        // 预热万一在某台机器上惹出麻烦,用户不必等新版就能绕开;这个开关也是 A/B 的量尺。
        Environment.SetEnvironmentVariable(StartupWarmup.DisableEnvironmentVariable, "1");

        StartupWarmup.Begin(Paths());

        Assert.IsFalse(StartupWarmup.IsPending);
        using SonnetDbEngine engine = StartupWarmup.Claim(Paths());
        Assert.StartsWith(Paths().RootDirectory, engine.RootDirectory);
    }
}

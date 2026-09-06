using System.Diagnostics;
using VelaShell.Infrastructure.Persistence;

namespace VelaShell.Infrastructure.Diagnostics;

/// <summary>
/// 把打开数据库这件事提前到后台,与 Avalonia 的平台初始化并行。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么值得做</b>:<see cref="StartupTrace" /> 量出来,冷启动里「打开 SonnetDB + 读设置」
/// 稳定占 ~590 ms(Debug 构建下约合首帧的 15%),而它排在 Avalonia 平台初始化与 XAML 加载
/// (~760 ms)之后 —— 那一段完全不碰数据库。两件事本可以同时做。
/// </para>
/// <para>
/// <b>为什么不能只是 <c>Task.Run(() =&gt; new SonnetDbEngine(...))</c> 了事</b>:SonnetDB 对它的
/// WAL 持独占锁。预热建一个、DI 再建一个,第二个必定撞上「文件被占用」,启动直接崩 ——
/// 所以预热出来的引擎必须**就是** DI 之后交出去的那一个。<see cref="Claim" /> 就是这个交接点:
/// DI 的工厂调它,拿走预热的成果;没预热过、或根目录对不上(测试、<c>--data-root</c> 换过),
/// 就地新建一个,行为与改动前完全一致。
/// </para>
/// <para>
/// <b>异常照原样抛出</b>:预热里撞上的「数据库被占用」要在 <see cref="Claim" /> 处原样浮出来,
/// 否则 <c>Program.Main</c> 那条 <c>IsDatabaseLockedFailure</c> 分支就接不住,
/// 用户看到的会从一句说得清的提示退回成崩溃框。
/// </para>
/// </remarks>
public static class StartupWarmup
{
    /// <summary>
    /// 设为 <c>1</c> 时不预热,数据库仍由 DI 就地打开(改动前的行为)。
    /// </summary>
    /// <remarks>
    /// 一是排障退路:预热万一在某台机器上惹出麻烦,用户不必等新版就能绕开。
    /// 二是量尺 —— 配合 <see cref="StartupTrace" /> 就能在同一个二进制上交替跑 A/B,
    /// 否则只能拿两次构建对比,而构建之间的差异会淹没这半秒。
    /// </remarks>
    public const string DisableEnvironmentVariable = "VELASHELL_NO_DB_WARMUP";

    private static readonly Lock _gate = new();
    private static Task<SonnetDbEngine>? _pending;
    private static string? _pendingRoot;

    /// <summary>预热是否已被认领(供测试与诊断观察)。</summary>
    public static bool IsPending
    {
        get
        {
            lock (_gate)
            {
                return _pending is not null;
            }
        }
    }

    /// <summary>
    /// 在后台开始打开数据库。重复调用只有第一次生效。
    /// </summary>
    /// <param name="paths">存储路径(必须与之后 DI 解析出来的那一份同根)。</param>
    public static void Begin(VelaShellStoragePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (Environment.GetEnvironmentVariable(DisableEnvironmentVariable) == "1")
        {
            return;
        }
        lock (_gate)
        {
            if (_pending is not null)
            {
                return;
            }
            _pendingRoot = paths.RootDirectory;
            // 刻意不 catch:异常留在 Task 里,由 Claim 原样抛出(见类型注释)。
            _pending = Task.Run(() =>
            {
                SonnetDbEngine engine = new(paths);
                StartupTrace.Mark("DbWarmup");
                return engine;
            });
        }
    }

    /// <summary>
    /// 认领预热出来的引擎;没有可认领的就地新建一个。
    /// </summary>
    /// <param name="paths">DI 解析出来的存储路径。</param>
    /// <returns>可用的引擎实例(调用方即所有者)。</returns>
    public static SonnetDbEngine Claim(VelaShellStoragePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Task<SonnetDbEngine>? pending;
        bool sameRoot;
        lock (_gate)
        {
            pending = _pending;
            sameRoot = string.Equals(_pendingRoot, paths.RootDirectory, StringComparison.OrdinalIgnoreCase);
            _pending = null;
            _pendingRoot = null;
        }
        if (pending is null)
        {
            return new(paths);
        }
        if (!sameRoot)
        {
            // 根目录对不上说明预热的是另一个库(测试里换了 --data-root,或路径被覆盖过)。
            // 那一个必须关掉,否则它一直占着另一个库的 WAL。
            Discard(pending);
            return new(paths);
        }
        return pending.GetAwaiter().GetResult();
    }

    /// <summary>
    /// 丢弃尚未认领的预热成果并关掉它打开的库。
    /// </summary>
    /// <remarks>
    /// 启动在认领之前就失败时(数据迁移抛了、单实例守卫改主意了)必须调这个:
    /// 否则那个后台开出来的引擎会一直占着 WAL,用户重开一次就撞上「数据库被占用」——
    /// 一个为了快 0.6 秒的优化,反而把应用变成打不开。
    /// </remarks>
    public static void DiscardIfUnclaimed()
    {
        Task<SonnetDbEngine>? pending;
        lock (_gate)
        {
            pending = _pending;
            _pending = null;
            _pendingRoot = null;
        }
        if (pending is not null)
        {
            Discard(pending);
        }
    }

    /// <summary>
    /// 等预热跑完,把它开出来的库关掉。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>同步等,不是尽力而为。</b>丢弃这件事的全部意义就是「那个库确实不占着了」——
    /// 挂个 <c>ContinueWith</c> 就返回的话,调用方紧接着去开同一个库仍会撞上 WAL 占用,
    /// 而那正是这段代码要防的事。半开的库直接不管更糟:文件句柄漏在后台线程上。
    /// </para>
    /// <para>
    /// 给一个上限是因为这两个调用点都在启动/退出路径上:开库要是真卡住了,
    /// 宁可漏一个句柄也不能让应用停在这儿不动。超时会留一行日志。
    /// </para>
    /// </remarks>
    private static void Discard(Task<SonnetDbEngine> pending)
    {
        try
        {
            if (!pending.Wait(DiscardTimeout))
            {
                Trace.WriteLine("[VelaShell] Timed out waiting for the warmed database engine to open; leaving it to the process exit.");
                return;
            }
            pending.Result.Dispose();
        }
        catch (AggregateException)
        {
            // 预热本身就失败了(多半是库被占用):这里只是把异常观察掉,
            // 真正的报错留给之后 DI 里那次就地新建去抛。
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VelaShell] Discarding warmed database engine failed: {ex}");
        }
    }

    /// <summary>丢弃时等待开库完成的上限。</summary>
    private static readonly TimeSpan DiscardTimeout = TimeSpan.FromSeconds(10);
}

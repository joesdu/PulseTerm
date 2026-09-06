using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace VelaShell.Benchmarks;

/// <summary>
/// 吞吐与分配基准的入口。
/// </summary>
/// <remarks>
/// <para>
/// <c>dotnet run -c Release --project tests/VelaShell.Benchmarks -- --filter *VtParser*</c>
/// 跑指定基准;不带 <c>--filter</c> 会列出全部让人选。<c>--smoke</c> 是本仓自加的短跑档
/// (BDN 的 <c>ShortRun</c>),用来在改动前后快速看方向,不用等完整的 15 次迭代。
/// </para>
/// <para>
/// <b>这些数字不进 CI 门禁。</b>BDN 的结果受机器负载影响太大,当门禁只会天天误报;
/// 它的用处是在<b>同一台机器上</b>比较改动前后 —— P-05 那次启动优化已经演示过,
/// 轮间抖动能轻松淹掉一次真实的 5% 改进,不交替对比就是在读噪声。
/// </para>
/// </remarks>
public static class Program
{
    /// <summary>短跑档的命令行开关。</summary>
    private const string SmokeSwitch = "--smoke";

    /// <summary>入口。</summary>
    /// <param name="args">透传给 BenchmarkDotNet 的参数(<c>--filter</c> 等)。</param>
    public static void Main(string[] args)
    {
        bool smoke = args.Contains(SmokeSwitch, StringComparer.Ordinal);
        string[] forwarded = [.. args.Where(a => !string.Equals(a, SmokeSwitch, StringComparison.Ordinal))];

        // 进程内跑,不生成子工程。这条最初是 BenchmarkDotNet 0.15.8 的绕行:它的运行时
        // moniker 表里没有 net11.0,默认工具链一上来就
        // `GetRuntimeVersion not implemented for NotRecognized` 崩掉。
        // 代价是没有进程隔离(BDN 会就此警告一句):被测代码与宿主共用一个运行时,
        // 环境变量、已加载的程序集、GC 状态都是同一份。对本仓这些纯 CPU/分配的基准够用。
        //
        // 0.16.0-preview.1 起 CsProjCoreToolchain 已经有了 NetCoreApp11_0,原来那条
        // "等 BDN 认识 net11.0 就换回默认工具链"的退出条件已经满足 —— 但换回去要真跑一轮
        // 子进程基准才敢说没问题,而基准不进 CI 门禁,坏了也没人拦得住。所以先原样保留,
        // 换工具链单独做、单独验。
        //
        // 静态成员名在 0.16 里从 Instance 改成了 Default(构造函数也从 (bool) 变成
        // (InProcessEmitSettings))。这里用静态成员,不受构造函数变更影响。
        IConfig config = DefaultConfig.Instance;
        Job job = (smoke ? Job.ShortRun.WithId("smoke") : Job.Default)
            .WithToolchain(InProcessEmitToolchain.Default);
        config = config.AddJob(job);
        BenchmarkSwitcher
            .FromAssembly(typeof(Program).Assembly)
            .Run(forwarded, config);
    }
}

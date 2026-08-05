using System.Net;
using System.Runtime.CompilerServices;
using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Concurrency;
using VelaShell.Core.Diagnostics;
using VelaShell.Features.Monitoring;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 链路追踪面板的生命周期测试。重点在连点"开始"时的重入:旧轮次的收尾和结果推送
/// 都可能晚于新轮次启动才排到 UI 线程,不做身份校验就会清掉新轮次的运行标记、
/// 或者把旧轮次的跃点灌进新列表。
/// </summary>
[TestClass]
[TestCategory("Trace")]
public class TraceRouteViewModelTests
{
    /// <summary>
    /// ReactiveCommand 的 CanExecute 默认在 RxApp.MainThreadScheduler 上投递,而无头单测里
    /// 那个调度器背后没有消息泵 —— 不换成立即调度,命令永远处于不可执行状态,Execute 静默无效。
    /// </summary>
    [ClassInitialize]
    public static void Init(TestContext _) =>
        RxSchedulers.MainThreadScheduler = ImmediateSequencer.Instance;

    [TestMethod]
    public async Task Restart_KeepsRunningFlag_WhenTheOldRunFinishesLate()
    {
        var service = new GatedTraceService();
        var vm = new TraceRouteViewModel(service, uiDispatcher: Inline) { Target = "example.test" };

        vm.StartCommand.Execute().Subscribe();
        Assert.IsTrue(vm.IsRunning);
        SlowRun first = await service.NextRun();

        // 第二次点击:新一轮启动后,第一轮才被取消并收尾。
        vm.StartCommand.Execute().Subscribe();
        Assert.IsTrue(vm.IsRunning);
        SlowRun second = await service.NextRun();
        await first.Completion;

        // 旧轮次收尾不得清掉新轮次的运行标记。
        Assert.IsTrue(vm.IsRunning, "旧轮次的收尾把新轮次的运行标记清掉了。");

        second.Release();
        vm.Dispose();
    }

    [TestMethod]
    public async Task Restart_DoesNotMergeResultsFromTheAbandonedRun()
    {
        var service = new GatedTraceService();
        var vm = new TraceRouteViewModel(service, uiDispatcher: Inline) { Target = "example.test" };

        vm.StartCommand.Execute().Subscribe();
        SlowRun first = await service.NextRun();
        vm.StartCommand.Execute().Subscribe();
        SlowRun second = await service.NextRun();

        // 被放弃的第一轮此时才推结果 —— 不该进新一轮的列表。
        first.Push(Hop(1, "10.0.0.1"));
        await first.Completion;
        Assert.IsEmpty(vm.Hops, "被放弃轮次的结果混进了新一轮的列表。");

        second.Push(Hop(1, "10.0.0.2"));
        second.Release();
        await second.Completion;
        Assert.HasCount(1, vm.Hops);
        Assert.AreEqual("10.0.0.2", vm.Hops[0].Address?.ToString());

        vm.Dispose();
    }

    [TestMethod]
    public async Task Stop_ClearsRunningFlagAndIsIdempotent()
    {
        var service = new GatedTraceService();
        var vm = new TraceRouteViewModel(service, uiDispatcher: Inline) { Target = "example.test" };

        vm.StartCommand.Execute().Subscribe();
        SlowRun run = await service.NextRun();
        vm.StopCommand.Execute().Subscribe();
        Assert.IsFalse(vm.IsRunning);

        vm.StopCommand.Execute().Subscribe(); // 重复停止不该抛
        await run.Completion;
        Assert.IsFalse(vm.IsRunning);
        vm.Dispose();
    }

    /// <summary>同步执行的调度器:单测里没有 UI 线程可切。</summary>
    private static Task Inline(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    private static TraceHop Hop(int ttl, string ip)
    {
        var hop = new TraceHop(ttl);
        hop.Add(new(ttl, IPAddress.Parse(ip), TimeSpan.FromMilliseconds(5), false, false));
        return hop;
    }

    /// <summary>一次可以被测试精确控制推进节奏的追踪。</summary>
    private sealed class SlowRun
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<TraceHop> _pending = [];

        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completion => Completed.Task;

        public void Push(TraceHop hop) => _pending.Add(hop);

        public void Release() => _release.TrySetResult();

        public async IAsyncEnumerable<TraceUpdate> Enumerate(
            [EnumeratorCancellation] CancellationToken token
        )
        {
            try
            {
                await using (token.Register(() => _release.TrySetResult()))
                {
                    await _release.Task.ConfigureAwait(false);
                }
                if (_pending.Count > 0)
                {
                    yield return new(_pending, false, 1, IPAddress.Loopback);
                }
            }
            finally
            {
                Completed.TrySetResult();
            }
        }
    }

    /// <summary>
    /// 把每次追踪都交给测试逐个取走。用队列而不是单个信号:追踪是在 Execute 里同步启动的,
    /// 测试拿到控制权时那一轮早已开始,信号式的实现会一直等下一轮,直接卡死。
    /// </summary>
    private sealed class GatedTraceService : ITraceRouteService
    {
        private readonly Lock _gate = new();
        private readonly Queue<SlowRun> _ready = new();
        private TaskCompletionSource<SlowRun>? _waiter;

        public Task<SlowRun> NextRun()
        {
            lock (_gate)
            {
                if (_ready.Count > 0)
                {
                    return Task.FromResult(_ready.Dequeue());
                }
                _waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return _waiter.Task;
            }
        }

        public IAsyncEnumerable<TraceUpdate> RunAsync(TraceOptions options, CancellationToken cancellationToken = default)
        {
            SlowRun run = new();
            lock (_gate)
            {
                if (_waiter is { } waiter)
                {
                    _waiter = null;
                    waiter.TrySetResult(run);
                }
                else
                {
                    _ready.Enqueue(run);
                }
            }
            return run.Enumerate(cancellationToken);
        }
    }
}

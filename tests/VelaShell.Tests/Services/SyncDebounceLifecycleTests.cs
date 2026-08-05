using VelaShell.Services;

namespace VelaShell.Tests.Services;

[TestClass]
public class SyncDebounceLifecycleTests
{
    [TestMethod]
    [TestCategory("SyncDebounce")]
    public void TrySwapNew_BeforeShutdown_ReturnsTrueWithNewToken()
    {
        var lifecycle = new SyncDebounceLifecycle();

        bool ok1 = lifecycle.TrySwapNew(out CancellationToken t1);
        bool ok2 = lifecycle.TrySwapNew(out CancellationToken t2);

        Assert.IsTrue(ok1);
        Assert.IsTrue(ok2);
        Assert.AreNotEqual(t2, t1, "Consecutive tokens must differ");
    }

    [TestMethod]
    [TestCategory("SyncDebounce")]
    public void TrySwapNew_CancelsPreviousToken()
    {
        var lifecycle = new SyncDebounceLifecycle();

        lifecycle.TrySwapNew(out CancellationToken first);
        Assert.IsFalse(first.IsCancellationRequested);

        lifecycle.TrySwapNew(out _);

        Assert.IsTrue(first.IsCancellationRequested);
    }

    [TestMethod]
    [TestCategory("SyncDebounce")]
    public void TrySwapNew_DoesNotCancelCurrentToken()
    {
        var lifecycle = new SyncDebounceLifecycle();

        lifecycle.TrySwapNew(out _); // previous (null) — no-op
        lifecycle.TrySwapNew(out CancellationToken current);

        Assert.IsFalse(current.IsCancellationRequested);
    }

    [TestMethod]
    [TestCategory("SyncDebounce")]
    public void MultipleSwaps_EachPreviousCancelled()
    {
        var lifecycle = new SyncDebounceLifecycle();

        var tokens = new CancellationToken[5];
        for (int i = 0; i < 5; i++)
        {
            lifecycle.TrySwapNew(out tokens[i]);
        }

        for (int i = 0; i < 4; i++)
        {
            Assert.IsTrue(tokens[i].IsCancellationRequested, $"Token[{i}] should be cancelled");
        }

        // The last one is still live.
        Assert.IsFalse(tokens[4].IsCancellationRequested);
    }

    [TestMethod]
    [TestCategory("SyncDebounce")]
    public void Shutdown_CancelsCurrentCts()
    {
        var lifecycle = new SyncDebounceLifecycle();

        lifecycle.TrySwapNew(out CancellationToken token);
        Assert.IsFalse(token.IsCancellationRequested);

        lifecycle.Shutdown();

        Assert.IsTrue(token.IsCancellationRequested);
    }

    [TestMethod]
    [TestCategory("SyncDebounce")]
    public void Shutdown_IsIdempotent()
    {
        var lifecycle = new SyncDebounceLifecycle();

        lifecycle.TrySwapNew(out _);
        lifecycle.Shutdown();
        lifecycle.Shutdown(); // second call is a no-op, no throw
    }

    [TestMethod]
    [TestCategory("SyncDebounce")]
    public void Shutdown_WithoutSwap_DoesNotThrow()
    {
        var lifecycle = new SyncDebounceLifecycle();

        lifecycle.Shutdown(); // no CTS was ever created
    }

    [TestMethod]
    [TestCategory("SyncDebounce")]
    public void TrySwapNew_AfterShutdown_ReturnsFalse()
    {
        var lifecycle = new SyncDebounceLifecycle();

        lifecycle.TrySwapNew(out _);
        lifecycle.Shutdown();

        bool ok = lifecycle.TrySwapNew(out CancellationToken token);

        Assert.IsFalse(ok, "TrySwapNew must refuse after Shutdown");
        Assert.IsFalse(token.CanBeCanceled, "Returned token must be CancellationToken.None (uncancellable)");
    }

    [TestMethod]
    [TestCategory("SyncDebounce")]
    public void Shutdown_AllIssuedTokensCancelled()
    {
        var lifecycle = new SyncDebounceLifecycle();

        lifecycle.TrySwapNew(out CancellationToken t1);
        lifecycle.TrySwapNew(out CancellationToken t2);
        lifecycle.TrySwapNew(out CancellationToken t3);

        lifecycle.Shutdown();

        Assert.IsTrue(t1.IsCancellationRequested);
        Assert.IsTrue(t2.IsCancellationRequested);
        Assert.IsTrue(t3.IsCancellationRequested);
    }

    [TestMethod]
    [TestCategory("SyncDebounce")]
    public void TrySwapNew_ConcurrentSwaps_DoNotLeak()
    {
        // Simulate rapid concurrent saves (each triggers QueueAutoSync).
        var lifecycle = new SyncDebounceLifecycle();
        var all = new System.Collections.Concurrent.ConcurrentBag<CancellationToken>();

        Parallel.For(0, 100, _ =>
        {
            if (lifecycle.TrySwapNew(out CancellationToken token))
            {
                all.Add(token);
            }
        });

        Assert.ContainsSingle(token => !token.IsCancellationRequested, all);

        lifecycle.Shutdown();

        Assert.IsTrue(all.All(token => token.IsCancellationRequested));
    }

    [TestMethod]
    [TestCategory("SyncDebounce")]
    public void ConcurrentSwapVsShutdown_NoNewSourceSurvives()
    {
        // Regression: concurrent TrySwapNew calls racing against Shutdown
        // must never produce a live, uncancellable token after Shutdown returns.
        for (int trial = 0; trial < 50; trial++)
        {
            var lifecycle = new SyncDebounceLifecycle();
            var collected = new System.Collections.Concurrent.ConcurrentBag<CancellationToken>();
            var barrier = new ManualResetEventSlim(false);

            Task[] swapTasks = [.. Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                barrier.Wait();
                if (lifecycle.TrySwapNew(out CancellationToken token))
                {
                    collected.Add(token);
                }
            }))];

            var shutdownTask = Task.Run(() =>
            {
                barrier.Wait();
                lifecycle.Shutdown();
            });

            barrier.Set();
            Task.WaitAll([.. swapTasks, shutdownTask]);

            // After Shutdown returns, every token that was successfully issued
            // must be cancelled. No uncancellable token may survive.
            foreach (CancellationToken token in collected)
            {
                Assert.IsTrue(
                    token.IsCancellationRequested,
                    $"Trial {trial}: a token survived Shutdown uncancelled — race condition");
            }

            // And no new swap may succeed after Shutdown.
            Assert.IsFalse(
                lifecycle.TrySwapNew(out _),
                $"Trial {trial}: TrySwapNew succeeded after Shutdown — gate broken");
        }
    }

    [TestMethod]
    [TestCategory("SyncDebounce")]
    public void TryStartCurrent_WithValidToken_InvokesDelegate()
    {
        var lifecycle = new SyncDebounceLifecycle();
        lifecycle.TrySwapNew(out CancellationToken token);
        bool invoked = false;

        bool ok = lifecycle.TryStartCurrent(() => { invoked = true; return Task.CompletedTask; }, token, out Task? task);

        Assert.IsTrue(ok);
        Assert.IsNotNull(task);
        Assert.IsTrue(invoked);
    }

    [TestMethod]
    [TestCategory("SyncDebounce")]
    public void TryStartCurrent_AfterShutdown_DoesNotInvokeDelegate()
    {
        var lifecycle = new SyncDebounceLifecycle();
        lifecycle.TrySwapNew(out CancellationToken token);
        lifecycle.Shutdown();
        bool invoked = false;

        bool ok = lifecycle.TryStartCurrent(() => { invoked = true; return Task.CompletedTask; }, token, out Task? task);

        Assert.IsFalse(ok);
        Assert.IsNull(task);
        Assert.IsFalse(invoked, "Delegate must never be invoked after Shutdown");
    }

    [TestMethod]
    [TestCategory("SyncDebounce")]
    public void TryStartCurrent_WithSupersededToken_DoesNotInvokeDelegate()
    {
        var lifecycle = new SyncDebounceLifecycle();
        lifecycle.TrySwapNew(out CancellationToken oldToken);
        lifecycle.TrySwapNew(out _); // supersede oldToken
        bool invoked = false;

        bool ok = lifecycle.TryStartCurrent(() => { invoked = true; return Task.CompletedTask; }, oldToken, out Task? task);

        Assert.IsFalse(ok);
        Assert.IsNull(task);
        Assert.IsFalse(invoked, "Delegate must never be invoked with a superseded token");
    }

    [TestMethod]
    [TestCategory("SyncDebounce")]
    public void TryStartCurrent_ClearsCtsAfterConsumption()
    {
        var lifecycle = new SyncDebounceLifecycle();
        lifecycle.TrySwapNew(out CancellationToken token);

        lifecycle.TryStartCurrent(() => Task.CompletedTask, token, out Task? _);

        // After consumption, a new TrySwapNew must succeed (CTS was cleared).
        bool ok = lifecycle.TrySwapNew(out CancellationToken freshToken);
        Assert.IsTrue(ok);
        Assert.IsFalse(freshToken.IsCancellationRequested);
    }

    [TestMethod]
    [TestCategory("SyncDebounce")]
    public void TryStartCurrent_CalledTwice_SecondFails()
    {
        var lifecycle = new SyncDebounceLifecycle();
        lifecycle.TrySwapNew(out CancellationToken token);

        lifecycle.TryStartCurrent(() => Task.CompletedTask, token, out Task? _);
        bool invoked = false;
        bool ok = lifecycle.TryStartCurrent(() => { invoked = true; return Task.CompletedTask; }, token, out Task? _);

        Assert.IsFalse(ok, "Second start with same token must fail — CTS was cleared");
        Assert.IsFalse(invoked);
    }

    [TestMethod]
    [TestCategory("SyncDebounce")]
    public void TryStartCurrent_InvokesDelegateWhileHoldingShutdownGate()
    {
        var lifecycle = new SyncDebounceLifecycle();
        lifecycle.TrySwapNew(out CancellationToken token);

        // `_gate` 的类型是 System.Threading.Lock(不是 object):此时 C# 的 lock 语句编译成
        // Lock.EnterScope(),走的**不是** Monitor。因此 Monitor.IsEntered(gate) 恒为 false,
        // 哪怕锁确实被持有 —— 必须用 Lock.IsHeldByCurrentThread 才测得准。
        // (这条测试曾因此假红:被测的不变量一直是成立的,错的是探针。)
        var gate = (Lock)typeof(SyncDebounceLifecycle)
            .GetField("_gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(lifecycle)!;
        bool gateHeld = false;

        bool started = lifecycle.TryStartCurrent(() =>
        {
            gateHeld = gate.IsHeldByCurrentThread;
            return Task.CompletedTask;
        }, token, out _);

        Assert.IsTrue(started);
        Assert.IsTrue(
            gateHeld,
            "The sync delegate must be invoked while holding the same gate used by Shutdown.");
    }
}

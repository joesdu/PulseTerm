using NSubstitute;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Plugins.Capabilities;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteExec;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// 插件远程执行能力。这一层的契约在 SDK 1.1 才补全(标准错误 + 退出码 + 流式),
/// 而它决定了插件能不能**如实**报告一条失败的命令 —— 在此之前
/// "命令失败了"和"命令没有输出"在插件看来完全一样。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public class RemoteExecCapabilityTests
{
    private static (RemoteExecCapability Capability, ISshClientWrapper Client, string SessionId) NewCapability()
    {
        var sessionId = Guid.NewGuid();
        ISshClientWrapper client = Substitute.For<ISshClientWrapper>();
        client.IsConnected.Returns(true);
        ISshConnectionService connections = Substitute.For<ISshConnectionService>();
        connections.GetClient(sessionId).Returns(client);
        return (new(connections), client, sessionId.ToString());
    }

    [TestMethod]
    public async Task RunAsync_CarriesStandardErrorAndExitCodeThrough()
    {
        (RemoteExecCapability capability, ISshClientWrapper client, string sessionId) = NewCapability();
        client.RunCommandDetailedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(new RemoteCommandResult("", "Error response from daemon: no such container", 1));

        ExecResult result = await capability.RunAsync(sessionId, "docker stop nope");

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("no such container", result.Error);
        // 标准错误**不并进**标准输出:解析 --format json 的插件会被一行警告噎死。
        Assert.AreEqual("", result.Output);
    }

    [TestMethod]
    public async Task RunAsync_DoesNotThrowOnNonZeroExit()
    {
        (RemoteExecCapability capability, ISshClientWrapper client, string sessionId) = NewCapability();
        client.RunCommandDetailedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(new RemoteCommandResult("", "boom", 42));

        // 命令跑失败是一种正常结果,不是异常事件 —— 抛出去会逼每个调用点包 try/catch。
        ExecResult result = await capability.RunAsync(sessionId, "false");
        Assert.AreEqual(42, result.ExitCode);
    }

    [TestMethod]
    public async Task RunAsync_ThrowsSessionNotFoundForUnknownSession()
    {
        (RemoteExecCapability capability, _, _) = NewCapability();
        await Assert.ThrowsExactlyAsync<PluginSessionNotFoundException>(
            () => capability.RunAsync(Guid.NewGuid().ToString(), "docker ps"));
    }

    [TestMethod]
    public async Task StreamAsync_ReportsEachLineWithItsStreamInOrder()
    {
        (RemoteExecCapability capability, ISshClientWrapper client, string sessionId) = NewCapability();
        client.StreamCommandAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Action<bool, string>>(), Arg.Any<CancellationToken>())
              .Returns(call =>
              {
                  Action<bool, string> onLine = call.Arg<Action<bool, string>>();
                  onLine(false, "first");
                  onLine(true, "a warning");
                  onLine(false, "second");
                  return Task.FromResult(new RemoteCommandStreamResult(0, 3));
              });

        SyncProgress<ExecOutput> lines = new();
        ExecStreamResult result = await capability.StreamAsync(sessionId, "docker logs -f web", null, lines);

        // **行序是契约的一部分**,而它成立的前提是接收器同步转发:宿主就在读行的那个线程上
        // 顺序调 Report。换成 System.Progress<T> 就会 Post 到线程池,顺序当场没了 ——
        // 所以这个测试刻意不用它,SDK 的文档里也写明了别用。
        Assert.AreEqual(3, result.Lines);
        CollectionAssert.AreEqual(
            (string[])["first", "a warning", "second"],
            lines.Items.Select(static l => l.Line).ToArray());
        Assert.AreEqual(ExecStream.StandardOutput, lines.Items[0].Stream);
        Assert.AreEqual(ExecStream.StandardError, lines.Items[1].Stream);
        Assert.AreEqual(ExecStream.StandardOutput, lines.Items[2].Stream);
    }

    [TestMethod]
    public async Task StreamAsync_PassesTheStandardErrorPreferenceDown()
    {
        (RemoteExecCapability capability, ISshClientWrapper client, string sessionId) = NewCapability();
        client.StreamCommandAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Action<bool, string>>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(new RemoteCommandStreamResult(0, 0)));

        await capability.StreamAsync(sessionId, "docker events", new() { IncludeStandardError = false },
            new SyncProgress<ExecOutput>());

        await client.Received(1).StreamCommandAsync(
            "docker events", false, Arg.Any<Action<bool, string>>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task StreamAsync_RefusesMoreThanTheConcurrencyCap()
    {
        (RemoteExecCapability capability, ISshClientWrapper client, string sessionId) = NewCapability();
        // 挂在**调用方给的**令牌上,而不是测试自己的:那才是真实形态
        // (`docker logs -f` 一直跑,直到面板关掉、插件取消它)。
        client.StreamCommandAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Action<bool, string>>(), Arg.Any<CancellationToken>())
              .Returns(async call =>
              {
                  await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>()).ConfigureAwait(false);
                  return new RemoteCommandStreamResult(0, 0);
              });

        using CancellationTokenSource release = new();
        List<Task> inFlight = [];
        for (int i = 0; i < IRemoteExecApi.MaxConcurrentStreams; i++)
        {
            inFlight.Add(capability.StreamAsync(sessionId, $"tail -F /var/log/{i}", null, new SyncProgress<ExecOutput>(), release.Token));
        }
        await WaitForAsync(() => client.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ISshClientWrapper.StreamCommandAsync))
                                 == IRemoteExecApi.MaxConcurrentStreams);

        // 流是不限时的,每条占一个 SSH 通道。没有上限的话,一个忘了取消的插件
        // 能把对端的 MaxSessions 耗光 —— 坏掉的是用户的连接,不只是这个插件。
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => capability.StreamAsync(sessionId, "one too many", null, new SyncProgress<ExecOutput>()));

        await release.CancelAsync();
        foreach (Task task in inFlight)
        {
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => task);
        }

        // 名额在收尾时归还:取消掉那几条之后应该又能开新的。
        client.StreamCommandAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Action<bool, string>>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(new RemoteCommandStreamResult(0, 0)));
        ExecStreamResult after = await capability.StreamAsync(sessionId, "docker events", null, new SyncProgress<ExecOutput>());
        Assert.AreEqual(0, after.ExitCode);
    }

    [TestMethod]
    public async Task StreamAsync_TranslatesItsOwnDeadlineIntoTimeout()
    {
        (RemoteExecCapability capability, ISshClientWrapper client, string sessionId) = NewCapability();
        client.StreamCommandAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Action<bool, string>>(), Arg.Any<CancellationToken>())
              .Returns(async call =>
              {
                  await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>()).ConfigureAwait(false);
                  return new RemoteCommandStreamResult(0, 0);
              });

        // 超时是"我给的死线到了",取消是"调用方不要了" —— 两者对调用方的意义不同,
        // 不该都表现成 OperationCanceledException。
        await Assert.ThrowsExactlyAsync<TimeoutException>(
            () => capability.StreamAsync(sessionId, "tail -F /var/log/syslog",
                new() { Timeout = TimeSpan.FromMilliseconds(80) }, new SyncProgress<ExecOutput>()));
    }

    [TestMethod]
    public async Task StreamAsync_PropagatesCallerCancellationAsCancellation()
    {
        (RemoteExecCapability capability, ISshClientWrapper client, string sessionId) = NewCapability();
        client.StreamCommandAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Action<bool, string>>(), Arg.Any<CancellationToken>())
              .Returns(async call =>
              {
                  await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>()).ConfigureAwait(false);
                  return new RemoteCommandStreamResult(0, 0);
              });
        using CancellationTokenSource cts = new();
        Task streaming = capability.StreamAsync(sessionId, "docker logs -f web", null, new SyncProgress<ExecOutput>(), cts.Token);

        await cts.CancelAsync();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => streaming);
    }

    [TestMethod]
    public async Task StreamAsync_ThrowsSessionNotFoundForUnknownSession()
    {
        (RemoteExecCapability capability, _, _) = NewCapability();
        await Assert.ThrowsExactlyAsync<PluginSessionNotFoundException>(
            () => capability.StreamAsync(Guid.NewGuid().ToString(), "docker events", null, new SyncProgress<ExecOutput>()));
    }

    [TestMethod]
    public void ExecResult_FailureTextPrefersStandardError()
    {
        ExecResult failed = new("some stdout noise") { Error = "Error: no such volume\ndetails", ExitCode = 1 };
        Assert.AreEqual("Error: no such volume", failed.FailureText);

        ExecResult quiet = new("") { Error = "", ExitCode = 7 };
        Assert.AreEqual("exit 7", quiet.FailureText);
    }

    /// <summary>
    /// 同步转发的 <see cref="IProgress{T}" />:<see cref="Report" /> 就在调用线程上执行。
    /// <para>
    /// 这正是 SDK 要求插件使用的形态 —— <see cref="Progress{T}" /> 会把回调 Post 出去,
    /// 顺序与线程都不再保证,而日志流的顺序是它全部的意义。
    /// </para>
    /// </summary>
    /// <typeparam name="T">回调载荷类型。</typeparam>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly List<T> _items = [];

        /// <summary>已收到的载荷(按到达顺序)。</summary>
        public IReadOnlyList<T> Items
        {
            get
            {
                lock (_items)
                {
                    return [.. _items];
                }
            }
        }

        /// <inheritdoc />
        public void Report(T value)
        {
            lock (_items)
            {
                _items.Add(value);
            }
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }
        Assert.IsTrue(condition(), "condition was not met before the timeout");
    }
}

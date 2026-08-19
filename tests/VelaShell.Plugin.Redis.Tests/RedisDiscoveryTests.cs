using System.Text;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Redis.Tests;

/// <summary>
/// 从 SSH 会话探测 Redis 的解析与提议。
/// <para>
/// 这是本插件的差异化功能,而它的正确性几乎全在**解析远端输出**这一件事上 ——
/// 用 SDK 的假会话/假 exec/假远端文件系统把三种真实机器的输出喂进去,
/// 比在真机上试三遍更能覆盖(老系统只有 netstat、新系统有 ss、配置文件读不到)。
/// </para>
/// </summary>
[TestClass]
public sealed class RedisDiscoveryTests
{
    private const string PluginId = "velashell.redis";

    private static TestPluginContext NewContext(string? locale = null)
    {
        var context = new TestPluginContext { PluginId = PluginId };
        if (locale is { Length: > 0 })
        {
            context.HostInfo.Locale = locale;
        }
        return context;
    }

    /// <summary>把四段输出拼成 <c>RemoteExec</c> 会返回的那种带分隔标记的整体。</summary>
    private static string Sections(string listening, string processes, string version, string configs) =>
        string.Join("###VELA###", [listening, processes, version, configs]);

    [TestMethod]
    public async Task Probe_ParsesSsOutput()
    {
        using TestPluginContext context = NewContext();
        context.FakeSessions.AddConnected(host: "prod-1");
        string sessionId = (await context.Sessions.ListAsync())[0].SessionId;
        context.FakeRemoteExec.Handler = (_, _) => Sections(
            "LISTEN 0 511 127.0.0.1:6379 0.0.0.0:* users:((\"redis-server\",pid=1204,fd=6))\n",
            string.Empty,
            "Redis server v=7.2.4 sha=00000000:0 malloc=jemalloc bits=64\n",
            string.Empty);

        IReadOnlyList<RedisDiscoveredInstance> found = await new RedisDiscovery(context).ProbeAsync(sessionId);

        RedisDiscoveredInstance instance = found.Single();
        Assert.AreEqual(6379, instance.Port);
        Assert.AreEqual("7.2.4", instance.Version);
        Assert.IsFalse(instance.HasPassword);
    }

    [TestMethod]
    public async Task Probe_ParsesNetstatOutputOnOlderSystems()
    {
        using TestPluginContext context = NewContext();
        context.FakeSessions.AddConnected();
        string sessionId = (await context.Sessions.ListAsync())[0].SessionId;
        context.FakeRemoteExec.Handler = (_, _) => Sections(
            "tcp 0 0 127.0.0.1:6379 0.0.0.0:* LISTEN 1204/redis-server\n",
            string.Empty,
            "redis-cli 5.0.7\n",
            string.Empty);

        IReadOnlyList<RedisDiscoveredInstance> found = await new RedisDiscovery(context).ProbeAsync(sessionId);

        Assert.AreEqual(6379, found.Single().Port);
        Assert.AreEqual("5.0.7", found.Single().Version);
    }

    [TestMethod]
    public async Task Probe_FindsMultipleInstancesAndSortsThem()
    {
        // 一台机器上跑两个实例是常态。
        using TestPluginContext context = NewContext();
        context.FakeSessions.AddConnected();
        string sessionId = (await context.Sessions.ListAsync())[0].SessionId;
        context.FakeRemoteExec.Handler = (_, _) => Sections(
            "LISTEN 0 511 127.0.0.1:6380 0.0.0.0:* users:((\"redis-server\"))\n"
            + "LISTEN 0 511 127.0.0.1:6379 0.0.0.0:* users:((\"redis-server\"))\n",
            string.Empty,
            "Redis server v=7.0.0\n",
            string.Empty);

        IReadOnlyList<RedisDiscoveredInstance> found = await new RedisDiscovery(context).ProbeAsync(sessionId);

        Assert.AreSequenceEqual([6379, 6380], [.. found.Select(item => item.Port)]);
    }

    [TestMethod]
    public async Task Probe_FallsBackToTheProcessCommandLine()
    {
        // ss/netstat 都拿不到(容器里常见),但进程命令行还在。
        using TestPluginContext context = NewContext();
        context.FakeSessions.AddConnected();
        string sessionId = (await context.Sessions.ListAsync())[0].SessionId;
        context.FakeRemoteExec.Handler = (_, _) => Sections(
            string.Empty,
            "/usr/bin/redis-server *:6381\n",
            string.Empty,
            string.Empty);

        IReadOnlyList<RedisDiscoveredInstance> found = await new RedisDiscovery(context).ProbeAsync(sessionId);

        Assert.AreEqual(6381, found.Single().Port);
        Assert.AreEqual(string.Empty, found.Single().Version, "版本探不到就留空,不猜。");
    }

    [TestMethod]
    public async Task Probe_ReadsPortPasswordAndTlsFromTheConfigFile()
    {
        using TestPluginContext context = NewContext();
        context.FakeSessions.AddConnected();
        string sessionId = (await context.Sessions.ListAsync())[0].SessionId;
        context.FakeRemoteExec.Handler = (_, _) => Sections(
            "LISTEN 0 511 127.0.0.1:6379 0.0.0.0:* users:((\"redis-server\"))\n"
            + "LISTEN 0 511 127.0.0.1:6390 0.0.0.0:* users:((\"redis-server\"))\n",
            string.Empty,
            "Redis server v=7.2.4\n",
            "/etc/redis/redis.conf\n");
        context.FakeRemoteFs.AddFile(sessionId, "/etc/redis/redis.conf", Encoding.UTF8.GetBytes("""
            # Redis configuration
            bind 127.0.0.1
            port 6379
            tls-port 6390
            requirepass s3cr3t
            appendonly yes
            """));

        IReadOnlyList<RedisDiscoveredInstance> found = await new RedisDiscovery(context).ProbeAsync(sessionId);

        RedisDiscoveredInstance plain = found.Single(item => item.Port == 6379);
        Assert.IsTrue(plain.HasPassword);
        Assert.IsFalse(plain.UsesTls);
        Assert.AreEqual("/etc/redis/redis.conf", plain.ConfigPath);

        RedisDiscoveredInstance tls = found.Single(item => item.Port == 6390);
        Assert.IsTrue(tls.UsesTls, "tls-port 上的那个实例要标成 TLS。");
    }

    [TestMethod]
    public async Task Probe_UnreadableConfig_StillReportsTheInstance()
    {
        // 配置常常只有 root 可读。读不到不该表现成"探测失败" ——
        // 只意味着用户要自己填一次密码。
        using TestPluginContext context = NewContext();
        context.FakeSessions.AddConnected();
        string sessionId = (await context.Sessions.ListAsync())[0].SessionId;
        context.FakeRemoteExec.Handler = (_, _) => Sections(
            "LISTEN 0 511 127.0.0.1:6379 0.0.0.0:* users:((\"redis-server\"))\n",
            string.Empty,
            "Redis server v=7.2.4\n",
            "/etc/redis/redis.conf\n");
        // 刻意不写那份文件 → 假远端文件系统会报找不到。

        IReadOnlyList<RedisDiscoveredInstance> found = await new RedisDiscovery(context).ProbeAsync(sessionId);

        Assert.AreEqual(6379, found.Single().Port);
        Assert.IsFalse(found.Single().HasPassword);
        Assert.AreEqual(string.Empty, found.Single().ConfigPath);
    }

    [TestMethod]
    public async Task Probe_NothingListening_ReturnsEmpty()
    {
        using TestPluginContext context = NewContext();
        context.FakeSessions.AddConnected();
        string sessionId = (await context.Sessions.ListAsync())[0].SessionId;
        context.FakeRemoteExec.Handler = (_, _) => Sections(string.Empty, string.Empty, string.Empty, string.Empty);

        Assert.IsEmpty(await new RedisDiscovery(context).ProbeAsync(sessionId));
    }

    [TestMethod]
    public async Task Probe_ExecFailure_DegradesToEmpty()
    {
        // 远端连不上/命令被禁:探测无果,不是异常 —— 命令面板里的命令不该把宿主吓一跳。
        using TestPluginContext context = NewContext();
        context.FakeSessions.AddConnected();
        string sessionId = (await context.Sessions.ListAsync())[0].SessionId;
        context.FakeRemoteExec.Handler = (_, _) => throw new InvalidOperationException("no exec channel");

        Assert.IsEmpty(await new RedisDiscovery(context).ProbeAsync(sessionId));
    }

    [TestMethod]
    public async Task ConnectedSessions_SkipsSessionsThatAreNotConnected()
    {
        using TestPluginContext context = NewContext();
        context.FakeSessions.AddConnected(host: "up-1");
        context.FakeSessions.AddConnected(host: "up-2");

        IReadOnlyList<PluginSdk.Sessions.SessionInfo> sessions =
            await new RedisDiscovery(context).ConnectedSessionsAsync();

        // 替身只造得出已连接的会话,所以这里守的是全部都被收进来;
        // "跳过未连接的" 那一半由 ConnectedSessionsAsync 里的 State 过滤本身承担。
        Assert.HasCount(2, sessions);
        Assert.IsTrue(sessions.All(session => session.State == PluginSdk.Sessions.SessionState.Connected));
    }

    // ── 提议连接 ──────────────────────────────────────────────────

    [TestMethod]
    public async Task DiscoverCommand_ProposesOneConnectionPerInstance()
    {
        using TestPluginContext context = NewContext("zh-Hans");
        context.FakeSessions.AddConnected(host: "prod-1");
        context.FakeRemoteExec.Handler = (_, _) => Sections(
            "LISTEN 0 511 127.0.0.1:6379 0.0.0.0:* users:((\"redis-server\"))\n"
            + "LISTEN 0 511 127.0.0.1:6380 0.0.0.0:* users:((\"redis-server\"))\n",
            string.Empty,
            "Redis server v=7.2.4\n",
            string.Empty);
        // 模拟用户两次都按了保存。
        context.RecordingWorkspaces.ProposalAccepted = true;
        var plugin = new RedisPlugin();
        await plugin.ActivateAsync(context, CancellationToken.None);

        await context.RecordingCommands.RunAsync("velashell.redis.discover");

        Assert.HasCount(2, context.RecordingWorkspaces.Proposals);
        WorkspaceConnectionProposal first = context.RecordingWorkspaces.Proposals[0];
        Assert.AreEqual(PluginId, first.WorkspaceId);
        Assert.AreEqual(6379, first.Port);
        Assert.Contains("prod-1", first.Name, "名字里要带主机 —— 一台机器上两个实例得分得清。");
        Assert.AreEqual("development", first.Settings["environment"],
            "不替用户猜「这是生产」:环境标记决定护栏强度。");
    }

    [TestMethod]
    public async Task DiscoverCommand_StopsWhenTheUserCancels()
    {
        // 取消一条就是"够了"的信号,继续弹下一个只会烦人。
        using TestPluginContext context = NewContext();
        context.FakeSessions.AddConnected();
        context.FakeRemoteExec.Handler = (_, _) => Sections(
            "LISTEN 0 511 127.0.0.1:6379 0.0.0.0:* users:((\"redis-server\"))\n"
            + "LISTEN 0 511 127.0.0.1:6380 0.0.0.0:* users:((\"redis-server\"))\n",
            string.Empty, string.Empty, string.Empty);
        context.RecordingWorkspaces.ProposalAccepted = false;
        await new RedisPlugin().ActivateAsync(context, CancellationToken.None);

        await context.RecordingCommands.RunAsync("velashell.redis.discover");

        Assert.HasCount(1, context.RecordingWorkspaces.Proposals);
    }

    [TestMethod]
    public async Task DiscoverCommand_CarriesTheDiscoveredPasswordButNeverLogsIt()
    {
        using TestPluginContext context = NewContext();
        context.FakeSessions.AddConnected();
        string sessionId = (await context.Sessions.ListAsync())[0].SessionId;
        context.FakeRemoteExec.Handler = (_, _) => Sections(
            "LISTEN 0 511 127.0.0.1:6379 0.0.0.0:* users:((\"redis-server\"))\n",
            string.Empty, string.Empty, "/etc/redis/redis.conf\n");
        context.FakeRemoteFs.AddFile(sessionId, "/etc/redis/redis.conf",
            Encoding.UTF8.GetBytes("port 6379\nrequirepass hunter2\n"));
        context.RecordingWorkspaces.ProposalAccepted = true;
        await new RedisPlugin().ActivateAsync(context, CancellationToken.None);

        await context.RecordingCommands.RunAsync("velashell.redis.discover");

        Assert.AreEqual("hunter2", context.RecordingWorkspaces.Proposals.Single().Password,
            "探到的口令要带上,否则用户还得自己去翻一遍配置。");
        // **口令绝不进日志。** 这一条比"能探到"更重要。
        Assert.DoesNotContain(
            entry => entry.Message.Contains("hunter2", StringComparison.Ordinal), context.CollectingLog.Entries,
            "日志里出现了口令。");
    }

    [TestMethod]
    public async Task DiscoverCommand_NoConnectedSession_DoesNothing()
    {
        using TestPluginContext context = NewContext();
        await new RedisPlugin().ActivateAsync(context, CancellationToken.None);

        await context.RecordingCommands.RunAsync("velashell.redis.discover");

        Assert.IsEmpty(context.RecordingWorkspaces.Proposals);
    }

    [TestMethod]
    public async Task Proposal_ForAnotherPluginsWorkspaceId_IsRejected()
    {
        // 借宿主的对话框去替别家建配置 —— 能力面必须挡住。
        using TestPluginContext context = NewContext();

        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            context.Workspaces.ProposeConnectionAsync(new()
            {
                WorkspaceId = "other.vendor",
                Name = "x",
                Host = "127.0.0.1",
                Port = 6379
            }));
    }

    [TestMethod]
    public async Task Proposal_WithAnOutOfRangePort_IsRejected()
    {
        using TestPluginContext context = NewContext();

        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            context.Workspaces.ProposeConnectionAsync(new()
            {
                WorkspaceId = PluginId,
                Name = "x",
                Host = "127.0.0.1",
                Port = 0
            }));
    }
}

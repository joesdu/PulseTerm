using NSubstitute;
using VelaShell.Core.Processes;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Infrastructure.Tests;

/// <summary>
/// 远端任务管理器的数据源:采集前先确认对端认 sh 语法。
/// </summary>
/// <remarks>
/// 不拦的后果和状态栏指标同源(#305):进程探测命令读的是 /proc 与 ps,cmd.exe 跑不动它
/// 却照样有输出(整行被 echo 原样打回),而 <see cref="RemoteProcessProbe.Parse" /> 只在输出为空时
/// 返回 null —— 面板于是显示一张 "CPU 0.0% / 0 个进程" 的空表,而不是那句现成的
/// "该视图读取远端的 /proc 与 ps,需要一个已连接的 Linux 会话"。
/// </remarks>
[TestClass]
[TestCategory("Processes")]
public class RemoteProcessServiceTests
{
    [TestMethod]
    public async Task NonPosixRemote_SendsNoProbeCommand_AndReportsUnavailable()
    {
        var sessionId = Guid.NewGuid();
        ISshClientWrapper client = Substitute.For<ISshClientWrapper>();
        client.IsConnected.Returns(true);
        client.RunCommandDetailedAsync(RemoteShellProbe.ProbeCommand, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RemoteCommandResult("", "'printf' 不是内部或外部命令", 1)));
        client.RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("__N__; nproc ; echo __P__; cat /proc/stat\n");
        ISshConnectionService connections = Substitute.For<ISshConnectionService>();
        connections.GetClient(sessionId).Returns(client);
        var service = new RemoteProcessService(connections);

        Assert.IsNull(await service.GetSnapshotAsync(sessionId, TestContext.CancellationToken));

        await client.DidNotReceive().RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>反向守卫:Linux 远端不能被这道闸误伤,采集命令照发。</summary>
    [TestMethod]
    public async Task PosixRemote_StillRunsTheProbeCommand()
    {
        var sessionId = Guid.NewGuid();
        ISshClientWrapper client = Substitute.For<ISshClientWrapper>();
        client.IsConnected.Returns(true);
        client.RunCommandDetailedAsync(RemoteShellProbe.ProbeCommand, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RemoteCommandResult(RemoteShellProbe.PosixMarker + "\n", "", 0)));
        client.RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("");
        ISshConnectionService connections = Substitute.For<ISshConnectionService>();
        connections.GetClient(sessionId).Returns(client);
        var service = new RemoteProcessService(connections);

        await service.GetSnapshotAsync(sessionId, TestContext.CancellationToken);

        await client.Received(1).RunCommandAsync(
            RemoteProcessProbe.ProbeCommand, Arg.Any<CancellationToken>());
    }

    /// <summary>MSTest 注入的测试上下文(取消令牌)。</summary>
    public TestContext TestContext { get; set; } = null!;
}

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using NSubstitute;
using Tmds.Ssh;
using VelaShell.Core.Ssh;
using VelaShell.Core.FileTransfer.Abstractions;
using VelaShell.Core.ZModem.Model;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Terminal;
using VelaShell.Terminal.FileTransfer;
using VelaShell.Core.FileTransfer.Model;

namespace VelaShell.Tests.Integration;

/// <summary>
/// ZMODEM 在<b>真实 SSH 通道</b>上的端到端覆盖:容器里的 lrzsz(sz/rz)对我们的
/// 检测器/路由器/引擎。协议引擎的单测再全,也测不到真实链路特有的东西——
/// 网络分块把引导序列切碎、shell 回显混在协议字节前、真实 PTY 的时序。
/// 走的就是生产管线:TmdsSshClientWrapper → ShellStreamWrapper → SshTerminalBridge
/// → TerminalTransferRouter → 引擎,只有 UI 被替身化。
/// </summary>
// MSTEST0045(建议给 [Timeout] 加 CooperativeCancellation)在这里不适用:本组用例卡住的地方
// 是 docker CLI、SSH 握手与 lrzsz 传输,它们都不观察 TestContext 的取消令牌;改成协作取消后
// 超时形同虚设,挂死的用例会一直占着 CI。保留强制超时。
[SuppressMessage("Usage", "MSTEST0045:Use cooperative cancellation with [Timeout]",
    Justification = "被等待的 docker/SSH 操作不接受测试取消令牌,协作取消无法中断它们。")]
[TestClass]
public class TransferRealChannelIntegrationTests
{
    private const string TestHost = "localhost";
    private const int TestPort = 2222;
    private const string TestUser = "testuser";
    private const string TestPassword = "testpass";
    private const string ContainerName = "velashell-test-ssh";

    private static readonly Lazy<bool> DockerAvailable = new(() => RunDocker("version", out _));
    private static readonly Lazy<bool> LrzszAvailable = new(EnsureLrzszInstalled);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("DockerIntegration")]
    [Timeout(120_000)]
    public async Task RemoteSz_OverRealSshChannel_IsDetectedAndReceivedIntact()
    {
        if (SkipIfPrerequisitesMissing())
        {
            return;
        }

        // 已知随机负载放进容器(二进制内容顺带覆盖转义路径:含 CAN/XON/XOFF 等须转义字节)。
        byte[] payload = RandomNumberGenerator.GetBytes(64 * 1024);
        string localSeed = Path.Combine(Path.GetTempPath(), $"zm-dl-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(localSeed, payload);
        try
        {
            Assert.IsTrue(RunDocker($"cp \"{localSeed}\" {ContainerName}:/tmp/zm-download.bin", out string cpError), $"docker cp 失败:{cpError}");

            TmdsSshClientWrapper client = await ConnectAsync();
            try
            {
                IShellStreamWrapper shell = await client.CreateShellStreamAsync("xterm-256color", 120, 32, 0, 0, 16384);
                var sink = new MemorySink();
                var router = new TerminalTransferRouter(shell, () => sink);
                var ended = new TaskCompletionSource<FileTransferSession>(TaskCreationOptions.RunContinuationsAsynchronously);
                router.SessionEnded += s => ended.TrySetResult(s);

                ITerminalEmulator terminal = Substitute.For<ITerminalEmulator>();
                using var bridge = new SshTerminalBridge(terminal, shell) { TransferRouter = router };
                bridge.Start();

                bridge.SendRaw(Encoding.ASCII.GetBytes("sz /tmp/zm-download.bin\r"));

                FileTransferSession session = await ended.Task.WaitAsync(TimeSpan.FromSeconds(60));
                Assert.AreEqual(FileTransferState.Completed, session.Status);
                Assert.IsTrue(sink.Completed.TryGetValue("zm-download.bin", out byte[]? received), "sz 提供的文件没有完成接收。");
                Assert.AreSequenceEqual(payload, received);
            }
            finally
            {
                client.Dispose();
            }
        }
        finally
        {
            File.Delete(localSeed);
        }
    }

    [TestMethod]
    [TestCategory("DockerIntegration")]
    [Timeout(120_000)]
    public async Task RemoteRz_OverRealSshChannel_ReceivesOurUploadIntact()
    {
        if (SkipIfPrerequisitesMissing())
        {
            return;
        }

        byte[] payload = RandomNumberGenerator.GetBytes(64 * 1024);
        string localFile = Path.Combine(Path.GetTempPath(), $"zm-ul-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(localFile, payload);
        string remoteName = Path.GetFileName(localFile);
        try
        {
            TmdsSshClientWrapper client = await ConnectAsync();
            try
            {
                IShellStreamWrapper shell = await client.CreateShellStreamAsync("xterm-256color", 120, 32, 0, 0, 16384);
                var source = new SingleFileSource(localFile, remoteName, payload.Length);
                var router = new TerminalTransferRouter(
                    shell,
                    () => Substitute.For<IFileTransferSink>(),
                    () => source);
                var ended = new TaskCompletionSource<FileTransferSession>(TaskCreationOptions.RunContinuationsAsynchronously);
                router.SessionEnded += s => ended.TrySetResult(s);

                ITerminalEmulator terminal = Substitute.For<ITerminalEmulator>();
                using var bridge = new SshTerminalBridge(terminal, shell) { TransferRouter = router };
                bridge.Start();

                bridge.SendRaw(Encoding.ASCII.GetBytes("cd /tmp && rz\r"));

                FileTransferSession session = await ended.Task.WaitAsync(TimeSpan.FromSeconds(60));
                Assert.AreEqual(FileTransferState.Completed, session.Status);

                // 用远端自己的校验和验证落盘完整性(busybox md5sum)。
                string expected = Convert.ToHexStringLower(MD5.HashData(payload));
                string output = await client.RunCommandAsync($"md5sum /tmp/{remoteName}");
                Assert.StartsWith(expected, output.Trim());
            }
            finally
            {
                client.Dispose();
            }
        }
        finally
        {
            File.Delete(localFile);
        }
    }

    /// <summary>
    /// 连接被拆掉之后往 shell 流里写,必须是<b>静默空操作</b>并把 <c>CanWrite</c> 翻假。
    /// <para>
    /// 回归:<c>ShellStreamWrapper.WriteAsync</c> 曾把库里的 <c>SshChannelClosedException</c>
    /// 转成新建的 <c>ObjectDisposedException</c> 再抛出,而唯一的消费者(桥的写循环)只是
    /// catch 掉丢弃 —— 纯粹拿异常做控制流;更糟的是它顺手把 <c>_disposed</c> 置了位,
    /// 让随后真正的 <c>Dispose()</c> 直接短路返回,<c>RemoteProcess</c> 再也不会被确定性释放。
    /// 断开的通道只能用真实链路造出来,所以这条用例落在这套 Docker 夹具里。
    /// </para>
    /// </summary>
    [TestMethod]
    [TestCategory("DockerIntegration")]
    [Timeout(60_000)]
    public async Task WriteAfterConnectionTornDown_IsSilentNoOp_AndFlipsCanWrite()
    {
        if (SkipIfDockerOrSshMissing())
        {
            return;
        }

        TmdsSshClientWrapper client = await ConnectAsync();
        IShellStreamWrapper shell = await client.CreateShellStreamAsync("xterm-256color", 120, 32, 0, 0, 16384);
        Assert.IsTrue(shell.CanWrite, "刚开出来的 shell 流应可写。");

        // 整条连接拆掉:此后任何写入在库内必然失败。
        client.Dispose();

        byte[] payload = "echo still-here\r"u8.ToArray();

        // 关键断言:不抛。抛了就说明又退回了「拿异常做控制流」。
        await shell.WriteAsync(payload, 0, payload.Length, CancellationToken.None);
        Assert.IsFalse(shell.CanWrite, "通道已断后 CanWrite 必须翻假,上层才会短路后续写入。");

        // 短路之后再写也不该抛,更不该再去撞一次库内异常。
        await shell.WriteAsync(payload, 0, payload.Length, CancellationToken.None);

        // 写失败过之后,真正的 Dispose 仍须走到底(旧实现会因 _disposed 已置位而直接返回)。
        shell.Dispose();
        Assert.IsFalse(shell.CanRead, "Dispose 后不应再声称可读。");
    }

    /// <summary>只需要 Docker + SSH 服务器的用例用这个门(不涉及 lrzsz)。</summary>
    private bool SkipIfDockerOrSshMissing()
    {
        if (!DockerAvailable.Value)
        {
            TestContext.WriteLine("[SKIP] Docker 不可用。运行 'docker compose -f docker-compose.test.yml up -d' 以启用。");
            return true;
        }
        if (!IsSshServerReachable())
        {
            TestContext.WriteLine($"[SKIP] SSH 测试服务器 {TestHost}:{TestPort} 不可达。");
            return true;
        }
        return false;
    }

    private static async Task<TmdsSshClientWrapper> ConnectAsync()
    {
        var settings = new SshClientSettings($"{TestUser}@{TestHost}")
        {
            Port = TestPort,
            AutoConnect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            // 测试容器的主机键每次重建都变:无条件信任,不写 known_hosts。
            HostAuthentication = (_, _) => ValueTask.FromResult(true),
            UpdateKnownHostsFileAfterAuthentication = false
        };
        settings.Credentials.Add(new PasswordCredential(TestPassword));
        var client = new TmdsSshClientWrapper(settings);
        await client.ConnectAsync(CancellationToken.None);
        return client;
    }

    private bool SkipIfPrerequisitesMissing()
    {
        if (!DockerAvailable.Value)
        {
            TestContext.WriteLine("[SKIP] Docker 不可用。运行 'docker compose -f docker-compose.test.yml up -d' 以启用。");
            return true;
        }
        if (!IsSshServerReachable())
        {
            TestContext.WriteLine($"[SKIP] SSH 测试服务器 {TestHost}:{TestPort} 不可达。");
            return true;
        }
        if (!LrzszAvailable.Value)
        {
            TestContext.WriteLine("[SKIP] 容器内 lrzsz 不可用且无法安装(可能无外网)。");
            return true;
        }
        return false;
    }

    /// <summary>容器里确保 sz/rz 可用:已装直接过,否则 apk 装一次(容器无外网时优雅跳过)。</summary>
    private static bool EnsureLrzszInstalled() =>
        RunDocker($"exec {ContainerName} sh -c \"command -v sz >/dev/null 2>&1 || apk add --no-cache lrzsz\"", out _, timeoutMs: 60_000);

    private static bool RunDocker(string arguments, out string stderr, int timeoutMs = 10_000)
    {
        stderr = "";
        try
        {
            using var process = new Process
            {
                StartInfo = new()
                {
                    FileName = "docker",
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            if (!process.Start())
            {
                return false;
            }
            if (!process.WaitForExit(timeoutMs))
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                    // 已退出。
                }
                return false;
            }
            stderr = process.StandardError.ReadToEnd();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 判据是「能不能真的建起一个 SSH 会话」,而不是「TCP 端口通不通」。
    /// Docker 的端口代理<b>永远</b>接受 TCP 连接,哪怕后端 sshd 根本握不上手 ——
    /// 只探端口会让本该跳过的用例带着 10 秒连接超时红掉,把环境问题伪装成代码问题。
    /// 探测本身要花一次真实握手,所以缓存起来整轮只做一次。
    /// </summary>
    private static bool IsSshServerReachable() => SshLoginWorks.Value;

    private static readonly Lazy<bool> SshLoginWorks = new(() =>
    {
        if (!IsPortOpen())
        {
            return false;
        }
        try
        {
            using var probe = ConnectAsync().GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    });

    private static bool IsPortOpen()
    {
        try
        {
            using var client = new TcpClient();
            IAsyncResult result = client.BeginConnect(TestHost, TestPort, null, null);
            if (!result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(2)))
            {
                return false;
            }
            client.EndConnect(result);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>按文件名收集完成文件的内存 sink(测试专用,文件很小)。</summary>
    private sealed class MemorySink : IFileTransferSink
    {
        private readonly Dictionary<Guid, (string Name, MemoryStream Data)> _open = [];

        public Dictionary<string, byte[]> Completed { get; } = [];

        public ValueTask<(TransferFileDisposition Disposition, long ResumeOffset)> OnFileOfferedAsync(
            TransferFileMetadata metadata, FileTransferItem item, CancellationToken cancellationToken)
        {
            _open[item.Id] = (Path.GetFileName(metadata.FileName), new MemoryStream());
            return ValueTask.FromResult((TransferFileDisposition.Accept, 0L));
        }

        public ValueTask WriteAsync(FileTransferItem item, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            _open[item.Id].Data.Write(data.Span);
            return ValueTask.CompletedTask;
        }

        public ValueTask CompleteAsync(FileTransferItem item, CancellationToken cancellationToken)
        {
            (string name, MemoryStream stream) = _open[item.Id];
            Completed[name] = stream.ToArray();
            _open.Remove(item.Id);
            return ValueTask.CompletedTask;
        }

        public ValueTask FailAsync(FileTransferItem item, Exception? error, CancellationToken cancellationToken)
        {
            _open.Remove(item.Id);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>单文件上传源。</summary>
    private sealed class SingleFileSource(string localPath, string remoteName, long size) : IFileTransferSource
    {
        public ValueTask<IReadOnlyList<OutgoingTransferFile>> GetFilesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<OutgoingTransferFile>>(
                [new(localPath, remoteName, size, File.GetLastWriteTimeUtc(localPath))]);

        public ValueTask<Stream> OpenReadAsync(OutgoingTransferFile file, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Stream>(File.OpenRead(file.LocalPath));
    }
}

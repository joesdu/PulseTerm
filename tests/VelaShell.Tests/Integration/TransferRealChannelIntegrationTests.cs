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

    private static bool IsSshServerReachable()
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

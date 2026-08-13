using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using VelaShell.Infrastructure.Pty;

namespace VelaShell.Infrastructure.Tests;

/// <summary>
/// ConPTY 传输层的端到端冒烟。
/// </summary>
/// <remarks>
/// 无头测试宿主下 conhost 不驱动客户端 —— 本机(Windows 预览版 conhost)实测:交互式 cmd
/// 除 conhost 自己的握手外不产生任何输出、不响应输入、也不退出;连 `cmd /c echo x` 的 x
/// 都不出屏幕帧(但进程照常退出)。屏幕帧与输入通路依赖真实终端侧的完整 VT 协商,
/// 由 GUI 实测覆盖,这里断言不了,也就不假装断言。
/// 本类只测两条与上述无关、可确定成立的契约:伪控制台/管道挂载、以及子进程退出后的 EOF。
/// </remarks>
[TestClass]
[TestCategory("ConPty")]
[SupportedOSPlatform(nameof(OSPlatform.Windows))]
public class ConPtyShellStreamTests
{
    /// <summary>挂载正常:conhost 的 VT 握手序列经输出管道到达我们这侧。</summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    [OSCondition(OperatingSystems.Windows)]
    public async Task ConPty_SpawnsShell_DeliversConhostHandshake()
    {
        using var stream = ConPtyShellStream.Start("cmd.exe", workingDirectory: null, columns: 80, rows: 25);

        string output = await ReadUntilAsync(stream, s => s.Contains('\x1b'), TimeSpan.FromSeconds(15));

        Assert.Contains("\x1b[", output, "应收到 conhost 的 VT 握手序列(伪控制台与管道已挂上)。");
    }

    /// <summary>
    /// 生命周期契约:子进程退出后读端归一化为 EOF(返回 0),桥的读循环据此走远端关闭路径
    /// (标签变为已断开,可重开)。
    /// </summary>
    /// <remarks>
    /// 用自行退出的 `cmd /c exit`,而不是「向交互式 cmd 写 exit 让它退出」—— 后者要 conhost
    /// 真正驱动交互式客户端,而无头宿主下它不驱动(见类注释),那样测的其实是环境而不是本类。
    /// EOF 这条链(进程退出 → 关伪控制台 → conhost 那侧写端关闭 → 读端断管)与输入通路无关,
    /// 自退出足以完整覆盖。
    /// </remarks>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    [OSCondition(OperatingSystems.Windows)]
    public async Task ConPty_WhenChildExitsOnItsOwn_ReadSideSignalsEof()
    {
        using var stream = ConPtyShellStream.Start("cmd.exe /c exit", workingDirectory: null, columns: 80, rows: 25);

        bool sawEof = await ReadToEofAsync(stream, TimeSpan.FromSeconds(20));

        Assert.IsTrue(sawEof, "子进程退出后,读端应给出 EOF。");
        Assert.IsFalse(stream.CanRead, "子进程退出应关闭伪控制台,流不再可读。");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void ConPty_Dispose_KillsProcess_AndIsIdempotent()
    {
        var stream = ConPtyShellStream.Start("cmd.exe", workingDirectory: null, columns: 80, rows: 25);
        Assert.IsTrue(stream.CanRead);

        stream.Dispose();
        stream.Dispose(); // 幂等

        Assert.IsFalse(stream.CanRead);
        Assert.IsFalse(stream.CanWrite);
    }

    /// <summary>读到满足条件或超时为止;超时即让调用方的断言给出有意义的失败信息。</summary>
    private static async Task<string> ReadUntilAsync(ConPtyShellStream stream, Func<string, bool> satisfied, TimeSpan timeout)
    {
        var collected = new StringBuilder();
        byte[] buffer = new byte[4096];
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < timeout)
        {
            Task<int> read = stream.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None);
            // 超时定时器随本轮结束一起取消,否则每读一次都留下一个跑满 timeout 的计时器。
            using var timeoutCts = new CancellationTokenSource();
            var delay = Task.Delay(timeout - sw.Elapsed, timeoutCts.Token);
            Task winner = await Task.WhenAny(read, delay);
            await timeoutCts.CancelAsync();
            if (winner != read)
            {
                break;
            }
            int n = await read;
            if (n == 0)
            {
                break; // EOF
            }
            collected.Append(Encoding.UTF8.GetString(buffer, 0, n));
            if (satisfied(collected.ToString()))
            {
                break;
            }
        }
        return collected.ToString();
    }

    /// <summary>一路读到 EOF;超时返回 false。</summary>
    private static async Task<bool> ReadToEofAsync(ConPtyShellStream stream, TimeSpan timeout)
    {
        byte[] buffer = new byte[4096];
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < timeout)
        {
            Task<int> read = stream.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None);
            using var timeoutCts = new CancellationTokenSource();
            var delay = Task.Delay(timeout - sw.Elapsed, timeoutCts.Token);
            Task winner = await Task.WhenAny(read, delay);
            await timeoutCts.CancelAsync();
            if (winner != read)
            {
                return false;
            }
            if (await read == 0)
            {
                return true;
            }
        }
        return false;
    }
}

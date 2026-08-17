using System.Diagnostics;
using VelaShell.Core.Models;

namespace VelaShell.Core.Sftp;

/// <summary>
/// 传输进度节流器。
/// <para>
/// 底层传输库按分块(SFTP 约 32KB、S3 按分片内的读缓冲)触发进度回调,一个 7.7GB 的文件
/// 会产生二十多万次回调。上层的 <see cref="Progress{T}" /> 是在 UI 线程上构造的,每次 Report
/// 都会 Post 一个工作项到 Avalonia 调度器,并在其中触发多个 PropertyChanged + 字符串格式化。
/// 网络产出速度远高于 UI 线程的消费速度,队列只增不减 —— 表现就是传到 1GB 左右界面
/// 长时间卡死、随后又"追上"继续。这里在源头按时间片收敛上报频率。
/// </para>
/// <para>
/// 分块回调可能并发到达且乱序(S3 的并发分片上传更是必然乱序),因此已传字节数取单调最大值,
/// 避免进度条回退。
/// </para>
/// <para>
/// 原为 <c>SftpService</c> 的私有嵌套类;S3 后端要同一套节流与单调语义,
/// 提升为 Core 的公共类型而不是抄一份 —— 进度回退与 UI 卡死这两个坑不该再踩第二遍。
/// </para>
/// </summary>
/// <param name="sink">进度接收方;为 null 时整条链路短路。</param>
/// <param name="fileName">展示用的文件名。</param>
/// <param name="totalBytes">文件总字节数;为 0 时百分比恒为 0。</param>
public sealed class TransferProgressThrottle(IProgress<TransferProgress>? sink, string fileName, long totalBytes)
{
    /// <summary>两次上报之间的最小间隔:每秒最多刷新 10 次界面,足够顺滑且成本可忽略。</summary>
    private const long MinIntervalMs = 100;

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private long _lastReportMs = -MinIntervalMs;
    private long _maxBytes;

    /// <summary>是否需要上报(sink 为空时全链路短路,连对象分配都省掉)。</summary>
    public bool IsEnabled => sink is not null;

    /// <summary>按节流策略上报一次进度;间隔不足则丢弃(下一次仍会带上累计值)。</summary>
    /// <param name="bytesTransferred">累计已传字节数。</param>
    public void Report(long bytesTransferred)
    {
        if (sink is null)
        {
            return;
        }
        long observed = Monotonic(bytesTransferred);
        long nowMs = _stopwatch.ElapsedMilliseconds;
        long last = Volatile.Read(ref _lastReportMs);
        if (nowMs - last < MinIntervalMs)
        {
            return;
        }

        // CAS 抢占本时间片的上报权:并发回调下只有一个线程真正 Report,其余直接返回。
        if (Interlocked.CompareExchange(ref _lastReportMs, nowMs, last) != last)
        {
            return;
        }
        Emit(observed, nowMs);
    }

    /// <summary>
    /// 在已传字节数上累加一个增量后上报。并发分片上传时各分片只知道自己传了多少,
    /// 这个重载让它们无需自行维护全局计数。
    /// </summary>
    /// <param name="delta">本次新增的字节数。</param>
    public void Advance(long delta)
    {
        if (sink is null || delta <= 0)
        {
            return;
        }
        Report(Interlocked.Add(ref _maxBytes, delta));
    }

    /// <summary>无视节流强制上报一次,用于收尾 —— 否则进度会永远停在最后一个时间片的值上。</summary>
    /// <param name="bytesTransferred">最终已传字节数。</param>
    public void ReportFinal(long bytesTransferred)
    {
        if (sink is null)
        {
            return;
        }
        Emit(Monotonic(bytesTransferred), _stopwatch.ElapsedMilliseconds);
    }

    private long Monotonic(long bytesTransferred)
    {
        long current = Volatile.Read(ref _maxBytes);
        while (bytesTransferred > current)
        {
            long previous = Interlocked.CompareExchange(ref _maxBytes, bytesTransferred, current);
            if (previous == current)
            {
                return bytesTransferred;
            }
            current = previous;
        }
        return current;
    }

    private void Emit(long bytesTransferred, long elapsedMs)
    {
        double elapsedSeconds = elapsedMs / 1000d;
        double speed = elapsedSeconds > 0 ? bytesTransferred / elapsedSeconds : 0;
        long remainingBytes = totalBytes - bytesTransferred;
        TimeSpan estimatedTimeRemaining = speed > 0 && remainingBytes > 0
                                              ? TimeSpan.FromSeconds(remainingBytes / speed)
                                              : TimeSpan.Zero;
        sink!.Report(new()
        {
            FileName = fileName,
            BytesTransferred = bytesTransferred,
            TotalBytes = totalBytes,
            Percentage = totalBytes > 0 ? (int)((bytesTransferred * 100) / totalBytes) : 0,
            SpeedBytesPerSecond = speed,
            EstimatedTimeRemaining = estimatedTimeRemaining
        });
    }
}
